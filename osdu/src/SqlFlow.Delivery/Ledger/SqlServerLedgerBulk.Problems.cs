using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The statements behind releases and problems (docs/ledger.md, Problems): a release a slice of records at a time, naming
/// each record it released under its intervention; the pages of keys a release of every blocked record or of one problem
/// walks; and the reads that count a ledger's problems, name their files and sort the records blocked before the ledger
/// kept problems. Every one reads an index that holds what it is about, so it costs what it reaches.
/// </summary>
internal static partial class SqlServerLedgerBulk
{
    // One slice of a release, in one transaction with the rows that name what it released. The named records of one ledger
    // that are blocked (held, failed, or removed from OSDU), and with @waiting the named waiting records, each found by the
    // table's key. A record still holding its rendered document goes back to pending for the worker, its tries counted
    // afresh; any other blocked record is unblocked and asked to be planned again; a waiting record goes back to pending
    // without its references, so it is sent without waiting. Every right-hand side reads the row as it was. Released, a
    // record has no problem any more. With an activity, each record released is named under it.
    private const string ReleaseSliceSql = $$"""
        DECLARE @released TABLE ([DeliveryKey] uniqueidentifier NOT NULL PRIMARY KEY);
        UPDATE r SET
            r.[Status] = CASE WHEN r.[Status] = N'waiting' OR r.[PendingDocumentRef] IS NOT NULL THEN N'pending' ELSE r.[Status] END,
            r.[Blocked] = 0,
            r.[ProblemHash] = NULL,
            r.[AttemptCount] = CASE WHEN r.[Status] <> N'waiting' AND r.[PendingDocumentRef] IS NOT NULL THEN 0 ELSE r.[AttemptCount] END,
            r.[NextAttemptUtc] = CASE WHEN r.[Status] = N'waiting' OR r.[PendingDocumentRef] IS NOT NULL THEN NULL ELSE r.[NextAttemptUtc] END,
            r.[PlanRequestedUtc] = CASE WHEN r.[Status] <> N'waiting' AND r.[PendingDocumentRef] IS NULL THEN @now ELSE r.[PlanRequestedUtc] END,
            r.[LastError] = CASE WHEN r.[Status] <> N'waiting' AND r.[PendingDocumentRef] IS NULL THEN @note ELSE NULL END,
            r.[WaitingFor] = CASE WHEN r.[Status] = N'waiting' THEN NULL ELSE r.[WaitingFor] END,
            r.[PendingReferences] = CASE WHEN r.[Status] = N'waiting' THEN NULL ELSE r.[PendingReferences] END,
            r.[UpdatedUtc] = @now
        OUTPUT inserted.[DeliveryKey] INTO @released ([DeliveryKey])
        FROM (SELECT DISTINCT CAST(k.[value] AS uniqueidentifier) AS [DeliveryKey] FROM OPENJSON(@keys) AS k) AS n
        INNER JOIN [osdu].[Record] AS r WITH (FORCESEEK ({{RecordKey}} ([PartitionId], [FlowId], [DeliveryKey])))
            ON r.[PartitionId] = @partitionId AND r.[FlowId] = @flowId AND r.[DeliveryKey] = n.[DeliveryKey]
        WHERE (r.[Blocked] = 1 AND r.[Status] IN (N'held', N'failed', N'deleted'))
           OR (@waiting = 1 AND r.[Status] = N'waiting');
        IF @activityId IS NOT NULL
            INSERT INTO [osdu].[ActivityRecord] ([PartitionId], [FlowId], [DeliveryKey], [ActivityId])
            SELECT @partitionId, @flowId, x.[DeliveryKey], @activityId
            FROM @released AS x
            WHERE NOT EXISTS (
                SELECT 1 FROM [osdu].[ActivityRecord] AS a
                WHERE a.[PartitionId] = @partitionId AND a.[FlowId] = @flowId AND a.[DeliveryKey] = x.[DeliveryKey] AND a.[ActivityId] = @activityId);
        SELECT COUNT(*) FROM @released;
        """;

    // One slice of a redelivery, in one transaction with the rows that name what it marked. Each named record of one ledger
    // (with @delivered, each that OSDU holds: delivered, with an id) forgets what OSDU holds of the part @scope names
    // (metadata, payload or all; a payload sent in parts keeps @marker on its delivered hash, naming the parts to send) and
    // its fingerprints, and is asked to be planned again. A record still blocked by a problem keeps its error, which is
    // what its problem was read from. With an activity, each record marked is named under it.
    private const string RedeliverSliceSql = $$"""
        DECLARE @marked TABLE ([DeliveryKey] uniqueidentifier NOT NULL PRIMARY KEY);
        UPDATE r SET
            r.[MetadataHash] = CASE WHEN @scope = N'payload' THEN r.[MetadataHash] ELSE NULL END,
            r.[PayloadHash] = CASE WHEN @scope = N'metadata' THEN r.[PayloadHash] WHEN @scope = N'payload' THEN @marker ELSE NULL END,
            r.[PayloadModifiedUtc] = CASE WHEN @scope = N'metadata' THEN r.[PayloadModifiedUtc] ELSE NULL END,
            r.[SourceFingerprint] = NULL,
            r.[PendingSourceFingerprint] = NULL,
            r.[PlanRequestedUtc] = @now,
            r.[LastError] = CASE WHEN r.[ProblemHash] IS NOT NULL THEN r.[LastError] ELSE @note END,
            r.[UpdatedUtc] = @now
        OUTPUT inserted.[DeliveryKey] INTO @marked ([DeliveryKey])
        FROM (SELECT DISTINCT CAST(k.[value] AS uniqueidentifier) AS [DeliveryKey] FROM OPENJSON(@keys) AS k) AS n
        INNER JOIN [osdu].[Record] AS r WITH (FORCESEEK ({{RecordKey}} ([PartitionId], [FlowId], [DeliveryKey])))
            ON r.[PartitionId] = @partitionId AND r.[FlowId] = @flowId AND r.[DeliveryKey] = n.[DeliveryKey]
        WHERE @delivered = 0 OR (r.[Status] = N'delivered' AND r.[TargetId] IS NOT NULL);
        IF @activityId IS NOT NULL
            INSERT INTO [osdu].[ActivityRecord] ([PartitionId], [FlowId], [DeliveryKey], [ActivityId])
            SELECT @partitionId, @flowId, x.[DeliveryKey], @activityId
            FROM @marked AS x
            WHERE NOT EXISTS (
                SELECT 1 FROM [osdu].[ActivityRecord] AS a
                WHERE a.[PartitionId] = @partitionId AND a.[FlowId] = @flowId AND a.[DeliveryKey] = x.[DeliveryKey] AND a.[ActivityId] = @activityId);
        SELECT COUNT(*) FROM @marked;
        """;

    // A page of the records OSDU holds of one ledger, in the status index's order, after the last of the page before. A
    // record this redelivery marked carries its moment as both its update time and its request, so the walk passes it over
    // when it meets it again at the end. No bound on the update time: the moments were written by the clocks of whichever
    // node or control plane wrote the record, which a bound read from this one's clock would cut at random.
    private const string DeliveredPageSql = """
        SELECT TOP (@slice) r.[UpdatedUtc], r.[DeliveryKey]
        FROM [osdu].[Record] AS r
        WHERE r.[PartitionId] = @partitionId AND r.[FlowId] = @flowId AND r.[Status] = N'delivered'
          AND r.[UpdatedUtc] >= @afterUtc
          AND NOT (r.[UpdatedUtc] = @afterUtc AND r.[DeliveryKey] <= @afterKey)
          AND (r.[PlanRequestedUtc] IS NULL OR r.[PlanRequestedUtc] <> @now OR r.[UpdatedUtc] <> @now)
        ORDER BY r.[UpdatedUtc], r.[DeliveryKey];
        """;

    // A page of the records one problem keeps blocked, in the problem index's own order (its update time, then its key),
    // after the last record of the page before. A record the release takes leaves the index and the walk only moves
    // forward, so it ends; a record a run blocks with the same problem while the walk goes is reached too, since it is
    // the problem's as much as the others.
    private const string ProblemPageSql = """
        SELECT TOP (@slice) r.[UpdatedUtc], r.[DeliveryKey]
        FROM [osdu].[Record] AS r
        WHERE r.[PartitionId] = @partitionId AND r.[FlowId] = @flowId AND r.[ProblemHash] IS NOT NULL AND r.[ProblemHash] = @problem
          AND r.[UpdatedUtc] >= @afterUtc
          AND NOT (r.[UpdatedUtc] = @afterUtc AND r.[DeliveryKey] <= @afterKey)
        ORDER BY r.[UpdatedUtc], r.[DeliveryKey];
        """;

    // A page of the flow's blocked records in one custody state, in the status index's order, as above. A record released
    // while it is walked keeps its state with a later update time and is no longer blocked, so it is passed over.
    private const string StatusPageSql = """
        SELECT TOP (@slice) r.[UpdatedUtc], r.[DeliveryKey]
        FROM [osdu].[Record] AS r
        WHERE r.[PartitionId] = @partitionId AND r.[FlowId] = @flowId AND r.[Status] = @status AND r.[Blocked] = 1
          AND r.[UpdatedUtc] >= @afterUtc
          AND NOT (r.[UpdatedUtc] = @afterUtc AND r.[DeliveryKey] <= @afterKey)
        ORDER BY r.[UpdatedUtc], r.[DeliveryKey];
        """;

    // A ledger's problems, counted from the problem index alone: per problem, its records, how many are held and failed,
    // and when they last changed; and before the page is cut, how many problems and records there are in all. The largest
    // first, ties broken by the hash so pages are stable.
    private const string ProblemsSql = """
        SELECT TOP (@max) r.[ProblemHash],
            COUNT_BIG(*) AS [Records],
            COUNT_BIG(CASE WHEN r.[Status] = N'held' THEN 1 END) AS [Held],
            COUNT_BIG(CASE WHEN r.[Status] = N'failed' THEN 1 END) AS [Failed],
            MIN(r.[UpdatedUtc]) AS [Oldest],
            MAX(r.[UpdatedUtc]) AS [Newest],
            COUNT_BIG(*) OVER () AS [Problems],
            SUM(COUNT_BIG(*)) OVER () AS [Total]
        FROM [osdu].[Record] AS r
        WHERE r.[PartitionId] = @partitionId AND r.[FlowId] = @flowId AND r.[ProblemHash] IS NOT NULL
        GROUP BY r.[ProblemHash]
        ORDER BY [Records] DESC, r.[ProblemHash];
        """;

    // One problem of a ledger, counted as the listing counts it: a seek of the problem's range of the index.
    private const string ProblemSql = """
        SELECT r.[ProblemHash],
            COUNT_BIG(*) AS [Records],
            COUNT_BIG(CASE WHEN r.[Status] = N'held' THEN 1 END) AS [Held],
            COUNT_BIG(CASE WHEN r.[Status] = N'failed' THEN 1 END) AS [Failed],
            MIN(r.[UpdatedUtc]) AS [Oldest],
            MAX(r.[UpdatedUtc]) AS [Newest],
            CAST(1 AS bigint) AS [Problems],
            COUNT_BIG(*) AS [Total]
        FROM [osdu].[Record] AS r
        WHERE r.[PartitionId] = @partitionId AND r.[FlowId] = @flowId AND r.[ProblemHash] IS NOT NULL AND r.[ProblemHash] = @problem
        GROUP BY r.[ProblemHash];
        """;

    // The most recently changed record of each problem named, and the one changed longest ago: a seek of each end of each
    // problem's range.
    private const string ProblemExamplesSql = """
        SELECT p.[ProblemHash], n.[DeliveryKey], o.[DeliveryKey]
        FROM (SELECT DISTINCT CAST(j.[value] AS bigint) AS [ProblemHash] FROM OPENJSON(@problems) AS j) AS p
        CROSS APPLY (
            SELECT TOP (1) r.[DeliveryKey]
            FROM [osdu].[Record] AS r
            WHERE r.[PartitionId] = @partitionId AND r.[FlowId] = @flowId AND r.[ProblemHash] IS NOT NULL AND r.[ProblemHash] = p.[ProblemHash]
            ORDER BY r.[UpdatedUtc] DESC, r.[DeliveryKey] DESC) AS n
        CROSS APPLY (
            SELECT TOP (1) r.[DeliveryKey]
            FROM [osdu].[Record] AS r
            WHERE r.[PartitionId] = @partitionId AND r.[FlowId] = @flowId AND r.[ProblemHash] IS NOT NULL AND r.[ProblemHash] = p.[ProblemHash]
            ORDER BY r.[UpdatedUtc], r.[DeliveryKey]) AS o;
        """;

    // The records of one problem at the positions named, newest first, numbered in the problem index's own order: one pass
    // over the problem's range of the index, as far as the last position.
    private const string ProblemSamplesSql = """
        SELECT x.[DeliveryKey]
        FROM (
            SELECT r.[DeliveryKey], ROW_NUMBER() OVER (ORDER BY r.[UpdatedUtc] DESC, r.[DeliveryKey] DESC) AS [Position]
            FROM [osdu].[Record] AS r
            WHERE r.[PartitionId] = @partitionId AND r.[FlowId] = @flowId AND r.[ProblemHash] IS NOT NULL AND r.[ProblemHash] = @problem) AS x
        WHERE x.[Position] IN (SELECT CAST(j.[value] AS bigint) FROM OPENJSON(@positions) AS j)
        ORDER BY x.[Position];
        """;

    // The files one problem's records were left at, from the file the problem index includes.
    private const string ProblemFilesSql = """
        SELECT TOP (@max) r.[PendingSourceFileName], COUNT_BIG(*) AS [Records]
        FROM [osdu].[Record] AS r
        WHERE r.[PartitionId] = @partitionId AND r.[FlowId] = @flowId AND r.[ProblemHash] IS NOT NULL AND r.[ProblemHash] = @problem
        GROUP BY r.[PendingSourceFileName]
        ORDER BY [Records] DESC, r.[PendingSourceFileName];
        """;

    // The ledger's blocked records it has not sorted into problems yet, from the index that holds them alone; the statement repeats the
    // index's filter, which is what lets the server use a filtered index it is told to.
    private const string UnsortedCountSql = $$"""
        SELECT COUNT_BIG(*)
        FROM [osdu].[Record] AS r WITH (INDEX ([{{DeliveryModel.UnsortedProblemIndex}}]))
        WHERE r.[PartitionId] = @partitionId AND r.[FlowId] = @flowId
          AND r.[ProblemHash] IS NULL AND r.[Blocked] = 1 AND r.[Status] IN (N'held', N'failed');
        """;

    // The next records to sort, across every ledger, in the index's order. A record sorted leaves the index, so the next
    // page starts where this one ended without a cursor.
    private const string UnsortedPageSql = $$"""
        SELECT TOP (@max) r.[PartitionId], r.[FlowId], r.[DeliveryKey], r.[LastError]
        FROM [osdu].[Record] AS r WITH (INDEX ([{{DeliveryModel.UnsortedProblemIndex}}]))
        WHERE r.[ProblemHash] IS NULL AND r.[Blocked] = 1 AND r.[Status] IN (N'held', N'failed')
        ORDER BY r.[PartitionId], r.[FlowId], r.[DeliveryKey];
        """;

    // Sorts records into the problem their error was read as, each found by the table's key, and only while it is still the
    // blocked record that error was read from: one changed meanwhile is left to its next page or to the write that changed it.
    private const string SortSql = $$"""
        UPDATE r SET r.[ProblemHash] = s.[ProblemHash]
        FROM OPENJSON(@sorted) WITH (
            [PartitionId] smallint '$.p',
            [FlowId] uniqueidentifier '$.f',
            [DeliveryKey] uniqueidentifier '$.k',
            [ProblemHash] bigint '$.h',
            [Error] nvarchar(2000) '$.e') AS s
        INNER JOIN [osdu].[Record] AS r WITH (FORCESEEK ({{RecordKey}} ([PartitionId], [FlowId], [DeliveryKey])))
            ON r.[PartitionId] = s.[PartitionId] AND r.[FlowId] = s.[FlowId] AND r.[DeliveryKey] = s.[DeliveryKey]
        WHERE r.[ProblemHash] IS NULL AND r.[Blocked] = 1 AND r.[Status] IN (N'held', N'failed')
          AND ((r.[LastError] IS NULL AND s.[Error] IS NULL) OR r.[LastError] = s.[Error] COLLATE Latin1_General_100_BIN2);
        """;

    /// <summary>
    /// Releases the named records of one ledger in one transaction (see the statement), naming each one released under
    /// <paramref name="activityId"/> when given. Returns how many it released.
    /// </summary>
    public static Task<int> ReleaseSliceAsync(
        OsduDbContext db, short partitionId, Guid flowId, IReadOnlyList<Guid> keys, bool waiting, long? activityId, string note, DateTime now, CancellationToken ct)
        => InTransactionAsync(db, async (connection, transaction) =>
        {
            await using var command = Command(connection, transaction, ReleaseSliceSql, slice: null);
            command.Parameters.Add(new SqlParameter("@keys", SqlDbType.NVarChar, -1) { Value = System.Text.Json.JsonSerializer.Serialize(keys) });
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@flowId", SqlDbType.UniqueIdentifier) { Value = flowId });
            command.Parameters.Add(new SqlParameter("@waiting", SqlDbType.Bit) { Value = waiting });
            command.Parameters.Add(new SqlParameter("@activityId", SqlDbType.BigInt) { Value = activityId is { } id ? id : DBNull.Value });
            command.Parameters.Add(new SqlParameter("@note", SqlDbType.NVarChar, 2000) { Value = note });
            command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        }, ct);

    /// <summary>
    /// Marks the named records of one ledger for redelivery of <paramref name="scope"/> in one transaction (see the
    /// statement), naming each one marked under <paramref name="activityId"/> when given. Returns how many it marked.
    /// </summary>
    public static Task<int> RedeliverSliceAsync(
        OsduDbContext db, short partitionId, Guid flowId, IReadOnlyList<Guid> keys, string scope, string? marker, bool deliveredOnly, long? activityId, string note,
        DateTime now, CancellationToken ct)
        => InTransactionAsync(db, async (connection, transaction) =>
        {
            await using var command = Command(connection, transaction, RedeliverSliceSql, slice: null);
            command.Parameters.Add(new SqlParameter("@keys", SqlDbType.NVarChar, -1) { Value = System.Text.Json.JsonSerializer.Serialize(keys) });
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@flowId", SqlDbType.UniqueIdentifier) { Value = flowId });
            command.Parameters.Add(new SqlParameter("@scope", SqlDbType.NVarChar, 16) { Value = scope });
            command.Parameters.Add(new SqlParameter("@marker", SqlDbType.NVarChar, 64) { Value = marker is null ? DBNull.Value : marker });
            command.Parameters.Add(new SqlParameter("@delivered", SqlDbType.Bit) { Value = deliveredOnly });
            command.Parameters.Add(new SqlParameter("@activityId", SqlDbType.BigInt) { Value = activityId is { } id ? id : DBNull.Value });
            command.Parameters.Add(new SqlParameter("@note", SqlDbType.NVarChar, 2000) { Value = note });
            command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        }, ct);

    /// <summary>
    /// The next page of the records OSDU holds of one ledger after <paramref name="after"/>, at most <paramref name="slice"/>,
    /// in the status index's order, passing over those the redelivery that began at <paramref name="now"/> marked: the walk
    /// of a redelivery of every delivered record.
    /// </summary>
    public static async Task<IReadOnlyList<WalkPosition>> DeliveredPageAsync(
        OsduDbContext db, short partitionId, Guid flowId, WalkPosition after, DateTime now, int slice, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            await using var command = Command(connection, null, DeliveredPageSql, slice);
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@flowId", SqlDbType.UniqueIdentifier) { Value = flowId });
            command.Parameters.Add(new SqlParameter("@afterUtc", SqlDbType.DateTime2) { Value = after.UpdatedUtc });
            command.Parameters.Add(new SqlParameter("@afterKey", SqlDbType.UniqueIdentifier) { Value = after.DeliveryKey });
            command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
            var page = new List<WalkPosition>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                page.Add(new WalkPosition(reader.GetDateTime(0), reader.GetGuid(1)));
            }

            return page;
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>A place in an index's order of update time, then key: where the next page of a walk starts after.</summary>
    public readonly record struct WalkPosition(DateTime UpdatedUtc, Guid DeliveryKey)
    {
        /// <summary>Before every record.</summary>
        public static WalkPosition Start { get; } = new(DateTime.MinValue, Guid.Empty);
    }

    /// <summary>
    /// The next page of a release's walk: the records <paramref name="problem"/> keeps blocked, or with no problem the
    /// blocked records in <paramref name="status"/>, after <paramref name="after"/>, at most <paramref name="slice"/>, in the
    /// index's order.
    /// </summary>
    public static async Task<IReadOnlyList<WalkPosition>> ReleasePageAsync(
        OsduDbContext db, short partitionId, Guid flowId, long? problem, string? status, WalkPosition after, int slice, CancellationToken ct)
    {
        if (problem is null == status is null)
        {
            throw new ArgumentException("A release walks one problem's records or one custody state's, never both or neither.", nameof(problem));
        }

        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            await using var command = Command(connection, null, problem is null ? StatusPageSql : ProblemPageSql, slice);
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@flowId", SqlDbType.UniqueIdentifier) { Value = flowId });
            if (problem is { } hash)
            {
                command.Parameters.Add(new SqlParameter("@problem", SqlDbType.BigInt) { Value = hash });
            }
            else
            {
                command.Parameters.Add(new SqlParameter("@status", SqlDbType.NVarChar, 16) { Value = status });
            }

            command.Parameters.Add(new SqlParameter("@afterUtc", SqlDbType.DateTime2) { Value = after.UpdatedUtc });
            command.Parameters.Add(new SqlParameter("@afterKey", SqlDbType.UniqueIdentifier) { Value = after.DeliveryKey });
            var page = new List<WalkPosition>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                page.Add(new WalkPosition(reader.GetDateTime(0), reader.GetGuid(1)));
            }

            return page;
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>One problem as the problem index counts it.</summary>
    /// <param name="Problem">The problem's hash.</param>
    /// <param name="Records">Its records.</param>
    /// <param name="Held">Of those, the held.</param>
    /// <param name="Failed">Of those, the failed.</param>
    /// <param name="OldestUtc">When its record that changed longest ago last changed.</param>
    /// <param name="NewestUtc">When its most recently changed record last changed.</param>
    /// <param name="Problems">How many problems the ledger has in all.</param>
    /// <param name="Total">How many records they keep blocked in all.</param>
    public sealed record ProblemCount(long Problem, long Records, long Held, long Failed, DateTime OldestUtc, DateTime NewestUtc, long Problems, long Total);

    /// <summary>The ledger's problems, the largest first, at most <paramref name="max"/>; or with <paramref name="problem"/>, that one alone.</summary>
    public static async Task<IReadOnlyList<ProblemCount>> ProblemsAsync(OsduDbContext db, short partitionId, Guid flowId, long? problem, int max, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            await using var command = Command(connection, null, problem is null ? ProblemsSql : ProblemSql, slice: null);
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@flowId", SqlDbType.UniqueIdentifier) { Value = flowId });
            if (problem is { } hash)
            {
                command.Parameters.Add(new SqlParameter("@problem", SqlDbType.BigInt) { Value = hash });
            }
            else
            {
                command.Parameters.Add(new SqlParameter("@max", SqlDbType.Int) { Value = max });
            }

            var counts = new List<ProblemCount>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                counts.Add(new ProblemCount(
                    reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
                    DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc), DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc),
                    reader.GetInt64(6), reader.GetInt64(7)));
            }

            return counts;
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>A problem's most recently changed record and the one changed longest ago, by key.</summary>
    public sealed record ProblemEnds(long Problem, Guid Newest, Guid Oldest);

    /// <summary>The newest and the oldest record of each problem named.</summary>
    public static async Task<IReadOnlyList<ProblemEnds>> ProblemExamplesAsync(OsduDbContext db, short partitionId, Guid flowId, IReadOnlyCollection<long> problems, CancellationToken ct)
    {
        if (problems.Count == 0)
        {
            return [];
        }

        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            await using var command = Command(connection, null, ProblemExamplesSql, slice: null);
            command.Parameters.Add(new SqlParameter("@problems", SqlDbType.NVarChar, -1) { Value = System.Text.Json.JsonSerializer.Serialize(problems) });
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@flowId", SqlDbType.UniqueIdentifier) { Value = flowId });
            var ends = new List<ProblemEnds>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                ends.Add(new ProblemEnds(reader.GetInt64(0), reader.GetGuid(1), reader.GetGuid(2)));
            }

            return ends;
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The keys of one problem's records at <paramref name="positions"/> (1 the newest) of the problem index's order.</summary>
    public static async Task<IReadOnlyList<Guid>> ProblemSamplesAsync(
        OsduDbContext db, short partitionId, Guid flowId, long problem, IReadOnlyCollection<long> positions, CancellationToken ct)
    {
        if (positions.Count == 0)
        {
            return [];
        }

        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            await using var command = Command(connection, null, ProblemSamplesSql, slice: null);
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@flowId", SqlDbType.UniqueIdentifier) { Value = flowId });
            command.Parameters.Add(new SqlParameter("@problem", SqlDbType.BigInt) { Value = problem });
            command.Parameters.Add(new SqlParameter("@positions", SqlDbType.NVarChar, -1) { Value = System.Text.Json.JsonSerializer.Serialize(positions) });
            return await GuidsAsync(command, ct).ConfigureAwait(false);
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The files one problem's records were left at, the most records first, at most <paramref name="max"/>.</summary>
    public static async Task<IReadOnlyList<ProblemFile>> ProblemFilesAsync(OsduDbContext db, short partitionId, Guid flowId, long problem, int max, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            await using var command = Command(connection, null, ProblemFilesSql, slice: null);
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@flowId", SqlDbType.UniqueIdentifier) { Value = flowId });
            command.Parameters.Add(new SqlParameter("@problem", SqlDbType.BigInt) { Value = problem });
            command.Parameters.Add(new SqlParameter("@max", SqlDbType.Int) { Value = max });
            var files = new List<ProblemFile>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                files.Add(new ProblemFile(reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetInt64(1)));
            }

            return files;
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>How many of the ledger's blocked records are not sorted into a problem yet.</summary>
    public static async Task<long> UnsortedCountAsync(OsduDbContext db, short partitionId, Guid flowId, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            await using var command = Command(connection, null, UnsortedCountSql, slice: null);
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@flowId", SqlDbType.UniqueIdentifier) { Value = flowId });
            return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>A blocked record not sorted yet, with the error it would be sorted by.</summary>
    public sealed record UnsortedRecord(short PartitionId, Guid FlowId, Guid DeliveryKey, string? LastError);

    /// <summary>The next <paramref name="max"/> blocked records not sorted yet, across every ledger.</summary>
    public static async Task<IReadOnlyList<UnsortedRecord>> UnsortedPageAsync(OsduDbContext db, int max, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            await using var command = Command(connection, null, UnsortedPageSql, slice: null);
            command.Parameters.Add(new SqlParameter("@max", SqlDbType.Int) { Value = max });
            var page = new List<UnsortedRecord>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                page.Add(new UnsortedRecord(reader.GetInt16(0), reader.GetGuid(1), reader.GetGuid(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
            }

            return page;
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Sorts records into their problems in one statement; returns how many it sorted.</summary>
    public static async Task<int> SortAsync(OsduDbContext db, IReadOnlyList<(UnsortedRecord Record, long Problem)> sorted, CancellationToken ct)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        var payload = System.Text.Json.JsonSerializer.Serialize(sorted.Select(s => new
        {
            p = s.Record.PartitionId,
            f = s.Record.FlowId,
            k = s.Record.DeliveryKey,
            h = s.Problem,
            e = s.Record.LastError,
        }));
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            await using var command = Command(connection, null, SortSql, slice: null);
            command.Parameters.Add(new SqlParameter("@sorted", SqlDbType.NVarChar, -1) { Value = payload });
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }
}
