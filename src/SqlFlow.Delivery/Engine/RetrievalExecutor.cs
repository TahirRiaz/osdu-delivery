using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Retrieval;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using SqlFlow.Execution;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// Runs one retrieval flow document as one platform run. The run's parameters select the operation: retrieve (the
/// default; a run triggered without an operation retrieves) or plan (count what the query matches, write nothing);
/// force restarts an incremental flow at its declared start. The engine's log becomes the run log and the live
/// trace, the outcome becomes <c>run.json</c>, and the ledger row the run opens carries the run id.
/// </summary>
public sealed class RetrievalExecutor : IFlowDocumentExecutor
{
    private readonly IServiceProvider _provider;

    public RetrievalExecutor(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
    }

    public bool CanExecute(FlowDocument document) => document is RetrievalFlowDocument;

    public async Task<DocumentExecutionResult> ExecuteAsync(FlowDocument document, string flowFile, DocumentExecutionOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowFile);
        ArgumentNullException.ThrowIfNull(options);
        if (document is not RetrievalFlowDocument retrieval)
        {
            throw new SqlFlowException($"The retrieval executor cannot run a '{document.Kind}' document.");
        }

        var flow = retrieval.Flow with { SourcePath = Path.GetFullPath(flowFile) };
        var parameters = options.Parameters;
        parameters.Validate();
        var operation = Operation(parameters);
        var runId = options.RunId ?? Guid.CreateVersion7();
        var actor = string.IsNullOrWhiteSpace(options.Actor) ? "unknown" : options.Actor.Trim();
        var (runLogger, events, _) = RunArtifacts.BuildEventPlumbing(options, flow.Name);
        var loggers = new RunLogLoggerFactory(runLogger, events, runId, flow.Name);
        var log = loggers.CreateLogger("run");
        var context = _provider.GetRequiredService<EngineContext>().WithLoggers(loggers);
        var warningSink = _provider.GetService<DocumentExecutor>()?.WarningSink;

        var stopwatch = Stopwatch.StartNew();
        object result;
        string? error = null;
        var success = false;
        LogStart(log, flow.Name, operation, parameters.Describe(), actor, runId);
        try
        {
            result = await ExecuteOperationAsync(context, flow, operation, parameters, runId, actor, log, ct).ConfigureAwait(false);
            success = true;
            LogDone(log, operation, stopwatch.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException or InvalidOperationException or JsonException or UnauthorizedAccessException)
        {
            error = SecretHygiene.RedactedMessage(ex);
            LogFailed(log, operation, error);
            result = new OperationFailure(operation, error);
        }

        stopwatch.Stop();
        var artifact = RunArtifacts.Artifact(RetrievalDefinition.FlowTypeName, flow.Name, runId, success, error, result, events.Records);
        var directory = RunArtifacts.Write(flowFile, flow.Name, runId, artifact, runLogger.Render(), null, warningSink);
        return new DocumentExecutionResult
        {
            FlowName = flow.Name,
            FlowKind = RetrievalDefinition.FlowTypeName,
            Success = success,
            Error = error,
            RunId = runId,
            RunDirectory = directory,
            DurationSeconds = stopwatch.Elapsed.TotalSeconds,
            Result = result,
        };
    }

    /// <summary>The operation a retrieval flow runs: retrieve (also for the platform's default, deliver) or plan.</summary>
    public static string Operation(RunParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var operation = parameters.Operation.ToLowerInvariant();
        return operation switch
        {
            RunParameters.RetrieveOperation or RunParameters.DeliverOperation => RunParameters.RetrieveOperation,
            RunParameters.PlanOperation => RunParameters.PlanOperation,
            _ => throw new SqlFlowException($"A retrieval flow runs the {RunParameters.RetrieveOperation} and {RunParameters.PlanOperation} operations; '{parameters.Operation}' is not one of them."),
        };
    }

    private static async Task<object> ExecuteOperationAsync(EngineContext context, RetrievalDefinition flow, string operation, RunParameters parameters, Guid runId, string actor, ILogger log, CancellationToken ct)
    {
        if (parameters.SubmissionId is not null || parameters.RecordKeys.Count > 0 || parameters.Partitions.Count > 0 || !string.IsNullOrWhiteSpace(parameters.Drop))
        {
            throw new SqlFlowException("A retrieval flow takes no drop, submission, record or partition scope; only the flow's parameter values and force.");
        }

        var values = FlowParameters.Resolve(flow.Parameters, flow.SourcePath ?? flow.Name, parameters.Values);
        using var http = new HttpRuntime(flow.Reliability, context.Secrets, context.Time, allowLoopback: EngineContext.LoopbackAllowed);
        var client = await ProtocolFactory.ClientAsync(http, flow.Source.Endpoint, flow.Source.Auth, flow.Source.Headers, context.Secrets, ct).ConfigureAwait(false);
        var runner = new RetrievalRunner(flow, values, client, context.Stores, context.Ledger, context.Time, context.Loggers.CreateLogger<RetrievalRunner>());

        if (operation == RunParameters.PlanOperation)
        {
            var (window, estimates) = await runner.EstimateAsync(parameters.Force, ct).ConfigureAwait(false);
            foreach (var estimate in estimates)
            {
                log.LogInformation("plan {Kind}: {Total} matching record(s) (query: {Query})", estimate.Kind, estimate.TotalCount, estimate.Query ?? "none");
            }

            var total = estimates.Sum(e => e.TotalCount);
            log.LogInformation("plan: {Total} record(s) would be retrieved into {Location}", total, runner.Location(runId, context.Time.GetUtcNow().UtcDateTime));
            return new RetrievalPlanOutcome(RunParameters.PlanOperation, window?.Field, window?.From, window?.To, runner.Location(runId, context.Time.GetUtcNow().UtcDateTime), estimates, total);
        }

        var result = await runner.RunAsync(runId, actor, parameters.Force, ct).ConfigureAwait(false);
        return RetrieveOutcome.From(result);
    }

    private static void LogStart(ILogger log, string flow, string operation, string parameters, string actor, Guid runId)
        => log.LogInformation("retrieval flow '{Flow}': {Operation} (parameters: {Parameters}) requested by {Actor}, run {RunId}", flow, operation, parameters, actor, runId);

    private static void LogDone(ILogger log, string operation, double seconds)
        => log.LogInformation("{Operation} completed in {Seconds:0.###}s", operation, seconds);

    private static void LogFailed(ILogger log, string operation, string error)
        => log.LogError("{Operation} failed: {Error}", operation, error);
}

/// <summary>The <c>result</c> of a retrieve run: where the files went, the window, and the counts the ledger row carries.</summary>
public sealed record RetrieveOutcome(
    string Operation,
    long RetrievalId,
    string Location,
    string? Manifest,
    string? WindowField,
    DateTime? WindowFrom,
    DateTime? WindowTo,
    long Records,
    int Files,
    long Bytes,
    bool NothingToDo,
    IReadOnlyList<RetrievedKind> Kinds)
{
    public static RetrieveOutcome From(RetrievalResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new RetrieveOutcome(
            RunParameters.RetrieveOperation, result.RetrievalId, result.Location, result.ManifestLocation, result.Window?.Field, result.Window?.From, result.Window?.To,
            result.Records, result.Files, result.Bytes, result.NothingToDo, result.Kinds);
    }
}

/// <summary>The <c>result</c> of a plan run on a retrieval flow: what the query matches per kind, and where a retrieve would write.</summary>
public sealed record RetrievalPlanOutcome(
    string Operation, string? WindowField, DateTime? WindowFrom, DateTime? WindowTo, string Location, IReadOnlyList<RetrievalEstimate> Kinds, long Total);
