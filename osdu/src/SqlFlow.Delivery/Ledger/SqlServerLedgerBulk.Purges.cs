using System.Data;
using Microsoft.Data.SqlClient;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The statement behind deleting removed records from the ledger (docs/ledger.md, Deleting a removed record from the
/// ledger): one slice of records in one transaction, each kept as one line of <c>osdu.PurgedRecord</c> and named under the
/// intervention that deleted it, its attempts, search entries and row deleted.
/// </summary>
internal static partial class SqlServerLedgerBulk
{
    // One slice of records deleted from the ledger. Only a record OSDU no longer holds goes: one the ledger marks deleted, with
    // no lease and so no work in flight and no lease event waiting to be applied to it; the record is locked from the check to
    // the end of the transaction. Each goes with one line saying what it was (its key, source key, label, OSDU id, the last
    // version an attempt of it named, how many attempts went with it), who deleted it and under which intervention; the
    // intervention names it too, so the audit trail reaches it however many records it reached. The rows of the audit trail
    // that name it (the activities and their links) and a reversal's item of it stay: they are another record's, or the trail's.
    // The ledger's watermarks go with the first record deleted: a watermark says every row of its scope up to it was planned,
    // which is no longer true of a row whose record the ledger forgot, so the next run of the ledger reads every row once, as
    // after its rules moved, and delivers a row still in the source as a new record. A row it holds a record of is decided
    // by that record's own hashes, as always.
    private const string PurgeSql = $$"""
        DECLARE @purged TABLE ([DeliveryKey] uniqueidentifier NOT NULL PRIMARY KEY);
        INSERT INTO @purged ([DeliveryKey])
        SELECT r.[DeliveryKey]
        FROM (SELECT DISTINCT CAST(j.[value] AS uniqueidentifier) AS [DeliveryKey] FROM OPENJSON(@keys) AS j) AS k
        INNER JOIN [osdu].[Record] AS r WITH (UPDLOCK, FORCESEEK ({{RecordKey}} ([PartitionId], [FlowId], [DeliveryKey])))
            ON r.[PartitionId] = @partitionId AND r.[FlowId] = @flowId AND r.[DeliveryKey] = k.[DeliveryKey]
        WHERE r.[Status] = N'deleted' AND r.[LeaseOwner] IS NULL;

        INSERT INTO [osdu].[PurgedRecord] ([PartitionId], [FlowId], [DeliveryKey], [SourceKey], [Label], [TargetId], [LastVersion], [Attempts],
            [ActivityId], [PurgedBy], [PurgedUtc])
        SELECT @partitionId, @flowId, r.[DeliveryKey], r.[SourceKey], r.[Label], COALESCE(r.[TargetId] COLLATE Latin1_General_100_BIN2, r.[ClaimedTargetId]),
            (SELECT TOP (1) a.[TargetVersion] FROM [osdu].[Attempt] AS a
             WHERE a.[PartitionId] = @partitionId AND a.[FlowId] = @flowId AND a.[DeliveryKey] = r.[DeliveryKey] AND a.[TargetVersion] IS NOT NULL
             ORDER BY a.[AttemptId] DESC),
            (SELECT COUNT(*) FROM [osdu].[Attempt] AS a
             WHERE a.[PartitionId] = @partitionId AND a.[FlowId] = @flowId AND a.[DeliveryKey] = r.[DeliveryKey]),
            @activityId, @actor, @now
        FROM @purged AS p
        INNER JOIN [osdu].[Record] AS r
            ON r.[PartitionId] = @partitionId AND r.[FlowId] = @flowId AND r.[DeliveryKey] = p.[DeliveryKey];

        IF @activityId IS NOT NULL
            INSERT INTO [osdu].[ActivityRecord] ([PartitionId], [FlowId], [DeliveryKey], [ActivityId])
            SELECT @partitionId, @flowId, p.[DeliveryKey], @activityId
            FROM @purged AS p
            WHERE NOT EXISTS (
                SELECT 1 FROM [osdu].[ActivityRecord] AS e
                WHERE e.[PartitionId] = @partitionId AND e.[FlowId] = @flowId AND e.[DeliveryKey] = p.[DeliveryKey] AND e.[ActivityId] = @activityId);

        DELETE a
        FROM [osdu].[Attempt] AS a
        INNER JOIN @purged AS p ON a.[PartitionId] = @partitionId AND a.[FlowId] = @flowId AND a.[DeliveryKey] = p.[DeliveryKey];

        DELETE i
        FROM [osdu].[RecordIdentity] AS i
        INNER JOIN @purged AS p ON i.[PartitionId] = @partitionId AND i.[FlowId] = @flowId AND i.[DeliveryKey] = p.[DeliveryKey];

        DELETE r
        FROM [osdu].[Record] AS r
        INNER JOIN @purged AS p ON r.[PartitionId] = @partitionId AND r.[FlowId] = @flowId AND r.[DeliveryKey] = p.[DeliveryKey];

        IF EXISTS (SELECT 1 FROM @purged)
            DELETE FROM [osdu].[SourceWatermark] WHERE [PartitionId] = @partitionId AND [FlowId] = @flowId;

        SELECT p.[DeliveryKey] FROM @purged AS p;
        """;

    /// <summary>Deletes one slice of removed records from the ledger in one transaction; returns the records it deleted.</summary>
    public static Task<IReadOnlyList<DeliveryKey>> PurgeSliceAsync(
        OsduDbContext db, short partitionId, Guid flowId, IReadOnlyList<DeliveryKey> keys, string actor, long? activityId, DateTime now, CancellationToken ct)
        => InTransactionAsync<IReadOnlyList<DeliveryKey>>(db, async (connection, transaction) =>
        {
            await using var command = Command(connection, transaction, PurgeSql, slice: null);
            command.Parameters.Add(new SqlParameter("@keys", SqlDbType.NVarChar, -1) { Value = System.Text.Json.JsonSerializer.Serialize(keys.Select(k => k.Value)) });
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@flowId", SqlDbType.UniqueIdentifier) { Value = flowId });
            command.Parameters.Add(new SqlParameter("@actor", SqlDbType.NVarChar, 200) { Value = actor });
            command.Parameters.Add(new SqlParameter("@activityId", SqlDbType.BigInt) { Value = activityId is { } id ? id : DBNull.Value });
            command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
            var purged = new List<DeliveryKey>(keys.Count);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                purged.Add(new DeliveryKey(reader.GetGuid(0)));
            }

            return purged;
        }, ct);
}
