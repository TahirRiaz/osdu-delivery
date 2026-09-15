using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SqlFlow.Catalog;
using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The two writes that carry the volume of a submission (staging the pending records, and closing the records of a
/// drained batch with their attempts), done as one bulk copy into a staging table plus set-based statements when the
/// catalog is SQL Server (design.md section 16.2). Every other provider takes the entity path in
/// <see cref="CatalogLedger"/>, which is the same write row by row. Both run under the context's retrying execution
/// strategy inside one transaction, so a batch is staged whole or not at all.
/// </summary>
internal static class SqlServerLedgerBulk
{
    public const string ProviderName = "Microsoft.EntityFrameworkCore.SqlServer";

    private const int BulkBatchSize = 5000;

    private const int BulkTimeoutSeconds = 600;

    public static bool Applies(CatalogDbContext db) => string.Equals(db.Database.ProviderName, ProviderName, StringComparison.Ordinal);

    private const string PendingStageSql = """
        CREATE TABLE #PendingStage (
            [DeliveryKey] uniqueidentifier NOT NULL PRIMARY KEY,
            [FlowId] uniqueidentifier NOT NULL,
            [SourceKey] nvarchar(400) NOT NULL,
            [Label] nvarchar(400) NULL,
            [MappingName] nvarchar(200) NOT NULL,
            [TargetId] nvarchar(500) NULL,
            [LastSubmissionId] uniqueidentifier NULL,
            [PendingDocumentRef] nvarchar(64) NULL,
            [WorkBatch] int NULL,
            [PendingRenderContext] nvarchar(max) NULL,
            [PendingSourceFingerprint] nvarchar(200) NULL,
            [PendingSourceModifiedUtc] datetime2 NULL,
            [PendingMetadataHash] nvarchar(64) NULL,
            [PendingPayloadHash] nvarchar(64) NULL,
            [PendingPayloadModifiedUtc] datetime2 NULL,
            [PendingPayloadLocation] nvarchar(2000) NULL,
            [PendingMetadata] bit NOT NULL,
            [PendingPayload] bit NOT NULL,
            [CacheSetId] bigint NULL);
        """;

    // Work older than what the record already holds, delivered or queued, is taken out of the stage and named before
    // the merge. The update locks are held to the end of the transaction, so no concurrent intake can stage a newer
    // version between this test and the write. A comparison with a NULL column is unknown, and refuses nothing.
    private const string RefuseOlderSql = """
        DELETE s
        OUTPUT deleted.[DeliveryKey]
        FROM #PendingStage AS s
        INNER JOIN [delivery].[Record] AS t WITH (UPDLOCK, HOLDLOCK) ON t.[DeliveryKey] = s.[DeliveryKey]
        CROSS APPLY (SELECT CASE WHEN t.[PendingDocumentRef] IS NOT NULL AND t.[Status] IN (N'pending', N'delivering') THEN 1 ELSE 0 END AS [Queued]) AS q
        WHERE (s.[PendingSourceModifiedUtc] IS NOT NULL
                AND (s.[PendingSourceModifiedUtc] < t.[SourceModifiedUtc]
                     OR (q.[Queued] = 1 AND s.[PendingSourceModifiedUtc] < t.[PendingSourceModifiedUtc])))
           OR (s.[PendingPayload] = 1 AND s.[PendingPayloadModifiedUtc] IS NOT NULL
                AND (s.[PendingPayloadModifiedUtc] < t.[PayloadModifiedUtc]
                     OR (q.[Queued] = 1 AND t.[PendingPayload] = 1 AND s.[PendingPayloadModifiedUtc] < t.[PendingPayloadModifiedUtc])));
        """;

    // A record being delivered right now keeps its status, lease, retry count and last error: the new work queues
    // behind the delivery, whose completion leaves it pending. Every right-hand side reads the row as it was.
    private const string PendingMergeSql = """
        MERGE [delivery].[Record] WITH (HOLDLOCK) AS t
        USING #PendingStage AS s ON t.[DeliveryKey] = s.[DeliveryKey]
        WHEN MATCHED THEN
            UPDATE SET
                [SourceKey] = s.[SourceKey], [Label] = s.[Label], [MappingName] = s.[MappingName],
                [TargetId] = COALESCE(t.[TargetId], s.[TargetId]), [LastSubmissionId] = s.[LastSubmissionId], [NextAttemptUtc] = NULL,
                [Status] = CASE WHEN t.[Status] = N'delivering' AND t.[LeaseExpiresUtc] > @now THEN t.[Status] ELSE N'pending' END,
                [AttemptCount] = CASE WHEN t.[Status] = N'delivering' AND t.[LeaseExpiresUtc] > @now THEN t.[AttemptCount] ELSE 0 END,
                [LastError] = CASE WHEN t.[Status] = N'delivering' AND t.[LeaseExpiresUtc] > @now THEN t.[LastError] ELSE NULL END,
                [LeaseOwner] = CASE WHEN t.[Status] = N'delivering' AND t.[LeaseExpiresUtc] > @now THEN t.[LeaseOwner] ELSE NULL END,
                [LeaseExpiresUtc] = CASE WHEN t.[Status] = N'delivering' AND t.[LeaseExpiresUtc] > @now THEN t.[LeaseExpiresUtc] ELSE NULL END,
                [PendingDocumentRef] = s.[PendingDocumentRef], [WorkBatch] = s.[WorkBatch], [PendingStepJson] = NULL,
                [PendingRenderContext] = s.[PendingRenderContext], [PendingSourceFingerprint] = s.[PendingSourceFingerprint],
                [PendingSourceModifiedUtc] = s.[PendingSourceModifiedUtc],
                [PendingMetadataHash] = s.[PendingMetadataHash], [PendingPayloadHash] = s.[PendingPayloadHash],
                [PendingPayloadModifiedUtc] = s.[PendingPayloadModifiedUtc],
                [PendingPayloadLocation] = s.[PendingPayloadLocation], [PendingMetadata] = s.[PendingMetadata], [PendingPayload] = s.[PendingPayload],
                [CacheSetId] = s.[CacheSetId], [Blocked] = 0, [UpdatedUtc] = @now
        WHEN NOT MATCHED BY TARGET THEN
            INSERT ([DeliveryKey], [FlowId], [SourceKey], [Label], [MappingName], [TargetId], [Status], [LastSubmissionId], [AttemptCount],
                    [PendingDocumentRef], [WorkBatch], [PendingRenderContext], [PendingSourceFingerprint], [PendingSourceModifiedUtc],
                    [PendingMetadataHash], [PendingPayloadHash], [PendingPayloadModifiedUtc],
                    [PendingPayloadLocation], [PendingMetadata], [PendingPayload], [CacheSetId], [Blocked], [CreatedUtc], [UpdatedUtc])
            VALUES (s.[DeliveryKey], s.[FlowId], s.[SourceKey], s.[Label], s.[MappingName], s.[TargetId], N'pending', s.[LastSubmissionId], 0,
                    s.[PendingDocumentRef], s.[WorkBatch], s.[PendingRenderContext], s.[PendingSourceFingerprint], s.[PendingSourceModifiedUtc],
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
            [ClaimMetadataHash] nvarchar(64) NULL,
            [ClaimPayloadHash] nvarchar(64) NULL,
            [ClaimPayloadModifiedUtc] datetime2 NULL,
            [ClaimMetadata] bit NOT NULL,
            [ClaimPayload] bit NOT NULL);
        """;

    // The same write as CatalogLedger.ApplyCompletion. A record now carrying other pending work than the try claimed
    // (newer work queued behind it) promotes what the try delivered from the claim and goes back to pending, keeping
    // the newer work and its step progress; any other record settles as the completion says, promoting its own
    // pending columns.
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
        FROM [delivery].[Record] AS r
        INNER JOIN #CompletionStage AS s ON r.[DeliveryKey] = s.[DeliveryKey]
        CROSS APPLY (SELECT CASE
            WHEN s.[HasClaim] = 1 AND r.[PendingDocumentRef] IS NOT NULL
                 AND (r.[PendingDocumentRef] <> s.[ClaimDocumentRef]
                      OR r.[LastSubmissionId] <> s.[ClaimSubmissionId]
                      OR (r.[LastSubmissionId] IS NULL AND s.[ClaimSubmissionId] IS NOT NULL)
                      OR (r.[LastSubmissionId] IS NOT NULL AND s.[ClaimSubmissionId] IS NULL))
            THEN 1 ELSE 0 END AS [Superseded]) AS x;
        """;

    public static Task<PendingStaging> UpsertPendingAsync(CatalogDbContext db, IReadOnlyList<RecordState> records, DateTime now, CancellationToken ct)
        => InTransactionAsync(db, async (connection, transaction) =>
        {
            await ExecuteAsync(connection, transaction, PendingStageSql, ct).ConfigureAwait(false);
            using (var table = PendingTable(records))
            {
                await BulkCopyAsync(connection, transaction, "#PendingStage", table, ct).ConfigureAwait(false);
            }

            var refused = await KeysAsync(connection, transaction, RefuseOlderSql, ct).ConfigureAwait(false);
            var staged = await ScalarAsync(connection, transaction, PendingMergeSql, now, ct).ConfigureAwait(false);
            return new PendingStaging(staged, refused);
        }, ct);

    public static Task<int> CompleteManyAsync(CatalogDbContext db, IReadOnlyList<RecordCompletion> completions, DateTime now, CancellationToken ct)
        => InTransactionAsync(db, async (connection, transaction) =>
        {
            using (var attempts = AttemptTable(completions))
            {
                await BulkCopyAsync(connection, transaction, "[delivery].[Attempt]", attempts, ct).ConfigureAwait(false);
            }

            await ExecuteAsync(connection, transaction, CompletionStageSql, ct).ConfigureAwait(false);
            using (var stage = CompletionTable(completions))
            {
                await BulkCopyAsync(connection, transaction, "#CompletionStage", stage, ct).ConfigureAwait(false);
            }

            return await ScalarAsync(connection, transaction, CompletionUpdateSql + "SELECT @@ROWCOUNT;", now, ct).ConfigureAwait(false);
        }, ct);

    private static async Task<T> InTransactionAsync<T>(CatalogDbContext db, Func<SqlConnection, SqlTransaction, Task<T>> work, CancellationToken ct)
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

    private static async Task<int> ScalarAsync(SqlConnection connection, SqlTransaction transaction, string sql, DateTime now, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = BulkTimeoutSeconds;
        command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is int i ? i : Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
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

    private static DataTable PendingTable(IReadOnlyList<RecordState> records)
    {
        var table = new DataTable();
        table.Columns.Add("DeliveryKey", typeof(Guid));
        table.Columns.Add("FlowId", typeof(Guid));
        table.Columns.Add("SourceKey", typeof(string));
        table.Columns.Add("Label", typeof(string));
        table.Columns.Add("MappingName", typeof(string));
        table.Columns.Add("TargetId", typeof(string));
        table.Columns.Add("LastSubmissionId", typeof(Guid));
        table.Columns.Add("PendingDocumentRef", typeof(string));
        table.Columns.Add("WorkBatch", typeof(int));
        table.Columns.Add("PendingRenderContext", typeof(string));
        table.Columns.Add("PendingSourceFingerprint", typeof(string));
        table.Columns.Add("PendingSourceModifiedUtc", typeof(DateTime));
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
                r.DeliveryKey.Value, r.FlowId, Truncate(r.SourceKey, 400), Value(Truncate(r.Label, 400)), r.MappingName, Value(r.TargetId),
                Value(r.LastSubmissionId), Value(r.PendingDocumentRef), Value(r.WorkBatch), Value(r.PendingRenderContext),
                Value(r.PendingSourceFingerprint), Value(r.PendingSourceModifiedUtc), Value(r.PendingMetadataHash), Value(r.PendingPayloadHash),
                Value(r.PendingPayloadModifiedUtc), Value(r.PendingPayloadLocation), r.PendingMetadata, r.PendingPayload, Value(r.CacheSetId));
        }

        return table;
    }

    private static DataTable AttemptTable(IReadOnlyList<RecordCompletion> completions)
    {
        var table = new DataTable();
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
        foreach (var c in completions)
        {
            var a = c.Attempt;
            table.Rows.Add(
                a.DeliveryKey.Value, Value(a.SubmissionId), Value(a.RunId), Truncate(a.Worker, 200), a.StartedUtc, a.CompletedUtc,
                StatusText.Of(a.Outcome), Truncate(a.Phase, 32), Value(a.MetadataHash), Value(a.PayloadHash), Value(a.TargetVersion),
                Value(Truncate(a.Error, 2000)), Value(a.ResultJson), Value(a.WorkBatch));
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
                Value(claim?.SourceModifiedUtc), Value(claim?.MetadataHash), Value(claim?.PayloadHash), Value(claim?.PayloadModifiedUtc),
                claim?.Metadata ?? false, claim?.Payload ?? false);
        }

        return table;
    }

    private static object Value<T>(T? value) where T : struct => value.HasValue ? value.Value : DBNull.Value;

    private static object Value(string? value) => value is null ? DBNull.Value : value;

    private static string? Truncate(string? text, int max)
        => text is null ? null : text.Length <= max ? text : text[..max];
}
