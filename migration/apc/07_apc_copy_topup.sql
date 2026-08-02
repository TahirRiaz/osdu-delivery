/*
    Catch-up for drift on the live source.

    OLDPROD is still in production, so it keeps receiving rows while the migration runs. Chunk
    ranges end at the MaxKey captured during Prepare, so anything inserted afterwards was never
    in scope. Verified on this run: the shortfall on all eight affected tables equalled exactly
    the rows above MaxKey, with nothing unexplained.

    This adds chunks covering ONLY the new range (old MaxKey, new remote max] and appends them
    after the existing chunk numbers. It is purely additive: those keys have never been copied,
    so no existing chunk is touched, renumbered or re-scoped.

    Do NOT use mig.usp_ApcCopyPrepare to catch up. Prepare recomputes the chunk WIDTH from the
    new maximum, so the already-copied chunk numbers would then describe different key ranges
    than the ones they actually copied, and the plan would no longer be a faithful record of
    what was done.

    Run it, then run workers as usual:

        EXEC mig.usp_ApcCopyTopUp @TablePattern = N'APC%';
        EXEC mig.usp_ApcCopyRun   @WorkerId = 1;
        EXEC mig.usp_ApcCopyVerify @TablePattern = N'APC%';

    Expect a residual difference on any table still being written to: the source moves on while
    the catch-up runs. Only a quiesced source can reconcile to zero and stay there.
*/

SET NOCOUNT ON;
GO

CREATE OR ALTER PROCEDURE mig.usp_ApcCopyTopUp
    @TablePattern    sysname = N'APC%',
    @TargetChunkRows bigint  = 2000000,
    @Debug           bit     = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @TargetChunkRows < 1000
        THROW 50040, 'ApcCopyTopUp: @TargetChunkRows must be at least 1000.', 1;

    DECLARE @tbl sysname, @key sysname, @oldMax bigint, @newMax bigint, @newRows bigint,
            @sql nvarchar(max), @remote nvarchar(max), @chunks bigint, @width bigint,
            @lastChunk int, @msg nvarchar(500), @added int = 0;

    CREATE TABLE #added
    (
        TableName  sysname NOT NULL PRIMARY KEY,
        OldMaxKey  bigint  NOT NULL,
        NewMaxKey  bigint  NOT NULL,
        NewRows    bigint  NOT NULL,
        ChunksAdded int    NOT NULL
    );

    DECLARE c CURSOR LOCAL FAST_FORWARD FOR
        SELECT TableName, KeyColumn, MaxKey
        FROM mig.ApcCopyTable
        WHERE TableName LIKE @TablePattern
          AND KeyColumn IS NOT NULL
          AND MaxKey IS NOT NULL
        ORDER BY Priority;

    OPEN c;
    FETCH NEXT FROM c INTO @tbl, @key, @oldMax;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @newMax = NULL;
        SET @newRows = NULL;

        SET @remote = N'SELECT MAX(' + QUOTENAME(@key) + N') AS mx, SUM(CASE WHEN ' + QUOTENAME(@key)
                    + N' > ' + CAST(@oldMax AS nvarchar(20)) + N' THEN 1 ELSE 0 END) AS nw FROM arc.'
                    + QUOTENAME(@tbl);
        SET @sql = N'SELECT @mx = mx, @nw = nw FROM OPENQUERY(OLDPROD, '''
                 + REPLACE(@remote, '''', '''''') + N''');';
        IF @Debug = 1 PRINT @sql;
        EXEC sys.sp_executesql @sql, N'@mx bigint OUTPUT, @nw bigint OUTPUT',
             @mx = @newMax OUTPUT, @nw = @newRows OUTPUT;

        IF @newMax IS NOT NULL AND @newMax > @oldMax AND ISNULL(@newRows, 0) > 0
        BEGIN
            SELECT @lastChunk = ISNULL(MAX(ChunkNo), 0) FROM mig.ApcCopyChunk WHERE TableName = @tbl;

            SET @chunks = CASE WHEN @newRows <= @TargetChunkRows THEN 1
                               ELSE CAST(CEILING(@newRows * 1.0 / @TargetChunkRows) AS bigint) END;
            SET @width  = CAST(CEILING((@newMax - @oldMax) * 1.0 / @chunks) AS bigint);
            IF @width < 1 SET @width = 1;

            ;WITH n AS
            (
                SELECT TOP (CAST(@chunks AS int))
                       Seq = ROW_NUMBER() OVER (ORDER BY (SELECT NULL))
                FROM sys.all_columns a CROSS JOIN sys.all_columns b
            )
            INSERT INTO mig.ApcCopyChunk (TableName, ChunkNo, LoKey, HiKey)
            SELECT @tbl,
                   @lastChunk + n.Seq,
                   @oldMax + 1 + (n.Seq - 1) * @width,
                   CASE WHEN n.Seq = @chunks THEN @newMax
                        ELSE @oldMax + n.Seq * @width END
            FROM n;

            SET @added = @@ROWCOUNT;

            /* Reopen the table so workers pick the new chunks up, and move the watermark. */
            UPDATE mig.ApcCopyTable
            SET MaxKey = @newMax,
                Status = 'Ready',
                Message = N'Topped up for source drift above the previous watermark.'
            WHERE TableName = @tbl;

            INSERT INTO #added VALUES (@tbl, @oldMax, @newMax, @newRows, @added);

            SET @msg = REPLACE(CONCAT(N'TopUp: ', @tbl, N' +', @newRows, N' row(s) above key ', @oldMax,
                                      N', ', @added, N' chunk(s) added.'), N'%', N'%%');
            RAISERROR (@msg, 0, 1) WITH NOWAIT;
        END;

        FETCH NEXT FROM c INTO @tbl, @key, @oldMax;
    END;

    CLOSE c;
    DEALLOCATE c;

    IF NOT EXISTS (SELECT 1 FROM #added)
        RAISERROR (N'TopUp: no drift found, every table is current.', 0, 1) WITH NOWAIT;

    SELECT * FROM #added ORDER BY NewRows DESC;
    DROP TABLE #added;
END;
GO
