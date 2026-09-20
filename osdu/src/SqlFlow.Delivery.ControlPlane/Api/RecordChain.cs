using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// One run that handled the file a record came from, as a stage of its chain: which flow, of which kind, when it ran,
/// how it ended, and what it read or wrote of that file. The flow kinds are SQLFlow's own, so a stage says
/// pre-ingestion or ingestion in the estate's vocabulary rather than in a name this module invented.
/// </summary>
public sealed record DeliveryChainStageDto(
    string Stage, Guid RunId, Guid PipelineId, string FlowName, string FlowKind, int Wave, string Status, bool Success,
    DateTime RanUtc, double? DurationSeconds, string FileName, string? FilePath, long Rows, long SizeBytes,
    DateTimeOffset? FileModifiedUtc, string? Error);

/// <summary>
/// Where one record is in the whole chain: the file it came from, every run that handled that file on its way through
/// pre-ingestion and ingestion, and what the delivery ledger then did with the row. The stages before OSDU are read
/// from the platform's own record of processed files, so they are what actually ran, not a reconstruction.
/// </summary>
public sealed record DeliveryRecordChainDto(
    string? FileName, long? RowNumber, DateTime? RowUpdatedUtc, IReadOnlyList<DeliveryChainStageDto> Stages,
    bool FileKnown, string? Note);

/// <summary>
/// Assembles a record's chain from the catalog's processed files. A record carries the ingestion file its version was
/// built from (the ingestion table's lineage columns); every flow that handled a file of that name has a row in
/// <c>catalog.RunFile</c> against its run, so the chain is that set of runs in the order they ran, each named by its
/// flow's kind. Nothing here guesses: a file no run recorded gives an empty chain and says so.
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
    /// The chain of one record: the runs that handled its ingestion file, newest first, with the flow and kind of each.
    /// A record whose origin file the ledger does not hold (a record planned before the origin was recorded, or one
    /// whose source reports no file) answers with no stages and a note saying why.
    /// </summary>
    public static async Task<DeliveryRecordChainDto> OfAsync(CatalogDbContext catalog, RecordState record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(record);

        // The file the delivered version came from, or the one the waiting work will be built from.
        var fileName = record.SourceFileName ?? record.PendingSourceFileName;
        var rowNumber = record.SourceRowNumber ?? record.PendingSourceRowNumber;
        var updated = record.SourceUpdatedUtc ?? record.PendingSourceUpdatedUtc;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return new DeliveryRecordChainDto(
                null, rowNumber, updated, [], FileKnown: false,
                "The ledger holds no ingestion file for this record, so the runs that carried it cannot be named. A record gets its file when its ingestion table reports one.");
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
                fileName, rowNumber, updated, [], FileKnown: false,
                "No run in the catalog recorded processing a file of this name. The pre-ingestion and ingestion runs that carried it may have been pruned, or they ran before this estate recorded processed files.");
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

        return new DeliveryRecordChainDto(fileName, rowNumber, updated, stages, FileKnown: true, null);
    }
}
