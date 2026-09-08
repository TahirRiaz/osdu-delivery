using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Intake;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Execution;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// Runs one delivery flow document as one platform run: the executor behind the platform's
/// <see cref="DocumentExecutor"/>. The run's parameters select the operation (deliver, verify, plan, known-state)
/// and its scope (a submission to re-run, the records to redeliver or verify); the engine's log becomes the run
/// log and the live trace; the outcome becomes <c>run.json</c> and the run row's projected counts. Every ledger
/// activity and attempt the run writes carries the run id, so a record's history links back to the run that
/// produced it and the run links forward to what it did to each record.
/// </summary>
public sealed class DeliveryExecutor : IFlowDocumentExecutor
{
    /// <summary>Records verified per verify run; the next run picks up where the ordering by last-verified left off.</summary>
    public const int VerifyBatch = 5000;

    /// <summary>A verify run skips records verified more recently than this unless forced.</summary>
    public static readonly TimeSpan VerifyInterval = TimeSpan.FromHours(24);

    private readonly IServiceProvider _provider;

    public DeliveryExecutor(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
    }

    public bool CanExecute(FlowDocument document) => document is DeliveryFlowDocument;

    public async Task<DocumentExecutionResult> ExecuteAsync(FlowDocument document, string flowFile, DocumentExecutionOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowFile);
        ArgumentNullException.ThrowIfNull(options);
        if (document is not DeliveryFlowDocument delivery)
        {
            throw new SqlFlowException($"The delivery executor cannot run a '{document.Kind}' document.");
        }

        // The file the node materialized is where the repository layout (mappings, snapshots) is resolved from.
        var flow = delivery.Flow with { SourcePath = Path.GetFullPath(flowFile) };
        var parameters = options.Parameters;
        parameters.Validate();
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
        LogStart(log, flow.Name, parameters.Operation.ToLowerInvariant(), parameters.Describe(), actor, runId);
        try
        {
            result = await ExecuteOperationAsync(context, flow, parameters, runId, actor, runLogger, log, ct).ConfigureAwait(false);
            success = true;
            LogDone(log, parameters.Operation.ToLowerInvariant(), stopwatch.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException or InvalidOperationException or JsonException or UnauthorizedAccessException)
        {
            error = SecretHygiene.RedactedMessage(ex);
            LogFailed(log, parameters.Operation.ToLowerInvariant(), error);
            result = new OperationFailure(parameters.Operation.ToLowerInvariant(), error);
        }

        stopwatch.Stop();
        var artifact = RunArtifacts.Artifact(FlowDefinition.FlowTypeName, flow.Name, runId, success, error, result, events.Records);
        var directory = RunArtifacts.Write(flowFile, flow.Name, runId, artifact, runLogger.Render(), null, warningSink);
        return new DocumentExecutionResult
        {
            FlowName = flow.Name,
            FlowKind = FlowDefinition.FlowTypeName,
            Success = success,
            Error = error,
            RunId = runId,
            RunDirectory = directory,
            DurationSeconds = stopwatch.Elapsed.TotalSeconds,
            Result = result,
        };
    }

    private static async Task<object> ExecuteOperationAsync(EngineContext context, FlowDefinition flow, RunParameters parameters, Guid runId, string actor, RunLogger runLogger, ILogger log, CancellationToken ct)
    {
        var operation = parameters.Operation.ToLowerInvariant();
        var drop = parameters.Drop;
        var values = parameters.Values;

        // A submission re-run executes that submission's drop with the parameters it was received with, plus any
        // override the trigger carried; the intake's idempotency then re-plans it.
        SubmissionState? submission = null;
        if (parameters.SubmissionId is { } submissionId)
        {
            var ledger = context.Ledger ?? throw new DeliveryException("Re-running a submission needs the ledger, which lives in the catalog database.");
            submission = await ledger.GetSubmissionAsync(submissionId, ct).ConfigureAwait(false)
                ?? throw new DeliveryException($"Submission {submissionId:D} is not in the ledger.");
            if (submission.FlowId != flow.Id)
            {
                throw new DeliveryException($"Submission {submissionId:D} belongs to flow '{submission.FlowName}', not '{flow.Name}'.");
            }

            drop ??= submission.DropLocation;
            values = Merge(ParseValues(submission.ParametersJson), parameters.Values);
            LogSubmission(log, submissionId, submission.DropLocation);
        }

        // Deliver and plan read the drop and render; verify and known-state only touch the target and the ledger,
        // so they need neither the flow's parameters nor its render inputs.
        using var runtime = operation is RunParameters.DeliverOperation or RunParameters.PlanOperation
            ? await FlowRuntime.CreateAsync(context, flow, values, drop, ct).ConfigureAwait(false)
            : FlowRuntime.ForTarget(context, flow);
        runtime.Actor = actor;
        runtime.RunId = runId;
        runtime.ActivityLog = runLogger.Render;
        var keys = parameters.RecordKeys.Select(k => new DeliveryKey(k)).ToList();

        switch (operation)
        {
            case RunParameters.DeliverOperation:
                if (keys.Count > 0)
                {
                    // A scoped redelivery: forget what OSDU holds for these records, then let the plan re-send them.
                    var marked = await runtime.RedeliverAsync(keys, RedeliverScope.All, ct).ConfigureAwait(false);
                    LogRedeliver(log, marked, keys.Count);
                }

                var run = await runtime.RunAsync(force: parameters.Force || submission is not null, ct).ConfigureAwait(false);
                LogOutcome(log, SubmissionIntake.Summarize(run.Submission));
                return DeliverOutcome.From(run, runtime.DropLocation);

            case RunParameters.PlanOperation:
                var plan = await runtime.PlanAsync(parameters.Force, ct).ConfigureAwait(false);
                LogOutcome(log, plan.SkippedWholeRun
                    ? $"plan: whole run skipped, {plan.SkipReason}"
                    : $"plan: {plan.Entries.Count} record(s), {plan.Deliveries} to deliver, {plan.Skips} unchanged, {plan.Holds} held, {plan.Blocked} blocked");
                return PlanOutcome.From(plan, runtime.DropLocation);

            case RunParameters.VerifyOperation:
                var reconcile = flow.Verify.Reconcile;
                var summary = await runtime.VerifyAsync(VerifyBatch, parameters.Force ? null : VerifyInterval, reconcile, keys.Count == 0 ? null : keys, ct).ConfigureAwait(false);
                LogOutcome(log, $"verify: {summary}");
                return new VerifyRunOutcome(operation, summary.Checked, summary.Matched, summary.Drifted, summary.Missing, summary.Errors, reconcile);

            case RunParameters.KnownStateOperation:
                // The run names the location, or the flow declares one (with the flow parameters substituted).
                var to = parameters.PublishTo
                    ?? (flow.Source.KnownState is null ? null : FlowParameters.KnownStateLocation(flow, FlowParameters.Resolve(flow, values)))
                    ?? throw new DeliveryException("A known-state publication needs a location: publishTo on the run, or source.knownState on the flow.");
                var published = await runtime.PublishKnownStateAsync(to, ct).ConfigureAwait(false);
                LogOutcome(log, $"known-state: published {published} record(s) to {to}");
                return new KnownStateOutcome(operation, to, published);

            default:
                throw new SqlFlowException($"Unknown operation '{parameters.Operation}'.");
        }
    }

    private static IReadOnlyDictionary<string, string> ParseValues(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            throw new DeliveryException($"The submission's recorded parameters are not valid JSON: {ex.Message}", ex);
        }
    }

    private static IReadOnlyDictionary<string, string> Merge(IReadOnlyDictionary<string, string> recorded, IReadOnlyDictionary<string, string> overrides)
    {
        var merged = new Dictionary<string, string>(recorded, StringComparer.Ordinal);
        foreach (var (name, value) in overrides)
        {
            merged[name] = value;
        }

        return merged;
    }

    private static void LogStart(ILogger log, string flow, string operation, string parameters, string actor, Guid runId)
        => log.LogInformation("delivery flow '{Flow}': {Operation} (parameters: {Parameters}) requested by {Actor}, run {RunId}", flow, operation, parameters, actor, runId);

    private static void LogSubmission(ILogger log, Guid submissionId, string drop)
        => log.LogInformation("re-running submission {SubmissionId} from its drop {Drop}", submissionId, drop);

    private static void LogRedeliver(ILogger log, int marked, int requested)
        => log.LogInformation("marked {Marked} of {Requested} record(s) for redelivery", marked, requested);

    private static void LogOutcome(ILogger log, string outcome)
        => log.LogInformation("{Outcome}", outcome);

    private static void LogDone(ILogger log, string operation, double seconds)
        => log.LogInformation("{Operation} completed in {Seconds:0.###}s", operation, seconds);

    private static void LogFailed(ILogger log, string operation, string error)
        => log.LogError("{Operation} failed: {Error}", operation, error);
}

/// <summary>The <c>result</c> of a deliver run: what the intake planned and what the drain did (the counts the run row projects).</summary>
public sealed record DeliverOutcome(
    string Operation,
    Guid SubmissionId,
    string Drop,
    string Status,
    int RecordCount,
    int Planned,
    int SkippedUnchanged,
    int Blocked,
    int Delivered,
    int Held,
    int Failed,
    int Retried,
    bool NothingToDo,
    string? Error)
{
    public static DeliverOutcome From(RunResult run, string drop)
    {
        ArgumentNullException.ThrowIfNull(run);
        var s = run.Submission;
        return new DeliverOutcome(
            RunParameters.DeliverOperation, s.SubmissionId, drop, s.Status.ToString().ToLowerInvariant(), s.RecordCount,
            s.Planned, s.SkippedUnchanged, s.Blocked, s.Delivered, s.Held, s.Failed, run.Work.Retried, run.Intake.NothingToDo, s.Error);
    }
}

/// <summary>The <c>result</c> of a plan run: what a deliver would do, record by record in the run log, in counts here.</summary>
public sealed record PlanOutcome(
    string Operation,
    string Drop,
    int Records,
    int Deliveries,
    int Skips,
    int Holds,
    int Blocked,
    bool SkippedWholeRun,
    string? SkipReason,
    IReadOnlyList<string> Issues)
{
    public static PlanOutcome From(DeliveryPlan plan, string drop)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new PlanOutcome(
            RunParameters.PlanOperation, drop, plan.Entries.Count, plan.Deliveries, plan.Skips, plan.Holds, plan.Blocked,
            plan.SkippedWholeRun, plan.SkipReason, plan.Issues.Select(i => i.ToString()).ToList());
    }
}

/// <summary>The <c>result</c> of a verify run.</summary>
public sealed record VerifyRunOutcome(string Operation, int Checked, int Matched, int Drifted, int Missing, int Errors, bool Reconcile);

/// <summary>The <c>result</c> of a known-state publication.</summary>
public sealed record KnownStateOutcome(string Operation, string PublishedTo, int Records);

/// <summary>The <c>result</c> of a run that failed before producing an outcome.</summary>
public sealed record OperationFailure(string Operation, string Error);
