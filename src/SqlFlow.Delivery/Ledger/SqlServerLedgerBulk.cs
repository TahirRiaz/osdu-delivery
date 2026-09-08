using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SqlFlow.Catalog;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The two writes that carry the volume of a submission (staging the pending records, and closing the records of a
/// drained batch with their attempts), done as one bulk copy into a staging table plus one set-based statement
/// when the catalog is SQL Server (design.md section 16.2). Every other provider takes the entity path in
/// <see cref="CatalogLedger"/>, which is the same write row by row. Both run under the context's retrying
/// execution strategy inside one transaction, so a batch is staged whole or not at all.
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
            [PendingMetadataHash] nvarchar(64) NULL,
            [PendingPayloadHash] nvarchar(64) NULL,
            [PendingPayloadLocation] nvarchar(2000) NULL,
            [PendingMetadata] bit NOT NULL,
            [PendingPayload] bit NOT NULL);
        """;

    private const string PendingMergeSql = """
        MERGE [delivery].[Record] WITH (HOLDLOCK) AS t
        USING #PendingStage AS s ON t.[DeliveryKey] = s.[DeliveryKey]
        WHEN MATCHED AND NOT (t.[Status] = N'delivering' AND t.[LeaseExpiresUtc] IS NOT NULL AND t.[LeaseExpiresUtc] > @now) THEN
            UPDATE SET
                [SourceKey] = s.[SourceKey], [Label] = s.[Label], [MappingName] = s.[MappingName],
                [TargetId] = COALESCE(t.[TargetId], s.[TargetId]), [Status] = N'pending', [LastSubmissionId] = s.[LastSubmissionId],
                [AttemptCount] = 0, [NextAttemptUtc] = NULL, [LastError] = NULL, [LeaseOwner] = NULL, [LeaseExpiresUtc] = NULL,
                [PendingDocumentRef] = s.[PendingDocumentRef], [WorkBatch] = s.[WorkBatch], [PendingStepJson] = NULL,
                [PendingRenderContext] = s.[PendingRenderContext], [PendingSourceFingerprint] = s.[PendingSourceFingerprint],
                [PendingMetadataHash] = s.[PendingMetadataHash], [PendingPayloadHash] = s.[PendingPayloadHash],
                [PendingPayloadLocation] = s.[PendingPayloadLocation], [PendingMetadata] = s.[PendingMetadata], [PendingPayload] = s.[PendingPayload],
                [Blocked] = 0, [UpdatedUtc] = @now
        WHEN NOT MATCHED BY TARGET THEN
            INSERT ([DeliveryKey], [FlowId], [SourceKey], [Label], [MappingName], [TargetId], [Status], [LastSubmissionId], [AttemptCount],
                    [PendingDocumentRef], [WorkBatch], [PendingRenderContext], [PendingSourceFingerprint], [PendingMetadataHash], [PendingPayloadHash],
                    [PendingPayloadLocation], [PendingMetadata], [PendingPayload], [Blocked], [CreatedUtc], [UpdatedUtc])
            VALUES (s.[DeliveryKey], s.[FlowId], s.[SourceKey], s.[Label], s.[MappingName], s.[TargetId], N'pending', s.[LastSubmissionId], 0,
                    s.[PendingDocumentRef], s.[WorkBatch], s.[PendingRenderContext], s.[PendingSourceFingerprint], s.[PendingMetadataHash], s.[PendingPayloadHash],
                    s.[PendingPayloadLocation], s.[PendingMetadata], s.[PendingPayload], 0, @now, @now);
        SELECT @@ROWCOUNT;
        """;

    private const string CompletionStageSql = """
        CREATE TABLE #CompletionStage (
            [DeliveryKey] uniqueidentifier NOT NULL PRIMARY KEY,
            [Status] nvarchar(16) NOT NULL,
            [Blocked] bit NOT NULL,
            [Promote] bit NOT NULL,
            [NextAttemptUtc] datetime2 NULL,
            [LastError] nvarchar(2000) NULL,
            [TargetId] nvarchar(500) NULL,
            [TargetVersion] bigint NULL,
            [HasTargetState] bit NOT NULL,
            [TargetStateJson] nvarchar(max) NULL,
            [PendingStepJson] nvarchar(max) NULL);
        """;

    private const string CompletionUpdateSql = """
        UPDATE r SET
            [Status] = s.[Status], [Blocked] = s.[Blocked], [LeaseOwner] = NULL, [LeaseExpiresUtc] = NULL,
            [NextAttemptUtc] = s.[NextAttemptUtc], [LastError] = s.[LastError], [UpdatedUtc] = @now,
            [TargetId] = COALESCE(s.[TargetId], r.[TargetId]), [TargetVersion] = COALESCE(s.[TargetVersion], r.[TargetVersion]),
            [TargetStateJson] = CASE WHEN s.[HasTargetState] = 1 THEN s.[TargetStateJson] ELSE r.[TargetStateJson] END,
            [PendingStepJson] = s.[PendingStepJson],
            [RenderContext] = CASE WHEN s.[Promote] = 1 THEN COALESCE(r.[PendingRenderContext], r.[RenderContext]) ELSE r.[RenderContext] END,
            [SourceFingerprint] = CASE WHEN s.[Promote] = 1 THEN COALESCE(r.[PendingSourceFingerprint], r.[SourceFingerprint]) ELSE r.[SourceFingerprint] END,
            [MetadataHash] = CASE WHEN s.[Promote] = 1 AND r.[PendingMetadata] = 1 THEN r.[PendingMetadataHash] ELSE r.[MetadataHash] END,
            [PayloadHash] = CASE WHEN s.[Promote] = 1 AND r.[PendingPayload] = 1 THEN r.[PendingPayloadHash] ELSE r.[PayloadHash] END,
            [LastDeliveredUtc] = CASE WHEN s.[Promote] = 1 THEN @now ELSE r.[LastDeliveredUtc] END,
            [LastVerifiedUtc] = CASE WHEN s.[Promote] = 1 THEN NULL ELSE r.[LastVerifiedUtc] END,
            [LastVerifyOutcome] = CASE WHEN s.[Promote] = 1 THEN NULL ELSE r.[LastVerifyOutcome] END,
            [PendingDocumentRef] = CASE WHEN s.[Promote] = 1 THEN NULL ELSE r.[PendingDocumentRef] END,
            [WorkBatch] = CASE WHEN s.[Promote] = 1 THEN NULL ELSE r.[WorkBatch] END,
            [PendingMetadata] = CASE WHEN s.[Promote] = 1 THEN 0 ELSE r.[PendingMetadata] END,
            [PendingPayload] = CASE WHEN s.[Promote] = 1 THEN 0 ELSE r.[PendingPayload] END,
            [PendingPayloadLocation] = CASE WHEN s.[Promote] = 1 THEN NULL ELSE r.[PendingPayloadLocation] END,
            [AttemptCount] = CASE WHEN s.[Promote] = 1 THEN 0 ELSE r.[AttemptCount] END
        FROM [delivery].[Record] AS r
        INNER JOIN #CompletionStage AS s ON r.[DeliveryKey] = s.[DeliveryKey];
        """;

    public static Task<int> UpsertPendingAsync(CatalogDbContext db, IReadOnlyList<RecordState> records, DateTime now, CancellationToken ct)
        => InTransactionAsync(db, async (connection, transaction) =>
        {
            await ExecuteAsync(connection, transaction, PendingStageSql, ct).ConfigureAwait(false);
            using (var table = PendingTable(records))
            {
                await BulkCopyAsync(connection, transaction, "#PendingStage", table, ct).ConfigureAwait(false);
            }

            return await ScalarAsync(connection, transaction, PendingMergeSql, now, ct).ConfigureAwait(false);
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

    private static async Task<int> InTransactionAsync(CatalogDbContext db, Func<SqlConnection, SqlTransaction, Task<int>> work, CancellationToken ct)
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
        table.Columns.Add("PendingMetadataHash", typeof(string));
        table.Columns.Add("PendingPayloadHash", typeof(string));
        table.Columns.Add("PendingPayloadLocation", typeof(string));
        table.Columns.Add("PendingMetadata", typeof(bool));
        table.Columns.Add("PendingPayload", typeof(bool));
        foreach (var r in records)
        {
            table.Rows.Add(
                r.DeliveryKey.Value, r.FlowId, Truncate(r.SourceKey, 400), Value(Truncate(r.Label, 400)), r.MappingName, Value(r.TargetId),
                Value(r.LastSubmissionId), Value(r.PendingDocumentRef), Value(r.WorkBatch), Value(r.PendingRenderContext),
                Value(r.PendingSourceFingerprint), Value(r.PendingMetadataHash), Value(r.PendingPayloadHash), Value(r.PendingPayloadLocation),
                r.PendingMetadata, r.PendingPayload);
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
        table.Columns.Add("NextAttemptUtc", typeof(DateTime));
        table.Columns.Add("LastError", typeof(string));
        table.Columns.Add("TargetId", typeof(string));
        table.Columns.Add("TargetVersion", typeof(long));
        table.Columns.Add("HasTargetState", typeof(bool));
        table.Columns.Add("TargetStateJson", typeof(string));
        table.Columns.Add("PendingStepJson", typeof(string));
        foreach (var c in completions)
        {
            table.Rows.Add(
                c.DeliveryKey.Value, StatusText.Of(c.Status), c.Status is RecordStatus.Held or RecordStatus.Failed, c.Promote,
                Value(c.NextAttemptUtc), Value(Truncate(c.Error, 2000)), Value(c.TargetId), Value(c.TargetVersion),
                c.TargetStateJson is not null, Value(c.TargetStateJson), Value(c.PendingStepJson));
        }

        return table;
    }

    private static object Value<T>(T? value) where T : struct => value.HasValue ? value.Value : DBNull.Value;

    private static object Value(string? value) => value is null ? DBNull.Value : value;

    private static string? Truncate(string? text, int max)
        => text is null ? null : text.Length <= max ? text : text[..max];
}
