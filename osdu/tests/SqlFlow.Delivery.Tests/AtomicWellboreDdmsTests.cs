using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Storage;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The Wellbore DDMS shape of the ddms route as a unit of work (docs/atomic-delivery-plan.md, osdu/specs/wellbore-ddms
/// INTEGRATION.md section 3.3), against the fake platform with the DDMS's bulk sessions in front of it: the record declared
/// before its metadata write when bulk data follows and reported once the write landed (the record a unit created, or a
/// version naming the one it replaced), each session reported as an intent before its create and by its id once open,
/// abandoned whatever stopped it (a cancelled run included) and reported removed, a commit answered committing polled until it
/// settles, and the bulk step that keeps a resumed try from sending bulk data that landed. Each failure point is followed by
/// the unit's undo: open sessions abandoned (one whose create lost its answer found among the record's sessions), a session
/// still settling or one whose state cannot be read answered failed with the record waiting for it, then the record removed
/// through the DDMS's logical delete, or given back through storage the version it replaced, which a flow whose endpoint is
/// the DDMS itself cannot do.
/// </summary>
public sealed class AtomicWellboreDdmsTests
{
    private const string LogId = "dev:work-product-component--WellLog:log-1";
    private const string OtherLogId = "dev:work-product-component--WellLog:log-2";
    private const string WellboreId = "dev:master-data--Wellbore:wb-1";
    private const string Collection = FakeOsduPlatform.DdmsRoot + "/ddms/v3/welllogs";
    private const string LogPath = Collection + "/" + LogId;
    private const string MetadataIntent = "metadata-intent";

    /// <summary>When every unit of these tests began, which the creation time OSDU gives a record is compared with.</summary>
    private static readonly DateTime Began = new(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);

    private static DeliveryUnit Unit() => new(Guid.NewGuid(), Began);

    private static DeliveryKey Key(string id) => DeliveryKey.Derive("wellbore-ddms", [id]);

    private static JsonObject Log(string name = "GR run", string id = LogId) => FakeOsduPlatform.Record(id, "osdu:wks:work-product-component--WellLog:1.0.0", new JsonObject
    {
        ["Name"] = name,
        ["Curves"] = new JsonArray(new JsonObject { ["CurveID"] = "MD" }, new JsonObject { ["CurveID"] = "GR" }),
    });

    private static DdmsRouteTests.ColumnChunks Chunks(int count) => new(count, "MD", "GR");

    /// <summary>How storage writes a record's creation time.</summary>
    private static string Stamp(DateTime at) => at.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static UndoResult Answer(IReadOnlyList<UndoResult> results, Func<TargetArtifact, bool> which) => Assert.Single(results, r => which(r.Item.Artifact));

    private static UndoResult RecordAnswer(IReadOnlyList<UndoResult> results, string id = LogId)
        => Answer(results, a => a.Slot == TargetArtifact.RecordSlot && a.TargetId == id);

    private static UndoResult SessionAnswer(IReadOnlyList<UndoResult> results, string? sessionId)
        => Answer(results, a => a.Role == ArtifactRoles.Session && a.TargetId == sessionId);

    /// <summary>Every item of <paramref name="works"/> answered exactly once, each with a state an undo settles in.</summary>
    private static void AnsweredOnce(IReadOnlyList<UndoResult> results, params UndoWork[] works)
    {
        var items = works.SelectMany(w => w.Items).ToList();
        Assert.Equal(items.Count, results.Count);
        Assert.All(items, item => Assert.Single(results, r => ReferenceEquals(r.Item, item)));
        Assert.All(results, r => Assert.True(ArtifactStatuses.IsUndoOutcome(r.Outcome), $"{r.Item.Artifact.Slot} was answered {r.Outcome}"));
    }

    /// <summary>The record answered failed because something beside it could not be undone yet: left for the next undo, untouched.</summary>
    private static void Waits(UndoResult record, int beside)
    {
        Assert.Equal(ArtifactStatus.Failed, record.Outcome);
        Assert.StartsWith($"{beside} item(s) the delivery made beside the record could not be undone yet (", record.Note, StringComparison.Ordinal);
        Assert.EndsWith("); the record is taken back with them, on the next undo", record.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_created_log_is_declared_before_its_metadata_write_reported_after_it_and_its_session_is_an_intent_before_the_create()
    {
        using var rig = new Rig();
        var ledger = new UnitLedger(Unit());
        var outcome = await rig.Protocol.DeliverAsync(rig.Work(ledger, Log(), Chunks(2)));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal(2, outcome.ChunksSent);
        Assert.Equal(
            [
                "POST " + Collection,
                "POST " + LogPath + "/sessions",
                "POST " + LogPath + "/sessions/sess-1/data",
                "POST " + LogPath + "/sessions/sess-1/data",
                "PATCH " + LogPath + "/sessions/sess-1",
                "GET " + LogPath + "/data",
                "GET " + LogPath,
            ],
            rig.Sent());
        Assert.Equal([MetadataIntent, "metadata", "session", "session", "bulk"], ledger.Reported.Select(r => r.Report.Step));

        // Bulk data follows, so the record is named before its write goes: a write that lands without its answer is still the unit's.
        var (declared, beforeWrite) = ledger.Reported[0];
        Assert.Equal(0, beforeWrite);
        var intended = Assert.Single(declared.Artifacts);
        Assert.Equal(
            (TargetArtifact.RecordSlot, ArtifactRoles.Record, (string?)LogId, (long?)null, ArtifactStatus.Intent),
            (intended.Slot, intended.Role, intended.TargetId, intended.Version, intended.Status));

        // Once the write landed, the same slot is the record the unit created, under the version the write gave it.
        var (metadata, afterWrite) = ledger.Reported[1];
        Assert.Equal(1, afterWrite);
        var record = Assert.Single(metadata.Artifacts);
        Assert.Equal((TargetArtifact.RecordSlot, ArtifactRoles.Record, LogId, ArtifactStatus.Pending), (record.Slot, record.Role, record.TargetId, record.Status));
        Assert.Equal(rig.Platform.History[LogId][0]["version"]!.GetValue<long>(), record.Version);
        Assert.Null(record.PriorVersion);

        // The session is an intent before its create goes, found by the record it is opened for, and named once it is open.
        var (opening, beforeCreate) = ledger.Reported[2];
        var intent = Assert.Single(opening.Artifacts);
        Assert.StartsWith("session:", intent.Slot, StringComparison.Ordinal);
        Assert.Equal((ArtifactRoles.Session, ArtifactStatus.Intent, (string?)null, (string?)LogId), (intent.Role, intent.Status, intent.TargetId, intent.Locator));
        Assert.Equal("POST " + LogPath + "/sessions", rig.Sent()[beforeCreate]);
        var (opened, afterCreate) = ledger.Reported[3];
        var session = Assert.Single(opened.Artifacts);
        Assert.Equal((intent.Slot, ArtifactRoles.Session, "sess-1", ArtifactStatus.Pending, (string?)LogId), (session.Slot, session.Role, session.TargetId, session.Status, session.Locator));
        Assert.Equal(beforeCreate + 1, afterCreate);

        // The bulk step goes once the commit landed and before the bulk is read back, and names nothing to undo.
        var (bulk, afterCommit) = ledger.Reported[4];
        Assert.Empty(bulk.Artifacts);
        Assert.Equal(("2", "sess-1"), (bulk.Returned["chunks"], bulk.Returned["sessionId"]));
        Assert.Contains("\"commit\"", rig.Ddms.Calls[afterCommit - 1].Body, StringComparison.Ordinal);
        Assert.Equal("GET " + LogPath + "/data", rig.Sent()[afterCommit]);

        // Reported again under its slot, each artifact is one row: the record and the session stay open until the unit commits.
        Assert.Equal(
            [(TargetArtifact.RecordSlot, ArtifactStatus.Pending, (string?)LogId, record.Version), (intent.Slot, ArtifactStatus.Pending, (string?)"sess-1", (long?)null)],
            ledger.Rows.Select(r => (r.Slot, r.Status, r.TargetId, r.Version)));
        Assert.Equal("committed", rig.Ddms.Sessions["sess-1"].State);
    }

    [Fact]
    public async Task An_update_is_declared_and_reported_as_a_version_naming_the_one_it_replaced()
    {
        using var rig = new Rig();
        var held = await rig.DeliveredAsync();
        var ledger = new UnitLedger(Unit());
        var outcome = await rig.Protocol.DeliverAsync(rig.Work(ledger, Log("GR run, corrected"), Chunks(2), existing: held));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);

        var intended = Assert.Single(ledger.Reported[0].Report.Artifacts);
        Assert.Equal((ArtifactRoles.Version, held, (long?)null, ArtifactStatus.Intent), (intended.Role, intended.PriorVersion, intended.Version, intended.Status));
        var record = ledger.Row(TargetArtifact.RecordSlot);
        Assert.Equal((ArtifactRoles.Version, LogId, held), (record.Role, record.TargetId, record.PriorVersion));
        Assert.True(record.Version > held);
        Assert.Equal(ArtifactStatus.Pending, record.Status);
        Assert.Single(ledger.Rows, r => r.Role == ArtifactRoles.Session && r.TargetId == "sess-1");
    }

    [Fact]
    public async Task Metadata_alone_and_a_record_collection_leave_nothing_to_undo()
    {
        using var rig = new Rig();

        // With no bulk data to follow, the record's write is the whole delivery.
        var alone = new UnitLedger(Unit());
        Assert.True((await rig.Protocol.DeliverAsync(rig.Work(alone, Log(), null))).Succeeded);
        Assert.Equal(["metadata"], alone.Reported.Select(r => r.Report.Step));
        Assert.Empty(alone.Rows);

        var wellbore = new UnitLedger(Unit());
        var document = FakeOsduPlatform.Record(WellboreId, "osdu:wks:master-data--Wellbore:1.0.0", new JsonObject { ["FacilityName"] = "A/1-F-1" });
        Assert.True((await rig.Protocol.DeliverAsync(rig.Work(wellbore, document, null))).Succeeded);
        Assert.Empty(wellbore.Rows);
    }

    [Fact]
    public async Task A_refused_metadata_write_leaves_its_intent_which_the_undo_finds_gone()
    {
        using var rig = new Rig();
        rig.Ddms.Refuse(HttpMethod.Post, "/ddms/v3/welllogs", HttpStatusCode.BadRequest);
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Log(), Chunks(2));
        var held = await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(work));
        Assert.Equal(400, held.StatusCode);
        Assert.Equal([MetadataIntent], ledger.Reported.Select(r => r.Report.Step));
        Assert.False(rig.Platform.Records.ContainsKey(LogId));

        var calls = rig.Ddms.Calls.Count;
        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        var record = RecordAnswer(results);
        Assert.Equal((ArtifactStatus.Gone, LogId + ": OSDU no longer holds the record"), (record.Outcome, record.Note));
        Assert.Equal(["GET " + LogPath], rig.Sent(calls));
    }

    [Fact]
    public async Task A_created_log_whose_metadata_answer_was_lost_is_removed_by_the_undo()
    {
        using var rig = new Rig();
        rig.Ddms.Lose(HttpMethod.Post, "/ddms/v3/welllogs");
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Log(), Chunks(2));
        await Assert.ThrowsAsync<DeliveryException>(() => rig.Protocol.DeliverAsync(work));

        // The write landed and its answer was lost: the intent, which knows no version, is all the ledger holds of the record.
        Assert.True(rig.Platform.Records.ContainsKey(LogId));
        var intent = Assert.Single(ledger.Rows);
        Assert.Equal((ArtifactStatus.Intent, (long?)null), (intent.Status, intent.Version));

        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        Assert.Equal((ArtifactStatus.Removed, LogId + ": removed from OSDU (reversible)"), (RecordAnswer(results).Outcome, RecordAnswer(results).Note));
        Assert.Contains(LogId, rig.Platform.Removed);
    }

    [Fact]
    public async Task A_resumed_try_after_a_lost_metadata_answer_writes_the_record_again_under_the_same_slot()
    {
        using var rig = new Rig();
        rig.Ddms.Lose(HttpMethod.Post, "/ddms/v3/welllogs");
        var ledger = new UnitLedger(Unit());
        await Assert.ThrowsAsync<DeliveryException>(() => rig.Protocol.DeliverAsync(rig.Work(ledger, Log(), Chunks(2))));

        var resumed = await rig.Protocol.DeliverAsync(rig.Work(ledger, Log(), Chunks(2)));
        Assert.True(resumed.Succeeded, resumed.Failure?.Message);
        Assert.Equal(2, rig.Sent().Count(c => c == "POST " + Collection));
        Assert.Equal([MetadataIntent, MetadataIntent, "metadata", "session", "session", "bulk"], ledger.Reported.Select(r => r.Report.Step));
        var record = ledger.Row(TargetArtifact.RecordSlot);
        Assert.Equal((ArtifactRoles.Record, ArtifactStatus.Pending, (long?)rig.Platform.History[LogId][1]["version"]!.GetValue<long>()), (record.Role, record.Status, record.Version));
    }

    [Fact]
    public async Task An_update_whose_metadata_answer_was_lost_is_given_back_the_version_it_replaced()
    {
        using var rig = new Rig();
        var held = await rig.DeliveredAsync();
        rig.Ddms.Lose(HttpMethod.Post, "/ddms/v3/welllogs");
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Log("GR run, corrected"), Chunks(2), existing: held);
        await Assert.ThrowsAsync<DeliveryException>(() => rig.Protocol.DeliverAsync(work));
        Assert.Equal("GR run, corrected", rig.Platform.Records[LogId]["data"]!["Name"]!.GetValue<string>());

        // The intent knows no version it wrote; storage's latest is not the one the write would have replaced, so it landed.
        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        var record = RecordAnswer(results);
        Assert.Equal(ArtifactStatus.Restored, record.Outcome);
        Assert.StartsWith($"{LogId}: version {held} written back as version ", record.Note, StringComparison.Ordinal);
        Assert.Equal("GR run", rig.Platform.Records[LogId]["data"]!["Name"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_update_refused_before_it_landed_is_found_not_to_have_landed_and_nothing_is_written_back()
    {
        using var rig = new Rig();
        var held = await rig.DeliveredAsync();
        rig.Ddms.Refuse(HttpMethod.Post, "/ddms/v3/welllogs", HttpStatusCode.ServiceUnavailable);
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Log("GR run, corrected"), Chunks(2), existing: held);
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(work));

        var calls = rig.Ddms.Calls.Count;
        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        var record = RecordAnswer(results);
        Assert.Equal(
            (ArtifactStatus.Gone, $"{LogId}: OSDU's latest version is still {held}, the one the delivery's write would have replaced, so the write did not land"),
            (record.Outcome, record.Note));
        Assert.Equal(["GET /api/storage/v2/records/versions/" + LogId], rig.Sent(calls));
        Assert.Equal(held, rig.Platform.Records[LogId]["version"]!.GetValue<long>());
    }

    [Fact]
    public async Task A_single_chunk_goes_without_a_session_and_its_failure_leaves_the_created_log_to_the_undo()
    {
        using var rig = new Rig();
        rig.Ddms.Refuse(HttpMethod.Post, LogId + "/data", HttpStatusCode.InternalServerError);
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Log(), Chunks(1));
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(work));
        Assert.Equal([MetadataIntent, "metadata"], ledger.Reported.Select(r => r.Report.Step));
        Assert.DoesNotContain(rig.Sent(), c => c.Contains("/sessions", StringComparison.Ordinal));

        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        var record = RecordAnswer(results);
        Assert.Equal(ArtifactStatus.Removed, record.Outcome);
        Assert.Equal(LogId + ": removed from OSDU (reversible)", record.Note);
        Assert.Contains(LogId, rig.Platform.Removed);
        Assert.Equal("DELETE " + LogPath, rig.Sent()[^1]);
    }

    [Fact]
    public async Task A_refused_session_create_leaves_its_intent_which_the_undo_finds_nothing_open_for()
    {
        using var rig = new Rig();
        rig.Ddms.Refuse(HttpMethod.Post, LogId + "/sessions", HttpStatusCode.InternalServerError);
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Log(), Chunks(2));
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(work));
        Assert.Equal([MetadataIntent, "metadata", "session"], ledger.Reported.Select(r => r.Report.Step));
        Assert.Equal(ArtifactStatus.Intent, Assert.Single(ledger.Rows, r => r.Role == ArtifactRoles.Session).Status);
        Assert.Empty(rig.Ddms.Sessions);

        var calls = rig.Ddms.Calls.Count;
        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        var session = SessionAnswer(results, null);
        Assert.Equal(ArtifactStatus.Gone, session.Outcome);
        Assert.Equal($"no session of {LogId} is open: the create never landed, or its session ended", session.Note);
        Assert.Equal(ArtifactStatus.Removed, RecordAnswer(results).Outcome);

        // The record's sessions are listed while the record is there to list them under; nothing is abandoned.
        Assert.Equal(["GET " + LogPath + "/sessions", "GET " + LogPath, "DELETE " + LogPath], rig.Sent(calls));
    }

    [Fact]
    public async Task A_session_whose_create_lost_its_answer_is_found_among_the_records_sessions_and_abandoned_before_the_log_goes()
    {
        using var rig = new Rig();
        rig.Ddms.Lose(HttpMethod.Post, LogId + "/sessions");
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Log(), Chunks(2));
        var lost = await Assert.ThrowsAsync<DeliveryException>(() => rig.Protocol.DeliverAsync(work));
        Assert.IsType<HttpRequestException>(lost.InnerException);
        Assert.Equal("open", rig.Ddms.Sessions["sess-1"].State);

        // The create is never sent again: the intent is all the ledger knows of the session it opened.
        Assert.Single(rig.Sent(), c => c == "POST " + LogPath + "/sessions");
        var intent = Assert.Single(ledger.Rows, r => r.Role == ArtifactRoles.Session);
        Assert.Equal((ArtifactStatus.Intent, (string?)null), (intent.Status, intent.TargetId));

        var calls = rig.Ddms.Calls.Count;
        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        var session = SessionAnswer(results, null);
        Assert.Equal(ArtifactStatus.Removed, session.Outcome);
        Assert.Equal($"open session(s) sess-1 of {LogId} abandoned", session.Note);
        Assert.Equal("abandoned", rig.Ddms.Sessions["sess-1"].State);
        Assert.Equal(ArtifactStatus.Removed, RecordAnswer(results).Outcome);
        Assert.Equal(["GET " + LogPath + "/sessions", "PATCH " + LogPath + "/sessions/sess-1", "GET " + LogPath, "DELETE " + LogPath], rig.Sent(calls));
    }

    [Fact]
    public async Task A_session_the_listing_shows_still_settling_is_failed_and_the_log_waits_for_it()
    {
        using var rig = new Rig();
        rig.Ddms.Lose(HttpMethod.Post, LogId + "/sessions");
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Log(), Chunks(2));
        await Assert.ThrowsAsync<DeliveryException>(() => rig.Protocol.DeliverAsync(work));

        // The session the lost create opened is being abandoned when the undo lists the record's sessions.
        rig.Ddms.Sessions["sess-1"].State = "abandoning";
        var calls = rig.Ddms.Calls.Count;
        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        var session = SessionAnswer(results, null);
        Assert.Equal(ArtifactStatus.Failed, session.Outcome);
        Assert.StartsWith("session sess-1 is still abandoning", session.Note, StringComparison.Ordinal);
        Waits(RecordAnswer(results), 1);
        Assert.Equal(["GET " + LogPath + "/sessions"], rig.Sent(calls));
        Assert.DoesNotContain(LogId, rig.Platform.Removed);
    }

    [Fact]
    public async Task A_refused_chunk_abandons_the_session_and_reports_it_removed_so_the_undo_takes_the_log_alone()
    {
        using var rig = new Rig();
        rig.Ddms.Refuse(HttpMethod.Post, "/sessions/sess-1/data", HttpStatusCode.UnprocessableEntity, skip: 1);
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Log(), Chunks(2));
        var refused = await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(work));
        Assert.Equal(422, refused.StatusCode);

        // The second chunk is sent once, and the session abandoned at once.
        Assert.Equal(2, rig.Sent().Count(c => c.EndsWith("/sessions/sess-1/data", StringComparison.Ordinal)));
        Assert.Contains("\"abandon\"", rig.Ddms.Calls[^1].Body, StringComparison.Ordinal);
        Assert.Equal("abandoned", rig.Ddms.Sessions["sess-1"].State);
        Assert.Equal([MetadataIntent, "metadata", "session", "session", "session"], ledger.Reported.Select(r => r.Report.Step));
        var (abandoned, _) = ledger.Reported[^1];
        Assert.Equal("abandoned", abandoned.Returned["state"]);
        var removed = Assert.Single(abandoned.Artifacts);
        Assert.Equal((ArtifactRoles.Session, "sess-1", ArtifactStatus.Removed, "abandoned when its delivery failed"), (removed.Role, removed.TargetId, removed.Status, removed.Note));

        // A session the route removed itself is no longer open, so the undo is given the record alone.
        var undo = ledger.Undo(work);
        Assert.Equal([TargetArtifact.RecordSlot], undo.Items.Select(i => i.Artifact.Slot));
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        Assert.Equal(ArtifactStatus.Removed, RecordAnswer(results).Outcome);
        Assert.Contains(LogId, rig.Platform.Removed);
    }

    [Fact]
    public async Task A_chunk_whose_answer_was_lost_is_not_sent_again_and_its_session_is_abandoned()
    {
        using var rig = new Rig();
        rig.Ddms.Lose(HttpMethod.Post, "/sessions/sess-1/data", skip: 1);
        var ledger = new UnitLedger(Unit());
        await Assert.ThrowsAsync<DeliveryException>(() => rig.Protocol.DeliverAsync(rig.Work(ledger, Log(), Chunks(2))));

        // A chunk sent twice lands twice in the committed bulk, so a chunk whose outcome is unclear ends the session instead.
        Assert.Equal(2, rig.Sent().Count(c => c.EndsWith("/sessions/sess-1/data", StringComparison.Ordinal)));
        Assert.Equal(2, rig.Ddms.Sessions["sess-1"].Chunks.Count);
        Assert.Equal("abandoned", rig.Ddms.Sessions["sess-1"].State);
        Assert.Equal(ArtifactStatus.Removed, Assert.Single(ledger.Rows, r => r.Role == ArtifactRoles.Session).Status);
        Assert.False(rig.Platform.Bulk.ContainsKey(LogId));
    }

    [Fact]
    public async Task A_run_cancelled_mid_bulk_abandons_its_session_before_it_stops()
    {
        using var rig = new Rig();
        using var run = new CancellationTokenSource();
        rig.Ddms.CancelAt(HttpMethod.Post, "/sessions/sess-1/data", run, skip: 1);
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Log(), Chunks(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Protocol.DeliverAsync(work, run.Token));

        // The abandon and its report go whatever the run's token says, so the session is not left for the undo.
        Assert.Single(rig.Ddms.Sessions["sess-1"].Chunks);
        Assert.Equal("abandoned", rig.Ddms.Sessions["sess-1"].State);
        Assert.Equal("PATCH " + LogPath + "/sessions/sess-1", rig.Sent()[^1]);
        var removed = Assert.Single(ledger.Reported[^1].Report.Artifacts);
        Assert.Equal((ArtifactRoles.Session, "sess-1", ArtifactStatus.Removed), (removed.Role, removed.TargetId, removed.Status));

        var undo = ledger.Undo(work, UndoReason.Abandoned);
        Assert.Equal([TargetArtifact.RecordSlot], undo.Items.Select(i => i.Artifact.Slot));
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        Assert.Equal(ArtifactStatus.Removed, RecordAnswer(results).Outcome);
    }

    [Fact]
    public async Task An_abandon_that_failed_leaves_the_session_open_for_the_undo_which_abandons_it_before_the_log_goes()
    {
        using var rig = new Rig();
        rig.Ddms
            .Refuse(HttpMethod.Post, "/sessions/sess-1/data", HttpStatusCode.UnprocessableEntity, skip: 1)
            .Refuse(HttpMethod.Patch, "/sessions/sess-1", HttpStatusCode.InternalServerError, body: "abandon");
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Log(), Chunks(2));
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(work));
        Assert.Equal([MetadataIntent, "metadata", "session", "session"], ledger.Reported.Select(r => r.Report.Step));
        Assert.Equal("open", rig.Ddms.Sessions["sess-1"].State);

        var calls = rig.Ddms.Calls.Count;
        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        var session = SessionAnswer(results, "sess-1");
        Assert.Equal((ArtifactStatus.Removed, "session sess-1 abandoned"), (session.Outcome, session.Note));
        Assert.Equal("abandoned", rig.Ddms.Sessions["sess-1"].State);
        Assert.Equal(ArtifactStatus.Removed, RecordAnswer(results).Outcome);
        Assert.Equal(
            ["GET " + LogPath + "/sessions/sess-1", "PATCH " + LogPath + "/sessions/sess-1", "GET " + LogPath, "DELETE " + LogPath],
            rig.Sent(calls));
    }

    [Fact]
    public async Task A_commit_answered_committing_is_polled_until_it_is_committed()
    {
        using var rig = new Rig();
        rig.Ddms.NextCommit = SessionDdms.CommitOutcome.StaysCommitting;
        rig.Ddms.CommittingReads = 2;
        var ledger = new UnitLedger(Unit());
        var outcome = await rig.Protocol.DeliverAsync(rig.Work(ledger, Log(), Chunks(2)));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);

        // Two reads answer committing, the third committed; only then is the bulk step reported and the record read back.
        Assert.Equal(3, rig.Sent().Count(c => c == "GET " + LogPath + "/sessions/sess-1"));
        Assert.Equal("committed", rig.Ddms.Sessions["sess-1"].State);
        Assert.Equal(rig.Platform.Records[LogId]["version"]!.GetValue<long>(), outcome.TargetVersion);
        Assert.Equal("bulk", ledger.Reported[^1].Report.Step);
    }

    [Fact]
    public async Task A_commit_that_never_leaves_committing_fails_the_try_and_its_undo_waits_with_the_log_until_the_session_settles()
    {
        using var rig = new Rig();
        rig.Ddms.NextCommit = SessionDdms.CommitOutcome.StaysCommitting;
        rig.Ddms.CommittingReads = int.MaxValue;
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Log(), Chunks(2));
        var stuck = await Assert.ThrowsAsync<DeliveryException>(() => rig.Protocol.DeliverAsync(work));
        Assert.Contains("session sess-1 for " + LogId + " could not be committed and is committing; the payload did not land", stuck.Message, StringComparison.Ordinal);

        // The first read and thirty polls; the abandon that follows meets a session that is not open, so nothing is reported removed.
        Assert.Equal(31, rig.Sent().Count(c => c == "GET " + LogPath + "/sessions/sess-1"));
        Assert.DoesNotContain(ledger.Reported, r => r.Report.Step == "bulk");
        Assert.Equal(ArtifactStatus.Pending, Assert.Single(ledger.Rows, r => r.Role == ArtifactRoles.Session).Status);

        // A commit under way can still land, or fail and send the session back to open: the session's undo is tried again once
        // it settles, and the record waits with it, since a commit that lands writes a version over whatever the undo did.
        var calls = rig.Ddms.Calls.Count;
        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        var session = SessionAnswer(results, "sess-1");
        Assert.Equal(ArtifactStatus.Failed, session.Outcome);
        Assert.StartsWith("session sess-1 is still committing", session.Note, StringComparison.Ordinal);
        Waits(RecordAnswer(results), 1);
        Assert.Equal(["GET " + LogPath + "/sessions/sess-1"], rig.Sent(calls));
        Assert.DoesNotContain(LogId, rig.Platform.Removed);

        // The commit lands before the next undo: the session has nothing to abandon, and the record goes.
        ledger.Settle(results);
        rig.Ddms.Sessions["sess-1"].CommittingLeft = 0;
        var retry = ledger.Undo(work);
        Assert.Equal(undo.Items.Select(i => i.ArtifactId), retry.Items.Select(i => i.ArtifactId));
        var settled = await rig.Protocol.UndoAsync([retry]);
        AnsweredOnce(settled, retry);
        Assert.Equal((ArtifactStatus.Gone, "session sess-1 is committed, so there is nothing to abandon"), (SessionAnswer(settled, "sess-1").Outcome, SessionAnswer(settled, "sess-1").Note));
        Assert.Equal(ArtifactStatus.Removed, RecordAnswer(settled).Outcome);
        Assert.Contains(LogId, rig.Platform.Removed);
    }

    [Fact]
    public async Task A_commit_the_ddms_answers_for_an_abandoned_session_fails_the_try_and_the_undo_finds_the_session_ended()
    {
        using var rig = new Rig();
        rig.Ddms.NextCommit = SessionDdms.CommitOutcome.Expires;
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Log(), Chunks(2));
        var ended = await Assert.ThrowsAsync<DeliveryException>(() => rig.Protocol.DeliverAsync(work));
        Assert.Contains("is abandoned; the payload did not land", ended.Message, StringComparison.Ordinal);
        Assert.Equal(ArtifactStatus.Pending, Assert.Single(ledger.Rows, r => r.Role == ArtifactRoles.Session).Status);

        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        var session = SessionAnswer(results, "sess-1");
        Assert.Equal((ArtifactStatus.Gone, "session sess-1 is abandoned, so there is nothing to abandon"), (session.Outcome, session.Note));
        Assert.Equal(ArtifactStatus.Removed, RecordAnswer(results).Outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_refused_commit_or_one_the_ddms_sent_back_to_open_abandons_its_session(HttpStatusCode refusal)
    {
        using var rig = new Rig();
        rig.Ddms.Refuse(HttpMethod.Patch, "/sessions/sess-1", refusal, body: "commit");
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Log(), Chunks(2));
        await Assert.ThrowsAnyAsync<DeliveryException>(() => rig.Protocol.DeliverAsync(work));

        Assert.Equal("abandoned", rig.Ddms.Sessions["sess-1"].State);
        Assert.Equal(ArtifactStatus.Removed, Assert.Single(ledger.Rows, r => r.Role == ArtifactRoles.Session).Status);
        Assert.False(rig.Platform.Bulk.ContainsKey(LogId));
        var undo = ledger.Undo(work);
        Assert.Equal([TargetArtifact.RecordSlot], undo.Items.Select(i => i.Artifact.Slot));
    }

    [Fact]
    public async Task A_commit_whose_answer_was_lost_after_it_committed_is_taken_as_landed()
    {
        using var rig = new Rig();
        rig.Ddms.Lose(HttpMethod.Patch, "/sessions/sess-1", body: "commit");
        var ledger = new UnitLedger(Unit());
        var outcome = await rig.Protocol.DeliverAsync(rig.Work(ledger, Log(), Chunks(2)));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Single(rig.Sent(), c => c == "GET " + LogPath + "/sessions/sess-1");
        Assert.Equal("bulk", ledger.Reported[^1].Report.Step);
        Assert.DoesNotContain(rig.Ddms.Calls, c => c.Body?.Contains("\"abandon\"", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task A_resumed_try_does_not_send_bulk_data_that_landed_again()
    {
        using var rig = new Rig();

        // The bulk landed and the read back of the record failed: the try fails after its bulk step.
        rig.Ddms.Refuse(HttpMethod.Get, "/welllogs/" + LogId, HttpStatusCode.ServiceUnavailable);
        var ledger = new UnitLedger(Unit());
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(rig.Work(ledger, Log(), Chunks(2))));
        Assert.Equal("bulk", ledger.Reported[^1].Report.Step);
        var reported = ledger.Reported.Count;

        var calls = rig.Ddms.Calls.Count;
        var resumed = await rig.Protocol.DeliverAsync(rig.Work(ledger, Log(), Chunks(2)));
        Assert.True(resumed.Succeeded, resumed.Failure?.Message);
        Assert.Equal(["GET " + LogPath + "/data", "GET " + LogPath], rig.Sent(calls));
        Assert.Equal(reported, ledger.Reported.Count);
        Assert.Equal(("2", "sess-1"), (resumed.Returned["chunks"], resumed.Returned["sessionId"]));
        Assert.Single(rig.Ddms.Sessions);
    }

    [Fact]
    public async Task A_resumed_try_after_a_failed_session_writes_no_metadata_again_and_opens_a_session_of_its_own()
    {
        using var rig = new Rig();
        rig.Ddms.Refuse(HttpMethod.Post, "/sessions/sess-1/data", HttpStatusCode.UnprocessableEntity);
        var ledger = new UnitLedger(Unit());
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(rig.Work(ledger, Log(), Chunks(2))));
        var first = Assert.Single(ledger.Rows, r => r.Role == ArtifactRoles.Session);

        var calls = rig.Ddms.Calls.Count;
        var resumed = await rig.Protocol.DeliverAsync(rig.Work(ledger, Log(), Chunks(2)));
        Assert.True(resumed.Succeeded, resumed.Failure?.Message);
        Assert.DoesNotContain(rig.Sent(calls), c => c == "POST " + Collection);
        Assert.Equal("sess-2", resumed.Returned["sessionId"]);
        Assert.Single(ledger.Reported, r => r.Report.Step == MetadataIntent);

        // Each try's session has a slot of its own; the record stays as the first try reported it.
        var sessions = ledger.Rows.Where(r => r.Role == ArtifactRoles.Session).ToList();
        Assert.Equal(2, sessions.Count);
        Assert.Equal((first.Slot, ArtifactStatus.Removed, (string?)"sess-1"), (sessions[0].Slot, sessions[0].Status, sessions[0].TargetId));
        Assert.Equal((ArtifactStatus.Pending, (string?)"sess-2"), (sessions[1].Status, sessions[1].TargetId));
        Assert.NotEqual(first.Slot, sessions[1].Slot);
        Assert.Equal(rig.Platform.History[LogId][0]["version"]!.GetValue<long>(), ledger.Row(TargetArtifact.RecordSlot).Version);
    }

    [Fact]
    public async Task The_undo_of_an_update_writes_back_through_storage_the_version_it_replaced_with_its_bulk_link()
    {
        using var rig = new Rig();
        var held = await rig.DeliveredAsync();
        var link = WellboreDdmsBulkLink.Of(rig.Platform.Records[LogId]);
        Assert.NotNull(link);
        rig.Ddms.Refuse(HttpMethod.Post, "/sessions/sess-1/data", HttpStatusCode.UnprocessableEntity);
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Log("GR run, corrected"), Chunks(2), existing: held);
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(work));
        Assert.Equal("GR run, corrected", rig.Platform.Records[LogId]["data"]!["Name"]!.GetValue<string>());

        var calls = rig.Ddms.Calls.Count;
        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        var record = RecordAnswer(results);
        Assert.Equal(ArtifactStatus.Restored, record.Outcome);
        var latest = rig.Platform.Records[LogId];
        Assert.Equal($"{LogId}: version {held} written back as version {latest["version"]!.GetValue<long>()}", record.Note);

        // The record is written back through storage, not the DDMS: the version carries its own bulk link, and the bulk data
        // it names is what the DDMS serves again.
        Assert.Equal("GR run", latest["data"]!["Name"]!.GetValue<string>());
        Assert.Equal(link, WellboreDdmsBulkLink.Of(latest));
        Assert.Contains("PUT /api/storage/v2/records", rig.Sent(calls));
        Assert.DoesNotContain(rig.Sent(calls), c => c.StartsWith("DELETE ", StringComparison.Ordinal) || c == "POST " + Collection);
        Assert.DoesNotContain(LogId, rig.Platform.Removed);
    }

    [Fact]
    public async Task Under_a_ddms_endpoint_an_update_is_kept_and_says_why()
    {
        using var rig = new Rig(platformEndpoint: false);
        var held = await rig.DeliveredAsync();
        rig.Ddms.Refuse(HttpMethod.Post, "/sessions/sess-1/data", HttpStatusCode.UnprocessableEntity);
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Log("GR run, corrected"), Chunks(2), existing: held);
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(work));

        var calls = rig.Ddms.Calls.Count;
        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        var record = RecordAnswer(results);
        Assert.Equal(ArtifactStatus.Kept, record.Outcome);
        Assert.Contains("the flow's endpoint is the DDMS itself, so the storage service that keeps the record's versions is not under it", record.Note, StringComparison.Ordinal);
        Assert.EndsWith("; OSDU keeps the version the unfinished delivery wrote", record.Note, StringComparison.Ordinal);
        Assert.Empty(rig.Sent(calls));
        Assert.Equal("GR run, corrected", rig.Platform.Records[LogId]["data"]!["Name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Under_a_ddms_endpoint_a_created_log_is_still_removed_through_the_ddms()
    {
        using var rig = new Rig(platformEndpoint: false);
        rig.Ddms.Refuse(HttpMethod.Post, LogId + "/sessions", HttpStatusCode.InternalServerError);
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Log(), Chunks(2));
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(work));

        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        Assert.Equal(ArtifactStatus.Removed, RecordAnswer(results).Outcome);
        Assert.Contains(LogId, rig.Platform.Removed);
        Assert.DoesNotContain(rig.Sent(), c => c.Contains("/api/storage/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_log_osdu_held_before_the_unit_began_is_given_back_its_earlier_version_rather_than_removed()
    {
        using var rig = new Rig();
        var before = rig.Platform.Put(Log("written by another system"));
        rig.Ddms.Refuse(HttpMethod.Post, LogId + "/sessions", HttpStatusCode.InternalServerError);
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Log(), Chunks(2));
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(work));

        // The ledger held no version, so the unit reported the record as one it created; storage says it was created a day before.
        Assert.Equal(ArtifactRoles.Record, ledger.Row(TargetArtifact.RecordSlot).Role);
        rig.Platform.Records[LogId]["createTime"] = Stamp(Began.AddDays(-1));

        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        var record = RecordAnswer(results);
        Assert.Equal(ArtifactStatus.Restored, record.Outcome);
        Assert.StartsWith($"{LogId}: version {before} written back as version ", record.Note, StringComparison.Ordinal);
        Assert.Equal("written by another system", rig.Platform.Records[LogId]["data"]!["Name"]!.GetValue<string>());
        Assert.DoesNotContain(LogId, rig.Platform.Removed);
        Assert.DoesNotContain(rig.Sent(), c => c.StartsWith("DELETE ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-3)]
    public async Task A_log_osdu_created_after_the_unit_began_or_within_the_clock_allowance_is_removed(int minutes)
    {
        using var rig = new Rig();
        rig.Ddms.Refuse(HttpMethod.Post, LogId + "/sessions", HttpStatusCode.InternalServerError);
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Log(), Chunks(2));
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(work));
        rig.Platform.Records[LogId]["createTime"] = Stamp(Began.AddMinutes(minutes));

        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        Assert.Equal(ArtifactStatus.Removed, RecordAnswer(results).Outcome);
        Assert.Contains(LogId, rig.Platform.Removed);
    }

    [Fact]
    public async Task Newer_work_keeps_the_log_while_its_sessions_are_still_abandoned()
    {
        using var rig = new Rig();
        rig.Ddms
            .Refuse(HttpMethod.Post, "/sessions/sess-1/data", HttpStatusCode.UnprocessableEntity)
            .Refuse(HttpMethod.Patch, "/sessions/sess-1", HttpStatusCode.InternalServerError, body: "abandon");
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Log(), Chunks(2));
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(work));

        var undo = ledger.Undo(work, UndoReason.Abandoned, keepRecord: true);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        var record = RecordAnswer(results);
        Assert.Equal((ArtifactStatus.Superseded, "the record's newer work writes it again, so it is left as it is"), (record.Outcome, record.Note));
        Assert.Equal(ArtifactStatus.Removed, SessionAnswer(results, "sess-1").Outcome);
        Assert.Equal("abandoned", rig.Ddms.Sessions["sess-1"].State);
        Assert.DoesNotContain(LogId, rig.Platform.Removed);
        Assert.DoesNotContain(rig.Sent(), c => c.StartsWith("DELETE ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_second_undo_finds_the_session_and_the_log_gone_and_changes_nothing()
    {
        using var rig = new Rig();
        rig.Ddms
            .Refuse(HttpMethod.Post, "/sessions/sess-1/data", HttpStatusCode.UnprocessableEntity)
            .Refuse(HttpMethod.Patch, "/sessions/sess-1", HttpStatusCode.InternalServerError, body: "abandon");
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Log(), Chunks(2));
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(work));
        var undo = ledger.Undo(work);
        var first = await rig.Protocol.UndoAsync([undo]);
        Assert.All(first, r => Assert.Equal(ArtifactStatus.Removed, r.Outcome));

        // The same artifacts again, as when the first undo's answer never reached the ledger: only reads go.
        var calls = rig.Ddms.Calls.Count;
        var again = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(again, undo);
        Assert.Equal((ArtifactStatus.Gone, "session sess-1 is no longer known to the DDMS"), (SessionAnswer(again, "sess-1").Outcome, SessionAnswer(again, "sess-1").Note));
        Assert.Equal((ArtifactStatus.Gone, LogId + ": OSDU no longer holds the record"), (RecordAnswer(again).Outcome, RecordAnswer(again).Note));
        Assert.All(rig.Sent(calls), c => Assert.StartsWith("GET ", c, StringComparison.Ordinal));
        Assert.Equal("abandoned", rig.Ddms.Sessions["sess-1"].State);
    }

    [Fact]
    public async Task A_failing_call_answers_failed_for_its_own_artifact_and_the_log_waits_for_its_sessions()
    {
        using var rig = new Rig();
        rig.Ddms
            .Refuse(HttpMethod.Post, "/sessions/sess-1/data", HttpStatusCode.UnprocessableEntity)
            .Refuse(HttpMethod.Patch, "/sessions/sess-1", HttpStatusCode.InternalServerError, body: "abandon", times: 2)
            .Refuse(HttpMethod.Post, "/sessions/sess-2/data", HttpStatusCode.UnprocessableEntity)
            .Refuse(HttpMethod.Patch, "/sessions/sess-2", HttpStatusCode.InternalServerError, body: "abandon");
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Log(), Chunks(2));
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(work));
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(rig.Work(ledger, Log(), Chunks(2))));
        Assert.Equal(2, ledger.Rows.Count(r => r.Role == ArtifactRoles.Session && r.Status == ArtifactStatus.Pending));

        // The first session's abandon is refused again and the second session is abandoned; the record waits for the first.
        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        var refused = SessionAnswer(results, "sess-1");
        Assert.Equal(ArtifactStatus.Failed, refused.Outcome);
        Assert.Contains("HTTP 500", refused.Note, StringComparison.Ordinal);
        Assert.Equal(ArtifactStatus.Removed, SessionAnswer(results, "sess-2").Outcome);
        Waits(RecordAnswer(results), 1);
        Assert.Equal(("open", "abandoned"), (rig.Ddms.Sessions["sess-1"].State, rig.Ddms.Sessions["sess-2"].State));
        Assert.DoesNotContain(rig.Sent(), c => c.StartsWith("DELETE ", StringComparison.Ordinal));

        // The next undo is given what failed: the session is abandoned now, and the record's delete is refused in its turn.
        ledger.Settle(results);
        rig.Ddms.Refuse(HttpMethod.Delete, "/welllogs/" + LogId, HttpStatusCode.InternalServerError);
        var retry = ledger.Undo(work);
        Assert.Equal(2, retry.Items.Count);
        var second = await rig.Protocol.UndoAsync([retry]);
        AnsweredOnce(second, retry);
        Assert.Equal(ArtifactStatus.Removed, SessionAnswer(second, "sess-1").Outcome);
        var record = RecordAnswer(second);
        Assert.Equal(ArtifactStatus.Failed, record.Outcome);
        Assert.Contains("HTTP 500", record.Note, StringComparison.Ordinal);
        Assert.DoesNotContain(LogId, rig.Platform.Removed);

        // And the one after takes the record.
        ledger.Settle(second);
        var last = ledger.Undo(work);
        var third = await rig.Protocol.UndoAsync([last]);
        AnsweredOnce(third, last);
        Assert.Equal(ArtifactStatus.Removed, RecordAnswer(third).Outcome);
        Assert.Contains(LogId, rig.Platform.Removed);
    }

    [Fact]
    public async Task A_session_whose_state_cannot_be_read_is_failed_for_another_try_and_not_taken_as_gone()
    {
        using var rig = new Rig();
        rig.Ddms
            .Refuse(HttpMethod.Post, "/sessions/sess-1/data", HttpStatusCode.UnprocessableEntity)
            .Refuse(HttpMethod.Patch, "/sessions/sess-1", HttpStatusCode.InternalServerError, body: "abandon");
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Log(), Chunks(2));
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(work));

        // The DDMS could not be asked what state the session is in: it is still open, and an undo that could not reach OSDU
        // is tried again rather than settled.
        rig.Ddms.Refuse(HttpMethod.Get, "/sessions/sess-1", HttpStatusCode.ServiceUnavailable);
        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        Assert.Equal("open", rig.Ddms.Sessions["sess-1"].State);
        var session = SessionAnswer(results, "sess-1");
        Assert.Equal(ArtifactStatus.Failed, session.Outcome);
        Assert.Contains("HTTP 503", session.Note, StringComparison.Ordinal);
        Waits(RecordAnswer(results), 1);
        Assert.DoesNotContain(LogId, rig.Platform.Removed);
    }

    [Fact]
    public async Task Several_records_in_one_undo_are_each_answered_once_and_an_artifact_the_shape_never_makes_is_kept()
    {
        using var rig = new Rig();
        rig.Ddms
            .Refuse(HttpMethod.Post, LogId + "/sessions", HttpStatusCode.InternalServerError)
            .Refuse(HttpMethod.Post, OtherLogId + "/sessions", HttpStatusCode.InternalServerError);
        var first = new UnitLedger(Unit());
        var firstWork = rig.Work(first, Log(), Chunks(2));
        var second = new UnitLedger(Unit());
        var secondWork = rig.Work(second, Log(id: OtherLogId), Chunks(2));
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(firstWork));
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(secondWork));

        var stray = new UndoItem(99, TargetArtifact.Created("content:nmr", ArtifactRoles.Content, "dev:dataset--File.Generic:x"), first.Unit.Id, Began);
        var undoFirst = first.Undo(firstWork);
        undoFirst = undoFirst with { Items = [.. undoFirst.Items, stray] };
        var undoSecond = second.Undo(secondWork);
        var results = await rig.Protocol.UndoAsync([undoFirst, undoSecond]);
        AnsweredOnce(results, undoFirst, undoSecond);
        var kept = Assert.Single(results, r => ReferenceEquals(r.Item, stray));
        Assert.Equal((ArtifactStatus.Kept, "the Wellbore DDMS shape makes nothing of this kind beside a record"), (kept.Outcome, kept.Note));
        Assert.Equal(ArtifactStatus.Removed, RecordAnswer(results, LogId).Outcome);
        Assert.Equal(ArtifactStatus.Removed, RecordAnswer(results, OtherLogId).Outcome);
        Assert.Contains(LogId, rig.Platform.Removed);
        Assert.Contains(OtherLogId, rig.Platform.Removed);
    }

    [Fact]
    public async Task A_record_the_flow_can_no_longer_route_has_every_artifact_failed_naming_why()
    {
        using var rig = new Rig();
        const string unrouted = "dev:work-product-component--Unknown:u-1";
        var unit = Unit();
        var undo = new UndoWork
        {
            Key = Key(unrouted),
            TargetId = unrouted,
            Reason = UndoReason.Held,
            Items =
            [
                new UndoItem(1, TargetArtifact.RecordWritten(unrouted, 7, null), unit.Id, unit.StartedUtc),
                new UndoItem(2, TargetArtifact.Created("session:a", ArtifactRoles.Session, "sess-9", locator: unrouted), unit.Id, unit.StartedUtc),
            ],
        };

        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        Assert.All(results, r =>
        {
            Assert.Equal(ArtifactStatus.Failed, r.Outcome);
            Assert.StartsWith("the record cannot be routed to its DDMS: ", r.Note, StringComparison.Ordinal);
        });
        Assert.Empty(rig.Ddms.Calls);
    }

    [Fact]
    public async Task The_emulated_ledger_reopens_a_slot_the_route_settled_and_never_one_an_undo_settled()
    {
        var ledger = new UnitLedger(Unit());
        var report = ledger.Listen(() => 0);
        var removed = TargetArtifact.Created("session:a", ArtifactRoles.Session, "sess-1", locator: LogId) with { Status = ArtifactStatus.Removed, Note = "abandoned" };
        await report(new StepReport("session", new Dictionary<string, string>(), [removed]), CancellationToken.None);
        await report(new StepReport("session", new Dictionary<string, string>(), [removed with { Status = ArtifactStatus.Pending, Note = null }]), CancellationToken.None);
        Assert.Equal((ArtifactStatus.Pending, (string?)"abandoned"), (ledger.Rows[0].Status, ledger.Rows[0].Note));

        var unit = ledger.Unit;
        ledger.Settle([UndoResult.Removed(new UndoItem(1, ledger.Rows[0], unit.Id, unit.StartedUtc), "session sess-1 abandoned")]);
        await report(new StepReport("session", new Dictionary<string, string>(), [removed with { Status = ArtifactStatus.Pending }]), CancellationToken.None);
        Assert.Equal((ArtifactStatus.Removed, (string?)"session sess-1 abandoned"), (ledger.Rows[0].Status, ledger.Rows[0].Note));
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

    /// <summary>The route over the fake platform, behind the DDMS's sessions, with a clock whose waits end at once.</summary>
    private sealed class Rig : IDisposable
    {
        public Rig(bool platformEndpoint = true)
        {
            Platform = new FakeOsduPlatform();
            Ddms = new SessionDdms(Platform);
            Runtime = new HttpRuntime(
                new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
                new SecretResolver([new EnvSecretProvider()]), new TestClock(), Ddms, allowLoopback: true);

            // A flow whose endpoint is the DDMS itself names no root, and reaches nothing but the DDMS.
            var client = new OsduHttpClient(
                Runtime, platformEndpoint ? FakeOsduPlatform.Endpoint : FakeOsduPlatform.Endpoint + FakeOsduPlatform.DdmsRoot, new TargetAuth { Type = TargetAuthType.None },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
            var options = platformEndpoint ? new ProtocolOptions { DdmsRoot = FakeOsduPlatform.DdmsRoot } : new ProtocolOptions();
            Protocol = new OsduDdmsProtocol(client, options, NullLogger.Instance, time: new InstantClock());
        }

        public FakeOsduPlatform Platform { get; }

        public SessionDdms Ddms { get; }

        public HttpRuntime Runtime { get; }

        public OsduDdmsProtocol Protocol { get; }

        /// <summary>The calls sent from <paramref name="from"/> on, as method and path.</summary>
        public List<string> Sent(int from = 0) => Ddms.Calls.Skip(from).Select(c => c.Method + " " + Uri.UnescapeDataString(c.Uri.AbsolutePath)).ToList();

        /// <summary>One try of <paramref name="ledger"/>'s unit, resuming the steps it completed so far.</summary>
        public DeliveryWork Work(UnitLedger ledger, JsonObject document, IPayloadSource? payload, long? existing = null) => new()
        {
            Key = Key(document["id"]!.GetValue<string>()),
            TargetId = document["id"]!.GetValue<string>(),
            Document = document,
            DeliverMetadata = true,
            DeliverPayload = payload is not null,
            Payload = payload,
            ExistingVersion = existing,
            CompletedSteps = ledger.Steps,
            Unit = ledger.Unit,
            StepCompleted = ledger.Listen(() => Ddms.Calls.Count),
        };

        /// <summary>A log delivered whole before the test's unit (one chunk, straight to the bulk endpoint): the version the ledger holds.</summary>
        public async Task<long> DeliveredAsync()
        {
            var outcome = await Protocol.DeliverAsync(new DeliveryWork
            {
                Key = Key(LogId),
                TargetId = LogId,
                Document = Log(),
                DeliverMetadata = true,
                DeliverPayload = true,
                Payload = Chunks(1),
            });
            Assert.True(outcome.Succeeded, outcome.Failure?.Message);
            return outcome.TargetVersion!.Value;
        }

        public void Dispose()
        {
            Runtime.Dispose();
            Ddms.Dispose();
        }
    }

    /// <summary>
    /// The Wellbore DDMS's bulk sessions in front of the fake platform (osdu/specs/wellbore-ddms/INTEGRATION.md section 3.3):
    /// a session opened, fed chunks, committed (its bulk lands, and the record takes a new version from the one the session
    /// was opened from, naming it) or abandoned, its state read and the record's sessions listed, each answering 404 once the
    /// record is gone, and a session that is not open refusing a change with 409. The committed bulk is described with the
    /// rows and columns its chunks carried. Every other call goes to the platform. A test scripts what fails: a call refused
    /// before the service acts, a call that lands and loses its answer, the run cancelled at a call, a commit left committing
    /// or the session expiring. Every request is kept in order.
    /// </summary>
    private sealed class SessionDdms : DelegatingHandler
    {
        private const string Prefix = FakeOsduPlatform.DdmsRoot + "/ddms/v3/";

        private readonly FakeOsduPlatform _platform;
        private readonly List<Fault> _faults = [];
        private readonly Dictionary<string, (long Rows, List<string> Columns)> _described = new(StringComparer.Ordinal);
        private int _opened;

        public SessionDdms(FakeOsduPlatform platform)
            : base(platform)
        {
            _platform = platform;
        }

        /// <summary>What the DDMS does with the next commit of an open session; the commits after it land.</summary>
        public enum CommitOutcome
        {
            /// <summary>The bulk lands and the session is committed.</summary>
            Lands,

            /// <summary>The session moves to committing and the commit's answer is a gateway timeout; it lands after <see cref="CommittingReads"/> reads of its state.</summary>
            StaysCommitting,

            /// <summary>The session expired before the commit: it is abandoned, and the commit is answered 409.</summary>
            Expires,
        }

        public List<FakeHttpHandler.Request> Calls { get; } = [];

        public Dictionary<string, Session> Sessions { get; } = new(StringComparer.Ordinal);

        public CommitOutcome NextCommit { get; set; } = CommitOutcome.Lands;

        /// <summary>How many reads of a committing session's state answer committing before it lands; <see cref="int.MaxValue"/> for never.</summary>
        public int CommittingReads { get; set; }

        /// <summary>Refuses a call with <paramref name="status"/> before the service acts, after <paramref name="skip"/> such calls went through.</summary>
        public SessionDdms Refuse(HttpMethod method, string path, HttpStatusCode status, string? body = null, int skip = 0, int times = 1)
        {
            _faults.Add(new Fault(method, path, body, skip, times) { Status = status });
            return this;
        }

        /// <summary>Lets a call land and drops its answer, after <paramref name="skip"/> such calls went through.</summary>
        public SessionDdms Lose(HttpMethod method, string path, string? body = null, int skip = 0)
        {
            _faults.Add(new Fault(method, path, body, skip, 1) { Lost = true });
            return this;
        }

        /// <summary>Cancels <paramref name="run"/> as a call goes, after <paramref name="skip"/> such calls went through.</summary>
        public SessionDdms CancelAt(HttpMethod method, string path, CancellationTokenSource run, int skip = 0)
        {
            _faults.Add(new Fault(method, path, null, skip, 1) { Cancel = run });
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

            var fault = _faults.FirstOrDefault(f => f.Take(request.Method, path, body));
            if (fault?.Status is { } status)
            {
                return Json(status, new JsonObject { ["detail"] = "refused by the test" });
            }

            if (fault?.Cancel is { } run)
            {
                await run.CancelAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }

            var response = await SessionedAsync(request.Method, path, request.RequestUri.Query, bytes, body) ?? await base.SendAsync(request, cancellationToken);
            if (fault?.Lost == true)
            {
                response.Dispose();
                throw new HttpRequestException("the connection was reset before the answer came");
            }

            return response;
        }

        /// <summary>A session call, or the description of bulk a session committed; null for anything the platform answers.</summary>
        private async Task<HttpResponseMessage?> SessionedAsync(HttpMethod method, string path, string query, byte[] bytes, string? body)
        {
            if (!path.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return null;
            }

            // {collection}/{record}[/sessions[/{session}[/data]]] or {collection}/{record}/data
            var parts = path[Prefix.Length..].Split('/');
            if (parts is [_, var described, "data"] && method == HttpMethod.Get && query.Contains("describe=true", StringComparison.Ordinal)
                && _described.TryGetValue(described, out var shape))
            {
                return Json(HttpStatusCode.OK, new JsonObject { ["numberOfRows"] = shape.Rows, ["columns"] = new JsonArray(shape.Columns.Select(c => (JsonNode?)c).ToArray()) });
            }

            if (parts.Length < 3 || parts[2] != "sessions")
            {
                return null;
            }

            // Every session call reads the latest record first.
            var record = parts[1];
            if (!_platform.Records.ContainsKey(record) || _platform.Removed.Contains(record))
            {
                return Json(HttpStatusCode.NotFound, new JsonObject { ["detail"] = "record not found" });
            }

            if (parts.Length == 3)
            {
                if (method == HttpMethod.Post)
                {
                    var opened = new Session($"sess-{++_opened}", record, JsonNode.Parse(body!)?["fromVersion"]?.GetValue<long>() ?? 0);
                    Sessions[opened.Id] = opened;
                    return Json(HttpStatusCode.OK, opened.ToJson());
                }

                return Json(HttpStatusCode.OK, new JsonArray(Sessions.Values.Where(s => s.RecordId == record).Select(s => (JsonNode?)s.ToJson()).ToArray()));
            }

            if (!Sessions.TryGetValue(parts[3], out var session) || session.RecordId != record)
            {
                return Json(HttpStatusCode.NotFound, new JsonObject { ["detail"] = $"session {parts[3]} not found." });
            }

            if (parts.Length == 5 && parts[4] == "data" && method == HttpMethod.Post)
            {
                if (session.State != "open")
                {
                    return Json(HttpStatusCode.BadRequest, new JsonObject { ["detail"] = $"Session cannot accept data, state={session.State}" });
                }

                session.Chunks.Add(bytes);
                return Json(HttpStatusCode.OK, new JsonObject { ["rowCount"] = 1, ["columnCount"] = 2, ["columns"] = new JsonArray() });
            }

            if (parts.Length != 4)
            {
                return null;
            }

            if (method == HttpMethod.Get)
            {
                if (session.State == "committing")
                {
                    if (session.CommittingLeft > 0)
                    {
                        session.CommittingLeft--;
                    }
                    else
                    {
                        await LandAsync(session);
                    }
                }

                return Json(HttpStatusCode.OK, session.ToJson());
            }

            if (session.State != "open")
            {
                return Json(HttpStatusCode.Conflict, new JsonObject { ["detail"] = $"SessionInvalidState: the session is {session.State}" });
            }

            if (JsonNode.Parse(body!)?["state"]?.GetValue<string>() == "abandon")
            {
                session.State = "abandoned";
                return Json(HttpStatusCode.OK, session.ToJson());
            }

            var commit = NextCommit;
            NextCommit = CommitOutcome.Lands;
            switch (commit)
            {
                case CommitOutcome.StaysCommitting:
                    session.State = "committing";
                    session.CommittingLeft = CommittingReads;
                    return Json(HttpStatusCode.GatewayTimeout, new JsonObject { ["detail"] = "upstream request timeout" });
                case CommitOutcome.Expires:
                    session.State = "abandoned";
                    return Json(HttpStatusCode.Conflict, new JsonObject { ["detail"] = "SessionInvalidState: the session expired" });
                default:
                    await LandAsync(session);
                    var answer = session.ToJson();
                    answer["version"] = session.Version;
                    return Json(HttpStatusCode.OK, answer);
            }
        }

        /// <summary>The commit landing: the record as the session was opened from it, naming new bulk, written as its next version.</summary>
        private async Task LandAsync(Session session)
        {
            var from = _platform.History.TryGetValue(session.RecordId, out var kept)
                ? kept.LastOrDefault(v => v["version"]!.GetValue<long>() == session.FromVersion)
                : null;
            var linked = (JsonObject)(from ?? _platform.Records[session.RecordId]).DeepClone();
            if (linked["data"] is not JsonObject data)
            {
                data = new JsonObject();
                linked["data"] = data;
            }

            data["ExtensionProperties"] = new JsonObject { ["wdms"] = new JsonObject { ["bulkURI"] = "urn:wdms-1:uuid:" + Guid.NewGuid().ToString("D") } };
            _platform.Bulk[session.RecordId] = session.Chunks.SelectMany(c => c).ToArray();
            session.Version = _platform.Put(linked);
            session.State = "committed";

            long rows = 0;
            var columns = new List<string>();
            foreach (var chunk in session.Chunks)
            {
                using var stream = new MemoryStream(chunk, writable: false);
                var read = await ParquetFiles.ReadShapeAsync(stream);
                rows += read.Rows;
                columns.AddRange(read.ColumnNames.Where(name => !columns.Contains(name)).ToList());
            }

            _described[session.RecordId] = (rows, columns);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, JsonNode body)
            => new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

        /// <summary>One bulk session as the DDMS keeps it.</summary>
        public sealed class Session(string id, string recordId, long fromVersion)
        {
            public string Id { get; } = id;

            public string RecordId { get; } = recordId;

            public long FromVersion { get; } = fromVersion;

            public string State { get; set; } = "open";

            public List<byte[]> Chunks { get; } = [];

            public int CommittingLeft { get; set; }

            public long? Version { get; set; }

            public JsonObject ToJson() => new() { ["id"] = Id, ["recordId"] = RecordId, ["fromVersion"] = FromVersion, ["mode"] = "overwrite", ["state"] = State };
        }

        /// <summary>A scripted failure of the calls of one method whose path ends as given (and whose body names a value, when given).</summary>
        private sealed class Fault(HttpMethod method, string path, string? body, int skip, int times)
        {
            private int _skip = skip;
            private int _left = times;

            public HttpStatusCode? Status { get; init; }

            public bool Lost { get; init; }

            public CancellationTokenSource? Cancel { get; init; }

            /// <summary>Whether the call is one this failure takes: it matches, the calls to let through have gone, and it has not run out.</summary>
            public bool Take(HttpMethod called, string calledPath, string? calledBody)
            {
                if (_left == 0 || called != method || !calledPath.EndsWith(path, StringComparison.Ordinal)
                    || (body is not null && calledBody?.Contains("\"" + body + "\"", StringComparison.Ordinal) != true))
                {
                    return false;
                }

                if (_skip > 0)
                {
                    _skip--;
                    return false;
                }

                _left--;
                return true;
            }
        }
    }

    /// <summary>A clock whose timers fire at once, so a commit's polls do not wait out their interval.</summary>
    private sealed class InstantClock : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero).UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime > TimeSpan.Zero)
            {
                Interlocked.Add(ref _ticks, dueTime.Ticks);
            }

            return new Fired(callback, state, dueTime != Timeout.InfiniteTimeSpan);
        }

        /// <summary>A timer that fired once, on the thread pool, when it was made; changing it fires nothing more.</summary>
        private sealed class Fired : ITimer
        {
            public Fired(TimerCallback callback, object? state, bool fire)
            {
                if (fire)
                {
                    ThreadPool.QueueUserWorkItem(_ => callback(state));
                }
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
