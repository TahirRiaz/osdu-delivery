/*
    Tail handling for the APC copy.

    Two problems show up once most tables are finished:

      1. With one worker per table, workers go idle as soon as the number of unfinished tables
         drops below the worker count. The last table (APC_CallDetails_Norgesbuss, 228 chunks)
         would otherwise finish on a single stream.
      2. A worker that finds nothing claimable exits, so the operator has to keep relaunching it
         while other workers are still busy.

    So a table now admits up to @MaxWorkersPerTable concurrent workers, and a worker that finds
    nothing claimable waits and retries instead of exiting, until either the queue is genuinely
    empty or it has been idle for @IdleExitSeconds.

    Concurrent writers on one table are safe here: INSERT ... WITH (TABLOCK) into a heap takes a
    bulk update lock, which is compatible with other bulk update locks, and SET IDENTITY_INSERT
    is session scoped so two sessions can hold it on the same table (verified against this
    instance before enabling this).
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

    /* Only the operator resets orphaned chunks, and only when no worker is live: a running
       worker must never reset a chunk that another worker legitimately holds. */
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
            SET @msg = CONCAT(N'W', @WorkerId, N': stopping, @MaxChunks (', @MaxChunks, N') reached.');
            RAISERROR (@msg, 0, 1) WITH NOWAIT;
            BREAK;
        END;

        IF @MaxMinutes IS NOT NULL AND DATEDIFF(MINUTE, @startedUtc, SYSUTCDATETIME()) >= @MaxMinutes
        BEGIN
            SET @msg = CONCAT(N'W', @WorkerId, N': stopping, @MaxMinutes (', @MaxMinutes, N') reached.');
            RAISERROR (@msg, 0, 1) WITH NOWAIT;
            BREAK;
        END;

        /* --- atomic claim, spreading workers across the least busy tables ----------------- */
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
            /* Nothing claimable. Either the queue is empty (finish) or every remaining table is
               at its worker limit (wait for one to free up). */
            SELECT @workLeft = COUNT(*)
            FROM mig.ApcCopyChunk c
            JOIN mig.ApcCopyTable t ON t.TableName = c.TableName
            WHERE c.TableName LIKE @TablePattern
              AND t.Status = 'Ready'
              AND (c.Status IN ('Pending', 'Running') OR (c.Status = 'Failed' AND c.Attempts < @MaxAttempts));

            IF @workLeft = 0
            BEGIN
                SET @msg = CONCAT(N'W', @WorkerId, N': queue empty for pattern ', @TablePattern, N'.');
                RAISERROR (@msg, 0, 1) WITH NOWAIT;
                BREAK;
            END;

            IF @idleSince IS NULL
                SET @idleSince = SYSUTCDATETIME();

            IF DATEDIFF(SECOND, @idleSince, SYSUTCDATETIME()) >= @IdleExitSeconds
            BEGIN
                SET @msg = CONCAT(N'W', @WorkerId, N': idle ', @IdleExitSeconds,
                                  N's with ', @workLeft, N' chunk(s) held by other workers, exiting.');
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

            SET @msg = CONCAT(N'W', @WorkerId, N': ', @tbl, N' chunk ', @chunkNo, N' -> ', @rowsCopied,
                              N' rows in ', DATEDIFF(SECOND, @t0, SYSUTCDATETIME()), N's (',
                              CAST(@rowsCopied * 1000 / NULLIF(DATEDIFF_BIG(MILLISECOND, @t0, SYSUTCDATETIME()), 0) AS bigint),
                              N' rows/s).');
            RAISERROR (@msg, 0, 1) WITH NOWAIT;
        END TRY
        BEGIN CATCH
            DECLARE @err nvarchar(2000) = LEFT(ERROR_MESSAGE(), 2000);

            UPDATE mig.ApcCopyChunk
            SET Status = 'Failed', CompletedUtc = SYSUTCDATETIME(),
                ElapsedMs = DATEDIFF_BIG(MILLISECOND, @t0, SYSUTCDATETIME()), ErrorMessage = @err
            WHERE TableName = @tbl AND ChunkNo = @chunkNo;

            SET @msg = CONCAT(N'W', @WorkerId, N': ', @tbl, N' chunk ', @chunkNo, N' FAILED: ', @err);
            RAISERROR (@msg, 0, 1) WITH NOWAIT;

            IF @StopOnError = 1
                BREAK;
        END CATCH;
    END;

    /* Mark fully copied tables Done and realign the identity seed with the copied keys, so a
       later insert cannot collide with a migrated row. Idempotent, so concurrent workers
       reaching this at the same time is harmless. */
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

        FETCH NEXT FROM dcur INTO @doneTbl;
    END;

    CLOSE dcur;
    DEALLOCATE dcur;

    SET @msg = CONCAT(N'W', @WorkerId, N': finished, ', @done, N' chunk(s) copied this session.');
    RAISERROR (@msg, 0, 1) WITH NOWAIT;
END;
GO
