using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The two writes that carry the volume of a submission (staging the pending records, and closing the records of a
/// drained batch with their attempts), done as one bulk copy into a staging table plus set-based statements when the
/// module database is SQL Server (design.md section 16.2). Every other provider takes the entity path in
/// <see cref="OsduLedger"/>, which is the same write row by row. Both run under the context's retrying execution
/// strategy inside one transaction, so a batch is staged whole or not at all. Both write one flow's records: a record is
/// its flow and its delivery key together, and every statement matches on both.
/// </summary>
internal static class SqlServerLedgerBulk
{
    public const string ProviderName = "Microsoft.EntityFrameworkCore.SqlServer";

    private const int BulkBatchSize = 5000;

    private const int BulkTimeoutSeconds = 600;

    /// <summary>The unique index through which the database refuses a second flow's claim on an OSDU id.</summary>
    private const string ClaimIndex = "IX_Record_ClaimedTargetId";

    /// <summary>How many times staging is tried when a concurrent intake of another flow got there first.</summary>
    private const int ClaimRaceAttempts = 3;

    public static bool Applies(OsduDbContext db) => string.Equals(db.Database.ProviderName, ProviderName, StringComparison.Ordinal);

    private const string PendingStageSql = """
        CREATE TABLE #PendingStage (
            [FlowId] uniqueidentifier NOT NULL,
            [DeliveryKey] uniqueidentifier NOT NULL,
            [SourceKey] nvarchar(400) NOT NULL,
            [SourceKeyJson] nvarchar(2000) NULL,
            [Label] nvarchar(400) NULL,
            [MappingName] nvarchar(200) NOT NULL,
            [TargetId] nvarchar(500) NULL,
            [LastSubmissionId] uniqueidentifier NULL,
            [PendingDocumentRef] nvarchar(64) NULL,
            [WorkBatch] int NULL,
            [PendingRenderContext] nvarchar(max) NULL,
            [PendingSourceFingerprint] nvarchar(200) NULL,
            [PendingSourceModifiedUtc] datetime2 NULL,
            [PendingSourceFileName] nvarchar(800) NULL,
            [PendingSourceRowNumber] bigint NULL,
            [PendingSourceUpdatedUtc] datetime2 NULL,
            [PendingMetadataHash] nvarchar(64) NULL,
            [PendingPayloadHash] nvarchar(64) NULL,
            [PendingPayloadModifiedUtc] datetime2 NULL,
            [PendingPayloadLocation] nvarchar(2000) NULL,
            [PendingMetadata] bit NOT NULL,
            [PendingPayload] bit NOT NULL,
            [CacheSetId] bigint NULL,
            PRIMARY KEY ([FlowId], [DeliveryKey]));
        """;

    // Work older than what the record already holds, delivered or queued, is taken out of the stage and named before
    // the merge. The update locks are held to the end of the transaction, so no concurrent intake can stage a newer
    // version between this test and the write. A comparison with a NULL column is unknown, and refuses nothing.
    private const string RefuseOlderSql = """
        DELETE s
        OUTPUT deleted.[DeliveryKey]
        FROM #PendingStage AS s
        INNER JOIN [osdu].[Record] AS t WITH (UPDLOCK, HOLDLOCK) ON t.[FlowId] = s.[FlowId] AND t.[DeliveryKey] = s.[DeliveryKey]
        CROSS APPLY (SELECT CASE WHEN t.[PendingDocumentRef] IS NOT NULL AND t.[Status] IN (N'pending', N'delivering') THEN 1 ELSE 0 END AS [Queued]) AS q
        WHERE (s.[PendingSourceModifiedUtc] IS NOT NULL
                AND (s.[PendingSourceModifiedUtc] < t.[SourceModifiedUtc]
                     OR (q.[Queued] = 1 AND s.[PendingSourceModifiedUtc] < t.[PendingSourceModifiedUtc])))
           OR (s.[PendingPayload] = 1 AND s.[PendingPayloadModifiedUtc] IS NOT NULL
                AND (s.[PendingPayloadModifiedUtc] < t.[PayloadModifiedUtc]
                     OR (q.[Queued] = 1 AND t.[PendingPayload] = 1 AND s.[PendingPayloadModifiedUtc] < t.[PendingPayloadModifiedUtc])));
        """;

    // A record keeps the OSDU id it was first given, so that id, not the one this work was rendered with, is the one the
    // work is delivered to and the one the claim check below compares.
    private const string CarriedTargetSql = """
        UPDATE s SET [TargetId] = t.[TargetId]
        FROM #PendingStage AS s
        INNER JOIN [osdu].[Record] AS t ON t.[FlowId] = s.[FlowId] AND t.[DeliveryKey] = s.[DeliveryKey]
        WHERE t.[TargetId] IS NOT NULL;
        """;

    // Work for an OSDU id another flow's record has claimed is taken out of the stage and named, with the owning flow
    // and its name as its last submission recorded it. Ids compare exactly (the claim column's binary collation), and
    // each stage row is one seek of the filtered claim index, whose predicate the query repeats so the index always
    // applies. The unique index on the claim is what settles a race between two flows' intakes; this read names the owner.
    private const string RefuseClaimedSql = """
        DELETE s
        OUTPUT deleted.[DeliveryKey], deleted.[TargetId], o.[FlowId], o.[FlowName]
        FROM #PendingStage AS s
        CROSS APPLY (
            SELECT TOP (1) t.[FlowId], sub.[FlowName]
            FROM [osdu].[Record] AS t
            LEFT JOIN [osdu].[Submission] AS sub ON sub.[SubmissionId] = t.[LastSubmissionId]
            WHERE t.[ClaimedTargetId] IS NOT NULL
              AND t.[ClaimedTargetId] = s.[TargetId] COLLATE Latin1_General_100_BIN2
              AND t.[FlowId] <> s.[FlowId]) AS o
        WHERE s.[TargetId] IS NOT NULL;
        """;

    // A record being delivered right now keeps its status, lease, retry count and last error: the new work queues
    // behind the delivery, whose completion leaves it pending. Every right-hand side reads the row as it was. Staging
    // answers a request to plan the record again, so the request is cleared, and claims the record's OSDU id for its
    // flow the first time it queues a document.
    private const string PendingMergeSql = """
        MERGE [osdu].[Record] WITH (HOLDLOCK) AS t
        USING #PendingStage AS s ON t.[FlowId] = s.[FlowId] AND t.[DeliveryKey] = s.[DeliveryKey]
        WHEN MATCHED THEN
            UPDATE SET
                [SourceKey] = s.[SourceKey], [SourceKeyJson] = COALESCE(s.[SourceKeyJson], t.[SourceKeyJson]), [Label] = s.[Label], [MappingName] = s.[MappingName],
                [TargetId] = COALESCE(t.[TargetId], s.[TargetId]),
                [ClaimedTargetId] = COALESCE(t.[ClaimedTargetId], s.[TargetId] COLLATE Latin1_General_100_BIN2),
                [LastSubmissionId] = s.[LastSubmissionId], [NextAttemptUtc] = NULL,
                [Status] = CASE WHEN t.[Status] = N'delivering' AND t.[LeaseExpiresUtc] > @now THEN t.[Status] ELSE N'pending' END,
                [AttemptCount] = CASE WHEN t.[Status] = N'delivering' AND t.[LeaseExpiresUtc] > @now THEN t.[AttemptCount] ELSE 0 END,
                [LastError] = CASE WHEN t.[Status] = N'delivering' AND t.[LeaseExpiresUtc] > @now THEN t.[LastError] ELSE NULL END,
                [LeaseOwner] = CASE WHEN t.[Status] = N'delivering' AND t.[LeaseExpiresUtc] > @now THEN t.[LeaseOwner] ELSE NULL END,
                [LeaseExpiresUtc] = CASE WHEN t.[Status] = N'delivering' AND t.[LeaseExpiresUtc] > @now THEN t.[LeaseExpiresUtc] ELSE NULL END,
                [PendingDocumentRef] = s.[PendingDocumentRef], [WorkBatch] = s.[WorkBatch], [PendingStepJson] = NULL,
                [PendingRenderContext] = s.[PendingRenderContext], [PendingSourceFingerprint] = s.[PendingSourceFingerprint],
                [PendingSourceModifiedUtc] = s.[PendingSourceModifiedUtc],
                [PendingSourceFileName] = s.[PendingSourceFileName], [PendingSourceRowNumber] = s.[PendingSourceRowNumber],
                [PendingSourceUpdatedUtc] = s.[PendingSourceUpdatedUtc],
                [PendingMetadataHash] = s.[PendingMetadataHash], [PendingPayloadHash] = s.[PendingPayloadHash],
                [PendingPayloadModifiedUtc] = s.[PendingPayloadModifiedUtc],
                [PendingPayloadLocation] = s.[PendingPayloadLocation], [PendingMetadata] = s.[PendingMetadata], [PendingPayload] = s.[PendingPayload],
                [CacheSetId] = s.[CacheSetId], [Blocked] = 0, [PlanRequestedUtc] = NULL, [UpdatedUtc] = @now
        WHEN NOT MATCHED BY TARGET THEN
            INSERT ([DeliveryKey], [FlowId], [SourceKey], [SourceKeyJson], [Label], [MappingName], [TargetId], [ClaimedTargetId], [Status], [LastSubmissionId], [AttemptCount],
                    [PendingDocumentRef], [WorkBatch], [PendingRenderContext], [PendingSourceFingerprint], [PendingSourceModifiedUtc],
                    [PendingSourceFileName], [PendingSourceRowNumber], [PendingSourceUpdatedUtc],
                    [PendingMetadataHash], [PendingPayloadHash], [PendingPayloadModifiedUtc],
                    [PendingPayloadLocation], [PendingMetadata], [PendingPayload], [CacheSetId], [Blocked], [CreatedUtc], [UpdatedUtc])
            VALUES (s.[DeliveryKey], s.[FlowId], s.[SourceKey], s.[SourceKeyJson], s.[Label], s.[MappingName], s.[TargetId], s.[TargetId] COLLATE Latin1_General_100_BIN2, N'pending', s.[LastSubmissionId], 0,
                    s.[PendingDocumentRef], s.[WorkBatch], s.[PendingRenderContext], s.[PendingSourceFingerprint], s.[PendingSourceModifiedUtc],
                    s.[PendingSourceFileName], s.[PendingSourceRowNumber], s.[PendingSourceUpdatedUtc],
                    s.[PendingMetadataHash], s.[PendingPayloadHash], s.[PendingPayloadModifiedUtc],
                    s.[PendingPayloadLocation], s.[PendingMetadata], s.[PendingPayload], s.[CacheSetId], 0, @now, @now);
        SELECT @@ROWCOUNT;
        """;

    private const string CompletionStageSql = """
        CREATE TABLE #CompletionStage (
            [DeliveryKey] uniqueidentifier NOT NULL PRIMARY KEY,
            [Status] nvarchar(16) NOT NULL,
            [Blocked] bit NOT NULL,
            [Promote] bit NOT NULL,
            [NothingSent] bit NOT NULL,
            [NextAttemptUtc] datetime2 NULL,
            [LastError] nvarchar(2000) NULL,
            [TargetId] nvarchar(500) NULL,
            [TargetVersion] bigint NULL,
            [HasTargetState] bit NOT NULL,
            [TargetStateJson] nvarchar(max) NULL,
            [PendingStepJson] nvarchar(max) NULL,
            [HasClaim] bit NOT NULL,
            [ClaimSubmissionId] uniqueidentifier NULL,
            [ClaimDocumentRef] nvarchar(64) NULL,
            [ClaimRenderContext] nvarchar(max) NULL,
            [ClaimSourceFingerprint] nvarchar(200) NULL,
            [ClaimSourceModifiedUtc] datetime2 NULL,
            [ClaimSourceFileName] nvarchar(800) NULL,
            [ClaimSourceRowNumber] bigint NULL,
            [ClaimSourceUpdatedUtc] datetime2 NULL,
            [ClaimMetadataHash] nvarchar(64) NULL,
            [ClaimPayloadHash] nvarchar(64) NULL,
            [ClaimPayloadModifiedUtc] datetime2 NULL,
            [ClaimMetadata] bit NOT NULL,
            [ClaimPayload] bit NOT NULL);
        """;

    // The same write as OsduLedger.ApplyCompletion. A record now carrying other pending work than the try claimed
    // (newer work queued behind it) promotes what the try delivered from the claim and goes back to pending, keeping
    // the newer work and its step progress; any other record settles as the completion says, promoting its own
    // pending columns. A promotion carries the origin of the version it delivered: the claim's when superseded, the
    // pending origin otherwise, and only when the work names one.
    private const string CompletionUpdateSql = """
        UPDATE r SET
            [Status] = CASE WHEN x.[Superseded] = 1 THEN N'pending' ELSE s.[Status] END,
            [Blocked] = CASE WHEN x.[Superseded] = 1 THEN CAST(0 AS bit) ELSE s.[Blocked] END,
            [LeaseOwner] = NULL, [LeaseExpiresUtc] = NULL, [UpdatedUtc] = @now,
            [NextAttemptUtc] = CASE WHEN x.[Superseded] = 1 THEN NULL ELSE s.[NextAttemptUtc] END,
            [LastError] = CASE WHEN x.[Superseded] = 1 THEN NULL ELSE s.[LastError] END,
            [TargetId] = COALESCE(s.[TargetId], r.[TargetId]), [TargetVersion] = COALESCE(s.[TargetVersion], r.[TargetVersion]),
            [TargetStateJson] = CASE WHEN s.[HasTargetState] = 1 THEN s.[TargetStateJson] ELSE r.[TargetStateJson] END,
            [PendingStepJson] = CASE WHEN x.[Superseded] = 1 THEN r.[PendingStepJson] ELSE s.[PendingStepJson] END,
            [RenderContext] = CASE WHEN s.[Promote] = 0 THEN r.[RenderContext]
                WHEN x.[Superseded] = 1 THEN COALESCE(s.[ClaimRenderContext], r.[RenderContext])
                ELSE COALESCE(r.[PendingRenderContext], r.[RenderContext]) END,
            [SourceFingerprint] = CASE WHEN s.[Promote] = 0 THEN r.[SourceFingerprint]
                WHEN x.[Superseded] = 1 THEN COALESCE(s.[ClaimSourceFingerprint], r.[SourceFingerprint])
                ELSE COALESCE(r.[PendingSourceFingerprint], r.[SourceFingerprint]) END,
            [SourceModifiedUtc] = CASE WHEN s.[Promote] = 0 THEN r.[SourceModifiedUtc]
                WHEN x.[Superseded] = 1 THEN COALESCE(s.[ClaimSourceModifiedUtc], r.[SourceModifiedUtc])
                ELSE COALESCE(r.[PendingSourceModifiedUtc], r.[SourceModifiedUtc]) END,
            [SourceFileName] = CASE WHEN s.[Promote] = 0 THEN r.[SourceFileName]
                WHEN x.[Superseded] = 1 THEN CASE WHEN s.[ClaimSourceUpdatedUtc] IS NULL AND s.[ClaimSourceFileName] IS NULL THEN r.[SourceFileName] ELSE s.[ClaimSourceFileName] END
                WHEN r.[PendingSourceUpdatedUtc] IS NULL AND r.[PendingSourceFileName] IS NULL THEN r.[SourceFileName]
                ELSE r.[PendingSourceFileName] END,
            [SourceRowNumber] = CASE WHEN s.[Promote] = 0 THEN r.[SourceRowNumber]
                WHEN x.[Superseded] = 1 THEN CASE WHEN s.[ClaimSourceUpdatedUtc] IS NULL AND s.[ClaimSourceFileName] IS NULL THEN r.[SourceRowNumber] ELSE s.[ClaimSourceRowNumber] END
                WHEN r.[PendingSourceUpdatedUtc] IS NULL AND r.[PendingSourceFileName] IS NULL THEN r.[SourceRowNumber]
                ELSE r.[PendingSourceRowNumber] END,
            [SourceUpdatedUtc] = CASE WHEN s.[Promote] = 0 THEN r.[SourceUpdatedUtc]
                WHEN x.[Superseded] = 1 THEN CASE WHEN s.[ClaimSourceUpdatedUtc] IS NULL AND s.[ClaimSourceFileName] IS NULL THEN r.[SourceUpdatedUtc] ELSE s.[ClaimSourceUpdatedUtc] END
                WHEN r.[PendingSourceUpdatedUtc] IS NULL AND r.[PendingSourceFileName] IS NULL THEN r.[SourceUpdatedUtc]
                ELSE r.[PendingSourceUpdatedUtc] END,
            [MetadataHash] = CASE WHEN s.[Promote] = 0 THEN r.[MetadataHash]
                WHEN x.[Superseded] = 1 THEN CASE WHEN s.[ClaimMetadata] = 1 THEN s.[ClaimMetadataHash] ELSE r.[MetadataHash] END
                WHEN r.[PendingMetadata] = 1 THEN r.[PendingMetadataHash] ELSE r.[MetadataHash] END,
            [PayloadHash] = CASE WHEN s.[Promote] = 0 THEN r.[PayloadHash]
                WHEN x.[Superseded] = 1 THEN CASE WHEN s.[ClaimPayload] = 1 THEN s.[ClaimPayloadHash] ELSE r.[PayloadHash] END
                WHEN r.[PendingPayload] = 1 THEN r.[PendingPayloadHash] ELSE r.[PayloadHash] END,
            [PayloadModifiedUtc] = CASE WHEN s.[Promote] = 0 THEN r.[PayloadModifiedUtc]
                WHEN x.[Superseded] = 1 THEN CASE WHEN s.[ClaimPayload] = 1 THEN COALESCE(s.[ClaimPayloadModifiedUtc], r.[PayloadModifiedUtc]) ELSE r.[PayloadModifiedUtc] END
                WHEN r.[PendingPayload] = 1 THEN COALESCE(r.[PendingPayloadModifiedUtc], r.[PayloadModifiedUtc]) ELSE r.[PayloadModifiedUtc] END,
            [LastDeliveredUtc] = CASE WHEN s.[Promote] = 1 AND s.[NothingSent] = 0 THEN @now ELSE r.[LastDeliveredUtc] END,
            [LastVerifiedUtc] = CASE WHEN s.[Promote] = 1 AND s.[NothingSent] = 0 THEN NULL ELSE r.[LastVerifiedUtc] END,
            [LastVerifyOutcome] = CASE WHEN s.[Promote] = 1 AND s.[NothingSent] = 0 THEN NULL ELSE r.[LastVerifyOutcome] END,
            [PendingDocumentRef] = CASE WHEN s.[Promote] = 1 AND x.[Superseded] = 0 THEN NULL ELSE r.[PendingDocumentRef] END,
            [WorkBatch] = CASE WHEN s.[Promote] = 1 AND x.[Superseded] = 0 THEN NULL ELSE r.[WorkBatch] END,
            [PendingMetadata] = CASE WHEN s.[Promote] = 1 AND x.[Superseded] = 0 THEN CAST(0 AS bit) ELSE r.[PendingMetadata] END,
            [PendingPayload] = CASE WHEN s.[Promote] = 1 AND x.[Superseded] = 0 THEN CAST(0 AS bit) ELSE r.[PendingPayload] END,
            [PendingPayloadLocation] = CASE WHEN s.[Promote] = 1 AND x.[Superseded] = 0 THEN NULL ELSE r.[PendingPayloadLocation] END,
            [AttemptCount] = CASE WHEN s.[Promote] = 1 OR x.[Superseded] = 1 THEN 0 ELSE r.[AttemptCount] END
        FROM [osdu].[Record] AS r
        INNER JOIN #CompletionStage AS s ON r.[FlowId] = @flowId AND r.[DeliveryKey] = s.[DeliveryKey]
        CROSS APPLY (SELECT CASE
            WHEN s.[HasClaim] = 1 AND r.[PendingDocumentRef] IS NOT NULL
                 AND (r.[PendingDocumentRef] <> s.[ClaimDocumentRef]
                      OR r.[LastSubmissionId] <> s.[ClaimSubmissionId]
                      OR (r.[LastSubmissionId] IS NULL AND s.[ClaimSubmissionId] IS NOT NULL)
                      OR (r.[LastSubmissionId] IS NOT NULL AND s.[ClaimSubmissionId] IS NULL))
            THEN 1 ELSE 0 END AS [Superseded]) AS x;
        """;

    /// <summary>
    /// Stages one flow's pending work. When another flow's intake claims one of the same OSDU ids between this staging's
    /// claim check and its write, the database refuses the write through the claim's unique index and nothing is kept;
    /// the staging is then run again, and its check now names the id as the other flow's. A staging the database chose
    /// as a deadlock victim while intakes of several flows contended is rolled back whole, and is run again the same way.
    /// </summary>
    public static async Task<PendingStaging> UpsertPendingAsync(OsduDbContext db, Guid flowId, IReadOnlyList<RecordState> records, DateTime now, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await InTransactionAsync(db, (connection, transaction) => StagePendingAsync(connection, transaction, flowId, records, now, ct), ct).ConfigureAwait(false);
            }
            catch (SqlException ex) when (IsContention(ex) && attempt < ClaimRaceAttempts)
            {
                // Another flow's staging committed first; the next try reads what it left.
            }
        }
    }

    private static async Task<PendingStaging> StagePendingAsync(
        SqlConnection connection, SqlTransaction transaction, Guid flowId, IReadOnlyList<RecordState> records, DateTime now, CancellationToken ct)
    {
        await ExecuteAsync(connection, transaction, PendingStageSql, ct).ConfigureAwait(false);
        using (var table = PendingTable(flowId, records))
        {
            await BulkCopyAsync(connection, transaction, "#PendingStage", table, ct).ConfigureAwait(false);
        }

        var refused = await KeysAsync(connection, transaction, RefuseOlderSql, ct).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, CarriedTargetSql, ct).ConfigureAwait(false);
        var conflicts = await ConflictsAsync(connection, transaction, ct).ConfigureAwait(false);
        var staged = await ScalarAsync(connection, transaction, PendingMergeSql, now, flowId, ct).ConfigureAwait(false);
        return new PendingStaging(staged, refused, conflicts);
    }

    /// <summary>
    /// Whether a staging failed only because another intake got there first: the claim's unique index refused an id
    /// another flow's record claimed meanwhile (2601, 2627), or the database ended a deadlock by rolling this one back (1205).
    /// </summary>
    private static bool IsContention(SqlException ex)
        => ex.Errors.Cast<SqlError>().Any(e => e.Number == 1205 || (e.Number is 2601 or 2627 && e.Message.Contains(ClaimIndex, StringComparison.Ordinal)));

    public static Task<int> CompleteManyAsync(OsduDbContext db, Guid flowId, IReadOnlyList<RecordCompletion> completions, DateTime now, CancellationToken ct)
        => InTransactionAsync(db, async (connection, transaction) =>
        {
            using (var attempts = AttemptTable(flowId, completions))
            {
                await BulkCopyAsync(connection, transaction, "[osdu].[Attempt]", attempts, ct).ConfigureAwait(false);
            }

            await ExecuteAsync(connection, transaction, CompletionStageSql, ct).ConfigureAwait(false);
            using (var stage = CompletionTable(completions))
            {
                await BulkCopyAsync(connection, transaction, "#CompletionStage", stage, ct).ConfigureAwait(false);
            }

            return await ScalarAsync(connection, transaction, CompletionUpdateSql + "SELECT @@ROWCOUNT;", now, flowId, ct).ConfigureAwait(false);
        }, ct);

    private static async Task<T> InTransactionAsync<T>(OsduDbContext db, Func<SqlConnection, SqlTransaction, Task<T>> work, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                var connection = (SqlConnection)db.Database.GetDbConnection();
                var transaction = (SqlTransaction)tx.GetDbTransaction();
                var result = await work(connection, transaction).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                return result;
            }
            finally
            {
                await db.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }

    private static async Task BulkCopyAsync(SqlConnection connection, SqlTransaction transaction, string destination, DataTable table, CancellationToken ct)
    {
        using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction)
        {
            DestinationTableName = destination,
            BatchSize = BulkBatchSize,
            BulkCopyTimeout = BulkTimeoutSeconds,
            EnableStreaming = true,
        };
        foreach (DataColumn column in table.Columns)
        {
            bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        }

        await bulk.WriteToServerAsync(table, ct).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(SqlConnection connection, SqlTransaction transaction, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = BulkTimeoutSeconds;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<int> ScalarAsync(SqlConnection connection, SqlTransaction transaction, string sql, DateTime now, Guid flowId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = BulkTimeoutSeconds;
        command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
        command.Parameters.Add(new SqlParameter("@flowId", SqlDbType.UniqueIdentifier) { Value = flowId });
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is int i ? i : Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Runs the claim check and reads back the records it took out of the stage, with the flow that owns each id.</summary>
    private static async Task<IReadOnlyList<TargetIdConflict>> ConflictsAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = RefuseClaimedSql;
        command.CommandTimeout = BulkTimeoutSeconds;
        var conflicts = new List<TargetIdConflict>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            conflicts.Add(new TargetIdConflict(
                new DeliveryKey(reader.GetGuid(0)),
                reader.GetString(1),
                reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return conflicts;
    }

    /// <summary>Runs a statement whose result set is one delivery key per row, and reads the keys back.</summary>
    private static async Task<IReadOnlyList<DeliveryKey>> KeysAsync(SqlConnection connection, SqlTransaction transaction, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = BulkTimeoutSeconds;
        var keys = new List<DeliveryKey>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            keys.Add(new DeliveryKey(reader.GetGuid(0)));
        }

        return keys;
    }

    private static DataTable PendingTable(Guid flowId, IReadOnlyList<RecordState> records)
    {
        var table = new DataTable();
        table.Columns.Add("DeliveryKey", typeof(Guid));
        table.Columns.Add("FlowId", typeof(Guid));
        table.Columns.Add("SourceKey", typeof(string));
        table.Columns.Add("SourceKeyJson", typeof(string));
        table.Columns.Add("Label", typeof(string));
        table.Columns.Add("MappingName", typeof(string));
        table.Columns.Add("TargetId", typeof(string));
        table.Columns.Add("LastSubmissionId", typeof(Guid));
        table.Columns.Add("PendingDocumentRef", typeof(string));
        table.Columns.Add("WorkBatch", typeof(int));
        table.Columns.Add("PendingRenderContext", typeof(string));
        table.Columns.Add("PendingSourceFingerprint", typeof(string));
        table.Columns.Add("PendingSourceModifiedUtc", typeof(DateTime));
        table.Columns.Add("PendingSourceFileName", typeof(string));
        table.Columns.Add("PendingSourceRowNumber", typeof(long));
        table.Columns.Add("PendingSourceUpdatedUtc", typeof(DateTime));
        table.Columns.Add("PendingMetadataHash", typeof(string));
        table.Columns.Add("PendingPayloadHash", typeof(string));
        table.Columns.Add("PendingPayloadModifiedUtc", typeof(DateTime));
        table.Columns.Add("PendingPayloadLocation", typeof(string));
        table.Columns.Add("PendingMetadata", typeof(bool));
        table.Columns.Add("PendingPayload", typeof(bool));
        table.Columns.Add("CacheSetId", typeof(long));
        foreach (var r in records)
        {
            table.Rows.Add(
                r.DeliveryKey.Value, flowId, Truncate(r.SourceKey, 400), Value(Truncate(r.SourceKeyJson, 2000)), Value(Truncate(r.Label, 400)), r.MappingName, Value(r.TargetId),
                Value(r.LastSubmissionId), Value(r.PendingDocumentRef), Value(r.WorkBatch), Value(r.PendingRenderContext),
                Value(r.PendingSourceFingerprint), Value(r.PendingSourceModifiedUtc),
                Value(Truncate(r.PendingSourceFileName, DeliveryModel.MaxSourceFileNameLength)), Value(r.PendingSourceRowNumber), Value(r.PendingSourceUpdatedUtc),
                Value(r.PendingMetadataHash), Value(r.PendingPayloadHash),
                Value(r.PendingPayloadModifiedUtc), Value(r.PendingPayloadLocation), r.PendingMetadata, r.PendingPayload, Value(r.CacheSetId));
        }

        return table;
    }

    private static DataTable AttemptTable(Guid flowId, IReadOnlyList<RecordCompletion> completions)
    {
        var table = new DataTable();
        table.Columns.Add("FlowId", typeof(Guid));
        table.Columns.Add("DeliveryKey", typeof(Guid));
        table.Columns.Add("SubmissionId", typeof(Guid));
        table.Columns.Add("RunId", typeof(Guid));
        table.Columns.Add("Worker", typeof(string));
        table.Columns.Add("StartedUtc", typeof(DateTime));
        table.Columns.Add("CompletedUtc", typeof(DateTime));
        table.Columns.Add("Outcome", typeof(string));
        table.Columns.Add("Phase", typeof(string));
        table.Columns.Add("MetadataHash", typeof(string));
        table.Columns.Add("PayloadHash", typeof(string));
        table.Columns.Add("TargetVersion", typeof(long));
        table.Columns.Add("Error", typeof(string));
        table.Columns.Add("ResultJson", typeof(string));
        table.Columns.Add("WorkBatch", typeof(int));
        table.Columns.Add("SourceFileName", typeof(string));
        table.Columns.Add("SourceRowNumber", typeof(long));
        table.Columns.Add("SourceUpdatedUtc", typeof(DateTime));
        foreach (var c in completions)
        {
            var a = c.Attempt;
            table.Rows.Add(
                flowId, a.DeliveryKey.Value, Value(a.SubmissionId), Value(a.RunId), Truncate(a.Worker, 200), a.StartedUtc, a.CompletedUtc,
                StatusText.Of(a.Outcome), Truncate(a.Phase, 32), Value(a.MetadataHash), Value(a.PayloadHash), Value(a.TargetVersion),
                Value(Truncate(a.Error, 2000)), Value(a.ResultJson), Value(a.WorkBatch),
                Value(Truncate(a.SourceFileName, DeliveryModel.MaxSourceFileNameLength)), Value(a.SourceRowNumber), Value(a.SourceUpdatedUtc));
        }

        return table;
    }

    private static DataTable CompletionTable(IReadOnlyList<RecordCompletion> completions)
    {
        var table = new DataTable();
        table.Columns.Add("DeliveryKey", typeof(Guid));
        table.Columns.Add("Status", typeof(string));
        table.Columns.Add("Blocked", typeof(bool));
        table.Columns.Add("Promote", typeof(bool));
        table.Columns.Add("NothingSent", typeof(bool));
        table.Columns.Add("NextAttemptUtc", typeof(DateTime));
        table.Columns.Add("LastError", typeof(string));
        table.Columns.Add("TargetId", typeof(string));
        table.Columns.Add("TargetVersion", typeof(long));
        table.Columns.Add("HasTargetState", typeof(bool));
        table.Columns.Add("TargetStateJson", typeof(string));
        table.Columns.Add("PendingStepJson", typeof(string));
        table.Columns.Add("HasClaim", typeof(bool));
        table.Columns.Add("ClaimSubmissionId", typeof(Guid));
        table.Columns.Add("ClaimDocumentRef", typeof(string));
        table.Columns.Add("ClaimRenderContext", typeof(string));
        table.Columns.Add("ClaimSourceFingerprint", typeof(string));
        table.Columns.Add("ClaimSourceModifiedUtc", typeof(DateTime));
        table.Columns.Add("ClaimSourceFileName", typeof(string));
        table.Columns.Add("ClaimSourceRowNumber", typeof(long));
        table.Columns.Add("ClaimSourceUpdatedUtc", typeof(DateTime));
        table.Columns.Add("ClaimMetadataHash", typeof(string));
        table.Columns.Add("ClaimPayloadHash", typeof(string));
        table.Columns.Add("ClaimPayloadModifiedUtc", typeof(DateTime));
        table.Columns.Add("ClaimMetadata", typeof(bool));
        table.Columns.Add("ClaimPayload", typeof(bool));
        foreach (var c in completions)
        {
            var claim = c.Claimed;
            table.Rows.Add(
                c.DeliveryKey.Value, StatusText.Of(c.Status), c.Status is RecordStatus.Held or RecordStatus.Failed, c.Promote, c.NothingSent,
                Value(c.NextAttemptUtc), Value(Truncate(c.Error, 2000)), Value(c.TargetId), Value(c.TargetVersion),
                c.TargetStateJson is not null, Value(c.TargetStateJson), Value(c.PendingStepJson),
                claim is not null, Value(claim?.SubmissionId), Value(claim?.DocumentRef), Value(claim?.RenderContext), Value(claim?.SourceFingerprint),
                Value(claim?.SourceModifiedUtc),
                Value(Truncate(claim?.Origin.FileName, DeliveryModel.MaxSourceFileNameLength)), Value(claim?.Origin.RowNumber), Value(claim?.Origin.UpdatedUtc),
                Value(claim?.MetadataHash), Value(claim?.PayloadHash), Value(claim?.PayloadModifiedUtc),
                claim?.Metadata ?? false, claim?.Payload ?? false);
        }

        return table;
    }

    private static object Value<T>(T? value) where T : struct => value.HasValue ? value.Value : DBNull.Value;

    private static object Value(string? value) => value is null ? DBNull.Value : value;

    private static string? Truncate(string? text, int max)
        => text is null ? null : text.Length <= max ? text : text[..max];
}
