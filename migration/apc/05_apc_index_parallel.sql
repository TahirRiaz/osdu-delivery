/*
    Parallel index build for the APC migration.

    mig.usp_ApcCopyIndexes walks every index through one cursor, so the whole tail runs on a
    single stream. Index builds on DIFFERENT tables share nothing, so they parallelise cleanly,
    and unlike the copy there is no shared heap to deadlock on: the single writer rule that the
    copy needed does not apply across tables here.

    Two rules keep it correct:

      1. Work is claimed atomically from mig.ApcIndexPlan under (UPDLOCK, READPAST), so no two
         workers take the same index.
      2. ONE WORKER PER TABLE. Concurrent DDL on the same table would serialise behind its schema
         lock anyway, and it would break the clustered-first ordering below.

    Ordering within a table is clustered/primary key FIRST. Building the clustered index after a
    nonclustered one would silently rebuild that nonclustered index, since its row locator changes
    from a heap RID to the clustering key.

    Fully restartable. The plan is rebuilt from OLDPROD each time it is prepared, and any index
    that already exists locally is marked Done rather than rebuilt, so this picks up exactly where
    the sequential build left off.
*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('mig.ApcIndexPlan', 'U') IS NULL
BEGIN
    CREATE TABLE mig.ApcIndexPlan
    (
        TableName    sysname        NOT NULL,
        IndexName    sysname        NOT NULL,
        TypeDesc     nvarchar(60)   NOT NULL,
        IsPrimaryKey bit            NOT NULL,
        IsUnique     bit            NOT NULL,
        KeyCols      nvarchar(2000) NOT NULL,
        IncludeCols  nvarchar(2000) NULL,
        TableRows    bigint         NULL,
        Status       varchar(20)    NOT NULL CONSTRAINT DF_ApcIndexPlan_Status DEFAULT ('Pending'),
        Attempts     int            NOT NULL CONSTRAINT DF_ApcIndexPlan_Attempts DEFAULT (0),
        WorkerId     int            NULL,
        StartedUtc   datetime2(3)   NULL,
        CompletedUtc datetime2(3)   NULL,
        ElapsedMs    bigint         NULL,
        ErrorMessage nvarchar(2000) NULL,
        CONSTRAINT PK_ApcIndexPlan PRIMARY KEY CLUSTERED (TableName, IndexName),
        CONSTRAINT CK_ApcIndexPlan_Status CHECK (Status IN ('Pending', 'Running', 'Done', 'Failed', 'Skipped'))
    );

    CREATE NONCLUSTERED INDEX IX_ApcIndexPlan_Status ON mig.ApcIndexPlan (Status, TableName);
END;
GO

/* ============================================================================================
   mig.usp_ApcIndexPrepare
     Pulls the index definitions from OLDPROD, and marks anything already built locally as Done
     so a restart never rebuilds completed work.
   ============================================================================================ */
CREATE OR ALTER PROCEDURE mig.usp_ApcIndexPrepare
    @TablePattern sysname = N'APC%',
    @Debug        bit     = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @sql nvarchar(max), @remote nvarchar(max);

    CREATE TABLE #rix
    (
        TableName    sysname        NOT NULL,
        IndexName    sysname        NOT NULL,
        TypeDesc     nvarchar(60)   NOT NULL,
        IsPrimaryKey bit            NOT NULL,
        IsUnique     bit            NOT NULL,
        KeyCols      nvarchar(2000) NOT NULL,
        IncludeCols  nvarchar(2000) NULL,
        CONSTRAINT PK_rix2 PRIMARY KEY (TableName, IndexName)
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

    MERGE mig.ApcIndexPlan AS tgt
    USING
    (
        SELECT x.TableName, x.IndexName, x.TypeDesc, x.IsPrimaryKey, x.IsUnique, x.KeyCols, x.IncludeCols,
               TableRows = t.RemoteRows
        FROM #rix x
        LEFT JOIN mig.ApcCopyTable t ON t.TableName = x.TableName
    ) AS src ON src.TableName = tgt.TableName AND src.IndexName = tgt.IndexName
    WHEN MATCHED THEN UPDATE SET
        tgt.TypeDesc     = src.TypeDesc,
        tgt.IsPrimaryKey = src.IsPrimaryKey,
        tgt.IsUnique     = src.IsUnique,
        tgt.KeyCols      = src.KeyCols,
        tgt.IncludeCols  = src.IncludeCols,
        tgt.TableRows    = src.TableRows
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (TableName, IndexName, TypeDesc, IsPrimaryKey, IsUnique, KeyCols, IncludeCols, TableRows)
        VALUES (src.TableName, src.IndexName, src.TypeDesc, src.IsPrimaryKey, src.IsUnique,
                src.KeyCols, src.IncludeCols, src.TableRows);

    /* Anything already present locally is finished work, whoever built it. */
    UPDATE p
    SET Status = 'Done',
        ErrorMessage = N'Already present before this run.'
    FROM mig.ApcIndexPlan p
    WHERE p.Status <> 'Done'
      AND EXISTS (SELECT 1 FROM sys.indexes i
                  WHERE i.object_id = OBJECT_ID(N'arc.' + QUOTENAME(p.TableName), 'U')
                    AND i.name = p.IndexName);

    /* A table that never got copied has nothing to index. */
    UPDATE p
    SET Status = 'Skipped', ErrorMessage = N'Target table does not exist locally.'
    FROM mig.ApcIndexPlan p
    WHERE p.Status = 'Pending'
      AND OBJECT_ID(N'arc.' + QUOTENAME(p.TableName), 'U') IS NULL;

    SELECT Status, Indexes = COUNT(*) FROM mig.ApcIndexPlan GROUP BY Status;

    SELECT TableName, TableRows,
           Pending = SUM(CASE WHEN Status = 'Pending' THEN 1 ELSE 0 END),
           Done    = SUM(CASE WHEN Status = 'Done'    THEN 1 ELSE 0 END)
    FROM mig.ApcIndexPlan
    GROUP BY TableName, TableRows
    HAVING SUM(CASE WHEN Status = 'Pending' THEN 1 ELSE 0 END) > 0
    ORDER BY TableRows DESC;
END;
GO

/* ============================================================================================
   mig.usp_ApcIndexRun
     One index-build worker. Run several concurrently, each with its own @WorkerId.

     Biggest tables are claimed FIRST here, the opposite of the copy. The build is only as fast
     as its slowest table, so the 456M row table must start immediately rather than end up last
     on a single stream.
   ============================================================================================ */
CREATE OR ALTER PROCEDURE mig.usp_ApcIndexRun
    @TablePattern    sysname = N'APC%',
    @WorkerId        int     = 1,
    @MaxAttempts     int     = 2,
    @IdleExitSeconds int     = 60,
    @ResetRunning    bit     = 0,
    @Debug           bit     = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

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
              /* one worker per table */
              AND NOT EXISTS (SELECT 1 FROM mig.ApcIndexPlan r
                              WHERE r.TableName = p.TableName AND r.Status = 'Running')
              /* clustered first: never start a nonclustered while its clustered is outstanding */
              AND NOT EXISTS (SELECT 1 FROM mig.ApcIndexPlan c
                              WHERE c.TableName = p.TableName
                                AND c.TypeDesc = N'CLUSTERED'
                                AND c.Status IN ('Pending', 'Failed')
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
            FROM mig.ApcIndexPlan
            WHERE TableName LIKE @TablePattern
              AND (Status IN ('Pending', 'Running') OR (Status = 'Failed' AND Attempts < @MaxAttempts));

            IF @workLeft = 0
            BEGIN
                SET @msg = REPLACE(CONCAT(N'IX-W', @WorkerId, N': nothing left to build.'), N'%', N'%%');
                RAISERROR (@msg, 0, 1) WITH NOWAIT;
                BREAK;
            END;

            IF @idleSince IS NULL SET @idleSince = SYSUTCDATETIME();

            IF DATEDIFF(SECOND, @idleSince, SYSUTCDATETIME()) >= @IdleExitSeconds
            BEGIN
                SET @msg = REPLACE(CONCAT(N'IX-W', @WorkerId, N': idle with ', @workLeft,
                                          N' index(es) held by other workers, exiting.'), N'%', N'%%');
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

/* ============================================================================================
   Progress view for the index build.
   ============================================================================================ */
CREATE OR ALTER VIEW mig.vw_ApcIndexProgress
AS
SELECT p.TableName,
       p.TableRows,
       Total    = COUNT(*),
       Done     = SUM(CASE WHEN p.Status = 'Done'    THEN 1 ELSE 0 END),
       Running  = SUM(CASE WHEN p.Status = 'Running' THEN 1 ELSE 0 END),
       Pending  = SUM(CASE WHEN p.Status = 'Pending' THEN 1 ELSE 0 END),
       Failed   = SUM(CASE WHEN p.Status = 'Failed'  THEN 1 ELSE 0 END),
       BuildMin = CAST(SUM(ISNULL(p.ElapsedMs, 0)) / 60000.0 AS decimal(10, 1))
FROM mig.ApcIndexPlan p
GROUP BY p.TableName, p.TableRows;
GO
