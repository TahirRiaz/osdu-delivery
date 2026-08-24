-- Row-count comparison, old production DWH vs new production DWH.
-- Runs on the NEW server (dw-mi-sql-prod, database dw-dwh-prod) and reaches the old
-- production DWH through the OLDPROD linked server, so one query covers both estates.
--
-- Counts come from sys.dm_db_partition_stats (heap/clustered rows), which is the same
-- number SELECT COUNT(*) returns for a settled table and costs nothing to read, so the
-- sweep covers every table in both databases at once.
--
-- New-side objects are matched by name as a TABLE first and, failing that, as a VIEW:
-- a ported source whose physical table was renamed keeps a compatibility view under the
-- old production name, and that view is what downstream consumers actually read. Views
-- are counted with COUNT(*) because a view has no stored row count.
--
-- Read-only. Nothing here writes to either estate.
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

IF OBJECT_ID('tempdb..#old') IS NOT NULL DROP TABLE #old;
IF OBJECT_ID('tempdb..#new') IS NOT NULL DROP TABLE #new;
IF OBJECT_ID('tempdb..#viewcount') IS NOT NULL DROP TABLE #viewcount;
IF OBJECT_ID('tempdb..#backing') IS NOT NULL DROP TABLE #backing;
IF OBJECT_ID('tempdb..#retired') IS NOT NULL DROP TABLE #retired;

-- Objects deliberately NOT carried forward to the new estate. They are still listed, under
-- their own tier, so the decision stays visible and reversible, but they are kept out of the
-- headline figures: counting a table nobody intends to rebuild as a shortfall misstates the
-- migration. Add a row here (schema + LIKE pattern) when a table is retired by decision.
CREATE TABLE #retired (sch sysname, pattern nvarchar(200), reason nvarchar(200));
INSERT #retired (sch, pattern, reason) VALUES
    ('arc', 'TTDB[_]%',                   'Trapeze TTDB is not carried forward'),
    ('arc', 'Fara_Statload_DupeActual',   'Temp table, never a consumer contract'),
    ('edw', 'APC_MatchedTrip',     'Dropped in the APCEDW conversion'),
    ('edw', 'APC_PassengersPerLine',  'Not carried forward'),
    ('edw', 'APC_PassangersPerDay',   'Not carried forward'),
    ('edw', 'APC_PassengersPerStop',  'Not carried forward');

SELECT sch, tbl, rowcnt
INTO #old
FROM OPENQUERY(OLDPROD, '
    SELECT s.name AS sch, t.name AS tbl, SUM(ps.row_count) AS rowcnt
    FROM sys.tables t
    JOIN sys.schemas s ON s.schema_id = t.schema_id
    JOIN sys.dm_db_partition_stats ps ON ps.object_id = t.object_id AND ps.index_id IN (0,1)
    WHERE t.is_ms_shipped = 0
    GROUP BY s.name, t.name');

SELECT s.name AS sch, t.name AS tbl, SUM(ps.row_count) AS rowcnt
INTO #new
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
JOIN sys.dm_db_partition_stats ps ON ps.object_id = t.object_id AND ps.index_id IN (0,1)
WHERE t.is_ms_shipped = 0
GROUP BY s.name, t.name;

-- Old-side tables with no same-named new table, but a new VIEW under that name: the
-- compatibility-view case. Count those views so the comparison follows what consumers see.
CREATE TABLE #viewcount (sch sysname, tbl sysname, rowcnt bigint);

DECLARE @sch sysname, @tbl sysname, @sql nvarchar(max);
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
    -- A compatibility view can fail to bind if its underlying table has not been created
    -- yet. Record it as unreadable rather than aborting the whole sweep.
    BEGIN TRY
        SET @sql = N'INSERT #viewcount (sch, tbl, rowcnt) SELECT @s, @t, COUNT_BIG(*) FROM '
                 + QUOTENAME(@sch) + N'.' + QUOTENAME(@tbl) + N' WITH (NOLOCK)';
        EXEC sp_executesql @sql, N'@s sysname, @t sysname', @sch, @tbl;
    END TRY
    BEGIN CATCH
        INSERT #viewcount (sch, tbl, rowcnt) VALUES (@sch, @tbl, NULL);
    END CATCH
    FETCH NEXT FROM compat INTO @sch, @tbl;
END
CLOSE compat;
DEALLOCATE compat;

-- The physical table behind a counted compatibility view holds the SAME rows as the view,
-- under the V3 name. Left alone it appears a second time as an unmatched new-side table, so
-- its rows would be counted twice in any total. Mark those tables so the headline figures
-- count each row once; they stay in the output under their own tier.
SELECT DISTINCT
       OBJECT_SCHEMA_NAME(d.referenced_id) AS sch,
       OBJECT_NAME(d.referenced_id)        AS tbl
INTO #backing
FROM sys.sql_expression_dependencies d
JOIN #viewcount v
  ON v.sch = OBJECT_SCHEMA_NAME(d.referencing_id)
 AND v.tbl = OBJECT_NAME(d.referencing_id)
WHERE d.referenced_id IS NOT NULL;

WITH cmp AS (
    SELECT
        COALESCE(o.sch, n.sch, v.sch)  AS SchemaName,
        COALESCE(o.tbl, n.tbl, v.tbl)  AS TableName,
        o.rowcnt                          AS OldRows,
        COALESCE(n.rowcnt, v.rowcnt)        AS NewRows,
        CASE WHEN n.tbl IS NOT NULL THEN 'table'
             WHEN v.tbl IS NOT NULL THEN 'view'  END AS NewObjectType
    FROM #old o
    FULL OUTER JOIN #new n ON n.sch = o.sch AND n.tbl = o.tbl
    LEFT JOIN #viewcount v ON v.sch = o.sch AND v.tbl = o.tbl
)
SELECT
    SchemaName,
    TableName,
    OldRows,
    NewRows,
    NewRows - OldRows AS Delta,
    CAST(CASE WHEN ISNULL(OldRows, 0) = 0 THEN NULL
              ELSE 100.0 * (NewRows - OldRows) / OldRows END AS decimal(10, 2)) AS DeltaPct,
    NewObjectType,
    CASE
        WHEN EXISTS (SELECT 1 FROM #retired x
                      WHERE x.sch = SchemaName AND TableName LIKE x.pattern) THEN 'retired'
        WHEN OldRows IS NULL AND EXISTS (SELECT 1 FROM #backing b
                                          WHERE b.sch = SchemaName AND b.tbl = TableName) THEN 'backing'
        WHEN SchemaName IN ('arc', 'edw', 'skey', 'man', 'sta', 'ver') THEN 'consumer'
        ELSE 'internal'
    END AS Tier,
    CASE
        WHEN OldRows IS NULL                       THEN 'ONLY_IN_NEW'
        WHEN NewRows IS NULL                       THEN 'MISSING_IN_NEW'
        WHEN OldRows = NewRows                     THEN 'MATCH'
        WHEN NewRows = 0 AND OldRows > 0           THEN 'EMPTY_IN_NEW'
        WHEN NewRows > OldRows                     THEN 'NEW_HAS_MORE'
        ELSE                                            'NEW_HAS_FEWER'
    END AS Status
FROM cmp
ORDER BY
    -- Consumer-facing schemas first: 'raw' is the old engine's own per-flow staging
    -- (<table>_<flowid>), 'tmp' is scratch, 'mig' is V3 migration bookkeeping, and none of
    -- them are read by anything downstream, so a difference there is not a finding.
    CASE WHEN SchemaName IN ('arc', 'edw', 'skey', 'man', 'sta', 'ver') THEN 0 ELSE 1 END,
    CASE
        WHEN NewRows IS NULL              THEN 1
        WHEN NewRows = 0 AND OldRows > 0  THEN 2
        WHEN OldRows > NewRows            THEN 3
        WHEN OldRows < NewRows            THEN 4
        WHEN OldRows IS NULL              THEN 5
        ELSE                                   6
    END,
    ABS(ISNULL(NewRows, 0) - ISNULL(OldRows, 0)) DESC,
    SchemaName, TableName;
