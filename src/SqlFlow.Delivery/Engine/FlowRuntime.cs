using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Drops;
using SqlFlow.Delivery.Engine.FanOut;
using SqlFlow.Delivery.Engine.Intake;
using SqlFlow.Delivery.Engine.KnownState;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Verify;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Storage;

namespace SqlFlow.Delivery.Engine;

/// <summary>The shared, flow-independent services the engine is composed from.</summary>
public sealed record EngineContext(
    DeliveryDocumentLoader Documents,
    IDropReader Drops,
    FileStoreRegistry Stores,
    ISecretResolver Secrets,
    ILedger? Ledger,
    TimeProvider Time,
    ILoggerFactory Loggers,
    IProtocolFactory Protocols,
    IDeliveryListener Listener,
    IFanOutDispatcher? FanOut = null)
{
    /// <summary>The environment switch that lets a flow target a loopback address (local OSDU emulators, tests).</summary>
    public const string AllowLoopbackVariable = "SQLFLOW_DELIVERY_ALLOW_LOOPBACK";

    /// <summary>Whether flows may target a loopback address in this process (the switch above, read once per check).</summary>
    public static bool LoopbackAllowed
        => Environment.GetEnvironmentVariable(AllowLoopbackVariable) is { } v && v.Equals("true", StringComparison.OrdinalIgnoreCase);

    /// <summary>The fan-out dispatcher, never null: a host without a catalog gets one that is not available.</summary>
    public IFanOutDispatcher Dispatcher => FanOut ?? NoFanOutDispatcher.Instance;

    /// <summary>A context with a different logger factory: the node swaps in the run log for one run.</summary>
    public EngineContext WithLoggers(ILoggerFactory loggers) => this with { Loggers = loggers };
}

/// <summary>What a deliver run did: the intake, the drain, the submission it left, and how far it fanned out.</summary>
public sealed record RunResult(IntakeResult Intake, WorkerSummary Work, SubmissionState Submission, int IntakeMembers = 0, int DrainMembers = 0);

/// <summary>
/// One flow, resolved and ready: parameters applied, render inputs pinned (from the mappings and snapshots the
/// flow's repository layout locates), and (when a ledger and a target are wired) the protocol, worker, verifier
/// and publisher over them. Every operation an operator can trigger goes through here and is recorded in the
/// ledger's activity trail with the <see cref="Actor"/> that asked for it and the platform <see cref="RunId"/>
/// it ran as. A deliver run coordinates its own fan-out (design.md section 16.4): intake partitions first, then
/// drains, each a member run of the same flow on any node of the pool, waited for and completed here.
/// </summary>
public sealed class FlowRuntime : IDisposable
{
    private static readonly TimeSpan MemberPoll = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan MemberProgress = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan LeaseSettle = TimeSpan.FromSeconds(30);

    private readonly EngineContext _context;
    private readonly ResolvedMapping? _mapping;
    private readonly string? _drop;
    private readonly ILogger _log;
    private HttpRuntime? _http;
    private IDeliveryProtocol? _protocol;

    private FlowRuntime(EngineContext context, FlowDefinition flow, DeliveryLayout layout, MappingCatalog mappings, ISnapshotStore snapshots, IReadOnlyDictionary<string, string> parameters, ResolvedMapping? mapping, string? dropLocation)
    {
        _context = context;
        Flow = flow;
        Layout = layout;
        Mappings = mappings;
        Snapshots = snapshots;
        Parameters = parameters;
        _mapping = mapping;
        _drop = dropLocation;
        _log = context.Loggers.CreateLogger("run");
    }

    public FlowDefinition Flow { get; }

    public DeliveryLayout Layout { get; }

    public MappingCatalog Mappings { get; }

    public ISnapshotStore Snapshots { get; }

    public IReadOnlyDictionary<string, string> Parameters { get; }

    /// <summary>The pinned render inputs. Only a runtime opened with <see cref="CreateAsync(EngineContext, FlowDefinition, IReadOnlyDictionary{string, string}?, string?, CancellationToken)"/> has them.</summary>
    public ResolvedMapping Mapping => _mapping ?? throw new DeliveryException("This operation renders records and needs the flow's mapping and snapshots; the runtime was opened for target operations only.");

    /// <summary>The drop the runtime reads. Only a runtime opened with the full <see cref="CreateAsync(EngineContext, FlowDefinition, IReadOnlyDictionary{string, string}?, string?, CancellationToken)"/> has one.</summary>
    public string DropLocation => _drop ?? throw new DeliveryException("This operation reads the drop and needs the flow's parameters; the runtime was opened for target operations only.");

    /// <summary>True when the runtime was opened with a drop and render inputs (deliver, plan), false for target-only work.</summary>
    public bool HasDrop => _drop is not null;

    public EngineContext Context => _context;

    /// <summary>Who is asking: the run's trigger source (manual:&lt;user&gt;, schedule:&lt;name&gt;), gui:&lt;user&gt; for
    /// an intervention, cli:&lt;user&gt; on a workstation. Recorded on every activity.</summary>
    public string Actor { get; set; } = "unknown";

    /// <summary>The platform run this runtime executes for, when it is one; stamped on activities and attempts.</summary>
    public Guid? RunId { get; set; }

    /// <summary>Supplies the captured log for the activity being completed (the executor sets it).</summary>
    public Func<string?>? ActivityLog { get; set; }

    /// <summary>Loads and resolves a flow. Fails at parse time with the file path on every message.</summary>
    public static async Task<FlowRuntime> CreateAsync(EngineContext context, string flowPath, IReadOnlyDictionary<string, string>? parameters, string? dropOverride, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var flow = context.Documents.LoadFlow(flowPath);
        return await CreateAsync(context, flow, parameters, dropOverride, ct).ConfigureAwait(false);
    }

    public static async Task<FlowRuntime> CreateAsync(EngineContext context, FlowDefinition flow, IReadOnlyDictionary<string, string>? parameters, string? dropOverride, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(flow);
        var values = FlowParameters.Resolve(flow, parameters);
        var layout = DeliveryLayout.Resolve(flow);
        var mappings = new MappingCatalog(layout.MappingsDirectory, context.Documents);
        var snapshots = new FileSnapshotStore(layout.SnapshotsRoot, context.Stores);
        var resolver = new RenderResolver(mappings, snapshots);
        var mapping = await resolver.ResolveAsync(flow, ct).ConfigureAwait(false);
        var drop = dropOverride ?? FlowParameters.DropLocation(flow, values);
        return new FlowRuntime(context, flow, layout, mappings, snapshots, values, mapping, drop);
    }

    /// <summary>
    /// A runtime for the operations that touch the target and the ledger but never the drop (verify, delete,
    /// release, redeliver, known-state, probe, drain): no parameters are required and no mapping is resolved, so
    /// they work for a flow whose drop parameters are unknown or whose snapshots are not on this host.
    /// </summary>
    public static FlowRuntime ForTarget(EngineContext context, FlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(flow);
        var layout = DeliveryLayout.Resolve(flow);
        return new FlowRuntime(
            context, flow, layout, new MappingCatalog(layout.MappingsDirectory, context.Documents), new FileSnapshotStore(layout.SnapshotsRoot, context.Stores),
            new Dictionary<string, string>(StringComparer.Ordinal), null, null);
    }

    /// <summary>The render inputs alone (mappings and snapshot store), for snapshot capture and mapping checks that need no drop.</summary>
    public static (MappingCatalog Mappings, ISnapshotStore Snapshots) RenderInputs(EngineContext context, FlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(flow);
        var layout = DeliveryLayout.Resolve(flow);
        return (new MappingCatalog(layout.MappingsDirectory, context.Documents), new FileSnapshotStore(layout.SnapshotsRoot, context.Stores));
    }

    public Planner Planner => new(_context.Drops, _context.Ledger, _context.Loggers.CreateLogger<Planner>());

    /// <summary>Renders the drop and reports what would change, every entry collected. Changes nothing, records nothing.</summary>
    public Task<DeliveryPlan> PlanAsync(bool force = false, CancellationToken ct = default)
        => Planner.PlanAsync(Flow, Mapping, Parameters, DropLocation, force, ct);

    public SubmissionIntake Intake => new(RequireLedger(), Planner, _context.Stores, _context.Time, _context.Listener, _context.Loggers.CreateLogger<SubmissionIntake>()) { RunId = RunId };

    public async Task<IDeliveryProtocol> ProtocolAsync(CancellationToken ct = default)
    {
        if (_protocol is not null)
        {
            return _protocol;
        }

        _http ??= new HttpRuntime(Flow.Reliability, _context.Secrets, _context.Time, allowLoopback: EngineContext.LoopbackAllowed);
        _protocol = await _context.Protocols.CreateAsync(Flow, _http, ct).ConfigureAwait(false);
        return _protocol;
    }

    public async Task<DeliveryWorker> WorkerAsync(CancellationToken ct = default)
        => new(RequireLedger(), _context.Drops, _context.Stores, await ProtocolAsync(ct).ConfigureAwait(false), Flow, _context.Time, _context.Listener, _context.Loggers.CreateLogger<DeliveryWorker>()) { RunId = RunId };

    public async Task<Verifier> VerifierAsync(CancellationToken ct = default)
        => new(RequireLedger(), await ProtocolAsync(ct).ConfigureAwait(false), Flow, _context.Time, _context.Listener, _context.Loggers.CreateLogger<Verifier>());

    public KnownStatePublisher Publisher => new(RequireLedger(), _context.Stores, _context.Time, _context.Loggers.CreateLogger<KnownStatePublisher>());

    /// <summary>Intake plus drain, fanned out across the fleet when the flow asks for it: the <c>deliver</c> operation.</summary>
    public Task<RunResult> RunAsync(bool force, CancellationToken ct = default)
        => TrackAsync("deliver", new { force, drop = DropLocation, parameters = Parameters, fanOut = Flow.Reliability.FanOut }, null, async () =>
        {
            await EnsureLegalTagsAsync(ct).ConfigureAwait(false);
            FanOutHandle? handle = null;
            try
            {
                var (intake, intakeMembers) = await IntakeWithFanOutAsync(force, h => handle = h, ct).ConfigureAwait(false);
                handle = null;
                if (intake.NothingToDo)
                {
                    return (new RunResult(intake, WorkerSummary.Empty, intake.Submission, intakeMembers), SubmissionIntake.Summarize(intake.Submission), intake.Submission.SubmissionId);
                }

                var (work, drainMembers) = await DrainWithFanOutAsync(intake.Submission, h => handle = h, ct).ConfigureAwait(false);
                handle = null;
                var submission = await Intake.CompleteAsync(intake.Submission.SubmissionId, Flow.Id, ct).ConfigureAwait(false);
                return (new RunResult(intake, work, submission, intakeMembers, drainMembers), SubmissionIntake.Summarize(submission), submission.SubmissionId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested && handle is not null)
            {
                // A cancelled root takes its members with it.
                await CancelMembersAsync(handle).ConfigureAwait(false);
                throw;
            }
        }, ct);

    /// <summary>
    /// Asks the legal service about the mapping's legal tags before a run plans or sends anything. Every record the
    /// mapping renders carries the same tags, and storage refuses a record whose tag is unknown or expired, so a run
    /// that starts with a bad tag would fail each record it plans with the same error; refusing the run names the tag
    /// and the service's reason once, before anything reaches the ledger or OSDU. A target that does not ask (see
    /// <see cref="IDeliveryProtocol.InvalidLegalTagsAsync"/>) is logged as not checked, never taken as valid.
    /// </summary>
    private async Task EnsureLegalTagsAsync(CancellationToken ct)
    {
        var tags = Mapping.Mapping.Envelope.LegalTags;
        var protocol = await ProtocolAsync(ct).ConfigureAwait(false);
        var invalid = await protocol.InvalidLegalTagsAsync(tags, ct).ConfigureAwait(false);
        if (invalid is null)
        {
            _log.LogInformation(
                "The mapping's legal tags ({Tags}) were not checked with the legal service before the run: the target does not ask it (validateLegalTags is off, or a well log endpoint names neither ddmsRoot nor legalValidatePath).",
                string.Join(", ", tags));
            return;
        }

        if (invalid.Count > 0)
        {
            throw new DeliveryException(
                string.Create(CultureInfo.InvariantCulture, $"The legal service refuses {invalid.Count} of the legal tag(s) mapping {Mapping.Mapping.Reference} puts on every record, so nothing was planned or sent: ")
                + string.Join("; ", invalid.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + ": " + kv.Value)));
        }
    }

    /// <summary>Intake only: register and plan the drop (or a subset of its partitions) into work batches, leaving the delivery to a drain.</summary>
    public Task<IntakeResult> IntakeAsync(bool force, IReadOnlyList<int>? partitions = null, CancellationToken ct = default)
        => TrackAsync("intake", new { force, drop = DropLocation, parameters = Parameters, partitions = partitions is null ? null : SubmissionIntake.DescribePartitions(partitions) }, null, async () =>
        {
            await EnsureLegalTagsAsync(ct).ConfigureAwait(false);
            var intake = await Intake.IntakeAsync(Flow, Mapping, Parameters, DropLocation, force, partitions, ct).ConfigureAwait(false);
            return (intake, intake.AlreadyProcessed ? SubmissionIntake.Summarize(intake.Submission) : intake.Counts.ToString(), intake.Submission.SubmissionId);
        }, ct);

    /// <summary>Drains the pending work of the flow (one pass, or until nothing is due), optionally of one submission: the <c>drain</c> operation.</summary>
    public Task<WorkerSummary> WorkAsync(bool once, Guid? submissionId = null, CancellationToken ct = default)
        => TrackAsync("drain", new { once, submissionId }, null, async () =>
        {
            var worker = await WorkerAsync(ct).ConfigureAwait(false);
            var summary = once ? await worker.PassAsync(submissionId, ct).ConfigureAwait(false) : await worker.DrainAsync(submissionId, ct).ConfigureAwait(false);
            if (submissionId is { } s)
            {
                await Intake.CompleteAsync(s, Flow.Id, ct).ConfigureAwait(false);
            }

            return (summary, summary.ToString(), submissionId);
        }, ct);

    /// <summary>The drift pass: the <c>verify</c> operation.</summary>
    public Task<VerifySummary> VerifyAsync(int max, TimeSpan? notVerifiedWithin, bool reconcile, IReadOnlyList<DeliveryKey>? keys = null, CancellationToken ct = default)
        => TrackAsync("verify", new { max, notVerifiedWithin, reconcile, keys = keys?.Select(k => k.ToString()).ToList() }, keys is { Count: 1 } ? keys[0] : null, async () =>
        {
            var verifier = await VerifierAsync(ct).ConfigureAwait(false);
            var summary = await verifier.RunAsync(max, notVerifiedWithin, reconcile, keys, ct).ConfigureAwait(false);
            return (summary, summary.ToString(), (Guid?)null);
        }, ct);

    public Task<long> PublishKnownStateAsync(string to, CancellationToken ct = default)
        => TrackAsync("known-state", new { to }, null, async () =>
        {
            var count = await Publisher.PublishAsync(Flow, to, ct).ConfigureAwait(false);
            return (count, string.Create(CultureInfo.InvariantCulture, $"published {count} record(s) to {to}"), (Guid?)null);
        }, ct);

    /// <summary>Releases held, failed or deleted records (all of them when <paramref name="keys"/> is null).</summary>
    public Task<int> ReleaseAsync(IReadOnlyList<DeliveryKey>? keys, CancellationToken ct = default)
        => TrackAsync("release", new { keys = keys?.Select(k => k.ToString()).ToList() }, keys is { Count: 1 } ? keys[0] : null, async () =>
        {
            var released = await RequireLedger().ReleaseAsync(Flow.Id, keys, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            await EmitAsync("record.released", keys, $"released by {Actor}", ct).ConfigureAwait(false);
            return (released, $"released {released} record(s)", (Guid?)null);
        }, ct);

    /// <summary>
    /// Marks records for redelivery (design.md section 7.6: forget what OSDU holds so the next plan re-sends).
    /// The redelivery itself happens on the next <see cref="RunAsync"/> of the drop that carries the records.
    /// </summary>
    public Task<int> RedeliverAsync(IReadOnlyList<DeliveryKey> keys, RedeliverScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return TrackAsync("redeliver", new { keys = keys.Select(k => k.ToString()).ToList(), scope = scope.ToString() }, keys.Count == 1 ? keys[0] : null, async () =>
        {
            var marked = await RequireLedger().ForceRedeliverAsync(Flow.Id, keys, scope, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            await EmitAsync("record.redeliver", keys, $"redelivery of {scope.ToString().ToLowerInvariant()} requested by {Actor}", ct).ConfigureAwait(false);
            return (marked, $"marked {marked} record(s) for redelivery of {scope.ToString().ToLowerInvariant()}", (Guid?)null);
        }, ct);
    }

    /// <summary>
    /// Removes records from OSDU through the flow's protocol, to the extent the scope asks for, and records what
    /// happened to each one in the ledger. One record and ten thousand take the same path: the selection is
    /// resolved to keys, the records are removed in chunks (batched into one request where the protocol and the
    /// scope allow it), and every record gets its own ledger attempt naming the scope and the operator. A record
    /// the flow never delivered has nothing in OSDU to remove and is reported as skipped rather than failing the
    /// removal; a record OSDU has already lost settles the ledger just as a removal would, because the state the
    /// operator asked for is the state OSDU is in.
    /// </summary>
    public Task<RemovalSummary> RemoveAsync(RemovalSelection selection, RemovalScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var single = selection.Keys is { Count: 1 } only ? only[0] : (DeliveryKey?)null;
        return TrackAsync("delete", selection.Describe(scope), single, async () =>
        {
            var ledger = RequireLedger();
            var keys = selection.Keys
                ?? await ledger.ListKeysAsync(Flow.Id, selection.Filter!, RemovalLimits.MaxSelection, ct).ConfigureAwait(false);
            var protocol = await ProtocolAsync(ct).ConfigureAwait(false);
            var results = new List<RemovalRecordResult>(keys.Count);

            foreach (var chunk in keys.Chunk(RemovalLimits.Chunk))
            {
                ct.ThrowIfCancellationRequested();
                var records = await ledger.GetRecordsAsync(Flow.Id, chunk, ct).ConfigureAwait(false);
                var removals = new List<RecordRemoval>(chunk.Length);
                foreach (var key in chunk)
                {
                    if (!records.TryGetValue(key, out var record))
                    {
                        results.Add(RemovalRecordResult.Skipped(key, null, null, null, "the record is not in this flow's ledger", null));
                    }
                    else if (record.TargetId is null)
                    {
                        results.Add(RemovalRecordResult.Skipped(key, record.SourceKey, record.Label, null, "the record has no OSDU id: it was never delivered", record.LastSubmissionId));
                    }
                    else
                    {
                        removals.Add(new RecordRemoval(key, record.TargetId, JsonMerge.ToValues(record.TargetStateJson)));
                    }
                }

                if (removals.Count == 0)
                {
                    continue;
                }

                var outcomes = await protocol.DeleteBatchAsync(removals, scope, ct).ConfigureAwait(false);

                // The ledger settles the whole chunk in one write, and only for the records the target actually
                // answered for: a record whose call failed keeps the state it had, so a retry of the removal is
                // still the removal of a record that is still there.
                var settled = outcomes.Where(o => o.Succeeded).Select(o => o.Removal.Key).ToList();
                await ledger.MarkRemovedAsync(settled, scope, Actor, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
                foreach (var outcome in outcomes)
                {
                    results.Add(await AnnounceRemovalAsync(outcome, scope, records[outcome.Removal.Key], ct).ConfigureAwait(false));
                }
            }

            var summary = RemovalSummary.Of(scope, keys.Count, results);
            return (summary, summary.Describe(), results.Count == 1 ? results[0].SubmissionId : null);
        }, ct);
    }

    /// <summary>Announces one record's removal, now that the ledger holds it, and says what it did.</summary>
    private async Task<RemovalRecordResult> AnnounceRemovalAsync(RemovalResult outcome, RemovalScope scope, RecordState record, CancellationToken ct)
    {
        var key = outcome.Removal.Key;
        if (outcome.Failure is { } failure)
        {
            return RemovalRecordResult.Failed(key, record.SourceKey, record.Label, record.TargetId, HeaderRedaction.RedactMessage(failure.Message), record.LastSubmissionId);
        }

        var result = outcome.Outcome!;
        var now = _context.Time.GetUtcNow().UtcDateTime;
        await _context.Listener.OnEventAsync(new DeliveryEvent
        {
            AtUtc = now,
            FlowId = Flow.Id,
            FlowName = Flow.Name,
            Kind = scope == RemovalScope.History ? "record.history-purged" : "record.deleted",
            SubmissionId = record.LastSubmissionId,
            DeliveryKey = key,
            SourceKey = record.SourceKey,
            Label = record.Label,
            TargetId = record.TargetId,
            TargetVersion = record.TargetVersion,
            Worker = Actor,
            Phase = scope == RemovalScope.History ? "purge-history" : "delete",
            Detail = result.Detail,
        }, ct).ConfigureAwait(false);

        return result.AlreadyGone
            ? RemovalRecordResult.AlreadyGone(key, record.SourceKey, record.Label, record.TargetId, result.Detail, record.LastSubmissionId)
            : RemovalRecordResult.Removed(key, record.SourceKey, record.Label, record.TargetId, result.Detail, record.LastSubmissionId);
    }

    /// <summary>
    /// The intake, spread across the fleet when the flow declares a fan-out, the drop is partitioned, and the
    /// catalog can enqueue runs: every member plans its share of the partitions into work batches, this run plans
    /// its own share, waits for the members, and finalises the submission with the totals.
    /// </summary>
    private async Task<(IntakeResult Result, int Members)> IntakeWithFanOutAsync(bool force, Action<FanOutHandle?> track, CancellationToken ct)
    {
        var dispatcher = _context.Dispatcher;
        var fanOut = Flow.Reliability.FanOut;
        var header = await Planner.OpenAsync(Flow, Mapping, Parameters, DropLocation, force, ct).ConfigureAwait(false);
        var manifest = header.Drop.Manifest;
        var applies = fanOut > 0 && dispatcher.Available && RunId is not null && manifest.Partitioned && header.Partitions >= 2 && !header.SkippedWholeRun
            && (manifest.RecordCount == 0 || manifest.RecordCount >= Flow.Reliability.FanOutMinRecords)
            && header.Partitions - 1 <= SubmissionIntake.MaxFanOutPartition;
        if (!applies)
        {
            if (fanOut > 0 && !header.SkippedWholeRun && manifest.PartitionCount >= 2 && !manifest.Partitioned)
            {
                _log.LogInformation("The drop is not declared partitioned, so its intake runs on this node alone with {Parallelism} renderer(s); declare it partitioned to spread it across {FanOut} member run(s).", Flow.Reliability.EffectiveRenderParallelism, fanOut);
            }

            return (await Intake.IntakeAsync(Flow, Mapping, Parameters, DropLocation, force, null, ct).ConfigureAwait(false), 0);
        }

        var shares = Split(header.Partitions, fanOut + 1);
        var members = shares.Skip(1).Where(s => s.Count > 0).Select(share => new RunParameters
        {
            Operation = RunParameters.IntakeOperation,
            Force = force,
            Drop = DropLocation,
            Values = Parameters,
            SubmissionId = manifest.SubmissionId,
            Partitions = share,
        }).ToList();

        // The submission is registered first, so every member finds it and the drop's identity is settled once.
        var own = await Intake.IntakeAsync(Flow, Mapping, Parameters, DropLocation, force, shares[0], ct).ConfigureAwait(false);
        if (own.AlreadyProcessed)
        {
            return (own, 0);
        }

        var handle = await dispatcher.EnqueueAsync(RunId!.Value, RunParameters.IntakeOperation, members, ct).ConfigureAwait(false);
        track(handle);
        _log.LogInformation("Fanned the intake of {Partitions} partition(s) out to {Members} member run(s) (group {GroupId}); this run planned partitions {Own}.", header.Partitions, members.Count, handle.GroupId, SubmissionIntake.DescribePartitions(shares[0]));
        var state = await WaitForMembersAsync(handle, "intake", ct).ConfigureAwait(false);
        track(null);

        var totals = own.Counts;
        foreach (var member in state.Members)
        {
            if (!member.Succeeded)
            {
                throw new DeliveryException($"Intake member {member.Slot} (run {member.RunId:D}) {member.Status}: {member.Error ?? "no error recorded"}. The submission stays planned as far as it got; re-run it to finish the intake.");
            }

            var outcome = IntakeOutcome.Parse(member.ResultJson)
                ?? throw new DeliveryException($"Intake member {member.Slot} (run {member.RunId:D}) reported no outcome.");
            totals = totals.Add(outcome.ToCounts());
        }

        var submission = await Intake.FinalizePlanningAsync(Flow, manifest.SubmissionId, Parameters, DropManifestSummary.Of(manifest), totals, ct).ConfigureAwait(false);
        return (new IntakeResult(submission, header, totals, AlreadyProcessed: false), members.Count);
    }

    /// <summary>
    /// The drain, with member drains across the fleet when the flow declares a fan-out and the submission is big
    /// enough: this run drains too, waits for the members, then settles whatever their leases or backoffs left.
    /// </summary>
    private async Task<(WorkerSummary Work, int Members)> DrainWithFanOutAsync(SubmissionState submission, Action<FanOutHandle?> track, CancellationToken ct)
    {
        var dispatcher = _context.Dispatcher;
        var fanOut = Flow.Reliability.FanOut;
        var members = 0;
        FanOutHandle? handle = null;
        if (fanOut > 0 && dispatcher.Available && RunId is not null && submission.Planned >= Flow.Reliability.FanOutMinRecords)
        {
            var parameters = Enumerable.Range(0, fanOut)
                .Select(_ => new RunParameters { Operation = RunParameters.DrainOperation, SubmissionId = submission.SubmissionId })
                .ToList();
            handle = await dispatcher.EnqueueAsync(RunId.Value, RunParameters.DrainOperation, parameters, ct).ConfigureAwait(false);
            track(handle);
            members = parameters.Count;
            _log.LogInformation("Fanned the drain of {Planned} record(s) in {Batches} batch(es) out to {Members} member run(s) (group {GroupId}); this run drains too.", submission.Planned, submission.BatchCount, members, handle.GroupId);
        }

        var worker = await WorkerAsync(ct).ConfigureAwait(false);
        var total = await worker.DrainAsync(submission.SubmissionId, ct).ConfigureAwait(false);
        if (handle is not null)
        {
            var state = await WaitForMembersAsync(handle, "drain", ct).ConfigureAwait(false);
            track(null);
            foreach (var member in state.Members.Where(m => !m.Succeeded))
            {
                _log.LogWarning("Drain member {Slot} (run {RunId}) {Status}: {Error}; its records are picked up here.", member.Slot, member.RunId, member.Status, member.Error ?? "no error recorded");
            }
        }

        // Whatever is left belongs to nobody alive: leases held by a member that died expire and are reclaimed,
        // records in backoff come due. Keep draining until the submission is settled or the run is cancelled.
        var ledger = RequireLedger();
        while (await ledger.HasPendingAsync(Flow.Id, submission.SubmissionId, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var reclaimed = await ledger.ReclaimExpiredLeasesAsync(Flow.Id, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            if (reclaimed > 0)
            {
                _log.LogInformation("Reclaimed {Count} record(s) whose lease expired.", reclaimed);
            }

            var more = await worker.DrainAsync(submission.SubmissionId, ct).ConfigureAwait(false);
            total = total.Add(more);
            if (more.Processed == 0 && more.Batches == 0)
            {
                if (!await ledger.HasPendingAsync(Flow.Id, submission.SubmissionId, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false))
                {
                    break;
                }

                _log.LogInformation("Records are still leased elsewhere; waiting {Seconds}s for the leases to settle.", (int)LeaseSettle.TotalSeconds);
                await Task.Delay(LeaseSettle, _context.Time, ct).ConfigureAwait(false);
            }
        }

        return (total, members);
    }

    private async Task<FanOutState> WaitForMembersAsync(FanOutHandle handle, string what, CancellationToken ct)
    {
        var dispatcher = _context.Dispatcher;
        var lastProgress = _context.Time.GetUtcNow();
        while (true)
        {
            var state = await dispatcher.StateAsync(handle, ct).ConfigureAwait(false);
            if (state.AllTerminal)
            {
                _log.LogInformation("The {What} members finished: {State}.", what, state);
                return state;
            }

            var now = _context.Time.GetUtcNow();
            if (now - lastProgress >= MemberProgress)
            {
                lastProgress = now;
                _log.LogInformation("Waiting for the {What} members: {State}.", what, state);
            }

            await Task.Delay(MemberPoll, _context.Time, ct).ConfigureAwait(false);
        }
    }

    private async Task CancelMembersAsync(FanOutHandle handle)
    {
        try
        {
            await _context.Dispatcher.CancelAsync(handle, CancellationToken.None).ConfigureAwait(false);
            _log.LogInformation("Cancelled the member runs of group {GroupId} with this run.", handle.GroupId);
        }
        catch (Exception ex) when (ex is DeliveryException or InvalidOperationException or System.Data.Common.DbException)
        {
            _log.LogWarning("Could not cancel the member runs of group {GroupId}: {Message}", handle.GroupId, ex.Message);
        }
    }

    /// <summary>Deals partitions round-robin over <paramref name="workers"/> shares; share 0 is the coordinator's own.</summary>
    public static IReadOnlyList<IReadOnlyList<int>> Split(int partitions, int workers)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(partitions);
        ArgumentOutOfRangeException.ThrowIfLessThan(workers, 1);
        var shares = Enumerable.Range(0, workers).Select(_ => new List<int>()).ToList();
        for (var p = 0; p < partitions; p++)
        {
            shares[p % workers].Add(p);
        }

        return shares;
    }

    private async Task<T> TrackAsync<T>(string kind, object? parameters, DeliveryKey? key, Func<Task<(T Result, string Summary, Guid? SubmissionId)>> action, CancellationToken ct)
    {
        var ledger = _context.Ledger;
        if (ledger is null)
        {
            return (await action().ConfigureAwait(false)).Result;
        }

        var started = _context.Time.GetUtcNow().UtcDateTime;
        var activity = await ledger.StartActivityAsync(new ActivityRecord
        {
            FlowId = Flow.Id,
            FlowName = Flow.Name,
            Kind = kind,
            Actor = Actor,
            StartedUtc = started,
            ParametersJson = parameters is null ? null : JsonSerializer.Serialize(parameters),
            DeliveryKey = key?.Value,
            RunId = RunId,
        }, ct).ConfigureAwait(false);

        try
        {
            var (result, summary, submissionId) = await action().ConfigureAwait(false);
            await CompleteActivityAsync(ledger, activity.ActivityId, "completed", summary, submissionId).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await CompleteActivityAsync(ledger, activity.ActivityId, "cancelled", "cancelled", null).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is DeliveryException or HttpRequestException or IOException or InvalidOperationException)
        {
            await CompleteActivityAsync(ledger, activity.ActivityId, "failed", HeaderRedaction.RedactMessage(ex.Message), null).ConfigureAwait(false);
            throw;
        }
    }

    private async Task CompleteActivityAsync(ILedger ledger, long activityId, string outcome, string summary, Guid? submissionId)
    {
        var log = ActivityLog?.Invoke();
        if (submissionId is { } s)
        {
            summary = $"{summary} (submission {s:D})";
        }

        await ledger.CompleteActivityAsync(activityId, outcome, summary, log, _context.Time.GetUtcNow().UtcDateTime, submissionId, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task EmitAsync(string kind, IReadOnlyList<DeliveryKey>? keys, string detail, CancellationToken ct)
    {
        if (keys is null)
        {
            await _context.Listener.OnEventAsync(new DeliveryEvent { AtUtc = _context.Time.GetUtcNow().UtcDateTime, FlowId = Flow.Id, FlowName = Flow.Name, Kind = kind, Worker = Actor, Detail = detail + " (all blocked records)" }, ct).ConfigureAwait(false);
            return;
        }

        foreach (var key in keys)
        {
            await _context.Listener.OnEventAsync(new DeliveryEvent { AtUtc = _context.Time.GetUtcNow().UtcDateTime, FlowId = Flow.Id, FlowName = Flow.Name, Kind = kind, DeliveryKey = key, Worker = Actor, Detail = detail }, ct).ConfigureAwait(false);
        }
    }

    private ILedger RequireLedger()
        => _context.Ledger ?? throw new DeliveryException(
            "This operation needs the ledger, which lives in the catalog database. Run it through the control plane, or on a node or CLI started with the catalog connection; without a catalog only validate, plan and snapshot capture are available.");

    public void Dispose() => _http?.Dispose();
}
