using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Ledger;

/// <summary>An artifact a step reported, as an append writes it.</summary>
internal sealed record ArtifactUpsert(Guid DeliveryKey, DeliveryUnit Unit, TargetArtifact Artifact, Guid? SubmissionId, Guid? RunId, DateTime AtUtc);

/// <summary>How a try ended for its unit, as an append settles the unit's artifacts: delivered, or aborted (held or failed).</summary>
internal sealed record UnitEnd(Guid DeliveryKey, Guid UnitId, bool Delivered, IReadOnlyList<string> Superseded);

/// <summary>What an undo settled one artifact as, as an append writes it.</summary>
internal sealed record ArtifactSettle(long ArtifactId, string State, string? Note, DateTime? NextUndoUtc, Guid? RunId, string SettledBy, bool Minted = false);

/// <summary>What one append writes of artifacts, in its transaction: the steps' artifacts, the units its tries ended, the undos' settlements.</summary>
internal sealed record ArtifactWrites(short PartitionId, Guid FlowId, IReadOnlyList<ArtifactUpsert> Upserts, IReadOnlyList<UnitEnd> Ends, IReadOnlyList<ArtifactSettle> Settlements, DateTime NowUtc)
{
    /// <summary>The records whose version an undo moved, by the write that gave a record back the version the ledger holds.</summary>
    public IReadOnlyList<(Guid DeliveryKey, RecordVersionMove Move)> Moves { get; init; } = [];

    public bool IsEmpty => Upserts.Count == 0 && Ends.Count == 0 && Settlements.Count == 0 && Moves.Count == 0;
}

internal static partial class SqlServerLedgerBulk
{
    // The steps' artifacts, one row per slot of a unit: a later report of the slot (an intent completed by its id, a resumed
    // try reporting the step again) updates the row while it is still open, and never moves one an undo or a commit settled.
    // Only the worker holding a record's lease writes the record's artifacts, so no two writers race for one slot, and no
    // range of keys is locked.
    private const string ArtifactStageSql = """
        CREATE TABLE #ArtifactStage (
            [Ord] int NOT NULL PRIMARY KEY,
            [DeliveryKey] uniqueidentifier NOT NULL,
            [UnitId] uniqueidentifier NOT NULL,
            [UnitStartedUtc] datetime2 NOT NULL,
            [Slot] nvarchar(200) NOT NULL,
            [Role] nvarchar(16) NOT NULL,
            [TargetId] nvarchar(500) COLLATE Latin1_General_100_BIN2 NULL,
            [Locator] nvarchar(1000) NULL,
            [Version] bigint NULL,
            [PriorVersion] bigint NULL,
            [State] nvarchar(16) NOT NULL,
            [Note] nvarchar(1000) NULL,
            [SubmissionId] uniqueidentifier NULL,
            [RunId] uniqueidentifier NULL,
            [AtUtc] datetime2 NOT NULL);
        """;

    private const string ArtifactUpsertSql = """
        SELECT s.* INTO #ArtifactLatest
        FROM (SELECT a.*, ROW_NUMBER() OVER (PARTITION BY a.[DeliveryKey], a.[UnitId], a.[Slot] ORDER BY a.[Ord] DESC) AS [Rank] FROM #ArtifactStage AS a) AS s
        WHERE s.[Rank] = 1;

        -- A resumed try that reads OSDU again sees the unit's own write: a slot first reported as the record the unit created
        -- stays so, and the version a write replaced is the one first reported.
        UPDATE t SET
            [Role] = CASE WHEN t.[Role] = N'record' AND s.[Role] = N'version' THEN t.[Role] ELSE s.[Role] END,
            [TargetId] = COALESCE(s.[TargetId], t.[TargetId]),
            [Locator] = COALESCE(s.[Locator], t.[Locator]),
            [Version] = COALESCE(s.[Version], t.[Version]),
            [PriorVersion] = CASE WHEN t.[Role] = N'record' AND s.[Role] = N'version' THEN t.[PriorVersion] ELSE COALESCE(t.[PriorVersion], s.[PriorVersion]) END,
            [State] = s.[State],
            [Note] = COALESCE(s.[Note], t.[Note]),
            [UpdatedUtc] = s.[AtUtc],
            [SettledUtc] = CASE WHEN s.[State] IN (N'removed', N'kept', N'gone') THEN s.[AtUtc] END,
            [SettledRunId] = CASE WHEN s.[State] IN (N'removed', N'kept', N'gone') THEN s.[RunId] END,
            [SettledBy] = CASE WHEN s.[State] IN (N'removed', N'kept', N'gone') THEN N'route' END
        FROM [osdu].[Artifact] AS t
        INNER JOIN #ArtifactLatest AS s
            ON t.[PartitionId] = @partitionId AND t.[FlowId] = @flowId AND t.[DeliveryKey] = s.[DeliveryKey] AND t.[UnitId] = s.[UnitId] AND t.[Slot] = s.[Slot]
        WHERE t.[State] IN (N'intent', N'pending')
            OR (t.[State] IN (N'removed', N'kept', N'gone') AND t.[SettledBy] = N'route');

        INSERT INTO [osdu].[Artifact] ([PartitionId], [FlowId], [DeliveryKey], [UnitId], [UnitStartedUtc], [Slot], [Role], [TargetId], [Locator],
                [Version], [PriorVersion], [State], [Note], [UndoAttempts], [SubmissionId], [CreatedRunId], [CreatedUtc], [UpdatedUtc],
                [SettledUtc], [SettledRunId], [SettledBy])
        SELECT @partitionId, @flowId, s.[DeliveryKey], s.[UnitId], s.[UnitStartedUtc], s.[Slot], s.[Role], s.[TargetId], s.[Locator],
                s.[Version], s.[PriorVersion], s.[State], s.[Note], 0, s.[SubmissionId], s.[RunId], s.[AtUtc], s.[AtUtc],
                CASE WHEN s.[State] IN (N'removed', N'kept', N'gone') THEN s.[AtUtc] END, CASE WHEN s.[State] IN (N'removed', N'kept', N'gone') THEN s.[RunId] END,
                CASE WHEN s.[State] IN (N'removed', N'kept', N'gone') THEN N'route' END
        FROM #ArtifactLatest AS s
        WHERE NOT EXISTS (
            SELECT 1 FROM [osdu].[Artifact] AS t
            WHERE t.[PartitionId] = @partitionId AND t.[FlowId] = @flowId AND t.[DeliveryKey] = s.[DeliveryKey] AND t.[UnitId] = s.[UnitId] AND t.[Slot] = s.[Slot]);

        DROP TABLE #ArtifactLatest;
        DROP TABLE #ArtifactStage;
        """;

    private const string UnitEndStageSql = """
        CREATE TABLE #UnitEnd (
            [DeliveryKey] uniqueidentifier NOT NULL,
            [UnitId] uniqueidentifier NOT NULL,
            [Delivered] bit NOT NULL,
            PRIMARY KEY ([DeliveryKey], [UnitId]));
        CREATE TABLE #Obsolete (
            [DeliveryKey] uniqueidentifier NOT NULL,
            [UnitId] uniqueidentifier NOT NULL,
            [TargetId] nvarchar(500) COLLATE Latin1_General_100_BIN2 NOT NULL);
        """;

    // A delivered unit: an intent whose call never answered is due (the delivery went on without learning what the call made);
    // the ids it minted are live; what stood only for its progress (the record itself, a session, rows, points, objects) goes,
    // since the record's own state names it now. A minted id the route removed itself stays, as removed. An aborted unit: every
    // artifact still open is due. The ids a delivery made obsolete, minted by an earlier unit of the record and still live, are
    // superseded.
    private const string UnitEndSql = """
        UPDATE a SET [State] = N'due', [UpdatedUtc] = @now,
            [Note] = COALESCE(a.[Note], N'the call that creates it gave no answer this delivery learned, and the delivery completed without it')
        FROM [osdu].[Artifact] AS a
        INNER JOIN #UnitEnd AS e ON a.[PartitionId] = @partitionId AND a.[FlowId] = @flowId AND a.[DeliveryKey] = e.[DeliveryKey] AND a.[UnitId] = e.[UnitId]
        WHERE e.[Delivered] = 1 AND a.[State] = N'intent';

        UPDATE a SET [State] = N'live', [UpdatedUtc] = @now
        FROM [osdu].[Artifact] AS a
        INNER JOIN #UnitEnd AS e ON a.[PartitionId] = @partitionId AND a.[FlowId] = @flowId AND a.[DeliveryKey] = e.[DeliveryKey] AND a.[UnitId] = e.[UnitId]
        WHERE e.[Delivered] = 1 AND a.[State] = N'pending' AND a.[Role] IN (N'dataset', N'content', N'output', N'dataspace');

        DELETE a
        FROM [osdu].[Artifact] AS a
        INNER JOIN #UnitEnd AS e ON a.[PartitionId] = @partitionId AND a.[FlowId] = @flowId AND a.[DeliveryKey] = e.[DeliveryKey] AND a.[UnitId] = e.[UnitId]
        WHERE e.[Delivered] = 1 AND a.[State] IN (N'pending', N'removed') AND a.[Role] NOT IN (N'dataset', N'content', N'output', N'dataspace');

        UPDATE a SET [State] = N'due', [UpdatedUtc] = @now
        FROM [osdu].[Artifact] AS a
        INNER JOIN #UnitEnd AS e ON a.[PartitionId] = @partitionId AND a.[FlowId] = @flowId AND a.[DeliveryKey] = e.[DeliveryKey] AND a.[UnitId] = e.[UnitId]
        WHERE e.[Delivered] = 0 AND a.[State] IN (N'intent', N'pending');

        UPDATE a SET [State] = N'superseded', [UpdatedUtc] = @now
        FROM [osdu].[Artifact] AS a
        INNER JOIN #Obsolete AS o ON a.[PartitionId] = @partitionId AND a.[FlowId] = @flowId AND a.[DeliveryKey] = o.[DeliveryKey] AND a.[TargetId] = o.[TargetId]
        WHERE a.[State] = N'live' AND a.[UnitId] <> o.[UnitId];

        DROP TABLE #Obsolete;
        DROP TABLE #UnitEnd;
        """;

    private const string ArtifactSettleStageSql = """
        CREATE TABLE #ArtifactSettle (
            [ArtifactId] bigint NOT NULL PRIMARY KEY,
            [State] nvarchar(16) NOT NULL,
            [Note] nvarchar(1000) NULL,
            [NextUndoUtc] datetime2 NULL,
            [RunId] uniqueidentifier NULL,
            [SettledBy] nvarchar(200) NOT NULL,
            [Minted] bit NOT NULL);
        """;

    // An undo's settlements, applied only to artifacts still open: an artifact an earlier undo settled is not settled again,
    // which is what makes a sweep that meets an undo the worker already ran harmless. A failed undo counts its try and says
    // when it is tried again; any other outcome settles the artifact for good. A minted id a ledger's deletion removes is
    // settled from live or superseded too.
    private const string ArtifactSettleSql = """
        UPDATE a SET
            [State] = s.[State],
            [Note] = s.[Note],
            [UpdatedUtc] = @now,
            [UndoAttempts] = a.[UndoAttempts] + 1,
            [NextUndoUtc] = s.[NextUndoUtc],
            [SettledUtc] = CASE WHEN s.[State] = N'failed' THEN a.[SettledUtc] ELSE @now END,
            [SettledRunId] = CASE WHEN s.[State] = N'failed' THEN a.[SettledRunId] ELSE s.[RunId] END,
            [SettledBy] = CASE WHEN s.[State] = N'failed' THEN a.[SettledBy] ELSE s.[SettledBy] END
        FROM [osdu].[Artifact] AS a
        INNER JOIN #ArtifactSettle AS s ON a.[PartitionId] = @partitionId AND a.[ArtifactId] = s.[ArtifactId]
        WHERE a.[FlowId] = @flowId
            AND (a.[State] IN (N'intent', N'pending', N'due', N'failed') OR (s.[Minted] = 1 AND a.[State] IN (N'live', N'superseded')));
        DROP TABLE #ArtifactSettle;
        """;

    private const string VersionMoveStageSql = """
        CREATE TABLE #VersionMove ([DeliveryKey] uniqueidentifier NOT NULL PRIMARY KEY, [From] bigint NOT NULL, [To] bigint NOT NULL);
        """;

    // A record an undo gave back the version the ledger holds now holds it under the version the write-back got: the ledger
    // moves with it, and only while it still holds the version written back, so a later delivery's version is never moved.
    private const string VersionMoveSql = """
        UPDATE r SET
            [TargetVersion] = m.[To],
            [TargetStateJson] = CASE WHEN r.[TargetStateJson] IS NULL OR ISJSON(r.[TargetStateJson]) = 0 THEN r.[TargetStateJson]
                ELSE JSON_MODIFY(r.[TargetStateJson], '$.version', CONVERT(nvarchar(20), m.[To])) END,
            [UpdatedUtc] = @now
        FROM [osdu].[Record] AS r
        INNER JOIN #VersionMove AS m ON r.[PartitionId] = @partitionId AND r.[FlowId] = @flowId AND r.[DeliveryKey] = m.[DeliveryKey]
        WHERE r.[TargetVersion] = m.[From];
        DROP TABLE #VersionMove;
        """;

    /// <summary>Writes one append's artifacts in the caller's transaction: upserts, then the units its tries ended, then settlements, then the versions undos moved.</summary>
    private static async Task WriteArtifactsAsync(SqlConnection connection, SqlTransaction transaction, ArtifactWrites writes, CancellationToken ct)
    {
        if (writes.IsEmpty)
        {
            return;
        }

        if (writes.Upserts.Count > 0)
        {
            await ExecuteAsync(connection, transaction, ArtifactStageSql, ct).ConfigureAwait(false);
            using (var table = ArtifactStageTable(writes.Upserts))
            {
                await BulkCopyAsync(connection, transaction, "#ArtifactStage", table, ct).ConfigureAwait(false);
            }

            await ArtifactStatementAsync(connection, transaction, ArtifactUpsertSql, writes, ct).ConfigureAwait(false);
        }

        if (writes.Ends.Count > 0)
        {
            await ExecuteAsync(connection, transaction, UnitEndStageSql, ct).ConfigureAwait(false);
            using (var ends = UnitEndTable(writes.Ends))
            {
                await BulkCopyAsync(connection, transaction, "#UnitEnd", ends, ct).ConfigureAwait(false);
            }

            using (var obsolete = ObsoleteTable(writes.Ends))
            {
                if (obsolete.Rows.Count > 0)
                {
                    await BulkCopyAsync(connection, transaction, "#Obsolete", obsolete, ct).ConfigureAwait(false);
                }
            }

            await ArtifactStatementAsync(connection, transaction, UnitEndSql, writes, ct).ConfigureAwait(false);
        }

        if (writes.Settlements.Count > 0)
        {
            await ExecuteAsync(connection, transaction, ArtifactSettleStageSql, ct).ConfigureAwait(false);
            using (var table = SettleTable(writes.Settlements))
            {
                await BulkCopyAsync(connection, transaction, "#ArtifactSettle", table, ct).ConfigureAwait(false);
            }

            await ArtifactStatementAsync(connection, transaction, ArtifactSettleSql, writes, ct).ConfigureAwait(false);
        }

        if (writes.Moves.Count > 0)
        {
            await ExecuteAsync(connection, transaction, VersionMoveStageSql, ct).ConfigureAwait(false);
            using (var table = new DataTable())
            {
                table.Columns.Add("DeliveryKey", typeof(Guid));
                table.Columns.Add("From", typeof(long));
                table.Columns.Add("To", typeof(long));
                foreach (var (key, move) in writes.Moves.GroupBy(m => m.DeliveryKey).Select(g => g.Last()))
                {
                    table.Rows.Add(key, move.From, move.To);
                }

                await BulkCopyAsync(connection, transaction, "#VersionMove", table, ct).ConfigureAwait(false);
            }

            await ArtifactStatementAsync(connection, transaction, VersionMoveSql, writes, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes undos outside a lease (the sweep, a removal): their attempts and what they settled, in one transaction.
    /// </summary>
    public static Task<int> SettleArtifactsAsync(OsduDbContext db, IReadOnlyList<DeliveryAttempt> attempts, ArtifactWrites writes, CancellationToken ct)
        => InTransactionAsync(db, async (connection, transaction) =>
        {
            if (attempts.Count > 0)
            {
                using var table = AttemptTable(attempts);
                await BulkCopyAsync(connection, transaction, "[osdu].[Attempt]", table, ct).ConfigureAwait(false);
            }

            await WriteArtifactsAsync(connection, transaction, writes, ct).ConfigureAwait(false);
            return attempts.Count + writes.Settlements.Count;
        }, ct);

    // The sweep's page, a record at a time: what is due, what failed and is past its backoff, and the intents and pending
    // artifacts of a unit their record no longer carries (its steps were dropped, or a newer unit began). The record's unit is
    // read from its steps, where the worker keeps it. A record a lease holds is left out, whatever the lease's state: until the
    // lease is applied the record does not yet say which unit it carries, so its artifacts cannot be told abandoned. A page
    // takes whole records, the next @max of them after @after in key order, so one record's undo is never split between pages.
    private const string SweepSql = """
        WITH [Eligible] AS (
            SELECT a.[ArtifactId], a.[DeliveryKey]
            FROM [osdu].[Artifact] AS a
            INNER JOIN [osdu].[Record] AS r ON r.[PartitionId] = a.[PartitionId] AND r.[FlowId] = a.[FlowId] AND r.[DeliveryKey] = a.[DeliveryKey]
            WHERE a.[PartitionId] = @partitionId AND a.[FlowId] = @flowId
              AND a.[State] IN (N'intent', N'pending', N'due', N'failed')
              AND r.[LeaseOwner] IS NULL
              AND (a.[State] = N'due'
                   OR (a.[State] = N'failed' AND (@exhausted = 1 OR a.[UndoAttempts] < @maxAttempts) AND (a.[NextUndoUtc] IS NULL OR a.[NextUndoUtc] <= @now))
                   OR (a.[State] IN (N'intent', N'pending')
                       AND (r.[PendingStepJson] IS NULL
                            OR TRY_CONVERT(uniqueidentifier, JSON_VALUE(r.[PendingStepJson], '$."$unit".id')) IS NULL
                            OR TRY_CONVERT(uniqueidentifier, JSON_VALUE(r.[PendingStepJson], '$."$unit".id')) <> a.[UnitId])))),
        [Page] AS (
            SELECT TOP (@max) e.[DeliveryKey]
            FROM [Eligible] AS e
            WHERE @after IS NULL OR e.[DeliveryKey] > @after
            GROUP BY e.[DeliveryKey]
            ORDER BY e.[DeliveryKey])
        SELECT e.[ArtifactId], e.[DeliveryKey]
        FROM [Eligible] AS e
        INNER JOIN [Page] AS p ON p.[DeliveryKey] = e.[DeliveryKey]
        ORDER BY e.[DeliveryKey], e.[ArtifactId];
        """;

    /// <summary>
    /// The ids of the sweep's next page, the eligible artifacts of the next <paramref name="max"/> records after
    /// <paramref name="after"/>, in the order the database orders the records' keys, and each record's in the order they were
    /// created; with the last record's key, the cursor of the page after it.
    /// </summary>
    public static async Task<(IReadOnlyList<long> Ids, Guid? Last)> SweepArtifactIdsAsync(
        OsduDbContext db, short partitionId, Guid flowId, DateTime now, Guid? after, int max, bool exhausted, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            await using var command = Command(connection, null, SweepSql, slice: null);
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@flowId", SqlDbType.UniqueIdentifier) { Value = flowId });
            command.Parameters.Add(new SqlParameter("@after", SqlDbType.UniqueIdentifier) { Value = after is { } cursor ? cursor : DBNull.Value });
            command.Parameters.Add(new SqlParameter("@max", SqlDbType.Int) { Value = max });
            command.Parameters.Add(new SqlParameter("@exhausted", SqlDbType.Bit) { Value = exhausted });
            command.Parameters.Add(new SqlParameter("@maxAttempts", SqlDbType.Int) { Value = ArtifactLimits.MaxUndoAttempts });
            command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
            var ids = new List<long>();
            Guid? last = null;
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                ids.Add(reader.GetInt64(0));
                last = reader.GetGuid(1);
            }

            return (ids, last);
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static async Task ArtifactStatementAsync(SqlConnection connection, SqlTransaction transaction, string sql, ArtifactWrites writes, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, sql, slice: null);
        command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = writes.PartitionId });
        command.Parameters.Add(new SqlParameter("@flowId", SqlDbType.UniqueIdentifier) { Value = writes.FlowId });
        command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = writes.NowUtc });
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static DataTable ArtifactStageTable(IReadOnlyList<ArtifactUpsert> upserts)
    {
        var table = new DataTable();
        table.Columns.Add("Ord", typeof(int));
        table.Columns.Add("DeliveryKey", typeof(Guid));
        table.Columns.Add("UnitId", typeof(Guid));
        table.Columns.Add("UnitStartedUtc", typeof(DateTime));
        table.Columns.Add("Slot", typeof(string));
        table.Columns.Add("Role", typeof(string));
        table.Columns.Add("TargetId", typeof(string));
        table.Columns.Add("Locator", typeof(string));
        table.Columns.Add("Version", typeof(long));
        table.Columns.Add("PriorVersion", typeof(long));
        table.Columns.Add("State", typeof(string));
        table.Columns.Add("Note", typeof(string));
        table.Columns.Add("SubmissionId", typeof(Guid));
        table.Columns.Add("RunId", typeof(Guid));
        table.Columns.Add("AtUtc", typeof(DateTime));
        var ord = 0;
        foreach (var u in upserts)
        {
            var a = u.Artifact;
            table.Rows.Add(
                ord++, u.DeliveryKey, u.Unit.Id, u.Unit.StartedUtc, a.Slot, a.Role, Value(a.TargetId), Value(ArtifactLimits.Locator(a.Locator)),
                Value(a.Version), Value(a.PriorVersion), ArtifactStatuses.Name(a.Status), Value(ArtifactLimits.Note(a.Note)),
                Value(u.SubmissionId), Value(u.RunId), u.AtUtc);
        }

        return table;
    }

    private static DataTable UnitEndTable(IReadOnlyList<UnitEnd> ends)
    {
        var table = new DataTable();
        table.Columns.Add("DeliveryKey", typeof(Guid));
        table.Columns.Add("UnitId", typeof(Guid));
        table.Columns.Add("Delivered", typeof(bool));
        foreach (var end in ends.GroupBy(e => (e.DeliveryKey, e.UnitId)).Select(g => g.Last()))
        {
            table.Rows.Add(end.DeliveryKey, end.UnitId, end.Delivered);
        }

        return table;
    }

    private static DataTable ObsoleteTable(IReadOnlyList<UnitEnd> ends)
    {
        var table = new DataTable();
        table.Columns.Add("DeliveryKey", typeof(Guid));
        table.Columns.Add("UnitId", typeof(Guid));
        table.Columns.Add("TargetId", typeof(string));
        foreach (var end in ends.Where(e => e.Delivered))
        {
            foreach (var id in end.Superseded.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct(StringComparer.Ordinal))
            {
                table.Rows.Add(end.DeliveryKey, end.UnitId, id);
            }
        }

        return table;
    }

    private static DataTable SettleTable(IReadOnlyList<ArtifactSettle> settlements)
    {
        var table = new DataTable();
        table.Columns.Add("ArtifactId", typeof(long));
        table.Columns.Add("State", typeof(string));
        table.Columns.Add("Note", typeof(string));
        table.Columns.Add("NextUndoUtc", typeof(DateTime));
        table.Columns.Add("RunId", typeof(Guid));
        table.Columns.Add("SettledBy", typeof(string));
        table.Columns.Add("Minted", typeof(bool));
        foreach (var s in settlements.GroupBy(s => s.ArtifactId).Select(g => g.Last()))
        {
            table.Rows.Add(s.ArtifactId, s.State, Value(ArtifactLimits.Note(s.Note)), Value(s.NextUndoUtc), Value(s.RunId), Truncate(s.SettledBy, 200) ?? "worker", s.Minted);
        }

        return table;
    }
}
