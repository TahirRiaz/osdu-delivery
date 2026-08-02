/*
    Index build, corrected concurrency model.

    What the 6-way parallel attempt taught us: clustered index builds do NOT parallelise on this
    General Purpose instance. Six concurrent clustered builds on the 456M / 280M / 243M / 206M /
    166M row tables all contended for the same storage and tempdb sort space. One finished in
    107 minutes; the other five ran 3 hours 47 minutes without completing and were lost to
    rollback when their connections dropped. Sequentially the same class of build was running a
    110M row PK in 19 minutes, so concurrency made each build roughly 3x slower and cost more
    than it saved.

    A long build is also a long silence on the connection, since a worker only reports when an
    index completes. Keeping each build short is therefore a reliability property, not just a
    speed one.

    So the work is split by index type:

      * CLUSTERED  -> ONE worker. Sequential, and it gets the whole box's parallelism for its
                      own sort, which is what actually makes it finish quickly.
      * NONCLUSTERED -> several workers. These are far cheaper (110-200s on a 100M row table)
                      and they run against tables whose clustered index already exists, so they
                      neither block nor get rebuilt.

    The clustered-first rule inside the claim still holds: a nonclustered index is never started
    while its own table's clustered index is outstanding, because building the clustered index
    afterwards would silently rebuild it.
*/

SET NOCOUNT ON;
GO

CREATE OR ALTER PROCEDURE mig.usp_ApcIndexRun
    @TablePattern     sysname      = N'APC%',
    @WorkerId         int          = 1,
    @IndexTypeFilter  nvarchar(30) = NULL,   -- 'CLUSTERED', 'NONCLUSTERED', or NULL for any
    @MaxAttempts      int          = 2,
    @IdleExitSeconds  int          = 60,
    @ResetRunning     bit          = 0,
    @Debug            bit          = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @IndexTypeFilter IS NOT NULL AND @IndexTypeFilter NOT IN (N'CLUSTERED', N'NONCLUSTERED')
        THROW 50030, 'ApcIndexRun: @IndexTypeFilter must be CLUSTERED, NONCLUSTERED, or NULL.', 1;

    DECLARE @tbl sysname, @ix sysname, @type nvarchar(60), @isPk bit, @isUq bit,
            @keyCols nvarchar(2000), @incCols nvarchar(2000), @objName nvarchar(400),
            @sql nvarchar(max), @msg nvarchar(2000), @t0 datetime2(3),
            @idleSince datetime2(3) = NULL, @workLeft int, @done int = 0;

    IF @ResetRunning = 1
    BEGIN
        UPDATE mig.ApcIndexPlan
        SET Status = 'Pending', ErrorMessage = N'Reset from Running: previous session did not finish it.'
        WHERE Status = 'Running' AND TableName LIKE @TablePattern;
    END;

    DECLARE @claim TABLE (TableName sysname, IndexName sysname, TypeDesc nvarchar(60),
                          IsPrimaryKey bit, IsUnique bit, KeyCols nvarchar(2000), IncludeCols nvarchar(2000));

    WHILE 1 = 1
    BEGIN
        SET @tbl = NULL;
        DELETE FROM @claim;

        ;WITH nxt AS
        (
            SELECT TOP (1) p.TableName, p.IndexName, p.TypeDesc, p.IsPrimaryKey, p.IsUnique,
                   p.KeyCols, p.IncludeCols, p.Status, p.Attempts, p.WorkerId, p.StartedUtc,
                   p.CompletedUtc, p.ElapsedMs, p.ErrorMessage
            FROM mig.ApcIndexPlan p WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE p.TableName LIKE @TablePattern
              AND (p.Status = 'Pending' OR (p.Status = 'Failed' AND p.Attempts < @MaxAttempts))
              AND (@IndexTypeFilter IS NULL
                   OR (@IndexTypeFilter = N'CLUSTERED'    AND p.TypeDesc =  N'CLUSTERED')
                   OR (@IndexTypeFilter = N'NONCLUSTERED' AND p.TypeDesc <> N'CLUSTERED'))
              /* one worker per table */
              AND NOT EXISTS (SELECT 1 FROM mig.ApcIndexPlan r
                              WHERE r.TableName = p.TableName AND r.Status = 'Running')
              /* never start a nonclustered while its own table's clustered is outstanding */
              AND NOT EXISTS (SELECT 1 FROM mig.ApcIndexPlan c
                              WHERE c.TableName = p.TableName
                                AND c.TypeDesc = N'CLUSTERED'
                                AND c.Status IN ('Pending', 'Running', 'Failed')
                                AND p.TypeDesc <> N'CLUSTERED')
            ORDER BY p.TableRows DESC,
                     CASE WHEN p.TypeDesc = N'CLUSTERED' THEN 0 ELSE 1 END,
                     p.IndexName
        )
        UPDATE nxt
        SET Status = 'Running', Attempts = Attempts + 1, WorkerId = @WorkerId,
            StartedUtc = SYSUTCDATETIME(), CompletedUtc = NULL, ElapsedMs = NULL, ErrorMessage = NULL
        OUTPUT inserted.TableName, inserted.IndexName, inserted.TypeDesc, inserted.IsPrimaryKey,
               inserted.IsUnique, inserted.KeyCols, inserted.IncludeCols
        INTO @claim (TableName, IndexName, TypeDesc, IsPrimaryKey, IsUnique, KeyCols, IncludeCols);

        SELECT @tbl = TableName, @ix = IndexName, @type = TypeDesc, @isPk = IsPrimaryKey,
               @isUq = IsUnique, @keyCols = KeyCols, @incCols = IncludeCols
        FROM @claim;

        IF @tbl IS NULL
        BEGIN
            SELECT @workLeft = COUNT(*)
            FROM mig.ApcIndexPlan p
            WHERE p.TableName LIKE @TablePattern
              AND (p.Status IN ('Pending', 'Running') OR (p.Status = 'Failed' AND p.Attempts < @MaxAttempts))
              AND (@IndexTypeFilter IS NULL
                   OR (@IndexTypeFilter = N'CLUSTERED'    AND p.TypeDesc =  N'CLUSTERED')
                   OR (@IndexTypeFilter = N'NONCLUSTERED' AND p.TypeDesc <> N'CLUSTERED'));

            IF @workLeft = 0
            BEGIN
                SET @msg = REPLACE(CONCAT(N'IX-W', @WorkerId, N': nothing left in scope.'), N'%', N'%%');
                RAISERROR (@msg, 0, 1) WITH NOWAIT;
                BREAK;
            END;

            IF @idleSince IS NULL SET @idleSince = SYSUTCDATETIME();

            IF DATEDIFF(SECOND, @idleSince, SYSUTCDATETIME()) >= @IdleExitSeconds
            BEGIN
                SET @msg = REPLACE(CONCAT(N'IX-W', @WorkerId, N': idle, ', @workLeft,
                                          N' index(es) blocked or held elsewhere, exiting.'), N'%', N'%%');
                RAISERROR (@msg, 0, 1) WITH NOWAIT;
                BREAK;
            END;

            WAITFOR DELAY '00:00:10';
            CONTINUE;
        END;

        SET @idleSince = NULL;
        SET @objName = QUOTENAME(N'arc') + N'.' + QUOTENAME(@tbl);
        SET @t0 = SYSUTCDATETIME();

        BEGIN TRY
            IF EXISTS (SELECT 1 FROM sys.indexes i
                       WHERE i.object_id = OBJECT_ID(@objName, 'U') AND i.name = @ix)
            BEGIN
                UPDATE mig.ApcIndexPlan
                SET Status = 'Done', CompletedUtc = SYSUTCDATETIME(), ElapsedMs = 0,
                    ErrorMessage = N'Already present.'
                WHERE TableName = @tbl AND IndexName = @ix;
            END
            ELSE
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
                EXEC sys.sp_executesql @sql;

                UPDATE mig.ApcIndexPlan
                SET Status = 'Done', CompletedUtc = SYSUTCDATETIME(),
                    ElapsedMs = DATEDIFF_BIG(MILLISECOND, @t0, SYSUTCDATETIME()), ErrorMessage = NULL
                WHERE TableName = @tbl AND IndexName = @ix;

                SET @done += 1;
            END;

            SET @msg = REPLACE(CONCAT(N'IX-W', @WorkerId, N': ', @tbl, N'.', @ix, N' (', @type, N') built in ',
                                      DATEDIFF(SECOND, @t0, SYSUTCDATETIME()), N's.'), N'%', N'%%');
            RAISERROR (@msg, 0, 1) WITH NOWAIT;
        END TRY
        BEGIN CATCH
            DECLARE @err nvarchar(2000) = LEFT(ERROR_MESSAGE(), 2000);

            UPDATE mig.ApcIndexPlan
            SET Status = 'Failed', CompletedUtc = SYSUTCDATETIME(),
                ElapsedMs = DATEDIFF_BIG(MILLISECOND, @t0, SYSUTCDATETIME()), ErrorMessage = @err
            WHERE TableName = @tbl AND IndexName = @ix;

            SET @msg = REPLACE(CONCAT(N'IX-W', @WorkerId, N': ', @tbl, N'.', @ix, N' FAILED: ', @err), N'%', N'%%');
            RAISERROR (@msg, 0, 1) WITH NOWAIT;
        END CATCH;
    END;

    SET @msg = REPLACE(CONCAT(N'IX-W', @WorkerId, N': finished, ', @done, N' index(es) built this session.'), N'%', N'%%');
    RAISERROR (@msg, 0, 1) WITH NOWAIT;
END;
GO
