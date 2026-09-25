using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.FanOut;
using SqlFlow.Delivery.Engine.Intake;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Source;
using SqlFlow.Execution;
using SqlFlow.Orchestration;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// Runs one delivery flow document as one platform run. The run's operation (deliver, plan, intake, drain, verify,
/// replan) and its payload (the submission it works on, the records it is scoped to, the key slices a member plans)
/// decide what it reads out of the flow's ingestion tables and what it does with it; the engine's log becomes the run
/// log and the live trace; the outcome becomes <c>run.json</c> and the run row's projected counts. Every ledger activity
/// and attempt the run writes carries the run id, so a record's history links back to the run that produced it and the
/// run links forward to what it did to each record.
/// </summary>
public sealed class DeliveryExecutor : IFlowDocumentExecutor
{
    /// <summary>Records verified per verify run; the next run picks up where the ordering by last-verified left off.</summary>
    public const int VerifyBatch = 5000;

    /// <summary>A verify run skips records verified more recently than this unless forced.</summary>
    public static readonly TimeSpan VerifyInterval = TimeSpan.FromHours(24);

    /// <summary>How many plan entries a plan run writes to its trace before summarising the rest.</summary>
    public const int PlanEntriesLogged = 200;

    /// <summary>The records the ledger asked to plan again that one deliver run takes in its key-scoped pass.</summary>
    public const int RequestedPerRun = 5000;

    private readonly IServiceProvider _provider;

    public DeliveryExecutor(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
    }

    public bool CanExecute(RegisteredFlowDocument document) => document is DeliveryFlowDocument;

    public async Task<DocumentExecutionResult> ExecuteAsync(RegisteredFlowDocument document, string flowFile, DocumentExecutionOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowFile);
        ArgumentNullException.ThrowIfNull(options);
        if (document is not DeliveryFlowDocument delivery)
        {
            throw new SqlFlowException($"The delivery executor cannot run a '{document.Kind}' document.");
        }

        // The file the node materialized is where the repository layout (mappings, snapshots) is resolved from.
        var source = delivery.Source.WithSourcePath(Path.GetFullPath(flowFile));
        var parameters = options.Parameters;
        parameters.Validate();
        var operation = DeliveryOperations.Of(parameters);
        var payload = DeliveryRunPayload.Parse(parameters);
        payload.Validate(operation);

        var runId = options.RunId ?? Guid.CreateVersion7();
        var actor = string.IsNullOrWhiteSpace(options.Actor) ? "unknown" : options.Actor.Trim();
        var (runLogger, events, _) = RunArtifacts.BuildEventPlumbing(options, source.Name);
        var loggers = new RunLogLoggerFactory(runLogger, events, runId, source.Name);
        var log = loggers.CreateLogger("run");
        var context = _provider.GetRequiredService<EngineContext>()
            .ForRun(loggers)
            .WithFanOut(options.FanOut is { } fanOut ? new RunFanOutDispatcher(fanOut) : null)
            // The control plane supplies what it holds centrally; the node answers the rest from its own environment.
            .WithSuppliedReferences(payload.References);

        var stopwatch = Stopwatch.StartNew();
        object result;
        string? error = null;
        var success = false;
        LogStart(log, source.Name, operation, parameters.Describe(), actor, runId);
        try
        {
            result = await ExecuteOperationAsync(context, source, operation, parameters, payload, runId, actor, runLogger, log, ct).ConfigureAwait(false);
            success = true;
            LogDone(log, operation, stopwatch.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (SourceRunIncompleteException incomplete)
        {
            // A source whose interfaces did not all complete ends failed, and its outcome still says what each one did.
            error = RunFailure.Describe(incomplete);
            LogFailed(log, operation, error, null);
            result = incomplete.Outcome;
        }
        catch (Exception ex)
        {
            // The run boundary: every failure ends the run as a recorded one. An unexpected kind is a defect, so its
            // stack goes to the run log as well.
            error = RunFailure.Describe(ex);
            LogFailed(log, operation, error, RunFailure.IsExpected(ex) ? null : ex);
            result = new OperationFailure(operation, error);
        }

        stopwatch.Stop();
        var artifact = RunArtifacts.Artifact(FlowDefinition.FlowTypeName, source.Name, runId, success, error, result, events.Records);
        var directory = RunArtifacts.Write(flowFile, source.Name, runId, artifact, runLogger.Render(), null, options.Echo);
        return new DocumentExecutionResult
        {
            FlowName = source.Name,
            FlowKind = FlowDefinition.FlowTypeName,
            Success = success,
            Error = error,
            RunId = runId,
            RunDirectory = directory,
            DurationSeconds = stopwatch.Elapsed.TotalSeconds,
            Result = result,
        };
    }

    /// <summary>True for the operations that read the ingestion tables and render.</summary>
    internal static bool ReadsSource(string operation)
        => operation is DeliveryOperations.Deliver or DeliveryOperations.Plan or DeliveryOperations.Intake or DeliveryOperations.Replan;

    /// <summary>True for the operations that deliver records, which a failure guard watches.</summary>
    internal static bool Delivers(string operation)
        => operation is DeliveryOperations.Deliver or DeliveryOperations.Replan or DeliveryOperations.Drain;

    /// <summary>
    /// A run works on one interface when the flow is in the single form, when it names one, or when what it works on
    /// belongs to one (a submission, records, slices); otherwise it runs the source's interfaces through a
    /// <see cref="SourceRuntime"/>.
    /// </summary>
    private static async Task<object> ExecuteOperationAsync(
        EngineContext context, SourceDefinition source, string operation, RunParameters parameters, DeliveryRunPayload payload,
        Guid runId, string actor, RunLogger runLogger, ILogger log, CancellationToken ct)
    {
        var (flow, submission) = await TargetInterfaceAsync(context, source, operation, payload, ct).ConfigureAwait(false);
        if (flow is null)
        {
            var sources = new SourceRuntime(context, source)
            {
                Actor = actor,
                RunId = runId,
                ActivityLog = runLogger.Render,
                Values = parameters.Values,
            };
            return await sources.ExecuteAsync(operation, payload, ct).ConfigureAwait(false);
        }

        // A run that names a submission works on that submission's records with the parameter values it was registered
        // with, plus any override the trigger carried. A drain works on the submission's batches and reads no source.
        var values = parameters.Values;
        if (submission is not null)
        {
            values = Merge(ParseValues(submission.ParametersJson), parameters.Values);
            LogSubmission(log, submission.SubmissionId, submission.SourceObject);
        }

        // Deliver, plan, intake and replan read the ingestion tables and render; verify and drain only touch the target
        // and the ledger, so they need neither the flow's parameters nor its render inputs.
        var interfaceContext = flow.Interface is null ? context : context.ForInterface(flow.Interface);
        using var runtime = ReadsSource(operation)
            ? await FlowRuntime.CreateAsync(interfaceContext, flow, values, ct).ConfigureAwait(false)
            : FlowRuntime.ForTarget(interfaceContext, flow);
        runtime.Actor = actor;
        runtime.RunId = runId;
        runtime.ActivityLog = runLogger.Render;
        runtime.SourceDocument = source;
        if (runtime.ReadsSource)
        {
            await runtime.CheckRouteAsync(runtime.Mapping.Mapping.Kind, ct).ConfigureAwait(false);
        }

        LogRoute(log, operation, runtime);
        return await GuardedAsync(runtime, operation, token => ExecuteInterfaceAsync(runtime, operation, payload, submission, log, token), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The interface a run works on, and the submission it names, or no interface for a run of the whole source. A
    /// submission belongs to the interface whose ledger identity registered it, and a run naming records or slices has to
    /// name the interface they belong to when the source has several.
    /// </summary>
    private static async Task<(FlowDefinition? Flow, SubmissionState? Submission)> TargetInterfaceAsync(
        EngineContext context, SourceDefinition source, string operation, DeliveryRunPayload payload, CancellationToken ct)
    {
        var named = payload.Interface is { } name ? source.Interface(name) : null;
        SubmissionState? submission = null;
        if (payload.SubmissionId is { } submissionId)
        {
            // Read even for a drain, which needs nothing else of it: a submission is only ever worked on by the interface
            // whose ledger registered it.
            var ledger = context.Ledger ?? throw new DeliveryException(DeliveryServices.NoLedgerMessage);
            submission = await ledger.GetSubmissionAsync(submissionId, ct).ConfigureAwait(false)
                ?? throw new DeliveryException($"Submission {submissionId:D} is not in the ledger.");
        }

        var flow = named
            ?? (source.DeclaresInterfaces ? null : source.First)
            ?? (submission is null ? null : source.ByFlowId(submission.FlowId)
                ?? throw new DeliveryException($"Submission {submission.SubmissionId:D} belongs to flow '{submission.FlowName}', which is not an interface of '{source.Name}'."));

        if (flow is null)
        {
            if (payload.RecordKeys.Count > 0 || payload.Slices.Count > 0)
            {
                throw new DeliveryException(
                    $"Flow '{source.Name}' delivers {source.Interfaces.Count} interface(s) ({string.Join(", ", source.Names)}); a run on records or slices names the interface they belong to with 'interface' in its payload.");
            }

            source.Select(payload.Interfaces);
            return (null, null);
        }

        if (payload.Interfaces.Count > 0)
        {
            // The single form has no interfaces to select; this says so by name.
            source.Select(payload.Interfaces);
        }

        if (submission is not null && submission.FlowId != flow.Id)
        {
            throw new DeliveryException($"Submission {submission.SubmissionId:D} belongs to flow '{submission.FlowName}', not '{flow.Label}'.");
        }

        return (flow, operation == DeliveryOperations.Drain ? null : submission);
    }

    /// <summary>
    /// Runs one interface's operation under its failure guard when the operation delivers: the guard watches every
    /// record, and when it trips the work stops, hands its leases back and the interface ends stopped with the reason.
    /// </summary>
    internal static async Task<T> GuardedAsync<T>(FlowRuntime runtime, string operation, Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(work);
        if (!Delivers(operation))
        {
            return await work(ct).ConfigureAwait(false);
        }

        using var guard = new FailureGuard(runtime.Flow.FailWhen);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, guard.Token);
        runtime.Guard = guard;
        try
        {
            return await work(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (guard.Tripped && !ct.IsCancellationRequested)
        {
            throw new InterfaceStoppedException(runtime.Flow, guard.Reason!);
        }
        finally
        {
            runtime.Guard = null;
        }
    }

    /// <summary>One interface's operation on a runtime opened for it, as a run of that interface alone performs it.</summary>
    internal static async Task<object> ExecuteInterfaceAsync(
        FlowRuntime runtime, string operation, DeliveryRunPayload payload, SubmissionState? submission, ILogger log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(log);
        var context = runtime.Context;
        var flow = runtime.Flow;
        var keys = payload.RecordKeys.Select(k => new DeliveryKey(k)).ToList();
        var selection = SourceSelection.Incremental(null);
        if (ReadsSource(operation))
        {
            runtime.SubmissionId = payload.SubmissionId;
            runtime.Slices = payload.Slices.Count > 0 ? payload.Slices : null;
            selection = await SelectionAsync(context, flow, runtime.Parameters, operation, submission, keys, log, ct).ConfigureAwait(false);
            runtime.Selection = selection;
        }

        var source = flow.Source.Record.Object;
        var force = ForcesReplan(payload, submission is not null) || operation == DeliveryOperations.Replan;

        switch (operation)
        {
            case DeliveryOperations.Deliver:
            case DeliveryOperations.Replan:
                if (keys.Count > 0)
                {
                    // A scoped redelivery: forget what OSDU holds for these records (all of it, or the part the run
                    // names), then let the plan re-send them.
                    var marked = await runtime.RedeliverAsync(keys, RedeliverScopeOf(payload, flow), ct).ConfigureAwait(false);
                    LogRedeliver(log, marked, keys.Count);
                }

                var (requestedSubmission, requestedRecords) = await DeliverRequestedAsync(runtime, selection, payload, log, ct).ConfigureAwait(false);
                var run = await runtime.RunAsync(force, ct).ConfigureAwait(false);
                var delivered = DeliverOutcome.From(run, operation, source, selection.Describe(), requestedSubmission, requestedRecords);
                LogOutcome(log, string.Create(
                    CultureInfo.InvariantCulture,
                    $"this run: {delivered.Planned} planned, {delivered.Delivered} delivered, {delivered.SkippedUnchanged + delivered.UnchangedAtPush} unchanged, {delivered.Held} held, {delivered.Failed} failed; submission {delivered.SubmissionId:D} {SubmissionIntake.Summarize(run.Submission)}"));
                return delivered;

            case DeliveryOperations.Intake:
                var intake = await runtime.IntakeAsync(force || submission is not null, ct).ConfigureAwait(false);
                LogOutcome(log, intake.AlreadyProcessed ? SubmissionIntake.Summarize(intake.Submission) : $"intake: {intake.Counts}");
                return IntakeOutcome.From(intake, source, selection.Describe(), payload.Slices);

            case DeliveryOperations.Drain:
                var drained = await runtime.WorkAsync(once: false, payload.SubmissionId, ct).ConfigureAwait(false);
                LogOutcome(log, $"drain: {drained}");
                return DrainOutcome.From(drained, payload.SubmissionId);

            case DeliveryOperations.Plan:
                return await PlanAsync(runtime, force, source, selection, log, ct).ConfigureAwait(false);

            case DeliveryOperations.Verify:
                var reconcile = flow.Verify.Reconcile;
                var summary = await runtime.VerifyAsync(VerifyBatch, payload.Force ? null : VerifyInterval, reconcile, keys.Count == 0 ? null : keys, ct).ConfigureAwait(false);
                LogOutcome(log, $"verify: {summary}");
                return new VerifyRunOutcome(operation, summary.Checked, summary.Matched, summary.Drifted, summary.Missing, summary.Errors, reconcile);

            default:
                throw new SqlFlowException($"'{operation}' is not an operation of a delivery flow.");
        }
    }

    /// <summary>
    /// Which records this run reads. A run on a submission reads the rows that submission recorded, a record-scoped run
    /// reads those records by their stored key tuples, a replan reads the whole scope, and an ordinary run reads what
    /// changed since the scope's watermark, less the flow's overlap.
    /// </summary>
    private static async Task<SourceSelection> SelectionAsync(
        EngineContext context, FlowDefinition flow, IReadOnlyDictionary<string, string> values, string operation,
        SubmissionState? submission, IReadOnlyList<DeliveryKey> keys, ILogger log, CancellationToken ct)
    {
        if (submission is not null)
        {
            return SourceWindowDescription.Parse(submission.SourceWindowJson)?.ToSelection()
                ?? SourceSelection.Incremental(submission.WindowFromUtc);
        }

        if (keys.Count > 0)
        {
            var ledger = context.Ledger ?? throw new DeliveryException(DeliveryServices.NoLedgerMessage);
            var records = await ledger.GetRecordsAsync(flow.Id, keys, ct).ConfigureAwait(false);
            var tuples = new List<KeyTuple>(keys.Count);
            foreach (var key in keys)
            {
                if (!records.TryGetValue(key, out var record))
                {
                    throw new DeliveryException($"Record {key} is not in this flow's ledger, so the row it is built from cannot be found; deliver the flow's scope to plan it.");
                }

                if (record.SourceKeyJson is not { } json)
                {
                    throw new DeliveryException(
                        $"Record {key} carries no source key, so the row it is built from cannot be looked up; deliver the flow's scope once to record it.");
                }

                tuples.Add(KeyTuple.FromJson(json));
            }

            return SourceSelection.ForKeys(tuples);
        }

        if (operation == DeliveryOperations.Replan)
        {
            return SourceSelection.Full();
        }

        if (context.Ledger is not { } source)
        {
            return SourceSelection.Full();
        }

        var scope = Planner.ScopeKey(values);
        var watermark = await source.GetWatermarkAsync(flow.Id, scope, ct).ConfigureAwait(false);
        if (watermark is null)
        {
            log.LogInformation("No whole-scope plan of scope {Scope} has completed yet, so this run reads every row in scope.", scope);
            return SourceSelection.Full();
        }

        // The overlap looks a little below the watermark again, for rows whose statement committed after the last read
        // fixed its upper bound: their update time is inside the window that has already been read.
        var lower = watermark.UpdatedThroughUtc.AddSeconds(-flow.Source.Incremental.OverlapSeconds);
        log.LogInformation(
            "Scope {Scope} was planned through {Through:o}; reading the rows changed after {Lower:o} (an overlap of {Overlap}s).",
            scope, watermark.UpdatedThroughUtc, lower, flow.Source.Incremental.OverlapSeconds);
        return SourceSelection.Incremental(lower);
    }

    /// <summary>
    /// Delivers the records the ledger asked to plan again (a release, a redelivery, a cache rollout) before the run's
    /// own pass. Their rows did not change, so an incremental read would not meet them; they are read by key, in a
    /// submission of their own, and the run reports what that pass did beside its own counts.
    /// </summary>
    private static async Task<(Guid? Submission, long Records)> DeliverRequestedAsync(
        FlowRuntime runtime, SourceSelection selection, DeliveryRunPayload payload, ILogger log, CancellationToken ct)
    {
        if (!selection.CoversScope || runtime.Context.Ledger is null || payload.RecordKeys.Count > 0)
        {
            return (null, 0);
        }

        var keys = await runtime.PlanRequestedKeysAsync(RequestedPerRun, ct).ConfigureAwait(false);
        if (keys.Count == 0)
        {
            return (null, 0);
        }

        log.LogInformation("{Count} record(s) wait to be planned again; reading them by key before this run's own pass.", keys.Count);
        runtime.Selection = SourceSelection.ForKeys(keys);
        runtime.SubmissionId = null;
        try
        {
            var run = await runtime.RunAsync(force: true, ct).ConfigureAwait(false);
            log.LogInformation("The records asked for: {Summary} (submission {SubmissionId:D}).", SubmissionIntake.Summarize(run.Submission), run.Submission.SubmissionId);
            return (run.Submission.SubmissionId, run.Submission.Planned);
        }
        finally
        {
            runtime.Selection = selection;
            runtime.SubmissionId = payload.SubmissionId;
        }
    }

    /// <summary>
    /// Whether a run re-plans past the gates that skip a whole read: the tier-0 window gate and an already completed
    /// submission. A forced run, a submission re-run and a run scoped to record keys all do. The scoped run has to, or
    /// its marks are never seen. Forcing lifts only those two gates; each record's own hashes still decide what is sent.
    /// </summary>
    public static bool ForcesReplan(DeliveryRunPayload payload, bool reRunningSubmission)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return payload.Force || reRunningSubmission || payload.RecordKeys.Count > 0;
    }

    /// <summary>
    /// What a record-scoped deliver run of <paramref name="flow"/> sends again: the part the run's <c>redeliver</c> names,
    /// everything when unset. A part the flow's route does not send is refused.
    /// </summary>
    public static RedeliverSelection RedeliverScopeOf(DeliveryRunPayload payload, FlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return RedeliverScopes.Of(payload.Redeliver, flow);
    }

    /// <summary>A plan run streams its records, writes the first entries to its trace and counts the rest.</summary>
    private static async Task<PlanOutcome> PlanAsync(FlowRuntime runtime, bool force, string source, SourceSelection selection, ILogger log, CancellationToken ct)
    {
        var planner = runtime.Planner;
        var header = await planner.OpenAsync(runtime.Flow, runtime.Mapping, runtime.Parameters, selection, gate: !force, stored: null, ct).ConfigureAwait(false);
        var summary = new PlanSummary();
        long shown = 0;
        await foreach (var entry in planner.EntriesAsync(header, null, runtime.Flow.Reliability.EffectiveRenderParallelism, summary, ct).ConfigureAwait(false))
        {
            if (shown++ < PlanEntriesLogged)
            {
                log.LogInformation(RunTrace.Bounded, "{Entry}", PlanFormatting.Describe(entry));
            }
        }

        if (shown > PlanEntriesLogged)
        {
            log.LogInformation("... and {More} more record(s); the counts below cover them all.", shown - PlanEntriesLogged);
        }

        var issues = header.Issues.Select(i => i.ToString()).ToList();
        foreach (var missing in header.Source.MissingKeys)
        {
            issues.Add($"the record table {source} holds no row with key {missing}");
        }

        LogOutcome(log, header.SkippedWholeRun ? $"plan: whole run skipped, {header.SkipReason}" : $"plan: {summary}");
        return new PlanOutcome(
            DeliveryOperations.Plan, source, selection.Describe(), summary.Records, summary.Deliveries, summary.Skips, summary.AwaitingApproval,
            summary.Stale, summary.Holds, summary.Blocked, summary.Untracked, header.Slices, header.SkippedWholeRun, header.SkipReason, issues);
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

    /// <summary>The route the run delivers by, what it carries there, and why the flow takes it when the flow says.</summary>
    private static void LogRoute(ILogger log, string operation, FlowRuntime runtime)
    {
        var flow = runtime.Flow;
        var kind = runtime.ReadsSource ? $" of {runtime.Mapping.Mapping.Kind}" : string.Empty;
        var reason = flow.RouteReason is { } why ? $" ({why})" : string.Empty;
        log.LogInformation("{Operation}{Kind} by the {Route} route{Reason}.", operation, kind, DeliveryProtocols.Name(flow.Target.Protocol), reason);
    }

    private static void LogSubmission(ILogger log, Guid submissionId, string source)
        => log.LogInformation("working on submission {SubmissionId} of {Source}", submissionId, source);

    private static void LogRedeliver(ILogger log, int marked, int requested)
        => log.LogInformation("marked {Marked} of {Requested} record(s) for redelivery", marked, requested);

    private static void LogOutcome(ILogger log, string outcome)
        => log.LogInformation("{Outcome}", outcome);

    private static void LogDone(ILogger log, string operation, double seconds)
        => log.LogInformation("{Operation} completed in {Seconds:0.###}s", operation, seconds);

    private static void LogFailed(ILogger log, string operation, string error, Exception? unexpected)
        => log.LogError(unexpected, "{Operation} failed: {Error}", operation, error);
}

/// <summary>
/// The <c>result</c> of a deliver or replan run: what this run's intake planned and what its drain did (the counts the
/// run row projects), the pass it made for the records waiting to be planned again, how far it fanned out, and the
/// totals of the submission it worked on across every run so far.
/// </summary>
public sealed record DeliverOutcome(
    string Operation,
    Guid SubmissionId,
    string Source,
    string Selection,
    string Status,
    long RecordCount,
    long Planned,
    long SkippedUnchanged,
    long AwaitingApproval,
    long SkippedStale,
    long UnchangedAtPush,
    long Blocked,
    long Delivered,
    long Held,
    long Failed,
    long Retried,
    int Batches,
    int IntakeMembers,
    int DrainMembers,
    bool NothingToDo,
    Guid? RequestedSubmissionId,
    long RequestedRecords,
    string? Error,
    SubmissionTotals Submission,
    long Waiting = 0)
{
    /// <summary>
    /// The run's headline count as the platform reads it from the run artifact (<c>result.rowsLoaded</c>), which the
    /// catalog projects onto the run row: the records this run actually delivered. Without it a delivery run would show
    /// no rows at all in the run lists, and a run that delivered thousands would be indistinguishable from an empty one.
    /// </summary>
    public long RowsLoaded => Delivered;

    /// <summary>
    /// A run's counts are its own work: a run re-sending two records of a delivered submission reports two, and one that
    /// found the submission already completed reports none. A fan-out root is the exception, because its members'
    /// deliveries are summed only in the submission, so it reports the submission it covers.
    /// </summary>
    public static DeliverOutcome From(RunResult run, string operation, string source, string selection, Guid? requestedSubmission, long requestedRecords)
    {
        ArgumentNullException.ThrowIfNull(run);
        var s = run.Submission;
        var totals = new SubmissionTotals(s.Planned, s.SkippedUnchanged, s.AwaitingApproval, s.SkippedStale, s.UnchangedAtPush, s.Blocked, s.Delivered, s.Held, s.Failed, s.BatchCount, s.Waiting);
        if (run.IntakeMembers > 0 || run.DrainMembers > 0)
        {
            return new DeliverOutcome(
                operation, s.SubmissionId, source, selection, s.Status.ToString().ToLowerInvariant(), s.RecordCount,
                s.Planned, s.SkippedUnchanged, s.AwaitingApproval, s.SkippedStale, s.UnchangedAtPush, s.Blocked, s.Delivered, s.Held, s.Failed, run.Work.Retried, s.BatchCount,
                run.IntakeMembers, run.DrainMembers, run.Intake.NothingToDo && run.Work.Processed == 0, requestedSubmission, requestedRecords, s.Error, totals, s.Waiting);
        }

        var planned = run.Intake.Counts;
        var work = run.Work;
        return new DeliverOutcome(
            operation, s.SubmissionId, source, selection, s.Status.ToString().ToLowerInvariant(), s.RecordCount,
            planned.Planned, planned.Skipped, planned.AwaitingApproval, planned.Stale, work.Unchanged, planned.Blocked, work.Delivered, planned.Held + work.Held, work.Failed, work.Retried, planned.Batches,
            run.IntakeMembers, run.DrainMembers, run.Intake.NothingToDo && run.Work.Processed == 0, requestedSubmission, requestedRecords, s.Error, totals, work.Waiting);
    }
}

/// <summary>A submission's counts across every run that has worked on it; <c>Waiting</c> counts its records still waiting for a record they refer to.</summary>
public sealed record SubmissionTotals(
    long Planned, long SkippedUnchanged, long AwaitingApproval, long SkippedStale, long UnchangedAtPush, long Blocked, long Delivered, long Held, long Failed, int Batches,
    long Waiting = 0);

/// <summary>The <c>result</c> of a plan run: what a deliver would do, the first records in the run log, the counts here.</summary>
public sealed record PlanOutcome(
    string Operation,
    string Source,
    string Selection,
    long Records,
    long Deliveries,
    long Skips,
    long AwaitingApproval,
    long Stale,
    long Holds,
    long Blocked,
    long Untracked,
    int Slices,
    bool SkippedWholeRun,
    string? SkipReason,
    IReadOnlyList<string> Issues);

/// <summary>The <c>result</c> of an intake run (a whole read, or a fan-out member's share of its key slices).</summary>
public sealed record IntakeOutcome(
    string Operation,
    Guid SubmissionId,
    string Source,
    string Selection,
    string? Slices,
    long Records,
    long Planned,
    long SkippedUnchanged,
    long AwaitingApproval,
    long SkippedStale,
    long Held,
    long Blocked,
    long Untracked,
    int Batches,
    bool AlreadyProcessed)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static IntakeOutcome From(IntakeResult intake, string source, string selection, IReadOnlyList<int> slices)
    {
        ArgumentNullException.ThrowIfNull(intake);
        ArgumentNullException.ThrowIfNull(slices);
        var c = intake.Counts;
        return new IntakeOutcome(
            DeliveryOperations.Intake, intake.Submission.SubmissionId, source, selection, slices.Count == 0 ? null : KeySlices.Describe(slices),
            c.Records, c.Planned, c.Skipped, c.AwaitingApproval, c.Stale, c.Held, c.Blocked, c.Untracked, c.Batches, intake.AlreadyProcessed);
    }

    public IntakeCounts ToCounts() => new(Records, Planned, SkippedUnchanged, Held, Blocked, Untracked, Batches, SkippedStale, AwaitingApproval);

    /// <summary>Reads a member run's outcome back from its run row; null when the row carries none or something else.</summary>
    public static IntakeOutcome? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var outcome = JsonSerializer.Deserialize<IntakeOutcome>(json, Json);
            return outcome is { Operation: DeliveryOperations.Intake } ? outcome : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The <c>result</c> of a drain run; <c>Waiting</c> counts the records its claims left waiting for a record they refer to.</summary>
public sealed record DrainOutcome(string Operation, Guid? SubmissionId, long Processed, long Delivered, long Unchanged, long Retried, long Held, long Failed, int Batches, long Waiting = 0)
{
    /// <summary>The run's headline count on the run row (<c>result.rowsLoaded</c>): the records this drain delivered.</summary>
    public long RowsLoaded => Delivered;

    public static DrainOutcome From(WorkerSummary summary, Guid? submissionId)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return new DrainOutcome(
            DeliveryOperations.Drain, submissionId, summary.Processed, summary.Delivered, summary.Unchanged, summary.Retried, summary.Held, summary.Failed, summary.Batches, summary.Waiting);
    }
}

/// <summary>The <c>result</c> of a verify run.</summary>
public sealed record VerifyRunOutcome(string Operation, int Checked, int Matched, int Drifted, int Missing, int Errors, bool Reconcile);

/// <summary>The <c>result</c> of a run that failed before producing an outcome.</summary>
public sealed record OperationFailure(string Operation, string Error);
