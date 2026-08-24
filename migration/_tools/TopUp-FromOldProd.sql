-- Cross-estate top-up: recover rows old production holds that the new estate does not.
-- Runs ON the new estate (dw-mi-sql-prod), reaching old production through the OLDPROD linked server.
--
-- READ THE DRY RUN FIRST. IT IS NOT OPTIONAL.
--
-- The schemas match between the estates (the conversions reconcile column-for-column), so it is tempting to
-- treat "old count > new count" as a shortfall and copy the difference across. That is wrong often enough to
-- be dangerous, because the count and the merge key do not measure the same thing:
--
--   * Several merge keys contain PROVENANCE columns that the two engines compute differently. The legacy
--     FileLineNumber_DW is not V3's, and a key built on file-derived values (arc.stavanger_parkering keys on
--     Dato, Klokkeslett, Sted, FileLineNumber_DW) will not align at all. Every old row then looks missing.
--     Measured 2026-08-25: a 55,305-row count shortfall, but the key anti-join wanted to insert 254,196 rows,
--     which would have nearly doubled the table with rows it already had.
--   * The reverse also happens. arc.Baatbooking_Sess was 37 rows short on count and the anti-join found
--     NOTHING to insert: old production simply holds 37 duplicate rows of keys the new estate already has.
--
-- So the only safe signal is the DRY RUN below agreeing with the count shortfall. Where the two numbers match
-- exactly, the key means the same thing in both estates and the top-up is sound. Where they disagree, STOP and
-- diagnose that table by hand; do not copy.
--
-- Verified safe on 2026-08-25 for arc.Billettkontroll (1,129), arc.GTFS_Entur_Trips (831),
-- edw.APC_PassengerPerStopPerYear_rogfk (36) and arc.GTFS_Entur_Stops (10); all four then reconciled to zero
-- with no duplicate merge keys.
--
-- The surrogate PK is regenerated rather than carried across: the consumer contract is NCI_KeyColumn, and
-- an identity value only has meaning inside its own estate. Every insert is an anti-join on that key, so a
-- second run moves nothing.
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @DryRun bit = 1;   -- 1 = report only. Set to 0 ONLY for tables whose dry run matched the shortfall.

DECLARE @targets TABLE (sch sysname, tb sysname);
INSERT @targets (sch, tb) VALUES
    ('arc','Billettkontroll'),
    ('arc','GTFS_Entur_Trips'),
    ('edw','APC_PassengerPerStopPerYear_rogfk'),
    ('arc','GTFS_Entur_Stops');

DECLARE @sch sysname, @tb sysname, @full nvarchar(300),
        @cols nvarchar(max), @keys nvarchar(max), @onc nvarchar(max),
        @remote nvarchar(max), @sql nvarchar(max), @m varchar(300);

DECLARE cur CURSOR LOCAL FAST_FORWARD FOR SELECT sch, tb FROM @targets;
OPEN cur;
FETCH NEXT FROM cur INTO @sch, @tb;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @full = QUOTENAME(@sch) + '.' + QUOTENAME(@tb);

    SELECT @cols = STUFF((SELECT ',' + QUOTENAME(c.name) FROM sys.columns c
        WHERE c.object_id = OBJECT_ID(@full) AND c.is_identity = 0 AND c.is_computed = 0
        ORDER BY c.column_id FOR XML PATH('')), 1, 1, '');

    SELECT @keys = STUFF((SELECT ',' + QUOTENAME(c.name) FROM sys.index_columns ic
        JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
        WHERE ic.object_id = OBJECT_ID(@full) AND i.name = 'NCI_KeyColumn' AND ic.is_included_column = 0
        ORDER BY ic.key_ordinal FOR XML PATH('')), 1, 1, ''),
      @onc = STUFF((SELECT ' AND t.' + QUOTENAME(c.name) + '=o.' + QUOTENAME(c.name) FROM sys.index_columns ic
        JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
        WHERE ic.object_id = OBJECT_ID(@full) AND i.name = 'NCI_KeyColumn' AND ic.is_included_column = 0
        FOR XML PATH('')), 1, 5, '');

    IF @keys IS NULL
    BEGIN
        -- No NCI_KeyColumn means no business merge key to anti-join on, and the surrogate PK is not
        -- comparable across estates. Copying on the surrogate would duplicate business rows.
        SET @m = '  ' + @sch + '.' + @tb + ': SKIPPED, no NCI_KeyColumn (needs the flow''s keyColumns by hand)';
        RAISERROR(@m, 0, 1) WITH NOWAIT;
    END
    ELSE IF @DryRun = 1
    BEGIN
        SET @remote = N'SELECT ' + @keys + N' FROM ' + @full;
        SET @sql = N'SELECT @n = COUNT(*) FROM OPENQUERY(OLDPROD,''' + REPLACE(@remote, '''', '''''') + N''') o
            WHERE NOT EXISTS (SELECT 1 FROM ' + @full + N' t WHERE ' + @onc + N');';
        DECLARE @n bigint;
        EXEC sp_executesql @sql, N'@n bigint OUTPUT', @n = @n OUTPUT;
        SET @m = '  ' + @sch + '.' + @tb + ': would insert ' + CAST(@n AS varchar(20))
               + ' (compare against the count shortfall; they MUST match)';
        RAISERROR(@m, 0, 1) WITH NOWAIT;
    END
    ELSE
    BEGIN
        SET @remote = N'SELECT ' + @cols + N' FROM ' + @full;
        SET @sql = N'INSERT INTO ' + @full + N' (' + @cols + N') SELECT ' + @cols + N'
            FROM OPENQUERY(OLDPROD,''' + REPLACE(@remote, '''', '''''') + N''') o
            WHERE NOT EXISTS (SELECT 1 FROM ' + @full + N' t WHERE ' + @onc + N');';
        EXEC sp_executesql @sql;
        SET @m = '  ' + @sch + '.' + @tb + ': inserted ' + CAST(@@ROWCOUNT AS varchar(20));
        RAISERROR(@m, 0, 1) WITH NOWAIT;
    END

    FETCH NEXT FROM cur INTO @sch, @tb;
END
CLOSE cur;
DEALLOCATE cur;
