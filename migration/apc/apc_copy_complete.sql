/*
===============================================================================================
    APC arc-table migration, OLD production DWH -> NEW production DWH.
    Self contained. Run this whole file once against [dw-dwh-prod] on dw-mi-sql-prod, then
    drive it with the commands in the HOW TO RUN block below.

    All data movement happens server side. Each chunk is a single
    INSERT ... SELECT ... FROM OPENQUERY(OLDPROD, ...), so rows go straight from the old server
    into the new one and never stream through a client.

    Source : linked server OLDPROD (dw-sql-server-prod.database.windows.net, db dw-dwh-prod).
             READ ONLY. Nothing here writes to it.
    Target : the database this file is executed in, schema arc.

    Deploying this file is safe at any time. Everything is CREATE OR ALTER or IF NOT EXISTS, and
    it never touches copied data. It does not start a copy by itself.

-----------------------------------------------------------------------------------------------
    HOW TO RUN FROM SSMS
-----------------------------------------------------------------------------------------------

    STEP 1. Deploy (this file). Execute it once.

    STEP 2. Plan the work. Reads the remote DDL, creates any missing arc.APC_* table locally as
            a HEAP, and splits each into ~2,000,000 row key ranges. Safe to re-run: it only adds
            missing chunks and never disturbs completed ones.

                EXEC mig.usp_ApcCopyPrepare @TablePattern = N'APC%', @TargetChunkRows = 2000000;

    STEP 3. Copy. Open several query windows and run ONE of these per window, each with its own
            @WorkerId. Workers claim chunks atomically, so they never collide and never repeat
            each other's work. Add or close windows at will.

                EXEC mig.usp_ApcCopyRun @WorkerId = 1;   -- window 1
                EXEC mig.usp_ApcCopyRun @WorkerId = 2;   -- window 2, and so on

            8 to 12 windows is the useful range here. Beyond that the old server's data IO,
            not the link, becomes the limit. Each worker prints a line per chunk. Stopping a
            window mid chunk is safe: that chunk rolls back whole and is retried later.

    STEP 4. Watch it, from any window:

                SELECT * FROM mig.vw_ApcCopyProgress ORDER BY Priority;
                EXEC mig.usp_ApcCopySourceLoad;              -- what it costs the LIVE old server

    STEP 5. Indexes. Only after the copy is finished. Rebuilds OLDPROD's own primary keys and
            nonclustered indexes. Tables load as heaps on purpose: building the indexes once at
            the end is far cheaper than maintaining them across a thousand inserts.

                EXEC mig.usp_ApcCopyIndexes @TablePattern = N'APC%';

    STEP 6. Reconcile.

                EXEC mig.usp_ApcCopyVerify @TablePattern = N'APC%';                 -- row counts
                EXEC mig.usp_ApcCopyVerify @TablePattern = N'APC%', @DeepVerify = 1; -- + checksums

            The deep pass compares CHECKSUM_AGG(BINARY_CHECKSUM(...)) per table. It is byte
            sensitive, so it catches a codepage or type mangling that equal row counts would
            hide. It is a full scan on both sides, so run it when you can afford it.

-----------------------------------------------------------------------------------------------
    IF SOMETHING GOES WRONG
-----------------------------------------------------------------------------------------------

    Chunks left 'Running' by a session that was killed:

        -- only when NO worker is live, otherwise you would steal a chunk from a live one
        EXEC mig.usp_ApcCopyRun @WorkerId = 99, @ResetRunning = 1, @MaxChunks = 0;

    Inspect failures:

        SELECT * FROM mig.ApcCopyChunk WHERE Status = 'Failed';

    Requeue failures by hand (keep Attempts above zero so the retry clears its key range first,
    which makes the re-copy safe even if the failed insert had committed):

        UPDATE mig.ApcCopyChunk SET Status = 'Pending' WHERE Status = 'Failed';

    Start one table over from scratch (DESTRUCTIVE, drops and reloads that table):

        EXEC mig.usp_ApcCopyPrepare @TablePattern = N'APC_Company', @Recreate = 1;

-----------------------------------------------------------------------------------------------
    NOTES THAT COST TIME TO LEARN
-----------------------------------------------------------------------------------------------

    * ONE WRITER PER TABLE. @MaxWorkersPerTable defaults to 1 deliberately. Two workers doing
      INSERT ... WITH (TABLOCK) into the same heap deadlock against each other on this instance:
      APC_PassengerCount chunk 23 and APC_CallDetails_Dalane chunk 21 each lost all their
      attempts that way, one after 65 minutes of work. Raise it only if you enjoy that.

    * RESTART IS FREE. A chunk is one autocommit statement, so a failure or a dropped connection
      rolls it back whole and leaves no partial rows. Any retry deletes its key range first,
      which also covers the case where the insert committed but the status update did not.

    * IDENTITY IS PRESERVED. Tables are recreated with the remote IDENTITY property and copied
      with SET IDENTITY_INSERT ON, then reseeded to the true maximum key, so the surrogate keys
      match old production exactly and a later insert cannot collide with a migrated row.

    * NO COMPAT VIEWS NEEDED. Tables are rebuilt from OLDPROD's own sys.columns and sys.indexes,
      so column set, order, types, nullability and index definitions match old production by
      construction rather than by a view over a reshaped table.

    * RAISERROR AND PERCENT SIGNS. Progress messages are escaped with REPLACE(@msg,'%','%%')
      because RAISERROR reads its message as a format string, and the table pattern 'APC%' made
      it throw "Invalid format specification".
===============================================================================================
*/

SET NOCOUNT ON;
GO

IF SCHEMA_ID('mig') IS NULL
    EXEC ('CREATE SCHEMA mig AUTHORIZATION dbo;');
GO

IF SCHEMA_ID('arc') IS NULL
    EXEC ('CREATE SCHEMA arc AUTHORIZATION dbo;');
GO

/* ============================================================================================
   Type renderer: turns a row of remote sys.columns metadata back into a DDL type name.
   ============================================================================================ */
CREATE OR ALTER FUNCTION mig.fn_SqlTypeName
(
    @TypeName   sysname,
    @MaxLength  smallint,
    @Precision  tinyint,
    @Scale      tinyint
)
RETURNS nvarchar(200)
WITH SCHEMABINDING
AS
BEGIN
    RETURN CASE
        WHEN @TypeName IN (N'varchar', N'char', N'varbinary', N'binary')
            THEN QUOTENAME(@TypeName) + N'('
                 + CASE WHEN @MaxLength = -1 THEN N'max' ELSE CAST(@MaxLength AS nvarchar(10)) END + N')'
        WHEN @TypeName IN (N'nvarchar', N'nchar')
            THEN QUOTENAME(@TypeName) + N'('
                 + CASE WHEN @MaxLength = -1 THEN N'max' ELSE CAST(@MaxLength / 2 AS nvarchar(10)) END + N')'
        WHEN @TypeName IN (N'decimal', N'numeric')
            THEN QUOTENAME(@TypeName) + N'(' + CAST(@Precision AS nvarchar(10)) + N',' + CAST(@Scale AS nvarchar(10)) + N')'
        WHEN @TypeName IN (N'datetime2', N'time', N'datetimeoffset')
            THEN QUOTENAME(@TypeName) + N'(' + CAST(@Scale AS nvarchar(10)) + N')'
        WHEN @TypeName = N'float'
            THEN QUOTENAME(@TypeName) + N'(' + CAST(@Precision AS nvarchar(10)) + N')'
        ELSE QUOTENAME(@TypeName)
    END;
END;
GO

/* ============================================================================================
   Control tables
   ============================================================================================ */
IF OBJECT_ID('mig.ApcCopyTable', 'U') IS NULL
BEGIN
    CREATE TABLE mig.ApcCopyTable
    (
        TableName     sysname        NOT NULL,
        SourceSchema  sysname        NOT NULL CONSTRAINT DF_ApcCopyTable_SourceSchema DEFAULT (N'arc'),
        TargetSchema  sysname        NOT NULL CONSTRAINT DF_ApcCopyTable_TargetSchema DEFAULT (N'arc'),
        KeyColumn     sysname        NULL,
        KeyIsIdentity bit            NOT NULL CONSTRAINT DF_ApcCopyTable_KeyIsIdentity DEFAULT (0),
        HasIdentity   bit            NOT NULL CONSTRAINT DF_ApcCopyTable_HasIdentity   DEFAULT (0),
        RemoteRows    bigint         NULL,
        MinKey        bigint         NULL,
        MaxKey        bigint         NULL,
        ChunkWidth    bigint         NULL,
        ColumnList    nvarchar(max)  NULL,
        Priority      int            NOT NULL CONSTRAINT DF_ApcCopyTable_Priority DEFAULT (100),
        Status        varchar(20)    NOT NULL CONSTRAINT DF_ApcCopyTable_Status   DEFAULT ('New'),
        Message       nvarchar(2000) NULL,
        PreparedUtc   datetime2(3)   NULL,
        CONSTRAINT PK_ApcCopyTable PRIMARY KEY CLUSTERED (TableName),
        CONSTRAINT CK_ApcCopyTable_Status CHECK (Status IN ('New', 'Ready', 'Blocked', 'Done'))
    );
END;
GO

IF OBJECT_ID('mig.ApcCopyChunk', 'U') IS NULL
BEGIN
    CREATE TABLE mig.ApcCopyChunk
    (
        TableName    sysname        NOT NULL,
        ChunkNo      int            NOT NULL,
        LoKey        bigint         NOT NULL,
        HiKey        bigint         NOT NULL,
        Status       varchar(20)    NOT NULL CONSTRAINT DF_ApcCopyChunk_Status   DEFAULT ('Pending'),
        Attempts     int            NOT NULL CONSTRAINT DF_ApcCopyChunk_Attempts DEFAULT (0),
        RowsCopied   bigint         NULL,
        StartedUtc   datetime2(3)   NULL,
        CompletedUtc datetime2(3)   NULL,
        ElapsedMs    bigint         NULL,
        ErrorMessage nvarchar(2000) NULL,
        WorkerId     int            NULL,
        CONSTRAINT PK_ApcCopyChunk PRIMARY KEY CLUSTERED (TableName, ChunkNo),
        CONSTRAINT CK_ApcCopyChunk_Status CHECK (Status IN ('Pending', 'Running', 'Done', 'Failed')),
        CONSTRAINT FK_ApcCopyChunk_Table FOREIGN KEY (TableName) REFERENCES mig.ApcCopyTable (TableName)
    );

    CREATE NONCLUSTERED INDEX IX_ApcCopyChunk_Status ON mig.ApcCopyChunk (Status, TableName, ChunkNo);
END;
GO

IF COL_LENGTH('mig.ApcCopyChunk', 'WorkerId') IS NULL
    ALTER TABLE mig.ApcCopyChunk ADD WorkerId int NULL;
GO

/* ============================================================================================
   mig.usp_ApcCopyPrepare
     Reads the remote catalog through OPENQUERY, recreates each table locally as a HEAP with no
     indexes, and plans the key ranges.

     @Recreate = 1 DROPS and rebuilds any target table already present, discarding its rows and
     its chunk history. It is the only destructive switch here and defaults to 0.
   ============================================================================================ */
CREATE OR ALTER PROCEDURE mig.usp_ApcCopyPrepare
    @TablePattern    sysname = N'APC%',
    @TargetChunkRows bigint  = 2000000,
    @Recreate        bit     = 0,
    @Debug           bit     = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @TargetChunkRows < 1000
        THROW 50010, 'ApcCopyPrepare: @TargetChunkRows must be at least 1000.', 1;

    DECLARE @sql nvarchar(max), @remote nvarchar(max);

    /* --- remote table list and row counts ------------------------------------------------ */
    CREATE TABLE #rtab
    (
        TableName   sysname NOT NULL PRIMARY KEY,
        RemoteRows  bigint  NOT NULL,
        HasIdentity bit     NOT NULL
    );

    SET @remote = N'
        SELECT t.name AS TableName,
               SUM(p.rows) AS RemoteRows,
               MAX(CASE WHEN ic.object_id IS NULL THEN 0 ELSE 1 END) AS HasIdentity
        FROM sys.tables t
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
        LEFT JOIN sys.identity_columns ic ON ic.object_id = t.object_id
        WHERE s.name = ''arc'' AND t.name LIKE ''' + REPLACE(@TablePattern, '''', '''''') + N'''
        GROUP BY t.name';

    SET @sql = N'INSERT INTO #rtab (TableName, RemoteRows, HasIdentity)
                 SELECT TableName, RemoteRows, HasIdentity
                 FROM OPENQUERY(OLDPROD, ''' + REPLACE(@remote, '''', '''''') + N''');';
    IF @Debug = 1 PRINT @sql;
    EXEC sys.sp_executesql @sql;

    IF NOT EXISTS (SELECT 1 FROM #rtab)
        THROW 50011, 'ApcCopyPrepare: OLDPROD returned no arc tables for the given pattern.', 1;

    /* --- remote column metadata ---------------------------------------------------------- */
    CREATE TABLE #rcol
    (
        TableName      sysname  NOT NULL,
        ColumnName     sysname  NOT NULL,
        ColumnId       int      NOT NULL,
        TypeName       sysname  NOT NULL,
        MaxLength      smallint NOT NULL,
        [Precision]    tinyint  NOT NULL,
        Scale          tinyint  NOT NULL,
        IsNullable     bit      NOT NULL,
        IsIdentity     bit      NOT NULL,
        SeedValue      bigint   NULL,
        IncrementValue bigint   NULL,
        CONSTRAINT PK_rcol PRIMARY KEY (TableName, ColumnId)
    );

    SET @remote = N'
        SELECT t.name AS TableName, c.name AS ColumnName, c.column_id AS ColumnId, ty.name AS TypeName,
               c.max_length AS MaxLength, c.precision AS [Precision], c.scale AS Scale,
               c.is_nullable AS IsNullable, c.is_identity AS IsIdentity,
               CAST(ic.seed_value AS bigint) AS SeedValue, CAST(ic.increment_value AS bigint) AS IncrementValue
        FROM sys.columns c
        JOIN sys.tables t ON t.object_id = c.object_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        JOIN sys.types ty ON ty.user_type_id = c.user_type_id
        LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
        WHERE s.name = ''arc'' AND t.name LIKE ''' + REPLACE(@TablePattern, '''', '''''') + N'''';

    SET @sql = N'INSERT INTO #rcol (TableName, ColumnName, ColumnId, TypeName, MaxLength, [Precision], Scale,
                                    IsNullable, IsIdentity, SeedValue, IncrementValue)
                 SELECT TableName, ColumnName, ColumnId, TypeName, MaxLength, [Precision], Scale,
                        IsNullable, IsIdentity, SeedValue, IncrementValue
                 FROM OPENQUERY(OLDPROD, ''' + REPLACE(@remote, '''', '''''') + N''');';
    IF @Debug = 1 PRINT @sql;
    EXEC sys.sp_executesql @sql;

    /* --- chunk key: identity first, else a single-column unique NOT NULL integer ---------- */
    CREATE TABLE #rkey
    (
        TableName  sysname NOT NULL PRIMARY KEY,
        KeyColumn  sysname NOT NULL,
        IsIdentity bit     NOT NULL
    );

    INSERT INTO #rkey (TableName, KeyColumn, IsIdentity)
    SELECT c.TableName, c.ColumnName, 1
    FROM #rcol c
    WHERE c.IsIdentity = 1
      AND c.TypeName IN (N'int', N'bigint', N'smallint');

    SET @remote = N'
        SELECT t.name AS TableName, c.name AS KeyColumn
        FROM sys.indexes i
        JOIN sys.tables t ON t.object_id = i.object_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
        JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        JOIN sys.types ty ON ty.user_type_id = c.user_type_id
        WHERE s.name = ''arc'' AND t.name LIKE ''' + REPLACE(@TablePattern, '''', '''''') + N'''
          AND i.is_unique = 1 AND c.is_nullable = 0 AND ty.name IN (''int'', ''bigint'', ''smallint'')
          AND 1 = (SELECT COUNT(*) FROM sys.index_columns ic2
                   WHERE ic2.object_id = i.object_id AND ic2.index_id = i.index_id AND ic2.is_included_column = 0)';

    CREATE TABLE #runique (TableName sysname NOT NULL, KeyColumn sysname NOT NULL);
    SET @sql = N'INSERT INTO #runique (TableName, KeyColumn)
                 SELECT TableName, KeyColumn FROM OPENQUERY(OLDPROD, ''' + REPLACE(@remote, '''', '''''') + N''');';
    IF @Debug = 1 PRINT @sql;
    EXEC sys.sp_executesql @sql;

    INSERT INTO #rkey (TableName, KeyColumn, IsIdentity)
    SELECT u.TableName, MIN(u.KeyColumn), 0
    FROM #runique u
    WHERE NOT EXISTS (SELECT 1 FROM #rkey k WHERE k.TableName = u.TableName)
    GROUP BY u.TableName;

    /* --- upsert the control rows --------------------------------------------------------- */
    MERGE mig.ApcCopyTable AS tgt
    USING
    (
        SELECT t.TableName,
               t.RemoteRows,
               t.HasIdentity,
               k.KeyColumn,
               ISNULL(k.IsIdentity, 0) AS KeyIsIdentity,
               ColumnList = STUFF((SELECT N', ' + QUOTENAME(c.ColumnName)
                                   FROM #rcol c
                                   WHERE c.TableName = t.TableName
                                   ORDER BY c.ColumnId
                                   FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, N''),
               Priority = ROW_NUMBER() OVER (ORDER BY t.RemoteRows ASC, t.TableName ASC)
        FROM #rtab t
        LEFT JOIN #rkey k ON k.TableName = t.TableName
    ) AS src ON src.TableName = tgt.TableName
    WHEN MATCHED THEN UPDATE SET
        tgt.RemoteRows    = src.RemoteRows,
        tgt.HasIdentity   = src.HasIdentity,
        tgt.KeyColumn     = src.KeyColumn,
        tgt.KeyIsIdentity = src.KeyIsIdentity,
        tgt.ColumnList    = src.ColumnList,
        tgt.Priority      = src.Priority,
        tgt.PreparedUtc   = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (TableName, RemoteRows, HasIdentity, KeyColumn, KeyIsIdentity, ColumnList, Priority, PreparedUtc)
        VALUES (src.TableName, src.RemoteRows, src.HasIdentity, src.KeyColumn, src.KeyIsIdentity,
                src.ColumnList, src.Priority, SYSUTCDATETIME());

    UPDATE mig.ApcCopyTable
    SET Status  = 'Blocked',
        Message = N'No single-column integer identity or unique NOT NULL key is available on OLDPROD, so the copy cannot be chunked. Set KeyColumn by hand and re-run mig.usp_ApcCopyPrepare.'
    WHERE KeyColumn IS NULL
      AND TableName LIKE @TablePattern;

    /* --- create the target tables and plan the chunks ------------------------------------ */
    DECLARE @tbl sysname, @keyCol sysname, @rows bigint, @cols nvarchar(max),
            @minKey bigint, @maxKey bigint, @chunks bigint, @width bigint, @objName nvarchar(400);

    DECLARE tcur CURSOR LOCAL FAST_FORWARD FOR
        SELECT t.TableName, t.KeyColumn, t.RemoteRows, t.ColumnList
        FROM mig.ApcCopyTable t
        WHERE t.TableName LIKE @TablePattern
          AND t.KeyColumn IS NOT NULL
        ORDER BY t.Priority;

    OPEN tcur;
    FETCH NEXT FROM tcur INTO @tbl, @keyCol, @rows, @cols;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @objName = QUOTENAME(N'arc') + N'.' + QUOTENAME(@tbl);

        IF @Recreate = 1 AND OBJECT_ID(@objName, 'U') IS NOT NULL
        BEGIN
            SET @sql = N'DROP TABLE ' + @objName + N';';
            IF @Debug = 1 PRINT @sql;
            EXEC sys.sp_executesql @sql;

            DELETE FROM mig.ApcCopyChunk WHERE TableName = @tbl;
        END;

        IF OBJECT_ID(@objName, 'U') IS NULL
        BEGIN
            SELECT @sql = N'CREATE TABLE ' + @objName + N' (' + CHAR(13) + CHAR(10)
                 + STUFF((SELECT N',' + CHAR(13) + CHAR(10) + N'    ' + QUOTENAME(c.ColumnName) + N' '
                                 + mig.fn_SqlTypeName(c.TypeName, c.MaxLength, c.[Precision], c.Scale)
                                 + CASE WHEN c.IsIdentity = 1
                                        THEN N' IDENTITY(' + CAST(ISNULL(c.SeedValue, 1) AS nvarchar(20)) + N','
                                             + CAST(ISNULL(c.IncrementValue, 1) AS nvarchar(20)) + N')'
                                        ELSE N'' END
                                 + CASE WHEN c.IsNullable = 1 THEN N' NULL' ELSE N' NOT NULL' END
                          FROM #rcol c
                          WHERE c.TableName = @tbl
                          ORDER BY c.ColumnId
                          FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 3, N'    ')
                 + CHAR(13) + CHAR(10) + N');';

            IF @Debug = 1 PRINT @sql;
            EXEC sys.sp_executesql @sql;
        END;

        /* MIN/MAX by TOP 1 ORDER BY: a seek on the remote clustered PK or unique index, so this
           stays instant even on the 456M row tables. A remote MIN()/MAX() would scan. */
        SET @minKey = NULL;
        SET @maxKey = NULL;

        SET @remote = N'SELECT (SELECT TOP 1 ' + QUOTENAME(@keyCol) + N' FROM arc.' + QUOTENAME(@tbl)
                     + N' ORDER BY ' + QUOTENAME(@keyCol) + N' ASC) AS MinKey, (SELECT TOP 1 '
                     + QUOTENAME(@keyCol) + N' FROM arc.' + QUOTENAME(@tbl) + N' ORDER BY '
                     + QUOTENAME(@keyCol) + N' DESC) AS MaxKey';

        SET @sql = N'SELECT @minOut = CAST(MinKey AS bigint), @maxOut = CAST(MaxKey AS bigint)
                     FROM OPENQUERY(OLDPROD, ''' + REPLACE(@remote, '''', '''''') + N''');';
        IF @Debug = 1 PRINT @sql;
        EXEC sys.sp_executesql @sql,
             N'@minOut bigint OUTPUT, @maxOut bigint OUTPUT',
             @minOut = @minKey OUTPUT, @maxOut = @maxKey OUTPUT;

        IF @minKey IS NULL
        BEGIN
            UPDATE mig.ApcCopyTable
            SET MinKey = NULL, MaxKey = NULL, ChunkWidth = NULL, Status = 'Done',
                Message = N'Empty on OLDPROD, nothing to copy.'
            WHERE TableName = @tbl;
        END
        ELSE
        BEGIN
            SET @chunks = CASE WHEN @rows <= @TargetChunkRows THEN 1
                               ELSE CAST(CEILING(@rows * 1.0 / @TargetChunkRows) AS bigint) END;
            SET @width  = CAST(CEILING((@maxKey - @minKey + 1) * 1.0 / @chunks) AS bigint);
            IF @width < 1 SET @width = 1;

            UPDATE mig.ApcCopyTable
            SET MinKey = @minKey, MaxKey = @maxKey, ChunkWidth = @width,
                Status = 'Ready', Message = NULL
            WHERE TableName = @tbl;

            /* Only ever ADD missing chunks so an in-flight run keeps its history. */
            ;WITH n AS
            (
                SELECT TOP (CAST(@chunks AS int))
                       ChunkNo = ROW_NUMBER() OVER (ORDER BY (SELECT NULL))
                FROM sys.all_columns a CROSS JOIN sys.all_columns b
            )
            INSERT INTO mig.ApcCopyChunk (TableName, ChunkNo, LoKey, HiKey)
            SELECT @tbl,
                   n.ChunkNo,
                   @minKey + (n.ChunkNo - 1) * @width,
                   CASE WHEN n.ChunkNo = @chunks THEN @maxKey
                        ELSE @minKey + n.ChunkNo * @width - 1 END
            FROM n
            WHERE NOT EXISTS (SELECT 1 FROM mig.ApcCopyChunk c
                              WHERE c.TableName = @tbl AND c.ChunkNo = n.ChunkNo);
        END;

        FETCH NEXT FROM tcur INTO @tbl, @keyCol, @rows, @cols;
    END;

    CLOSE tcur;
    DEALLOCATE tcur;

    SELECT TableName, KeyColumn, RemoteRows, MinKey, MaxKey, ChunkWidth,
           Chunks = (SELECT COUNT(*) FROM mig.ApcCopyChunk c WHERE c.TableName = t.TableName),
           Status, Message
    FROM mig.ApcCopyTable t
    WHERE t.TableName LIKE @TablePattern
    ORDER BY t.Priority;
END;
GO

/* ============================================================================================
   mig.usp_ApcCopyRun
     One copy worker. Run several concurrently, each with its own @WorkerId.

     Chunks are claimed with an UPDATE ... OUTPUT under (UPDLOCK, READPAST), so two workers can
     never take the same chunk and a worker never blocks behind another worker's claim.
   ============================================================================================ */
CREATE OR ALTER PROCEDURE mig.usp_ApcCopyRun
    @TablePattern       sysname = N'APC%',
    @WorkerId           int     = 1,
    @MaxChunks          int     = NULL,
    @MaxMinutes         int     = NULL,
    @MaxAttempts        int     = 6,
    @MaxWorkersPerTable int     = 1,
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

/* ============================================================================================
   mig.usp_ApcCopyIndexes
     Rebuilds the OLDPROD primary key and nonclustered indexes on the loaded tables. Run only
     after the copy finishes.
   ============================================================================================ */
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

/* ============================================================================================
   mig.usp_ApcCopyVerify
     Reconciles the copy against OLDPROD. Counts always; @DeepVerify also compares
     CHECKSUM_AGG(BINARY_CHECKSUM(*)) per table, which is byte sensitive and therefore catches a
     codepage or type mangling that a row count cannot.
   ============================================================================================ */
CREATE OR ALTER PROCEDURE mig.usp_ApcCopyVerify
    @TablePattern sysname = N'APC%',
    @DeepVerify   bit     = 0,
    @Debug        bit     = 0
AS
BEGIN
    SET NOCOUNT ON;

    CREATE TABLE #res
    (
        TableName      sysname NOT NULL PRIMARY KEY,
        RemoteRows     bigint  NULL,
        LocalRows      bigint  NULL,
        RemoteChecksum bigint  NULL,
        LocalChecksum  bigint  NULL
    );

    DECLARE @tbl sysname, @cols nvarchar(max), @sql nvarchar(max), @remote nvarchar(max),
            @rrows bigint, @lrows bigint, @rsum bigint, @lsum bigint, @objName nvarchar(400);

    DECLARE vcur CURSOR LOCAL FAST_FORWARD FOR
        SELECT TableName, ColumnList FROM mig.ApcCopyTable
        WHERE TableName LIKE @TablePattern ORDER BY Priority;

    OPEN vcur;
    FETCH NEXT FROM vcur INTO @tbl, @cols;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @objName = QUOTENAME(N'arc') + N'.' + QUOTENAME(@tbl);
        SET @rrows = NULL; SET @lrows = NULL; SET @rsum = NULL; SET @lsum = NULL;

        SET @remote = N'SELECT COUNT_BIG(*) AS c FROM arc.' + QUOTENAME(@tbl);
        SET @sql = N'SELECT @out = c FROM OPENQUERY(OLDPROD, ''' + REPLACE(@remote, '''', '''''') + N''');';
        IF @Debug = 1 PRINT @sql;
        EXEC sys.sp_executesql @sql, N'@out bigint OUTPUT', @out = @rrows OUTPUT;

        IF OBJECT_ID(@objName, 'U') IS NOT NULL
        BEGIN
            SET @sql = N'SELECT @out = COUNT_BIG(*) FROM ' + @objName + N';';
            EXEC sys.sp_executesql @sql, N'@out bigint OUTPUT', @out = @lrows OUTPUT;
        END;

        IF @DeepVerify = 1 AND OBJECT_ID(@objName, 'U') IS NOT NULL
        BEGIN
            SET @remote = N'SELECT CAST(CHECKSUM_AGG(BINARY_CHECKSUM(' + @cols + N')) AS bigint) AS s FROM arc.'
                        + QUOTENAME(@tbl);
            SET @sql = N'SELECT @out = s FROM OPENQUERY(OLDPROD, ''' + REPLACE(@remote, '''', '''''') + N''');';
            IF @Debug = 1 PRINT @sql;
            EXEC sys.sp_executesql @sql, N'@out bigint OUTPUT', @out = @rsum OUTPUT;

            SET @sql = N'SELECT @out = CAST(CHECKSUM_AGG(BINARY_CHECKSUM(' + @cols + N')) AS bigint) FROM ' + @objName + N';';
            EXEC sys.sp_executesql @sql, N'@out bigint OUTPUT', @out = @lsum OUTPUT;
        END;

        INSERT INTO #res (TableName, RemoteRows, LocalRows, RemoteChecksum, LocalChecksum)
        VALUES (@tbl, @rrows, @lrows, @rsum, @lsum);

        FETCH NEXT FROM vcur INTO @tbl, @cols;
    END;

    CLOSE vcur;
    DEALLOCATE vcur;

    SELECT r.TableName,
           r.RemoteRows,
           r.LocalRows,
           RowDelta = r.LocalRows - r.RemoteRows,
           r.RemoteChecksum,
           r.LocalChecksum,
           Verdict = CASE
                        WHEN r.LocalRows IS NULL THEN 'MISSING: target table not created'
                        WHEN r.LocalRows <> r.RemoteRows THEN 'ROW COUNT MISMATCH'
                        WHEN @DeepVerify = 1 AND ISNULL(r.LocalChecksum, -1) <> ISNULL(r.RemoteChecksum, -2)
                            THEN 'CHECKSUM MISMATCH'
                        WHEN @DeepVerify = 1 THEN 'OK (rows + checksum)'
                        ELSE 'OK (rows)'
                     END
    FROM #res r
    JOIN mig.ApcCopyTable t ON t.TableName = r.TableName
    ORDER BY t.Priority;
END;
GO

/* ============================================================================================
   What the copy is costing the LIVE old production DWH.
   ============================================================================================ */
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

/* ============================================================================================
   Progress view
   ============================================================================================ */
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
