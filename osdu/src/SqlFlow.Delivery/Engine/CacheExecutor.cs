using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Model;
using SqlFlow.Execution;
using SqlFlow.Orchestration;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// Runs one cache flow document as one platform run. The run's parameters select the operation: refresh (the default; a
/// run triggered without an operation, and a scheduled run, refreshes) or plan (count what each type's search matches,
/// write nothing). The engine's log becomes the run log and the live trace, and the outcome becomes <c>run.json</c>; the
/// version a refresh writes carries the run id, so every cached value can be traced to the run that captured it.
/// </summary>
public sealed class CacheExecutor : IFlowDocumentExecutor
{
    private readonly IServiceProvider _provider;

    public CacheExecutor(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
    }

    public bool CanExecute(RegisteredFlowDocument document) => document is CacheFlowDocument;

    public async Task<DocumentExecutionResult> ExecuteAsync(RegisteredFlowDocument document, string flowFile, DocumentExecutionOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowFile);
        ArgumentNullException.ThrowIfNull(options);
        if (document is not CacheFlowDocument cache)
        {
            throw new SqlFlowException($"The cache executor cannot run a '{document.Kind}' document.");
        }

        var flow = cache.Flow with { SourcePath = Path.GetFullPath(flowFile) };
        var parameters = options.Parameters;
        parameters.Validate();
        var operation = Operation(parameters);
        var runId = options.RunId ?? Guid.CreateVersion7();
        var actor = string.IsNullOrWhiteSpace(options.Actor) ? "unknown" : options.Actor.Trim();
        var (runLogger, events, _) = RunArtifacts.BuildEventPlumbing(options, flow.Name);
        var loggers = new RunLogLoggerFactory(runLogger, events, runId, flow.Name);
        var log = loggers.CreateLogger("run");
        // A cache and a retrieval flow name their platform and partition the same way a delivery flow does, so a run
        // of one resolves them from the central configuration the control plane supplied before the node's own.
        var context = _provider.GetRequiredService<EngineContext>()
            .WithLoggers(loggers)
            .WithSuppliedReferences(DeliveryRunPayload.Parse(options.Parameters).References);
        var warningSink = options.Echo;

        var stopwatch = Stopwatch.StartNew();
        object result;
        string? error = null;
        var success = false;
        log.LogInformation("cache flow '{Flow}': {Operation} (parameters: {Parameters}) requested by {Actor}, run {RunId}", flow.Name, operation, parameters.Describe(), actor, runId);
        try
        {
            result = await ExecuteOperationAsync(context, flow, operation, parameters, runId, actor, log, ct).ConfigureAwait(false);
            success = true;
            log.LogInformation("{Operation} completed in {Seconds:0.###}s", operation, stopwatch.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The run boundary: every failure ends the run as a recorded one. An unexpected kind is a defect, so its
            // stack goes to the run log as well.
            error = RunFailure.Describe(ex);
            log.LogError(RunFailure.IsExpected(ex) ? null : ex, "{Operation} failed: {Error}", operation, error);
            result = new OperationFailure(operation, error);
        }

        stopwatch.Stop();
        var artifact = RunArtifacts.Artifact(CacheDefinition.FlowTypeName, flow.Name, runId, success, error, result, events.Records);
        var directory = RunArtifacts.Write(flowFile, flow.Name, runId, artifact, runLogger.Render(), null, warningSink);
        return new DocumentExecutionResult
        {
            FlowName = flow.Name,
            FlowKind = CacheDefinition.FlowTypeName,
            Success = success,
            Error = error,
            RunId = runId,
            RunDirectory = directory,
            DurationSeconds = stopwatch.Elapsed.TotalSeconds,
            Result = result,
        };
    }

    /// <summary>The operation a cache flow runs: refresh (its default) or plan.</summary>
    public static string Operation(RunParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return (parameters.Operation ?? DeliveryOperations.Refresh) switch
        {
            DeliveryOperations.Refresh => DeliveryOperations.Refresh,
            DeliveryOperations.Plan => DeliveryOperations.Plan,
            var other => throw new SqlFlowException(
                $"A cache flow runs the {DeliveryOperations.Refresh} and {DeliveryOperations.Plan} operations; '{other}' is not one of them."),
        };
    }

    private static async Task<object> ExecuteOperationAsync(
        EngineContext context, CacheDefinition flow, string operation, RunParameters parameters, Guid runId, string actor, ILogger log, CancellationToken ct)
    {
        if (parameters.Payload is not null)
        {
            throw new SqlFlowException("A cache flow takes no payload: a refresh sweeps every declared type in full, so there is no submission, record or slice to name.");
        }

        var values = FlowParameters.Resolve(flow.Parameters, flow.SourcePath ?? flow.Name, parameters.Values);
        var refresher = new CacheRefresher(context, log);
        return operation == DeliveryOperations.Plan
            ? await refresher.PlanAsync(flow, values, ct).ConfigureAwait(false)
            : await refresher.RefreshAsync(flow, values, runId, actor, ct).ConfigureAwait(false);
    }
}
