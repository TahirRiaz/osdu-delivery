using System.Text.Json.Nodes;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The undo of the record itself that every route shares (docs/atomic-delivery-plan.md, The record itself), against a route side
/// that keeps the versions OSDU would hold: a record the unit created is removed only when OSDU created it after the unit began,
/// a record that existed is given back the version before the unit's write, an update is given back the version it replaced,
/// an intent whose write never landed writes nothing, newer work keeps the record, and every failure is an answer for its own
/// artifact, never an exception that hides another's.
/// </summary>
public sealed class ArtifactUndoTests
{
    private static readonly DateTime Started = new(2026, 10, 8, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DeliveryKey Key = DeliveryKey.Derive("artifact-undo", ["wellbore-1"]);
    private const string Id = "dev:master-data--Wellbore:wellbore-1";

    /// <summary>What OSDU holds of one record: its versions, newest last, each with its create time; and what the undo did to it.</summary>
    private sealed class Held : IDeliveryProtocol
    {
        public List<long> Versions { get; } = [];

        public DateTime? Created { get; set; }

        public bool Gone { get; set; }

        public List<string> Removals { get; } = [];

        public List<long> WrittenBack { get; } = [];

        public Exception? ReadFails { get; set; }

        public Exception? WriteFails { get; set; }

        public HashSet<long> Purged { get; } = [];

        public DeliveryProtocol Kind => DeliveryProtocol.Storage;

        public RecordSide Side(bool removes = true, bool reads = true, bool restores = true, bool lists = true) => new()
        {
            Remove = removes ? RemoveAsync : null,
            RemoveRefusal = removes ? null : "the DDMS has no reversible removal",
            Read = reads ? ReadStoredAsync : null,
            Restorer = restores ? this : null,
            RestoreRefusal = restores ? null : "the flow's endpoint is the DDMS itself",
            Versions = lists ? (_, _) => Task.FromResult<IReadOnlyList<long>?>(Versions.AsEnumerable().Reverse().ToList()) : null,
        };

        private Task<DeleteOutcome> RemoveAsync(string id, CancellationToken ct)
        {
            Removals.Add(id);
            var was = !Gone;
            Gone = true;
            return Task.FromResult(was ? new DeleteOutcome(true, false, "soft-deleted") : new DeleteOutcome(false, true, "record not found in OSDU"));
        }

        private Task<JsonObject?> ReadStoredAsync(string id, CancellationToken ct)
        {
            if (ReadFails is { } failure)
            {
                throw failure;
            }

            if (Gone || Versions.Count == 0)
            {
                return Task.FromResult<JsonObject?>(null);
            }

            var stored = new JsonObject { ["id"] = id, ["version"] = Versions[^1] };
            if (Created is { } created)
            {
                stored["createTime"] = created.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
            }

            return Task.FromResult<JsonObject?>(stored);
        }

        public Task<JsonObject?> ReadVersionAsync(string targetId, long version, CancellationToken ct = default)
            => Task.FromResult<JsonObject?>(Versions.Contains(version) && !Purged.Contains(version) ? new JsonObject { ["id"] = targetId, ["version"] = version, ["data"] = new JsonObject { ["v"] = version } } : null);

        public Task<IReadOnlyList<RestoreResult>> RestoreBatchAsync(IReadOnlyList<VersionRestore> restores, CancellationToken ct = default)
        {
            if (WriteFails is { } failure)
            {
                return Task.FromResult<IReadOnlyList<RestoreResult>>(restores.Select(r => new RestoreResult(r, null, null, failure)).ToList());
            }

            var results = new List<RestoreResult>();
            foreach (var restore in restores)
            {
                WrittenBack.Add(restore.Version);
                var next = Versions[^1] + 1;
                Versions.Add(next);
                results.Add(new RestoreResult(restore, next, new Dictionary<string, string>(StringComparer.Ordinal) { ["version"] = next.ToString(System.Globalization.CultureInfo.InvariantCulture) }, null));
            }

            return Task.FromResult<IReadOnlyList<RestoreResult>>(results);
        }

        public Task<DeliveryOutcome> DeliverAsync(DeliveryWork work, CancellationToken ct = default) => throw new InvalidOperationException("the undo never delivers");

        public Task<VerifyResult> VerifyAsync(string targetId, long? expectedVersion, CancellationToken ct = default) => throw new InvalidOperationException("the undo never verifies");

        public Task<DeleteOutcome> DeleteAsync(string targetId, RemovalScope scope, IReadOnlyDictionary<string, string>? targetState = null, CancellationToken ct = default)
            => throw new InvalidOperationException("the undo removes through its record side");

        public Task<JsonObject?> ReadAsync(string targetId, CancellationToken ct = default) => throw new InvalidOperationException("the undo reads through its record side");

        public Task<ProbeOutcome> ProbeAsync(CancellationToken ct = default) => throw new InvalidOperationException("the undo never probes");
    }

    private static UndoItem Item(TargetArtifact artifact, long id = 1) => new(id, artifact, Guid.Parse("0191e1f0-0000-7000-8000-000000000001"), Started);

    private static UndoWork Work(bool keepRecord = false, IReadOnlyList<UndoItem>? items = null) => new()
    {
        Key = Key,
        TargetId = Id,
        Reason = UndoReason.Held,
        KeepRecord = keepRecord,
        Items = items ?? [],
    };

    private static TargetArtifact Created(long? version = 101) => TargetArtifact.RecordWritten(Id, version, null);

    private static TargetArtifact Updated(long? version, long prior) => TargetArtifact.RecordWritten(Id, version, prior);

    [Fact]
    public async Task A_record_the_unit_created_after_it_began_is_removed()
    {
        var held = new Held { Created = Started.AddSeconds(3) };
        held.Versions.Add(101);
        var item = Item(Created());

        var result = Assert.Single(await ArtifactUndo.RecordItselfAsync(Work(items: [item]), [item], held.Side(), CancellationToken.None));

        Assert.Equal(ArtifactStatus.Removed, result.Outcome);
        Assert.Equal([Id], held.Removals);
        Assert.Empty(held.WrittenBack);
        Assert.Contains("soft-deleted", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_record_created_within_the_clock_allowance_before_the_unit_began_is_still_the_units()
    {
        var held = new Held { Created = Started - ArtifactLimits.ClockSkew + TimeSpan.FromSeconds(1) };
        held.Versions.Add(101);
        var item = Item(Created());

        var result = Assert.Single(await ArtifactUndo.RecordItselfAsync(Work(items: [item]), [item], held.Side(), CancellationToken.None));

        Assert.Equal(ArtifactStatus.Removed, result.Outcome);
    }

    [Fact]
    public async Task A_record_OSDU_held_before_the_unit_began_is_given_back_the_version_before_the_units_write_and_not_removed()
    {
        var held = new Held { Created = Started.AddDays(-30) };
        held.Versions.AddRange([90, 95, 101]);
        var item = Item(Created(101));

        var result = Assert.Single(await ArtifactUndo.RecordItselfAsync(Work(items: [item]), [item], held.Side(), CancellationToken.None));

        Assert.Equal(ArtifactStatus.Restored, result.Outcome);
        Assert.Empty(held.Removals);
        Assert.Equal([95L], held.WrittenBack);
        Assert.Contains("version 95 written back as version 102", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_record_held_before_the_unit_whose_earlier_version_cannot_be_told_is_kept_as_it_is()
    {
        var held = new Held { Created = Started.AddDays(-30) };
        held.Versions.Add(101);

        // The unit's step recorded no version, so which version came before it cannot be told.
        var unknown = Item(Created(version: null));
        var listed = Item(Created(101), id: 2);
        var results = await ArtifactUndo.RecordItselfAsync(Work(items: [unknown, listed]), [unknown, listed], held.Side(lists: false), CancellationToken.None);

        Assert.All(results, r => Assert.Equal(ArtifactStatus.Kept, r.Outcome));
        Assert.All(results, r => Assert.Contains("before this delivery began", r.Note, StringComparison.Ordinal));
        Assert.Empty(held.Removals);
        Assert.Empty(held.WrittenBack);
    }

    [Fact]
    public async Task A_record_OSDU_no_longer_holds_is_gone()
    {
        var held = new Held { Gone = true };
        var item = Item(Created());

        var result = Assert.Single(await ArtifactUndo.RecordItselfAsync(Work(items: [item]), [item], held.Side(), CancellationToken.None));

        Assert.Equal(ArtifactStatus.Gone, result.Outcome);
        Assert.Empty(held.Removals);
    }

    [Fact]
    public async Task Without_a_storage_read_a_created_record_is_removed_only_when_its_step_recorded_the_version_it_wrote()
    {
        var held = new Held();
        held.Versions.Add(101);
        var known = Item(Created(101));
        var unknown = Item(Created(version: null), id: 2);

        var removed = Assert.Single(await ArtifactUndo.RecordItselfAsync(Work(items: [known]), [known], held.Side(reads: false), CancellationToken.None));
        Assert.Equal(ArtifactStatus.Removed, removed.Outcome);

        held.Gone = false;
        var kept = Assert.Single(await ArtifactUndo.RecordItselfAsync(Work(items: [unknown]), [unknown], held.Side(reads: false), CancellationToken.None));
        Assert.Equal(ArtifactStatus.Kept, kept.Outcome);
        Assert.Contains("does not read storage", kept.Note, StringComparison.Ordinal);
        Assert.Single(held.Removals);
    }

    [Fact]
    public async Task A_route_without_a_reversible_removal_keeps_the_record_and_says_why()
    {
        var held = new Held { Created = Started.AddMinutes(1) };
        held.Versions.Add(101);
        var item = Item(Created());

        var result = Assert.Single(await ArtifactUndo.RecordItselfAsync(Work(items: [item]), [item], held.Side(removes: false), CancellationToken.None));

        Assert.Equal(ArtifactStatus.Kept, result.Outcome);
        Assert.Contains("the DDMS has no reversible removal", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_undo_of_a_removed_record_finds_it_gone()
    {
        var held = new Held { Created = Started.AddMinutes(1) };
        held.Versions.Add(101);
        var item = Item(Created(101));

        await ArtifactUndo.RecordItselfAsync(Work(items: [item]), [item], held.Side(), CancellationToken.None);
        var again = Assert.Single(await ArtifactUndo.RecordItselfAsync(Work(items: [item]), [item], held.Side(), CancellationToken.None));

        Assert.Equal(ArtifactStatus.Gone, again.Outcome);
        Assert.Single(held.Removals);
    }

    [Fact]
    public async Task An_update_is_given_back_the_version_it_replaced_as_the_next_version()
    {
        var held = new Held();
        held.Versions.AddRange([90, 101]);
        var item = Item(Updated(101, prior: 90));

        var result = Assert.Single(await ArtifactUndo.RecordItselfAsync(Work(items: [item]), [item], held.Side(), CancellationToken.None));

        Assert.Equal(ArtifactStatus.Restored, result.Outcome);
        Assert.Equal([90L], held.WrittenBack);
        Assert.Equal([90L, 101L, 102L], held.Versions);
        Assert.Empty(held.Removals);
    }

    [Fact]
    public async Task An_update_whose_write_never_landed_writes_nothing_back()
    {
        var held = new Held();
        held.Versions.AddRange([80, 90]);

        // An intent: the write was about to go, and its answer never came; OSDU's latest is still the version it would replace.
        var item = Item(Updated(version: null, prior: 90) with { Status = ArtifactStatus.Intent });

        var result = Assert.Single(await ArtifactUndo.RecordItselfAsync(Work(items: [item]), [item], held.Side(), CancellationToken.None));

        Assert.Equal(ArtifactStatus.Gone, result.Outcome);
        Assert.Contains("did not land", result.Note, StringComparison.Ordinal);
        Assert.Empty(held.WrittenBack);
        Assert.Equal([80L, 90L], held.Versions);
    }

    [Fact]
    public async Task An_update_whose_write_landed_without_its_answer_is_given_back_the_version_it_replaced()
    {
        var held = new Held();
        held.Versions.AddRange([90, 101]);
        var item = Item(Updated(version: null, prior: 90) with { Status = ArtifactStatus.Intent });

        var result = Assert.Single(await ArtifactUndo.RecordItselfAsync(Work(items: [item]), [item], held.Side(), CancellationToken.None));

        Assert.Equal(ArtifactStatus.Restored, result.Outcome);
        Assert.Equal([90L], held.WrittenBack);
    }

    [Fact]
    public async Task An_update_whose_earlier_version_was_purged_or_is_not_known_is_kept()
    {
        var held = new Held();
        held.Versions.AddRange([90, 101]);
        held.Purged.Add(90);
        var purged = Item(Updated(101, prior: 90));
        var unknown = Item(new TargetArtifact { Slot = TargetArtifact.RecordSlot, Role = ArtifactRoles.Version, TargetId = Id, Version = 101 }, id: 2);

        var results = await ArtifactUndo.RecordItselfAsync(Work(items: [purged, unknown]), [purged, unknown], held.Side(), CancellationToken.None);

        Assert.Equal([ArtifactStatus.Kept, ArtifactStatus.Kept], results.Select(r => r.Outcome));
        Assert.Contains("no longer holds version 90", results[0].Note, StringComparison.Ordinal);
        Assert.Contains("not known", results[1].Note, StringComparison.Ordinal);
        Assert.Empty(held.WrittenBack);
    }

    [Fact]
    public async Task A_route_that_cannot_write_a_version_back_keeps_the_update_and_says_why()
    {
        var held = new Held();
        held.Versions.AddRange([90, 101]);
        var item = Item(Updated(101, prior: 90));

        var result = Assert.Single(await ArtifactUndo.RecordItselfAsync(Work(items: [item]), [item], held.Side(restores: false), CancellationToken.None));

        Assert.Equal(ArtifactStatus.Kept, result.Outcome);
        Assert.Contains("the flow's endpoint is the DDMS itself", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Newer_work_keeps_the_record_itself()
    {
        var held = new Held { Created = Started.AddMinutes(1) };
        held.Versions.AddRange([90, 101]);
        var created = Item(Created(101));
        var updated = Item(Updated(101, prior: 90), id: 2);

        var results = await ArtifactUndo.RecordItselfAsync(Work(keepRecord: true, items: [created, updated]), [created, updated], held.Side(), CancellationToken.None);

        Assert.All(results, r => Assert.Equal(ArtifactStatus.Superseded, r.Outcome));
        Assert.Empty(held.Removals);
        Assert.Empty(held.WrittenBack);
    }

    [Fact]
    public async Task A_failing_read_or_write_is_an_answer_for_its_own_artifact()
    {
        var held = new Held { ReadFails = new HttpRequestException("connection reset by peer"), Created = Started.AddMinutes(1) };
        held.Versions.AddRange([90, 101]);
        var created = Item(Created(101));
        var readFailed = Assert.Single(await ArtifactUndo.RecordItselfAsync(Work(items: [created]), [created], held.Side(), CancellationToken.None));
        Assert.Equal(ArtifactStatus.Failed, readFailed.Outcome);
        Assert.Contains("connection reset", readFailed.Note, StringComparison.Ordinal);

        held.ReadFails = null;
        held.WriteFails = new DeliveryException("HTTP 503 from PUT /api/storage/v2/records");
        var updated = Item(Updated(101, prior: 90), id: 2);
        var writeFailed = Assert.Single(await ArtifactUndo.RecordItselfAsync(Work(items: [updated]), [updated], held.Side(), CancellationToken.None));
        Assert.Equal(ArtifactStatus.Failed, writeFailed.Outcome);
        Assert.Contains("could not be written back", writeFailed.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stopped_run_is_not_answered_for_its_artifacts()
    {
        var held = new Held { ReadFails = new OperationCanceledException() };
        held.Versions.Add(101);
        var item = Item(Created(101));
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ArtifactUndo.RecordItselfAsync(Work(items: [item]), [item], held.Side(), stopping.Token));
    }

    [Fact]
    public void The_create_time_storage_writes_is_read_in_its_forms_and_nothing_else_is_taken_for_one()
    {
        Assert.Equal(new DateTime(2026, 10, 8, 9, 1, 2, 300, DateTimeKind.Utc), ArtifactUndo.CreateTimeOf(new JsonObject { ["createTime"] = "2026-10-08T09:01:02.300Z" }));
        Assert.Equal(new DateTime(2026, 10, 8, 7, 1, 2, DateTimeKind.Utc), ArtifactUndo.CreateTimeOf(new JsonObject { ["createTime"] = "2026-10-08T09:01:02+02:00" }));
        Assert.Null(ArtifactUndo.CreateTimeOf(new JsonObject()));
        Assert.Null(ArtifactUndo.CreateTimeOf(new JsonObject { ["createTime"] = "yesterday" }));
        Assert.Null(ArtifactUndo.CreateTimeOf(new JsonObject { ["createTime"] = 1_760_000_000 }));
    }

    [Fact]
    public async Task Soft_deletes_go_through_storage_and_answer_each_id_once()
    {
        var platform = new FakeOsduPlatform();
        platform.Put(FakeOsduPlatform.Record("dev:dataset--File.Generic:a", "osdu:wks:dataset--File.Generic:1.0.0"));
        platform.Put(FakeOsduPlatform.Record("dev:dataset--File.Generic:b", "osdu:wks:dataset--File.Generic:1.0.0"));
        using var rig = new StorageRig(platform);
        var a = Item(TargetArtifact.Created("dataset:a", ArtifactRoles.Dataset, "dev:dataset--File.Generic:a"));
        var b = Item(TargetArtifact.Created("dataset:b", ArtifactRoles.Dataset, "dev:dataset--File.Generic:b"), id: 2);

        var results = await ArtifactUndo.SoftDeleteAsync(rig.Storage, Key, [(a, a.Artifact.TargetId!), (b, b.Artifact.TargetId!)], CancellationToken.None);

        Assert.Equal([(1L, ArtifactStatus.Removed), (2L, ArtifactStatus.Removed)], results.Select(r => (r.Item.ArtifactId, r.Outcome)));
        Assert.Contains("dev:dataset--File.Generic:a", platform.Removed);
        Assert.Contains("dev:dataset--File.Generic:b", platform.Removed);
        Assert.Empty(await ArtifactUndo.SoftDeleteAsync(rig.Storage, Key, [], CancellationToken.None));
    }

    /// <summary>The storage writer over the fake platform, as a route's undo builds it.</summary>
    private sealed class StorageRig : IDisposable
    {
        private readonly Http.HttpRuntime _runtime;

        public StorageRig(FakeOsduPlatform platform)
        {
            _runtime = new Http.HttpRuntime(
                new Model.FlowReliability { Retry = new Model.FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
                new Core.Secrets.SecretResolver([new Core.Secrets.EnvSecretProvider()]), TimeProvider.System, platform, allowLoopback: true);
            var client = new OsduHttpClient(
                _runtime, FakeOsduPlatform.Endpoint, new Model.TargetAuth { Type = Model.TargetAuthType.None },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
            Storage = new OsduRecordProtocol(client, new Model.ProtocolOptions());
        }

        public OsduRecordProtocol Storage { get; }

        public void Dispose() => _runtime.Dispose();
    }
}
