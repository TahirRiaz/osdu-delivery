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
/// <para>Some of these tests watch the database as a whole (how often it locked a whole ledger table), which the suite's
/// other SQL Server classes would disturb, so the class runs in <see cref="SqlServerLedgerIsolation"/>, after them and
/// apart from them.</para>
/// </summary>
[Collection(SqlServerLedgerIsolation.Name)]
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
    public async Task Refreshes_of_different_partitions_at_the_same_time_neither_block_nor_deadlock_each_other_on_sql_server()
    {
        // Every partition's refresh writes its own version rows, and two partitions refreshing together must each touch only
        // their own: a statement that reads past its partition waits on the other refresh's new rows while that refresh
        // waits on its own, and the database ends it as a deadlock victim.
        var store = await CachesAsync();
        var scopes = Enumerable.Range(0, 4).Select(i => $"parallel-{i}-" + Guid.NewGuid().ToString("N")).ToList();
        static IReadOnlyList<ReferenceType> Units(string name) =>
        [
            new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure",
            [
                ReferenceItem.FromText("test:reference-data--UnitOfMeasure:m", new Dictionary<string, string> { ["Code"] = "m", ["Name"] = name }),
            ]),
        ];

        try
        {
            for (var round = 0; round < 8; round++)
            {
                var name = "metre-" + round.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await Task.WhenAll(scopes.Select(scope => Task.Run(() => store.MergeAsync(
                    scope, "tests-cache", Units(name), new CacheCapture(null, "tests", "parallel"), DateTimeOffset.UtcNow))));
            }

            await using var db = Database();
            foreach (var scope in scopes)
            {
                Assert.Equal(8, await db.DeliveryCacheVersions.CountAsync(v => v.Scope == scope));
                Assert.Equal(1, await db.DeliveryCacheVersions.CountAsync(v => v.Scope == scope && v.Current));
            }
        }
        finally
        {
            foreach (var scope in scopes)
            {
                await CleanupCacheAsync(scope);
            }
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
        await ledger.UpsertPendingAsync(_flow, deliveries.Select((d, i) => Work(d.Name, s1, $"0:{i * 10}:10", "mh", now.AddDays(-3))).ToList());
        _clock.Advance(-TimeSpan.FromHours(26));
        var claimed = (await ledger.ClaimAsync(_flow, s1, "w1", 10, TimeSpan.FromDays(2), Now)).Records;
        Assert.Equal(4, claimed.Count);
        foreach (var (name, before) in deliveries)
        {
            _clock.Advance(now - before - Now);
            await ledger.CompleteAsync(_flow, Completion(claimed.Single(r => r.SourceKey.EndsWith("/" + name, StringComparison.Ordinal)), s1, Now));
        }

        _clock.Advance(now - Now);
        var recent = claimed.Single(r => r.SourceKey.EndsWith("/recent", StringComparison.Ordinal)).DeliveryKey;
        await ledger.RecordVerifyAsync(_flow, recent, VerifyOutcome.Drifted, 2, Now, requeue: false);
        await ledger.UpsertPendingAsync(_flow, [Work("waiting", s1, "0:40:10", "mh", now.AddDays(-3))]);

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
        await ledger.UpsertPendingAsync(_flow, Enumerable.Range(0, 12).Select(i => Work($"well-{i:D2}", s1, $"0:{i * 10}:10", "mh", Now.AddDays(-1))).ToList());

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
        // The origin that answers is the delivered one, which is what the index covers, so it is the completion that puts a
        // record within reach of the search: a queued version carries its file name, and is found once it is delivered.
        var staged = await ledger.ListAsync(_flow, new RecordQuery { Max = 20 });
        Assert.Empty(await ledger.ListAsync(_flow, new RecordQuery { Search = "welllog_2026", Max = 20 }));
        await ledger.CompleteManyAsync(_flow, staged.Select(r => Completion(r, s1, Now)).ToList());
        Assert.Equal(12, (await ledger.ListAsync(_flow, new RecordQuery { Search = "welllog_2026", Max = 20 })).Count);
    }

    [SkippableFact]
    public async Task Bulk_staging_queues_behind_an_in_flight_delivery_and_refuses_older_work_and_bulk_completion_keeps_them_apart()
    {
        var ledger = await LedgerAsync(_clock);
        var s1 = Guid.NewGuid();
        var first = await ledger.UpsertPendingAsync(_flow, [Work("a", s1, "0:0:10", "mh-a1", Now.AddDays(-3)), Work("b", s1, "0:10:10", "mh-b1", Now.AddDays(-3))]);
        Assert.Equal(2, first.Staged);
        Assert.Empty(first.Refused);
        var claimed = (await ledger.ClaimAsync(_flow, s1, "w1", 10, TimeSpan.FromMinutes(5), Now)).Records;
        Assert.Equal(2, claimed.Count);
        var a = claimed.Single(r => r.SourceKey.EndsWith("/a", StringComparison.Ordinal));
        var b = claimed.Single(r => r.SourceKey.EndsWith("/b", StringComparison.Ordinal));

        // One call carries newer work for a record in flight and older work for another: the first queues, the second is named.
        var s2 = Guid.NewGuid();
        var second = await ledger.UpsertPendingAsync(_flow, [Work("a", s2, "0:0:12", "mh-a2", Now.AddDays(-1)), Work("b", s2, "0:12:10", "mh-b0", Now.AddDays(-4))]);
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
        await ledger.SaveStepAsync(_flow, a.DeliveryKey, s1, "0:0:10", "{\"metadata\":{\"version\":\"1\"}}", Now);
        Assert.Null((await ledger.GetRecordAsync(_flow, a.DeliveryKey))!.PendingStepJson);

        // Two completions take the set-based statement: a was superseded while in flight, b was not.
        await ledger.CompleteManyAsync(_flow, [Completion(a, s1, Now), Completion(b, s1, Now)]);
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
        Assert.Equal("welllog_20260901.csv", (await ledger.ListAttemptsAsync(_flow, a.DeliveryKey, 5))[0].SourceFileName);
        var settledB = await ledger.GetRecordAsync(_flow, b.DeliveryKey);
        Assert.Equal(RecordStatus.Delivered, settledB!.Status);
        Assert.Equal("mh-b1", settledB.MetadataHash);
        Assert.Null(settledB.PendingDocumentRef);
        Assert.Equal(Now.AddDays(-3), settledB.SourceModifiedUtc);
        Assert.Equal(Now, settledB.LastDeliveredUtc);

        // The queued work lands without anything sent (the final check found it held): promoted, delivery time untouched.
        var delivered = Now;
        await ledger.UpsertPendingAsync(_flow, [Work("c", s2, "0:22:10", "mh-c1", Now)]);
        _clock.Advance(TimeSpan.FromMinutes(1));
        var next = (await ledger.ClaimAsync(_flow, s2, "w2", 10, TimeSpan.FromMinutes(5), Now)).Records;
        Assert.Equal(2, next.Count);
        var a2 = next.Single(r => r.SourceKey.EndsWith("/a", StringComparison.Ordinal));
        var c = next.Single(r => r.SourceKey.EndsWith("/c", StringComparison.Ordinal));
        await ledger.CompleteManyAsync(_flow, [Completion(a2, s2, Now, nothingSent: true), Completion(c, s2, Now)]);

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
    public async Task Two_flows_stage_and_complete_the_same_row_apart_and_an_osdu_id_keeps_one_owner_on_sql_server()
    {
        var ledger = await LedgerAsync(_clock);
        var other = FlowId.Of("sqlserver-ledger-" + Guid.NewGuid().ToString("N"));
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();
        await ledger.RegisterSubmissionAsync(new SubmissionState
        {
            SubmissionId = s1, FlowId = _flow, FlowName = "welllog-" + _run, MappingReference = "Thing@1.0.0", RenderContext = "{}",
        });

        // The same two rows in two flows, rendered to different kinds: four records, staged and completed by the bulk path.
        var mine = await ledger.UpsertPendingAsync(_flow, [Work("a", s1, "0:0:10", "mh-a", Now.AddDays(-1)), Work("b", s1, "0:10:10", "mh-b", Now.AddDays(-1))]);
        var theirs = await ledger.UpsertPendingAsync(other, [
            Work("a", s2, "0:0:10", "mh-a-other", Now.AddDays(-1)) with { FlowId = other, TargetId = "dev:y:" + _run + "a" },
            Work("b", s2, "0:10:10", "mh-b-other", Now.AddDays(-1)) with { FlowId = other, TargetId = "dev:y:" + _run + "b" },
        ]);
        Assert.Equal((2, 2), (mine.Staged, theirs.Staged));
        Assert.Empty(theirs.Conflicts);

        var claimed = (await ledger.ClaimAsync(_flow, s1, "w1", 10, TimeSpan.FromMinutes(5), Now)).Records;
        Assert.Equal(2, claimed.Count);
        await ledger.CompleteManyAsync(_flow, claimed.Select(r => Completion(r, s1, Now)).ToList());
        foreach (var record in claimed)
        {
            Assert.Equal(RecordStatus.Delivered, (await ledger.GetRecordAsync(_flow, record.DeliveryKey))!.Status);
            var untouched = await ledger.GetRecordAsync(other, record.DeliveryKey);
            Assert.Equal((RecordStatus.Pending, (string?)null), (untouched!.Status, untouched.MetadataHash));
            Assert.Single(await ledger.ListAttemptsAsync(_flow, record.DeliveryKey, 5));
            Assert.Empty(await ledger.ListAttemptsAsync(other, record.DeliveryKey, 5));
            Assert.Equal(2, (await ledger.LookupAsync(record.DeliveryKey.Value.ToString(), 10)).Count);
        }

        // A third flow rendering the first flow's ids is refused, record by record, with the owner named; a flow restaging
        // its own ids is not, and neither is the second flow, whose records keep the ids they were first given.
        var third = FlowId.Of("sqlserver-ledger-" + Guid.NewGuid().ToString("N"));
        var refused = await ledger.UpsertPendingAsync(third, [
            Work("a", s2, "0:0:10", "mh", Now) with { FlowId = third },
            Work("c", s2, "0:10:10", "mh", Now) with { FlowId = third, TargetId = "dev:z:" + _run + "c" },
        ]);
        Assert.Equal(1, refused.Staged);
        var conflict = Assert.Single(refused.Conflicts);
        Assert.Equal(("dev:x:" + _run + "a", _flow, "welllog-" + _run), (conflict.TargetId, conflict.OwnerFlowId, conflict.OwnerFlowName));
        Assert.Null(await ledger.GetRecordAsync(third, conflict.DeliveryKey));
        Assert.Equal(2, (await ledger.UpsertPendingAsync(_flow, [Work("a", s1, "0:0:10", "mh-a", Now), Work("b", s1, "0:10:10", "mh-b", Now)])).Staged);
        var again = await ledger.UpsertPendingAsync(other, [Work("a", s2, "0:0:10", "mh-a-other", Now) with { FlowId = other }]);
        Assert.Equal((1, 0), (again.Staged, again.Conflicts.Count));
        Assert.Equal("dev:y:" + _run + "a", (await ledger.GetRecordAsync(other, conflict.DeliveryKey))!.ClaimedTargetId);

        // An id that differs only by case is another OSDU record, and the database holds a claim to one owner outright.
        var cased = await ledger.UpsertPendingAsync(third, [Work("d", s2, "0:0:10", "mh", Now) with { FlowId = third, TargetId = "DEV:X:" + _run + "a" }]);
        Assert.Equal((1, 0), (cased.Staged, cased.Conflicts.Count));
        await using var db = Database();
        var claimedKey = DeliveryKey.Derive("sqlserver-ledger-test", [_run, "c"]).Value;
        await Assert.ThrowsAsync<DbUpdateException>(async () =>
        {
            var copy = await db.DeliveryRecords.AsNoTracking().SingleAsync(r => r.FlowId == third && r.DeliveryKey == claimedKey);
            db.DeliveryRecords.Add(new DeliveryRecord
            {
                FlowId = Guid.NewGuid(), DeliveryKey = copy.DeliveryKey, SourceKey = copy.SourceKey, MappingName = copy.MappingName,
                Status = "pending", TargetId = copy.TargetId, ClaimedTargetId = copy.ClaimedTargetId, CreatedUtc = Now, UpdatedUtc = Now,
            });
            await db.SaveChangesAsync();
        });
    }

    [SkippableFact]
    public async Task Pruning_deletes_in_bounded_statements_and_keeps_each_flow_s_last_attempt_on_sql_server()
    {
        // The attempts are dated before anything else the shared database holds, so the prune reaches this test's alone.
        var ledger = await LedgerAsync(_clock);
        var other = FlowId.Of("sqlserver-ledger-" + Guid.NewGuid().ToString("N"));
        var s1 = Guid.NewGuid();
        await ledger.UpsertPendingAsync(_flow, [Work("old", s1, "0:0:10", "mh", Now)]);
        await ledger.UpsertPendingAsync(other, [Work("old", s1, "0:0:10", "mh", Now) with { FlowId = other, TargetId = "dev:y:" + _run + "old" }]);
        var key = DeliveryKey.Derive("sqlserver-ledger-test", [_run, "old"]);
        var ancient = new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var flow in new[] { _flow, other })
        {
            for (var i = 0; i < 3; i++)
            {
                await ledger.CompleteAsync(flow, new RecordCompletion
                {
                    DeliveryKey = key,
                    Status = RecordStatus.Pending,
                    Attempt = new AttemptRecord { DeliveryKey = key, Worker = "w", StartedUtc = ancient.AddDays(i), CompletedUtc = ancient.AddDays(i), Outcome = AttemptOutcome.Failed, Phase = "none" },
                });
            }
        }

        var bounded = new OsduLedger(Database, _clock) { PruneBatch = 3 };
        Assert.Equal(4, await bounded.PruneAttemptsAsync(ancient.AddYears(1)));
        Assert.Equal(ancient.AddDays(2), Assert.Single(await ledger.ListAttemptsAsync(_flow, key, 10)).StartedUtc);
        Assert.Equal(ancient.AddDays(2), Assert.Single(await ledger.ListAttemptsAsync(other, key, 10)).StartedUtc);
    }

    [SkippableFact]
    public async Task Flows_racing_for_one_osdu_id_leave_one_owner_and_name_it_to_the_other_on_sql_server()
    {
        // Two intakes of different flows stage the same new ids at the same moment. Whichever order the database puts them
        // in, one flow owns each id, the other is told whose it is, and neither intake fails.
        var ledger = await LedgerAsync(_clock);
        var names = Enumerable.Range(0, 40).Select(i => $"race-{i:D2}").ToList();
        for (var round = 0; round < 5; round++)
        {
            var flows = new[] { FlowId.Of("sqlserver-ledger-" + Guid.NewGuid().ToString("N")), FlowId.Of("sqlserver-ledger-" + Guid.NewGuid().ToString("N")) };
            var prefix = $"{round}-";
            var results = await Task.WhenAll(flows.Select(flow => Task.Run(() => ledger.UpsertPendingAsync(
                flow,
                names.Select((n, i) => Work(prefix + n, Guid.NewGuid(), $"0:{i * 10}:10", "mh", Now) with { FlowId = flow }).ToList()))));

            Assert.Equal(names.Count, results.Sum(r => r.Staged));
            Assert.Equal(names.Count, results.Sum(r => r.Conflicts.Count));
            foreach (var (result, index) in results.Select((r, i) => (r, i)))
            {
                Assert.All(result.Conflicts, c => Assert.Equal(flows[1 - index], c.OwnerFlowId));
            }

            await using var db = Database();
            var ids = names.Select(n => "dev:x:" + _run + prefix + n).ToList();
            Assert.Equal(names.Count, await db.DeliveryRecords.CountAsync(r => r.ClaimedTargetId != null && ids.Contains(r.ClaimedTargetId)));
        }
    }

    [SkippableFact]
    public async Task A_batch_wider_than_a_slice_is_staged_leased_released_and_redelivered_a_slice_at_a_time_on_sql_server()
    {
        var ledger = await LedgerAsync(_clock);
        var sliced = new OsduLedger(Database, _clock) { WriteSlice = 3 };
        var owner = FlowId.Of("sqlserver-ledger-" + Guid.NewGuid().ToString("N"));
        await ledger.UpsertPendingAsync(owner, [Work("claimed", Guid.NewGuid(), "0:0:10", "mh", Now) with { FlowId = owner }]);
        var s1 = Guid.NewGuid();
        await ledger.UpsertPendingAsync(_flow, [Work("newer-0", s1, "0:0:10", "mh", Now), Work("newer-1", s1, "0:10:10", "mh", Now)]);

        // Ten records, three to a statement, so four slices in key order: two older than the work already queued, one
        // whose OSDU id another flow owns, and seven new. Every slice's outcome is in the one result.
        var s2 = Guid.NewGuid();
        var fresh = Enumerable.Range(0, 7).Select(i => Work($"fresh-{i}", s2, $"0:{i * 10}:10", "mh", Now.AddDays(-1))).ToList();
        var staging = await sliced.UpsertPendingAsync(_flow, [
            .. fresh.Take(4),
            Work("newer-0", s2, "0:0:10", "mh", Now.AddDays(-1)),
            Work("claimed", s2, "0:0:10", "mh", Now.AddDays(-1)),
            Work("newer-1", s2, "0:10:10", "mh", Now.AddDays(-1)),
            .. fresh.Skip(4),
        ]);
        Assert.Equal(7, staging.Staged);
        Assert.Equal(new[] { Key("newer-0"), Key("newer-1") }.Select(k => k.Value).Order(), staging.Refused.Select(k => k.Value).Order());
        var conflict = Assert.Single(staging.Conflicts);
        Assert.Equal((Key("claimed"), owner), (conflict.DeliveryKey, conflict.OwnerFlowId));
        Assert.Equal(s1, (await ledger.GetRecordAsync(_flow, Key("newer-0")))!.LastSubmissionId);

        // The batch's lease reaches all seven, and so do its renewal and its release.
        await sliced.AddWorkBatchAsync(new WorkBatchState { SubmissionId = s2, FlowId = _flow, Index = 0, Location = "work/" + _run, RecordCount = 7, CreatedUtc = Now });
        var claimed = await sliced.ClaimWorkBatchAsync(_flow, s2, "w1", TimeSpan.FromMinutes(5), Now);
        var token = claimed!.Lease.Token;
        Assert.Equal(fresh.Select(r => r.DeliveryKey.Value).Order(), claimed.Records.Select(r => r.DeliveryKey.Value).Order());
        Assert.All(claimed.Records, r => Assert.Equal((RecordStatus.Delivering, token, 1), (r.Status, r.LeaseOwner, r.AttemptCount)));
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await sliced.RenewLeaseAsync(token, TimeSpan.FromMinutes(5), Now));
        foreach (var record in fresh)
        {
            Assert.Equal(Now.AddMinutes(5), (await ledger.GetRecordAsync(_flow, record.DeliveryKey))!.LeaseExpiresUtc);
        }

        Assert.Equal(new LeaseApplied(0, 7), await sliced.CloseLeaseAsync(token, new LeaseClosing { End = LeaseEnd.Failed, Failure = "the node stopped" }, Now));
        Assert.Equal(7, await ledger.CountAsync(_flow, s2, RecordStatus.Pending));
        foreach (var record in fresh)
        {
            var released = await ledger.GetRecordAsync(_flow, record.DeliveryKey);
            Assert.Equal(((string?)null, 0), (released!.LeaseOwner, released.AttemptCount));
        }

        // Failed records are released, and then redelivered, a slice at a time.
        await ledger.CompleteManyAsync(_flow, fresh.Select(r => new RecordCompletion
        {
            DeliveryKey = r.DeliveryKey,
            Status = RecordStatus.Failed,
            Error = "refused by the target",
            Attempt = new AttemptRecord { DeliveryKey = r.DeliveryKey, SubmissionId = s2, Worker = "w1", StartedUtc = Now, CompletedUtc = Now, Outcome = AttemptOutcome.Failed, Phase = "metadata" },
        }).ToList());
        Assert.Equal(7, await ledger.CountAsync(_flow, s2, RecordStatus.Failed));
        Assert.Equal(7, await sliced.ReleaseAsync(_flow, null, Now));
        Assert.Equal(7, await ledger.CountAsync(_flow, s2, RecordStatus.Pending));
        Assert.Equal(7, await sliced.ForceRedeliverAsync(_flow, fresh.Select(r => r.DeliveryKey), RedeliverScope.Metadata, Now));
        Assert.Equal(7, (await ledger.ListPlanRequestedAsync(_flow, null, 20)).Count);
    }

    [SkippableFact]
    public async Task Staging_leasing_and_releasing_thousands_of_records_never_lock_the_whole_record_table_on_sql_server()
    {
        // SQL Server turns a statement's row locks into a lock on the whole table once the statement holds 5,000 of them on
        // one index, and a lock on the record, event or attempt table stops every node of every flow. Each write below
        // reaches 6,000 records, so each has to run in slices.
        var ledger = await LedgerAsync(_clock);
        const int Records = 6_000;
        var s1 = Guid.NewGuid();
        var records = Enumerable.Range(0, Records).Select(i => Work($"bulk-{i:D5}", s1, $"0:{i * 10}:10", "mh", Now.AddDays(-1))).ToList();
        var before = await LockEscalationsAsync();

        Assert.Equal(Records, (await ledger.UpsertPendingAsync(_flow, records)).Staged);
        await ledger.AddWorkBatchAsync(new WorkBatchState { SubmissionId = s1, FlowId = _flow, Index = 0, Location = "work/" + _run, RecordCount = Records, CreatedUtc = Now });
        var claimed = await ledger.ClaimWorkBatchAsync(_flow, s1, "w1", TimeSpan.FromMinutes(5), Now);
        var token = claimed!.Lease.Token;
        Assert.Equal(Records, claimed.Records.Count);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await ledger.RenewLeaseAsync(token, TimeSpan.FromMinutes(5), Now));

        // A worker appends what it sent in writes of at most the journal's size; the lease applies them a slice at a time.
        foreach (var chunk in claimed.Records.Chunk(Engine.Worker.LeaseJournal.MaxEntriesPerWrite))
        {
            await ledger.AppendAsync(_flow, token, new LeaseAppend([], chunk.Select(r => Completion(r, s1, Now)).ToList()));
        }

        Assert.Equal(Records, await ledger.CountAttemptsAsync(s1, AttemptOutcome.Delivered));
        Assert.Equal(new LeaseApplied(Records, 0), await ledger.CheckpointLeaseAsync(token, Now));
        Assert.Equal(Records, await ledger.CountAsync(_flow, s1, RecordStatus.Delivered));
        Assert.Equal(LeaseApplied.None, await ledger.CloseLeaseAsync(token, new LeaseClosing { End = LeaseEnd.Done, Delivered = Records }, Now));
        Assert.Equal(Records, await ledger.ForceRedeliverAsync(_flow, records.Select(r => r.DeliveryKey), RedeliverScope.Metadata, Now));
        Assert.Equal(before, await LockEscalationsAsync());

        // The measure is live: one statement over the same records does lock the whole table, rolled back at once. The
        // database escalates only while no other session holds a lock on the table. The suite's other classes are done by
        // now, but other processes may share the database, so the statement is run again, a moment apart, until that
        // moment comes.
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            BEGIN TRANSACTION;
            UPDATE [osdu].[Record] SET [UpdatedUtc] = [UpdatedUtc] WHERE [FlowId] = @flow;
            ROLLBACK TRANSACTION;
            """;
        command.Parameters.Add(new SqlParameter("@flow", System.Data.SqlDbType.UniqueIdentifier) { Value = _flow });
        var giveUp = DateTime.UtcNow.AddMinutes(1);
        while (await LockEscalationsAsync() == before && DateTime.UtcNow < giveUp)
        {
            await command.ExecuteNonQueryAsync();
            if (await LockEscalationsAsync() == before)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
        }

        Assert.True(await LockEscalationsAsync() > before, "One statement over 6,000 records should have locked the whole record table within a minute of trying.");
    }

    [SkippableFact]
    public async Task A_record_another_staging_inserts_while_a_slice_runs_is_compared_and_never_overwritten_on_sql_server()
    {
        // Staging locks the records that exist, never a range of keys, so another staging can insert a record after a slice
        // looked for it. The table's key then refuses the slice's insert, and the slice runs again, finds the record and
        // compares its work with it. The slice is held in its claim check, which reads the submission of the flow owning one
        // of its OSDU ids, while another session that holds that submission inserts newer work for a record the slice is
        // about to insert.
        var ledger = await LedgerAsync(_clock);
        var owner = FlowId.Of("sqlserver-ledger-" + Guid.NewGuid().ToString("N"));
        var owned = Guid.NewGuid();
        await ledger.RegisterSubmissionAsync(new SubmissionState
        {
            SubmissionId = owned, FlowId = owner, FlowName = "owner-" + _run, MappingReference = "Thing@1.0.0", RenderContext = "{}",
        });
        await ledger.UpsertPendingAsync(owner, [Work("claimed", owned, "0:0:10", "mh", Now) with { FlowId = owner }]);
        var s1 = Guid.NewGuid();
        var older = Work("late", s1, "0:10:10", "mh-older", Now.AddDays(-2));

        await using var other = new SqlConnection(ConnectionString.Value);
        await other.OpenAsync();
        await using var transaction = (SqlTransaction)await other.BeginTransactionAsync();
        short session;
        await using (var hold = other.CreateCommand())
        {
            hold.Transaction = transaction;
            hold.CommandText = "SELECT [SubmissionId] FROM [osdu].[Submission] WITH (XLOCK, ROWLOCK) WHERE [SubmissionId] = @id; SELECT @@SPID;";
            hold.Parameters.Add(new SqlParameter("@id", System.Data.SqlDbType.UniqueIdentifier) { Value = owned });
            await using var reader = await hold.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(await reader.NextResultAsync() && await reader.ReadAsync());
            session = reader.GetInt16(0);
        }

        var staging = Task.Run(() => ledger.UpsertPendingAsync(_flow, [older, Work("claimed", s1, "0:0:10", "mh", Now)]));
        await WaitUntilBlockedAsync(session, staging);

        await using (var db = new OsduDbContext(OsduDbContext.SqlServerOptions(other)))
        {
            await db.Database.UseTransactionAsync(transaction);
            db.DeliveryRecords.Add(new DeliveryRecord
            {
                FlowId = _flow, DeliveryKey = older.DeliveryKey.Value, SourceKey = older.SourceKey, MappingName = older.MappingName, Status = "pending",
                TargetId = older.TargetId, ClaimedTargetId = older.TargetId, LastSubmissionId = Guid.NewGuid(), PendingDocumentRef = "9:0:10",
                PendingSourceModifiedUtc = Now.AddDays(-1), PendingMetadataHash = "mh-newer", CreatedUtc = Now, UpdatedUtc = Now,
            });
            await db.SaveChangesAsync();
        }

        await transaction.CommitAsync();
        var result = await staging;
        Assert.Equal(0, result.Staged);
        Assert.Equal(older.DeliveryKey, Assert.Single(result.Refused));
        Assert.Equal((Key("claimed"), owner), (Assert.Single(result.Conflicts).DeliveryKey, result.Conflicts[0].OwnerFlowId));
        var kept = await ledger.GetRecordAsync(_flow, older.DeliveryKey);
        Assert.Equal(("mh-newer", "9:0:10"), (kept!.PendingMetadataHash, kept.PendingDocumentRef));
    }

    [SkippableFact]
    public async Task Ledger_reads_see_the_last_committed_state_without_waiting_for_a_writer_that_holds_the_rows_on_sql_server()
    {
        var ledger = await LedgerAsync(_clock);
        var s1 = Guid.NewGuid();
        await ledger.UpsertPendingAsync(_flow, [Work("held", s1, "0:0:10", "mh", Now), Work("other", s1, "0:10:10", "mh", Now)]);
        await ledger.AddWorkBatchAsync(new WorkBatchState { SubmissionId = s1, FlowId = _flow, Index = 0, Location = "work/" + _run, RecordCount = 2, CreatedUtc = Now });

        // Another session changes the records, the batch and the flow's leases, and keeps its transaction open.
        await using var writer = new SqlConnection(ConnectionString.Value);
        await writer.OpenAsync();
        await using var transaction = (SqlTransaction)await writer.BeginTransactionAsync();
        await using (var write = writer.CreateCommand())
        {
            write.Transaction = transaction;
            write.CommandText = """
                UPDATE [osdu].[Record] SET [LastError] = N'uncommitted', [Status] = N'failed' WHERE [FlowId] = @flow;
                UPDATE [osdu].[WorkBatch] SET [Status] = N'failed' WHERE [SubmissionId] = @submission;
                INSERT INTO [osdu].[Lease] ([Token], [FlowId], [Owner], [AcquiredUtc], [ExpiresUtc]) VALUES (@token, @flow, N'uncommitted', @now, @now);
                """;
            write.Parameters.Add(new SqlParameter("@flow", System.Data.SqlDbType.UniqueIdentifier) { Value = _flow });
            write.Parameters.Add(new SqlParameter("@submission", System.Data.SqlDbType.UniqueIdentifier) { Value = s1 });
            write.Parameters.Add(new SqlParameter("@token", System.Data.SqlDbType.NVarChar, 200) { Value = "uncommitted/" + _run });
            write.Parameters.Add(new SqlParameter("@now", System.Data.SqlDbType.DateTime2) { Value = Now.AddMinutes(-1) });
            Assert.Equal(4, await write.ExecuteNonQueryAsync());
        }

        try
        {
            // A read that waited would see the writer's rows only after it commits; the writer never does.
            using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var record = await ledger.GetRecordAsync(_flow, Key("held"), patience.Token);
            Assert.Equal((RecordStatus.Pending, (string?)null), (record!.Status, record.LastError));
            Assert.Equal(2, (await ledger.ListAsync(_flow, new RecordQuery(), patience.Token)).Count);
            Assert.Equal(2, await ledger.CountAsync(_flow, s1, RecordStatus.Pending, patience.Token));
            Assert.True(await ledger.HasPendingAsync(_flow, s1, Now, patience.Token));
            Assert.Equal(WorkBatchStatus.Queued, Assert.Single(await ledger.ListWorkBatchesAsync(s1, 10, 0, patience.Token)).Status);
            Assert.Null(await ledger.NextLeaseExpiryAsync(_flow, null, patience.Token));

            // The measure is live: a read committed read of the same row does wait for the writer.
            await using var reader = new SqlConnection(ConnectionString.Value);
            await reader.OpenAsync();
            await using var read = reader.CreateCommand();
            read.CommandText = "SET LOCK_TIMEOUT 200; SELECT [LastError] FROM [osdu].[Record] WHERE [FlowId] = @flow AND [DeliveryKey] = @key;";
            read.Parameters.Add(new SqlParameter("@flow", System.Data.SqlDbType.UniqueIdentifier) { Value = _flow });
            read.Parameters.Add(new SqlParameter("@key", System.Data.SqlDbType.UniqueIdentifier) { Value = Key("held").Value });
            Assert.Equal(1222, (await Assert.ThrowsAsync<SqlException>(() => read.ExecuteScalarAsync())).Number);
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    [SkippableFact]
    public async Task A_pooled_connection_that_served_a_ledger_read_goes_back_to_read_committed_on_sql_server()
    {
        await LedgerAsync(_clock);

        // One connection in a pool of this test's own, so the connection the ledger read on is the one checked after.
        var pooled = new SqlConnectionStringBuilder(ConnectionString.Value) { MaxPoolSize = 1, ApplicationName = "osdu-ledger-isolation-" + _run }.ConnectionString;
        var ledger = new OsduLedger(() => new OsduDbContext(OsduDbContext.SqlServerOptions(pooled)), _clock);
        var s1 = Guid.NewGuid();
        await ledger.UpsertPendingAsync(_flow, [Work("isolation", s1, "0:0:10", "mh", Now)]);
        try
        {
            Assert.NotNull(await ledger.GetRecordAsync(_flow, Key("isolation")));

            await using var connection = new SqlConnection(pooled);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT [transaction_isolation_level] FROM sys.dm_exec_sessions WHERE [session_id] = @@SPID;";
            Assert.Equal((short)2, Convert.ToInt16(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            await using var connection = new SqlConnection(pooled);
            SqlConnection.ClearPool(connection);
        }
    }

    private DeliveryKey Key(string name) => DeliveryKey.Derive("sqlserver-ledger-test", [_run, name]);

    /// <summary>
    /// How many times the database has locked the whole record, event or attempt table instead of its rows. It counts an escalation on the
    /// index whose locks reached the threshold; attempts are counted on every index of a statement that holds many locks in
    /// all, and say nothing on their own.
    /// </summary>
    private static async Task<long> LockEscalationsAsync()
    {
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(SUM(s.[index_lock_promotion_count]), 0)
            FROM sys.dm_db_index_operational_stats(DB_ID(), NULL, NULL, NULL) AS s
            WHERE s.[object_id] IN (OBJECT_ID(N'osdu.Record'), OBJECT_ID(N'osdu.RecordEvent'), OBJECT_ID(N'osdu.Attempt'));
            """;
        try
        {
            return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (SqlException ex) when (ex.Number is 297 or 300)
        {
            Skip.If(true, "Counting lock escalations needs VIEW DATABASE PERFORMANCE STATE (or VIEW SERVER STATE) on the test database: " + ex.Message);
            throw;
        }
    }

    /// <summary>Waits until a request of another session is blocked by <paramref name="session"/>, failing if <paramref name="work"/> ends first.</summary>
    private static async Task WaitUntilBlockedAsync(short session, Task work)
    {
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE [blocking_session_id] = @session;";
        command.Parameters.Add(new SqlParameter("@session", System.Data.SqlDbType.Int) { Value = (int)session });
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) == 0)
        {
            if (work.IsCompleted)
            {
                await work;
                Assert.Fail("The staging finished without waiting for the session that holds the owner's submission.");
            }

            Assert.True(DateTime.UtcNow < deadline, "The staging did not reach the claim check within 30 seconds.");
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }
    }

    [SkippableFact]
    public async Task The_scope_watermark_and_the_records_waiting_to_be_planned_round_trip_on_sql_server()
    {
        var ledger = await LedgerAsync(_clock);
        var scope = "logSource=" + _run;
        var submission = Guid.NewGuid();
        await ledger.UpsertPendingAsync(_flow, [Work("w1", submission, "0:0:10", "mh", Now.AddDays(-1))]);
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
