using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The ledger's migrations run over a ledger written before them. <c>LedgerPerFlow</c>: records keyed by their flow, every
/// attempt given its record's flow, the OSDU ids already written claimed, an interrupted rollout's cursor completed, and
/// the ledgers it cannot convert refused with a message that says why. <c>LeasesAndRecordEvents</c>: the leases stopped
/// workers left behind kept as lease rows, and a revert refused while an appended event is not on its record. Each test
/// works in a database of its own, created on the server <c>SQLFLOW_TEST_DB</c> names and dropped afterwards: a migration
/// has to start from the schema it upgrades, and the suite's shared database is already past it.
/// </summary>
public sealed class SqlServerLedgerMigrationTests
{
    private const string Before = "20260916105416_RemoveManualSubmission";

    private const string BeforeLeases = "20260916133609_CoverWorkerReads";

    private const string BeforeWaits = "20260916221306_DeliveryInterfaces";

    private static readonly Lazy<string?> TestDatabase = new(() => Environment.GetEnvironmentVariable("SQLFLOW_TEST_DB"));

    private static readonly Lazy<bool> Reachable = new(() =>
    {
        var cs = TestDatabase.Value;
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

    private static readonly DateTime Now = new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);

    private static readonly Guid Logs = FlowId.Of("recall-welllog");

    private static readonly Guid Wellbores = FlowId.Of("recall-wellbore");

    [SkippableFact]
    public async Task An_existing_ledger_is_keyed_per_flow_with_every_attempt_claim_and_cursor_placed()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await database.AllowSnapshotAsync();
        await database.MigrateAsync(Before);

        var submission = Guid.NewGuid();
        var delivered = Guid.NewGuid();
        var queued = Guid.NewGuid();
        var held = Guid.NewGuid();
        var unrecorded = Guid.NewGuid();
        await database.ExecuteAsync(
            """
            INSERT INTO [osdu].[Submission] ([SubmissionId], [FlowId], [FlowName], [MappingReference], [RenderContext], [ParametersJson], [RecordCount],
                [BatchCount], [Slices], [Status], [ReceivedUtc], [Planned], [SkippedUnchanged], [AwaitingApproval], [SkippedStale], [UnchangedAtPush],
                [Blocked], [Delivered], [Held], [Failed], [Untracked], [Kind], [SourceConnection], [SourceObject])
            VALUES (@submission, @logs, N'recall-welllog', N'WellLog@1.4.0', N'{}', N'{}', 3, 1, 1, N'completed', @now, 2, 0, 0, 0, 0, 0, 1, 1, 0, 0,
                N'incremental', N'${env:OSDU_SAMPLE_DB}', N'OsduSample.ing.WellLog');
            INSERT INTO [osdu].[Record] ([DeliveryKey], [FlowId], [SourceKey], [MappingName], [Status], [AttemptCount], [PendingMetadata], [PendingPayload],
                [Blocked], [CreatedUtc], [UpdatedUtc], [TargetId], [LastDeliveredUtc], [PendingDocumentRef], [LastSubmissionId])
            VALUES
                (@delivered, @logs, N'recall:NO_15_9/L-1001', N'WellLog', N'delivered', 0, 0, 0, 0, @now, @now, N'opendes:work-product-component--WellLog:a', @now, NULL, @submission),
                (@queued, @logs, N'recall:NO_15_9/L-1002', N'WellLog', N'pending', 0, 1, 0, 0, @now, @now, N'opendes:work-product-component--WellLog:b', NULL, N'0:0:10', @submission),
                (@held, @wellbores, N'recall:WB-A', N'Wellbore', N'held', 0, 0, 0, 1, @now, @now, N'opendes:master-data--Wellbore:c', NULL, NULL, NULL);
            INSERT INTO [osdu].[Attempt] ([DeliveryKey], [SubmissionId], [Worker], [StartedUtc], [CompletedUtc], [Outcome], [Phase])
            VALUES
                (@delivered, @submission, N'w', @now, @now, N'delivered', N'metadata'),
                (@held, NULL, N'intake', @now, @now, N'held', N'render'),
                (@unrecorded, @submission, N'w', @now, @now, N'failed', N'none');
            INSERT INTO [osdu].[Activity] ([FlowId], [FlowName], [Kind], [Actor], [StartedUtc], [Outcome], [DeliveryKey])
            VALUES (@logs, N'recall-welllog', N'release', N'user:tahir', @now, N'completed', @delivered);
            INSERT INTO [osdu].[UpdateTag] ([Kind], [Scope], [TypeName], [ItemId], [Path], [Change], [ToVersion], [Mode], [Status], [SetIds],
                [AffectedRecords], [Processed], [DetectedUtc], [Cursor])
            VALUES (N'cache', N'opendes', N'Wellbore', N'opendes:master-data--Wellbore:x', N'Name', N'changed', N'v2', N'auto', N'rolling', N'1', 3, 1, @now, @held);
            """,
            ("submission", submission), ("delivered", delivered), ("queued", queued), ("held", held), ("unrecorded", unrecorded));

        await database.MigrateAsync(null);

        // Every attempt names its record's flow; one whose record is missing takes its submission's.
        await using (var db = database.Context())
        {
            var attempts = await db.DeliveryAttempts.AsNoTracking().ToDictionaryAsync(a => a.DeliveryKey, a => a.FlowId);
            Assert.Equal(Logs, attempts[delivered]);
            Assert.Equal(Wellbores, attempts[held]);
            Assert.Equal(Logs, attempts[unrecorded]);

            // The ids a record queued or delivered a document for are claimed; a record only ever held claims nothing.
            var claims = await db.DeliveryRecords.AsNoTracking().ToDictionaryAsync(r => r.DeliveryKey, r => r.ClaimedTargetId);
            Assert.Equal("opendes:work-product-component--WellLog:a", claims[delivered]);
            Assert.Equal("opendes:work-product-component--WellLog:b", claims[queued]);
            Assert.Null(claims[held]);

            // The rollout's cursor named a record by key, and now names its flow too.
            Assert.Equal(Wellbores, (await db.DeliveryUpdateTags.AsNoTracking().SingleAsync()).CursorFlowId);
        }

        Assert.Equal(["FlowId", "DeliveryKey"], await database.KeyColumnsAsync());

        // The statistics view is back, and the upgraded ledger takes a second flow's record of the same row while it
        // keeps the first flow's OSDU id for that flow.
        var ledger = new OsduLedger(database.Context, TimeProvider.System);
        Assert.Equal((2L, 1L), ((await ledger.StatsAsync(Logs, Now)).Total, (await ledger.StatsAsync(Logs, Now)).Delivered));
        var second = await ledger.UpsertPendingAsync(Wellbores, [Pending(Wellbores, delivered, "opendes:master-data--Wellbore:a")]);
        Assert.Equal((1, 0), (second.Staged, second.Conflicts.Count));
        var third = FlowId.Of("recall-welllog-copy");
        var conflict = Assert.Single((await ledger.UpsertPendingAsync(third, [Pending(third, delivered, "opendes:work-product-component--WellLog:a")])).Conflicts);
        Assert.Equal((Logs, "recall-welllog"), (conflict.OwnerFlowId, conflict.OwnerFlowName));
        Assert.Equal(2, (await ledger.LookupAsync(delivered.ToString(), 10)).Count);

        // Back down is refused while two flows hold records of one key, because the earlier ledger keeps one per key.
        var refused = await Assert.ThrowsAsync<SqlException>(() => database.MigrateAsync(Before));
        Assert.Contains("have records in more than one flow", refused.Message, StringComparison.Ordinal);
        Assert.Equal(["FlowId", "DeliveryKey"], await database.KeyColumnsAsync());

        // With one record per key again, the migration goes back down to the key it replaced.
        await database.ExecuteAsync("DELETE FROM [osdu].[Record] WHERE [FlowId] = @wellbores AND [DeliveryKey] = @delivered;", ("delivered", delivered));
        await database.MigrateAsync(Before);
        Assert.Equal(["DeliveryKey"], await database.KeyColumnsAsync());
        Assert.Equal(1L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM sys.views WHERE [name] = N'RecordCount' AND SCHEMA_NAME([schema_id]) = N'osdu';"));
    }

    [SkippableFact]
    public async Task An_attempt_that_no_record_or_submission_places_stops_the_migration_and_says_how_to_find_it()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await database.MigrateAsync(Before);
        await database.ExecuteAsync(
            """
            INSERT INTO [osdu].[Attempt] ([DeliveryKey], [SubmissionId], [Worker], [StartedUtc], [CompletedUtc], [Outcome], [Phase])
            VALUES (@orphan, NULL, N'w', @now, @now, N'failed', N'none');
            """,
            ("orphan", Guid.NewGuid()));

        var failed = await Assert.ThrowsAsync<SqlException>(() => database.MigrateAsync(null));
        Assert.Contains("1 attempt(s) in [osdu].[Attempt] belong to no record and no submission", failed.Message, StringComparison.Ordinal);
        Assert.Contains("WHERE [FlowId] IS NULL", failed.Message, StringComparison.Ordinal);

        // The migration is undone as a whole: the ledger is still the one it started from.
        Assert.Equal(["DeliveryKey"], await database.KeyColumnsAsync());
        Assert.Equal(0L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM sys.columns WHERE [object_id] = OBJECT_ID(N'[osdu].[Attempt]') AND [name] = N'FlowId';"));
    }

    [SkippableFact]
    public async Task Two_records_holding_one_osdu_id_stop_the_migration()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await database.MigrateAsync(Before);
        await database.ExecuteAsync(
            """
            INSERT INTO [osdu].[Record] ([DeliveryKey], [FlowId], [SourceKey], [MappingName], [Status], [AttemptCount], [PendingMetadata], [PendingPayload],
                [Blocked], [CreatedUtc], [UpdatedUtc], [TargetId], [LastDeliveredUtc])
            VALUES
                (@first, @logs, N'a', N'WellLog', N'delivered', 0, 0, 0, 0, @now, @now, N'opendes:work-product-component--WellLog:same', @now),
                (@second, @wellbores, N'b', N'WellLog', N'delivered', 0, 0, 0, 0, @now, @now, N'opendes:work-product-component--WellLog:same', @now);
            """,
            ("first", Guid.NewGuid()), ("second", Guid.NewGuid()));

        var failed = await Assert.ThrowsAsync<SqlException>(() => database.MigrateAsync(null));
        Assert.Contains("1 OSDU id(s) are held by more than one record", failed.Message, StringComparison.Ordinal);
        Assert.Equal(["DeliveryKey"], await database.KeyColumnsAsync());
    }

    [SkippableFact]
    public async Task Leases_in_flight_become_lease_rows_and_going_back_waits_until_every_appended_event_is_applied()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await database.AllowSnapshotAsync();
        await database.MigrateAsync(BeforeLeases);

        var submission = Guid.NewGuid();
        var run = Guid.NewGuid();
        var inBatch = Guid.NewGuid();
        var retried = Guid.NewGuid();
        var retriedLater = Guid.NewGuid();
        var idle = Guid.NewGuid();
        var batchToken = "node-a/" + Guid.NewGuid().ToString("N");
        var retryToken = "node-b/" + Guid.NewGuid().ToString("N");
        await database.ExecuteAsync(
            """
            INSERT INTO [osdu].[WorkBatch] ([SubmissionId], [Index], [FlowId], [Location], [RecordCount], [Status], [CreatedUtc], [StartedUtc], [RunId],
                [LeaseOwner], [LeaseExpiresUtc], [Delivered], [Held], [Failed], [Retrying])
            VALUES (@submission, 0, @logs, N'work/0', 1, N'running', @now, @now, @run, @batchToken, DATEADD(MINUTE, 5, @now), 0, 0, 0, 0);
            INSERT INTO [osdu].[Record] ([FlowId], [DeliveryKey], [SourceKey], [MappingName], [Status], [AttemptCount], [PendingMetadata], [PendingPayload],
                [Blocked], [CreatedUtc], [UpdatedUtc], [LastSubmissionId], [PendingDocumentRef], [WorkBatch], [LeaseOwner], [LeaseExpiresUtc])
            VALUES
                (@logs, @inBatch, N'a', N'WellLog', N'delivering', 1, 1, 0, 0, @now, @now, @submission, N'0:0:10', 0, @batchToken, DATEADD(MINUTE, 5, @now)),
                (@logs, @retried, N'b', N'WellLog', N'delivering', 2, 1, 0, 0, @now, @now, @submission, N'0:10:10', NULL, @retryToken, DATEADD(MINUTE, -1, @now)),
                (@logs, @retriedLater, N'c', N'WellLog', N'delivering', 2, 1, 0, 0, @now, DATEADD(SECOND, 1, @now), @submission, N'0:20:10', NULL, @retryToken, DATEADD(MINUTE, 1, @now)),
                (@logs, @idle, N'd', N'WellLog', N'pending', 0, 1, 0, 0, @now, @now, @submission, N'0:30:10', NULL, NULL, NULL);
            """,
            ("submission", submission), ("run", run), ("inBatch", inBatch), ("retried", retried), ("retriedLater", retriedLater), ("idle", idle),
            ("batchToken", batchToken), ("retryToken", retryToken));

        await database.MigrateAsync(null);

        // The batch's lease keeps its batch, run and expiry; the retry claim's records make one lease, expiring with the last.
        await using (var db = database.Context())
        {
            var leases = await db.DeliveryLeases.AsNoTracking().ToDictionaryAsync(l => l.Token);
            Assert.Equal(2, leases.Count);
            var batch = leases[batchToken];
            Assert.Equal(
                (Logs, (Guid?)submission, (int?)0, "node-a", (Guid?)run, Now, Now.AddMinutes(5)),
                (batch.FlowId, batch.SubmissionId, batch.WorkBatch, batch.Owner, batch.RunId, batch.AcquiredUtc, batch.ExpiresUtc));
            var retry = leases[retryToken];
            Assert.Equal(
                (Logs, (Guid?)null, (int?)null, "node-b", (Guid?)null, Now.AddSeconds(1), Now.AddMinutes(1)),
                (retry.FlowId, retry.SubmissionId, retry.WorkBatch, retry.Owner, retry.RunId, retry.AcquiredUtc, retry.ExpiresUtc));
        }

        Assert.Equal(0L, await database.ScalarAsync(
            "SELECT COUNT_BIG(*) FROM sys.columns WHERE [name] = N'LeaseExpiresUtc' AND [object_id] IN (OBJECT_ID(N'[osdu].[Record]'), OBJECT_ID(N'[osdu].[WorkBatch]'));"));

        // The ledger reads them as its own.
        var ledger = new OsduLedger(database.Context, TimeProvider.System);
        var leased = await ledger.GetRecordAsync(Logs, new DeliveryKey(retried));
        Assert.Equal((retryToken, (DateTime?)Now.AddMinutes(1)), (leased!.LeaseOwner, leased.LeaseExpiresUtc));
        Assert.Equal(Now.AddMinutes(1), await ledger.NextLeaseExpiryAsync(Logs, null));

        // A worker appends under the batch's lease, and going back is refused while that is not on the record.
        const string Step = "{\"upload\":{\"done\":\"1\"}}";
        await ledger.AppendAsync(Logs, batchToken, new LeaseAppend([new RecordStep(new DeliveryKey(inBatch), submission, "0:0:10", Step, Now)], []));
        var refused = await Assert.ThrowsAsync<SqlException>(() => database.MigrateAsync(BeforeLeases));
        Assert.Contains("1 delivery event(s) in osdu.RecordEvent have not been applied to their records", refused.Message, StringComparison.Ordinal);
        Assert.Equal(1L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM [osdu].[RecordEvent];"));

        // Once the lease applies it, the migration goes back and puts each lease's expiry on its batch and its records.
        Assert.Equal(LeaseApplied.None, await ledger.CheckpointLeaseAsync(batchToken, Now));
        Assert.Equal(Step, (await ledger.GetRecordAsync(Logs, new DeliveryKey(inBatch)))!.PendingStepJson);
        await database.MigrateAsync(BeforeLeases);
        const string Expiry = "SELECT DATEDIFF(SECOND, @now, [LeaseExpiresUtc]) FROM [osdu].[Record] WHERE [DeliveryKey] = @key;";
        Assert.Equal(300L, await database.ScalarAsync(Expiry, ("key", inBatch)));
        Assert.Equal(60L, await database.ScalarAsync(Expiry, ("key", retried)));
        Assert.Equal(60L, await database.ScalarAsync(Expiry, ("key", retriedLater)));
        Assert.Equal(1L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM [osdu].[Record] WHERE [DeliveryKey] = @key AND [LeaseExpiresUtc] IS NULL;", ("key", idle)));
        Assert.Equal(300L, await database.ScalarAsync("SELECT DATEDIFF(SECOND, @now, [LeaseExpiresUtc]) FROM [osdu].[WorkBatch] WHERE [SubmissionId] = @submission;", ("submission", submission)));
        Assert.Equal(0L, await database.ScalarAsync(
            "SELECT COUNT_BIG(*) FROM sys.tables WHERE [name] IN (N'Lease', N'RecordEvent') AND SCHEMA_NAME([schema_id]) = N'osdu';"));
    }

    /// <summary>
    /// The ledger asks nothing of the database beyond its own schema. It used to read every listing, wait and claim in
    /// a snapshot transaction, so a database that did not allow snapshot isolation stopped every node from claiming any
    /// work at all: a setting the product never set and never checked, on a database the control plane creates itself.
    /// A worker writes the record table only when it claims, checkpoints, closes or recovers a lease, and a record and
    /// its lease are read in one statement, so the reads need no isolation level of their own.
    /// </summary>
    [SkippableFact]
    public async Task A_ledger_claims_delivers_and_lists_on_a_database_that_does_not_allow_snapshot_isolation()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await database.MigrateAsync(null);
        Assert.Equal(0L, await database.ScalarAsync(
            $"SELECT COUNT_BIG(*) FROM sys.databases WHERE [name] = N'{database.Name}' AND [snapshot_isolation_state] = 1;"));

        var ledger = new OsduLedger(database.Context, TimeProvider.System);
        var key = Guid.NewGuid();
        Assert.Equal(1, (await ledger.UpsertPendingAsync(Logs, [Pending(Logs, key, "opendes:work-product-component--WellLog:s")])).Staged);

        // The claim is the read path that used to fail first, before a node took any work.
        var claimed = await ledger.ClaimAsync(Logs, null, "w1", 10, TimeSpan.FromMinutes(5), Now);
        Assert.Single(claimed.Records);
        Assert.Equal(1L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM [osdu].[Lease];"));

        // And the listing reads the record with the expiry of the lease that holds it, in one statement.
        var listed = await ledger.GetRecordAsync(Logs, new DeliveryKey(key));
        Assert.NotNull(listed);
        Assert.Equal(RecordStatus.Delivering, listed.Status);
        Assert.NotNull(listed.LeaseExpiresUtc);
    }

    [SkippableFact]
    public async Task A_ledger_written_before_records_could_wait_takes_the_columns_and_waits_after_the_migration()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await database.AllowSnapshotAsync();
        await database.MigrateAsync(BeforeWaits);

        // A record written by the schema before waits existed: it refers to nothing, because nothing recorded what it refers to.
        var wellbore = Guid.NewGuid();
        await database.ExecuteAsync(
            """
            INSERT INTO [osdu].[Record] ([DeliveryKey], [FlowId], [SourceKey], [MappingName], [Status], [AttemptCount], [PendingMetadata], [PendingPayload],
                [Blocked], [CreatedUtc], [UpdatedUtc], [TargetId], [ClaimedTargetId], [PendingDocumentRef])
            VALUES (@wellbore, @wellbores, N'recall:WB-A', N'Wellbore', N'pending', 0, 1, 0, 0, @now, @now,
                N'opendes:master-data--Wellbore:a', N'opendes:master-data--Wellbore:a', N'0:0:10');
            """,
            ("wellbore", wellbore));

        await database.MigrateAsync(null);

        // The columns are there and empty, and the ledger waits on the upgraded database as it does on a new one.
        Assert.Equal(0L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM [osdu].[Record] WHERE [PendingReferences] IS NOT NULL OR [WaitingFor] IS NOT NULL;"));
        Assert.Equal(0L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM [osdu].[Submission] WHERE [Waiting] <> 0;"));
        var ledger = new OsduLedger(database.Context, TimeProvider.System);
        var log = Guid.NewGuid();
        Assert.Equal(1, (await ledger.UpsertPendingAsync(
            Logs,
            [Pending(Logs, log, "opendes:work-product-component--WellLog:s") with
            {
                PendingReferences = [new RecordReference("opendes:master-data--Wellbore:a", "data.WellboreID")],
            }])).Staged);

        var claim = await ledger.ClaimAsync(Logs, null, "w1", 10, TimeSpan.FromMinutes(5), Now);
        Assert.Empty(claim.Records);
        Assert.Equal("opendes:master-data--Wellbore:a", Assert.Single(claim.Waiting).WaitingFor);
        Assert.Equal(1, (await ledger.StatsAsync(Logs, Now)).Waiting);

        // The record written before the migration still delivers, and its delivery releases what waited for it.
        var wellboreClaim = await ledger.ClaimAsync(Wellbores, null, "w2", 10, TimeSpan.FromMinutes(5), Now);
        await ledger.CompleteAsync(Wellbores, new RecordCompletion
        {
            DeliveryKey = new DeliveryKey(wellbore),
            Status = RecordStatus.Delivered,
            Promote = true,
            TargetVersion = 1,
            Claimed = ClaimedWork.Of(Assert.Single(wellboreClaim.Records)),
            Attempt = new AttemptRecord
            {
                DeliveryKey = new DeliveryKey(wellbore),
                Worker = "w2",
                StartedUtc = Now,
                CompletedUtc = Now,
                Outcome = AttemptOutcome.Delivered,
                Phase = "metadata",
            },
        });

        Assert.Equal(RecordStatus.Pending, (await ledger.GetRecordAsync(Logs, new DeliveryKey(log)))!.Status);
    }

    private static RecordState Pending(Guid flow, Guid key, string targetId) => new()
    {
        DeliveryKey = new DeliveryKey(key),
        FlowId = flow,
        SourceKey = "recall:NO_15_9/L-1001",
        MappingName = "Wellbore",
        TargetId = targetId,
        LastSubmissionId = Guid.NewGuid(),
        PendingDocumentRef = "0:0:10",
        PendingRenderContext = "{}",
        PendingMetadataHash = "mh",
        PendingMetadata = true,
    };

    /// <summary>A database of the test's own on the test server, dropped with everything in it when the test ends.</summary>
    private sealed class ScratchDatabase : IAsyncDisposable
    {
        private readonly string _master;

        private ScratchDatabase(string master, string connectionString, string name)
        {
            _master = master;
            ConnectionString = connectionString;
            Name = name;
        }

        public string ConnectionString { get; }

        public string Name { get; }

        public static async Task<ScratchDatabase> CreateAsync()
        {
            Skip.IfNot(
                Reachable.Value,
                "The ledger migration tests need a reachable SQL Server whose login may create databases. Set SQLFLOW_TEST_DB, for example through the git-ignored .sqlflow/env file.");
            var name = "osdu_ledger_migration_" + Guid.NewGuid().ToString("N")[..12];
            var master = new SqlConnectionStringBuilder(TestDatabase.Value!) { InitialCatalog = "master" }.ConnectionString;
            await using (var connection = new SqlConnection(master))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = $"CREATE DATABASE [{name}];";
                await command.ExecuteNonQueryAsync();
            }

            return new ScratchDatabase(master, new SqlConnectionStringBuilder(TestDatabase.Value!) { InitialCatalog = name }.ConnectionString, name);
        }

        public OsduDbContext Context() => new(OsduDbContext.SqlServerOptions(ConnectionString));

        /// <summary>Lets the database run snapshot transactions, as the ledger's reads need.</summary>
        public async Task AllowSnapshotAsync()
        {
            await using var connection = new SqlConnection(_master);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"ALTER DATABASE [{Name}] SET ALLOW_SNAPSHOT_ISOLATION ON;";
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>Migrates to <paramref name="target"/>, up or down, or to the newest migration when it is null.</summary>
        public async Task MigrateAsync(string? target)
        {
            await using var db = Context();
            if (target is null)
            {
                await db.Database.MigrateAsync();
            }
            else
            {
                await db.GetService<IMigrator>().MigrateAsync(target);
            }
        }

        /// <summary>Runs a batch with the test's flows and clock as <c>@logs</c>, <c>@wellbores</c> and <c>@now</c>, and the given values.</summary>
        public async Task ExecuteAsync(string sql, params (string Name, object Value)[] values)
        {
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = Command(connection, sql, values);
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>A number the query answers, with the same parameters as <see cref="ExecuteAsync"/>.</summary>
        public async Task<long> ScalarAsync(string sql, params (string Name, object Value)[] values)
        {
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = Command(connection, sql, values);
            return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        private static SqlCommand Command(SqlConnection connection, string sql, (string Name, object Value)[] values)
        {
            var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.Add(new SqlParameter("@logs", System.Data.SqlDbType.UniqueIdentifier) { Value = Logs });
            command.Parameters.Add(new SqlParameter("@wellbores", System.Data.SqlDbType.UniqueIdentifier) { Value = Wellbores });
            command.Parameters.Add(new SqlParameter("@now", System.Data.SqlDbType.DateTime2) { Value = Now });
            foreach (var (name, value) in values)
            {
                command.Parameters.Add(value switch
                {
                    Guid id => new SqlParameter("@" + name, System.Data.SqlDbType.UniqueIdentifier) { Value = id },
                    string text => new SqlParameter("@" + name, System.Data.SqlDbType.NVarChar, 4000) { Value = text },
                    _ => throw new ArgumentException($"The parameter @{name} is a {value.GetType().Name}; the scratch database takes ids and text.", nameof(values)),
                });
            }

            return command;
        }

        /// <summary>The columns of the record table's primary key, in key order.</summary>
        public async Task<IReadOnlyList<string>> KeyColumnsAsync()
        {
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT c.[name]
                FROM sys.indexes AS i
                INNER JOIN sys.index_columns AS ic ON ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
                INNER JOIN sys.columns AS c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                WHERE i.[object_id] = OBJECT_ID(N'[osdu].[Record]') AND i.[is_primary_key] = 1
                ORDER BY ic.[key_ordinal];
                """;
            var columns = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(0));
            }

            return columns;
        }

        public async ValueTask DisposeAsync()
        {
            using (var pooled = new SqlConnection(ConnectionString))
            {
                SqlConnection.ClearPool(pooled);
            }

            await using var connection = new SqlConnection(_master);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"ALTER DATABASE [{Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Name}];";
            await command.ExecuteNonQueryAsync();
        }
    }
}
