using System.Data;
using System.Data.SqlTypes;
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
/// <see cref="OsduLedger"/>, which is the same write row by row. Both write one flow's records: a record is its flow and
/// its delivery key together, and every statement matches on both.
/// <para>Many nodes write the record table at once, so neither write takes a lock on a range of keys, and no statement
/// writes more records than the caller's slice: SQL Server turns the row locks of a statement that takes 5,000 of them on
/// one table into a lock on the whole table, which would stop every other flow's writes while it ran. A completion
/// writes what one flush of the worker closes; staging copies the whole batch once and writes it a slice at a time, each
/// slice in a transaction of its own, so a record is staged whole or not at all and a failure keeps the slices before it.</para>
/// </summary>
internal static class SqlServerLedgerBulk
{
    public const string ProviderName = "Microsoft.EntityFrameworkCore.SqlServer";

    private const int BulkBatchSize = 5000;

    private const int BulkTimeoutSeconds = 600;

    /// <summary>The unique index through which the database refuses a second flow's claim on an OSDU id.</summary>
    private const string ClaimIndex = "IX_Record_ClaimedTargetId";

    /// <summary>The record table's key, through which the database refuses a record another staging inserted first.</summary>
    private const string RecordKey = "PK_Record";

    /// <summary>How many times one slice of staging is tried when a concurrent writer got there first.</summary>
    private const int ContentionAttempts = 5;

    public static bool Applies(OsduDbContext db) => string.Equals(db.Database.ProviderName, ProviderName, StringComparison.Ordinal);

    // The batch is copied once, sorted by key and numbered into slices, so each slice is one contiguous key range and a
    // statement reads its slice by the stage's clustered key. Existing marks the records the slice found, and locked.
    private const string PendingStageSql = """
        CREATE TABLE #PendingStage (
            [Slice] int NOT NULL,
            [Existing] bit NOT NULL DEFAULT 0,
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
            PRIMARY KEY ([Slice], [FlowId], [DeliveryKey]),
            UNIQUE ([FlowId], [DeliveryKey]));
        """;

    // The slice's records that exist are found by key and update-locked to the end of the transaction, so nothing else
    // writes them between the tests below and the update. Only rows that exist are locked, never a range of keys: a
    // record another staging inserts meanwhile is not held off here, and the insert below refuses it instead. A record
    // keeps the OSDU id it was first given, so that id, not the one this work was rendered with, is the one the work is
    // delivered to and the one the claim check compares.
    private const string LockExistingSql = $$"""
        UPDATE s SET [Existing] = 1, [TargetId] = COALESCE(t.[TargetId], s.[TargetId])
        FROM #PendingStage AS s
        INNER JOIN [osdu].[Record] AS t WITH (UPDLOCK, FORCESEEK ({{RecordKey}} ([FlowId], [DeliveryKey]))) ON t.[FlowId] = s.[FlowId] AND t.[DeliveryKey] = s.[DeliveryKey]
        WHERE s.[Slice] = @slice;
        """;

    // Work older than what the record already holds, delivered or queued, is taken out of the stage and named before
    // the write. A comparison with a NULL column is unknown, and refuses nothing.
    private const string RefuseOlderSql = $$"""
        DELETE s
        OUTPUT deleted.[DeliveryKey]
        FROM #PendingStage AS s
        INNER JOIN [osdu].[Record] AS t WITH (UPDLOCK, FORCESEEK ({{RecordKey}} ([FlowId], [DeliveryKey]))) ON t.[FlowId] = s.[FlowId] AND t.[DeliveryKey] = s.[DeliveryKey]
        CROSS APPLY (SELECT CASE WHEN t.[PendingDocumentRef] IS NOT NULL AND t.[Status] IN (N'pending', N'delivering') THEN 1 ELSE 0 END AS [Queued]) AS q
        WHERE s.[Slice] = @slice
          AND ((s.[PendingSourceModifiedUtc] IS NOT NULL
                AND (s.[PendingSourceModifiedUtc] < t.[SourceModifiedUtc]
                     OR (q.[Queued] = 1 AND s.[PendingSourceModifiedUtc] < t.[PendingSourceModifiedUtc])))
           OR (s.[PendingPayload] = 1 AND s.[PendingPayloadModifiedUtc] IS NOT NULL
                AND (s.[PendingPayloadModifiedUtc] < t.[PayloadModifiedUtc]
                     OR (q.[Queued] = 1 AND t.[PendingPayload] = 1 AND s.[PendingPayloadModifiedUtc] < t.[PendingPayloadModifiedUtc]))));
        """;

    // Work for an OSDU id another flow's record has claimed is taken out of the stage and named, with the owning flow
    // and its name as its last submission recorded it. Ids compare exactly (the claim column's binary collation). The
    // claim index is unique, so a stage row meets at most one record, and each is one seek of that index, named here: the
    // filtered index applies because the query repeats its predicate, and a plan that went looking for the first match
    // any other way would read the whole table for every id nobody has claimed. The unique index is also what settles a
    // race between two flows' intakes; this read names the owner.
    private const string RefuseClaimedSql = $$"""
        DELETE s
        OUTPUT deleted.[DeliveryKey], deleted.[TargetId], t.[FlowId], sub.[FlowName]
        FROM #PendingStage AS s
        INNER JOIN [osdu].[Record] AS t WITH (FORCESEEK ({{ClaimIndex}} ([ClaimedTargetId])))
            ON t.[ClaimedTargetId] = s.[TargetId] COLLATE Latin1_General_100_BIN2
            AND t.[ClaimedTargetId] IS NOT NULL
            AND t.[FlowId] <> s.[FlowId]
        LEFT JOIN [osdu].[Submission] AS sub ON sub.[SubmissionId] = t.[LastSubmissionId]
        WHERE s.[Slice] = @slice AND s.[TargetId] IS NOT NULL;
        """;

    // The records the slice found are updated, and the others inserted. A record being delivered right now keeps its
    // status, lease, retry count and last error: the new work queues behind the delivery, whose completion leaves it
    // pending. Every right-hand side reads the row as it was. Staging answers a request to plan the record again, so the
    // request is cleared, and claims the record's OSDU id for its flow the first time it queues a document. A record
    // another staging inserted after the slice looked is never overwritten here: the insert fails on the table's key,
    // and the slice runs again and finds it.
    private const string PendingWriteSql = $$"""
        DECLARE @updated int;
        UPDATE t SET
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
        FROM [osdu].[Record] AS t WITH (FORCESEEK ({{RecordKey}} ([FlowId], [DeliveryKey])))
        INNER JOIN #PendingStage AS s ON t.[FlowId] = s.[FlowId] AND t.[DeliveryKey] = s.[DeliveryKey]
        WHERE s.[Slice] = @slice AND s.[Existing] = 1;
        SET @updated = @@ROWCOUNT;
        INSERT INTO [osdu].[Record] ([DeliveryKey], [FlowId], [SourceKey], [SourceKeyJson], [Label], [MappingName], [TargetId], [ClaimedTargetId], [Status], [LastSubmissionId], [AttemptCount],
                [PendingDocumentRef], [WorkBatch], [PendingRenderContext], [PendingSourceFingerprint], [PendingSourceModifiedUtc],
                [PendingSourceFileName], [PendingSourceRowNumber], [PendingSourceUpdatedUtc],
                [PendingMetadataHash], [PendingPayloadHash], [PendingPayloadModifiedUtc],
                [PendingPayloadLocation], [PendingMetadata], [PendingPayload], [CacheSetId], [Blocked], [CreatedUtc], [UpdatedUtc])
        SELECT s.[DeliveryKey], s.[FlowId], s.[SourceKey], s.[SourceKeyJson], s.[Label], s.[MappingName], s.[TargetId], s.[TargetId] COLLATE Latin1_General_100_BIN2, N'pending', s.[LastSubmissionId], 0,
                s.[PendingDocumentRef], s.[WorkBatch], s.[PendingRenderContext], s.[PendingSourceFingerprint], s.[PendingSourceModifiedUtc],
                s.[PendingSourceFileName], s.[PendingSourceRowNumber], s.[PendingSourceUpdatedUtc],
                s.[PendingMetadataHash], s.[PendingPayloadHash], s.[PendingPayloadModifiedUtc],
                s.[PendingPayloadLocation], s.[PendingMetadata], s.[PendingPayload], s.[CacheSetId], 0, @now, @now
        FROM #PendingStage AS s
        WHERE s.[Slice] = @slice AND s.[Existing] = 0;
        SELECT @updated + @@ROWCOUNT;
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
    /// Stages one flow's pending work, <paramref name="slice"/> records to a transaction. A slice that lost a race is
    /// rolled back and run again, and then reads what the winner left: another flow's intake claimed one of the same OSDU
    /// ids between the slice's claim check and its write (the claim's unique index refuses the write, and the check now
    /// names the owner), another staging of this flow inserted one of the slice's records after the slice looked (the
    /// table's key refuses the insert, and the record is now found and compared), or the database ended a deadlock by
    /// rolling the slice back.
    /// </summary>
    public static async Task<PendingStaging> UpsertPendingAsync(OsduDbContext db, Guid flowId, IReadOnlyList<RecordState> records, int slice, DateTime now, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slice, 1);
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
            try
            {
                var connection = (SqlConnection)db.Database.GetDbConnection();
                await ExecuteAsync(connection, null, PendingStageSql, ct).ConfigureAwait(false);
                using (var table = PendingTable(flowId, records, slice))
                {
                    await BulkCopyAsync(connection, null, "#PendingStage", table, ct).ConfigureAwait(false);
                }

                var staged = 0;
                var refused = new List<DeliveryKey>();
                var conflicts = new List<TargetIdConflict>();
                var slices = (records.Count + slice - 1) / slice;
                for (var index = 0; index < slices; index++)
                {
                    var written = await StageSliceAsync(db, connection, flowId, index, now, ct).ConfigureAwait(false);
                    staged += written.Staged;
                    refused.AddRange(written.Refused);
                    conflicts.AddRange(written.Conflicts);
                }

                await ExecuteAsync(connection, null, "DROP TABLE #PendingStage;", ct).ConfigureAwait(false);
                return new PendingStaging(staged, refused, conflicts);
            }
            finally
            {
                await db.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }

    private static async Task<PendingStaging> StageSliceAsync(OsduDbContext db, SqlConnection connection, Guid flowId, int slice, DateTime now, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // Disposing a transaction that was not committed rolls it back, the stage's changes included, so a
                // slice that runs again starts from the rows it was copied with.
                await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                var transaction = (SqlTransaction)tx.GetDbTransaction();
                await SliceAsync(connection, transaction, LockExistingSql, slice, ct).ConfigureAwait(false);
                var refused = await KeysAsync(connection, transaction, RefuseOlderSql, slice, ct).ConfigureAwait(false);
                var conflicts = await ConflictsAsync(connection, transaction, slice, ct).ConfigureAwait(false);
                var staged = await ScalarAsync(connection, transaction, PendingWriteSql, now, flowId, slice, ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                return new PendingStaging(staged, refused, conflicts);
            }
            catch (Exception ex) when (attempt < ContentionAttempts && IsContention(ex))
            {
                // Another writer got there first. Waiting a moment, longer each time and never the same for two
                // stagings, keeps the two from meeting in the same way again.
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(10, 50) * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Whether a write failed only because another writer got there first: the claim's unique index refused an id another
    /// flow's record claimed meanwhile, the record table's key refused a record another staging inserted meanwhile (2601,
    /// 2627), or the database ended a deadlock by rolling this write back (1205). The context's execution strategy reports
    /// a deadlock wrapped as a transient failure, so the causes are searched too.
    /// </summary>
    internal static bool IsContention(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql
                && sql.Errors.Cast<SqlError>().Any(e => e.Number == DeadlockVictim || (e.Number is 2601 or 2627 && IsRaceIndex(e.Message))))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a statement failed because the database chose it as the victim of a deadlock, and rolled it back.</summary>
    internal static bool IsDeadlock(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql && sql.Errors.Cast<SqlError>().Any(e => e.Number == DeadlockVictim))
            {
                return true;
            }
        }

        return false;
    }

    private const int DeadlockVictim = 1205;

    // SQL Server quotes the index or constraint name in the message of a duplicate key error.
    private static bool IsRaceIndex(string message)
        => message.Contains($"'{ClaimIndex}'", StringComparison.Ordinal) || message.Contains($"'{RecordKey}'", StringComparison.Ordinal);

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

            return await ScalarAsync(connection, transaction, CompletionUpdateSql + "SELECT @@ROWCOUNT;", now, flowId, slice: null, ct).ConfigureAwait(false);
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

    private static async Task BulkCopyAsync(SqlConnection connection, SqlTransaction? transaction, string destination, DataTable table, CancellationToken ct)
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

    private static async Task ExecuteAsync(SqlConnection connection, SqlTransaction? transaction, string sql, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, sql, slice: null);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Runs a statement over one slice of the stage.</summary>
    private static async Task SliceAsync(SqlConnection connection, SqlTransaction transaction, string sql, int slice, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, sql, slice);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<int> ScalarAsync(SqlConnection connection, SqlTransaction transaction, string sql, DateTime now, Guid flowId, int? slice, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, sql, slice);
        command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
        command.Parameters.Add(new SqlParameter("@flowId", SqlDbType.UniqueIdentifier) { Value = flowId });
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is int i ? i : Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static SqlCommand Command(SqlConnection connection, SqlTransaction? transaction, string sql, int? slice)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = BulkTimeoutSeconds;
        if (slice is { } value)
        {
            command.Parameters.Add(new SqlParameter("@slice", SqlDbType.Int) { Value = value });
        }

        return command;
    }

    /// <summary>Runs the claim check over one slice and reads back the records it took out of the stage, with the flow that owns each id.</summary>
    private static async Task<IReadOnlyList<TargetIdConflict>> ConflictsAsync(SqlConnection connection, SqlTransaction transaction, int slice, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, RefuseClaimedSql, slice);
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

    /// <summary>Runs a statement over one slice whose result set is one delivery key per row, and reads the keys back.</summary>
    private static async Task<IReadOnlyList<DeliveryKey>> KeysAsync(SqlConnection connection, SqlTransaction transaction, string sql, int slice, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, sql, slice);
        var keys = new List<DeliveryKey>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            keys.Add(new DeliveryKey(reader.GetGuid(0)));
        }

        return keys;
    }

    /// <summary>
    /// The batch as the stage's rows, in the order SQL Server sorts the keys and numbered <paramref name="slice"/> to a
    /// slice, so each slice is one contiguous range of the record table's key and concurrent stagings lock in one order.
    /// </summary>
    private static DataTable PendingTable(Guid flowId, IReadOnlyList<RecordState> records, int slice)
    {
        var table = new DataTable();
        table.Columns.Add("Slice", typeof(int));
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
        var position = 0;
        foreach (var r in records.OrderBy(r => new SqlGuid(r.DeliveryKey.Value)))
        {
            table.Rows.Add(
                position++ / slice, r.DeliveryKey.Value, flowId, Truncate(r.SourceKey, 400), Value(Truncate(r.SourceKeyJson, 2000)), Value(Truncate(r.Label, 400)), r.MappingName, Value(r.TargetId),
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
