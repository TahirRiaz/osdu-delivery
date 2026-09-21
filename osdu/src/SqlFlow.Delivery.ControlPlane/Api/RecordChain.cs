using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>How a stage was found, which is also what it is evidence of.</summary>
public static class ChainMatch
{
    /// <summary>The run processed a file of that name: the platform's own record of processed files says so.</summary>
    public const string File = "file";

    /// <summary>
    /// The run was writing the record's ingestion table at the moment the row was stamped: it is the run that loaded
    /// the row, even though it processed no file of its own.
    /// </summary>
    public const string Table = "table";
}

/// <summary>
/// One run that carried a record's row, as a stage of its chain: which flow, of which kind, when it ran, how it ended,
/// and what it read or wrote. The flow kinds are SQLFlow's own, so a stage says pre-ingestion or ingestion in the
/// estate's vocabulary rather than in a name this module invented. <paramref name="MatchedBy"/> says how the run was
/// found: by the file it processed, or by the table it was writing when the row was stamped, in which case
/// <paramref name="ObjectName"/> names that table and <paramref name="FileName"/> is empty.
/// </summary>
public sealed record DeliveryChainStageDto(
    string Stage, Guid RunId, Guid PipelineId, string FlowName, string FlowKind, int Wave, string Status, bool Success,
    DateTime RanUtc, double? DurationSeconds, string FileName, string? FilePath, long Rows, long SizeBytes,
    DateTimeOffset? FileModifiedUtc, string? Error, string MatchedBy = ChainMatch.File, string? ObjectName = null);

/// <summary>
/// Where one record is in the whole chain: the file it came from, every run that handled that file on its way through
/// pre-ingestion and ingestion, and what the delivery ledger then did with the row. The stages before OSDU are read
/// from the platform's own record of processed files, so they are what actually ran, not a reconstruction.
/// </summary>
public sealed record DeliveryRecordChainDto(
    string? FileName, long? RowNumber, DateTime? RowUpdatedUtc, IReadOnlyList<DeliveryChainStageDto> Stages,
    bool FileKnown, string? Note);

/// <summary>
/// Assembles a record's chain from what the catalog recorded. A record carries the ingestion file its version was built
/// from (the ingestion table's lineage columns); every flow that handled a file of that name has a row in
/// <c>catalog.RunFile</c> against its run, so those runs are the chain, each named by its flow's kind.
///
/// An ingestion flow reads the table a pre-ingestion flow landed, not a file, so it records no file and a file name can
/// never name it. It is found the other way instead: the flows that write the record's own ingestion table are known
/// from the lineage every flow kind declares, and the row carries the moment it was last written. The run of one of
/// those flows that was executing at that moment is the run that loaded the row. Nothing here guesses: with no such
/// run, the stage is left out rather than filled with the table's latest load.
/// </summary>
internal static class RecordChain
{
    /// <summary>The most runs a chain shows per file. A file handled more often than this is shown newest first.</summary>
    public const int MaxStages = 50;

    /// <summary>
    /// What a flow kind means in the chain an operator reads. A pre-ingestion flow is one of SQLFlow's file kinds: it
    /// lands whatever a source system dropped, which is where a record enters the estate. Anything else is named by its
    /// own kind rather than forced into a stage it is not.
    /// </summary>
    public static string StageOf(string flowKind) => flowKind switch
    {
        "file" or "pre" or "csv" or "excel" or "xml" or "json" or "parquet" or "sftp" or "copy" => "pre-ingestion",
        "ing" => "ingestion",
        "delivery" => "delivery",
        "retrieval" => "retrieval",
        "cache" => "cache",
        _ => flowKind,
    };

    /// <summary>
    /// The chain of one record, newest first: the runs that handled its ingestion file, and the run that was writing
    /// its ingestion table when the row was stamped. A record whose origin file the ledger does not hold (a record
    /// planned before the origin was recorded, or one whose source reports no file) still names the run that loaded
    /// its row when there is one, and a note says what is missing.
    /// </summary>
    /// <param name="catalog">The platform's catalog, which holds the runs and the lineage.</param>
    /// <param name="record">The record whose chain is wanted.</param>
    /// <param name="sourceTable">
    /// The three-part name of the ingestion table the record's flow reads its records from, as the sync recorded it
    /// for the flow's interface. Without it the ingestion stage cannot be named, and the rest of the chain answers as
    /// before.
    /// </param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<DeliveryRecordChainDto> OfAsync(
        CatalogDbContext catalog, RecordState record, string? sourceTable, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(record);

        // The file the delivered version came from, or the one the waiting work will be built from.
        var fileName = record.SourceFileName ?? record.PendingSourceFileName;
        var rowNumber = record.SourceRowNumber ?? record.PendingSourceRowNumber;
        var updated = record.SourceUpdatedUtc ?? record.PendingSourceUpdatedUtc;
        var loading = await LoadingRunAsync(catalog, sourceTable, updated, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return new DeliveryRecordChainDto(
                null, rowNumber, updated, loading is null ? [] : [loading], FileKnown: false,
                loading is null
                    ? "The ledger holds no ingestion file for this record, and no recorded run was writing its table when the row was stamped. A record gets its file when its ingestion table reports one."
                    : "The ledger holds no ingestion file for this record, so the run that landed it cannot be named. The run that loaded the row into its table is named above.");
        }

        var files = await catalog.RunFiles.AsNoTracking()
            .Where(f => f.Name == fileName)
            .Join(
                catalog.Runs.AsNoTracking(),
                f => f.RunId,
                r => r.RunId,
                (f, r) => new { File = f, Run = r })
            .OrderByDescending(x => x.Run.WrittenUtc)
            .Take(MaxStages)
            .ToListAsync(ct).ConfigureAwait(false);
        if (files.Count == 0)
        {
            return new DeliveryRecordChainDto(
                fileName, rowNumber, updated, loading is null ? [] : [loading], FileKnown: false,
                "No run in the catalog recorded processing a file of this name, so the run that landed it cannot be named. The row did reach the ingestion table, which is where this record's file and row were read from: the run may have been pruned, it ran before this estate recorded processed files, or the file was loaded outside a platform run.");
        }

        var pipelineIds = files.Select(x => x.Run.PipelineId).Distinct().ToList();
        var waves = await catalog.Pipelines.AsNoTracking()
            .Where(p => pipelineIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Wave })
            .ToDictionaryAsync(p => p.Id, p => p.Wave, ct).ConfigureAwait(false);

        var stages = files.Select(x => new DeliveryChainStageDto(
            StageOf(x.Run.FlowKind),
            x.Run.RunId,
            x.Run.PipelineId,
            x.Run.FlowName,
            x.Run.FlowKind,
            waves.GetValueOrDefault(x.Run.PipelineId, -1),
            x.Run.Status,
            x.Run.Success,
            x.Run.WrittenUtc,
            x.Run.DurationSeconds,
            x.File.Name,
            x.File.Path,
            x.File.Rows,
            x.File.SizeBytes,
            x.File.Modified,
            x.Run.Error)).ToList();

        // The ingestion run processed no file, so it is never among the ones matched by name; it takes its place in
        // the chain by the time it ran, like every other stage.
        if (loading is not null && !stages.Exists(s => s.Stage == "ingestion"))
        {
            stages.Add(loading);
            stages = [.. stages.OrderByDescending(s => s.RanUtc)];
        }

        return new DeliveryRecordChainDto(fileName, rowNumber, updated, stages, FileKnown: true, null);
    }

    /// <summary>
    /// The run that loaded the record's row into its ingestion table: of the flows that write
    /// <paramref name="sourceTable"/> (what the lineage of every flow kind declares), the run that was executing at
    /// <paramref name="rowUpdatedUtc"/>, the moment the table stamped the row. A run whose window covers that moment is
    /// the run that wrote it, so this names a fact rather than the table's latest load; with no such run, or with no
    /// table or moment to go on, there is no ingestion stage.
    /// </summary>
    private static async Task<DeliveryChainStageDto?> LoadingRunAsync(
        CatalogDbContext catalog, string? sourceTable, DateTime? rowUpdatedUtc, CancellationToken ct)
    {
        if (sourceTable is null || rowUpdatedUtc is not { } stamped || ObjectSuffix(sourceTable) is not { } suffix)
        {
            return null;
        }

        var writers = await catalog.LineageEdges.AsNoTracking()
            .Where(e => e.PipelineId != null && (e.Relation == "Writes" || e.Relation == "Creates") && e.ObjectKey.EndsWith(suffix))
            .Select(e => new { PipelineId = e.PipelineId!.Value, e.ObjectName })
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        if (writers.Count == 0)
        {
            return null;
        }

        var ids = writers.Select(w => w.PipelineId).Distinct().ToList();
        var run = await catalog.Runs.AsNoTracking()
            .Where(r => ids.Contains(r.PipelineId) && r.StartUtc != null && r.StartUtc <= stamped && (r.EndUtc == null || r.EndUtc >= stamped))
            .OrderByDescending(r => r.StartUtc)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (run is null)
        {
            return null;
        }

        var wave = await catalog.Pipelines.AsNoTracking()
            .Where(p => p.Id == run.PipelineId)
            .Select(p => (int?)p.Wave)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return new DeliveryChainStageDto(
            StageOf(run.FlowKind), run.RunId, run.PipelineId, run.FlowName, run.FlowKind, wave ?? -1, run.Status,
            run.Success, run.WrittenUtc, run.DurationSeconds, string.Empty, null, run.RowsLoaded ?? 0, 0, null,
            run.Error, ChainMatch.Table, writers.Find(w => w.PipelineId == run.PipelineId)?.ObjectName ?? sourceTable);
    }

    /// <summary>
    /// The tail a lineage object key carries for a table: <c>|database|schema|table</c>, folded the way the catalog
    /// folds it. A name that is not three parts (a table named without its database, which lineage cannot place)
    /// matches nothing rather than matching every table of that name.
    /// </summary>
    private static string? ObjectSuffix(string threePartName)
    {
        var parts = threePartName.Split('.', StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
        {
            return null;
        }

        var unbracketed = parts.Select(part => part.Trim('[', ']').ToLowerInvariant()).ToList();
        return unbracketed.Exists(string.IsNullOrEmpty) ? null : "|" + string.Join('|', unbracketed);
    }
}
