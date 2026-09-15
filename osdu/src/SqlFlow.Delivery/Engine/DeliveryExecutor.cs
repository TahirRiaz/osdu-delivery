using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Intake;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Execution;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// Runs one delivery flow document as one platform run: the executor behind the platform's
/// <see cref="DocumentExecutor"/>. The run's parameters select the operation (deliver, verify, plan, known-state,
/// intake, drain) and its scope (a submission to re-run, the records to redeliver or verify, the partitions an
/// intake member plans); the engine's log becomes the run log and the live trace; the outcome becomes <c>run.json</c>
/// and the run row's projected counts. Every ledger activity and attempt the run writes carries the run id, so a
/// record's history links back to the run that produced it and the run links forward to what it did to each record.
/// </summary>
public sealed class DeliveryExecutor : IFlowDocumentExecutor
{
    /// <summary>Records verified per verify run; the next run picks up where the ordering by last-verified left off.</summary>
    public const int VerifyBatch = 5000;

    /// <summary>A verify run skips records verified more recently than this unless forced.</summary>
    public static readonly TimeSpan VerifyInterval = TimeSpan.FromHours(24);

    /// <summary>How many plan entries a plan run writes to its trace before summarising the rest.</summary>
    public const int PlanEntriesLogged = 200;

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
        catch (Exception ex)
        {
            // The run boundary: every failure ends the run as a recorded one. An unexpected kind is a defect, so its
            // stack goes to the run log as well.
            error = RunFailure.Describe(ex);
            LogFailed(log, parameters.Operation.ToLowerInvariant(), error, RunFailure.IsExpected(ex) ? null : ex);
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

        // A submission re-run (or an intake member's share of one) executes that submission's drop with the
        // parameters it was received with, plus any override the trigger carried; the intake's idempotency then
        // re-plans it. A drain works on the submission's batches and never opens the drop. A submission whose records
        // came inline reads the drop the run writes from the ledger's copy of them (design.md section 3.4).
        SubmissionState? submission = null;
        InlineSubmissionState? inline = null;
        if (parameters.SubmissionId is { } submissionId && operation is not RunParameters.DrainOperation)
        {
            var ledger = context.Ledger ?? throw new DeliveryException("Working on a submission needs the ledger, which lives in the catalog database.");
            submission = await ledger.GetSubmissionAsync(submissionId, ct).ConfigureAwait(false);
            inline = await ledger.GetInlineSubmissionAsync(submissionId, ct).ConfigureAwait(false);
            var (ownerId, ownerName) = submission is not null ? (submission.FlowId, submission.FlowName)
                : inline is not null ? (inline.FlowId, inline.FlowName)
                : throw new DeliveryException($"Submission {submissionId:D} is not in the ledger.");
            if (ownerId != flow.Id)
            {
                throw new DeliveryException($"Submission {submissionId:D} belongs to flow '{ownerName}', not '{flow.Name}'.");
            }

            if (inline is not null)
            {
                if (!string.IsNullOrWhiteSpace(parameters.Drop))
                {
                    throw new DeliveryException($"Submission {submissionId:D} carries its records inline: a run on it writes its drop, and takes no drop location.");
                }

                values = Merge(inline.Parameters(), parameters.Values);
                drop = InlineDrop.Location(flow, FlowParameters.Resolve(flow, values), submissionId);
                LogInlineSubmission(log, submissionId, inline.RecordCount, inline.ReceivedBy);
            }
            else
            {
                if (submission!.IsReplan && flow.Source.Replica is null)
                {
                    throw new DeliveryException($"Submission {submissionId:D} is a replan of the flow's replica, which the flow no longer declares (source.replica).");
                }

                drop ??= submission.IsReplan ? null : submission.DropLocation;
                values = Merge(ParseValues(submission.ParametersJson), parameters.Values);
                LogSubmission(log, submissionId, submission.IsReplan ? "the replica" : submission.DropLocation);
            }
        }

        var readsDrop = operation is RunParameters.DeliverOperation or RunParameters.PlanOperation or RunParameters.IntakeOperation;

        // A replan plans the replica's records again instead of reading a drop. A deliver run scoped to records of a flow with a
        // replica is one: the records are marked, and their latest replica rows planned, without anyone sending the drop again.
        var replan = readsDrop && (parameters.Replan
            || (operation == RunParameters.DeliverOperation && parameters.RecordKeys.Count > 0 && flow.Source.Replica is not null
                && submission is null && inline is null && string.IsNullOrWhiteSpace(parameters.Drop)));
        if (replan && flow.Source.Replica is null)
        {
            throw new DeliveryException($"Flow '{flow.Name}' declares no source.replica, so a run cannot replan from it; deliver its drop again instead.");
        }

        // A flow reading from SQL extracts its records into a drop under its work location first, unless the run names a
        // drop, works on a submission (whose drop an earlier run extracted), or replans (docs/delivery/sql-source.md).
        Guid? extraction = null;
        if (readsDrop && !replan && flow.Source.Sql is not null && submission is null && inline is null && string.IsNullOrWhiteSpace(drop))
        {
            extraction = Guid.CreateVersion7();
            drop = SqlSource.SqlDrop.Location(flow, FlowParameters.Resolve(flow, values), extraction.Value);
        }

        // Deliver, plan and intake read the drop and render; verify, known-state and drain only touch the target
        // and the ledger, so they need neither the flow's parameters nor its render inputs.
        using var runtime = readsDrop
            ? await FlowRuntime.CreateAsync(context, flow, values, drop, ct).ConfigureAwait(false)
            : FlowRuntime.ForTarget(context, flow);
        runtime.Actor = actor;
        runtime.RunId = runId;
        runtime.ActivityLog = runLogger.Render;
        if (readsDrop)
        {
            runtime.SubmissionId = parameters.SubmissionId;
        }

        if (inline is not null && runtime.HasDrop)
        {
            await runtime.WriteInlineDropAsync(inline, ct).ConfigureAwait(false);
        }

        if (extraction is { } extractionId)
        {
            await runtime.ExtractSqlDropAsync(extractionId, parameters.Force, ct).ConfigureAwait(false);
        }

        var keys = parameters.RecordKeys.Select(k => new DeliveryKey(k)).ToList();
        var partitions = parameters.Partitions.Count > 0 ? parameters.Partitions : null;
        var fromReplica = replan || submission is { IsReplan: true };
        var source = fromReplica ? $"replica {flow.Source.Replica!.Schema}" : runtime.HasDrop ? runtime.DropLocation : string.Empty;

        switch (operation)
        {
            case RunParameters.DeliverOperation:
                if (keys.Count > 0)
                {
                    // A scoped redelivery: forget what OSDU holds for these records (all of it, or the part the run
                    // names), then let the plan re-send them.
                    var marked = await runtime.RedeliverAsync(keys, RedeliverScopeOf(parameters), ct).ConfigureAwait(false);
                    LogRedeliver(log, marked, keys.Count);
                }

                if (replan)
                {
                    await runtime.ReplanAsync(keys.Count > 0 ? keys : null, ct).ConfigureAwait(false);
                }

                var run = await runtime.RunAsync(force: ForcesReplan(parameters, submission is not null) || replan, ct).ConfigureAwait(false);
                var delivered = DeliverOutcome.From(run, source);
                LogOutcome(log, string.Create(
                    CultureInfo.InvariantCulture,
                    $"this run: {delivered.Planned} planned, {delivered.Delivered} delivered, {delivered.SkippedUnchanged + delivered.UnchangedAtPush} unchanged, {delivered.Held} held, {delivered.Failed} failed; submission {delivered.SubmissionId:D} {SubmissionIntake.Summarize(run.Submission)}"));
                return delivered;

            case RunParameters.IntakeOperation:
                if (replan)
                {
                    await runtime.ReplanAsync(keys.Count > 0 ? keys : null, ct).ConfigureAwait(false);
                }

                var intake = await runtime.IntakeAsync(parameters.Force || submission is not null || replan, partitions, ct).ConfigureAwait(false);
                LogOutcome(log, intake.AlreadyProcessed ? SubmissionIntake.Summarize(intake.Submission) : $"intake: {intake.Counts}");
                return IntakeOutcome.From(intake, source, partitions);

            case RunParameters.DrainOperation:
                var drained = await runtime.WorkAsync(once: false, parameters.SubmissionId, ct).ConfigureAwait(false);
                LogOutcome(log, $"drain: {drained}");
                return DrainOutcome.From(drained, parameters.SubmissionId);

            case RunParameters.PlanOperation:
                return await PlanAsync(runtime, parameters.Force, partitions, replan, keys, submission, source, log, ct).ConfigureAwait(false);

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

    /// <summary>
    /// Whether a deliver run re-plans past the gates that skip a drop as a whole: the tier-0 source-version gate and an
    /// already completed submission. A forced run, a submission re-run and a run scoped to record keys all do. The
    /// scoped run has to, or its marks are never seen: a drop whose source did not advance is skipped before any record
    /// is compared, and the run reports the earlier delivery as if it had sent something. Forcing lifts only those two
    /// gates; each record's own hashes still decide what is sent, so only the marked records go.
    /// </summary>
    public static bool ForcesReplan(RunParameters parameters, bool reRunningSubmission)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return parameters.Force || reRunningSubmission || parameters.RecordKeys.Count > 0;
    }

    /// <summary>What a record-scoped deliver run sends again: the run's validated <c>redeliver</c>, everything when unset.</summary>
    public static RedeliverScope RedeliverScopeOf(RunParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return parameters.Redeliver is { } scope ? Enum.Parse<RedeliverScope>(scope, ignoreCase: true) : RedeliverScope.All;
    }

    /// <summary>
    /// A plan run streams its records (the drop's, a loaded or replanned submission's from the replica, or for a replan the
    /// replica's latest records, registering nothing), writes the first entries to its trace and counts the rest.
    /// </summary>
    private static async Task<PlanOutcome> PlanAsync(
        FlowRuntime runtime, bool force, IReadOnlyList<int>? partitions, bool replan, IReadOnlyList<DeliveryKey> keys, SubmissionState? submission, string source, ILogger log, CancellationToken ct)
    {
        var planner = runtime.Planner;
        var replica = runtime.Replica;
        PlanHeader header;
        IAsyncEnumerable<PlanInput> inputs;
        var fromReplica = replan || submission is { IsReplan: true } || (submission is { IsLoaded: true } && replica is not null);
        if (fromReplica)
        {
            if (partitions is not null)
            {
                throw new DeliveryException("A plan from the replica reads no drop, so it takes no partitions.");
            }

            var store = replica ?? throw new DeliveryException($"Flow '{runtime.Flow.Name}' declares no source.replica to plan from.");
            header = await planner.OpenStoredAsync(runtime.Flow, runtime.Mapping, runtime.Parameters, replan ? null : submission, gate: !force && submission is { IsReplan: false }, ct).ConfigureAwait(false);
            inputs = replan
                ? store.ReadLatestAsync(Planner.ScopeKey(runtime.Parameters), keys.Count > 0 ? keys : null, ct)
                : store.ReadSubmissionAsync(submission!.SubmissionId, 0, long.MaxValue, ct);
            source = $"replica {store.Schema}";
        }
        else
        {
            header = await planner.OpenAsync(runtime.Flow, runtime.Mapping, runtime.Parameters, runtime.DropLocation, force, ct).ConfigureAwait(false);
            inputs = planner.DropInputsAsync(header, partitions, ct);
        }

        var summary = new PlanSummary();
        long shown = 0;
        await foreach (var entry in planner.EntriesOfAsync(header, inputs, runtime.Flow.Reliability.EffectiveRenderParallelism, summary, ct).ConfigureAwait(false))
        {
            if (shown++ < PlanEntriesLogged)
            {
                log.LogInformation("{Entry}", PlanFormatting.Describe(entry));
            }
        }

        if (shown > PlanEntriesLogged)
        {
            log.LogInformation("... and {More} more record(s); the counts below cover them all.", shown - PlanEntriesLogged);
        }

        var issues = header.Issues.Select(i => i.ToString()).ToList();
        if (!fromReplica && !header.SkippedWholeRun && header.Drop is { Manifest.RecordCount: > 0 } drop && drop.Manifest.RecordCount != summary.Records)
        {
            issues.Add(Planner.RecordCountIssue(runtime.Flow, drop.Manifest.RecordCount, summary.Records).ToString());
        }

        LogOutcome(log, header.SkippedWholeRun ? $"plan: whole run skipped, {header.SkipReason}" : $"plan: {summary}");
        return new PlanOutcome(
            RunParameters.PlanOperation, source, summary.Records, summary.Deliveries, summary.Skips, summary.AwaitingApproval, summary.Stale, summary.Holds, summary.Blocked, summary.Untracked,
            header.Partitions, header.SkippedWholeRun, header.SkipReason, issues);
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
        => log.LogInformation("working on submission {SubmissionId} from its drop {Drop}", submissionId, drop);

    private static void LogInlineSubmission(ILogger log, Guid submissionId, int records, string receivedBy)
        => log.LogInformation("working on inline submission {SubmissionId}: {Records} record(s) sent by {ReceivedBy}", submissionId, records, receivedBy);

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
/// The <c>result</c> of a deliver run: what this run's intake planned and what its drain did (the counts the run row
/// projects), how far it fanned out, and the totals of the submission it worked on across every run so far.
/// </summary>
public sealed record DeliverOutcome(
    string Operation,
    Guid SubmissionId,
    string Drop,
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
    string? Error,
    SubmissionTotals Submission)
{
    /// <summary>
    /// A run's counts are its own work: a run re-sending two records of a delivered submission reports two, and one that
    /// found the submission already completed reports none. A fan-out root is the exception, because its members'
    /// deliveries are summed only in the submission, so it reports the submission it covers.
    /// </summary>
    public static DeliverOutcome From(RunResult run, string drop)
    {
        ArgumentNullException.ThrowIfNull(run);
        var s = run.Submission;
        var totals = new SubmissionTotals(s.Planned, s.SkippedUnchanged, s.AwaitingApproval, s.SkippedStale, s.UnchangedAtPush, s.Blocked, s.Delivered, s.Held, s.Failed, s.BatchCount);
        if (run.IntakeMembers > 0 || run.DrainMembers > 0)
        {
            return new DeliverOutcome(
                RunParameters.DeliverOperation, s.SubmissionId, drop, s.Status.ToString().ToLowerInvariant(), s.RecordCount,
                s.Planned, s.SkippedUnchanged, s.AwaitingApproval, s.SkippedStale, s.UnchangedAtPush, s.Blocked, s.Delivered, s.Held, s.Failed, run.Work.Retried, s.BatchCount,
                run.IntakeMembers, run.DrainMembers, run.Intake.NothingToDo && run.Work.Processed == 0, s.Error, totals);
        }

        var planned = run.Intake.Counts;
        var work = run.Work;
        return new DeliverOutcome(
            RunParameters.DeliverOperation, s.SubmissionId, drop, s.Status.ToString().ToLowerInvariant(), s.RecordCount,
            planned.Planned, planned.Skipped, planned.AwaitingApproval, planned.Stale, work.Unchanged, planned.Blocked, work.Delivered, planned.Held + work.Held, work.Failed, work.Retried, planned.Batches,
            run.IntakeMembers, run.DrainMembers, run.Intake.NothingToDo && run.Work.Processed == 0, s.Error, totals);
    }
}

/// <summary>A submission's counts across every run that has worked on it.</summary>
public sealed record SubmissionTotals(
    long Planned, long SkippedUnchanged, long AwaitingApproval, long SkippedStale, long UnchangedAtPush, long Blocked, long Delivered, long Held, long Failed, int Batches);

/// <summary>The <c>result</c> of a plan run: what a deliver would do, the first records in the run log, the counts here.</summary>
public sealed record PlanOutcome(
    string Operation,
    string Drop,
    long Records,
    long Deliveries,
    long Skips,
    long AwaitingApproval,
    long Stale,
    long Holds,
    long Blocked,
    long Untracked,
    int Partitions,
    bool SkippedWholeRun,
    string? SkipReason,
    IReadOnlyList<string> Issues);

/// <summary>The <c>result</c> of an intake run (a whole drop, or a fan-out member's share of its partitions).</summary>
public sealed record IntakeOutcome(
    string Operation,
    Guid SubmissionId,
    string Drop,
    string? Partitions,
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

    public static IntakeOutcome From(IntakeResult intake, string drop, IReadOnlyList<int>? partitions)
    {
        ArgumentNullException.ThrowIfNull(intake);
        var c = intake.Counts;
        return new IntakeOutcome(
            RunParameters.IntakeOperation, intake.Submission.SubmissionId, drop, partitions is null ? null : SubmissionIntake.DescribePartitions(partitions),
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
            return outcome is { Operation: RunParameters.IntakeOperation } ? outcome : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The <c>result</c> of a drain run.</summary>
public sealed record DrainOutcome(string Operation, Guid? SubmissionId, long Processed, long Delivered, long Unchanged, long Retried, long Held, long Failed, int Batches)
{
    public static DrainOutcome From(WorkerSummary summary, Guid? submissionId)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return new DrainOutcome(RunParameters.DrainOperation, submissionId, summary.Processed, summary.Delivered, summary.Unchanged, summary.Retried, summary.Held, summary.Failed, summary.Batches);
    }
}

/// <summary>The <c>result</c> of a verify run.</summary>
public sealed record VerifyRunOutcome(string Operation, int Checked, int Matched, int Drifted, int Missing, int Errors, bool Reconcile);

/// <summary>The <c>result</c> of a known-state publication.</summary>
public sealed record KnownStateOutcome(string Operation, string PublishedTo, long Records);

/// <summary>The <c>result</c> of a run that failed before producing an outcome.</summary>
public sealed record OperationFailure(string Operation, string Error);
