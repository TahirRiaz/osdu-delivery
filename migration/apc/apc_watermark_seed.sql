/*
    APC watermark seed: step each arc table's high-water mark past the migration copy instant.

    WHY THIS EXISTS
    ---------------
    arc holds the full APC history already: it was migrated table-to-table from old production over the OLDPROD
    linked server, 2,040,454,657 rows, reconciled exactly. The lake files were moved separately, with a
    server-side azcopy, and that is where the problem comes from: a server-side copy stamps every destination
    blob with the COPY instant, not the original write time. All 540,778 historical files therefore carry
    2026-08-02 07:05 as their last-modified time.

    A file flow's incremental watermark is MAX(FileDate_DW) on the durable table downstream (arc), compared
    against each file's modified time. arc's genuine maximum is 20260801092317 (2026-08-01 09:23), which is
    EARLIER than the copy instant, so every one of those 540,778 files looks new and the whole history is
    replayed on every run. That is not a watermark defect: the watermark is correct, the file timestamps are
    the thing that lost their meaning.

    Advancing the watermark past the copy instant makes the already-migrated files fall below it and be skipped,
    while anything the copy flows bring in LATER still lands above it and loads normally. One row per table is
    enough, because the probe reads a single MAX over the whole table.

    WHY NOT DATE THE FILES BY NAME INSTEAD
    --------------------------------------
    The engine can read a file's business date from its name (fileDate.from: name), which would also step over
    the copied history. It was deliberately NOT used here: it would make a LATE file invisible. A correction for
    an old operating day, redelivered weeks later, has a filename date below the watermark and would never load.
    Producers in this estate do re-touch old files. Keeping modified-time semantics and nudging the mark once is
    the smaller, more honest change.

    WHAT THIS TOUCHES
    -----------------
    Exactly ONE row per arc table: the row that already holds the maximum FileDate_DW. Its FileDate_DW moves
    from 2026-08-01 09:23 to just after the copy finished. No other column, no other row, no other table. The
    cost is that one row's file-date provenance is a migration artifact rather than its true source stamp, which
    is why it is recorded here rather than done by hand.

    The arc tables are SHARED by all five operators (rows are discriminated by SourceSystemID) and the probe
    takes one MAX over the whole table, so a single seeded row serves every region. Seeding per region would
    change five rows to achieve exactly the same maximum.

    Re-runnable: a table already at or above the seed value is left alone.
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;

-- Just after the azcopy completed (2026-08-02 07:05, the newest copied blob was 07:05:30). Anything the copy
-- flows land from here on carries a later stamp and stays loadable.
DECLARE @SeedFileDate decimal(14, 0) = 20260802071000;

DECLARE @results TABLE
(
    TableName   sysname NOT NULL,
    OldMax      decimal(14, 0) NULL,
    NewMax      decimal(14, 0) NULL,
    RowsTouched int NOT NULL
);

DECLARE @tables TABLE (Name sysname NOT NULL PRIMARY KEY);
INSERT INTO @tables (Name) VALUES
    ('APC_AssignedBlocks'), ('APC_Calls'), ('APC_Line'), ('APC_PassengerCount'),
    ('APC_PassengerInOut'), ('APC_PlannedBlocks'), ('APC_PlannedJourneys'), ('APC_StopPoint');

DECLARE @tbl sysname, @sql nvarchar(max), @oldMax decimal(14, 0), @touched int;

DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT Name FROM @tables ORDER BY Name;
OPEN c;
FETCH NEXT FROM c INTO @tbl;

WHILE @@FETCH_STATUS = 0
BEGIN
    IF OBJECT_ID(N'arc.' + QUOTENAME(@tbl), 'U') IS NULL
    BEGIN
        INSERT INTO @results VALUES (@tbl, NULL, NULL, 0);
        FETCH NEXT FROM c INTO @tbl;
        CONTINUE;
    END;

    SET @oldMax = NULL;
    SET @sql = N'SELECT @m = MAX(FileDate_DW) FROM arc.' + QUOTENAME(@tbl) + N' WITH (NOLOCK);';
    EXEC sys.sp_executesql @sql, N'@m decimal(14,0) OUTPUT', @m = @oldMax OUTPUT;

    SET @touched = 0;

    IF @oldMax IS NOT NULL AND @oldMax < @SeedFileDate
    BEGIN
        -- Move the single row that currently holds the maximum. TOP (1) with the equality predicate keeps this
        -- to exactly one row even when several rows share that maximum.
        SET @sql = N'UPDATE TOP (1) arc.' + QUOTENAME(@tbl)
                 + N' SET FileDate_DW = @seed WHERE FileDate_DW = @old;';
        EXEC sys.sp_executesql @sql,
             N'@seed decimal(14,0), @old decimal(14,0)', @seed = @SeedFileDate, @old = @oldMax;
        SET @touched = @@ROWCOUNT;
    END;

    INSERT INTO @results
    SELECT @tbl, @oldMax, CASE WHEN @touched > 0 THEN @SeedFileDate ELSE @oldMax END, @touched;

    FETCH NEXT FROM c INTO @tbl;
END;

CLOSE c;
DEALLOCATE c;

SELECT TableName, OldMax, NewMax, RowsTouched,
       Verdict = CASE
                    WHEN OldMax IS NULL AND RowsTouched = 0 THEN 'table absent or empty'
                    WHEN RowsTouched = 0 THEN 'already at or above the seed; untouched'
                    ELSE 'watermark advanced past the copy instant'
                 END
FROM @results
ORDER BY TableName;
