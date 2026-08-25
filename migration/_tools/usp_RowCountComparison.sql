/*  Row-count comparison, old production DWH vs new production DWH, as a stored procedure.

    Install once on the NEW server (dw-mi-sql-prod), in the database you want to compare
    (dw-dwh-prod). The procedure compares THIS database against the same-named database on
    the other side of a linked server, so installing it in dw-pre-prod compares the landing
    databases instead, given a linked server that points there.

    Run it from SSMS:

        EXEC mig.RowCountComparison;                          -- only the tables that differ
        EXEC mig.RowCountComparison @OnlyDifferences = 0;     -- every table, including matches
        EXEC mig.RowCountComparison @SchemaName = 'edw';      -- one schema
        EXEC mig.RowCountComparison @NameLike = 'Fara[_]%';   -- one source
        EXEC mig.RowCountComparison @IncludeRetired = 1;      -- also the excluded objects

    It returns two result sets: a summary with the totals and percentages, then the detail
    row per table. Everything it does is read-only, on both sides.

    Add an exclusion for an object that is deliberately not carried forward:

        INSERT mig.RowCountExclusion (SchemaName, NamePattern, Reason)
        VALUES ('arc', 'Foo[_]%', 'Not carried forward');

    NamePattern is a LIKE pattern, so escape an underscore as [_] when it is a literal.
*/

IF SCHEMA_ID('mig') IS NULL
    EXEC ('CREATE SCHEMA mig');
GO

IF OBJECT_ID('mig.RowCountExclusion') IS NULL
    CREATE TABLE mig.RowCountExclusion
    (
        SchemaName  sysname       NOT NULL,
        NamePattern nvarchar(200) NOT NULL,
        Reason      nvarchar(400) NOT NULL,
        AddedDate   datetime2(0)  NOT NULL
            CONSTRAINT DF_RowCountExclusion_AddedDate DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_RowCountExclusion PRIMARY KEY CLUSTERED (SchemaName, NamePattern)
    );
GO

-- Seed the exclusions agreed so far. Re-running the script never duplicates or overwrites
-- them, so a reason edited in the table survives the next install.
INSERT mig.RowCountExclusion (SchemaName, NamePattern, Reason)
SELECT v.SchemaName, v.NamePattern, v.Reason
FROM (VALUES
        ('arc', 'TTDB[_]%',                 'Trapeze TTDB is not carried forward'),
        ('arc', 'Fara_Statload_DupeActual', 'Temp table, never a consumer contract'),
        ('arc', 'SVV_Monthtraffic',         'Not carried forward'),
        ('edw', 'APC_MatchedTrip',          'Dropped in the APCEDW conversion'),
        ('edw', 'APC_PassengersPerLine',    'Not carried forward'),
        ('edw', 'APC_PassangersPerDay',     'Not carried forward'),
        ('edw', 'APC_PassengersPerStop',    'Not carried forward'),
        ('edw', 'Dim_Calendar_edit',        'Not carried forward')
     ) AS v (SchemaName, NamePattern, Reason)
WHERE NOT EXISTS (SELECT 1 FROM mig.RowCountExclusion x
                   WHERE x.SchemaName = v.SchemaName AND x.NamePattern = v.NamePattern);
GO

CREATE OR ALTER PROCEDURE mig.RowCountComparison
    @LinkedServer     sysname       = 'OLDPROD',
    -- Schemas whose tables are read by something downstream. Everything else is the old
    -- engine's own staging and scratch, where a difference is not a finding.
    @ConsumerSchemas  nvarchar(400) = 'arc,edw,skey,man,sta,ver',
    @OnlyDifferences  bit           = 1,
    @IncludeRetired   bit           = 0,
    @IncludeBacking   bit           = 0,
    @IncludeInternal  bit           = 0,
    @SchemaName       sysname       = NULL,
    @NameLike         nvarchar(200) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
    SET XACT_ABORT ON;

    IF NOT EXISTS (SELECT 1 FROM sys.servers WHERE is_linked = 1 AND name = @LinkedServer)
    BEGIN
        DECLARE @msg nvarchar(300) = N'Linked server ' + QUOTENAME(@LinkedServer)
            + N' does not exist on this instance. Pass @LinkedServer, or create the link to old production.';
        THROW 51000, @msg, 1;
    END

    CREATE TABLE #old (sch sysname NOT NULL, tbl sysname NOT NULL, rowcnt bigint NOT NULL,
                       PRIMARY KEY (sch, tbl));
    CREATE TABLE #new (sch sysname NOT NULL, tbl sysname NOT NULL, rowcnt bigint NOT NULL,
                       PRIMARY KEY (sch, tbl));
    CREATE TABLE #viewcount (sch sysname NOT NULL, tbl sysname NOT NULL, rowcnt bigint NULL,
                             PRIMARY KEY (sch, tbl));
    CREATE TABLE #consumer (sch sysname NOT NULL PRIMARY KEY);

    INSERT #consumer (sch)
    SELECT DISTINCT LTRIM(RTRIM(value)) FROM STRING_SPLIT(@ConsumerSchemas, ',')
    WHERE LTRIM(RTRIM(value)) <> '';

    /*  Counts come from sys.dm_db_partition_stats on both sides: the heap or clustered-index
        row count, which is the number COUNT(*) returns for a table that is not being written
        at that moment, and costs nothing to read, so every table is covered in one pass.
        The remote side is reached through OPENQUERY, which needs a literal, hence dynamic SQL. */
    DECLARE @remote nvarchar(max) = N'
        SELECT s.name AS sch, t.name AS tbl, SUM(ps.row_count) AS rowcnt
        FROM sys.tables t
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        JOIN sys.dm_db_partition_stats ps
          ON ps.object_id = t.object_id AND ps.index_id IN (0, 1)
        WHERE t.is_ms_shipped = 0
        GROUP BY s.name, t.name';

    DECLARE @sql nvarchar(max) =
        N'INSERT #old (sch, tbl, rowcnt) SELECT sch, tbl, rowcnt FROM OPENQUERY('
        + QUOTENAME(@LinkedServer) + N', ''' + REPLACE(@remote, '''', '''''') + N''');';
    EXEC sp_executesql @sql;

    INSERT #new (sch, tbl, rowcnt)
    SELECT s.name, t.name, SUM(ps.row_count)
    FROM sys.tables t
    JOIN sys.schemas s ON s.schema_id = t.schema_id
    JOIN sys.dm_db_partition_stats ps ON ps.object_id = t.object_id AND ps.index_id IN (0, 1)
    WHERE t.is_ms_shipped = 0
    GROUP BY s.name, t.name;

    /*  An old table with no same-named new table can still be served to consumers by a
        compatibility view over a renamed physical table. Count those views, so the comparison
        follows what downstream actually reads. A view has no stored row count, so this is the
        one place the sweep issues a real COUNT(*). */
    DECLARE @sch sysname, @tbl sysname;
    DECLARE compat CURSOR LOCAL FAST_FORWARD FOR
        SELECT s.name, v.name
        FROM sys.views v
        JOIN sys.schemas s ON s.schema_id = v.schema_id
        WHERE EXISTS (SELECT 1 FROM #old o WHERE o.sch = s.name AND o.tbl = v.name)
          AND NOT EXISTS (SELECT 1 FROM #new n WHERE n.sch = s.name AND n.tbl = v.name);
    OPEN compat;
    FETCH NEXT FROM compat INTO @sch, @tbl;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        -- A compatibility view fails to bind while its underlying table is missing. Record it
        -- as unreadable rather than aborting the sweep over one broken view.
        BEGIN TRY
            SET @sql = N'INSERT #viewcount (sch, tbl, rowcnt) SELECT @s, @t, COUNT_BIG(*) FROM '
                     + QUOTENAME(@sch) + N'.' + QUOTENAME(@tbl) + N' WITH (NOLOCK);';
            EXEC sp_executesql @sql, N'@s sysname, @t sysname', @sch, @tbl;
        END TRY
        BEGIN CATCH
            INSERT #viewcount (sch, tbl, rowcnt) VALUES (@sch, @tbl, NULL);
        END CATCH
        FETCH NEXT FROM compat INTO @sch, @tbl;
    END
    CLOSE compat;
    DEALLOCATE compat;

    /*  The physical table behind a counted compatibility view holds the SAME rows as the view,
        under the V3 name. Left alone it shows up a second time as an unmatched new-side table,
        and its rows would be counted twice in every total. */
    SELECT DISTINCT
           OBJECT_SCHEMA_NAME(d.referenced_id) AS sch,
           OBJECT_NAME(d.referenced_id)        AS tbl
    INTO #backing
    FROM sys.sql_expression_dependencies d
    JOIN #viewcount v
      ON v.sch = OBJECT_SCHEMA_NAME(d.referencing_id)
     AND v.tbl = OBJECT_NAME(d.referencing_id)
    WHERE d.referenced_id IS NOT NULL;

    SELECT
        COALESCE(o.sch, n.sch, v.sch) AS SchemaName,
        COALESCE(o.tbl, n.tbl, v.tbl) AS TableName,
        o.rowcnt                      AS OldRows,
        COALESCE(n.rowcnt, v.rowcnt)  AS NewRows,
        CASE WHEN n.tbl IS NOT NULL THEN 'table'
             WHEN v.tbl IS NOT NULL THEN 'view' END AS NewObjectType
    INTO #cmp
    FROM #old o
    FULL OUTER JOIN #new n ON n.sch = o.sch AND n.tbl = o.tbl
    LEFT JOIN #viewcount v ON v.sch = o.sch AND v.tbl = o.tbl;

    SELECT
        c.SchemaName,
        c.TableName,
        c.OldRows,
        c.NewRows,
        c.NewRows - c.OldRows AS Delta,
        CAST(CASE WHEN ISNULL(c.OldRows, 0) = 0 THEN NULL
                  ELSE 100.0 * (c.NewRows - c.OldRows) / c.OldRows END AS decimal(10, 2)) AS DeltaPct,
        c.NewObjectType,
        CASE
            WHEN x.SchemaName IS NOT NULL                                 THEN 'retired'
            WHEN c.OldRows IS NULL AND b.tbl IS NOT NULL                  THEN 'backing'
            WHEN EXISTS (SELECT 1 FROM #consumer s WHERE s.sch = c.SchemaName) THEN 'consumer'
            ELSE 'internal'
        END AS Tier,
        CASE
            WHEN c.OldRows IS NULL                     THEN 'ONLY_IN_NEW'
            WHEN c.NewRows IS NULL                     THEN 'MISSING_IN_NEW'
            WHEN c.OldRows = c.NewRows                 THEN 'MATCH'
            WHEN c.NewRows = 0 AND c.OldRows > 0       THEN 'EMPTY_IN_NEW'
            WHEN c.NewRows > c.OldRows                 THEN 'NEW_HAS_MORE'
            ELSE                                            'NEW_HAS_FEWER'
        END AS Status,
        x.Reason AS ExcludedBecause
    INTO #result
    FROM #cmp c
    LEFT JOIN #backing b ON b.sch = c.SchemaName AND b.tbl = c.TableName
    OUTER APPLY (SELECT TOP (1) e.SchemaName, e.Reason
                 FROM mig.RowCountExclusion e
                 WHERE e.SchemaName = c.SchemaName AND c.TableName LIKE e.NamePattern
                 ORDER BY e.NamePattern) AS x;

    -- Result set 1: the totals. Net alone understates the deviation, because a table that
    -- gained rows cancels one that lost them, so the gross figures are reported beside it.
    SELECT
        COUNT(*)                                                     AS Tables,
        SUM(CASE WHEN Status = 'MATCH' THEN 1 ELSE 0 END)            AS Matching,
        SUM(CASE WHEN Status <> 'MATCH' THEN 1 ELSE 0 END)           AS Differing,
        SUM(CASE WHEN Status = 'MISSING_IN_NEW' THEN 1 ELSE 0 END)   AS MissingInNew,
        SUM(ISNULL(OldRows, 0))                                      AS OldRows,
        SUM(ISNULL(NewRows, 0))                                      AS NewRows,
        SUM(ISNULL(NewRows, 0) - ISNULL(OldRows, 0))                 AS NetDelta,
        SUM(CASE WHEN ISNULL(NewRows, 0) > ISNULL(OldRows, 0)
                 THEN ISNULL(NewRows, 0) - ISNULL(OldRows, 0) ELSE 0 END) AS RowsAdded,
        SUM(CASE WHEN ISNULL(OldRows, 0) > ISNULL(NewRows, 0)
                 THEN ISNULL(OldRows, 0) - ISNULL(NewRows, 0) ELSE 0 END) AS RowsMissing,
        CAST(CASE WHEN SUM(ISNULL(OldRows, 0)) = 0 THEN NULL
                  ELSE 100.0 * SUM(CASE WHEN ISNULL(OldRows, 0) > ISNULL(NewRows, 0)
                                        THEN ISNULL(OldRows, 0) - ISNULL(NewRows, 0) ELSE 0 END)
                       / SUM(ISNULL(OldRows, 0)) END AS decimal(10, 4))   AS RowsMissingPct
    FROM #result
    WHERE Tier = 'consumer';

    -- Result set 2: the detail, worst first.
    SELECT SchemaName, TableName, OldRows, NewRows, Delta, DeltaPct,
           NewObjectType, Tier, Status, ExcludedBecause
    FROM #result
    WHERE (Tier = 'consumer'
           OR (Tier = 'retired'  AND @IncludeRetired  = 1)
           OR (Tier = 'backing'  AND @IncludeBacking  = 1)
           OR (Tier = 'internal' AND @IncludeInternal = 1))
      AND (@OnlyDifferences = 0 OR Status <> 'MATCH')
      AND (@SchemaName IS NULL OR SchemaName = @SchemaName)
      AND (@NameLike IS NULL OR TableName LIKE @NameLike)
    ORDER BY
        CASE Tier WHEN 'consumer' THEN 0 WHEN 'retired' THEN 1 WHEN 'backing' THEN 2 ELSE 3 END,
        CASE Status
            WHEN 'MISSING_IN_NEW' THEN 1
            WHEN 'EMPTY_IN_NEW'   THEN 2
            WHEN 'NEW_HAS_FEWER'  THEN 3
            WHEN 'NEW_HAS_MORE'   THEN 4
            WHEN 'ONLY_IN_NEW'    THEN 5
            ELSE 6
        END,
        ABS(ISNULL(NewRows, 0) - ISNULL(OldRows, 0)) DESC,
        SchemaName, TableName;
END
GO
