using System.Text.Json;
using Microsoft.Extensions.Logging;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Drops;
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
    IDeliveryListener Listener)
{
    /// <summary>The environment switch that lets a flow target a loopback address (local OSDU emulators, tests).</summary>
    public const string AllowLoopbackVariable = "SQLFLOW_DELIVERY_ALLOW_LOOPBACK";

    /// <summary>Whether flows may target a loopback address in this process (the switch above, read once per check).</summary>
    public static bool LoopbackAllowed
        => Environment.GetEnvironmentVariable(AllowLoopbackVariable) is { } v && v.Equals("true", StringComparison.OrdinalIgnoreCase);

    /// <summary>A context with a different logger factory: the node swaps in the run log for one run.</summary>
    public EngineContext WithLoggers(ILoggerFactory loggers) => this with { Loggers = loggers };
}

public sealed record RunResult(IntakeResult Intake, WorkerSummary Work, SubmissionState Submission);

/// <summary>
/// One flow, resolved and ready: parameters applied, render inputs pinned (from the mappings and snapshots the
/// flow's repository layout locates), and (when a ledger and a target are wired) the protocol, worker, verifier
/// and publisher over them. Every operation an operator can trigger goes through here and is recorded in the
/// ledger's activity trail with the <see cref="Actor"/> that asked for it and the platform <see cref="RunId"/>
/// it ran as.
/// </summary>
public sealed class FlowRuntime : IDisposable
{
    private readonly EngineContext _context;
    private readonly ResolvedMapping? _mapping;
    private readonly string? _drop;
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
    /// release, redeliver, known-state, probe): no parameters are required and no mapping is resolved, so they
    /// work for a flow whose drop parameters are unknown or whose snapshots are not on this host.
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

    /// <summary>Renders the drop and reports what would change. Changes nothing, records nothing.</summary>
    public Task<DeliveryPlan> PlanAsync(bool force = false, CancellationToken ct = default)
        => Planner.PlanAsync(Flow, Mapping, Parameters, DropLocation, force, ct);

    public SubmissionIntake Intake => new(RequireLedger(), Planner, _context.Time, _context.Listener, _context.Loggers.CreateLogger<SubmissionIntake>()) { RunId = RunId };

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
        => new(RequireLedger(), _context.Drops, await ProtocolAsync(ct).ConfigureAwait(false), Flow, _context.Time, _context.Listener, _context.Loggers.CreateLogger<DeliveryWorker>()) { RunId = RunId };

    public async Task<Verifier> VerifierAsync(CancellationToken ct = default)
        => new(RequireLedger(), await ProtocolAsync(ct).ConfigureAwait(false), Flow, _context.Time, _context.Listener, _context.Loggers.CreateLogger<Verifier>());

    public KnownStatePublisher Publisher => new(RequireLedger(), _context.Stores, _context.Time, _context.Loggers.CreateLogger<KnownStatePublisher>());

    /// <summary>Intake plus drain: the <c>deliver</c> operation.</summary>
    public Task<RunResult> RunAsync(bool force, CancellationToken ct = default)
        => TrackAsync("deliver", new { force, drop = DropLocation, parameters = Parameters }, null, async () =>
        {
            var intake = await Intake.IntakeAsync(Flow, Mapping, Parameters, DropLocation, force, ct).ConfigureAwait(false);
            if (intake.NothingToDo)
            {
                return (new RunResult(intake, WorkerSummary.Empty, intake.Submission), SubmissionIntake.Summarize(intake.Submission), intake.Submission.SubmissionId);
            }

            var worker = await WorkerAsync(ct).ConfigureAwait(false);
            var work = await worker.DrainAsync(intake.Submission.SubmissionId, ct).ConfigureAwait(false);
            var submission = await Intake.CompleteAsync(intake.Submission.SubmissionId, Flow.Id, ct).ConfigureAwait(false);
            return (new RunResult(intake, work, submission), SubmissionIntake.Summarize(submission), submission.SubmissionId);
        }, ct);

    /// <summary>Intake only: register and plan the drop, leave the delivery to a later drain.</summary>
    public Task<IntakeResult> SubmitAsync(bool force, CancellationToken ct = default)
        => TrackAsync("submit", new { force, drop = DropLocation, parameters = Parameters }, null, async () =>
        {
            var intake = await Intake.IntakeAsync(Flow, Mapping, Parameters, DropLocation, force, ct).ConfigureAwait(false);
            return (intake, SubmissionIntake.Summarize(intake.Submission), intake.Submission.SubmissionId);
        }, ct);

    /// <summary>Drains the pending records of the flow (one pass, or until nothing is due), optionally of one submission.</summary>
    public Task<WorkerSummary> WorkAsync(bool once, Guid? submissionId = null, CancellationToken ct = default)
        => TrackAsync("work", new { once, submissionId }, null, async () =>
        {
            var worker = await WorkerAsync(ct).ConfigureAwait(false);
            var summary = once ? await worker.PassAsync(submissionId, ct).ConfigureAwait(false) : await worker.DrainAsync(submissionId, ct).ConfigureAwait(false);
            if (submissionId is { } s)
            {
                await Intake.CompleteAsync(s, Flow.Id, ct).ConfigureAwait(false);
            }

            return (summary, $"{summary.Processed} processed: {summary.Delivered} delivered, {summary.Retried} retrying later, {summary.Held} held, {summary.Failed} failed", submissionId);
        }, ct);

    /// <summary>The drift pass: the <c>verify</c> operation.</summary>
    public Task<VerifySummary> VerifyAsync(int max, TimeSpan? notVerifiedWithin, bool reconcile, IReadOnlyList<DeliveryKey>? keys = null, CancellationToken ct = default)
        => TrackAsync("verify", new { max, notVerifiedWithin, reconcile, keys = keys?.Select(k => k.ToString()).ToList() }, keys is { Count: 1 } ? keys[0] : null, async () =>
        {
            var verifier = await VerifierAsync(ct).ConfigureAwait(false);
            var summary = await verifier.RunAsync(max, notVerifiedWithin, reconcile, keys, ct).ConfigureAwait(false);
            return (summary, summary.ToString(), (Guid?)null);
        }, ct);

    public Task<int> PublishKnownStateAsync(string to, CancellationToken ct = default)
        => TrackAsync("known-state", new { to }, null, async () =>
        {
            var count = await Publisher.PublishAsync(Flow, to, ct).ConfigureAwait(false);
            return (count, $"published {count} record(s) to {to}", (Guid?)null);
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
    /// Removes a record from OSDU through the flow's protocol (logical delete, or a purge), then records it in the
    /// ledger. The record stays blocked from redelivery until the source changes or an operator releases it.
    /// </summary>
    public Task<DeleteOutcome> DeleteAsync(DeliveryKey key, bool purge, CancellationToken ct = default)
        => TrackAsync("delete", new { key = key.ToString(), purge }, key, async () =>
        {
            var ledger = RequireLedger();
            var record = await ledger.GetRecordAsync(Flow.Id, key, ct).ConfigureAwait(false)
                ?? throw new DeliveryException($"Record {key} is not in the ledger for flow '{Flow.Name}'.");
            if (record.TargetId is null)
            {
                throw new DeliveryException($"Record {key} has no OSDU id; nothing to delete.");
            }

            var protocol = await ProtocolAsync(ct).ConfigureAwait(false);
            var outcome = await protocol.DeleteAsync(record.TargetId, purge, ct).ConfigureAwait(false);
            await ledger.MarkDeletedAsync(key, purge, Actor, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            await _context.Listener.OnEventAsync(new DeliveryEvent
            {
                AtUtc = _context.Time.GetUtcNow().UtcDateTime,
                FlowId = Flow.Id,
                FlowName = Flow.Name,
                Kind = "record.deleted",
                SubmissionId = record.LastSubmissionId,
                DeliveryKey = key,
                SourceKey = record.SourceKey,
                Label = record.Label,
                TargetId = record.TargetId,
                TargetVersion = record.TargetVersion,
                Worker = Actor,
                Phase = "delete",
                Detail = outcome.Detail,
            }, ct).ConfigureAwait(false);
            return (outcome, $"{record.TargetId}: {outcome.Detail}", record.LastSubmissionId);
        }, ct);

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

        await ledger.CompleteActivityAsync(activityId, outcome, summary, log, _context.Time.GetUtcNow().UtcDateTime, CancellationToken.None).ConfigureAwait(false);
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
