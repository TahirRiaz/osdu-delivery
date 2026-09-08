using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Catalog;

/// <summary>How much per-run trace is stored and how many event rows the retention policy would reclaim: the
/// numbers the maintenance view shows.</summary>
public sealed record RunTraceStorageSummary(long TotalEvents, long PrunableEvents, int PrunableRuns);

/// <summary>
/// Retention for the per-run trace (<see cref="CatalogRunEvent"/>): every line a run logged as it executed. The
/// <see cref="CatalogRun"/> header (stats and error message) is never touched, and neither is the delivery ledger
/// (the record history that makes a delivery traceable lives in its own tables and has its own retention).
/// <para>
/// A run's events are prunable when the run finished successfully (or was cancelled/skipped, never failed), is
/// older than the grace window, and is not its pipeline's most recent run, so each pipeline keeps the trace of
/// its latest run and of every failed run, and only the older duplicates are dropped. The predicate is defined
/// once here and drives both the summary count and the batched delete, so what the view reports as prunable is
/// exactly what a cleanup removes.
/// </para>
/// </summary>
public static class RunTraceStore
{
    // The DELETE TOP (N) batch size: large enough that draining a big backlog is a handful of round-trips, small
    // enough that each delete's transaction and lock footprint stays modest and never escalates to a table lock.
    private const int DeleteBatchSize = 5000;

    /// <summary>The run ids whose trace is prunable at <paramref name="supersededBeforeUtc"/> (a terminal,
    /// non-failed run, older than the grace instant, that is not its pipeline's latest). A composable query, so the
    /// summary and the delete share one definition of "prunable".</summary>
    public static IQueryable<Guid> PrunableRunIds(CatalogDbContext catalog, DateTime supersededBeforeUtc)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Runs
            .Where(r =>
                (r.Status == RunStatuses.Succeeded || r.Status == RunStatuses.Cancelled || r.Status == RunStatuses.Skipped)
                && r.WrittenUtc < supersededBeforeUtc
                && catalog.Runs.Any(nr => nr.PipelineId == r.PipelineId && nr.WrittenUtc > r.WrittenUtc))
            .Select(r => r.RunId);
    }

    /// <summary>Counts what is stored and how many event rows are prunable. A null
    /// <paramref name="supersededBeforeUtc"/> means retention is off (keep forever): the total is still reported
    /// but nothing is prunable.</summary>
    public static async Task<RunTraceStorageSummary> SummarizeAsync(
        CatalogDbContext catalog, DateTime? supersededBeforeUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var totalEvents = await catalog.RunEvents.LongCountAsync(ct).ConfigureAwait(false);
        if (supersededBeforeUtc is not { } before)
        {
            return new RunTraceStorageSummary(totalEvents, 0, 0);
        }

        var prunable = PrunableRunIds(catalog, before);
        // Count only runs that still have events to drop, so the view never reports "N runs" with nothing behind it.
        var prunableRuns = await prunable.CountAsync(r => catalog.RunEvents.Any(e => e.RunId == r), ct).ConfigureAwait(false);
        var prunableEvents = await catalog.RunEvents
            .LongCountAsync(e => prunable.Contains(e.RunId), ct).ConfigureAwait(false);

        return new RunTraceStorageSummary(totalEvents, prunableEvents, prunableRuns);
    }

    /// <summary>Deletes the prunable event rows (retention policy), in bounded batches. A null
    /// <paramref name="supersededBeforeUtc"/> (retention off) is a no-op. Idempotent and safe to run concurrently
    /// across replicas: each batch simply claims whatever prunable rows remain. Returns the number of rows deleted.</summary>
    public static async Task<int> PruneEventsAsync(
        CatalogDbContext catalog, DateTime? supersededBeforeUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (supersededBeforeUtc is not { } before)
        {
            return 0;
        }

        var eventTable = QualifiedTable(catalog, typeof(CatalogRunEvent));
        var runTable = QualifiedTable(catalog, typeof(CatalogRun));

        // DELETE TOP (N) event rows joined to their prunable run, looped until a pass deletes fewer than the cap
        // (the tail). The join drives from the run side, so the optimizer seeks prunable runs (Status + WrittenUtc
        // index) then their event rows (the RunEvent RunId index) instead of scanning the whole table. The table
        // names come from the model (schema-qualified) and the status values are the RunStatuses constants, so the
        // interpolation carries no injection surface; the only runtime value, the grace instant, is the bound {0}
        // parameter.
        var sql =
            $"DELETE TOP ({DeleteBatchSize}) c FROM {eventTable} AS c " +
            $"INNER JOIN {runTable} r ON r.[RunId] = c.[RunId] " +
            $"WHERE r.[Status] IN ('{RunStatuses.Succeeded}', '{RunStatuses.Cancelled}', '{RunStatuses.Skipped}') " +
            "AND r.[WrittenUtc] < {0} " +
            $"AND EXISTS (SELECT 1 FROM {runTable} nr WHERE nr.[PipelineId] = r.[PipelineId] AND nr.[WrittenUtc] > r.[WrittenUtc]);";

        var total = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var deleted = await catalog.Database
                .ExecuteSqlRawAsync(sql, new object[] { before }, ct).ConfigureAwait(false);
            total += deleted;
            if (deleted < DeleteBatchSize)
            {
                return total;
            }
        }
    }

    /// <summary>Deletes every event row for every run, in bounded batches: the execution log in full, keeping the
    /// run headers (history, timing, stats, error). This is the deliberate, operator-driven escape hatch for
    /// reclaiming the trace on demand, distinct from the retention prune, which spares each pipeline's latest run
    /// and failed runs. Returns the number of rows deleted.</summary>
    public static async Task<int> PurgeAllEventsAsync(CatalogDbContext catalog, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var eventTable = QualifiedTable(catalog, typeof(CatalogRunEvent));

        // DELETE TOP (N) with no predicate, looped until the table is empty (a pass deletes fewer than the cap), so
        // even a huge table drains in bounded batches without one giant transaction. The table name comes from the
        // model (schema-qualified), so there is no injection surface.
        var sql = $"DELETE TOP ({DeleteBatchSize}) FROM {eventTable};";
        var total = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var deleted = await catalog.Database.ExecuteSqlRawAsync(sql, ct).ConfigureAwait(false);
            total += deleted;
            if (deleted < DeleteBatchSize)
            {
                return total;
            }
        }
    }

    /// <summary>The schema-qualified, bracketed table name for a mapped entity (for example
    /// <c>[catalog].[RunEvent]</c>). Read from the model so the raw delete follows the catalog's actual schema
    /// (it is not <c>dbo</c>), and keeps following it if that ever changes, instead of hardcoding it.</summary>
    private static string QualifiedTable(CatalogDbContext catalog, Type clrType)
    {
        var entity = catalog.Model.FindEntityType(clrType)
            ?? throw new InvalidOperationException($"No mapped entity for {clrType.Name}.");
        var table = entity.GetTableName()
            ?? throw new InvalidOperationException($"{clrType.Name} is not mapped to a table.");
        var schema = entity.GetSchema();
        return schema is null ? $"[{table}]" : $"[{schema}].[{table}]";
    }
}
