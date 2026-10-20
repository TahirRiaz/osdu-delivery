using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Snapshots;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Deliveries keep a type current, on SQL Server (osdu/docs/reference/concepts/partition-cache.md): what a delivery adds
/// stays beside what it added before and survives a cache flow's capture that does not find it, until a refresh finds OSDU's
/// storage no longer holds it; the ledger names what a run landed; and a delivery run's records, read back from storage,
/// reach the cache types its kind keeps current, held by its ledger, and only those.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class CacheKeepCurrentStoreTests : IDisposable
{
    private const string Scope = "dev";
    private const string LogKind = "osdu:wks:work-product-component--WellLog:1.4.0";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task What_a_delivery_adds_stays_until_a_refresh_finds_storage_no_longer_holds_it()
    {
        var store = _db.Caches();
        var capture = new CacheCapture(null, "tests", "tests");
        const string Delivery = "delivery:welldb-unit-delivery";

        await store.MergeAsync(Scope, "reference-cache", [Units(("m", "metre"), ("ft", "foot"))], capture, T(0));
        await store.MergeAsync(Scope, Delivery, [Units(("s", "second"))], capture with { Adds = true }, T(1));
        var added = await store.MergeAsync(Scope, Delivery, [Units(("kg", "kilogram"))], capture with { Adds = true }, T(2));

        // A delivery's later records go beside its earlier ones; it holds both.
        Assert.Equal(["ft", "kg", "m", "s"], Ids(added.Snapshot));
        Assert.Equal(["kg", "s"], await HeldAsync(Delivery));
        Assert.Equal(["kg", "s"], (await store.HeldByDeliveriesAsync(Scope, "UnitOfMeasure", ["ft", "kg", "m", "s", "x"])).Order(StringComparer.Ordinal));

        // A cache flow's capture that finds only the metre takes out the foot, which only it held, and keeps what the delivery holds.
        var refreshed = await store.MergeAsync(Scope, "reference-cache", [Units(("m", "metre"))], capture, T(3));
        Assert.Equal(["kg", "m", "s"], Ids(refreshed.Snapshot));

        // Told storage no longer holds the second, the next capture takes it out; the kilogram, which storage holds, stays.
        var gone = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase) { ["UnitOfMeasure"] = new HashSet<string> { "s" } };
        var cleaned = await store.MergeAsync(Scope, "reference-cache", [Units(("m", "metre"))], capture with { Gone = gone }, T(4));
        Assert.Equal(["kg", "m"], Ids(cleaned.Snapshot));
        Assert.Equal(["kg"], await HeldAsync(Delivery));
    }

    [Fact]
    public async Task A_refresh_asks_storage_only_about_what_deliveries_hold_and_it_did_not_find()
    {
        var store = _db.Caches();
        var capture = new CacheCapture(null, "tests", "tests");
        await store.MergeAsync(Scope, "reference-cache", [Units(("m", "metre"), ("ft", "foot"))], capture, T(0));
        await store.MergeAsync(Scope, "delivery:welldb-unit-delivery", [Units(("kg", "kilogram"), ("s", "second"))], capture with { Adds = true }, T(1));

        using var platform = new FakeOsduPlatform();
        platform.Put(FakeOsduPlatform.Record("kg", "osdu:wks:reference-data--UnitOfMeasure:1.0.0"));
        using var rig = new StorageRig(platform);

        // The capture found the metre; the foot was only the cache flow's, so storage is not asked about it.
        var gone = await CacheRefresher.GoneAsync(store, Scope, rig.Storage, [Units(("m", "metre"))], NullLogger.Instance, CancellationToken.None);

        Assert.Equal(["s"], Assert.Single(gone).Value.Order(StringComparer.Ordinal));
        var asked = platform.Calls.Where(r => r.Uri.AbsolutePath.EndsWith("/query/records", StringComparison.Ordinal)).ToList();
        var body = JsonNode.Parse(Assert.Single(asked).Body!)!["records"]!.AsArray().Select(n => n!.GetValue<string>()).Order(StringComparer.Ordinal);
        Assert.Equal(["kg", "s"], body);
    }

    [Fact]
    public async Task A_delivery_run_s_records_are_read_back_into_the_types_its_kind_keeps_current_and_only_those()
    {
        var tables = await SampleEstate.BuildAsync(_root, _clock.GetUtcNow().UtcDateTime.AddMinutes(-5), time: _clock);
        var ledger = _db.Ledger(_clock);
        var protocol = new FakeProtocol();
        // The run renders against the sample cache, as every engine suite does; the step writes into the partition's cache on SQL Server.
        var engine = Samples.Engine(ledger, _clock, sources: tables) with { Protocols = new FakeProtocolFactory(protocol) };
        using var runtime = await FlowRuntime.CreateAsync(engine, Samples.LocalFlow(_root), SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);

        await _db.DeclareCacheAsync(Scope, "welldb-logs-cache", new ReferenceTypeSpec
        {
            Name = "WellLog",
            EntityType = "work-product-component--WellLog",
            Kind = "osdu:wks:work-product-component--WellLog:*",
            Fields = [new ReferenceFieldSpec("data.Name")],
            KeepCurrent = true,
        });
        await _db.DeclareCacheAsync(Scope, "welldb-wellbore-cache", new ReferenceTypeSpec
        {
            Name = "Wellbore",
            EntityType = "master-data--Wellbore",
            Kind = "osdu:wks:master-data--Wellbore:*",
            Fields = [new ReferenceFieldSpec("data.FacilityName")],
            KeepCurrent = true,
        });

        var started = _clock.GetUtcNow().UtcDateTime;
        _clock.Advance(TimeSpan.FromMinutes(1));
        var intake = await runtime.Intake.IntakeAsync(runtime.Flow, runtime.Mapping, runtime.Parameters, runtime.Request, force: false);
        var worker = new DeliveryWorker(ledger, engine.Payloads, engine.Stores, protocol, runtime.Flow, _clock, CompositeDeliveryListener.Empty, Samples.Logger<DeliveryWorker>(), "test-worker") { MaxWait = null };
        var work = await worker.DrainAsync(intake.Submission.SubmissionId);
        await runtime.Intake.CompleteAsync(intake.Submission.SubmissionId, runtime.Flow.Id);
        Assert.True(work.Delivered > 1);

        // The ledger names what the run landed, and nothing landed before it.
        var landed = await ledger.LandedSinceAsync(runtime.Flow.Id, started, 1_000);
        Assert.Equal(work.Delivered, landed.Count);
        Assert.Empty(await ledger.LandedSinceAsync(runtime.Flow.Id, _clock.GetUtcNow().UtcDateTime.AddMinutes(1), 1_000));

        // OSDU holds every record but one, which a cleanup took meanwhile.
        using var platform = new FakeOsduPlatform();
        foreach (var id in landed.Skip(1))
        {
            platform.Put(FakeOsduPlatform.Record(id, LogKind, new JsonObject { ["Name"] = $"log {id}" }));
        }

        using var rig = new StorageRig(platform);
        var keeper = new CacheKeeper(engine with { Cache = _db.Caches() }, NullLogger.Instance);
        var kept = await keeper.KeepCurrentAsync(
            Scope, LogKind, runtime.Flow.Id, runtime.Flow.LedgerName, started, _ => Task.FromResult(rig.Client), OsduRecordProtocol.DefaultVerifyBatchPath,
            Guid.NewGuid(), "tests", CancellationToken.None);

        Assert.NotNull(kept);
        Assert.Equal(["WellLog"], kept.Types);
        Assert.Equal((landed.Count, landed.Count - 1, 1, true), (kept.Landed, kept.Read, kept.Missing, kept.Written));
        var cached = await _db.Caches().LoadAsync(Scope, kept.Version!);
        var logs = cached!.Type("WellLog")!;
        Assert.Equal(landed.Count - 1, logs.Items.Count);
        Assert.All(logs.Items, item => Assert.Equal($"log {item.Id}", item.Fields["Name"].Text));
        Assert.Null(cached.Type("Wellbore"));
        Assert.Equal(landed.Count - 1, (await HeldAsync(CacheHolders.Delivery(runtime.Flow.LedgerName), "WellLog")).Count);

        // A kind no type keeps current keeps nothing current, and does not read storage.
        Assert.Null(await keeper.KeepCurrentAsync(
            Scope, "osdu:wks:master-data--Well:1.0.0", runtime.Flow.Id, runtime.Flow.LedgerName, started, _ => throw new InvalidOperationException("storage is not read"),
            OsduRecordProtocol.DefaultVerifyBatchPath, null, "tests", CancellationToken.None));
    }

    [Fact]
    public async Task A_deliver_run_ends_with_the_cache_step_and_a_step_that_cannot_reach_storage_does_not_fail_it()
    {
        var tables = await SampleEstate.BuildAsync(_root, _clock.GetUtcNow().UtcDateTime.AddMinutes(-5), time: _clock);
        var ledger = _db.Ledger(_clock);
        var caches = _db.Caches();
        await Samples.ImportSampleCacheAsync(caches);
        await _db.DeclareCacheAsync(Scope, "welldb-logs-cache", new ReferenceTypeSpec
        {
            Name = "WellLog",
            EntityType = "work-product-component--WellLog",
            Kind = "osdu:wks:work-product-component--WellLog:*",
            Fields = [new ReferenceFieldSpec("data.Name")],
            KeepCurrent = true,
        });
        var engine = Samples.Engine(ledger, _clock, sources: tables, cache: caches) with { Protocols = new FakeProtocolFactory(new FakeProtocol()) };
        using var runtime = await FlowRuntime.CreateAsync(engine, Samples.LocalFlow(_root), SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);

        // The sample flow's target is a placeholder no one answers on: the records land through the fake protocol, and the
        // step that reads them back cannot reach storage. The run stands, and says what the step could not do.
        var run = await runtime.RunAsync(force: false);
        Assert.True(run.Work.Delivered > 0);
        var kept = Assert.IsType<CacheKeptOutcome>(run.CacheKept);
        Assert.Equal(Scope, kept.Scope);
        Assert.Contains("A refresh of the cache flow brings the records in", kept.Skipped, StringComparison.Ordinal);

        // A run that sends nothing has nothing to keep current.
        _clock.Advance(TimeSpan.FromHours(1));
        var idle = await runtime.RunAsync(force: false);
        Assert.Equal(0, idle.Work.Delivered);
        Assert.Null(idle.CacheKept);
    }

    private static DateTimeOffset T(int hours) => new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero).AddHours(hours);

    private static ReferenceType Units(params (string Id, string Code)[] items)
        => new("UnitOfMeasure", "reference-data--UnitOfMeasure", items.Select(i => ReferenceItem.FromText(i.Id, new Dictionary<string, string> { ["Code"] = i.Code })));

    private static List<string> Ids(ReferenceSnapshot snapshot)
        => [.. snapshot.Type("UnitOfMeasure")!.Items.Select(i => i.Id).Order(StringComparer.Ordinal)];

    private async Task<List<string>> HeldAsync(string holder, string type = "UnitOfMeasure")
    {
        await using var db = _db.CreateDbContext();
        return await db.DeliveryCacheMembers.Where(m => m.Scope == Scope && m.TypeName == type && m.FlowName == holder)
            .Select(m => m.RecordId).OrderBy(id => id).ToListAsync();
    }

    /// <summary>The storage service of the fake platform, as a delivery's connection reaches it.</summary>
    private sealed class StorageRig : IDisposable
    {
        private readonly Http.HttpRuntime _runtime;

        public StorageRig(FakeOsduPlatform platform)
        {
            _runtime = new Http.HttpRuntime(
                new Model.FlowReliability { Retry = new Model.FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
                new Core.Secrets.SecretResolver([new Core.Secrets.EnvSecretProvider()]), TimeProvider.System, platform, allowLoopback: true);
            Client = new OsduHttpClient(
                _runtime, FakeOsduPlatform.Endpoint, new Model.TargetAuth { Type = Model.TargetAuthType.None },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = Scope });
            Storage = new StorageRecords(Client, OsduRecordProtocol.DefaultVerifyBatchPath);
        }

        public OsduHttpClient Client { get; }

        public StorageRecords Storage { get; }

        public void Dispose() => _runtime.Dispose();
    }
}
