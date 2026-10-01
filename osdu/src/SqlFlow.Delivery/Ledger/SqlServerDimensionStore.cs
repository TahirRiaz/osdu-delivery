using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SqlFlow.Core.Data;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// Writes a completed dimension build in one transaction (docs/dimension-plan.md, Tables): the build's originals and members
/// are copied into temporary tables, and set-based statements add what is new, change what changed, mark removed what the
/// build no longer found and bring back what it found again, log every change to an original, rewrite the attributes of
/// the originals it found, and close the build with its counts. A reader sees the dimension as one build left it, never half of the next. An application lock per dimension
/// keeps two builds of one dimension from writing at once; a build of another dimension writes beside it.
/// </summary>
internal static class SqlServerDimensionStore
{
    /// <summary>How long one statement of a write may run: a dimension may hold millions of originals.</summary>
    private const int CommandTimeoutSeconds = 900;

    /// <summary>How long a write waits for another build of the same dimension to finish writing.</summary>
    private const int LockTimeoutMs = 120_000;

    /// <summary>How many times a write the database chose as a deadlock victim is made again.</summary>
    private const int DeadlockAttempts = 3;

    private const string StageSql = """
        CREATE TABLE #DimValue (
            [Seq] int NOT NULL,
            [Original] nvarchar(1024) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [OriginalHash] binary(32) NOT NULL PRIMARY KEY,
            [CleanValue] nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
            [LeftOut] nvarchar(16) NULL,
            [Note] nvarchar(400) NULL,
            [Label] nvarchar(1024) NULL,
            [LabelFrom] nvarchar(1024) NULL,
            [Filter] nvarchar(4000) NULL,
            [Count] bigint NOT NULL,
            [Filterable] bit NOT NULL,
            [MemberId] bigint NULL);
        CREATE TABLE #DimMember (
            [Value] nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
            [Records] bigint NOT NULL,
            [RecordsExact] bit NOT NULL,
            [Originals] int NOT NULL,
            [Unfilterable] int NOT NULL,
            [Filter] nvarchar(max) NULL,
            [FilterParts] int NOT NULL);
        CREATE TABLE #DimChange (
            [ValueId] bigint NOT NULL,
            [Change] nvarchar(16) NOT NULL,
            [FromMemberId] bigint NULL,
            [ToMemberId] bigint NULL);
        CREATE TABLE #DimAttr (
            [OriginalHash] binary(32) NOT NULL,
            [Name] nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Value] nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [ValueFrom] nvarchar(1024) NULL,
            [Records] bigint NULL,
            PRIMARY KEY ([OriginalHash], [Name], [Value]));
        CREATE TABLE #DimText (
            [Name] nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [TextHash] binary(32) NOT NULL,
            [Text] nvarchar(1024) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Value] nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Records] bigint NOT NULL,
            PRIMARY KEY ([Name], [TextHash]));
        """;

    private const string LockSql = """
        DECLARE @granted int;
        EXEC @granted = sys.sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = @timeout;
        SELECT @granted;
        """;

    // The whole merge, in the order that keeps every step's reads true: members first, so each original can be given the id
    // of its member; then the originals, whose changes are gathered as they are made; then the log and the counts.
    private const string MergeSql = """
        SET NOCOUNT ON;
        DECLARE @firstBuild bit = CASE WHEN EXISTS (
            SELECT 1 FROM [osdu].[Dimension] WHERE [PartitionId] = @p AND [DimensionId] = @d AND [LastRunId] IS NOT NULL) THEN 0 ELSE 1 END;
        DECLARE @membersAdded bigint, @membersRemoved bigint, @membersRestored bigint;

        SELECT @membersRestored = COUNT_BIG(*)
        FROM [osdu].[DimensionMember] AS m INNER JOIN #DimMember AS s ON m.[Value] = s.[Value]
        WHERE m.[PartitionId] = @p AND m.[DimensionId] = @d AND m.[RemovedRunId] IS NOT NULL;

        -- A member is rewritten only when something of it changed, so a build that found the same thing writes nothing.
        UPDATE m SET m.[Records] = s.[Records], m.[RecordsExact] = s.[RecordsExact], m.[Originals] = s.[Originals],
            m.[Unfilterable] = s.[Unfilterable], m.[Filter] = s.[Filter], m.[FilterParts] = s.[FilterParts],
            m.[RemovedRunId] = NULL, m.[RemovedUtc] = NULL
        FROM [osdu].[DimensionMember] AS m INNER JOIN #DimMember AS s ON m.[Value] = s.[Value]
        WHERE m.[PartitionId] = @p AND m.[DimensionId] = @d
          AND (m.[RemovedRunId] IS NOT NULL OR m.[Records] <> s.[Records] OR m.[RecordsExact] <> s.[RecordsExact]
               OR m.[Originals] <> s.[Originals] OR m.[Unfilterable] <> s.[Unfilterable] OR m.[FilterParts] <> s.[FilterParts]
               OR ISNULL(m.[Filter], N'') <> ISNULL(s.[Filter], N'') OR (m.[Filter] IS NULL AND s.[Filter] IS NOT NULL)
               OR (m.[Filter] IS NOT NULL AND s.[Filter] IS NULL));

        INSERT INTO [osdu].[DimensionMember] ([PartitionId], [DimensionId], [Value], [Records], [RecordsExact], [Originals], [Unfilterable],
            [Filter], [FilterParts], [FirstSeenRunId], [FirstSeenUtc])
        SELECT @p, @d, s.[Value], s.[Records], s.[RecordsExact], s.[Originals], s.[Unfilterable], s.[Filter], s.[FilterParts], @run, @now
        FROM #DimMember AS s
        WHERE NOT EXISTS (SELECT 1 FROM [osdu].[DimensionMember] AS m WHERE m.[PartitionId] = @p AND m.[DimensionId] = @d AND m.[Value] = s.[Value]);
        SET @membersAdded = @@ROWCOUNT;

        UPDATE m SET m.[RemovedRunId] = @run, m.[RemovedUtc] = @now
        FROM [osdu].[DimensionMember] AS m
        WHERE m.[PartitionId] = @p AND m.[DimensionId] = @d AND m.[RemovedRunId] IS NULL
          AND NOT EXISTS (SELECT 1 FROM #DimMember AS s WHERE s.[Value] = m.[Value]);
        SET @membersRemoved = @@ROWCOUNT;

        UPDATE s SET s.[MemberId] = m.[MemberId]
        FROM #DimValue AS s INNER JOIN [osdu].[DimensionMember] AS m
            ON m.[PartitionId] = @p AND m.[DimensionId] = @d AND m.[Value] = s.[CleanValue];

        -- An original found again after a build that did not find it is restored; one under another member (or under none
        -- where it had one, or one where it had none) moved.
        INSERT INTO #DimChange ([ValueId], [Change], [FromMemberId], [ToMemberId])
        SELECT v.[ValueId], CASE WHEN v.[RemovedRunId] IS NOT NULL THEN N'restored' ELSE N'moved' END, v.[MemberId], s.[MemberId]
        FROM [osdu].[DimensionValue] AS v INNER JOIN #DimValue AS s ON v.[OriginalHash] = s.[OriginalHash]
        WHERE v.[PartitionId] = @p AND v.[DimensionId] = @d
          AND (v.[RemovedRunId] IS NOT NULL OR ISNULL(v.[MemberId], -1) <> ISNULL(s.[MemberId], -1));

        UPDATE v SET v.[MemberSinceRunId] = CASE WHEN v.[RemovedRunId] IS NOT NULL OR ISNULL(v.[MemberId], -1) <> ISNULL(s.[MemberId], -1)
                THEN @run ELSE v.[MemberSinceRunId] END,
            v.[MemberId] = s.[MemberId], v.[LeftOut] = s.[LeftOut], v.[Note] = s.[Note], v.[Count] = s.[Count], v.[Filterable] = s.[Filterable],
            v.[Label] = s.[Label], v.[LabelFrom] = s.[LabelFrom], v.[Filter] = s.[Filter],
            v.[RemovedRunId] = NULL, v.[RemovedUtc] = NULL
        FROM [osdu].[DimensionValue] AS v INNER JOIN #DimValue AS s ON v.[OriginalHash] = s.[OriginalHash]
        WHERE v.[PartitionId] = @p AND v.[DimensionId] = @d
          AND (v.[RemovedRunId] IS NOT NULL OR ISNULL(v.[MemberId], -1) <> ISNULL(s.[MemberId], -1) OR v.[Count] <> s.[Count]
               OR v.[Filterable] <> s.[Filterable] OR ISNULL(v.[LeftOut], N'') <> ISNULL(s.[LeftOut], N'')
               OR ISNULL(v.[Note], N'') <> ISNULL(s.[Note], N'')
               -- A label compares exactly: a record renamed only in case is a new label.
               OR ISNULL(v.[Label], N'') COLLATE Latin1_General_100_BIN2 <> ISNULL(s.[Label], N'') COLLATE Latin1_General_100_BIN2
               OR (v.[Label] IS NULL AND s.[Label] IS NOT NULL) OR (v.[Label] IS NOT NULL AND s.[Label] IS NULL)
               OR ISNULL(v.[LabelFrom], N'') <> ISNULL(s.[LabelFrom], N'')
               OR ISNULL(v.[Filter], N'') <> ISNULL(s.[Filter], N''));

        INSERT INTO [osdu].[DimensionValue] ([PartitionId], [DimensionId], [Original], [OriginalHash], [MemberId], [LeftOut], [Note], [Count],
            [Filterable], [Label], [LabelFrom], [Filter], [FirstSeenRunId], [FirstSeenUtc], [MemberSinceRunId])
        OUTPUT inserted.[ValueId], N'added', NULL, inserted.[MemberId] INTO #DimChange ([ValueId], [Change], [FromMemberId], [ToMemberId])
        SELECT @p, @d, s.[Original], s.[OriginalHash], s.[MemberId], s.[LeftOut], s.[Note], s.[Count], s.[Filterable], s.[Label], s.[LabelFrom],
            s.[Filter], @run, @now, @run
        FROM #DimValue AS s
        WHERE NOT EXISTS (SELECT 1 FROM [osdu].[DimensionValue] AS v WHERE v.[PartitionId] = @p AND v.[DimensionId] = @d AND v.[OriginalHash] = s.[OriginalHash])
        -- The new originals take their ids in the order the build listed them, so arrival order within a build is the build's.
        ORDER BY s.[Seq];

        UPDATE v SET v.[RemovedRunId] = @run, v.[RemovedUtc] = @now
        OUTPUT inserted.[ValueId], N'removed', deleted.[MemberId], NULL INTO #DimChange ([ValueId], [Change], [FromMemberId], [ToMemberId])
        FROM [osdu].[DimensionValue] AS v
        WHERE v.[PartitionId] = @p AND v.[DimensionId] = @d AND v.[RemovedRunId] IS NULL
          AND NOT EXISTS (SELECT 1 FROM #DimValue AS s WHERE s.[OriginalHash] = v.[OriginalHash]);

        -- The attribute values of every original the build found are what it read: one no longer read is dropped, one read
        -- again rewritten only when where it was read or how many records hold it changed, and a new one added. An original
        -- the build did not find keeps what its last build read.
        DELETE a
        FROM [osdu].[DimensionAttribute] AS a
        INNER JOIN [osdu].[DimensionValue] AS v ON v.[PartitionId] = a.[PartitionId] AND v.[ValueId] = a.[ValueId]
        INNER JOIN #DimValue AS s ON s.[OriginalHash] = v.[OriginalHash]
        WHERE a.[PartitionId] = @p AND a.[DimensionId] = @d
          AND NOT EXISTS (SELECT 1 FROM #DimAttr AS t WHERE t.[OriginalHash] = s.[OriginalHash] AND t.[Name] = a.[Name] AND t.[Value] = a.[Value]);
        DECLARE @attributesChanged bigint = @@ROWCOUNT;

        UPDATE a SET a.[ValueFrom] = t.[ValueFrom], a.[Records] = t.[Records]
        FROM [osdu].[DimensionAttribute] AS a
        INNER JOIN [osdu].[DimensionValue] AS v ON v.[PartitionId] = a.[PartitionId] AND v.[ValueId] = a.[ValueId]
        INNER JOIN #DimAttr AS t ON t.[OriginalHash] = v.[OriginalHash] AND t.[Name] = a.[Name] AND t.[Value] = a.[Value]
        WHERE a.[PartitionId] = @p AND a.[DimensionId] = @d
          AND (ISNULL(a.[ValueFrom], N'') <> ISNULL(t.[ValueFrom], N'') OR (a.[ValueFrom] IS NULL AND t.[ValueFrom] IS NOT NULL)
               OR (a.[ValueFrom] IS NOT NULL AND t.[ValueFrom] IS NULL)
               OR ISNULL(a.[Records], -1) <> ISNULL(t.[Records], -1));
        SET @attributesChanged += @@ROWCOUNT;

        INSERT INTO [osdu].[DimensionAttribute] ([PartitionId], [DimensionId], [ValueId], [Name], [Value], [ValueFrom], [Records])
        SELECT @p, @d, v.[ValueId], t.[Name], t.[Value], t.[ValueFrom], t.[Records]
        FROM #DimAttr AS t
        INNER JOIN [osdu].[DimensionValue] AS v ON v.[PartitionId] = @p AND v.[DimensionId] = @d AND v.[OriginalHash] = t.[OriginalHash]
        WHERE NOT EXISTS (
            SELECT 1 FROM [osdu].[DimensionAttribute] AS a
            WHERE a.[PartitionId] = @p AND a.[DimensionId] = @d AND a.[ValueId] = v.[ValueId] AND a.[Name] = t.[Name] AND a.[Value] = t.[Value]);
        SET @attributesChanged += @@ROWCOUNT;

        -- The texts a build collected replace those the dimension had, unless it could not settle the field (and so read none).
        IF @fieldIndex IS NOT NULL
        BEGIN
            DELETE FROM [osdu].[DimensionCollectedText] WHERE [PartitionId] = @p AND [DimensionId] = @d;
            INSERT INTO [osdu].[DimensionCollectedText] ([PartitionId], [DimensionId], [Name], [TextHash], [Text], [Value], [Records])
            SELECT @p, @d, t.[Name], t.[TextHash], t.[Text], t.[Value], t.[Records] FROM #DimText AS t;
        END;

        -- The first build's originals are its arrivals, told by the build that first found each; a later build logs its own.
        INSERT INTO [osdu].[DimensionChange] ([PartitionId], [DimensionId], [DimensionRunId], [ValueId], [Change], [FromMemberId], [ToMemberId], [ChangedUtc])
        SELECT @p, @d, @run, c.[ValueId], c.[Change], c.[FromMemberId], c.[ToMemberId], @now
        FROM #DimChange AS c
        WHERE c.[Change] <> N'added' OR @firstBuild = 0;

        DECLARE @members bigint = (SELECT COUNT_BIG(*) FROM [osdu].[DimensionMember] WHERE [PartitionId] = @p AND [DimensionId] = @d AND [RemovedRunId] IS NULL);
        DECLARE @originals bigint = (SELECT COUNT_BIG(*) FROM [osdu].[DimensionValue] WHERE [PartitionId] = @p AND [DimensionId] = @d AND [RemovedRunId] IS NULL);

        -- A build that could not settle the field (no record for its templates to be read by) leaves the one the dimension had.
        UPDATE [osdu].[Dimension] SET
            [FieldIndex] = CASE WHEN @fieldIndex IS NULL THEN [FieldIndex] ELSE @fieldIndex END,
            [NestedPath] = CASE WHEN @fieldIndex IS NULL THEN [NestedPath] ELSE @nestedPath END,
            [AggregateBy] = CASE WHEN @fieldIndex IS NULL THEN [AggregateBy] ELSE @aggregateBy END,
            [Repeats] = CASE WHEN @fieldIndex IS NULL THEN [Repeats] ELSE @repeats END,
            [CollectedJson] = CASE WHEN @fieldIndex IS NULL THEN [CollectedJson] ELSE @collected END,
            [Members] = @members, [Originals] = @originals, [LastRunId] = @run, [LastBuiltUtc] = @now
        WHERE [PartitionId] = @p AND [DimensionId] = @d;

        SELECT
            (SELECT COUNT_BIG(*) FROM #DimChange WHERE [Change] = N'added'),
            (SELECT COUNT_BIG(*) FROM #DimChange WHERE [Change] = N'removed'),
            (SELECT COUNT_BIG(*) FROM #DimChange WHERE [Change] = N'moved'),
            (SELECT COUNT_BIG(*) FROM #DimChange WHERE [Change] = N'restored'),
            @membersAdded, @membersRemoved, @membersRestored, @members, @originals, @attributesChanged;
        """;

    // A dimension's rows, table by table, a batch at a time, each batch its own statement so no one transaction holds millions
    // of rows; under the dimension's write lock, held by the session, so a build's write waits or comes first. The dimension's
    // own row goes last: a removal that stops part way leaves it listed, and removing it again finishes the work.
    private const string RemoveSql = """
        SET NOCOUNT ON;
        DECLARE @granted int;
        EXEC @granted = sys.sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = @timeout;
        IF @granted < 0
        BEGIN
            SELECT CAST(@granted AS bigint), CAST(0 AS bigint), CAST(0 AS bigint), CAST(0 AS bigint), CAST(0 AS bigint), CAST(0 AS bigint), CAST(0 AS bigint);
            RETURN;
        END;

        BEGIN TRY
            DECLARE @n bigint, @texts bigint = 0, @attributes bigint = 0, @changes bigint = 0, @keys bigint = 0, @values bigint = 0, @builds bigint = 0;
            WHILE 1 = 1 BEGIN DELETE TOP (@batch) FROM [osdu].[DimensionCollectedText] WHERE [PartitionId] = @p AND [DimensionId] = @d; SET @n = @@ROWCOUNT; SET @texts += @n; IF @n < @batch BREAK; END;
            WHILE 1 = 1 BEGIN DELETE TOP (@batch) FROM [osdu].[DimensionAttribute] WHERE [PartitionId] = @p AND [DimensionId] = @d; SET @n = @@ROWCOUNT; SET @attributes += @n; IF @n < @batch BREAK; END;
            WHILE 1 = 1 BEGIN DELETE TOP (@batch) FROM [osdu].[DimensionChange] WHERE [PartitionId] = @p AND [DimensionId] = @d; SET @n = @@ROWCOUNT; SET @changes += @n; IF @n < @batch BREAK; END;
            WHILE 1 = 1 BEGIN DELETE TOP (@batch) FROM [osdu].[DimensionValue] WHERE [PartitionId] = @p AND [DimensionId] = @d; SET @n = @@ROWCOUNT; SET @keys += @n; IF @n < @batch BREAK; END;
            WHILE 1 = 1 BEGIN DELETE TOP (@batch) FROM [osdu].[DimensionMember] WHERE [PartitionId] = @p AND [DimensionId] = @d; SET @n = @@ROWCOUNT; SET @values += @n; IF @n < @batch BREAK; END;
            WHILE 1 = 1 BEGIN DELETE TOP (@batch) FROM [osdu].[DimensionRun] WHERE [PartitionId] = @p AND [DimensionId] = @d; SET @n = @@ROWCOUNT; SET @builds += @n; IF @n < @batch BREAK; END;
            DELETE FROM [osdu].[Dimension] WHERE [PartitionId] = @p AND [DimensionId] = @d;
            SET @n = @@ROWCOUNT;
            EXEC sys.sp_releaseapplock @Resource = @resource, @LockOwner = N'Session';
            SELECT @n, @values, @keys, @builds, @changes, @attributes, @texts;
        END TRY
        BEGIN CATCH
            EXEC sys.sp_releaseapplock @Resource = @resource, @LockOwner = N'Session';
            THROW;
        END CATCH;
        """;

    /// <summary>The rows a removal took from each table.</summary>
    public sealed record Removed(long Values, long Keys, long Builds, long Changes, long Attributes, long Texts);

    /// <summary>The most rows one statement of a removal deletes.</summary>
    private const int RemoveBatch = 20_000;

    /// <summary>
    /// Removes every row of dimension <paramref name="dimensionId"/> in partition <paramref name="partitionId"/> and then the
    /// dimension (<see cref="ILedger.RemoveDimensionAsync"/>).
    /// </summary>
    /// <exception cref="DeliveryException">Another write of the dimension held its lock past the timeout.</exception>
    public static async Task<Removed> RemoveAsync(OsduDbContext db, short partitionId, int dimensionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            await using var command = new SqlCommand(RemoveSql, connection) { CommandTimeout = CommandTimeoutSeconds };
            command.Parameters.Add(new SqlParameter("@p", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@d", SqlDbType.Int) { Value = dimensionId });
            command.Parameters.Add(new SqlParameter("@batch", SqlDbType.Int) { Value = RemoveBatch });
            command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = string.Create(CultureInfo.InvariantCulture, $"osdu-dimension:{partitionId}:{dimensionId}") });
            command.Parameters.Add(new SqlParameter("@timeout", SqlDbType.Int) { Value = LockTimeoutMs });
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                throw new DeliveryException(string.Create(CultureInfo.InvariantCulture, $"Removing dimension {dimensionId} returned no counts."));
            }

            if (reader.GetInt64(0) < 0)
            {
                throw new DeliveryException(string.Create(CultureInfo.InvariantCulture,
                    $"A build of dimension {dimensionId} held its write lock for more than {LockTimeoutMs / 1000} seconds (sp_getapplock answered {reader.GetInt64(0)}), so nothing was removed. Remove it again when the build has finished."));
            }

            return new Removed(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6));
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>What a write changed, and what the dimension holds after it: the attributes it added, rewrote or dropped among them.</summary>
    public sealed record Written(DimensionChangeCounts Changes, long Members, long Originals, long AttributesChanged);

    /// <summary>The hash an original is unique by: SHA-256 of its UTF-8 bytes.</summary>
    public static byte[] HashOf(string original)
    {
        ArgumentNullException.ThrowIfNull(original);
        return SHA256.HashData(Encoding.UTF8.GetBytes(original));
    }

    /// <summary>
    /// Merges <paramref name="write"/> into its dimension and closes its build as completed with <paramref name="close"/>'s
    /// counts, in one transaction. A deadlock rolls it all back and it is written again.
    /// </summary>
    public static async Task<Written> WriteAsync(
        OsduDbContext db, short partitionId, DimensionWrite write, Func<Written, DeliveryDimensionRun, Task> close, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(close);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await WriteOnceAsync(db, partitionId, write, close, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < DeadlockAttempts && SqlServerLedgerBulk.IsDeadlock(ex))
            {
                db.ChangeTracker.Clear();
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(50, 200) * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task<Written> WriteOnceAsync(
        OsduDbContext db, short partitionId, DimensionWrite write, Func<Written, DeliveryDimensionRun, Task> close, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
            var connection = (SqlConnection)db.Database.GetDbConnection();
            var transaction = (SqlTransaction)tx.GetDbTransaction();
            await LockAsync(connection, transaction, partitionId, write.DimensionId, ct).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, StageSql, ct).ConfigureAwait(false);
            await CopyAsync(connection, transaction, "#DimValue", ValueColumns, ValueTypes, write.Originals, ValueRow, ct).ConfigureAwait(false);
            await CopyAsync(connection, transaction, "#DimMember", MemberColumns, MemberTypes, write.Members, (member, _) => MemberRow(member), ct).ConfigureAwait(false);
            await CopyAsync(connection, transaction, "#DimAttr", AttributeColumns, AttributeTypes, AttributesOf(write), (attribute, _) => attribute, ct).ConfigureAwait(false);
            await CopyAsync(connection, transaction, "#DimText", TextColumns, TextTypes, TextsOf(write), (text, _) => text, ct).ConfigureAwait(false);

            Written written;
            await using (var command = Command(connection, transaction, MergeSql))
            {
                command.Parameters.Add(new SqlParameter("@p", SqlDbType.SmallInt) { Value = partitionId });
                command.Parameters.Add(new SqlParameter("@d", SqlDbType.Int) { Value = write.DimensionId });
                command.Parameters.Add(new SqlParameter("@run", SqlDbType.BigInt) { Value = write.DimensionRunId });
                command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = write.CompletedUtc });
                command.Parameters.Add(new SqlParameter("@fieldIndex", SqlDbType.NVarChar, 16) { Value = (object?)write.Field?.Index ?? DBNull.Value });
                command.Parameters.Add(new SqlParameter("@nestedPath", SqlDbType.NVarChar, DeliveryDimension.MaxPathLength) { Value = (object?)write.Field?.NestedPath ?? DBNull.Value });
                command.Parameters.Add(new SqlParameter("@aggregateBy", SqlDbType.NVarChar, DeliveryDimension.MaxAggregateByLength) { Value = (object?)write.Field?.AggregateBy ?? DBNull.Value });
                command.Parameters.Add(new SqlParameter("@repeats", SqlDbType.Bit) { Value = write.Field?.Repeats ?? false });
                command.Parameters.Add(new SqlParameter("@collected", SqlDbType.NVarChar, -1) { Value = (object?)write.CollectedJson ?? DBNull.Value });
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    throw new DeliveryException($"Writing dimension {write.DimensionId} returned no counts.");
                }

                written = new Written(
                    new DimensionChangeCounts(
                        MembersAdded: reader.GetInt64(4), MembersRemoved: reader.GetInt64(5), MembersRestored: reader.GetInt64(6),
                        OriginalsAdded: reader.GetInt64(0), OriginalsRemoved: reader.GetInt64(1), OriginalsMoved: reader.GetInt64(2), OriginalsRestored: reader.GetInt64(3)),
                    reader.GetInt64(7),
                    reader.GetInt64(8),
                    reader.GetInt64(9));
            }

            var run = await db.DeliveryDimensionRuns
                .FirstOrDefaultAsync(r => r.PartitionId == partitionId && r.DimensionRunId == write.DimensionRunId, ct).ConfigureAwait(false)
                ?? throw new DeliveryException(string.Create(CultureInfo.InvariantCulture, $"Dimension build {write.DimensionRunId} is not in the ledger, so it cannot be closed."));
            await close(written, run).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return written;
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static async Task LockAsync(SqlConnection connection, SqlTransaction transaction, short partitionId, int dimensionId, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, LockSql);
        command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = string.Create(CultureInfo.InvariantCulture, $"osdu-dimension:{partitionId}:{dimensionId}") });
        command.Parameters.Add(new SqlParameter("@timeout", SqlDbType.Int) { Value = LockTimeoutMs });
        var granted = Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (granted < 0)
        {
            throw new DeliveryException(
                string.Create(CultureInfo.InvariantCulture,
                    $"Another build of dimension {dimensionId} held its write lock for more than {LockTimeoutMs / 1000} seconds (sp_getapplock answered {granted}), so this build wrote nothing. Build it again when the other has finished."));
        }
    }

    private static readonly string[] ValueColumns =
        ["Seq", "Original", "OriginalHash", "CleanValue", "LeftOut", "Note", "Label", "LabelFrom", "Filter", "Count", "Filterable"];

    private static readonly Type[] ValueTypes =
        [typeof(int), typeof(string), typeof(byte[]), typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(long), typeof(bool)];

    private static object?[] ValueRow(DimensionOriginalWrite value, int seq)
        =>
        [
            seq, value.Original, HashOf(value.Original), value.CleanValue, value.LeftOut, OsduLedger.Truncate(value.Note, 400),
            OsduLedger.Truncate(value.Label, DeliveryDimensionValue.MaxLabelLength), OsduLedger.Truncate(value.LabelFrom, DeliveryDimensionValue.MaxOriginalLength),
            value.Filter is { Length: > DeliveryDimensionValue.MaxFilterLength } ? null : value.Filter, value.Count, value.Filterable,
        ];

    private static readonly string[] AttributeColumns = ["OriginalHash", "Name", "Value", "ValueFrom", "Records"];

    private static readonly Type[] AttributeTypes = [typeof(byte[]), typeof(string), typeof(string), typeof(string), typeof(long)];

    /// <summary>
    /// Every attribute value of every original of <paramref name="write"/>, a row each, as the staging table takes them: the
    /// value cut to what a row keeps, a value named twice for one original under one attribute kept once (its first), an
    /// empty one left out.
    /// </summary>
    private static List<object?[]> AttributesOf(DimensionWrite write)
    {
        var rows = new List<object?[]>();
        foreach (var original in write.Originals)
        {
            if (original.Attributes is not { Count: > 0 } attributes)
            {
                continue;
            }

            var hash = HashOf(original.Original);
            var seen = new HashSet<(string, string)>();
            foreach (var attribute in attributes)
            {
                if (string.IsNullOrEmpty(attribute.Value) || attribute.Name.Length > DeliveryDimensionAttributeValue.MaxNameLength)
                {
                    continue;
                }

                var value = OsduLedger.Truncate(attribute.Value, DeliveryDimensionAttributeValue.MaxValueLength)!;
                if (seen.Add((attribute.Name, value)))
                {
                    rows.Add([hash, attribute.Name, value, OsduLedger.Truncate(attribute.From, DeliveryDimensionValue.MaxOriginalLength), attribute.Records]);
                }
            }
        }

        return rows;
    }

    private static readonly string[] TextColumns = ["Name", "TextHash", "Text", "Value", "Records"];

    private static readonly Type[] TextTypes = [typeof(string), typeof(byte[]), typeof(string), typeof(string), typeof(long)];

    /// <summary>
    /// Every text the build collected, a row each, as the staging table takes them: a text longer than a row keeps, or named
    /// twice under one attribute, kept once (its first).
    /// </summary>
    private static List<object?[]> TextsOf(DimensionWrite write)
    {
        var rows = new List<object?[]>(write.CollectedTexts.Count);
        var seen = new HashSet<(string, string)>();
        foreach (var text in write.CollectedTexts)
        {
            if (text.Text.Length > DeliveryDimensionValue.MaxOriginalLength || text.Name.Length > DeliveryDimensionAttributeValue.MaxNameLength
                || string.IsNullOrEmpty(text.Value) || !seen.Add((text.Name, text.Text)))
            {
                continue;
            }

            rows.Add([text.Name, HashOf(text.Text), text.Text, OsduLedger.Truncate(text.Value, DeliveryDimensionAttributeValue.MaxValueLength), text.Records]);
        }

        return rows;
    }

    private static readonly string[] MemberColumns = ["Value", "Records", "RecordsExact", "Originals", "Unfilterable", "Filter", "FilterParts"];

    private static readonly Type[] MemberTypes = [typeof(string), typeof(long), typeof(bool), typeof(int), typeof(int), typeof(string), typeof(int)];

    private static object?[] MemberRow(DimensionMemberWrite member)
        => [member.Value, member.Records, member.RecordsExact, member.Originals, member.Unfilterable, member.Filter, member.FilterParts];

    /// <summary>
    /// Copies <paramref name="items"/> into a temporary table a row at a time, never holding a second copy of them; each row is
    /// given its place in the list.
    /// </summary>
    private static async Task CopyAsync<T>(
        SqlConnection connection, SqlTransaction transaction, string table, string[] columns, Type[] types, IReadOnlyList<T> items, Func<T, int, object?[]> row,
        CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return;
        }

        using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction)
        {
            DestinationTableName = table,
            BatchSize = 10_000,
            BulkCopyTimeout = CommandTimeoutSeconds,
        };
        foreach (var column in columns)
        {
            bulk.ColumnMappings.Add(column, column);
        }

        await using var reader = new StreamingDataReader(columns, types, Rows(items, row, ct).GetAsyncEnumerator(ct));
        await bulk.WriteToServerAsync(reader, ct).ConfigureAwait(false);
    }

    private static async IAsyncEnumerable<object?[]> Rows<T>(IReadOnlyList<T> items, Func<T, int, object?[]> row, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (i % 50_000 == 49_999)
            {
                // A long copy lets other work on the thread pool run, and notices a cancellation, now and then.
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            yield return row(items[i], i);
        }
    }

    private static SqlCommand Command(SqlConnection connection, SqlTransaction transaction, string sql)
        => new(sql, connection, transaction) { CommandTimeout = CommandTimeoutSeconds };

    private static async Task ExecuteAsync(SqlConnection connection, SqlTransaction transaction, string sql, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, sql);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
