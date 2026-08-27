using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Maintenance;
using SqlFlow.SqlServer.Health;

namespace SqlFlow.SqlServer.Maintenance;

/// <summary>
/// The SQL Server implementations of the standard warehouse maintenance actions, and the registry the node
/// dispatches through. Every action is READ-ONLY: it measures the warehouse through catalog views and dynamic
/// management views and returns findings with review-ready SQL. Nothing here executes a mutating statement,
/// which is what makes the whole family safe to expose to an assistant.
///
/// The four warehouse-health probes delegate to <see cref="SqlServerHealthProbe"/> rather than re-querying the
/// same DMVs: the probes and the maintenance family are one code path, and the probes' historical result shape
/// is preserved verbatim as the report's <see cref="MaintenanceReport.Detail"/> so the insights recommendations
/// keep reading it unchanged.
/// </summary>
public static class SqlServerMaintenanceActions
{
    /// <summary>Every action, keyed by its descriptor name.</summary>
    private static readonly IReadOnlyDictionary<string, IMaintenanceAction> Registry =
        new IMaintenanceAction[]
        {
            new MissingIndexesAction(),
            new StatisticsHealthAction(),
            new IndexUsageAction(),
            new TopQueriesAction(),
            new IndexFragmentationAction(),
            new TableSpaceAction(),
            new HeapTablesAction(),
            new ConstraintTrustAction(),
            new DuplicateKeysAction(),
        }.ToDictionary(a => a.Descriptor.Name, StringComparer.Ordinal);

    /// <summary>The actions this build implements, in catalog order.</summary>
    public static IReadOnlyList<IMaintenanceAction> All { get; } =
        MaintenanceActions.All.Select(d => Registry[d.Name]).ToArray();

    /// <summary>
    /// Runs one validated maintenance request. Throws <see cref="SqlFlowException"/> when the action is not
    /// implemented for this provider, which cannot happen for a request that passed
    /// <see cref="MaintenanceActions.Validate"/> against the resolved kind, but is checked because the resolved
    /// kind of an @alias is only known here.
    /// </summary>
    public static Task<MaintenanceReport> RunAsync(MaintenanceExecutionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Registry.TryGetValue(context.Request.Action, out var action))
        {
            throw new SqlFlowException(
                $"No SQL Server implementation exists for the maintenance action '{context.Request.Action}'.");
        }

        if (!action.Descriptor.SupportsKind(context.Kind))
        {
            throw new SqlFlowException(
                $"The maintenance action '{action.Descriptor.Name}' is authored in T-SQL; the source resolved " +
                $"to kind '{context.Kind}'. Only SQL Server and Azure SQL sources are supported.");
        }

        return action.RunAsync(context, ct);
    }

    // ----------------------------------------------------------------------------------------------------
    // The four warehouse-health probes, wrapped so they carry findings and review SQL like every other action
    // while keeping their historical Detail shape.
    // ----------------------------------------------------------------------------------------------------

    private sealed class MissingIndexesAction : IMaintenanceAction
    {
        public MaintenanceActionDescriptor Descriptor { get; } = MaintenanceActions.Find(MaintenanceActions.MissingIndexes)!;

        public async Task<MaintenanceReport> RunAsync(MaintenanceExecutionContext context, CancellationToken ct)
        {
            var limit = context.Request.Limit;
            var advisories = await SqlServerHealthProbe
                .MissingIndexesAsync(context.ConnectionString, context.Scope.Database, limit, ct).ConfigureAwait(false);

            var database = advisories.Count > 0 ? advisories[0].Database : context.Scope.Database;
            var findings = advisories.Select(a => new MaintenanceFinding
            {
                // The improvement measure has no absolute unit, so the split is by order of magnitude: the
                // advisories worth acting on first are the ones the optimizer scored orders above the rest.
                Severity = a.ImprovementMeasure >= 100_000 ? MaintenanceSeverity.Critical
                    : a.ImprovementMeasure >= 10_000 ? MaintenanceSeverity.Warning
                    : MaintenanceSeverity.Info,
                Category = "missingIndex",
                Target = $"{a.Schema}.{a.Table}",
                Detail = string.Create(CultureInfo.InvariantCulture,
                    $"{a.UserSeeks + a.UserScans} compilations would have used an index on " +
                    $"{a.EqualityColumns ?? a.InequalityColumns ?? "(included columns only)"}; the optimizer " +
                    $"estimated a {a.AvgUserImpactPercent:0.#}% cost reduction (improvement measure " +
                    $"{a.ImprovementMeasure:0}). Review against the table's existing indexes before creating it."),
                Metrics = new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    ["userSeeks"] = a.UserSeeks,
                    ["userScans"] = a.UserScans,
                    ["avgUserImpactPercent"] = a.AvgUserImpactPercent,
                    ["improvementMeasure"] = a.ImprovementMeasure,
                },
                SuggestedSql = a.SuggestedIndexSql,
            }).ToArray();

            return Build(Descriptor, context, database, advisories.Count, findings, limit,
                notes:
                [
                    "Missing-index advisories overlap each other and ignore write cost. Creating every one of " +
                    "them makes a warehouse slower, not faster; consolidate them into a smaller index set.",
                    "The advisories reset with the plan cache, so an instance restarted recently under-reports.",
                ],
                detail: new { database, advisories });
        }
    }

    private sealed class StatisticsHealthAction : IMaintenanceAction
    {
        public MaintenanceActionDescriptor Descriptor { get; } = MaintenanceActions.Find(MaintenanceActions.StatisticsHealth)!;

        public async Task<MaintenanceReport> RunAsync(MaintenanceExecutionContext context, CancellationToken ct)
        {
            var limit = context.Request.Limit;
            var statistics = await SqlServerHealthProbe
                .StatisticsHealthAsync(context.ConnectionString, context.Scope.Database, limit, ct).ConfigureAwait(false);

            var findings = statistics.Where(s => s.IsStale).Select(s => new MaintenanceFinding
            {
                Severity = s.ModificationPercent >= 50 ? MaintenanceSeverity.Critical : MaintenanceSeverity.Warning,
                Category = "staleStatistics",
                Target = $"{s.Schema}.{s.Table}.{s.StatisticName}",
                Detail = string.Create(CultureInfo.InvariantCulture,
                    $"{s.ModificationCounter:N0} rows changed since the statistic was last built " +
                    $"({s.ModificationPercent:0.#}% of {s.Rows:N0} rows" +
                    $"{(s.LastUpdated is { } updated ? $", last updated {updated:yyyy-MM-dd}" : ", never updated")}). " +
                    $"The optimizer is costing plans on a distribution that no longer holds."),
                Metrics = new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    ["rows"] = s.Rows,
                    ["modificationCounter"] = s.ModificationCounter,
                    ["modificationPercent"] = s.ModificationPercent,
                    ["samplePercent"] = s.SamplePercent,
                },
                SuggestedSql = s.SuggestedUpdateSql,
            }).ToArray();

            var staleCount = statistics.Count(s => s.IsStale);
            return Build(Descriptor, context, context.Scope.Database, statistics.Count, findings, limit,
                notes:
                [
                    "Staleness is measured against the engine's own dynamic auto-update threshold. A statistic " +
                    "past it will be rebuilt by the engine on next use anyway unless auto-update is off; the " +
                    "value of updating deliberately is choosing WHEN that cost lands.",
                ],
                detail: new { database = context.Scope.Database, statistics, staleCount });
        }
    }

    private sealed class IndexUsageAction : IMaintenanceAction
    {
        public MaintenanceActionDescriptor Descriptor { get; } = MaintenanceActions.Find(MaintenanceActions.IndexUsage)!;

        public async Task<MaintenanceReport> RunAsync(MaintenanceExecutionContext context, CancellationToken ct)
        {
            var limit = context.Request.Limit;
            var indexes = await SqlServerHealthProbe
                .IndexUsageAsync(context.ConnectionString, context.Scope.Database, limit, ct).ConfigureAwait(false);

            var findings = indexes.Where(i => i.IsUnused).Select(i => new MaintenanceFinding
            {
                Severity = i.Writes >= 1_000_000 ? MaintenanceSeverity.Warning : MaintenanceSeverity.Info,
                Category = "unusedIndex",
                Target = $"{i.Schema}.{i.Table}.{i.IndexName}",
                Detail = string.Create(CultureInfo.InvariantCulture,
                    $"{i.Writes:N0} writes maintained this index and no read used it since the counters last " +
                    $"reset; it occupies {i.SizeKb / 1024.0:0.#} MB over {i.RowCount:N0} rows. Confirm the " +
                    $"counter window is long enough to be meaningful before dropping it."),
                Metrics = new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    ["reads"] = i.Reads,
                    ["writes"] = i.Writes,
                    ["sizeKb"] = i.SizeKb,
                    ["rowCount"] = i.RowCount,
                },
                SuggestedSql = string.Create(CultureInfo.InvariantCulture,
                    $"DROP INDEX {Quote(i.IndexName)} ON {Quote(i.Schema)}.{Quote(i.Table)};"),
            }).ToArray();

            var unusedCount = indexes.Count(i => i.IsUnused);
            return Build(Descriptor, context, context.Scope.Database, indexes.Count, findings, limit,
                notes:
                [
                    "The usage counters reset when the instance restarts, so a short window makes every index " +
                    "look unused. Check how long the counters have been accumulating before dropping anything.",
                    "An index that serves only a quarter-end or year-end report reads as unused for months.",
                ],
                detail: new { database = context.Scope.Database, indexes, unusedCount });
        }
    }

    private sealed class TopQueriesAction : IMaintenanceAction
    {
        public MaintenanceActionDescriptor Descriptor { get; } = MaintenanceActions.Find(MaintenanceActions.TopQueries)!;

        public async Task<MaintenanceReport> RunAsync(MaintenanceExecutionContext context, CancellationToken ct)
        {
            var limit = context.Request.Limit;
            var queries = await SqlServerHealthProbe
                .TopQueriesAsync(context.ConnectionString, context.Scope.Database, limit, ct).ConfigureAwait(false);

            // Only the genuinely dominant statements become findings: everything in the plan cache is "top"
            // something, and a list of 200 statements is not an answer.
            var totalElapsed = queries.Sum(q => q.TotalElapsedMs);
            var findings = queries
                .Where(q => totalElapsed > 0 && q.TotalElapsedMs / totalElapsed >= 0.05)
                .Select(q => new MaintenanceFinding
                {
                    Severity = q.TotalElapsedMs / totalElapsed >= 0.25 ? MaintenanceSeverity.Warning : MaintenanceSeverity.Info,
                    Category = "expensiveStatement",
                    Target = q.Database ?? "(ad-hoc batch)",
                    Detail = string.Create(CultureInfo.InvariantCulture,
                        $"{q.TotalElapsedMs / totalElapsed * 100:0.#}% of the cached workload's elapsed time: " +
                        $"{q.ExecutionCount:N0} executions averaging {q.AvgElapsedMs:0} ms and " +
                        $"{q.AvgLogicalReads:N0} logical reads. Statement: {Excerpt(q.StatementText)}"),
                    Metrics = new Dictionary<string, double>(StringComparer.Ordinal)
                    {
                        ["executionCount"] = q.ExecutionCount,
                        ["totalElapsedMs"] = q.TotalElapsedMs,
                        ["avgElapsedMs"] = q.AvgElapsedMs,
                        ["avgLogicalReads"] = q.AvgLogicalReads,
                        ["shareOfWorkloadPercent"] = totalElapsed > 0 ? q.TotalElapsedMs / totalElapsed * 100 : 0,
                    },
                })
                .ToArray();

            return Build(Descriptor, context, context.Scope.Database, queries.Count, findings, limit,
                notes:
                [
                    "The plan cache is a rolling window: plans age out and take their counters with them, so " +
                    "this ranks what is expensive lately, not what was expensive over any fixed period.",
                    "Shares are of the CACHED workload only, not of everything the server has ever run.",
                ],
                detail: new { database = context.Scope.Database, queries });
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // The measurements added by the maintenance family.
    // ----------------------------------------------------------------------------------------------------

    private sealed class IndexFragmentationAction : IMaintenanceAction
    {
        public MaintenanceActionDescriptor Descriptor { get; } = MaintenanceActions.Find(MaintenanceActions.IndexFragmentation)!;

        public async Task<MaintenanceReport> RunAsync(MaintenanceExecutionContext context, CancellationToken ct)
        {
            var request = context.Request;
            var reorganize = request.Threshold("reorganizeThreshold", 5);
            var rebuild = request.Threshold("rebuildThreshold", 30);
            var minimumPages = request.Threshold("minimumPages", 1000);
            if (rebuild < reorganize)
            {
                throw new SqlFlowException(
                    "rebuildThreshold must be at or above reorganizeThreshold; a rebuild is the heavier remedy " +
                    "for the worse fragmentation.");
            }

            // LIMITED mode reads only the parent level of each B-tree, so it never scans leaf pages. It is the
            // only mode cheap enough to run over a warehouse, and it does not report page fullness, which is
            // why the report says nothing about page density.
            const string sql = """
                DECLARE @objectId int = CASE WHEN @qualified IS NULL THEN NULL ELSE OBJECT_ID(@qualified) END;
                IF @qualified IS NOT NULL AND @objectId IS NULL
                    THROW 50000, 'The scoped object does not exist in the scoped database.', 1;

                SELECT TOP (@limit)
                    s.name AS schema_name,
                    o.name AS table_name,
                    i.name AS index_name,
                    i.type_desc,
                    ps.avg_fragmentation_in_percent,
                    ps.page_count,
                    ps.fragment_count,
                    i.is_primary_key,
                    i.is_unique
                FROM sys.dm_db_index_physical_stats(DB_ID(), @objectId, NULL, NULL, 'LIMITED') AS ps
                JOIN sys.indexes AS i ON i.object_id = ps.object_id AND i.index_id = ps.index_id
                JOIN sys.objects AS o ON o.object_id = i.object_id
                JOIN sys.schemas AS s ON s.schema_id = o.schema_id
                WHERE o.is_ms_shipped = 0
                  AND o.type = 'U'
                  AND i.index_id > 0
                  AND i.name IS NOT NULL
                  AND ps.page_count >= @minimumPages
                  AND ps.avg_fragmentation_in_percent >= @reorganize
                  AND (@schema IS NULL OR s.name = @schema)
                ORDER BY ps.avg_fragmentation_in_percent DESC, ps.page_count DESC;
                """;

            await using var connection = await OpenAsync(context, ct).ConfigureAwait(false);
            await using var command = Command(connection, sql, context.Request.Limit);
            command.Parameters.Add(new SqlParameter("@qualified", (object?)QualifiedOrNull(context.Scope) ?? DBNull.Value));
            command.Parameters.Add(new SqlParameter("@schema", (object?)context.Scope.Schema ?? DBNull.Value));
            command.Parameters.Add(new SqlParameter("@minimumPages", (long)minimumPages));
            command.Parameters.Add(new SqlParameter("@reorganize", reorganize));

            var findings = new List<MaintenanceFinding>();
            var examined = 0;
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                examined++;
                var schema = reader.GetString(0);
                var table = reader.GetString(1);
                var index = reader.GetString(2);
                var typeDesc = reader.GetString(3);
                var fragmentation = reader.GetDouble(4);
                var pages = reader.GetInt64(5);
                // fragment_count is not reported for every index type even in LIMITED mode; zero reads as
                // "not measured" in the sentence below rather than failing the whole scan.
                var fragments = reader.IsDBNull(6) ? 0L : reader.GetInt64(6);

                var needsRebuild = fragmentation >= rebuild;
                var remedy = needsRebuild ? "REBUILD" : "REORGANIZE";
                findings.Add(new MaintenanceFinding
                {
                    Severity = needsRebuild ? MaintenanceSeverity.Warning : MaintenanceSeverity.Info,
                    Category = "fragmentation",
                    Target = $"{schema}.{table}.{index}",
                    Detail = string.Create(CultureInfo.InvariantCulture,
                        $"{fragmentation:0.#}% logically fragmented across {pages:N0} pages in {fragments:N0} " +
                        $"fragments ({typeDesc}). Past the {(needsRebuild ? rebuild : reorganize):0.#}% " +
                        $"{remedy.ToLowerInvariant()} threshold."),
                    Metrics = new Dictionary<string, double>(StringComparer.Ordinal)
                    {
                        ["fragmentationPercent"] = fragmentation,
                        ["pageCount"] = pages,
                        ["fragmentCount"] = fragments,
                        ["sizeMb"] = pages * 8 / 1024.0,
                    },
                    SuggestedSql = needsRebuild
                        ? $"ALTER INDEX {Quote(index)} ON {Quote(schema)}.{Quote(table)} REBUILD WITH (ONLINE = ON);"
                        : $"ALTER INDEX {Quote(index)} ON {Quote(schema)}.{Quote(table)} REORGANIZE;",
                });
            }

            return Build(Descriptor, context, context.Scope.Database, examined, findings, context.Request.Limit,
                notes:
                [
                    "Measured in LIMITED mode, which reads each B-tree's parent level only. That is what makes " +
                    "the scan affordable; it also means page fullness is not measured and is not reported.",
                    "ONLINE = ON is suggested for every rebuild. It requires Enterprise or Azure SQL; on " +
                    "Standard the statement fails and the rebuild must run offline in a maintenance window.",
                    "Fragmentation matters for range scans, not for the singleton lookups a warehouse load " +
                    "does. Rebuilding an index nothing scans buys nothing and costs a full log write.",
                ],
                detail: null);
        }
    }

    private sealed class TableSpaceAction : IMaintenanceAction
    {
        public MaintenanceActionDescriptor Descriptor { get; } = MaintenanceActions.Find(MaintenanceActions.TableSpace)!;

        public async Task<MaintenanceReport> RunAsync(MaintenanceExecutionContext context, CancellationToken ct)
        {
            var minimumMb = context.Request.Threshold("minimumMb", 0);

            const string sql = """
                SELECT TOP (@limit)
                    s.name AS schema_name,
                    o.name AS table_name,
                    SUM(CASE WHEN ps.index_id IN (0, 1) THEN ps.row_count ELSE 0 END) AS row_count,
                    SUM(ps.reserved_page_count) * 8 AS reserved_kb,
                    SUM(ps.used_page_count) * 8 AS used_kb,
                    SUM(CASE WHEN ps.index_id IN (0, 1) THEN ps.in_row_data_page_count ELSE 0 END) * 8 AS data_kb,
                    MAX(CASE WHEN p.data_compression > 0 THEN 1 ELSE 0 END) AS any_compressed,
                    COUNT(DISTINCT ps.index_id) AS index_count
                FROM sys.dm_db_partition_stats AS ps
                JOIN sys.objects AS o ON o.object_id = ps.object_id
                JOIN sys.schemas AS s ON s.schema_id = o.schema_id
                JOIN sys.partitions AS p ON p.partition_id = ps.partition_id
                WHERE o.is_ms_shipped = 0
                  AND o.type = 'U'
                  AND (@schema IS NULL OR s.name = @schema)
                  AND (@object IS NULL OR o.name = @object)
                GROUP BY s.name, o.name
                HAVING SUM(ps.reserved_page_count) * 8.0 / 1024 >= @minimumMb
                ORDER BY SUM(ps.reserved_page_count) DESC;
                """;

            await using var connection = await OpenAsync(context, ct).ConfigureAwait(false);
            await using var command = Command(connection, sql, context.Request.Limit);
            command.Parameters.Add(new SqlParameter("@schema", (object?)context.Scope.Schema ?? DBNull.Value));
            command.Parameters.Add(new SqlParameter("@object", (object?)context.Scope.ObjectName ?? DBNull.Value));
            command.Parameters.Add(new SqlParameter("@minimumMb", minimumMb));

            var entries = new List<object>();
            var findings = new List<MaintenanceFinding>();
            var examined = 0;
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                examined++;
                var schema = reader.GetString(0);
                var table = reader.GetString(1);
                var rows = reader.GetInt64(2);
                var reservedKb = reader.GetInt64(3);
                var usedKb = reader.GetInt64(4);
                var dataKb = reader.GetInt64(5);
                var compressed = reader.GetInt32(6) == 1;
                var indexCount = reader.GetInt32(7);
                var indexKb = Math.Max(0, usedKb - dataKb);

                entries.Add(new
                {
                    schema,
                    table,
                    rows,
                    reservedMb = reservedKb / 1024.0,
                    dataMb = dataKb / 1024.0,
                    indexMb = indexKb / 1024.0,
                    unusedMb = Math.Max(0, reservedKb - usedKb) / 1024.0,
                    compressed,
                    indexCount,
                });

                // A large uncompressed table is the finding worth acting on: warehouse fact tables compress
                // several-fold, and row compression is transparent to every reader.
                if (!compressed && reservedKb >= 1024L * 1024)
                {
                    findings.Add(new MaintenanceFinding
                    {
                        Severity = reservedKb >= 10L * 1024 * 1024 ? MaintenanceSeverity.Warning : MaintenanceSeverity.Info,
                        Category = "uncompressedTable",
                        Target = $"{schema}.{table}",
                        Detail = string.Create(CultureInfo.InvariantCulture,
                            $"{reservedKb / 1024.0 / 1024:0.##} GB reserved over {rows:N0} rows with no " +
                            $"compression. Estimate the saving before committing: sp_estimate_data_compression_savings."),
                        Metrics = new Dictionary<string, double>(StringComparer.Ordinal)
                        {
                            ["rows"] = rows,
                            ["reservedMb"] = reservedKb / 1024.0,
                            ["dataMb"] = dataKb / 1024.0,
                            ["indexMb"] = indexKb / 1024.0,
                        },
                        SuggestedSql =
                            $"ALTER TABLE {Quote(schema)}.{Quote(table)} REBUILD WITH (DATA_COMPRESSION = ROW);",
                    });
                }
            }

            return Build(Descriptor, context, context.Scope.Database, examined, findings, context.Request.Limit,
                notes:
                [
                    "Sizes come from the partition metadata, which is exact for a settled table and free to " +
                    "read; a load still committing can make a table look momentarily smaller than it is.",
                    "Compression suggestions are ROW, not PAGE: ROW is the one that is nearly always a win on " +
                    "a warehouse table and costs the least CPU on read.",
                ],
                detail: new { database = context.Scope.Database, tables = entries });
        }
    }

    private sealed class HeapTablesAction : IMaintenanceAction
    {
        public MaintenanceActionDescriptor Descriptor { get; } = MaintenanceActions.Find(MaintenanceActions.HeapTables)!;

        public async Task<MaintenanceReport> RunAsync(MaintenanceExecutionContext context, CancellationToken ct)
        {
            var minimumRows = context.Request.Threshold("minimumRows", 1000);

            const string sql = """
                SELECT TOP (@limit)
                    s.name AS schema_name,
                    o.name AS table_name,
                    SUM(ps.row_count) AS row_count,
                    SUM(ps.reserved_page_count) * 8 AS reserved_kb
                FROM sys.indexes AS i
                JOIN sys.objects AS o ON o.object_id = i.object_id
                JOIN sys.schemas AS s ON s.schema_id = o.schema_id
                JOIN sys.dm_db_partition_stats AS ps
                    ON ps.object_id = i.object_id AND ps.index_id = i.index_id
                WHERE i.type = 0
                  AND o.is_ms_shipped = 0
                  AND o.type = 'U'
                  AND (@schema IS NULL OR s.name = @schema)
                  AND (@object IS NULL OR o.name = @object)
                GROUP BY s.name, o.name
                HAVING SUM(ps.row_count) >= @minimumRows
                ORDER BY SUM(ps.row_count) DESC;
                """;

            await using var connection = await OpenAsync(context, ct).ConfigureAwait(false);
            await using var command = Command(connection, sql, context.Request.Limit);
            command.Parameters.Add(new SqlParameter("@schema", (object?)context.Scope.Schema ?? DBNull.Value));
            command.Parameters.Add(new SqlParameter("@object", (object?)context.Scope.ObjectName ?? DBNull.Value));
            command.Parameters.Add(new SqlParameter("@minimumRows", (long)minimumRows));

            var findings = new List<MaintenanceFinding>();
            var examined = 0;
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                examined++;
                var schema = reader.GetString(0);
                var table = reader.GetString(1);
                var rows = reader.GetInt64(2);
                var reservedKb = reader.GetInt64(3);

                findings.Add(new MaintenanceFinding
                {
                    Severity = rows >= 1_000_000 ? MaintenanceSeverity.Warning : MaintenanceSeverity.Info,
                    Category = "heap",
                    Target = $"{schema}.{table}",
                    Detail = string.Create(CultureInfo.InvariantCulture,
                        $"No clustered index over {rows:N0} rows and {reservedKb / 1024.0:0.#} MB. Every access " +
                        $"is a full scan, and space freed by deletes is not reclaimed until the table gets one."),
                    Metrics = new Dictionary<string, double>(StringComparer.Ordinal)
                    {
                        ["rows"] = rows,
                        ["reservedMb"] = reservedKb / 1024.0,
                    },
                    // The clustering key is a design decision that depends on how the table is queried, so the
                    // suggestion names the shape and deliberately leaves the columns to the reviewer.
                    SuggestedSql =
                        $"-- Choose the clustering key from how {schema}.{table} is actually queried " +
                        "(the load's merge key is usually right)." + Environment.NewLine +
                        $"CREATE CLUSTERED INDEX {Quote("CIX_" + table)} ON {Quote(schema)}.{Quote(table)} (/* key columns */);",
                });
            }

            return Build(Descriptor, context, context.Scope.Database, examined, findings, context.Request.Limit,
                notes:
                [
                    "A staging table deliberately left as a heap for bulk-load throughput is a legitimate heap. " +
                    "Judge each finding against what the table is for before clustering it.",
                ],
                detail: null);
        }
    }

    private sealed class ConstraintTrustAction : IMaintenanceAction
    {
        public MaintenanceActionDescriptor Descriptor { get; } = MaintenanceActions.Find(MaintenanceActions.ConstraintTrust)!;

        public async Task<MaintenanceReport> RunAsync(MaintenanceExecutionContext context, CancellationToken ct)
        {
            const string sql = """
                SELECT TOP (@limit) kind, schema_name, table_name, constraint_name
                FROM (
                    SELECT 'FOREIGN KEY' AS kind, s.name AS schema_name, o.name AS table_name, fk.name AS constraint_name
                    FROM sys.foreign_keys AS fk
                    JOIN sys.objects AS o ON o.object_id = fk.parent_object_id
                    JOIN sys.schemas AS s ON s.schema_id = o.schema_id
                    WHERE fk.is_not_trusted = 1 AND fk.is_disabled = 0 AND o.is_ms_shipped = 0
                    UNION ALL
                    SELECT 'CHECK', s.name, o.name, cc.name
                    FROM sys.check_constraints AS cc
                    JOIN sys.objects AS o ON o.object_id = cc.parent_object_id
                    JOIN sys.schemas AS s ON s.schema_id = o.schema_id
                    WHERE cc.is_not_trusted = 1 AND cc.is_disabled = 0 AND o.is_ms_shipped = 0
                ) AS untrusted
                WHERE (@schema IS NULL OR schema_name = @schema)
                  AND (@object IS NULL OR table_name = @object)
                ORDER BY schema_name, table_name, constraint_name;
                """;

            await using var connection = await OpenAsync(context, ct).ConfigureAwait(false);
            await using var command = Command(connection, sql, context.Request.Limit);
            command.Parameters.Add(new SqlParameter("@schema", (object?)context.Scope.Schema ?? DBNull.Value));
            command.Parameters.Add(new SqlParameter("@object", (object?)context.Scope.ObjectName ?? DBNull.Value));

            var findings = new List<MaintenanceFinding>();
            var examined = 0;
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                examined++;
                var kind = reader.GetString(0);
                var schema = reader.GetString(1);
                var table = reader.GetString(2);
                var constraint = reader.GetString(3);

                findings.Add(new MaintenanceFinding
                {
                    Severity = MaintenanceSeverity.Info,
                    Category = "untrustedConstraint",
                    Target = $"{schema}.{table}.{constraint}",
                    Detail =
                        $"The {kind.ToLowerInvariant()} constraint is enabled but not trusted, which happens " +
                        "after a bulk load or a NOCHECK re-enable. The optimizer ignores it entirely, so it " +
                        "stops eliminating joins and predicates the constraint would otherwise prove redundant.",
                    Metrics = new Dictionary<string, double>(StringComparer.Ordinal),
                    // WITH CHECK validates every existing row, which is a full scan of the table and can fail
                    // if the data really does violate the constraint. That is the point: it is the check.
                    SuggestedSql =
                        $"ALTER TABLE {Quote(schema)}.{Quote(table)} WITH CHECK CHECK CONSTRAINT {Quote(constraint)};",
                });
            }

            return Build(Descriptor, context, context.Scope.Database, examined, findings, context.Request.Limit,
                notes:
                [
                    "Re-trusting a constraint validates every existing row, so it scans the table and will fail " +
                    "if the data genuinely violates it. A failure is a finding, not an error to work around.",
                ],
                detail: null);
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Shared helpers.
    // ----------------------------------------------------------------------------------------------------

    /// <summary>Assembles the report: ranks findings most severe first, deduplicates the review script, and
    /// reports truncation honestly when the action stopped at its limit.</summary>
    private static MaintenanceReport Build(
        MaintenanceActionDescriptor descriptor,
        MaintenanceExecutionContext context,
        string? database,
        int examined,
        IReadOnlyList<MaintenanceFinding> findings,
        int limit,
        IReadOnlyList<string> notes,
        object? detail)
    {
        var ranked = findings
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Target, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var script = ranked
            .Select(f => f.SuggestedSql)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new MaintenanceReport
        {
            Action = descriptor.Name,
            Title = descriptor.Title,
            Database = database,
            Scope = context.Scope.Describe(),
            ItemsExamined = examined,
            Truncated = examined >= limit,
            Findings = ranked,
            SuggestedSql = script,
            Notes = notes,
            Detail = detail,
        };
    }

    private static async Task<SqlConnection> OpenAsync(MaintenanceExecutionContext context, CancellationToken ct)
    {
        var connection = new SqlConnection(context.ConnectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(context.Scope.Database))
            {
                await connection.ChangeDatabaseAsync(context.Scope.Database, ct).ConfigureAwait(false);
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static SqlCommand Command(SqlConnection connection, string sql, int limit)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 0; // bounded by the executor's cancellation budget, matching the other probes
        command.Parameters.Add(new SqlParameter("@limit", limit));
        return command;
    }

    /// <summary>The two-part name for an object-scoped request, or null when the scope is wider. Both parts are
    /// quoted, so OBJECT_ID resolves a name containing a bracket or a period correctly.</summary>
    private static string? QualifiedOrNull(MaintenanceScope scope)
        => scope.ObjectName is null ? null : $"{Quote(scope.Schema!)}.{Quote(scope.ObjectName)}";

    private static string Quote(string identifier)
        => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    /// <summary>A one-line excerpt of a statement for a finding sentence; the full text stays in the detail.</summary>
    private static string Excerpt(string statement)
    {
        var collapsed = string.Join(' ', statement.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= 160 ? collapsed : collapsed[..157] + "...";
    }
}
