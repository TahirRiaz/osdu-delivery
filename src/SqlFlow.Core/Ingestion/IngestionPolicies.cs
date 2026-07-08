namespace SqlFlow.Core.Ingestion;

/// <summary>
/// How rows are applied to the target (legacy KeyColumns and the skip/batch family). There is deliberately
/// no merge-mode enum: the legacy engine stages the source then applies an explicit UPDATE then INSERT
/// (never a T-SQL MERGE), and the effective behavior is derived from these columns plus the incremental and
/// truncate settings, exactly as legacy does.
/// </summary>
public sealed record IngestionLoadPolicy
{
    /// <summary>Business keys that identify a row for the update/insert match (legacy KeyColumns).</summary>
    public IReadOnlyList<string> KeyColumns { get; init; } = [];

    /// <summary>Omit the UPDATE branch: insert-only (legacy SkipUpdateExsisting).</summary>
    public bool SkipUpdateExisting { get; init; }

    /// <summary>Omit the INSERT branch: update-only (legacy SkipInsertNew).</summary>
    public bool SkipInsertNew { get; init; }

    /// <summary>Confirm the keys exist on both sides before applying changes (legacy MatchKeysInSrcTrg).</summary>
    public bool MatchKeysInSourceAndTarget { get; init; }

    /// <summary>Batch the upsert to avoid lock escalation (legacy UseBatchUpsertToAvoideLockEscalation).</summary>
    public bool BatchUpsertToAvoidLockEscalation { get; init; }

    /// <summary>Rows per upsert batch when batching (legacy BatchUpsertRowCount, default 2000).</summary>
    public int BatchUpsertRowCount { get; init; } = 2000;

    /// <summary>When set, staging is applied to the target one dataset at a time, partitioned by this column's
    /// distinct values in ascending order, each dataset's UPDATE-then-INSERT running against the target as the
    /// prior datasets left it (legacy DataSetColumn). This preserves file/partition load order in a shared
    /// staging area: a business key that recurs across datasets collapses to the last dataset that carries it,
    /// which a single set-based upsert cannot express. Rows are deduplicated to one row per (dataset, key)
    /// before the loop. Honors <see cref="BatchUpsertToAvoidLockEscalation"/> within each dataset; not
    /// combinable with SCD2 versioning. Null (the default) applies the plain set-based upsert.</summary>
    public string? DataSetColumn { get; init; }

    /// <summary>
    /// Per-file (per-dataset) full replace: the column that identifies a source file/dataset, typically
    /// <c>FileName_DW</c> (set the pre flow's <c>showPathWithFileName</c> so it carries the full path, the safe
    /// identity that never collides across folders). When set, the apply becomes a purge-then-insert scoped to
    /// the incoming batch: before inserting, the engine deletes every target row whose <see cref="ReloadColumn"/>
    /// value is present in the staged batch (a set-based <c>DELETE ... WHERE EXISTS</c>, NULL-safe so rows with no
    /// file identity are never touched), then inserts the staged rows. A resent file therefore fully replaces its
    /// prior version - including records the new version dropped, which a keyed upsert would leave orphaned -
    /// while files absent from this run's batch are untouched. It supersedes the keyed upsert (there is nothing to
    /// update after the purge); when <see cref="KeyColumns"/> are also declared, the insert collapses the batch to
    /// one row per key so the target's unique key is not violated. Not combinable with
    /// <see cref="DataSetColumn"/> (the ordered upsert loop), SCD2 versioning, the match-key delete pass, or
    /// <c>target.truncateBeforeLoad</c>. Null (the default) applies the normal upsert. Designed for the chained
    /// file landing pattern where the ods flow reads <c>[pre].[v&lt;Table&gt;]</c> and the provenance columns ride
    /// through the view onto the target.
    /// </summary>
    public string? ReloadColumn { get; init; }

    /// <summary>Stream rows from source to target (legacy StreamData, default true). When false the engine
    /// buffers in memory and writes on <see cref="Threads"/> parallel writers.</summary>
    public bool StreamData { get; init; } = true;

    /// <summary>Parallel reader/writer count (legacy NoOfThreads). Null means the engine default; the legacy
    /// engine treats 0 the same way.</summary>
    public int? Threads { get; init; }

    /// <summary>Truncate the staging (pre) table after a successful run instead of leaving its rows
    /// (legacy TruncatePreTableOnCompletion). Only applies when <see cref="KeepStagingTable"/> is set: a kept
    /// table is emptied so it carries structure without the run's data. When the staging table is not kept (the
    /// default) it is dropped outright, which already discards the data, so this flag has no effect there.</summary>
    public bool TruncatePreTableOnCompletion { get; init; }

    /// <summary>Keep the run-scoped staging table after a SUCCESSFUL run (default false: drop on success). A
    /// FAILED run always keeps its staging table for debugging, regardless of this flag.</summary>
    public bool KeepStagingTable { get; init; }

    /// <summary>
    /// After a SUCCESSFUL load, truncate the upstream landing ("pre") table that feeds this flow's source, but
    /// only once the target has caught up: the truncate fires only when MAX(the incremental watermark) in the
    /// target is greater than or equal to MAX in the landing table, proving every landed row has been
    /// consolidated. Designed for the chained file landing pattern
    /// (<c>file -&gt; [pre].[&lt;Table&gt;] -&gt; view [pre].[v&lt;Table&gt;] -&gt; target</c>): the source is the
    /// typed view <c>[pre].[v&lt;Table&gt;]</c> and the table truncated is the landing table <c>[pre].[&lt;Table&gt;]</c>
    /// (the leading <c>v_</c> is stripped; a source that is already a base table is truncated as-is). A FAILED
    /// run never truncates (the truncate is on the success path); an empty landing table is a no-op; a target
    /// that has NOT caught up leaves the landing table intact so no un-consolidated data is lost. Requires an
    /// incremental watermark column (<see cref="IncrementalPolicy.Columns"/> or
    /// <see cref="IncrementalPolicy.DateColumn"/>) and a SQL Server source (the landing truncate is issued as
    /// T-SQL on the source connection); both are enforced at run start. Off by default.
    /// </summary>
    public bool TruncateSourceWhenConsolidated { get; init; }
}

/// <summary>
/// Change detection when there is no usable business key, or to detect modified rows (legacy HashKey
/// family). A fixed-length binary hash over <see cref="HashColumns"/> identifies and compares rows.
/// </summary>
public sealed record ChangePolicy
{
    public IReadOnlyList<string> HashColumns { get; init; } = [];          // HashKeyColumns

    /// <summary>The hash algorithm (legacy HashKeyType). Null means the engine default (SHA2_256 in the
    /// legacy configuration).</summary>
    public string? HashType { get; init; }                                // HashKeyType

    public IReadOnlyList<string> IgnoreColumnsInHash { get; init; } = [];  // IgnoreColumnsInHashkey

    public bool HasHashKey => HashColumns.Count > 0;
}

/// <summary>What to do with a target row whose key vanished from the source (legacy flw.MatchKey.ActionType).</summary>
public enum MatchKeyAction
{
    /// <summary>Soft delete: stamp DeletedDate_DW (and RowStatus_DW 'D' when row status is on). The row stays.</summary>
    Tag,

    /// <summary>Hard delete the row from the target.</summary>
    Delete,
}

/// <summary>
/// How the key-match (deleted-row detection) pass behaves when <see cref="IngestionLoadPolicy.MatchKeysInSourceAndTarget"/>
/// is on (legacy flw.MatchKey, scoped to the per-flow fields the ingestion-integrated pass consumes). After the
/// load, the full distinct source key set is compared against the target; a target row whose key no longer
/// exists in the source is tagged or deleted. Unlike legacy, the threshold is actually enforced, a breach is
/// loudly reported, and a tagged row whose key REAPPEARS in the source is un-tagged (resurrected).
/// </summary>
public sealed record MatchKeyPolicy
{
    /// <summary>Tag (soft delete, the safe default) or Delete (legacy ActionType).</summary>
    public MatchKeyAction Action { get; init; } = MatchKeyAction.Tag;

    /// <summary>When more than this percentage of the target would be affected, the action is SKIPPED and a
    /// warning is logged: a mass disappearance is more often a broken source read than a real mass delete
    /// (legacy ActionThresholdPercent, default 20; legacy carried it but never enforced it).</summary>
    public int ActionThresholdPercent { get; init; } = 20;

    /// <summary>Tag only rows whose <see cref="DateColumn"/> is within this many months; older rows are left
    /// alone (legacy IgnoreDeletedRowsAfter, Tag mode only). Null applies the tag regardless of age.</summary>
    public int? IgnoreDeletedRowsAfterMonths { get; init; }

    /// <summary>The date column for the ignore window (legacy flw.MatchKey.DateColumn). Falls back to the
    /// flow's incremental date column.</summary>
    public string? DateColumn { get; init; }

    /// <summary>Match keys overriding the flow's load keys (legacy flw.MatchKey.KeyColumns). Empty uses
    /// <see cref="IngestionLoadPolicy.KeyColumns"/>.</summary>
    public IReadOnlyList<string> KeyColumns { get; init; } = [];

    /// <summary>A predicate bounding the SOURCE key read (legacy srcFilter, raw-append contract: it carries
    /// its own leading AND). Null falls back to the flow's source filter, so rows the load never reads are not
    /// treated as deleted (legacy defaulted to no filter, which deleted filtered-out rows).</summary>
    public string? SourceFilter { get; init; }

    /// <summary>A predicate bounding which TARGET rows the pass may touch (legacy trgFilter, raw-append
    /// contract: it carries its own leading AND).</summary>
    public string? TargetFilter { get; init; }
}

/// <summary>System (audit) columns to inject and maintain (legacy SysColumns; valid values from
/// flw.SysColumn). The legacy default is InsertedDate_DW and UpdatedDate_DW.</summary>
public sealed record SystemColumnsPolicy
{
    public bool InsertedDate { get; init; } = true;   // InsertedDate_DW
    public bool UpdatedDate { get; init; } = true;    // UpdatedDate_DW
    public bool DeletedDate { get; init; }            // DeletedDate_DW (soft-delete marker)
    public bool RowStatus { get; init; }              // RowStatus_DW
}

/// <summary>Schema synchronization policy (legacy SyncSchema and the column-cleanup family).</summary>
public sealed record SchemaSyncPolicy
{
    /// <summary>Propagate new source columns to the target on each run (legacy SyncSchema, default true).</summary>
    public bool Sync { get; init; } = true;

    /// <summary>Clean target column names on sync (legacy OnSyncCleanColumnName); designed for SAP sources.</summary>
    public bool CleanColumnNames { get; init; }

    /// <summary>Regex used to clean column names (legacy CleanColumnNameSQLRegExp); overrides the config default.</summary>
    public string? CleanColumnNameRegex { get; init; }

    /// <summary>Replacement for invalid characters in a cleaned name (legacy ReplaceInvalidCharsWith).</summary>
    public string? ReplaceInvalidCharsWith { get; init; }

    /// <summary>Convert Unicode source types to non-Unicode on the target (legacy OnSyncConvertUnicodeDataType);
    /// designed for SAP Open Hub, which uses NVARCHAR for all text.</summary>
    public bool ConvertUnicodeToNonUnicode { get; init; }

    /// <summary>Allow a table-rewrite ALTER (for example int to bigint) to run inline. Default false: an
    /// expensive rewrite holds a table lock for the full row rewrite, so it is refused unless explicitly
    /// opted in, to be run in a maintenance window. Legacy had no such guard.</summary>
    public bool AllowTableRewrite { get; init; }
}

/// <summary>Incremental-capture policy (legacy IncrementalColumns, DateColumn, NoOfOverlapDays, FullLoad).</summary>
public sealed record IncrementalPolicy
{
    /// <summary>High-water columns whose MAX is read from the target (or MIN from the source when
    /// <see cref="FetchMinValuesFromSource"/>) to bound the next read (legacy IncrementalColumns).</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary>Date column used with <see cref="OverlapDays"/> to build an overlapping window (legacy DateColumn).</summary>
    public string? DateColumn { get; init; }

    /// <summary>Days subtracted from the watermark to re-read a safety window (legacy NoOfOverlapDays, default 7).</summary>
    public int OverlapDays { get; init; } = 7;

    /// <summary>Force a full load regardless of the incremental settings (legacy FullLoad, an int treated as
    /// truthy: non-zero means full).</summary>
    public bool FullLoad { get; init; }

    /// <summary>Read MIN from the source instead of MAX from the target, to reprocess the whole source
    /// (legacy FetchMinValuesFromSrc).</summary>
    public bool FetchMinValuesFromSource { get; init; }

    public bool IsIncremental => Columns.Count > 0 || !string.IsNullOrWhiteSpace(DateColumn);
}

/// <summary>One-time backfill plan (legacy InitLoad family): bound a large historical load and chunk it.</summary>
public sealed record InitLoadPolicy
{
    public bool Enabled { get; init; }            // InitLoad
    public DateOnly? FromDate { get; init; }      // InitLoadFromDate
    public DateOnly? ToDate { get; init; }        // InitLoadToDate
    public string? BatchBy { get; init; }         // InitLoadBatchBy (a single-character unit)
    public int? BatchSize { get; init; }          // InitLoadBatchSize
    public string? KeyColumn { get; init; }       // InitLoadKeyColumn
    public int? KeyMaxValue { get; init; }        // InitLoadKeyMaxValue
}

/// <summary>Target history and dimension helpers (legacy trgVersioning, InsertUnknownDimRow, token family).</summary>
public sealed record VersioningPolicy
{
    /// <summary>Maintain a SQL Server system-versioned temporal history table for the target (legacy
    /// trgVersioning). NOT YET IMPLEMENTED: the engine rejects a flow that sets this rather than silently
    /// ignoring it (system-versioning interacts with the schema-evolution engine and is a dedicated feature).
    /// Use <see cref="Scd2"/> for engine-managed dimension history today.</summary>
    public bool TemporalHistory { get; init; }

    /// <summary>Insert an unknown-member row for dimension handling (legacy InsertUnknownDimRow). NOT YET
    /// IMPLEMENTED: the engine rejects a flow that sets this rather than silently ignoring it (a correct
    /// implementation needs a sentinel-key convention plus exemption from the match-key delete pass).</summary>
    public bool InsertUnknownDimensionRow { get; init; }

    /// <summary>Reserved (legacy TokenVersioning). The legacy engine logs "not implemented"; carried for
    /// fidelity and future use.</summary>
    public bool TokenVersioning { get; init; }

    /// <summary>Reserved (legacy TokenRetentionDays).</summary>
    public int? TokenRetentionDays { get; init; }

    /// <summary>Application-managed slowly-changing-dimension (Type 2) history: period columns the engine
    /// maintains itself, so unlike <see cref="TemporalHistory"/> it can be enabled on an already-created,
    /// already-populated target (the period columns are added through normal schema evolution and existing
    /// rows are backfilled as the current version).</summary>
    public Scd2Policy Scd2 { get; init; } = new();
}

/// <summary>
/// Application-managed slowly-changing-dimension Type 2 (period-based dimension history). The engine keeps a
/// validity period per row: when a tracked attribute of a keyed row changes, the current row is closed
/// (its <see cref="ValidToColumn"/> stamped, <see cref="CurrentFlagColumn"/> cleared) and a new current
/// version inserted. The period columns are ordinary datetime2/bit columns, so the feature can be turned on
/// for an existing populated target: schema evolution adds the columns and the existing rows are backfilled as
/// the current version. The current row is the one whose <see cref="CurrentFlagColumn"/> is 1 (its
/// <see cref="ValidToColumn"/> holds the open-ended sentinel).
/// </summary>
public sealed record Scd2Policy
{
    public bool Enabled { get; init; }

    /// <summary>The period-start column (default <c>ValidFrom_DW</c>).</summary>
    public string ValidFromColumn { get; init; } = "ValidFrom_DW";

    /// <summary>The period-end column (default <c>ValidTo_DW</c>); the current row holds the open sentinel.</summary>
    public string ValidToColumn { get; init; } = "ValidTo_DW";

    /// <summary>The current-row indicator column (default <c>IsCurrent_DW</c>): 1 for the live version, 0 for
    /// expired versions.</summary>
    public string CurrentFlagColumn { get; init; } = "IsCurrent_DW";

    /// <summary>The attributes whose change opens a new version, as SOURCE column names; empty means every
    /// non-key data column (the usual dimension behavior).</summary>
    public IReadOnlyList<string> TrackedColumns { get; init; } = [];
}

/// <summary>Pre and post processing hooks (legacy PreProcessOnTrg / PostProcessOnTrg raw T-SQL commands, and
/// the PreInvokeAlias / PostInvokeAlias ADF or Automation runbook hooks).</summary>
public sealed record ProcessPolicy
{
    /// <summary>A raw T-SQL command (typically <c>EXEC [schema].[proc]</c>) run verbatim on the target before
    /// the load, no parameters bound (legacy PreProcessOnTrg).</summary>
    public string? PreProcessOnTarget { get; init; }   // PreProcessOnTrg (raw T-SQL, run as CommandType.Text)

    /// <summary>A raw T-SQL command run verbatim on the target after the load commits (legacy PostProcessOnTrg).</summary>
    public string? PostProcessOnTarget { get; init; }  // PostProcessOnTrg (raw T-SQL, run as CommandType.Text)
    public string? PreInvokeAlias { get; init; }       // PreInvokeAlias (an flw.Invoke alias)
    public string? PostInvokeAlias { get; init; }      // PostInvokeAlias
}
