using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The Well Delivery shape of the ddms route as a unit of work (docs/atomic-delivery-plan.md, osdu/specs/well-delivery-ddms
/// INTEGRATION.md sections 4 and 5), against the fake platform with the DDMS's soft delete of one version in front of it: the
/// write's intent reported with the version the route chose before the write goes (the version of the entity the unit
/// writes, with the version the ledger held before, none for an entity it held no version of, and the id of its Storage copy
/// where the deployment keeps one; nothing for a rewrite in place), reported pending once the DDMS took it, and a resumed try
/// that writes the recorded version again under the same slot. Each failure point is followed by the unit's undo: the
/// version the unit wrote soft-deleted alone, so an earlier version is the latest again and an entity another system wrote
/// keeps its versions; the Storage copy of an entity the ledger held no version of removed when Storage says the unit's write
/// created it (a copy left by an entity the DDMS's store refused included) and kept otherwise; newer work keeping both.
/// </summary>
public sealed class AtomicWellDeliveryTests
{
    private const string WellId = "dev:master-data--Well:welldemo2";
    private const string ForeignId = "osdu:master-data--Well:w9";
    private const string CopyId = "dev:master-data--Well:w9";
    private const string WellKind = "osdu:wks:master-data--Well:1.0.0";
    private const string Entities = FakeOsduPlatform.WellDeliveryRoot + "/storage/v1/";
    private const string Storage = "/api/storage/v2/records/";

    private static readonly DateTime Began = new(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);

    private static DeliveryUnit Unit() => new(Guid.NewGuid(), Began);

    private static DeliveryKey Key(string id) => DeliveryKey.Derive("well-delivery", [id]);

    private static JsonObject Entity(string id = WellId, string name = "Well 2") => FakeOsduPlatform.Record(id, WellKind, new JsonObject
    {
        ["FacilityName"] = name,
        ["ExistenceKind"] = "dev:reference-data--ExistenceKind:Planned:",
    });

    /// <summary>The entries the DDMS keeps for the versions of one well, by version, each with whether it is soft-deleted.</summary>
    private static Dictionary<long, bool> Versions(FakeOsduPlatform platform, string entityId) => platform.WellDeliveryEntities
        .Where(e => e.Key.StartsWith("well|" + entityId + "|", StringComparison.Ordinal))
        .ToDictionary(e => long.Parse(e.Key[(e.Key.LastIndexOf('|') + 1)..], CultureInfo.InvariantCulture), e => e.Value.Deleted);

    private static UndoResult RecordAnswer(IReadOnlyList<UndoResult> results, string id) => Assert.Single(results, r => r.Item.Artifact.TargetId == id);

    /// <summary>Every item of <paramref name="works"/> answered exactly once, each with a state an undo settles in.</summary>
    private static void AnsweredOnce(IReadOnlyList<UndoResult> results, params UndoWork[] works)
    {
        var items = works.SelectMany(w => w.Items).ToList();
        Assert.Equal(items.Count, results.Count);
        Assert.All(items, item => Assert.Single(results, r => ReferenceEquals(r.Item, item)));
        Assert.All(results, r => Assert.True(ArtifactStatuses.IsUndoOutcome(r.Outcome), $"{r.Item.Artifact.Slot} was answered {r.Outcome}"));
    }

    [Fact]
    public async Task A_created_entity_is_an_intent_naming_its_version_and_storage_copy_before_the_write_and_pending_after()
    {
        using var rig = new Rig();
        var ledger = new UnitLedger(Unit());
        var outcome = await rig.Protocol.DeliverAsync(rig.Work(ledger, Entity(ForeignId)));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        var version = outcome.TargetVersion!.Value;
        Assert.Equal(["PUT " + Entities + "well"], rig.Sent());
        Assert.Equal(["version", "metadata"], ledger.Reported.Select(r => r.Report.Step));

        // The version the route chose names the write before it goes: the version of the entity the unit writes, with no
        // version before it, and the id its Storage copy takes in the partition.
        var (chosen, beforeWrite) = ledger.Reported[0];
        Assert.Equal(0, beforeWrite);
        Assert.Equal("new", chosen.Returned["write"]);
        var intent = Assert.Single(chosen.Artifacts);
        Assert.Equal(
            (TargetArtifact.RecordSlot, ArtifactRoles.Objects, (string?)ForeignId, (long?)version, (long?)null, (string?)CopyId, ArtifactStatus.Intent),
            (intent.Slot, intent.Role, intent.TargetId, intent.Version, intent.PriorVersion, intent.Locator, intent.Status));

        var (written, afterWrite) = ledger.Reported[1];
        Assert.Equal(1, afterWrite);
        Assert.Equal(intent with { Status = ArtifactStatus.Pending }, Assert.Single(written.Artifacts));
        Assert.Equal(ArtifactStatus.Pending, Assert.Single(ledger.Rows).Status);
        Assert.True(rig.Platform.Records.ContainsKey(CopyId));
    }

    [Fact]
    public async Task An_update_writing_a_new_version_names_the_version_it_replaced()
    {
        using var rig = new Rig();
        var first = await rig.DeliveredAsync(Entity());
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        var ledger = new UnitLedger(Unit());
        var outcome = await rig.Protocol.DeliverAsync(rig.Work(ledger, Entity(name: "Well 2 renamed"), first.TargetVersion, first.Returned));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.True(outcome.TargetVersion > first.TargetVersion);

        // A new version of an entity the ledger holds is undone by soft-deleting that version alone.
        var intent = Assert.Single(ledger.Reported[0].Report.Artifacts);
        Assert.Equal(
            (TargetArtifact.RecordSlot, ArtifactRoles.Objects, (string?)WellId, outcome.TargetVersion, first.TargetVersion, (string?)WellId, ArtifactStatus.Intent),
            (intent.Slot, intent.Role, intent.TargetId, intent.Version, intent.PriorVersion, intent.Locator, intent.Status));
        Assert.Equal(intent with { Status = ArtifactStatus.Pending }, Assert.Single(ledger.Rows));
    }

    [Fact]
    public async Task Without_a_storage_copy_the_write_names_no_copy()
    {
        using var rig = new Rig(mirror: false);
        var ledger = new UnitLedger(Unit());
        Assert.True((await rig.Protocol.DeliverAsync(rig.Work(ledger, Entity()))).Succeeded);
        var row = Assert.Single(ledger.Rows);
        Assert.Equal((ArtifactRoles.Objects, (string?)null), (row.Role, row.Locator));
        Assert.Empty(rig.Platform.Records);
    }

    [Fact]
    public async Task A_rewrite_in_place_reports_nothing_to_undo()
    {
        using var rig = new Rig();
        var first = await rig.DeliveredAsync(Entity());
        var ledger = new UnitLedger(Unit());
        var outcome = await rig.Protocol.DeliverAsync(rig.Work(ledger, Entity(), first.TargetVersion, first.Returned));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal(first.TargetVersion, outcome.TargetVersion);
        Assert.Equal("in place", ledger.Reported[0].Report.Returned["write"]);
        Assert.All(ledger.Reported, r => Assert.Empty(r.Report.Artifacts));
        Assert.Empty(ledger.Rows);
    }

    [Fact]
    public async Task On_ibm_the_same_content_is_written_under_a_new_version_reported_like_any_other()
    {
        using var rig = new Rig(provider: DdmsProvider.Ibm, cloudant: true);
        var first = await rig.DeliveredAsync(Entity());
        var ledger = new UnitLedger(Unit());
        var outcome = await rig.Protocol.DeliverAsync(rig.Work(ledger, Entity(), first.TargetVersion, first.Returned));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        var row = Assert.Single(ledger.Rows);
        Assert.Equal((ArtifactRoles.Objects, outcome.TargetVersion, first.TargetVersion), (row.Role, row.Version, row.PriorVersion));
    }

    [Fact]
    public async Task A_write_whose_answer_was_lost_after_it_stored_is_settled_by_its_version_and_reported_pending()
    {
        using var rig = new Rig();
        rig.Platform.WellDeliveryFailsAfterWrite = true;
        var ledger = new UnitLedger(Unit());
        var outcome = await rig.Protocol.DeliverAsync(rig.Work(ledger, Entity()));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal(["PUT " + Entities + "well", $"GET {Entities}well/welldemo2/{outcome.TargetVersion}"], rig.Sent());
        Assert.Equal(["version", "metadata"], ledger.Reported.Select(r => r.Report.Step));
        Assert.Equal(ArtifactStatus.Pending, Assert.Single(ledger.Rows).Status);
    }

    [Fact]
    public async Task A_write_refused_before_it_stored_leaves_an_intent_the_undo_finds_gone()
    {
        using var rig = new Rig();
        rig.Ddms.Refuse(HttpMethod.Put, "/storage/v1/well", HttpStatusCode.BadRequest);
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Entity());
        var refused = await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(work));
        Assert.Equal(400, refused.StatusCode);
        Assert.Equal(["version"], ledger.Reported.Select(r => r.Report.Step));
        var intent = Assert.Single(ledger.Rows);
        Assert.Equal(ArtifactStatus.Intent, intent.Status);

        var calls = rig.Ddms.Calls.Count;
        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        var record = RecordAnswer(results, WellId);
        Assert.Equal((ArtifactStatus.Gone, $"the DDMS holds no version {intent.Version} of {WellId}; Storage holds no copy {WellId}"), (record.Outcome, record.Note));
        Assert.Equal([$"DELETE {Entities}well/welldemo2/{intent.Version}", "GET " + Storage + WellId], rig.Sent(calls));
    }

    [Fact]
    public async Task A_storage_copy_left_by_an_entity_the_ddms_store_refused_is_removed_by_the_undo()
    {
        using var rig = new Rig();
        rig.Ddms.StoreRefusesNextWrite = true;
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Entity(ForeignId));
        var refused = await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(work));
        Assert.Equal(500, refused.StatusCode);

        // The copy went into Storage before the DDMS's own store refused the entity; the intent names both.
        Assert.True(rig.Platform.Records.ContainsKey(CopyId));
        Assert.Empty(Versions(rig.Platform, "w9"));
        var intent = Assert.Single(ledger.Rows);
        Assert.Equal((ArtifactStatus.Intent, (string?)CopyId), (intent.Status, intent.Locator));
        Assert.NotNull(intent.Version);

        var calls = rig.Ddms.Calls.Count;
        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        Assert.Equal(
            [$"DELETE {Entities}well/w9/{intent.Version}", "GET " + Storage + CopyId, "POST " + Storage + CopyId + ":delete"],
            rig.Sent(calls));
        Assert.Contains(CopyId, rig.Platform.Removed);

        // The undo removed what the unit left in OSDU, the copy, so the artifact is removed, not gone.
        var record = RecordAnswer(results, ForeignId);
        Assert.EndsWith($", and its Storage copy {CopyId} removed (reversible)", record.Note, StringComparison.Ordinal);
        Assert.Equal(ArtifactStatus.Removed, record.Outcome);
    }

    [Fact]
    public async Task Without_a_storage_copy_an_entity_the_ddms_store_refused_leaves_nothing_behind()
    {
        using var rig = new Rig(mirror: false);
        rig.Ddms.StoreRefusesNextWrite = true;
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Entity());
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(work));
        Assert.Empty(rig.Platform.Records);
        var version = Assert.Single(ledger.Rows).Version;

        var calls = rig.Ddms.Calls.Count;
        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        var record = RecordAnswer(results, WellId);
        Assert.Equal((ArtifactStatus.Gone, $"the DDMS holds no version {version} of {WellId}"), (record.Outcome, record.Note));
        Assert.Equal([$"DELETE {Entities}well/welldemo2/{version}"], rig.Sent(calls));
    }

    [Fact]
    public async Task The_undo_of_a_created_entity_soft_deletes_the_version_it_wrote_and_removes_the_copy_storage_says_it_created()
    {
        using var rig = new Rig();
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Entity());
        var outcome = await rig.Protocol.DeliverAsync(work);
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);

        // The delivery's one step landed and its unit was abandoned before it committed.
        var calls = rig.Ddms.Calls.Count;
        var undo = ledger.Undo(work, UndoReason.Abandoned);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        var record = RecordAnswer(results, WellId);
        Assert.Equal(ArtifactStatus.Removed, record.Outcome);
        Assert.Equal(
            $"version {outcome.TargetVersion} of {WellId} soft-deleted, the version the unit wrote, the only one the ledger knows of (a write of the same version restores it), and its Storage copy {WellId} removed (reversible)",
            record.Note);
        Assert.Equal([$"DELETE {Entities}well/welldemo2/{outcome.TargetVersion}", "GET " + Storage + WellId, "POST " + Storage + WellId + ":delete"], rig.Sent(calls));
        Assert.Equal(new Dictionary<long, bool> { [outcome.TargetVersion!.Value] = true }, Versions(rig.Platform, "welldemo2"));
        Assert.Contains(WellId, rig.Platform.Removed);
    }

    [Fact]
    public async Task The_undo_of_a_new_version_soft_deletes_that_version_alone_so_the_one_it_replaced_is_the_latest_again()
    {
        using var rig = new Rig();
        var first = await rig.DeliveredAsync(Entity());
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Entity(name: "Well 2 renamed"), first.TargetVersion, first.Returned);
        var outcome = await rig.Protocol.DeliverAsync(work);
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);

        var calls = rig.Ddms.Calls.Count;
        var undo = ledger.Undo(work, UndoReason.Abandoned);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        var version = RecordAnswer(results, WellId);
        Assert.Equal(ArtifactStatus.Removed, version.Outcome);
        Assert.Equal(
            $"version {outcome.TargetVersion} of {WellId} soft-deleted, so version {first.TargetVersion} is the latest again (a write of the same version restores it); its Storage copy {WellId} keeps the version this write gave it",
            version.Note);
        Assert.Equal([$"DELETE {Entities}well/welldemo2/{outcome.TargetVersion}"], rig.Sent(calls));
        Assert.Equal(new Dictionary<long, bool> { [first.TargetVersion!.Value] = false, [outcome.TargetVersion!.Value] = true }, Versions(rig.Platform, "welldemo2"));
        Assert.Equal(first.TargetVersion, (await rig.Protocol.ReadAsync(WellId))!["version"]!.GetValue<long>());
        Assert.DoesNotContain(WellId, rig.Platform.Removed);
    }

    [Fact]
    public async Task Without_a_storage_copy_the_undo_of_a_new_version_names_no_copy()
    {
        using var rig = new Rig(mirror: false);
        var first = await rig.DeliveredAsync(Entity());
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Entity(name: "Well 2 renamed"), first.TargetVersion, first.Returned);
        Assert.True((await rig.Protocol.DeliverAsync(work)).Succeeded);
        Assert.Empty(rig.Platform.Records);

        // The deployment keeps no copy in Storage, so what the undo says of the record must not name one.
        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        var version = RecordAnswer(results, WellId);
        Assert.Equal(ArtifactStatus.Removed, version.Outcome);
        Assert.DoesNotContain("Storage copy", version.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Newer_work_keeps_a_created_entity_and_a_new_version_as_they_are()
    {
        using var rig = new Rig();
        var created = new UnitLedger(Unit());
        var createdWork = rig.Work(created, Entity(ForeignId));
        Assert.True((await rig.Protocol.DeliverAsync(createdWork)).Succeeded);
        var first = await rig.DeliveredAsync(Entity());
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        var updated = new UnitLedger(Unit());
        var updatedWork = rig.Work(updated, Entity(name: "Well 2 renamed"), first.TargetVersion, first.Returned);
        Assert.True((await rig.Protocol.DeliverAsync(updatedWork)).Succeeded);

        var calls = rig.Ddms.Calls.Count;
        var undoCreated = created.Undo(createdWork, UndoReason.Abandoned, keepRecord: true);
        var undoUpdated = updated.Undo(updatedWork, UndoReason.Abandoned, keepRecord: true);
        var results = await rig.Protocol.UndoAsync([undoCreated, undoUpdated]);
        AnsweredOnce(results, undoCreated, undoUpdated);
        Assert.All(results, r => Assert.Equal((ArtifactStatus.Superseded, "the record's newer work writes the entity again, so its version is left as it is"), (r.Outcome, r.Note)));
        Assert.Empty(rig.Sent(calls));
        Assert.All(rig.Platform.WellDeliveryEntities.Values, e => Assert.False(e.Deleted));
        Assert.Empty(rig.Platform.Removed);
    }

    [Fact]
    public async Task A_second_undo_finds_the_versions_and_the_copy_gone_and_changes_nothing()
    {
        using var rig = new Rig();
        var created = new UnitLedger(Unit());
        var createdWork = rig.Work(created, Entity(ForeignId));
        var made = await rig.Protocol.DeliverAsync(createdWork);
        var first = await rig.DeliveredAsync(Entity());
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        var updated = new UnitLedger(Unit());
        var updatedWork = rig.Work(updated, Entity(name: "Well 2 renamed"), first.TargetVersion, first.Returned);
        var written = await rig.Protocol.DeliverAsync(updatedWork);
        var undoCreated = created.Undo(createdWork);
        var undoUpdated = updated.Undo(updatedWork);
        Assert.All(await rig.Protocol.UndoAsync([undoCreated, undoUpdated]), r => Assert.Equal(ArtifactStatus.Removed, r.Outcome));
        var held = rig.Platform.WellDeliveryEntities.ToDictionary(e => e.Key, e => e.Value.Deleted, StringComparer.Ordinal);
        var removed = rig.Platform.Removed.Order(StringComparer.Ordinal).ToList();

        // The same artifacts again, as when the first undo's answer never reached the ledger.
        var again = await rig.Protocol.UndoAsync([undoCreated, undoUpdated]);
        AnsweredOnce(again, undoCreated, undoUpdated);
        var entity = RecordAnswer(again, ForeignId);
        Assert.Equal((ArtifactStatus.Gone, $"the DDMS holds no version {made.TargetVersion} of {ForeignId}; Storage holds no copy {CopyId}"), (entity.Outcome, entity.Note));
        var version = RecordAnswer(again, WellId);
        Assert.Equal(ArtifactStatus.Gone, version.Outcome);
        Assert.StartsWith($"the DDMS holds no version {written.TargetVersion} of {WellId}", version.Note, StringComparison.Ordinal);
        Assert.Equal(held, rig.Platform.WellDeliveryEntities.ToDictionary(e => e.Key, e => e.Value.Deleted, StringComparer.Ordinal));
        Assert.Equal(removed, rig.Platform.Removed.Order(StringComparer.Ordinal));
        Assert.False(Versions(rig.Platform, "welldemo2")[first.TargetVersion!.Value]);
    }

    [Fact]
    public async Task A_failing_delete_answers_failed_for_its_own_artifact_alone()
    {
        using var rig = new Rig();
        var created = new UnitLedger(Unit());
        var createdWork = rig.Work(created, Entity(ForeignId));
        Assert.True((await rig.Protocol.DeliverAsync(createdWork)).Succeeded);
        var first = await rig.DeliveredAsync(Entity());
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        var updated = new UnitLedger(Unit());
        var updatedWork = rig.Work(updated, Entity(name: "Well 2 renamed"), first.TargetVersion, first.Returned);
        var written = await rig.Protocol.DeliverAsync(updatedWork);

        rig.Ddms.Refuse(HttpMethod.Delete, $"/well/welldemo2/{written.TargetVersion}", HttpStatusCode.InternalServerError);
        var undoCreated = created.Undo(createdWork);
        var undoUpdated = updated.Undo(updatedWork);
        var results = await rig.Protocol.UndoAsync([undoCreated, undoUpdated]);
        AnsweredOnce(results, undoCreated, undoUpdated);
        var version = RecordAnswer(results, WellId);
        Assert.Equal(ArtifactStatus.Failed, version.Outcome);
        Assert.Contains("HTTP 500", version.Note, StringComparison.Ordinal);
        Assert.False(Versions(rig.Platform, "welldemo2")[written.TargetVersion!.Value]);
        Assert.Equal(ArtifactStatus.Removed, RecordAnswer(results, ForeignId).Outcome);
        Assert.All(Versions(rig.Platform, "w9").Values, Assert.True);
    }

    [Fact]
    public async Task An_entity_another_system_wrote_before_the_unit_began_keeps_its_versions_and_its_copy()
    {
        using var rig = new Rig();

        // The DDMS held the well, and Storage its copy, before this flow first delivered it, so the ledger holds no version.
        const long earlier = 1_600_000_000_000;
        var theirs = Entity(name: "Well 2, as another system wrote it");
        theirs["version"] = earlier;
        rig.Platform.WellDeliveryEntities[$"well|welldemo2|{earlier}"] = (theirs, false);
        var ledger = new UnitLedger(Unit());
        var work = rig.Work(ledger, Entity());
        var outcome = await rig.Protocol.DeliverAsync(work);
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Null(Assert.Single(ledger.Rows).PriorVersion);
        rig.Platform.Records[WellId]["createTime"] = Began.AddDays(-30).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        // The undo takes back the version the unit wrote, and only that: the other system's version and copy stay.
        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        var record = RecordAnswer(results, WellId);
        Assert.Equal(ArtifactStatus.Removed, record.Outcome);
        Assert.Contains($"its Storage copy {WellId}, created at ", record.Note, StringComparison.Ordinal);
        Assert.EndsWith(" before this delivery began, keeps the version this write gave it", record.Note, StringComparison.Ordinal);
        var versions = Versions(rig.Platform, "welldemo2");
        Assert.True(versions[outcome.TargetVersion!.Value]);
        Assert.False(versions[earlier]);
        Assert.DoesNotContain(WellId, rig.Platform.Removed);
    }

    [Fact]
    public async Task Under_a_ddms_endpoint_versions_are_undone_and_a_copy_storage_cannot_be_asked_about_is_kept_saying_so()
    {
        using var rig = new Rig(platformEndpoint: false);
        var created = new UnitLedger(Unit());
        var createdWork = rig.Work(created, Entity(ForeignId));
        var made = await rig.Protocol.DeliverAsync(createdWork);
        Assert.True(made.Succeeded, made.Failure?.Message);
        var first = await rig.DeliveredAsync(Entity());
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        var updated = new UnitLedger(Unit());
        var updatedWork = rig.Work(updated, Entity(name: "Well 2 renamed"), first.TargetVersion, first.Returned);
        var written = await rig.Protocol.DeliverAsync(updatedWork);
        Assert.True(written.Succeeded, written.Failure?.Message);

        var calls = rig.Ddms.Calls.Count;
        var undoCreated = created.Undo(createdWork);
        var undoUpdated = updated.Undo(updatedWork);
        var results = await rig.Protocol.UndoAsync([undoCreated, undoUpdated]);
        AnsweredOnce(results, undoCreated, undoUpdated);

        // The DDMS's versions are its own to delete; the copy is in Storage, which the flow does not reach.
        var entity = RecordAnswer(results, ForeignId);
        Assert.Equal(ArtifactStatus.Removed, entity.Outcome);
        Assert.EndsWith($"; its Storage copy {CopyId} keeps the version this write gave it", entity.Note, StringComparison.Ordinal);
        Assert.Equal(ArtifactStatus.Removed, RecordAnswer(results, WellId).Outcome);
        Assert.Equal([$"DELETE {Entities}well/w9/{made.TargetVersion}", $"DELETE {Entities}well/welldemo2/{written.TargetVersion}"], rig.Sent(calls));
        Assert.DoesNotContain(CopyId, rig.Platform.Removed);
    }

    [Fact]
    public async Task A_resumed_try_writes_the_recorded_version_again_and_reports_it_under_the_same_slot()
    {
        using var rig = new Rig();
        rig.Ddms.Refuse(HttpMethod.Put, "/storage/v1/well", HttpStatusCode.ServiceUnavailable);
        var ledger = new UnitLedger(Unit());
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(rig.Work(ledger, Entity(ForeignId))));
        var chosen = Assert.Single(ledger.Rows).Version;
        Assert.Equal(ArtifactStatus.Intent, ledger.Rows[0].Status);

        var calls = rig.Ddms.Calls.Count;
        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        var resumed = await rig.Protocol.DeliverAsync(rig.Work(ledger, Entity(ForeignId)));
        Assert.True(resumed.Succeeded, resumed.Failure?.Message);
        Assert.Equal(chosen, resumed.TargetVersion);
        Assert.Equal(["PUT " + Entities + "well"], rig.Sent(calls));
        Assert.Equal(chosen, JsonNode.Parse(rig.Ddms.Calls[calls].Body!)!["version"]!.GetValue<long>());

        // The resumed try reports the write alone, and the one row of the slot is now pending.
        Assert.Equal(["version", "metadata"], ledger.Reported.Select(r => r.Report.Step));
        var row = Assert.Single(ledger.Rows);
        Assert.Equal((ArtifactRoles.Objects, chosen, ArtifactStatus.Pending), (row.Role, row.Version, row.Status));
    }

    [Fact]
    public async Task An_artifact_the_shape_never_makes_is_kept()
    {
        using var rig = new Rig();
        var unit = Unit();
        var undo = new UndoWork
        {
            Key = Key(WellId),
            TargetId = WellId,
            Reason = UndoReason.Held,
            Items =
            [
                new UndoItem(1, TargetArtifact.Created("session:a", ArtifactRoles.Session, "sess-1", locator: WellId), unit.Id, unit.StartedUtc),
                new UndoItem(2, TargetArtifact.Created("objects:a", ArtifactRoles.Objects, WellId), unit.Id, unit.StartedUtc),
            ],
        };

        var results = await rig.Protocol.UndoAsync([undo]);
        AnsweredOnce(results, undo);
        Assert.All(results, r => Assert.Equal((ArtifactStatus.Kept, "the Well Delivery shape makes nothing of this kind beside a record"), (r.Outcome, r.Note)));
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

    /// <summary>
    /// The route to a Well Delivery DDMS on the fake platform, under a test clock that chooses its versions. A flow whose
    /// endpoint is the DDMS itself gives the DDMS no root and reaches nothing else.
    /// </summary>
    private sealed class Rig : IDisposable
    {
        public Rig(bool mirror = true, DdmsProvider? provider = null, bool cloudant = false, bool platformEndpoint = true)
        {
            Platform = new FakeOsduPlatform { WellDeliveryMirror = mirror, WellDeliveryCloudant = cloudant };
            Ddms = new VersionedDdms(Platform);
            Clock = new TestClock();
            Runtime = new HttpRuntime(
                new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
                new SecretResolver([new EnvSecretProvider()]), new TestClock(), Ddms, allowLoopback: true);
            var endpoint = platformEndpoint ? FakeOsduPlatform.Endpoint : FakeOsduPlatform.Endpoint + FakeOsduPlatform.WellDeliveryRoot;
            var client = new OsduHttpClient(
                Runtime, endpoint, new TargetAuth { Type = TargetAuthType.None },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
            var service = new DdmsService("welldelivery", platformEndpoint ? FakeOsduPlatform.WellDeliveryRoot : null, DdmsShape.WellDeliveryV1, DdmsCatalog.WellDeliveryCollections)
            {
                WellDelivery = new WellDeliverySettings { Mirror = mirror, Provider = provider },
            };
            var target = new FlowTarget { Endpoint = endpoint, Protocol = DeliveryProtocol.Ddms, Ddms = [service] };
            var flow = platformEndpoint ? Samples.Targeting(target, "wells") : Samples.Targeting(target);
            Protocol = new OsduDdmsProtocol(client, new ProtocolOptions(), NullLogger.Instance, time: Clock, routing: DdmsRouting.Of(flow));
        }

        public FakeOsduPlatform Platform { get; }

        public VersionedDdms Ddms { get; }

        public TestClock Clock { get; }

        public HttpRuntime Runtime { get; }

        public OsduDdmsProtocol Protocol { get; }

        /// <summary>The calls sent from <paramref name="from"/> on, as method and path.</summary>
        public List<string> Sent(int from = 0) => Ddms.Calls.Skip(from).Select(c => c.Method + " " + Uri.UnescapeDataString(c.Uri.AbsolutePath)).ToList();

        /// <summary>One try of <paramref name="ledger"/>'s unit, resuming the steps it completed so far.</summary>
        public DeliveryWork Work(UnitLedger ledger, JsonObject document, long? existing = null, IReadOnlyDictionary<string, string>? state = null) => new()
        {
            Key = Key(document["id"]!.GetValue<string>()),
            TargetId = document["id"]!.GetValue<string>(),
            Document = document,
            DeliverMetadata = true,
            DeliverPayload = false,
            ExistingVersion = existing,
            TargetState = state ?? new Dictionary<string, string>(StringComparer.Ordinal),
            CompletedSteps = ledger.Steps,
            Unit = ledger.Unit,
            StepCompleted = ledger.Listen(() => Ddms.Calls.Count),
        };

        /// <summary>An entity delivered before the test's unit: what the ledger then holds of it.</summary>
        public async Task<DeliveryOutcome> DeliveredAsync(JsonObject document)
        {
            var outcome = await Protocol.DeliverAsync(new DeliveryWork
            {
                Key = Key(document["id"]!.GetValue<string>()),
                TargetId = document["id"]!.GetValue<string>(),
                Document = document,
                DeliverMetadata = true,
                DeliverPayload = false,
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
    /// The Well Delivery DDMS in front of the fake platform, with what the platform's fake leaves out: the soft delete of one
    /// version (osdu/specs/well-delivery-ddms/INTEGRATION.md section 5: <c>DELETE /storage/v1/{type}/{id}/{version}</c> with the
    /// JSON content type, 204, or 404 for a version it holds no live entry of), and the failures a test scripts: a call
    /// refused before the service acts, and a write whose copy landed in Storage while the DDMS's own store refused the entity
    /// (the DDMS copies an entity into Storage before its store takes it). Every request is kept in order.
    /// </summary>
    private sealed class VersionedDdms : DelegatingHandler
    {
        private const string Prefix = FakeOsduPlatform.WellDeliveryRoot + "/storage/v1/";

        private readonly FakeOsduPlatform _platform;
        private readonly List<(HttpMethod Method, string Path, HttpStatusCode Status)> _refusals = [];

        public VersionedDdms(FakeOsduPlatform platform)
            : base(platform)
        {
            _platform = platform;
        }

        public List<FakeHttpHandler.Request> Calls { get; } = [];

        /// <summary>The next write's copy lands in Storage, and the DDMS's own store refuses the entity with a server error.</summary>
        public bool StoreRefusesNextWrite { get; set; }

        /// <summary>Refuses the next call of <paramref name="method"/> whose path ends with <paramref name="path"/>, before the service acts.</summary>
        public VersionedDdms Refuse(HttpMethod method, string path, HttpStatusCode status)
        {
            _refusals.Add((method, path, status));
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var media = request.Content?.Headers.ContentType?.MediaType;
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            Calls.Add(new FakeHttpHandler.Request(
                request.Method, request.RequestUri, body, media, request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase)));

            var refusal = _refusals.FindIndex(r => r.Method == request.Method && path.EndsWith(r.Path, StringComparison.Ordinal));
            if (refusal >= 0)
            {
                var status = _refusals[refusal].Status;
                _refusals.RemoveAt(refusal);
                return Json(status, new JsonObject { ["message"] = "refused by the test" });
            }

            if (request.Method == HttpMethod.Delete && path.StartsWith(Prefix, StringComparison.Ordinal)
                && path[Prefix.Length..].Split('/') is [var type, var entityId, var version] && !version.EndsWith(":purge", StringComparison.Ordinal))
            {
                return media == "application/json" ? DeleteVersion(type, entityId, version) : new HttpResponseMessage(HttpStatusCode.UnsupportedMediaType);
            }

            if (request.Method == HttpMethod.Put && StoreRefusesNextWrite && path.StartsWith(Prefix, StringComparison.Ordinal))
            {
                StoreRefusesNextWrite = false;
                return await RefusedAfterCopyAsync(request, body!, cancellationToken);
            }

            return await base.SendAsync(request, cancellationToken);
        }

        private HttpResponseMessage DeleteVersion(string type, string entityId, string version)
        {
            var key = $"{type.ToLowerInvariant()}|{entityId}|{version}";
            if (!_platform.WellDeliveryEntities.TryGetValue(key, out var held) || held.Deleted)
            {
                return Json(HttpStatusCode.NotFound, new JsonObject { ["message"] = $"Could not find entity version with id: {entityId}_{version}" });
            }

            _platform.WellDeliveryEntities[key] = (held.Entity, true);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        /// <summary>The write goes through to the platform, which stores the entity and its copy; then the store's entry is taken back.</summary>
        private async Task<HttpResponseMessage> RefusedAfterCopyAsync(HttpRequestMessage request, string body, CancellationToken cancellationToken)
        {
            var entity = JsonNode.Parse(body)!.AsObject();
            var segments = entity["id"]!.GetValue<string>().Split(':');
            var type = segments[1][(segments[1].IndexOf("--", StringComparison.Ordinal) + 2)..].ToLowerInvariant();
            var key = $"{type}|{string.Join(':', segments.Skip(2))}|{entity["version"]!.GetValue<long>().ToString(CultureInfo.InvariantCulture)}";
            (JsonObject Entity, bool Deleted)? before = _platform.WellDeliveryEntities.TryGetValue(key, out var was) ? was : null;
            (await base.SendAsync(request, cancellationToken)).Dispose();
            if (before is { } kept)
            {
                _platform.WellDeliveryEntities[key] = kept;
            }
            else
            {
                _platform.WellDeliveryEntities.Remove(key);
                _platform.WellDeliveryIndex.Remove(key);
            }

            return Json(HttpStatusCode.InternalServerError, new JsonObject { ["message"] = "An unknown error has occurred." });
        }

        private static HttpResponseMessage Json(HttpStatusCode status, JsonNode body)
            => new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    }
}
