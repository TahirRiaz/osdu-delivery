using SqlFlow.Core.Acquire;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Runs;

namespace SqlFlow.Acquire.Engine;

/// <summary>
/// Executes an acquisition flow (flowType: acq): resolves the incremental watermark from the run history, runs the
/// engine, and logs the run boundary through the shared run-log seam. It never throws for a fetch failure - the
/// engine returns a failed/partial result - so a batch member behaves exactly like a directly-invoked flow. The one
/// code path for CLI, control plane, and worker.
/// </summary>
public sealed class AcquireFlowRunner
{
    private readonly AcquireEngine _engine;

    public AcquireFlowRunner(AcquireEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
    }

    public async Task<AcquireRunResult> RunAsync(
        AcquireFlow flow, string anchorDirectory, IngestionRunOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentException.ThrowIfNullOrWhiteSpace(anchorDirectory);
        ArgumentNullException.ThrowIfNull(options);
        var events = options.Events ?? NullRunEventSink.Instance;

        events.Log(RunLogLevel.Info, "run.start",
            $"acquire '{flow.Name}' (flow {flow.FlowId}, transport {flow.Source.Transport}) -> {flow.Landing.Target}");

        // The typed per-run contract maps onto the acquisition's native knobs: a backfill window re-windows every
        // date-window iteration, and a full load ignores the stored watermark (the flow re-fetches from its
        // declared bounds / seed). A file pattern has no acquisition meaning and is surfaced by the executor.
        var parameters = options.Parameters;
        var fullLoad = parameters.FullLoad;
        var priorWatermark = flow.Incremental is not null && !fullLoad
            ? AcquireWatermarkHistory.LastWatermark(anchorDirectory, flow.Name)
            : null;
        if (fullLoad && flow.Incremental is not null)
        {
            events.Log(RunLogLevel.Info, "params", "full load requested: the stored watermark is ignored for this run.");
        }

        var overrides = new AcquireRunOverrides
        {
            WindowFrom = parameters.BackfillFrom is { } from ? new DateTimeOffset(DateTime.SpecifyKind(from, DateTimeKind.Utc)) : null,
            WindowTo = parameters.BackfillTo is { } to ? new DateTimeOffset(DateTime.SpecifyKind(to, DateTimeKind.Utc)) : null,
            // A backfill re-lands every re-fetched file (fresh timestamp) rather than skipping byte-identical payloads,
            // so the downstream incremental flows pick them up again.
            ReprocessFiles = parameters.ReprocessFiles,
        };

        var runId = options.RunId ?? Guid.NewGuid();
        var result = await _engine.RunAsync(flow, runId, events, priorWatermark, ct, overrides).ConfigureAwait(false);

        events.Log(RunLogLevel.Info, "run.end", result.Success
            ? $"SUCCESS in {result.DurationSeconds}s: {result.FilesWritten} file(s), {result.BytesWritten} byte(s), {result.PagesFetched} page(s) across {result.Iterations} iteration(s)"
            : $"FAILED after {result.DurationSeconds}s ({result.FilesWritten} file(s) landed before failure): {result.Error}");

        return result;
    }
}
