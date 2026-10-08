using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

// Removing what an inventory found (docs/inventory-plan.md, Removing what an inventory found): the ids a removal may act on,
// each with the finding the inventory recorded and the finding the reconcile's own rule gives it now, read a chunk at a time;
// and what a chunk came to, written with the inventory rows it changed in one transaction.
internal static partial class SqlServerLedgerBulk
{
    // The ids of one finding the inventory holds as served (or, for a removal that names its ids, those ids whatever they are
    // now), in order after the last one read, each with what the ledgers hold of it now and the finding that gives it.
    private const string InventoryRemovalCandidatesSql = """
        SELECT TOP (@limit) r.[InventoryRecordId], r.[TargetId], r.[Version], r.[CreateUser],
            CASE WHEN r.[GoneUtc] IS NOT NULL OR r.[FirstSeenUtc] IS NULL THEN N'gone' ELSE r.[Finding] END AS [Recorded],
            {{finding}} AS [Current],
            COALESCE(rec.[FlowId], art.[FlowId], pur.[FlowId]) AS [LedgerFlowId],
            COALESCE(rec.[DeliveryKey], art.[DeliveryKey], pur.[DeliveryKey]) AS [DeliveryKey],
            rec.[Status] AS [LedgerStatus], art.[ArtifactId], art.[State] AS [ArtifactState]
        FROM [osdu].[InventoryRecord] AS r
        {{ledger}}
        WHERE r.[PartitionId] = @partitionId AND r.[InventoryId] = @inventoryId AND r.[InventoryRecordId] > @after
            AND ((@byIds = 0 AND r.[Finding] = @finding AND r.[GoneUtc] IS NULL AND r.[FirstSeenUtc] IS NOT NULL)
                OR (@byIds = 1 AND r.[TargetId] IN (SELECT i.[value] COLLATE Latin1_General_100_BIN2 FROM OPENJSON(@ids) AS i)))
        ORDER BY r.[InventoryRecordId];
        """;

    private const string InventoryRemovedStageSql = """
        CREATE TABLE #Removed (
            [InventoryRecordId] bigint NOT NULL PRIMARY KEY,
            [TargetId] nvarchar(500) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Version] bigint NULL,
            [Finding] nvarchar(16) NOT NULL,
            [Outcome] nvarchar(16) NOT NULL,
            [Reason] nvarchar(1000) NULL,
            [LedgerFlowId] uniqueidentifier NULL,
            [DeliveryKey] uniqueidentifier NULL);
        """;

    // A chunk's outcomes kept, the ids it took out of OSDU (or found OSDU no longer served) marked gone in the inventory at once,
    // so its counts say what is left without another build, and the removal's tallies added to.
    private const string InventoryRemovedSql = """
        INSERT INTO [osdu].[InventoryRemovalItem] ([PartitionId], [InventoryRemovalId], [InventoryRecordId], [TargetId], [Version], [Finding],
                [Outcome], [Reason], [LedgerFlowId], [DeliveryKey], [RecordedUtc])
        SELECT @partitionId, @removalId, x.[InventoryRecordId], x.[TargetId], x.[Version], x.[Finding], x.[Outcome], x.[Reason],
            x.[LedgerFlowId], x.[DeliveryKey], @now
        FROM #Removed AS x;

        UPDATE r SET
            [GoneUtc] = COALESCE(r.[GoneUtc], @now),
            [FindingUtc] = CASE WHEN r.[Finding] = N'gone' THEN r.[FindingUtc] ELSE @now END,
            [Finding] = N'gone',
            [Detail] = CASE WHEN x.[Outcome] = N'removed' THEN @removedDetail ELSE @goneDetail END
        FROM [osdu].[InventoryRecord] AS r
        INNER JOIN #Removed AS x ON r.[PartitionId] = @partitionId AND r.[InventoryId] = @inventoryId AND r.[InventoryRecordId] = x.[InventoryRecordId]
        WHERE x.[Outcome] IN (N'removed', N'gone');

        UPDATE [osdu].[InventoryRemoval] SET
            [Removed] = [Removed] + @removed, [Gone] = [Gone] + @gone, [Skipped] = [Skipped] + @skipped, [Failed] = [Failed] + @failed
        WHERE [PartitionId] = @partitionId AND [InventoryRemovalId] = @removalId;
        DROP TABLE #Removed;
        """;

    /// <summary>
    /// The next ids a removal may act on, at most <paramref name="limit"/>, after <paramref name="after"/>: those of
    /// <paramref name="finding"/> the inventory holds as served, or with <paramref name="ids"/> those ids whatever they are now;
    /// each with the finding the reconcile's rule gives it against the ledgers as they stand and <paramref name="owners"/>.
    /// </summary>
    public static Task<IReadOnlyList<InventoryRemovalCandidate>> InventoryRemovalCandidatesAsync(
        OsduDbContext db, short partitionId, int inventoryId, string finding, IReadOnlyList<string>? ids, IReadOnlyList<string> owners, long after, int limit, CancellationToken ct)
        => InTransactionAsync<IReadOnlyList<InventoryRemovalCandidate>>(db, async (connection, transaction) =>
        {
            await using var command = Command(connection, transaction, InventorySql(InventoryRemovalCandidatesSql), slice: null);
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@inventoryId", SqlDbType.Int) { Value = inventoryId });
            command.Parameters.Add(new SqlParameter("@finding", SqlDbType.NVarChar, 16) { Value = finding });
            command.Parameters.Add(new SqlParameter("@byIds", SqlDbType.Bit) { Value = ids is not null });
            command.Parameters.Add(new SqlParameter("@ids", SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(ids ?? []) });
            command.Parameters.Add(new SqlParameter("@owners", SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(owners) });
            command.Parameters.Add(new SqlParameter("@after", SqlDbType.BigInt) { Value = after });
            command.Parameters.Add(new SqlParameter("@limit", SqlDbType.Int) { Value = limit });
            var candidates = new List<InventoryRemovalCandidate>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                candidates.Add(new InventoryRemovalCandidate(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetGuid(6),
                    reader.IsDBNull(7) ? null : reader.GetGuid(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetInt64(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10)));
            }

            return candidates;
        }, ct);

    /// <summary>
    /// Keeps what one chunk of a removal came to: every id's outcome and reason, the ids removed (or found gone) marked gone in
    /// the inventory with what removed them, and the removal's tallies, in one transaction.
    /// </summary>
    public static Task<int> RecordInventoryRemovalAsync(
        OsduDbContext db, short partitionId, int inventoryId, long removalId, IReadOnlyList<InventoryRemovalItem> items, string removedDetail, string goneDetail, DateTime now, CancellationToken ct)
        => InTransactionAsync(db, async (connection, transaction) =>
        {
            await ExecuteAsync(connection, transaction, InventoryRemovedStageSql, ct).ConfigureAwait(false);
            using (var table = new DataTable())
            {
                foreach (var (name, type) in new (string, Type)[]
                {
                    ("InventoryRecordId", typeof(long)), ("TargetId", typeof(string)), ("Version", typeof(long)), ("Finding", typeof(string)),
                    ("Outcome", typeof(string)), ("Reason", typeof(string)), ("LedgerFlowId", typeof(Guid)), ("DeliveryKey", typeof(Guid)),
                })
                {
                    table.Columns.Add(name, type);
                }

                foreach (var item in items.GroupBy(i => i.InventoryRecordId).Select(g => g.Last()))
                {
                    table.Rows.Add(
                        item.InventoryRecordId, item.TargetId, Value(item.Version), item.Finding, item.Outcome,
                        Value(InventoryRemovals.Reason(item.Reason)), Value(item.LedgerFlowId), Value(item.DeliveryKey));
                }

                await BulkCopyAsync(connection, transaction, "#Removed", table, ct).ConfigureAwait(false);
            }

            var tally = InventoryRemovalTally.None.Add(items.GroupBy(i => i.InventoryRecordId).Select(g => g.Last()));
            await using var command = Command(connection, transaction, InventoryRemovedSql, slice: null);
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@inventoryId", SqlDbType.Int) { Value = inventoryId });
            command.Parameters.Add(new SqlParameter("@removalId", SqlDbType.BigInt) { Value = removalId });
            command.Parameters.Add(new SqlParameter("@removedDetail", SqlDbType.NVarChar, DeliveryModel.MaxInventoryDetailLength) { Value = Cut(removedDetail) });
            command.Parameters.Add(new SqlParameter("@goneDetail", SqlDbType.NVarChar, DeliveryModel.MaxInventoryDetailLength) { Value = Cut(goneDetail) });
            command.Parameters.Add(new SqlParameter("@removed", SqlDbType.BigInt) { Value = tally.Removed });
            command.Parameters.Add(new SqlParameter("@gone", SqlDbType.BigInt) { Value = tally.Gone });
            command.Parameters.Add(new SqlParameter("@skipped", SqlDbType.BigInt) { Value = tally.Skipped });
            command.Parameters.Add(new SqlParameter("@failed", SqlDbType.BigInt) { Value = tally.Failed });
            command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, ct);

    private static string Cut(string detail) => detail.Length <= DeliveryModel.MaxInventoryDetailLength ? detail : detail[..DeliveryModel.MaxInventoryDetailLength];
}
