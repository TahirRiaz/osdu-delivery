using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Snapshots;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The ledger's SQL Server bulk path against the module's own database: staging and completion as set-based statements,
/// which an in-memory SQLite database never takes, and the statistics view the provider builds. Runs when
/// <c>SQLFLOW_TEST_DB</c> points at a reachable, disposable database and skips otherwise; it writes only in the
/// <c>osdu</c> schema its own migration creates, and every run works under a flow and keys of its own, so runs never
/// see each other's rows.
/// </summary>
public class SqlServerLedgerTests
{
    private static readonly Lazy<string?> ConnectionString = new(() => Environment.GetEnvironmentVariable("SQLFLOW_TEST_DB"));

    private static readonly Lazy<bool> Reachable = new(() =>
    {
        var cs = ConnectionString.Value;
        if (string.IsNullOrWhiteSpace(cs))
        {
            return false;
        }

        try
        {
            using var connection = new SqlConnection(cs);
            connection.Open();
            return true;
        }
        catch (SqlException)
        {
            return false;
        }
    });

    /// <summary>The module's schema, brought up to date once per test run; the database itself is never created here.</summary>
    private static readonly Lazy<Task> Migrated = new(async () =>
    {
        await using var db = Database();
        await db.Database.MigrateAsync();
    });

    private readonly TestClock _clock = new();
    private readonly Guid _flow = FlowId.Of("sqlserver-ledger-" + Guid.NewGuid().ToString("N"));
    private readonly string _run = Guid.NewGuid().ToString("N");

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>A context over the module's database named by <c>SQLFLOW_TEST_DB</c>.</summary>
    private static OsduDbContext Database() => new(OsduDbContext.SqlServerOptions(ConnectionString.Value!));

    private static void RequireDatabase()
        => Skip.IfNot(
            Reachable.Value,
            "The SQL Server ledger tests need a reachable, disposable database. Set SQLFLOW_TEST_DB, for example through the git-ignored .sqlflow/env file.");

    private static async Task<OsduLedger> LedgerAsync(TimeProvider clock)
    {
        RequireDatabase();
        await Migrated.Value;
        return new OsduLedger(Database, clock);
    }

    private static async Task<OsduCacheStore> CachesAsync()
    {
        RequireDatabase();
        await Migrated.Value;
        return new OsduCacheStore(Database);
    }

    [SkippableFact]
    public async Task Cached_records_whose_osdu_ids_differ_only_by_case_are_two_rows()
    {
        // A live partition holds ...UnitOfMeasure:ft (the foot) and ...UnitOfMeasure:fT (the femtotesla). Under the
        // server's case-folding default the database took them for one key and the repository sync failed.
        var store = await CachesAsync();
        var cache = "case-" + Guid.NewGuid().ToString("N");
        const string Foot = "test:reference-data--UnitOfMeasure:ft";
        const string Femtotesla = "test:reference-data--UnitOfMeasure:fT";
        var units = new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure",
        [
            ReferenceItem.FromText(Foot, new Dictionary<string, string> { ["Code"] = "ft", ["Name"] = "foot" }),
            ReferenceItem.FromText(Femtotesla, new Dictionary<string, string> { ["Code"] = "fT", ["Name"] = "femtotesla" }),
        ]);

        try
        {
            var write = await store.MergeAsync(cache, "tests-cache", [units], new CacheCapture(null, "tests", "seeded"), DateTimeOffset.UtcNow);

            await using var db = Database();
            var found = await db.DeliveryCacheItems
                .Where(i => i.Scope == cache && i.RecordId == Foot)
                .Select(i => i.RecordId)
                .ToListAsync();
            Assert.Equal([Foot], found);

            // The flow's hold on each record is keyed by the id the same way, so both units are held.
            Assert.Equal(2, await db.DeliveryCacheMembers.CountAsync(m => m.Scope == cache && m.FlowName == "tests-cache"));
            Assert.Equal([Foot], await db.DeliveryCacheMembers.Where(m => m.Scope == cache && m.RecordId == Foot).Select(m => m.RecordId).ToListAsync());

            var loaded = await new OsduCacheStore(Database).LoadAsync(cache, write.Snapshot.Version);
            Assert.Equal(2, loaded!.Type("UnitOfMeasure")!.Items.Count);
        }
        finally
        {
            await CleanupCacheAsync(cache);
        }
    }

    [SkippableFact]
    public async Task A_cache_version_writes_and_reads_its_ranges_on_sql_server()
    {
        // The store's transaction, its range updates and the binary comparison of stored values, on the real server.
        var store = await CachesAsync();
        var cache = "ranges-" + Guid.NewGuid().ToString("N");
        var capture = new CacheCapture(Guid.NewGuid(), "tests", "seeded");
        static IReadOnlyList<ReferenceType> Units(string metreName) =>
        [
            new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure",
            [
                ReferenceItem.FromText("test:reference-data--UnitOfMeasure:m", new Dictionary<string, string> { ["Code"] = "m", ["Name"] = metreName }),
                ReferenceItem.FromText("test:reference-data--UnitOfMeasure:ft", new Dictionary<string, string> { ["Code"] = "ft", ["Name"] = "foot" }),
            ]),
        ];

        try
        {
            // Both captures land in one second: the second version's label carries its sequence.
            var captured = DateTimeOffset.UtcNow;
            var first = await store.MergeAsync(cache, "tests-cache", Units("metre"), capture, captured);
            var second = await store.MergeAsync(cache, "tests-cache", Units("Metre"), capture, captured);
            Assert.Equal(first.Snapshot.Version, second.Previous!.Version);
            Assert.Equal(first.Snapshot.Version + "-2", second.Snapshot.Version);

            var reader = new OsduCacheStore(Database);
            var versions = await reader.ListVersionsAsync(cache);
            Assert.Equal([second.Snapshot.Version, first.Snapshot.Version], versions.Select(v => v.Version));
            Assert.Equal(first.Snapshot.Version, versions[0].PreviousVersion);
            Assert.Equal(capture.RunId, versions[0].RunId);
            Assert.Equal("tests-cache", versions[0].FlowName);
            Assert.Equal(second.Snapshot.Version, await reader.CurrentVersionAsync(cache));
            Assert.Equal("metre", (await reader.LoadAsync(cache, first.Snapshot.Version))!.Type("UnitOfMeasure")!.Match("Code", "m")!.Fields["Name"].Text);
            Assert.Equal("Metre", (await reader.LoadAsync(cache, second.Snapshot.Version))!.Type("UnitOfMeasure")!.Match("Code", "m")!.Fields["Name"].Text);

            // A change of case is a change: only the metre moved, and the foot's one row covers both versions.
            await using var db = Database();
            var diff = await CacheVersions.CompareAsync(db, new CacheComparisonQuery(cache, first.Snapshot.Version));
            Assert.Equal((1L, 0L, 0L), (diff!.Changed, diff.Added, diff.Removed));
            Assert.Equal(3, await db.DeliveryCacheItems.CountAsync(i => i.Scope == cache));
        }
        finally
        {
            await CleanupCacheAsync(cache);
        }
    }

    private static async Task CleanupCacheAsync(string scope)
    {
        await using var db = Database();
        await db.DeliveryCacheMembers.Where(m => m.Scope == scope).ExecuteDeleteAsync();
        await db.DeliveryCacheItems.Where(i => i.Scope == scope).ExecuteDeleteAsync();
        await db.DeliveryCacheVersions.Where(v => v.Scope == scope).ExecuteDeleteAsync();
    }

    private RecordState Work(string name, Guid submission, string reference, string metadataHash, DateTime modified) => new()
    {
        DeliveryKey = DeliveryKey.Derive("sqlserver-ledger-test", [_run, name]),
        FlowId = _flow,
        SourceKey = _run + "/" + name,
        SourceKeyJson = $"[\"{_run}\",\"{name}\"]",
        MappingName = "Thing",
        TargetId = "dev:x:" + _run + name,
        LastSubmissionId = submission,
        PendingDocumentRef = reference,
        WorkBatch = 0,
        PendingRenderContext = "{}",
        PendingSourceModifiedUtc = modified,
        PendingSourceFileName = "welllog_20260901.csv",
        PendingSourceRowNumber = 1,
        PendingSourceUpdatedUtc = modified,
        PendingMetadataHash = metadataHash,
        PendingPayloadHash = "ph",
        PendingPayloadModifiedUtc = modified,
        PendingPayloadLocation = @"lake\curves\a|chunk_*.parquet",
        PendingMetadata = true,
        PendingPayload = true,
    };

    private static RecordCompletion Completion(RecordState claimed, Guid submission, DateTime at, bool nothingSent = false) => new()
    {
        DeliveryKey = claimed.DeliveryKey,
        Status = RecordStatus.Delivered,
        Promote = true,
        NothingSent = nothingSent,
        TargetVersion = nothingSent ? null : 1,
        Claimed = ClaimedWork.Of(claimed),
        Attempt = new AttemptRecord
        {
            DeliveryKey = claimed.DeliveryKey,
            SubmissionId = submission,
            Worker = "w",
            StartedUtc = at,
            CompletedUtc = at,
            Outcome = nothingSent ? AttemptOutcome.Skipped : AttemptOutcome.Delivered,
            Phase = nothingSent ? AttemptPhases.Unchanged : "metadata+payload",
            SourceFileName = claimed.PendingSourceFileName,
            SourceRowNumber = claimed.PendingSourceRowNumber,
            SourceUpdatedUtc = claimed.PendingSourceUpdatedUtc,
        },
    };

    [SkippableFact]
    public async Task Flow_statistics_come_from_the_indexed_view_and_count_the_last_24_hours_to_the_tick()
    {
        // Half past the hour: the 24-hour window then opens part way through an hour, so both halves of its count run (the
        // view's whole hours, and the index count of the part-hour).
        _clock.Advance(TimeSpan.FromMinutes(30));
        var now = Now;
        var ledger = await LedgerAsync(_clock);
        var s1 = Guid.NewGuid();
        (string Name, TimeSpan Before)[] deliveries =
        [
            ("old", TimeSpan.FromHours(25)),
            ("part-hour-outside", TimeSpan.FromHours(24) + TimeSpan.FromMinutes(10)),
            ("part-hour-inside", TimeSpan.FromHours(24) - TimeSpan.FromMinutes(10)),
            ("recent", TimeSpan.FromHours(1)),
        ];
        await ledger.UpsertPendingAsync(deliveries.Select((d, i) => Work(d.Name, s1, $"0:{i * 10}:10", "mh", now.AddDays(-3))).ToList());
        _clock.Advance(-TimeSpan.FromHours(26));
        var claimed = await ledger.ClaimAsync(_flow, s1, "w1", 10, TimeSpan.FromDays(2), Now);
        Assert.Equal(4, claimed.Count);
        foreach (var (name, before) in deliveries)
        {
            _clock.Advance(now - before - Now);
            await ledger.CompleteAsync(Completion(claimed.Single(r => r.SourceKey.EndsWith("/" + name, StringComparison.Ordinal)), s1, Now));
        }

        _clock.Advance(now - Now);
        var recent = claimed.Single(r => r.SourceKey.EndsWith("/recent", StringComparison.Ordinal)).DeliveryKey;
        await ledger.RecordVerifyAsync(recent, VerifyOutcome.Drifted, 2, Now, requeue: false);
        await ledger.UpsertPendingAsync([Work("waiting", s1, "0:40:10", "mh", now.AddDays(-3))]);

        var stats = await ledger.StatsAsync(_flow, Now);
        Assert.Equal(5, stats.Total);
        Assert.Equal(4, stats.Delivered);
        Assert.Equal(1, stats.Pending);
        Assert.Equal(1, stats.Drifted);
        Assert.Equal(2, stats.DeliveredLast24h);
        Assert.Equal(now.AddHours(-1), stats.LastDeliveredUtc);

        // What the statistics read is the view, and it holds the flow's five records.
        await using var db = Database();
        var viewed = await db.DeliveryRecordCounts
            .FromSqlRaw("SELECT [FlowId], [Status], [LastVerifyOutcome], [DeliveredHour], [Records] FROM [osdu].[RecordCount] WITH (NOEXPAND)")
            .Where(c => c.FlowId == _flow)
            .ToListAsync();
        Assert.Equal(5, viewed.Sum(c => c.Records));
        Assert.Equal(4, viewed.Where(c => c.Status == "delivered").Sum(c => c.Records));
    }

    [SkippableFact]
    public async Task Record_listing_searches_and_counts_run_bounded_on_sql_server()
    {
        // The bounded shapes (a TOP per identity index under UNION ALL, a TOP inside a count) as SQL Server runs them.
        var ledger = await LedgerAsync(_clock);
        var s1 = Guid.NewGuid();
        await ledger.UpsertPendingAsync(Enumerable.Range(0, 12).Select(i => Work($"well-{i:D2}", s1, $"0:{i * 10}:10", "mh", Now.AddDays(-1))).ToList());

        var prefix = new RecordQuery { Search = _run + "/well-0" };
        Assert.Equal(new BoundedCount(10, Exact: true), await ledger.CountAsync(_flow, prefix, 11));
        Assert.Equal(new BoundedCount(4, Exact: false), await ledger.CountAsync(_flow, prefix, 4));
        var first = await ledger.ListAsync(_flow, prefix with { Max = 6 });
        var second = await ledger.ListAsync(_flow, prefix with { Offset = 6, Max = 6 });
        Assert.Equal(10, first.Concat(second).Select(r => r.DeliveryKey).Distinct().Count());

        var contains = new RecordQuery { Search = "ell-1", Mode = SearchMode.Contains };
        Assert.Equal(2, (await ledger.ListAsync(_flow, contains)).Count);
        Assert.Equal(12, (await ledger.ListKeysAsync(_flow, new RecordQuery(), 100)).Count);

        Assert.Equal(new BoundedCount(5, Exact: false), await ledger.CountLookupAsync(_run + "/well-", 5));
        Assert.Equal(new BoundedCount(12, Exact: true), await ledger.CountLookupAsync(_run + "/well-", 13));
        Assert.Equal(3, (await ledger.LookupAsync(_run + "/well-", 3)).Count);

        // A record is found by the file its row came from, which is how an operator gets from a landed file to its records.
        Assert.Equal(12, (await ledger.ListAsync(_flow, new RecordQuery { Search = "welllog_2026", Max = 20 })).Count);
    }

    [SkippableFact]
    public async Task Bulk_staging_queues_behind_an_in_flight_delivery_and_refuses_older_work_and_bulk_completion_keeps_them_apart()
    {
        var ledger = await LedgerAsync(_clock);
        var s1 = Guid.NewGuid();
        var first = await ledger.UpsertPendingAsync([Work("a", s1, "0:0:10", "mh-a1", Now.AddDays(-3)), Work("b", s1, "0:10:10", "mh-b1", Now.AddDays(-3))]);
        Assert.Equal(2, first.Staged);
        Assert.Empty(first.Refused);
        var claimed = await ledger.ClaimAsync(_flow, s1, "w1", 10, TimeSpan.FromMinutes(5), Now);
        Assert.Equal(2, claimed.Count);
        var a = claimed.Single(r => r.SourceKey.EndsWith("/a", StringComparison.Ordinal));
        var b = claimed.Single(r => r.SourceKey.EndsWith("/b", StringComparison.Ordinal));

        // One call carries newer work for a record in flight and older work for another: the first queues, the second is named.
        var s2 = Guid.NewGuid();
        var second = await ledger.UpsertPendingAsync([Work("a", s2, "0:0:12", "mh-a2", Now.AddDays(-1)), Work("b", s2, "0:12:10", "mh-b0", Now.AddDays(-4))]);
        Assert.Equal(1, second.Staged);
        Assert.Equal(b.DeliveryKey, Assert.Single(second.Refused));
        var queued = await ledger.GetRecordAsync(_flow, a.DeliveryKey);
        Assert.Equal(RecordStatus.Delivering, queued!.Status);
        Assert.Equal(a.LeaseOwner, queued.LeaseOwner);
        Assert.Equal(1, queued.AttemptCount);
        Assert.Equal(s2, queued.LastSubmissionId);
        Assert.Equal("0:0:12", queued.PendingDocumentRef);
        Assert.Equal(s1, (await ledger.GetRecordAsync(_flow, b.DeliveryKey))!.LastSubmissionId);

        // The in-flight try's steps never reach the newer work.
        await ledger.SaveStepAsync(a.DeliveryKey, s1, "0:0:10", "{\"metadata\":{\"version\":\"1\"}}");
        Assert.Null((await ledger.GetRecordAsync(_flow, a.DeliveryKey))!.PendingStepJson);

        // Two completions take the set-based statement: a was superseded while in flight, b was not.
        await ledger.CompleteManyAsync([Completion(a, s1, Now), Completion(b, s1, Now)]);
        var settledA = await ledger.GetRecordAsync(_flow, a.DeliveryKey);
        Assert.Equal(RecordStatus.Pending, settledA!.Status);
        Assert.Null(settledA.LeaseOwner);
        Assert.Equal(0, settledA.AttemptCount);
        Assert.Equal("mh-a1", settledA.MetadataHash);
        Assert.Equal(Now.AddDays(-3), settledA.SourceModifiedUtc);
        Assert.Equal(Now.AddDays(-3), settledA.PayloadModifiedUtc);
        Assert.Equal(Now, settledA.LastDeliveredUtc);
        Assert.Equal("mh-a2", settledA.PendingMetadataHash);
        Assert.Equal("0:0:12", settledA.PendingDocumentRef);
        // The delivered version's origin is the row the document was built from, and the attempt names it too.
        Assert.Equal("welllog_20260901.csv", settledA.SourceFileName);
        Assert.Equal("welllog_20260901.csv", (await ledger.ListAttemptsAsync(a.DeliveryKey, 5))[0].SourceFileName);
        var settledB = await ledger.GetRecordAsync(_flow, b.DeliveryKey);
        Assert.Equal(RecordStatus.Delivered, settledB!.Status);
        Assert.Equal("mh-b1", settledB.MetadataHash);
        Assert.Null(settledB.PendingDocumentRef);
        Assert.Equal(Now.AddDays(-3), settledB.SourceModifiedUtc);
        Assert.Equal(Now, settledB.LastDeliveredUtc);

        // The queued work lands without anything sent (the final check found it held): promoted, delivery time untouched.
        var delivered = Now;
        await ledger.UpsertPendingAsync([Work("c", s2, "0:22:10", "mh-c1", Now)]);
        _clock.Advance(TimeSpan.FromMinutes(1));
        var next = await ledger.ClaimAsync(_flow, s2, "w2", 10, TimeSpan.FromMinutes(5), Now);
        Assert.Equal(2, next.Count);
        var a2 = next.Single(r => r.SourceKey.EndsWith("/a", StringComparison.Ordinal));
        var c = next.Single(r => r.SourceKey.EndsWith("/c", StringComparison.Ordinal));
        await ledger.CompleteManyAsync([Completion(a2, s2, Now, nothingSent: true), Completion(c, s2, Now)]);

        var landedA = await ledger.GetRecordAsync(_flow, a.DeliveryKey);
        Assert.Equal(RecordStatus.Delivered, landedA!.Status);
        Assert.Equal("mh-a2", landedA.MetadataHash);
        Assert.Equal(delivered.AddDays(-1), landedA.SourceModifiedUtc);
        Assert.Equal(delivered, landedA.LastDeliveredUtc);
        Assert.Null(landedA.PendingDocumentRef);
        Assert.Equal(Now, (await ledger.GetRecordAsync(_flow, c.DeliveryKey))!.LastDeliveredUtc);
        Assert.Equal(2, await ledger.CountAttemptsAsync(s1, AttemptOutcome.Delivered));
        Assert.Equal(1, await ledger.CountAttemptsAsync(s2, AttemptOutcome.Delivered));
        Assert.Equal(1, await ledger.CountAttemptsAsync(s2, AttemptOutcome.Skipped, AttemptPhases.Unchanged));
    }

    [SkippableFact]
    public async Task The_scope_watermark_and_the_records_waiting_to_be_planned_round_trip_on_sql_server()
    {
        var ledger = await LedgerAsync(_clock);
        var scope = "logSource=" + _run;
        var submission = Guid.NewGuid();
        await ledger.UpsertPendingAsync([Work("w1", submission, "0:0:10", "mh", Now.AddDays(-1))]);
        var key = DeliveryKey.Derive("sqlserver-ledger-test", [_run, "w1"]);

        await ledger.SetWatermarkAsync(new SourceWatermark(_flow, scope, Now, submission, Now, "ctx-1"));
        await ledger.SetWatermarkAsync(new SourceWatermark(_flow, scope, Now.AddMinutes(-30), Guid.NewGuid(), Now, "ctx-0"));
        var mark = await ledger.GetWatermarkAsync(_flow, scope);
        Assert.Equal(Now, mark!.UpdatedThroughUtc);
        Assert.Equal("ctx-1", mark.ContextHash);

        Assert.Equal(1, await ledger.ForceRedeliverAsync(_flow, [key], RedeliverScope.All, Now));
        var requested = Assert.Single(await ledger.ListPlanRequestedAsync(_flow, null, 10));
        Assert.Equal(key, requested.DeliveryKey);
        Assert.Equal($"[\"{_run}\",\"w1\"]", requested.SourceKeyJson);
        await ledger.ClearPlanRequestedAsync(_flow, [key]);
        Assert.Empty(await ledger.ListPlanRequestedAsync(_flow, null, 10));
    }
}
