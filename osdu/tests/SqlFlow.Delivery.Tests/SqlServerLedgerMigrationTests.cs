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
/// The <c>LedgerPerFlow</c> migration run over a ledger written before it: records keyed by their flow, every attempt
/// given its record's flow, the OSDU ids already written claimed, an interrupted rollout's cursor completed, and the
/// ledgers it cannot convert refused with a message that says why. Each test works in a database of its own, created on
/// the server <c>SQLFLOW_TEST_DB</c> names and dropped afterwards: the migration has to start from the schema it upgrades,
/// and the suite's shared database is already past it.
/// </summary>
public sealed class SqlServerLedgerMigrationTests
{
    private const string Before = "20260916105416_RemoveManualSubmission";

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

        /// <summary>Runs a batch with the test's flows and clock as <c>@logs</c>, <c>@wellbores</c> and <c>@now</c>, and the given ids.</summary>
        public async Task ExecuteAsync(string sql, params (string Name, Guid Value)[] ids)
        {
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.Add(new SqlParameter("@logs", System.Data.SqlDbType.UniqueIdentifier) { Value = Logs });
            command.Parameters.Add(new SqlParameter("@wellbores", System.Data.SqlDbType.UniqueIdentifier) { Value = Wellbores });
            command.Parameters.Add(new SqlParameter("@now", System.Data.SqlDbType.DateTime2) { Value = Now });
            foreach (var (name, value) in ids)
            {
                command.Parameters.Add(new SqlParameter("@" + name, System.Data.SqlDbType.UniqueIdentifier) { Value = value });
            }

            await command.ExecuteNonQueryAsync();
        }

        public async Task<long> ScalarAsync(string sql)
        {
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
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
