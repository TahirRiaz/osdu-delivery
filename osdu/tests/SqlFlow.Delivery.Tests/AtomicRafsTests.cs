using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Protocols.Ddms;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The RAFS shape of the ddms route as a unit of work (docs/atomic-delivery-plan.md, osdu/specs/rafs-ddms/INTEGRATION.md
/// section 2.4), against the fake platform with scripted failures in front of it: the record reported once it is written
/// when content follows (the record a unit created, or a version naming the one it replaced), each table reported as an
/// intent before its write and by the dataset RAFS registered once answered (a new dataset as content, an earlier delivery's
/// dataset as a version naming the one the record named, blob content as objects), and a resumed try that sends again what
/// never answered and only that. Each failure point is followed by the unit's undo: content datasets removed through storage
/// (a write whose answer was lost found by the URN RAFS wrote into the record), blob content kept, a dataset version given back
/// the version the record named (a lost write to an earlier delivery's dataset included), the record waiting while anything
/// beside it could not be undone, then removed through RAFS or given back the version it replaced, and nothing written back
/// or removed through storage where the flow's endpoint is RAFS itself.
/// </summary>
public sealed class AtomicRafsTests
{
    private const string AnalysisId = "dev:work-product-component--SamplesAnalysis:sa-1";
    private const string AnalysisKind = "osdu:wks:work-product-component--SamplesAnalysis:1.0.0";
    private const string V2 = FakeOsduPlatform.RafsRoot + "/v2/";
    private const string AnalysisPath = V2 + "samplesanalysis/" + AnalysisId;
    private const string Storage = "/api/storage/v2/records/";
    private const string DatasetPrefix = "dev:dataset--File.Generic:";

    private static readonly DateTime Began = new(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);

    private static DeliveryUnit Unit() => new(Guid.NewGuid(), Began);

    private static DeliveryKey Key(string id) => DeliveryKey.Derive("rafs", [id]);

    private static JsonObject Analysis(string name = "NMR run") => FakeOsduPlatform.Record(AnalysisId, AnalysisKind, new JsonObject
    {
        ["Name"] = name,
        ["SampleAnalysisTypeIDs"] = new JsonArray("dev:reference-data--SampleAnalysisType:NMR:"),
    });

    /// <summary>A record's content tables, each a parquet table of the given rows named after its content type.</summary>
    private static RafsRouteTests.NamedFiles Tables(params (string ContentType, int Rows)[] tables)
        => new(tables.Select(t => (t.ContentType + ".parquet", RafsRouteTests.Table("SamplesAnalysisID", AnalysisId, t.Rows))).ToArray());

    private static string Slot(string contentType) => "content:" + contentType;

    /// <summary>The dataset RAFS registered for a content type of the record.</summary>
    private static string Dataset(FakeOsduPlatform platform, string contentType)
        => Assert.Single(platform.Records.Keys, k => k.StartsWith(DatasetPrefix + contentType + "-", StringComparison.Ordinal));

    private static string FileSize(FakeOsduPlatform platform, string dataset)
        => platform.Records[dataset]["data"]!["DatasetProperties"]!["FileSourceInfo"]!["FileSize"]!.GetValue<string>();

    private static string[] Urns(JsonObject record) => record["data"]?["DDMSDatasets"] is JsonArray list ? list.Select(n => n!.GetValue<string>()).ToArray() : [];

    private static UndoResult Answer(IReadOnlyList<UndoResult> results, string slot) => Assert.Single(results, r => r.Item.Artifact.Slot == slot);

    /// <summary>Every item of <paramref name="works"/> answered exactly once, each with a state an undo settles in.</summary>
    private static void AnsweredOnce(IReadOnlyList<UndoResult> results, params UndoWork[] works)
    {
        var items = works.SelectMany(w => w.Items).ToList();
        Assert.Equal(items.Count, results.Count);
        Assert.All(items, item => Assert.Single(results, r => ReferenceEquals(r.Item, item)));
        Assert.All(results, r => Assert.True(ArtifactStatuses.IsUndoOutcome(r.Outcome), $"{r.Item.Artifact.Slot} was answered {r.Outcome}"));
    }

    [Fact]
    public async Task A_created_record_is_reported_after_its_write_and_each_table_as_an_intent_before_its_post_and_by_its_dataset_after()
    {
        using var rig = new Rig();
        var ledger = new UnitLedger(Unit());
        var outcome = await rig.Protocol.DeliverAsync(rig.Work(ledger, Analysis(), Tables(("nmr", 3), ("routinecoreanalysis", 1))));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal(
            [
                "GET " + V2 + "samplesanalysis/analysistypes",
                "POST " + V2 + "samplesanalysis",
                "POST " + AnalysisPath + "/data/nmr",
                "POST " + AnalysisPath + "/data/routinecoreanalysis",
                "GET " + AnalysisPath,
            ],
            rig.Sent());
        Assert.Equal(
            ["metadata", "content-nmr-intent", "content-nmr", "content-routinecoreanalysis-intent", "content-routinecoreanalysis"],
            ledger.Reported.Select(r => r.Report.Step));

        // The record is the unit's once it is written, as the record it created, under the version the write gave it.
        var (metadata, afterWrite) = ledger.Reported[0];
        Assert.Equal(2, afterWrite);
        var record = Assert.Single(metadata.Artifacts);
        Assert.Equal((TargetArtifact.RecordSlot, ArtifactRoles.Record, (string?)AnalysisId, ArtifactStatus.Pending), (record.Slot, record.Role, record.TargetId, record.Status));
        Assert.Equal(rig.Platform.History[AnalysisId][0]["version"]!.GetValue<long>(), record.Version);

        // RAFS mints each table's dataset: the intent goes before the write, found by the record and the content type.
        foreach (var (type, at) in new[] { ("nmr", 1), ("routinecoreanalysis", 3) })
        {
            var (writing, beforePost) = ledger.Reported[at];
            Assert.Equal("writing", writing.Returned["state"]);
            var intent = Assert.Single(writing.Artifacts);
            Assert.Equal(
                (Slot(type), ArtifactRoles.Content, (string?)null, (string?)(AnalysisId + "#" + type), ArtifactStatus.Intent),
                (intent.Slot, intent.Role, intent.TargetId, intent.Locator, intent.Status));
            Assert.Equal("POST " + AnalysisPath + "/data/" + type, rig.Sent()[beforePost]);

            var (written, afterPost) = ledger.Reported[at + 1];
            Assert.Equal(beforePost + 1, afterPost);
            var dataset = Dataset(rig.Platform, type);
            var created = Assert.Single(written.Artifacts);
            Assert.Equal(
                (Slot(type), ArtifactRoles.Content, (string?)dataset, (long?)rig.Platform.Records[dataset]["version"]!.GetValue<long>(), (string?)(AnalysisId + "#" + type), ArtifactStatus.Pending),
                (created.Slot, created.Role, created.TargetId, created.Version, created.Locator, created.Status));
            Assert.Equal(dataset, outcome.Returned[$"content.{type}.dataset"]);
        }

        Assert.Equal(
            [(TargetArtifact.RecordSlot, ArtifactRoles.Record), (Slot("nmr"), ArtifactRoles.Content), (Slot("routinecoreanalysis"), ArtifactRoles.Content)],
            ledger.Rows.Select(r => (r.Slot, r.Role)));
        Assert.All(ledger.Rows, r => Assert.Equal(ArtifactStatus.Pending, r.Status));
    }

    [Fact]
    public async Task A_record_written_without_content_reports_nothing_to_undo()
    {
        using var rig = new Rig();
        var ledger = new UnitLedger(Unit());
        Assert.True((await rig.Protocol.DeliverAsync(rig.Work(ledger, Analysis(), null))).Succeeded);
        Assert.Equal(["metadata"], ledger.Reported.Select(r => r.Report.Step));
        Assert.Empty(ledger.Rows);
    }

    [Fact]
    public async Task An_update_gives_an_earlier_dataset_a_new_version_naming_the_one_the_record_named_and_a_new_table_a_dataset_of_its_own()
    {
        using var rig = new Rig();
        var first = await rig.DeliveredAsync(Tables(("nmr", 3)));
        var dataset = first.Returned[RafsShape.DatasetsKey];
        var named = rig.Platform.Records[dataset]["version"]!.GetValue<long>();
        var ledger = new UnitLedger(Unit());
        var outcome = await rig.Protocol.DeliverAsync(rig.Work(ledger, Analysis("NMR run, corrected"), Tables(("nmr", 2), ("routinecoreanalysis", 1)), first.TargetVersion, first.Returned));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);

        var record = ledger.Row(TargetArtifact.RecordSlot);
        Assert.Equal((ArtifactRoles.Version, first.TargetVersion), (record.Role, record.PriorVersion));

        // The intent went as content; answered with the dataset an earlier delivery made, the slot is a version of it.
        Assert.Equal(ArtifactRoles.Content, Assert.Single(ledger.Reported[1].Report.Artifacts).Role);
        var nmr = ledger.Row(Slot("nmr"));
        Assert.Equal(
            (ArtifactRoles.Version, (string?)dataset, (long?)rig.Platform.Records[dataset]["version"]!.GetValue<long>(), (long?)named, ArtifactStatus.Pending),
            (nmr.Role, nmr.TargetId, nmr.Version, nmr.PriorVersion, nmr.Status));
        Assert.True(nmr.Version > named);

        var routine = ledger.Row(Slot("routinecoreanalysis"));
        Assert.Equal((ArtifactRoles.Content, (string?)Dataset(rig.Platform, "routinecoreanalysis")), (routine.Role, routine.TargetId));
    }

    [Fact]
    public async Task Blob_content_is_reported_as_objects_and_kept_by_the_undo_while_the_record_is_removed()
    {
        using var rig = new Rig(blob: true);
        rig.Ddms.Refuse(HttpMethod.Get, "/samplesanalysis/" + AnalysisId, HttpStatusCode.ServiceUnavailable);
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Analysis(), Tables(("nmr", 1)));
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(work));

        var blob = ledger.Row(Slot("nmr"));
        Assert.Equal((ArtifactRoles.Objects, ArtifactStatus.Pending), (blob.Role, blob.Status));
        Assert.Equal(Urns(rig.Platform.Records[AnalysisId]).Single().Split('/')[^1], blob.TargetId);
        Assert.DoesNotContain(rig.Platform.Records.Keys, k => k.StartsWith(DatasetPrefix, StringComparison.Ordinal));

        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        var kept = Answer(results, Slot("nmr"));
        Assert.Equal(
            (ArtifactStatus.Kept, "blob-mode content stays in RAFS's own store, which no call removes; the record given back its earlier version no longer names it"),
            (kept.Outcome, kept.Note));
        var record = Answer(results, TargetArtifact.RecordSlot);
        Assert.Equal((ArtifactStatus.Removed, AnalysisId + ": removed through RAFS (reversible)"), (record.Outcome, record.Note));
        Assert.Contains(AnalysisId, rig.Platform.Removed);
    }

    [Fact]
    public async Task A_refused_table_leaves_its_intent_which_the_undo_finds_gone_and_the_record_is_removed()
    {
        using var rig = new Rig();
        rig.Ddms.Refuse(HttpMethod.Post, "/data/nmr", HttpStatusCode.InternalServerError);
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Analysis(), Tables(("nmr", 1)));
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(work));
        Assert.Equal(["metadata", "content-nmr-intent"], ledger.Reported.Select(r => r.Report.Step));
        Assert.Equal(ArtifactStatus.Intent, ledger.Row(Slot("nmr")).Status);

        var calls = rig.Ddms.Calls.Count;
        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        var nmr = Answer(results, Slot("nmr"));
        Assert.Equal(ArtifactStatus.Gone, nmr.Outcome);
        Assert.StartsWith(AnalysisId + " names no nmr content dataset an earlier delivery did not make", nmr.Note, StringComparison.Ordinal);
        Assert.Equal(ArtifactStatus.Removed, Answer(results, TargetArtifact.RecordSlot).Outcome);
        Assert.Equal(["GET " + AnalysisPath, "GET " + AnalysisPath, "DELETE " + AnalysisPath], rig.Sent(calls));
    }

    [Fact]
    public async Task A_table_whose_answer_was_lost_is_found_by_the_urn_rafs_wrote_and_its_dataset_removed_before_the_record()
    {
        using var rig = new Rig();
        rig.Ddms.Lose(HttpMethod.Post, "/data/nmr");
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Analysis(), Tables(("nmr", 1)));
        var lost = await Assert.ThrowsAsync<DeliveryException>(() => rig.Protocol.DeliverAsync(work));
        Assert.IsType<HttpRequestException>(lost.InnerException);

        // RAFS registered the dataset and named it in the record; the ledger holds only the intent.
        var dataset = Dataset(rig.Platform, "nmr");
        Assert.Contains(Urns(rig.Platform.Records[AnalysisId]), u => u.Contains(dataset, StringComparison.Ordinal));
        Assert.Equal((ArtifactStatus.Intent, (string?)null), (ledger.Row(Slot("nmr")).Status, ledger.Row(Slot("nmr")).TargetId));

        var calls = rig.Ddms.Calls.Count;
        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        var nmr = Answer(results, Slot("nmr"));
        Assert.Equal((ArtifactStatus.Removed, $"{dataset}: removed from OSDU (reversible)"), (nmr.Outcome, nmr.Note));
        Assert.Equal(ArtifactStatus.Removed, Answer(results, TargetArtifact.RecordSlot).Outcome);
        Assert.Equal(["GET " + AnalysisPath, "POST " + Storage + dataset + ":delete", "GET " + AnalysisPath, "DELETE " + AnalysisPath], rig.Sent(calls));
        Assert.Contains(dataset, rig.Platform.Removed);
        Assert.Contains(AnalysisId, rig.Platform.Removed);
    }

    [Fact]
    public async Task A_later_table_refused_takes_back_the_earlier_tables_dataset_and_the_record()
    {
        using var rig = new Rig();
        rig.Ddms.Refuse(HttpMethod.Post, "/data/routinecoreanalysis", HttpStatusCode.UnprocessableEntity);
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Analysis(), Tables(("nmr", 1), ("routinecoreanalysis", 1)));
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(work));
        var dataset = Dataset(rig.Platform, "nmr");

        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        Assert.Equal(ArtifactStatus.Removed, Answer(results, Slot("nmr")).Outcome);
        Assert.Equal(ArtifactStatus.Gone, Answer(results, Slot("routinecoreanalysis")).Outcome);
        Assert.Equal(ArtifactStatus.Removed, Answer(results, TargetArtifact.RecordSlot).Outcome);
        Assert.Contains(dataset, rig.Platform.Removed);
        Assert.Contains(AnalysisId, rig.Platform.Removed);
    }

    [Fact]
    public async Task A_read_back_that_failed_after_every_table_landed_leaves_all_of_it_to_the_undo()
    {
        using var rig = new Rig();
        rig.Ddms.Refuse(HttpMethod.Get, "/samplesanalysis/" + AnalysisId, HttpStatusCode.ServiceUnavailable);
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Analysis(), Tables(("nmr", 1), ("routinecoreanalysis", 1)));
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(work));
        var datasets = new[] { Dataset(rig.Platform, "nmr"), Dataset(rig.Platform, "routinecoreanalysis") };

        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        Assert.All(results, r => Assert.Equal(ArtifactStatus.Removed, r.Outcome));
        Assert.All(datasets, d => Assert.Contains(d, rig.Platform.Removed));
        Assert.Contains(AnalysisId, rig.Platform.Removed);
    }

    [Fact]
    public async Task A_resumed_try_does_not_send_tables_that_landed_again()
    {
        using var rig = new Rig();
        rig.Ddms.Refuse(HttpMethod.Get, "/samplesanalysis/" + AnalysisId, HttpStatusCode.ServiceUnavailable);
        var ledger = new UnitLedger(Unit());
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(rig.Work(ledger, Analysis(), Tables(("nmr", 1), ("routinecoreanalysis", 1)))));
        var reported = ledger.Reported.Count;

        var calls = rig.Ddms.Calls.Count;
        var resumed = await rig.Protocol.DeliverAsync(rig.Work(ledger, Analysis(), Tables(("nmr", 1), ("routinecoreanalysis", 1))));
        Assert.True(resumed.Succeeded, resumed.Failure?.Message);
        Assert.Equal(["GET " + AnalysisPath], rig.Sent(calls));
        Assert.Equal(reported, ledger.Reported.Count);
        Assert.Equal(
            [Dataset(rig.Platform, "nmr"), Dataset(rig.Platform, "routinecoreanalysis")],
            resumed.Returned[RafsShape.DatasetsKey].Split(','));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_resumed_try_writes_a_table_whose_write_was_marked_and_never_answered(bool landed)
    {
        using var rig = new Rig();
        if (landed)
        {
            rig.Ddms.Lose(HttpMethod.Post, "/data/nmr");
        }
        else
        {
            rig.Ddms.Refuse(HttpMethod.Post, "/data/nmr", HttpStatusCode.InternalServerError);
        }

        var ledger = new UnitLedger(Unit());
        await Assert.ThrowsAnyAsync<DeliveryException>(() => rig.Protocol.DeliverAsync(rig.Work(ledger, Analysis(), Tables(("nmr", 1)))));

        // The intent was marked before its write went, and the write never answered: whether it landed is not known, and a
        // second write of the same table re-versions the dataset the record names, so the resumed try sends it.
        Assert.Equal("writing", ledger.Steps["content-nmr-intent"]["state"]);
        Assert.False(ledger.Steps.ContainsKey("content-nmr"));
        var calls = rig.Ddms.Calls.Count;
        var resumed = await rig.Protocol.DeliverAsync(rig.Work(ledger, Analysis(), Tables(("nmr", 1))));
        Assert.True(resumed.Succeeded, resumed.Failure?.Message);
        Assert.Equal(["POST " + AnalysisPath + "/data/nmr", "GET " + AnalysisPath], rig.Sent(calls));
        Assert.True(resumed.Returned.ContainsKey("content.nmr.urn"), "the resumed try reported content it never wrote");

        // One dataset whatever the first write did, named in the record and on the slot's one row, pending.
        var dataset = Dataset(rig.Platform, "nmr");
        Assert.Equal(dataset, resumed.Returned[RafsShape.DatasetsKey]);
        Assert.Single(Urns(rig.Platform.Records[AnalysisId]), u => u.Contains(dataset, StringComparison.Ordinal));
        var row = ledger.Row(Slot("nmr"));
        Assert.Equal((ArtifactRoles.Content, (string?)dataset, ArtifactStatus.Pending), (row.Role, row.TargetId, row.Status));
    }

    [Fact]
    public async Task The_undo_of_an_update_writes_back_the_record_and_the_dataset_version_it_replaced_and_removes_a_new_dataset()
    {
        using var rig = new Rig();
        var first = await rig.DeliveredAsync(Tables(("nmr", 3)));
        var dataset = first.Returned[RafsShape.DatasetsKey];
        var urns = Urns(rig.Platform.Records[AnalysisId]);
        var size = FileSize(rig.Platform, dataset);

        // The record is read before its write; the read back after the tables fails.
        rig.Ddms.Refuse(HttpMethod.Get, "/samplesanalysis/" + AnalysisId, HttpStatusCode.ServiceUnavailable, skip: 1);
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Analysis("NMR run, corrected"), Tables(("nmr", 2), ("routinecoreanalysis", 1)), first.TargetVersion, first.Returned);
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(work));
        var added = Dataset(rig.Platform, "routinecoreanalysis");
        Assert.NotEqual(size, FileSize(rig.Platform, dataset));

        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        Assert.Equal(ArtifactStatus.Removed, Answer(results, Slot("routinecoreanalysis")).Outcome);
        Assert.Contains(added, rig.Platform.Removed);

        var nmr = Answer(results, Slot("nmr"));
        Assert.Equal(ArtifactStatus.Restored, nmr.Outcome);
        Assert.StartsWith($"{dataset}: version {ledger.Row(Slot("nmr")).PriorVersion} written back as version ", nmr.Note, StringComparison.Ordinal);
        Assert.Equal(size, FileSize(rig.Platform, dataset));

        var record = Answer(results, TargetArtifact.RecordSlot);
        Assert.Equal(ArtifactStatus.Restored, record.Outcome);
        Assert.StartsWith($"{AnalysisId}: version {first.TargetVersion} written back as version ", record.Note, StringComparison.Ordinal);
        Assert.Equal("NMR run", rig.Platform.Records[AnalysisId]["data"]!["Name"]!.GetValue<string>());
        Assert.Equal(urns, Urns(rig.Platform.Records[AnalysisId]));
        Assert.DoesNotContain(AnalysisId, rig.Platform.Removed);
        Assert.DoesNotContain(dataset, rig.Platform.Removed);
    }

    [Fact]
    public async Task A_dataset_version_whose_earlier_version_is_not_known_is_kept_and_says_so()
    {
        using var rig = new Rig();
        var first = await rig.DeliveredAsync(Tables(("nmr", 3)));
        var dataset = first.Returned[RafsShape.DatasetsKey];
        var state = first.Returned.Where(kv => kv.Key != "content.nmr.contentId").ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        rig.Ddms.Refuse(HttpMethod.Get, "/samplesanalysis/" + AnalysisId, HttpStatusCode.ServiceUnavailable, skip: 1);
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Analysis(), Tables(("nmr", 2)), first.TargetVersion, state);
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(work));
        Assert.Null(ledger.Row(Slot("nmr")).PriorVersion);

        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        var nmr = Answer(results, Slot("nmr"));
        Assert.Equal((ArtifactStatus.Kept, $"{dataset}: the version the delivery replaced is not known, so nothing was put back"), (nmr.Outcome, nmr.Note));
        Assert.Equal(ArtifactStatus.Restored, Answer(results, TargetArtifact.RecordSlot).Outcome);
    }

    [Fact]
    public async Task An_intent_for_a_table_the_record_already_had_is_given_back_the_dataset_version_the_record_named_when_its_write_landed()
    {
        using var rig = new Rig();
        var first = await rig.DeliveredAsync(Tables(("nmr", 3)));
        var dataset = first.Returned[RafsShape.DatasetsKey];
        var named = rig.Platform.Records[dataset]["version"]!.GetValue<long>();
        var size = FileSize(rig.Platform, dataset);
        rig.Ddms.Lose(HttpMethod.Post, "/data/nmr");
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Analysis(), Tables(("nmr", 2)), first.TargetVersion, first.Returned);
        await Assert.ThrowsAsync<DeliveryException>(() => rig.Protocol.DeliverAsync(work));

        // RAFS gave the earlier delivery's dataset a new version and named it in the record before the answer was lost.
        Assert.True(rig.Platform.Records[dataset]["version"]!.GetValue<long>() > named);
        Assert.DoesNotContain(Urns(rig.Platform.Records[AnalysisId]), u => u.Contains($"{dataset}:{named}/", StringComparison.Ordinal));

        // The intent is undone as the answered write's version would be: the version the record named is written back.
        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        var nmr = Answer(results, Slot("nmr"));
        Assert.Equal(ArtifactStatus.Restored, nmr.Outcome);
        Assert.StartsWith($"{dataset}: version {named} written back as version ", nmr.Note, StringComparison.Ordinal);
        Assert.Equal(size, FileSize(rig.Platform, dataset));
        Assert.Equal(ArtifactStatus.Restored, Answer(results, TargetArtifact.RecordSlot).Outcome);
        Assert.Contains(Urns(rig.Platform.Records[AnalysisId]), u => u.Contains($"{dataset}:{named}/", StringComparison.Ordinal));
        Assert.DoesNotContain(dataset, rig.Platform.Removed);
    }

    [Fact]
    public async Task The_record_waits_while_a_content_dataset_could_not_be_removed_and_goes_with_it_on_the_next_undo()
    {
        using var rig = new Rig();
        rig.Ddms.Refuse(HttpMethod.Get, "/samplesanalysis/" + AnalysisId, HttpStatusCode.ServiceUnavailable);
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Analysis(), Tables(("nmr", 1), ("routinecoreanalysis", 1)));
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(work));
        var nmr = Dataset(rig.Platform, "nmr");

        // A dataset left in OSDU is found through the record, so the record is not removed before it.
        rig.Ddms.Refuse(HttpMethod.Post, Storage + nmr + ":delete", HttpStatusCode.InternalServerError);
        var calls = rig.Ddms.Calls.Count;
        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        Assert.Equal(ArtifactStatus.Failed, Answer(results, Slot("nmr")).Outcome);
        Assert.Equal(ArtifactStatus.Removed, Answer(results, Slot("routinecoreanalysis")).Outcome);
        var record = Answer(results, TargetArtifact.RecordSlot);
        Assert.Equal(ArtifactStatus.Failed, record.Outcome);
        Assert.StartsWith("1 item(s) the delivery made beside the record could not be undone yet (", record.Note, StringComparison.Ordinal);
        Assert.EndsWith("); the record is taken back with them, on the next undo", record.Note, StringComparison.Ordinal);
        Assert.DoesNotContain(rig.Sent(calls), c => c.StartsWith("DELETE ", StringComparison.Ordinal));
        Assert.DoesNotContain(AnalysisId, rig.Platform.Removed);

        // The next undo is given what failed, and takes both.
        ledger.Settle(results);
        var retry = ledger.Undo(work);
        Assert.Equal([TargetArtifact.RecordSlot, Slot("nmr")], retry.Items.Select(i => i.Artifact.Slot));
        var second = await rig.Protocol.UndoAsync([retry]);
        AnsweredOnce(second, retry);
        Assert.All(second, r => Assert.Equal(ArtifactStatus.Removed, r.Outcome));
        Assert.Contains(nmr, rig.Platform.Removed);
        Assert.Contains(AnalysisId, rig.Platform.Removed);
    }

    [Fact]
    public async Task A_record_osdu_held_before_the_unit_began_is_given_back_its_earlier_version_rather_than_removed()
    {
        using var rig = new Rig();
        var before = rig.Platform.Put(Analysis("written by another system"));
        rig.Ddms.Refuse(HttpMethod.Post, "/data/nmr", HttpStatusCode.InternalServerError);
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Analysis(), Tables(("nmr", 1)));
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(work));
        Assert.Equal(ArtifactRoles.Record, ledger.Row(TargetArtifact.RecordSlot).Role);
        rig.Platform.Records[AnalysisId]["createTime"] = Began.AddDays(-2).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        var calls = rig.Ddms.Calls.Count;
        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        var record = Answer(results, TargetArtifact.RecordSlot);
        Assert.Equal(ArtifactStatus.Restored, record.Outcome);
        Assert.StartsWith($"{AnalysisId}: version {before} written back as version ", record.Note, StringComparison.Ordinal);
        Assert.Equal("written by another system", rig.Platform.Records[AnalysisId]["data"]!["Name"]!.GetValue<string>());
        Assert.DoesNotContain(rig.Sent(calls), c => c.StartsWith("DELETE ", StringComparison.Ordinal));
        Assert.DoesNotContain(AnalysisId, rig.Platform.Removed);
    }

    [Fact]
    public async Task Newer_work_keeps_the_record_while_its_content_datasets_are_still_removed()
    {
        using var rig = new Rig();
        rig.Ddms.Refuse(HttpMethod.Get, "/samplesanalysis/" + AnalysisId, HttpStatusCode.ServiceUnavailable);
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Analysis(), Tables(("nmr", 1), ("routinecoreanalysis", 1)));
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(work));

        var undo = ledger.Undo(work, UndoReason.Abandoned, keepRecord: true);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        var record = Answer(results, TargetArtifact.RecordSlot);
        Assert.Equal((ArtifactStatus.Superseded, "the record's newer work writes it again, so it is left as it is"), (record.Outcome, record.Note));
        Assert.Equal(ArtifactStatus.Removed, Answer(results, Slot("nmr")).Outcome);
        Assert.Equal(ArtifactStatus.Removed, Answer(results, Slot("routinecoreanalysis")).Outcome);
        Assert.DoesNotContain(AnalysisId, rig.Platform.Removed);
        Assert.DoesNotContain(rig.Sent(), c => c.StartsWith("DELETE ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_second_undo_finds_the_datasets_and_the_record_gone_and_changes_nothing()
    {
        using var rig = new Rig();
        rig.Ddms.Lose(HttpMethod.Post, "/data/routinecoreanalysis");
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Analysis(), Tables(("nmr", 1), ("routinecoreanalysis", 1)));
        await Assert.ThrowsAsync<DeliveryException>(() => rig.Protocol.DeliverAsync(work));
        var undo = ledger.Undo(work);
        Assert.All(await rig.Protocol.UndoAsync([undo]), r => Assert.Equal(ArtifactStatus.Removed, r.Outcome));
        var removed = rig.Platform.Removed.Order(StringComparer.Ordinal).ToList();

        // The same artifacts again, as when the first undo's answer never reached the ledger.
        var again = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(again, undo);
        Assert.All(again, r => Assert.Equal(ArtifactStatus.Gone, r.Outcome));
        Assert.Equal($"{Dataset(rig.Platform, "nmr")}: record not found in OSDU", Answer(again, Slot("nmr")).Note);
        Assert.Equal(AnalysisId + ": OSDU no longer holds the record", Answer(again, TargetArtifact.RecordSlot).Note);
        Assert.Equal(removed, rig.Platform.Removed.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_failing_call_answers_failed_for_its_own_artifact_alone()
    {
        using var rig = new Rig();
        rig.Ddms.Lose(HttpMethod.Post, "/data/routinecoreanalysis");
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Analysis(), Tables(("capillarypressure", 1), ("nmr", 1), ("routinecoreanalysis", 1)));
        await Assert.ThrowsAsync<DeliveryException>(() => rig.Protocol.DeliverAsync(work));
        var nmr = Dataset(rig.Platform, "nmr");
        var capillary = Dataset(rig.Platform, "capillarypressure");

        // Storage refuses the removal of one dataset, and the read that finds the lost write's dataset fails once.
        rig.Ddms
            .Refuse(HttpMethod.Post, Storage + nmr + ":delete", HttpStatusCode.InternalServerError)
            .Refuse(HttpMethod.Get, "/samplesanalysis/" + AnalysisId, HttpStatusCode.InternalServerError);
        var undo = ledger.Undo(work, UndoReason.Abandoned, keepRecord: true);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        Assert.Equal(ArtifactStatus.Removed, Answer(results, Slot("capillarypressure")).Outcome);
        var refused = Answer(results, Slot("nmr"));
        Assert.Equal(ArtifactStatus.Failed, refused.Outcome);
        Assert.Contains("HTTP 500", refused.Note, StringComparison.Ordinal);
        var unread = Answer(results, Slot("routinecoreanalysis"));
        Assert.Equal(ArtifactStatus.Failed, unread.Outcome);
        Assert.Contains("HTTP 500", unread.Note, StringComparison.Ordinal);
        Assert.Equal(ArtifactStatus.Superseded, Answer(results, TargetArtifact.RecordSlot).Outcome);
        Assert.Contains(capillary, rig.Platform.Removed);
        Assert.DoesNotContain(nmr, rig.Platform.Removed);
        Assert.DoesNotContain(Dataset(rig.Platform, "routinecoreanalysis"), rig.Platform.Removed);
    }

    [Fact]
    public async Task Under_a_rafs_endpoint_content_datasets_are_kept_saying_why_and_a_created_record_is_still_removed()
    {
        using var rig = new Rig(platformEndpoint: false);
        rig.Ddms.Refuse(HttpMethod.Post, "/data/routinecoreanalysis", HttpStatusCode.UnprocessableEntity);
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Analysis(), Tables(("nmr", 1), ("routinecoreanalysis", 1)));
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(work));

        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        const string why = "RAFS never removes a content dataset, and this flow does not say where the storage service is to remove it; give the DDMS its root under target.ddms when the endpoint is the OSDU platform root";
        Assert.Equal((ArtifactStatus.Kept, why), (Answer(results, Slot("nmr")).Outcome, Answer(results, Slot("nmr")).Note));
        Assert.Equal((ArtifactStatus.Kept, why), (Answer(results, Slot("routinecoreanalysis")).Outcome, Answer(results, Slot("routinecoreanalysis")).Note));
        Assert.Equal(ArtifactStatus.Removed, Answer(results, TargetArtifact.RecordSlot).Outcome);
        Assert.Contains(AnalysisId, rig.Platform.Removed);
        Assert.DoesNotContain(rig.Sent(), c => c.Contains("/api/storage/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Under_a_rafs_endpoint_an_update_is_kept_and_says_why()
    {
        using var rig = new Rig(platformEndpoint: false);
        var first = await rig.DeliveredAsync(Tables(("nmr", 3)));
        rig.Ddms.Refuse(HttpMethod.Get, "/samplesanalysis/" + AnalysisId, HttpStatusCode.ServiceUnavailable, skip: 1);
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Analysis("NMR run, corrected"), Tables(("nmr", 2)), first.TargetVersion, first.Returned);
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(work));

        var calls = rig.Ddms.Calls.Count;
        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        Assert.All(results, r =>
        {
            Assert.Equal(ArtifactStatus.Kept, r.Outcome);
            Assert.Contains("the flow's endpoint is the DDMS itself, so the storage service that keeps the record's versions is not under it", r.Note, StringComparison.Ordinal);
            Assert.EndsWith("; OSDU keeps the version the unfinished delivery wrote", r.Note, StringComparison.Ordinal);
        });
        Assert.Empty(rig.Sent(calls));
        Assert.Equal("NMR run, corrected", rig.Platform.Records[AnalysisId]["data"]!["Name"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_artifact_the_shape_never_makes_is_kept()
    {
        using var rig = new Rig();
        var unit = Unit();
        var undo = new UndoWork
        {
            Key = Key(AnalysisId),
            TargetId = AnalysisId,
            Reason = UndoReason.Held,
            Items = [new UndoItem(1, TargetArtifact.Created("session:a", ArtifactRoles.Session, "sess-1", locator: AnalysisId), unit.Id, unit.StartedUtc)],
        };

        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        Assert.Equal((ArtifactStatus.Kept, "the RAFS shape makes nothing of this kind beside a record"), (results[0].Outcome, results[0].Note));
        Assert.Empty(rig.Ddms.Calls);
    }

    /// <summary>
    /// One unit's artifacts as the ledger keeps them (SqlServerLedgerBulk.Artifacts.cs: ArtifactUpsertSql for what a route
    /// reports, the settlement of what an undo answers), and its completed steps as the worker gives them to a later try
    /// (DeliveryWorker.SaveStepAsync, without the unit itself). A slot reported again updates its one row while the row is open
    /// or settled by the route itself (reported removed, kept or gone): the id, locator and version the report gives, else the
    /// earlier ones; the version a write replaced as first reported; a slot first reported as the record the unit created kept
    /// so; the state the report gives. A row an undo settled is never reopened by a report.
    /// </summary>
    private sealed class UnitLedger(DeliveryUnit unit)
    {
        private readonly List<Entry> _rows = [];
        private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _steps = new(StringComparer.Ordinal);

        public DeliveryUnit Unit { get; } = unit;

        /// <summary>Every report, in order, with how many calls had gone when it came.</summary>
        public List<(StepReport Report, int CallsBefore)> Reported { get; } = [];

        public IReadOnlyList<TargetArtifact> Rows => _rows.Select(r => r.Artifact).ToList();

        /// <summary>The steps a later try of the unit is given.</summary>
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Steps => new Dictionary<string, IReadOnlyDictionary<string, string>>(_steps, StringComparer.Ordinal);

        public TargetArtifact Row(string slot) => Assert.Single(_rows, r => r.Artifact.Slot == slot).Artifact;

        public Func<StepReport, CancellationToken, Task> Listen(Func<int> calls) => (report, _) =>
        {
            Reported.Add((report, calls()));
            _steps[report.Step] = new Dictionary<string, string>(report.Returned, StringComparer.Ordinal);
            foreach (var artifact in report.Artifacts)
            {
                Upsert(artifact);
            }

            return Task.CompletedTask;
        };

        /// <summary>What the ledger hands the route when the unit is aborted: its open artifacts, in the order they were first reported.</summary>
        public UndoWork Undo(DeliveryWork delivered, UndoReason reason = UndoReason.Held, bool keepRecord = false) => new()
        {
            Key = delivered.Key,
            TargetId = delivered.TargetId,
            TargetState = delivered.TargetState,
            CommittedVersion = delivered.ExistingVersion,
            Reason = reason,
            KeepRecord = keepRecord,
            Items = _rows.Where(r => ArtifactStatuses.IsOpen(r.Artifact.Status)).Select(r => new UndoItem(r.Id, r.Artifact, Unit.Id, Unit.StartedUtc)).ToList(),
        };

        /// <summary>Writes what an undo answered: each item's row takes its outcome and note, settled by the undo (failed stays open).</summary>
        public void Settle(IEnumerable<UndoResult> results)
        {
            foreach (var result in results)
            {
                var at = _rows.FindIndex(r => r.Id == result.Item.ArtifactId);
                if (at >= 0)
                {
                    var held = _rows[at].Artifact;
                    _rows[at] = new Entry(_rows[at].Id, held with { Status = result.Outcome, Note = result.Note ?? held.Note }, ByRoute: false);
                }
            }
        }

        private void Upsert(TargetArtifact reported)
        {
            var at = _rows.FindIndex(r => r.Artifact.Slot == reported.Slot);
            if (at < 0)
            {
                _rows.Add(new Entry(_rows.Count + 1, reported, SettledByRoute(reported.Status)));
                return;
            }

            var (id, held, byRoute) = _rows[at];
            if (held.Status is not (ArtifactStatus.Intent or ArtifactStatus.Pending) && !byRoute)
            {
                return;
            }

            var created = held.Role == ArtifactRoles.Record && reported.Role == ArtifactRoles.Version;
            _rows[at] = new Entry(
                id,
                held with
                {
                    Role = created ? held.Role : reported.Role,
                    TargetId = reported.TargetId ?? held.TargetId,
                    Locator = reported.Locator ?? held.Locator,
                    Version = reported.Version ?? held.Version,
                    PriorVersion = created ? held.PriorVersion : held.PriorVersion ?? reported.PriorVersion,
                    Status = reported.Status,
                    Note = reported.Note ?? held.Note,
                },
                SettledByRoute(reported.Status));
        }

        private static bool SettledByRoute(ArtifactStatus status) => status is ArtifactStatus.Removed or ArtifactStatus.Kept or ArtifactStatus.Gone;

        private sealed record Entry(long Id, TargetArtifact Artifact, bool ByRoute);
    }

    /// <summary>The route to RAFS on the fake platform. A flow whose endpoint is RAFS itself gives it no root and reaches nothing else.</summary>
    private sealed class Rig : IDisposable
    {
        public Rig(bool platformEndpoint = true, bool blob = false)
        {
            Platform = new FakeOsduPlatform { RafsBlobMode = blob };
            Ddms = new ContentDdms(Platform);
            Runtime = new HttpRuntime(
                new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
                new SecretResolver([new EnvSecretProvider()]), new TestClock(), Ddms, allowLoopback: true);
            var endpoint = platformEndpoint ? FakeOsduPlatform.Endpoint : FakeOsduPlatform.Endpoint + FakeOsduPlatform.RafsRoot;
            var client = new OsduHttpClient(
                Runtime, endpoint, new TargetAuth { Type = TargetAuthType.None },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
            var options = new ProtocolOptions { WorkflowPollSeconds = 1, DatasetIndexWaitSeconds = 0 };
            var service = new DdmsService("rafs", platformEndpoint ? FakeOsduPlatform.RafsRoot : null, DdmsShape.RafsV2, DdmsCatalog.RafsCollections);
            var target = new FlowTarget { Endpoint = endpoint, Protocol = DeliveryProtocol.Ddms, Ddms = [service], ProtocolOptions = options };
            var flow = platformEndpoint ? Samples.Targeting(target, "samples") : Samples.Targeting(target);
            Protocol = new OsduDdmsProtocol(client, options, NullLogger.Instance, routing: DdmsRouting.Of(flow));
        }

        public FakeOsduPlatform Platform { get; }

        public ContentDdms Ddms { get; }

        public HttpRuntime Runtime { get; }

        public OsduDdmsProtocol Protocol { get; }

        /// <summary>The calls sent from <paramref name="from"/> on, as method and path.</summary>
        public List<string> Sent(int from = 0) => Ddms.Calls.Skip(from).Select(c => c.Method + " " + Uri.UnescapeDataString(c.Uri.AbsolutePath)).ToList();

        /// <summary>One try of <paramref name="ledger"/>'s unit, resuming the steps it completed so far.</summary>
        public DeliveryWork Work(UnitLedger ledger, JsonObject document, IPayloadSource? content, long? existing = null, IReadOnlyDictionary<string, string>? state = null) => new()
        {
            Key = Key(AnalysisId),
            TargetId = AnalysisId,
            Document = document,
            DeliverMetadata = true,
            DeliverPayload = content is not null,
            Payload = content,
            ExistingVersion = existing,
            TargetState = state ?? new Dictionary<string, string>(StringComparer.Ordinal),
            CompletedSteps = ledger.Steps,
            Unit = ledger.Unit,
            StepCompleted = ledger.Listen(() => Ddms.Calls.Count),
        };

        /// <summary>The record and its content delivered before the test's unit: what the ledger then holds of it.</summary>
        public async Task<DeliveryOutcome> DeliveredAsync(IPayloadSource content)
        {
            var outcome = await Protocol.DeliverAsync(new DeliveryWork
            {
                Key = Key(AnalysisId),
                TargetId = AnalysisId,
                Document = Analysis(),
                DeliverMetadata = true,
                DeliverPayload = true,
                Payload = content,
            });
            Assert.True(outcome.Succeeded, outcome.Failure?.Message);
            return outcome;
        }

        public void Dispose()
        {
            Runtime.Dispose();
            Ddms.Dispose();
        }
    }

    /// <summary>
    /// The fake platform behind the failures a test scripts: a call refused before the service acts, and a call that lands and
    /// loses its answer, each after the matching calls to let through. Every request is kept in order.
    /// </summary>
    private sealed class ContentDdms(FakeOsduPlatform platform) : DelegatingHandler(platform)
    {
        private readonly List<Fault> _faults = [];

        public List<FakeHttpHandler.Request> Calls { get; } = [];

        /// <summary>Refuses the next call of <paramref name="method"/> whose path ends with <paramref name="path"/>, after <paramref name="skip"/> such calls went through.</summary>
        public ContentDdms Refuse(HttpMethod method, string path, HttpStatusCode status, int skip = 0)
        {
            _faults.Add(new Fault(method, path, skip) { Status = status });
            return this;
        }

        /// <summary>Lets the next call of <paramref name="method"/> whose path ends with <paramref name="path"/> land, and drops its answer.</summary>
        public ContentDdms Lose(HttpMethod method, string path)
        {
            _faults.Add(new Fault(method, path, 0));
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var bytes = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var media = request.Content?.Headers.ContentType?.MediaType;
            var body = media is not null && media.Contains("json", StringComparison.Ordinal) ? Encoding.UTF8.GetString(bytes) : null;
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            Calls.Add(new FakeHttpHandler.Request(
                request.Method, request.RequestUri, body, media, request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase)));

            var fault = _faults.FirstOrDefault(f => f.Take(request.Method, path));
            if (fault?.Status is { } status)
            {
                return new HttpResponseMessage(status)
                {
                    Content = new StringContent(new JsonObject { ["code"] = (int)status, ["reason"] = "refused by the test" }.ToJsonString(), Encoding.UTF8, "application/json"),
                };
            }

            var response = await base.SendAsync(request, cancellationToken);
            if (fault is not null)
            {
                response.Dispose();
                throw new HttpRequestException("the connection was reset before the answer came");
            }

            return response;
        }

        /// <summary>A scripted failure of the next call of one method whose path ends as given: refused with a status, or its answer lost.</summary>
        private sealed class Fault(HttpMethod method, string path, int skip)
        {
            private int _skip = skip;
            private bool _used;

            public HttpStatusCode? Status { get; init; }

            public bool Take(HttpMethod called, string calledPath)
            {
                if (_used || called != method || !calledPath.EndsWith(path, StringComparison.Ordinal))
                {
                    return false;
                }

                if (_skip > 0)
                {
                    _skip--;
                    return false;
                }

                _used = true;
                return true;
            }
        }
    }
}
