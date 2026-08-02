/*
    Fix: RAISERROR treats the message as a format string, so a percent sign in it is read as a
    format specifier. The worker messages embed @TablePattern ('APC%'), which made the trailing
    "APC%." parse as an invalid specifier and threw

        Invalid format specification: '%.'

    at the point a worker ran out of claimable chunks. The copy itself was unaffected (the failing
    worker had already committed all seven of its chunks), but the error aborted the procedure
    before the block that marks finished tables Done and reseeds their identity, so a table could
    be fully copied yet still sit in Ready.

    Every message is now passed through REPLACE(@msg, '%', '%%') immediately before RAISERROR,
    which escapes the specifier for good regardless of what a table name or a captured
    ERROR_MESSAGE() happens to contain. The tail block is unchanged in behaviour and is
    self healing: the next worker to finish re-evaluates every table and marks the ones whose
    chunks are all Done.
*/

SET NOCOUNT ON;
GO

CREATE OR ALTER PROCEDURE mig.usp_ApcCopyRun
    @TablePattern       sysname = N'APC%',
    @WorkerId           int     = 1,
    @MaxChunks          int     = NULL,
    @MaxMinutes         int     = NULL,
    @MaxAttempts        int     = 3,
    @MaxWorkersPerTable int     = 2,
    @IdleExitSeconds    int     = 120,
    @StopOnError        bit     = 0,
    @ResetRunning       bit     = 0,
    @Debug              bit     = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @startedUtc datetime2(3) = SYSUTCDATETIME(),
            @done       int = 0,
            @idleSince  datetime2(3) = NULL,
            @tbl        sysname,
            @chunkNo    int,
            @lo         bigint,
            @hi         bigint,
            @attempts   int,
            @keyCol     sysname,
            @cols       nvarchar(max),
            @hasIdent   bit,
            @sql        nvarchar(max),
            @remote     nvarchar(max),
            @rowsCopied bigint,
            @t0         datetime2(3),
            @objName    nvarchar(400),
            @msg        nvarchar(500),
            @workLeft   int;

    IF @MaxWorkersPerTable < 1
        THROW 50020, 'ApcCopyRun: @MaxWorkersPerTable must be at least 1.', 1;

    IF @ResetRunning = 1
    BEGIN
        UPDATE mig.ApcCopyChunk
        SET Status = 'Pending',
            ErrorMessage = N'Reset from Running: the previous session did not finish this chunk.'
        WHERE Status = 'Running'
          AND TableName LIKE @TablePattern;
    END;

    DECLARE @claim TABLE (TableName sysname, ChunkNo int, LoKey bigint, HiKey bigint, Attempts int);

    WHILE 1 = 1
    BEGIN
        IF @MaxChunks IS NOT NULL AND @done >= @MaxChunks
        BEGIN
            SET @msg = REPLACE(CONCAT(N'W', @WorkerId, N': stopping, @MaxChunks (', @MaxChunks, N') reached.'), N'%', N'%%');
            RAISERROR (@msg, 0, 1) WITH NOWAIT;
            BREAK;
        END;

        IF @MaxMinutes IS NOT NULL AND DATEDIFF(MINUTE, @startedUtc, SYSUTCDATETIME()) >= @MaxMinutes
        BEGIN
            SET @msg = REPLACE(CONCAT(N'W', @WorkerId, N': stopping, @MaxMinutes (', @MaxMinutes, N') reached.'), N'%', N'%%');
            RAISERROR (@msg, 0, 1) WITH NOWAIT;
            BREAK;
        END;

        SET @tbl = NULL;
        DELETE FROM @claim;

        ;WITH nxt AS
        (
            SELECT TOP (1) c.TableName, c.ChunkNo, c.LoKey, c.HiKey, c.Attempts,
                   c.Status, c.StartedUtc, c.CompletedUtc, c.ElapsedMs, c.ErrorMessage, c.WorkerId
            FROM mig.ApcCopyChunk c WITH (UPDLOCK, READPAST, ROWLOCK)
            JOIN mig.ApcCopyTable t ON t.TableName = c.TableName
            WHERE c.TableName LIKE @TablePattern
              AND t.Status = 'Ready'
              AND (c.Status = 'Pending' OR (c.Status = 'Failed' AND c.Attempts < @MaxAttempts))
              AND (SELECT COUNT(*) FROM mig.ApcCopyChunk r
                   WHERE r.TableName = c.TableName AND r.Status = 'Running') < @MaxWorkersPerTable
            ORDER BY (SELECT COUNT(*) FROM mig.ApcCopyChunk r2
                      WHERE r2.TableName = c.TableName AND r2.Status = 'Running'),
                     t.Priority, c.TableName, c.ChunkNo
        )
        UPDATE nxt
        SET Status = 'Running',
            Attempts = Attempts + 1,
            StartedUtc = SYSUTCDATETIME(),
            CompletedUtc = NULL,
            ElapsedMs = NULL,
            ErrorMessage = NULL,
            WorkerId = @WorkerId
        OUTPUT inserted.TableName, inserted.ChunkNo, inserted.LoKey, inserted.HiKey, deleted.Attempts
        INTO @claim (TableName, ChunkNo, LoKey, HiKey, Attempts);

        SELECT @tbl = TableName, @chunkNo = ChunkNo, @lo = LoKey, @hi = HiKey, @attempts = Attempts
        FROM @claim;

        IF @tbl IS NULL
        BEGIN
            SELECT @workLeft = COUNT(*)
            FROM mig.ApcCopyChunk c
            JOIN mig.ApcCopyTable t ON t.TableName = c.TableName
            WHERE c.TableName LIKE @TablePattern
              AND t.Status = 'Ready'
              AND (c.Status IN ('Pending', 'Running') OR (c.Status = 'Failed' AND c.Attempts < @MaxAttempts));

            IF @workLeft = 0
            BEGIN
                SET @msg = REPLACE(CONCAT(N'W', @WorkerId, N': queue empty, nothing left to claim.'), N'%', N'%%');
                RAISERROR (@msg, 0, 1) WITH NOWAIT;
                BREAK;
            END;

            IF @idleSince IS NULL
                SET @idleSince = SYSUTCDATETIME();

            IF DATEDIFF(SECOND, @idleSince, SYSUTCDATETIME()) >= @IdleExitSeconds
            BEGIN
                SET @msg = REPLACE(CONCAT(N'W', @WorkerId, N': idle ', @IdleExitSeconds, N's with ',
                                          @workLeft, N' chunk(s) held by other workers, exiting.'), N'%', N'%%');
                RAISERROR (@msg, 0, 1) WITH NOWAIT;
                BREAK;
            END;

            WAITFOR DELAY '00:00:10';
            CONTINUE;
        END;

        SET @idleSince = NULL;

        SELECT @keyCol = KeyColumn, @cols = ColumnList, @hasIdent = HasIdentity
        FROM mig.ApcCopyTable WHERE TableName = @tbl;

        SET @objName = QUOTENAME(N'arc') + N'.' + QUOTENAME(@tbl);
        SET @t0 = SYSUTCDATETIME();

        BEGIN TRY
            /* A retry may be re-running a chunk that actually committed, so clear its range
               first. Skipped on the first attempt, where the range is provably empty. */
            IF @attempts > 0
            BEGIN
                SET @sql = N'DELETE FROM ' + @objName + N' WHERE ' + QUOTENAME(@keyCol)
                         + N' >= @lo AND ' + QUOTENAME(@keyCol) + N' <= @hi;';
                IF @Debug = 1 PRINT @sql;
                EXEC sys.sp_executesql @sql, N'@lo bigint, @hi bigint', @lo = @lo, @hi = @hi;
            END;

            SET @remote = N'SELECT ' + @cols + N' FROM arc.' + QUOTENAME(@tbl)
                        + N' WHERE ' + QUOTENAME(@keyCol) + N' >= ' + CAST(@lo AS nvarchar(20))
                        + N' AND ' + QUOTENAME(@keyCol) + N' <= ' + CAST(@hi AS nvarchar(20));

            SET @sql = CASE WHEN @hasIdent = 1 THEN N'SET IDENTITY_INSERT ' + @objName + N' ON;' + CHAR(13) + CHAR(10) ELSE N'' END
                     + N'INSERT INTO ' + @objName + N' WITH (TABLOCK) (' + @cols + N')' + CHAR(13) + CHAR(10)
                     + N'SELECT ' + @cols + N' FROM OPENQUERY(OLDPROD, '''
                     + REPLACE(@remote, '''', '''''') + N''');' + CHAR(13) + CHAR(10)
                     + N'SET @rowsOut = CAST(@@ROWCOUNT AS bigint);' + CHAR(13) + CHAR(10)
                     + CASE WHEN @hasIdent = 1 THEN N'SET IDENTITY_INSERT ' + @objName + N' OFF;' ELSE N'' END;

            IF @Debug = 1 PRINT @sql;

            SET @rowsCopied = NULL;
            EXEC sys.sp_executesql @sql, N'@rowsOut bigint OUTPUT', @rowsOut = @rowsCopied OUTPUT;

            UPDATE mig.ApcCopyChunk
            SET Status = 'Done', RowsCopied = @rowsCopied, CompletedUtc = SYSUTCDATETIME(),
                ElapsedMs = DATEDIFF_BIG(MILLISECOND, @t0, SYSUTCDATETIME()), ErrorMessage = NULL
            WHERE TableName = @tbl AND ChunkNo = @chunkNo;

            SET @done += 1;

            SET @msg = REPLACE(CONCAT(N'W', @WorkerId, N': ', @tbl, N' chunk ', @chunkNo, N' -> ', @rowsCopied,
                                      N' rows in ', DATEDIFF(SECOND, @t0, SYSUTCDATETIME()), N's (',
                                      CAST(@rowsCopied * 1000 / NULLIF(DATEDIFF_BIG(MILLISECOND, @t0, SYSUTCDATETIME()), 0) AS bigint),
                                      N' rows/s).'), N'%', N'%%');
            RAISERROR (@msg, 0, 1) WITH NOWAIT;
        END TRY
        BEGIN CATCH
            DECLARE @err nvarchar(2000) = LEFT(ERROR_MESSAGE(), 2000);

            UPDATE mig.ApcCopyChunk
            SET Status = 'Failed', CompletedUtc = SYSUTCDATETIME(),
                ElapsedMs = DATEDIFF_BIG(MILLISECOND, @t0, SYSUTCDATETIME()), ErrorMessage = @err
            WHERE TableName = @tbl AND ChunkNo = @chunkNo;

            SET @msg = REPLACE(CONCAT(N'W', @WorkerId, N': ', @tbl, N' chunk ', @chunkNo, N' FAILED: ', @err), N'%', N'%%');
            RAISERROR (@msg, 0, 1) WITH NOWAIT;

            IF @StopOnError = 1
                BREAK;
        END CATCH;
    END;

    /* Mark fully copied tables Done and realign the identity seed with the copied keys. Runs on
       every worker exit, so a table left in Ready by an earlier aborted session is picked up
       here without operator intervention. */
    DECLARE @doneTbl sysname, @seedTo bigint;

    DECLARE dcur CURSOR LOCAL FAST_FORWARD FOR
        SELECT t.TableName
        FROM mig.ApcCopyTable t
        WHERE t.TableName LIKE @TablePattern
          AND t.Status = 'Ready'
          AND EXISTS (SELECT 1 FROM mig.ApcCopyChunk c WHERE c.TableName = t.TableName)
          AND NOT EXISTS (SELECT 1 FROM mig.ApcCopyChunk c
                          WHERE c.TableName = t.TableName AND c.Status <> 'Done');

    OPEN dcur;
    FETCH NEXT FROM dcur INTO @doneTbl;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        IF EXISTS (SELECT 1 FROM mig.ApcCopyTable WHERE TableName = @doneTbl AND HasIdentity = 1)
        BEGIN
            SELECT @seedTo = MaxKey FROM mig.ApcCopyTable WHERE TableName = @doneTbl;
            SET @sql = N'DBCC CHECKIDENT (''arc.' + REPLACE(@doneTbl, '''', '''''') + N''', RESEED, '
                     + CAST(@seedTo AS nvarchar(20)) + N') WITH NO_INFOMSGS;';
            IF @Debug = 1 PRINT @sql;
            EXEC sys.sp_executesql @sql;
        END;

        UPDATE mig.ApcCopyTable SET Status = 'Done' WHERE TableName = @doneTbl;

        SET @msg = REPLACE(CONCAT(N'W', @WorkerId, N': ', @doneTbl, N' fully copied, marked Done.'), N'%', N'%%');
        RAISERROR (@msg, 0, 1) WITH NOWAIT;

        FETCH NEXT FROM dcur INTO @doneTbl;
    END;

    CLOSE dcur;
    DEALLOCATE dcur;

    SET @msg = REPLACE(CONCAT(N'W', @WorkerId, N': finished, ', @done, N' chunk(s) copied this session.'), N'%', N'%%');
    RAISERROR (@msg, 0, 1) WITH NOWAIT;
END;
GO

/* Same escaping in the index builder, where a captured ERROR_MESSAGE() can carry a percent. */
CREATE OR ALTER PROCEDURE mig.usp_ApcCopyIndexes
    @TablePattern sysname = N'APC%',
    @LoadedOnly   bit     = 1,
    @Debug        bit     = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @sql nvarchar(max), @remote nvarchar(max), @msg nvarchar(2000);

    CREATE TABLE #rix
    (
        TableName    sysname        NOT NULL,
        IndexName    sysname        NOT NULL,
        TypeDesc     nvarchar(60)   NOT NULL,
        IsPrimaryKey bit            NOT NULL,
        IsUnique     bit            NOT NULL,
        KeyCols      nvarchar(2000) NOT NULL,
        IncludeCols  nvarchar(2000) NULL,
        CONSTRAINT PK_rix PRIMARY KEY (TableName, IndexName)
    );

    SET @remote = N'
        SELECT t.name AS TableName, i.name AS IndexName, i.type_desc AS TypeDesc,
               CAST(i.is_primary_key AS int) AS IsPrimaryKey, CAST(i.is_unique AS int) AS IsUnique,
               STUFF((SELECT '', '' + QUOTENAME(c2.name) + CASE WHEN ic2.is_descending_key = 1 THEN '' DESC'' ELSE '''' END
                      FROM sys.index_columns ic2
                      JOIN sys.columns c2 ON c2.object_id = ic2.object_id AND c2.column_id = ic2.column_id
                      WHERE ic2.object_id = i.object_id AND ic2.index_id = i.index_id AND ic2.is_included_column = 0
                      ORDER BY ic2.key_ordinal FOR XML PATH('''')), 1, 2, '''') AS KeyCols,
               ISNULL(STUFF((SELECT '', '' + QUOTENAME(c3.name)
                      FROM sys.index_columns ic3
                      JOIN sys.columns c3 ON c3.object_id = ic3.object_id AND c3.column_id = ic3.column_id
                      WHERE ic3.object_id = i.object_id AND ic3.index_id = i.index_id AND ic3.is_included_column = 1
                      ORDER BY ic3.index_column_id FOR XML PATH('''')), 1, 2, ''''), '''') AS IncludeCols
        FROM sys.indexes i
        JOIN sys.tables t ON t.object_id = i.object_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        WHERE s.name = ''arc'' AND t.name LIKE ''' + REPLACE(@TablePattern, '''', '''''') + N'''
          AND i.index_id > 0 AND i.is_hypothetical = 0';

    SET @sql = N'INSERT INTO #rix (TableName, IndexName, TypeDesc, IsPrimaryKey, IsUnique, KeyCols, IncludeCols)
                 SELECT TableName, IndexName, TypeDesc, IsPrimaryKey, IsUnique, KeyCols, IncludeCols
                 FROM OPENQUERY(OLDPROD, ''' + REPLACE(@remote, '''', '''''') + N''');';
    IF @Debug = 1 PRINT @sql;
    EXEC sys.sp_executesql @sql;

    DECLARE @tbl sysname, @ix sysname, @type nvarchar(60), @isPk bit, @isUq bit,
            @keyCols nvarchar(2000), @incCols nvarchar(2000), @objName nvarchar(400), @t0 datetime2(3);

    /* Clustered/PK first: a heap that gets its clustered index last would rebuild every
       nonclustered index built before it. */
    DECLARE icur CURSOR LOCAL FAST_FORWARD FOR
        SELECT x.TableName, x.IndexName, x.TypeDesc, x.IsPrimaryKey, x.IsUnique, x.KeyCols, x.IncludeCols
        FROM #rix x
        JOIN mig.ApcCopyTable t ON t.TableName = x.TableName
        WHERE (@LoadedOnly = 0 OR t.Status = 'Done')
        ORDER BY t.Priority, CASE WHEN x.TypeDesc = N'CLUSTERED' THEN 0 ELSE 1 END, x.IndexName;

    OPEN icur;
    FETCH NEXT FROM icur INTO @tbl, @ix, @type, @isPk, @isUq, @keyCols, @incCols;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @objName = QUOTENAME(N'arc') + N'.' + QUOTENAME(@tbl);

        IF OBJECT_ID(@objName, 'U') IS NOT NULL
           AND NOT EXISTS (SELECT 1 FROM sys.indexes i
                           WHERE i.object_id = OBJECT_ID(@objName, 'U') AND i.name = @ix)
        BEGIN
            IF @isPk = 1
                SET @sql = N'ALTER TABLE ' + @objName + N' ADD CONSTRAINT ' + QUOTENAME(@ix)
                         + N' PRIMARY KEY ' + @type + N' (' + @keyCols + N');';
            ELSE
                SET @sql = N'CREATE ' + CASE WHEN @isUq = 1 THEN N'UNIQUE ' ELSE N'' END + @type
                         + N' INDEX ' + QUOTENAME(@ix) + N' ON ' + @objName + N' (' + @keyCols + N')'
                         + CASE WHEN NULLIF(@incCols, N'') IS NULL THEN N'' ELSE N' INCLUDE (' + @incCols + N')' END
                         + N';';

            IF @Debug = 1 PRINT @sql;
            SET @t0 = SYSUTCDATETIME();

            BEGIN TRY
                EXEC sys.sp_executesql @sql;
                SET @msg = REPLACE(CONCAT(N'ApcCopyIndexes: created ', @tbl, N'.', @ix, N' in ',
                                          DATEDIFF(SECOND, @t0, SYSUTCDATETIME()), N's.'), N'%', N'%%');
                RAISERROR (@msg, 0, 1) WITH NOWAIT;
            END TRY
            BEGIN CATCH
                SET @msg = REPLACE(CONCAT(N'ApcCopyIndexes: ', @tbl, N'.', @ix, N' FAILED: ', ERROR_MESSAGE()), N'%', N'%%');
                RAISERROR (@msg, 0, 1) WITH NOWAIT;
            END CATCH;
        END;

        FETCH NEXT FROM icur INTO @tbl, @ix, @type, @isPk, @isUq, @keyCols, @incCols;
    END;

    CLOSE icur;
    DEALLOCATE icur;
END;
GO
