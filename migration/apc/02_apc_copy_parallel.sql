/*
    Parallel worker upgrade for the APC copy.

    A single OPENQUERY stream is latency bound, so the copy is driven by several concurrent
    worker sessions instead. Two rules make that safe:

      1. Chunks are claimed atomically. The claim is an UPDATE ... OUTPUT over the queue with
         (UPDLOCK, READPAST), so two workers can never take the same chunk and a worker never
         blocks behind another worker's claim.
      2. At most one worker per table. Workers skip any table that already has a chunk Running,
         which keeps concurrent inserts off the same heap and spreads the read load across
         different remote tables.

    OLDPROD is the LIVE old production DWH (GP_Gen5_8, 8 vCores). One saturated stream already
    costs it roughly 70 percent CPU, so worker count is a deliberate throttle, not a dial to
    max out. mig.usp_ApcCopySourceLoad reports what the copy is costing the source.
*/

SET NOCOUNT ON;
GO

IF COL_LENGTH('mig.ApcCopyChunk', 'WorkerId') IS NULL
    ALTER TABLE mig.ApcCopyChunk ADD WorkerId int NULL;
GO

/* ------------------------------------------------------------------------------------------
   mig.usp_ApcCopyRun (parallel capable)
     Safe to start in N concurrent sessions, each with a distinct @WorkerId.
   ------------------------------------------------------------------------------------------ */
CREATE OR ALTER PROCEDURE mig.usp_ApcCopyRun
    @TablePattern sysname = N'APC%',
    @WorkerId     int     = 1,
    @MaxChunks    int     = NULL,
    @MaxMinutes   int     = NULL,
    @MaxAttempts  int     = 3,
    @StopOnError  bit     = 0,
    @ResetRunning bit     = 0,
    @Debug        bit     = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @startedUtc datetime2(3) = SYSUTCDATETIME(),
            @done       int = 0,
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
            @msg        nvarchar(500);

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

        /* --- atomic claim ---------------------------------------------------------------- */
        DECLARE @claim TABLE (TableName sysname, ChunkNo int, LoKey bigint, HiKey bigint, Attempts int);
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
              /* one worker per table */
              AND NOT EXISTS (SELECT 1 FROM mig.ApcCopyChunk r
                              WHERE r.TableName = c.TableName AND r.Status = 'Running')
            ORDER BY t.Priority, c.TableName, c.ChunkNo
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
            SET @msg = CONCAT(N'W', @WorkerId, N': no claimable chunk for pattern ', @TablePattern, N'.');
            RAISERROR (@msg, 0, 1) WITH NOWAIT;
            BREAK;
        END;

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

        SET @tbl = NULL;
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

/* ------------------------------------------------------------------------------------------
   What the copy is costing the LIVE old production DWH. Run this while workers are active and
   back the worker count off if OLDPROD CPU stays high during business hours.
   ------------------------------------------------------------------------------------------ */
CREATE OR ALTER PROCEDURE mig.usp_ApcCopySourceLoad
    @Samples int = 8
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @sql nvarchar(max), @remote nvarchar(max);

    SET @remote = N'SELECT TOP ' + CAST(@Samples AS nvarchar(10))
                + N' end_time, avg_cpu_percent, avg_data_io_percent, avg_log_write_percent, max_worker_percent
                    FROM sys.dm_db_resource_stats ORDER BY end_time DESC';

    SET @sql = N'SELECT * FROM OPENQUERY(OLDPROD, ''' + REPLACE(@remote, '''', '''''') + N''');';
    EXEC sys.sp_executesql @sql;
END;
GO

/* Progress view: NULL-safe so a partially copied table does not raise the aggregate warning. */
CREATE OR ALTER VIEW mig.vw_ApcCopyProgress
AS
SELECT t.TableName,
       t.Priority,
       t.Status,
       t.RemoteRows,
       RowsCopied    = SUM(CASE WHEN c.Status = 'Done' THEN ISNULL(c.RowsCopied, 0) ELSE 0 END),
       PctRows       = CAST(CASE WHEN ISNULL(t.RemoteRows, 0) = 0 THEN 100.0
                                 ELSE SUM(CASE WHEN c.Status = 'Done' THEN ISNULL(c.RowsCopied, 0) ELSE 0 END)
                                      * 100.0 / t.RemoteRows END AS decimal(6, 2)),
       ChunksTotal   = COUNT(c.ChunkNo),
       ChunksDone    = SUM(CASE WHEN c.Status = 'Done'    THEN 1 ELSE 0 END),
       ChunksFailed  = SUM(CASE WHEN c.Status = 'Failed'  THEN 1 ELSE 0 END),
       ChunksRunning = SUM(CASE WHEN c.Status = 'Running' THEN 1 ELSE 0 END),
       RowsPerSec    = CAST(CASE WHEN SUM(ISNULL(c.ElapsedMs, 0)) = 0 THEN 0
                                 ELSE SUM(CASE WHEN c.Status = 'Done' THEN ISNULL(c.RowsCopied, 0) ELSE 0 END)
                                      * 1000.0 / SUM(ISNULL(c.ElapsedMs, 0)) END AS decimal(12, 1)),
       t.Message
FROM mig.ApcCopyTable t
LEFT JOIN mig.ApcCopyChunk c ON c.TableName = t.TableName
GROUP BY t.TableName, t.Priority, t.Status, t.RemoteRows, t.Message;
GO
