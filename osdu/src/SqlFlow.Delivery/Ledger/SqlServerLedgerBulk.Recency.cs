using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The statement behind what a partition's delivery ledgers changed in OSDU lately (docs:
/// osdu/docs/reference/flow/assertion.md, Records the index may not list yet), which an assertion run reads before it
/// judges what the search index lists.
/// </summary>
internal static partial class SqlServerLedgerBulk
{
    // Every delivery ledger of the partition that changed OSDU after @since, with what it changed: the records it wrote (their
    // last delivery), the records it took out of OSDU or put back at an earlier version and still holds (their status and last
    // update), and the records it deleted from itself that had an OSDU id (their line). Each count and latest moment is one
    // seek of an index that starts with the partition and the ledger; the OSDU id that names the entity type is one row of
    // the ledger's own records, or of the lines just deleted for a ledger that holds none any more.
    private const string RecentChangesSql = """
        SELECT l.[FlowId], l.[LedgerName], w.[Records], w.[Latest], m.[Records], m.[Latest], g.[Records], g.[Latest],
            COALESCE(s.[TargetId] COLLATE Latin1_General_100_BIN2, ps.[TargetId])
        FROM [osdu].[Ledger] AS l
        CROSS APPLY (
            SELECT COUNT_BIG(*) AS [Records], MAX(r.[LastDeliveredUtc]) AS [Latest]
            FROM [osdu].[Record] AS r
            WHERE r.[PartitionId] = l.[PartitionId] AND r.[FlowId] = l.[FlowId] AND r.[LastDeliveredUtc] > @since) AS w
        CROSS APPLY (
            SELECT COUNT_BIG(*) AS [Records], MAX(r.[UpdatedUtc]) AS [Latest]
            FROM [osdu].[Record] AS r
            WHERE r.[PartitionId] = l.[PartitionId] AND r.[FlowId] = l.[FlowId] AND r.[Status] IN (@deleted, @reverted) AND r.[UpdatedUtc] > @since) AS m
        CROSS APPLY (
            SELECT COUNT_BIG(*) AS [Records], MAX(p.[PurgedUtc]) AS [Latest]
            FROM [osdu].[PurgedRecord] AS p
            WHERE p.[PartitionId] = l.[PartitionId] AND p.[FlowId] = l.[FlowId] AND p.[PurgedUtc] > @since AND p.[TargetId] IS NOT NULL) AS g
        OUTER APPLY (
            SELECT TOP (1) r.[TargetId]
            FROM [osdu].[Record] AS r
            WHERE r.[PartitionId] = l.[PartitionId] AND r.[FlowId] = l.[FlowId] AND r.[TargetId] IS NOT NULL
            ORDER BY r.[TargetId]) AS s
        OUTER APPLY (
            SELECT TOP (1) p.[TargetId]
            FROM [osdu].[PurgedRecord] AS p
            WHERE p.[PartitionId] = l.[PartitionId] AND p.[FlowId] = l.[FlowId] AND p.[PurgedUtc] > @since AND p.[TargetId] IS NOT NULL) AS ps
        WHERE l.[PartitionId] = @partitionId AND l.[Kind] = @kind AND (w.[Records] > 0 OR m.[Records] > 0 OR g.[Records] > 0)
        ORDER BY l.[LedgerName];
        """;

    /// <summary>The columns of <see cref="RecentChangesSql"/> holding the latest write, removal and deletion of a ledger.</summary>
    private static readonly int[] LatestColumns = [3, 5, 7];

    /// <summary>What every delivery ledger of the partition changed in OSDU after <paramref name="since"/>; ledgers that changed nothing are left out.</summary>
    public static async Task<IReadOnlyList<RecentOsduChange>> RecentChangesAsync(OsduDbContext db, short partitionId, DateTime since, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            await using var command = Command(connection, null, RecentChangesSql, slice: null);
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@since", SqlDbType.DateTime2) { Value = since });
            command.Parameters.Add(new SqlParameter("@kind", SqlDbType.NVarChar, 16) { Value = LedgerKinds.Delivery });
            command.Parameters.Add(new SqlParameter("@deleted", SqlDbType.NVarChar, 16) { Value = StatusText.Of(RecordStatus.Deleted) });
            command.Parameters.Add(new SqlParameter("@reverted", SqlDbType.NVarChar, 16) { Value = StatusText.Of(RecordStatus.Reverted) });
            var changes = new List<RecentOsduChange>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var latest = LatestColumns
                    .Where(i => !reader.IsDBNull(i))
                    .Select(i => DateTime.SpecifyKind(reader.GetDateTime(i), DateTimeKind.Utc))
                    .Max();
                changes.Add(new RecentOsduChange(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetInt64(4),
                    reader.GetInt64(6),
                    latest,
                    reader.IsDBNull(8) ? null : reader.GetString(8)));
            }

            return changes;
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }
}
