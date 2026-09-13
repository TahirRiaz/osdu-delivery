using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Snapshots;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The ledger's SQL Server bulk path against a real catalog: staging and completion as set-based statements, which an
/// in-memory SQLite catalog never takes. Runs when <c>SQLFLOW_TEST_DB</c> points at a reachable, disposable catalog
/// database and skips otherwise. Every run works under a flow and keys of its own, so runs never see each other's rows.
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

    private readonly TestClock _clock = new();
    private readonly Guid _flow = FlowId.Of("sqlserver-ledger-" + Guid.NewGuid().ToString("N"));
    private readonly string _run = Guid.NewGuid().ToString("N");

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private static async Task<CatalogLedger> LedgerAsync(TimeProvider clock)
    {
        Skip.IfNot(
            Reachable.Value,
            "The SQL Server ledger tests need a reachable, disposable catalog database. Set SQLFLOW_TEST_DB, for example via the git-ignored .sqlflow/env file.");
        var cs = ConnectionString.Value!;
        await CatalogDatabase.ProvisionAsync(cs);
        return new CatalogLedger(() => CatalogDatabase.Create(cs), clock);
    }

    [SkippableFact]
    public async Task Cached_records_whose_osdu_ids_differ_only_by_case_are_two_rows()
    {
        // A live partition holds ...UnitOfMeasure:ft (the foot) and ...UnitOfMeasure:fT (the femtotesla). Under the
        // server's case-folding default the catalog took them for one key and the repository sync failed.
        Skip.IfNot(
            Reachable.Value,
            "The SQL Server ledger tests need a reachable, disposable catalog database. Set SQLFLOW_TEST_DB, for example via the git-ignored .sqlflow/env file.");
        var cs = ConnectionString.Value!;
        await CatalogDatabase.ProvisionAsync(cs);

        var cache = "case-" + Guid.NewGuid().ToString("N");
        const string Foot = "test:reference-data--UnitOfMeasure:ft";
        const string Femtotesla = "test:reference-data--UnitOfMeasure:fT";
        var store = new CatalogCacheStore(() => CatalogDatabase.Create(cs));
        var units = new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure",
        [
            ReferenceItem.FromText(Foot, new Dictionary<string, string> { ["Code"] = "ft", ["Name"] = "foot" }),
            ReferenceItem.FromText(Femtotesla, new Dictionary<string, string> { ["Code"] = "fT", ["Name"] = "femtotesla" }),
        ]);

        try
        {
            await store.SaveAsync(cache, new ReferenceSnapshot("v1", DateTimeOffset.UtcNow, [units]), new CacheCapture(null, "tests", "seeded"), makeCurrent: true);

            await using var db = CatalogDatabase.Create(cs);
            var found = await db.DeliveryCacheItems
                .Where(i => i.CacheName == cache && i.RecordId == Foot)
                .Select(i => i.RecordId)
                .ToListAsync();
            Assert.Equal([Foot], found);

            var loaded = await new CatalogCacheStore(() => CatalogDatabase.Create(cs)).LoadAsync(cache, "v1");
            Assert.Equal(2, loaded!.Type("UnitOfMeasure")!.Items.Count);
        }
        finally
        {
            await CleanupCacheAsync(cs, cache);
        }
    }

    [SkippableFact]
    public async Task A_cache_version_writes_and_reads_its_ranges_on_sql_server()
    {
        // The store's transaction, its range updates and the binary comparison of stored values, on the real server.
        Skip.IfNot(
            Reachable.Value,
            "The SQL Server ledger tests need a reachable, disposable catalog database. Set SQLFLOW_TEST_DB, for example via the git-ignored .sqlflow/env file.");
        var cs = ConnectionString.Value!;
        await CatalogDatabase.ProvisionAsync(cs);

        var cache = "ranges-" + Guid.NewGuid().ToString("N");
        var store = new CatalogCacheStore(() => CatalogDatabase.Create(cs));
        var capture = new CacheCapture(Guid.NewGuid(), "tests", "seeded");
        static ReferenceSnapshot Units(string version, string metreName) => new(version, DateTimeOffset.UtcNow,
        [
            new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure",
            [
                ReferenceItem.FromText("test:reference-data--UnitOfMeasure:m", new Dictionary<string, string> { ["Code"] = "m", ["Name"] = metreName }),
                ReferenceItem.FromText("test:reference-data--UnitOfMeasure:ft", new Dictionary<string, string> { ["Code"] = "ft", ["Name"] = "foot" }),
            ]),
        ]);

        try
        {
            await store.SaveAsync(cache, Units("v1", "metre"), capture, makeCurrent: true);
            var second = await store.SaveAsync(cache, Units("v2", "Metre"), capture, makeCurrent: true);
            Assert.Equal("v1", second.PreviousVersion);
            Assert.Equal(capture.RunId, second.RunId);

            var reader = new CatalogCacheStore(() => CatalogDatabase.Create(cs));
            Assert.Equal("v2", await reader.CurrentVersionAsync(cache));
            Assert.Equal("metre", (await reader.LoadAsync(cache, "v1"))!.Type("UnitOfMeasure")!.Match("Code", "m")!.Fields["Name"].Text);
            Assert.Equal("Metre", (await reader.LoadAsync(cache, "v2"))!.Type("UnitOfMeasure")!.Match("Code", "m")!.Fields["Name"].Text);

            // A change of case is a change: only the metre moved, and the foot's one row covers both versions.
            await using var db = CatalogDatabase.Create(cs);
            var diff = await CacheVersions.CompareAsync(db, new CacheComparisonQuery(cache, "v1"));
            Assert.Equal((1L, 0L, 0L), (diff!.Changed, diff.Added, diff.Removed));
            Assert.Equal(3, await db.DeliveryCacheItems.CountAsync(i => i.CacheName == cache));
        }
        finally
        {
            await CleanupCacheAsync(cs, cache);
        }
    }

    private static async Task CleanupCacheAsync(string cs, string cache)
    {
        await using var db = CatalogDatabase.Create(cs);
        await db.DeliveryCacheItems.Where(i => i.CacheName == cache).ExecuteDeleteAsync();
        await db.DeliveryCacheVersions.Where(v => v.CacheName == cache).ExecuteDeleteAsync();
    }

    private RecordState Work(string name, Guid submission, string reference, string metadataHash, DateTime modified) => new()
    {
        DeliveryKey = DeliveryKey.Derive("sqlserver-ledger-test", [_run, name]),
        FlowId = _flow,
        SourceKey = _run + "/" + name,
        MappingName = "Thing",
        TargetId = "dev:x:" + _run + name,
        LastSubmissionId = submission,
        PendingDocumentRef = reference,
        WorkBatch = 0,
        PendingRenderContext = "{}",
        PendingSourceModifiedUtc = modified,
        PendingMetadataHash = metadataHash,
        PendingPayloadHash = "ph",
        PendingPayloadModifiedUtc = modified,
        PendingPayloadLocation = "loc",
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
        await using var db = CatalogDatabase.Create(ConnectionString.Value!);
        var viewed = await db.DeliveryRecordCounts
            .FromSqlRaw("SELECT [FlowId], [Status], [LastVerifyOutcome], [DeliveredHour], [Records] FROM [delivery].[RecordCount] WITH (NOEXPAND)")
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
}
