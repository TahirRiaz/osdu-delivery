using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.FanOut;
using SqlFlow.Delivery.Engine.Intake;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Verify;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Storage;

namespace SqlFlow.Delivery.Engine;

/// <summary>The shared, flow-independent services the engine is composed from.</summary>
public sealed record EngineContext(
    DeliveryDocumentLoader Documents,
    IIngestionSourceFactory Sources,
    IPayloadFiles Payloads,
    FileStoreRegistry Stores,
    ISecretResolver Secrets,
    ILedger? Ledger,
    TimeProvider Time,
    ILoggerFactory Loggers,
    IProtocolFactory Protocols,
    IDeliveryListener Listener,
    IFanOutDispatcher? FanOut = null,
    Templates.ITemplateStore? Templates = null,
    ICacheStore? Cache = null,
    IRecordSearchFactory? Searches = null)
{
    /// <summary>The environment switch that lets a flow target a loopback address (local OSDU emulators, tests).</summary>
    public const string AllowLoopbackVariable = "SQLFLOW_DELIVERY_ALLOW_LOOPBACK";

    /// <summary>Whether flows may target a loopback address in this process (the switch above, read once per check).</summary>
    public static bool LoopbackAllowed
        => Environment.GetEnvironmentVariable(AllowLoopbackVariable) is { } v && v.Equals("true", StringComparison.OrdinalIgnoreCase);

    /// <summary>The fan-out dispatcher, never null: a run the platform gave no fan-out does its work itself.</summary>
    public IFanOutDispatcher Dispatcher => FanOut ?? NoFanOutDispatcher.Instance;

    /// <summary>Where a flow's search sources are answered, never null: the platform the flow delivers to, unless composed otherwise.</summary>
    public IRecordSearchFactory RecordSearches => Searches ?? new PlatformRecordSearchFactory(Loggers);

    /// <summary>A context with a different logger factory: the node swaps in the run log for one run.</summary>
    public EngineContext WithLoggers(ILoggerFactory loggers) => this with { Loggers = loggers };

    /// <summary>
    /// A context whose references resolve from the central configuration a run carried before they resolve from the
    /// node. A run given nothing keeps the node's own resolver, so it behaves exactly as it did before.
    /// </summary>
    public EngineContext WithSuppliedReferences(IReadOnlyDictionary<string, string> supplied)
        => this with { Secrets = Http.SuppliedReferenceResolver.For(supplied, Secrets) };

    /// <summary>A context with the fan-out the platform handed this run; every run gets its own.</summary>
    public EngineContext WithFanOut(IFanOutDispatcher? dispatcher) => this with { FanOut = dispatcher };

    /// <summary>A context whose loggers say, on every line, that it is about <paramref name="interfaceName"/>.</summary>
    public EngineContext ForInterface(string interfaceName) => this with { Loggers = new InterfaceLoggerFactory(Loggers, interfaceName) };
}

/// <summary>What a deliver run did: the intake, the drain, the submission it left, and how far it fanned out.</summary>
public sealed record RunResult(IntakeResult Intake, WorkerSummary Work, SubmissionState Submission, int IntakeMembers = 0, int DrainMembers = 0);

/// <summary>
/// One flow, resolved and ready: parameters applied, render inputs pinned (the mapping from the flow's repository, the
/// template and the cache from the catalog), and (when a ledger and a target are wired) the source, protocol, worker and
/// verifier over them. Every operation an operator can trigger goes through here and is recorded in the ledger's
/// activity trail with the <see cref="Actor"/> that asked for it and the platform <see cref="RunId"/> it ran as. A
/// deliver run coordinates its own fan-out (docs/stage4-design.md section 2.6): intake slices first, then drains, each a
/// member run of the same flow on any node of the pool, waited for and completed here.
/// </summary>
public sealed class FlowRuntime : IDisposable
{
    private static readonly TimeSpan MemberPoll = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan MemberProgress = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan LeaseSettle = TimeSpan.FromSeconds(30);

    /// <summary>How long past a stopped run's lease a run waits before reclaiming it, so the lease has run out by then.</summary>
    private static readonly TimeSpan LeaseExpiryMargin = TimeSpan.FromSeconds(1);

    /// <summary>Settled submissions whose released records one deliver run sends after its own.</summary>
    private const int SettledSubmissionsPerRun = 10;

    private readonly EngineContext _context;
    private readonly ResolvedMapping? _mapping;
    private readonly ILogger _log;
    private readonly TargetConnection _target;

    /// <summary>Where the mapping's search sources are answered; disposed with the runtime when it holds anything to release.</summary>
    private readonly IRecordSearch? _search;
    private IDeliveryProtocol? _protocol;
    private IIngestionSource? _source;
    private Planner? _planner;

    /// <summary>True once the mapping's legal tags were asked about for this runtime (a source's preflight asks up front).</summary>
    private bool _legalTagsChecked;

    /// <summary>Which records this interface's records wait for, worked out once per runtime.</summary>
    private WaitRules? _waits;

    private FlowRuntime(
        EngineContext context,
        FlowDefinition flow,
        DeliveryLayout layout,
        MappingCatalog mappings,
        IReadOnlyDictionary<string, string> parameters,
        ResolvedMapping? mapping,
        TargetConnection target,
        IRecordSearch? search)
    {
        _context = context;
        Flow = flow;
        Layout = layout;
        Mappings = mappings;
        Parameters = parameters;
        _mapping = mapping;
        _target = target;
        _search = search;
        _log = context.Loggers.CreateLogger("run");
    }

    public FlowDefinition Flow { get; }

    public DeliveryLayout Layout { get; }

    public MappingCatalog Mappings { get; }

    public IReadOnlyDictionary<string, string> Parameters { get; }

    /// <summary>The pinned render inputs. Only a runtime opened with <see cref="CreateAsync(EngineContext, FlowDefinition, IReadOnlyDictionary{string, string}?, CancellationToken)"/> has them.</summary>
    public ResolvedMapping Mapping => _mapping ?? throw new DeliveryException("This operation renders records and needs the flow's mapping, template and cache; the runtime was opened for target operations only.");

    /// <summary>True when the runtime can read the flow's source and render (deliver, plan, intake), false for target-only work.</summary>
    public bool ReadsSource => _mapping is not null;

    public EngineContext Context => _context;

    /// <summary>Which records this run reads: an incremental window by default, or what the run asked for.</summary>
    public SourceSelection Selection { get; set; } = SourceSelection.Incremental(null);

    /// <summary>Who is asking: the run's trigger source (manual:&lt;user&gt;, schedule:&lt;name&gt;), gui:&lt;user&gt; for
    /// an intervention, cli:&lt;user&gt; on a workstation. Recorded on every activity.</summary>
    public string Actor { get; set; } = "unknown";

    /// <summary>The platform run this runtime executes for, when it is one; stamped on activities and attempts.</summary>
    public Guid? RunId { get; set; }

    /// <summary>Supplies the captured log for the activity being completed (the executor sets it).</summary>
    public Func<string?>? ActivityLog { get; set; }

    /// <summary>The submission the run works on, when it names one: a re-run, or a fan-out member's.</summary>
    public Guid? SubmissionId { get; set; }

    /// <summary>The key slices a fan-out member plans of its coordinating run's submission; null plans the whole read.</summary>
    public IReadOnlyList<int>? Slices { get; set; }

    /// <summary>
    /// What judges this run's records for the interface as a whole, or null when nothing does: the planning reports what it
    /// held, the worker every record it settles. Whoever sets it cancels the run's token when it trips.
    /// </summary>
    public FailureGuard? Guard { get; set; }

    /// <summary>
    /// The source document this interface belongs to, when the caller has it (the executor and a source's run set it). A
    /// runtime without it reads the document from the flow's file when it needs the other interfaces.
    /// </summary>
    public SourceDefinition? SourceDocument { get; set; }

    /// <summary>Loads and resolves a flow. Fails at parse time with the file path on every message.</summary>
    public static async Task<FlowRuntime> CreateAsync(EngineContext context, string flowPath, IReadOnlyDictionary<string, string>? parameters, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var flow = context.Documents.LoadFlow(flowPath);
        return await CreateAsync(context, flow, parameters, ct).ConfigureAwait(false);
    }

    public static async Task<FlowRuntime> CreateAsync(EngineContext context, FlowDefinition flow, IReadOnlyDictionary<string, string>? parameters, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(flow);
        var values = FlowParameters.Resolve(flow, parameters);
        var layout = DeliveryLayout.Resolve(flow);
        var mappings = new MappingCatalog(layout.MappingsDirectory, context.Documents);

        // A search source asks the platform the flow delivers to, under the flow's own auth and partition, over the
        // connection the runtime delivers with; nothing is sent until a render asks its first question.
        var target = new TargetConnection(context, flow);
        IRecordSearch? search = null;
        try
        {
            search = context.RecordSearches.Create(flow, target.ClientAsync);
            var resolver = new RenderResolver(mappings, context.Cache, context.Templates, context.Secrets, search);
            var mapping = await resolver.ResolveAsync(flow, ct).ConfigureAwait(false);
            return new FlowRuntime(context, flow, layout, mappings, values, mapping, target, search);
        }
        catch
        {
            (search as IDisposable)?.Dispose();
            target.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A runtime for the operations that touch the target and the ledger but never the source (verify, delete,
    /// release, redeliver, probe, drain): no parameters are required and no mapping is resolved, so they work for a
    /// flow whose parameters are unknown or whose mapping could not render right now.
    /// </summary>
    public static FlowRuntime ForTarget(EngineContext context, FlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(flow);
        var layout = DeliveryLayout.Resolve(flow);
        return new FlowRuntime(
            context, flow, layout, new MappingCatalog(layout.MappingsDirectory, context.Documents),
            new Dictionary<string, string>(StringComparer.Ordinal), null, new TargetConnection(context, flow), null);
    }

    /// <summary>
    /// What this runtime's interface delivers and what its records refer to: the kind its mapping fills and the
    /// relationships of the properties it fills, read from the template the mapping pins (docs/interfaces-design.md
    /// section 6). A runtime opened for target operations reads the mapping and its template here.
    /// </summary>
    public async Task<InterfaceSchema> SchemaAsync(CancellationToken ct = default)
    {
        var name = Flow.Interface ?? Flow.Name;
        if (_mapping is { } resolved)
        {
            return InterfaceSchemas.Describe(name, resolved.Mapping, Templates.OsduTemplate.From(resolved.Schema));
        }

        var mapping = Mappings.Load(Flow.Render.Mapping);
        var templates = _context.Templates ?? throw new FlowValidationException(
            $"{KeyPaths.Where(Flow)}: a source's interfaces are ordered by the relationships in the templates their mappings pin, and templates live in the module's database, which this host was started without. Start it with the module's connection (Osdu:Database:Connection or SQLFLOW_OSDU_DB), or with --db when the catalog's database holds the osdu schema.");
        var schema = await templates.LoadAsync(mapping.Template, ct).ConfigureAwait(false)
            ?? throw new FlowValidationException(
                $"{KeyPaths.Where(Flow)}: mapping {mapping.Reference} pins template {mapping.Template}, which is not saved. Save it on the Templates page, or with 'sqlflow template import'.");
        return InterfaceSchemas.Describe(name, mapping, Templates.OsduTemplate.From(schema));
    }

    /// <summary>The flow's ingestion tables, opened once per runtime with the flow's own connection reference.</summary>
    public IIngestionSource Source => _source ??= _context.Sources.Open(Flow, Parameters);

    public Planner Planner => _planner ??= new Planner(Source, _context.Payloads, _context.Ledger, _context.Loggers.CreateLogger<Planner>());

    public SubmissionIntake Intake => new(RequireLedger(), Planner, _context.Stores, _context.Time, _context.Listener, _context.Loggers.CreateLogger<SubmissionIntake>()) { RunId = RunId };

    /// <summary>What this run asks the intake for: its selection, the submission it works on, and a member's slices.</summary>
    public IntakeRequest Request => new(Selection, SubmissionId, Slices);

    /// <summary>Reads the source and reports what would change, every entry collected. Changes nothing, records nothing.</summary>
    public Task<DeliveryPlan> PlanAsync(bool force = false, CancellationToken ct = default)
        => Planner.PlanAsync(Flow, Mapping, Parameters, Selection, force, ct);

    /// <summary>
    /// Checks that the flow's route can deliver <paramref name="kind"/>, the kind its mapping renders
    /// (<see cref="RouteChecks"/>). A flow on the ddms route that names a DDMS by its registration has its protocol built
    /// first, which reads the registration, so the check sees what the DDMS registered. A flow on the dspdm route has its
    /// business object read from DSPDM's metadata, which says whether it exists and how its rows are found again.
    /// </summary>
    public async Task CheckRouteAsync(string kind, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (Flow.Target.Protocol == DeliveryProtocol.Dspdm)
        {
            RouteChecks.Check(Flow, kind);
            if (await ProtocolAsync(ct).ConfigureAwait(false) is OsduDspdmProtocol dspdm)
            {
                await dspdm.CheckKindAsync(kind, ct).ConfigureAwait(false);
            }

            return;
        }
        if (DeliveryProtocols.ReachesDdms(Flow.Target.Protocol) && DdmsDiscovery.Needed(Flow))
        {
            var routing = await ProtocolAsync(ct).ConfigureAwait(false) switch
            {
                OsduDdmsProtocol ddms => ddms.Routing,
                OsduFileAndDdmsProtocol files => files.Routing,
                OsduManifestAndDdmsProtocol manifests => manifests.Routing,
                _ => null,
            };
            if (routing is not null)
            {
                RouteChecks.Check(Flow, kind, routing);
                return;
            }
        }

        RouteChecks.Check(Flow, kind);
    }

    public async Task<IDeliveryProtocol> ProtocolAsync(CancellationToken ct = default)
    {
        if (_protocol is not null)
        {
            return _protocol;
        }

        _protocol = await _context.Protocols.CreateAsync(Flow, _target.Http, ct).ConfigureAwait(false);
        return _protocol;
    }

    public async Task<DeliveryWorker> WorkerAsync(CancellationToken ct = default)
        => new(RequireLedger(), _context.Payloads, _context.Stores, await ProtocolAsync(ct).ConfigureAwait(false), Flow, _context.Time, _context.Listener, _context.Loggers.CreateLogger<DeliveryWorker>())
        {
            RunId = RunId,
            Guard = Guard,
            Waits = await WaitRulesAsync(ct).ConfigureAwait(false),
            References = await ReferenceCheckAsync(ct).ConfigureAwait(false),
        };

    /// <summary>The storage check a flow with <c>target.verifyReferences: storage</c> runs before it sends a record, or null.</summary>
    private async Task<ReferenceCheck?> ReferenceCheckAsync(CancellationToken ct)
    {
        if (Flow.Target.VerifyReferences != ReferenceVerification.Storage)
        {
            return null;
        }

        var client = await _target.ClientAsync(ct).ConfigureAwait(false);
        return new ReferenceCheck(RequireLedger(), client, Flow.Target.ProtocolOptions.VerifyBatchPath ?? OsduRecordProtocol.DefaultVerifyBatchPath);
    }

    /// <summary>
    /// Which records this interface's records wait for (docs/interfaces-design.md section 7): every record of the ledger
    /// still to land, except those of the interfaces of the same source that the source's order does not wait for, because
    /// the two refer to each other and the order says which goes first. Worked out from every interface of the source,
    /// whichever the run selected, so a run of one interface, and a fan-out member, wait exactly as a run of the source does.
    /// </summary>
    public async Task<WaitRules> WaitRulesAsync(CancellationToken ct = default)
    {
        if (_waits is not null)
        {
            return _waits;
        }

        var source = Flow.Interface is null
            ? null
            : SourceDocument ?? (Flow.SourcePath is { } path ? _context.Documents.LoadSource(path) : null);
        if (source is null || source.Interfaces.Count < 2)
        {
            return _waits = WaitRules.WaitForAll;
        }

        var schemas = new List<InterfaceSchema>(source.Interfaces.Count);
        foreach (var sibling in source.Interfaces)
        {
            if (string.Equals(sibling.Interface, Flow.Interface, StringComparison.OrdinalIgnoreCase))
            {
                schemas.Add(await SchemaAsync(ct).ConfigureAwait(false));
                continue;
            }

            using var other = ForTarget(_context, sibling);
            schemas.Add(await other.SchemaAsync(ct).ConfigureAwait(false));
        }

        var names = source.Interfaces.Select(i => i.Interface!).ToList();
        var order = InterfaceOrder.Plan(names, InterfaceOrder.Declared(source), schemas);
        var notWaitedFor = order.NotWaitedFor
            .Where(d => string.Equals(d.Interface, Flow.Interface, StringComparison.OrdinalIgnoreCase))
            .Select(d => source.Interface(d.DependsOn).Id)
            .ToHashSet();
        return _waits = new WaitRules { NotWaitedFor = notWaitedFor };
    }

    /// <summary>
    /// Sends back to pending the flow's waiting records whose wait ended while nothing released them (the record they
    /// wait for was found landed by a verify, or left the ledger), so this run takes them with the rest.
    /// </summary>
    private async Task ReleaseEndedWaitsAsync(CancellationToken ct)
    {
        var released = await RequireLedger().ReleaseResolvedWaitsAsync(Flow.Id, null, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        if (released > 0)
        {
            _log.LogInformation("{Count} waiting record(s) have nothing left to wait for, and go out with this run.", released);
        }
    }

    public async Task<Verifier> VerifierAsync(CancellationToken ct = default)
        => new(RequireLedger(), await ProtocolAsync(ct).ConfigureAwait(false), Flow, _context.Time, _context.Listener, _context.Loggers.CreateLogger<Verifier>());

    /// <summary>Intake plus drain, fanned out across the fleet when the flow asks for it: the <c>deliver</c> operation.</summary>
    public Task<RunResult> RunAsync(bool force, CancellationToken ct = default)
        => TrackAsync("deliver", new { force, source = Flow.Source.Record.Object, selection = Selection.Describe(), parameters = Parameters, fanOut = Flow.Reliability.FanOut }, null, async () =>
        {
            await EnsureLegalTagsAsync(ct).ConfigureAwait(false);
            await ReleaseEndedWaitsAsync(ct).ConfigureAwait(false);
            FanOutHandle? handle = null;
            try
            {
                var (intake, intakeMembers) = await IntakeWithFanOutAsync(force, h => handle = h, ct).ConfigureAwait(false);
                handle = null;
                if (!intake.AlreadyProcessed)
                {
                    // What the planning could not render counts before anything is sent; a guard it trips cancels the run here.
                    Guard?.Planned(intake.Counts.Held, intake.Counts.Planned);
                    ct.ThrowIfCancellationRequested();
                }

                if (intake.NothingToDo)
                {
                    // A plan with nothing new is not a run with nothing to send: a record released back to pending with
                    // its rendered document, or one a stopped run left due, still waits in this submission, and the plan
                    // skips it because the source row is exactly what it already queues. What is due now is sent; a record
                    // in backoff is not waited for, because a run that planned nothing must not sit out a retry's wait.
                    var worker = await WorkerAsync(ct).ConfigureAwait(false);
                    var sent = await PassUntilNothingClaimableAsync(worker, intake.Submission.SubmissionId, ct).ConfigureAwait(false);
                    sent = sent.Add(await SendOrphanedLeasesAsync(worker, intake.Submission.SubmissionId, ct).ConfigureAwait(false));
                    var leftovers = await SendSettledLeftoversAsync(worker, intake.Submission.SubmissionId, ct).ConfigureAwait(false);
                    if (sent.Processed == 0 && leftovers.Processed == 0 && sent.Waiting == 0 && leftovers.Waiting == 0)
                    {
                        return (new RunResult(intake, WorkerSummary.Empty, intake.Submission, intakeMembers), SubmissionIntake.Summarize(intake.Submission), intake.Submission.SubmissionId);
                    }

                    var settled = sent.Processed > 0 || sent.Waiting > 0
                        ? await Intake.CompleteAsync(intake.Submission.SubmissionId, Flow.Id, ct).ConfigureAwait(false)
                        : intake.Submission;
                    return (new RunResult(intake, sent.Add(leftovers), settled, intakeMembers), SubmissionIntake.Summarize(settled), settled.SubmissionId);
                }

                var (work, drainMembers) = await DrainWithFanOutAsync(intake.Submission, h => handle = h, ct).ConfigureAwait(false);
                handle = null;
                var submission = await Intake.CompleteAsync(intake.Submission.SubmissionId, Flow.Id, ct).ConfigureAwait(false);

                // Its own records sent, the run takes what settled submissions still hold (records released after their run).
                var settledLeftovers = await SendSettledLeftoversAsync(await WorkerAsync(ct).ConfigureAwait(false), intake.Submission.SubmissionId, ct).ConfigureAwait(false);
                return (new RunResult(intake, work.Add(settledLeftovers), submission, intakeMembers, drainMembers), SubmissionIntake.Summarize(submission), submission.SubmissionId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                if (handle is not null)
                {
                    // A cancelled root takes its members with it.
                    await CancelMembersAsync(handle).ConfigureAwait(false);
                }

                await SettleStoppedAsync().ConfigureAwait(false);
                throw;
            }
        }, ct);

    /// <summary>
    /// Closes the submission this run was delivering when it stopped (its failure guard tripped, or the run was cancelled),
    /// failed with why, while records of it are still pending, so the flow's next run sends them as it sends what any
    /// closed submission still holds. It is bookkeeping after the stop, so it runs whatever the run's token says; a ledger
    /// that cannot be reached leaves the submission open, and a drain of it sends the rest.
    /// </summary>
    private async Task SettleStoppedAsync()
    {
        if (SubmissionId is not { } submissionId || _context.Ledger is null)
        {
            return;
        }

        var reason = Guard?.Reason is { } tripped
            ? $"stopped: {tripped}"
            : "stopped: the run was cancelled before its records were all sent";
        try
        {
            await Intake.StopAsync(submissionId, Flow.Id, reason, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DeliveryException or System.Data.Common.DbException or InvalidOperationException or TimeoutException)
        {
            _log.LogWarning(
                "Submission {SubmissionId} stays open after the stop ({Message}); a drain of it sends the records it still holds.",
                submissionId, HeaderRedaction.RedactMessage(ex.Message));
        }
    }

    /// <summary>Intake only: plan this run's records (or a member's share of the slices) into work batches.</summary>
    public Task<IntakeResult> IntakeAsync(bool force, CancellationToken ct = default)
        => TrackAsync("intake", new { force, source = Flow.Source.Record.Object, selection = Selection.Describe(), parameters = Parameters, slices = Slices is null ? null : KeySlices.Describe(Slices) }, null, async () =>
        {
            await EnsureLegalTagsAsync(ct).ConfigureAwait(false);
            var intake = await Intake.IntakeAsync(Flow, Mapping, Parameters, Request, force, ct).ConfigureAwait(false);
            return (intake, intake.AlreadyProcessed ? SubmissionIntake.Summarize(intake.Submission) : intake.Counts.ToString(), intake.Submission.SubmissionId);
        }, ct);

    /// <summary>Drains the pending work of the flow (one pass, or until nothing is due), optionally of one submission: the <c>drain</c> operation.</summary>
    public Task<WorkerSummary> WorkAsync(bool once, Guid? submissionId = null, CancellationToken ct = default)
        => TrackAsync("drain", new { once, submissionId }, null, async () =>
        {
            await ReleaseEndedWaitsAsync(ct).ConfigureAwait(false);
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

    /// <summary>Releases held, failed or deleted records (all of them when <paramref name="keys"/> is null).</summary>
    public Task<int> ReleaseAsync(IReadOnlyList<DeliveryKey>? keys, CancellationToken ct = default)
        => TrackAsync("release", new { keys = keys?.Select(k => k.ToString()).ToList() }, keys is { Count: 1 } ? keys[0] : null, async () =>
        {
            var released = await RequireLedger().ReleaseAsync(Flow.Id, keys, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            await EmitAsync("record.released", keys, $"released by {Actor}", ct).ConfigureAwait(false);
            return (released, $"released {released} record(s)", (Guid?)null);
        }, ct);

    /// <summary>
    /// Marks records for redelivery (design.md section 7.6: forget what OSDU holds so the next plan re-sends), and asks
    /// the flow's next run to plan them again.
    /// </summary>
    public Task<int> RedeliverAsync(IReadOnlyList<DeliveryKey> keys, RedeliverScope scope, CancellationToken ct = default)
        => RedeliverAsync(keys, new RedeliverSelection(scope, []), ct);

    /// <summary>Marks records for redelivery of what <paramref name="selection"/> names, parts of a payload sent in parts included.</summary>
    public Task<int> RedeliverAsync(IReadOnlyList<DeliveryKey> keys, RedeliverSelection selection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(selection);
        return TrackAsync("redeliver", new { keys = keys.Select(k => k.ToString()).ToList(), scope = selection.Scope.ToString(), parts = selection.Parts }, keys.Count == 1 ? keys[0] : null, async () =>
        {
            var marked = await RequireLedger().ForceRedeliverAsync(Flow.Id, keys, selection, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            await EmitAsync("record.redeliver", keys, $"redelivery of {selection} requested by {Actor}", ct).ConfigureAwait(false);
            return (marked, $"marked {marked} record(s) for redelivery of {selection}", (Guid?)null);
        }, ct);
    }

    /// <summary>
    /// The record keys the ledger asked to be planned again, paged in key order: what a run turns into a key-scoped
    /// selection so a release, a redelivery and a cache rollout reach the records they marked.
    /// </summary>
    public async Task<IReadOnlyList<KeyTuple>> PlanRequestedKeysAsync(int max, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        var ledger = RequireLedger();
        var keys = new List<KeyTuple>();
        DeliveryKey? after = null;
        while (keys.Count < max)
        {
            var page = await ledger.ListPlanRequestedAsync(Flow.Id, after, Math.Min(max - keys.Count, 500), ct).ConfigureAwait(false);
            if (page.Count == 0)
            {
                break;
            }

            foreach (var record in page)
            {
                if (record.SourceKeyJson is { } json)
                {
                    keys.Add(KeyTuple.FromJson(json));
                }
                else
                {
                    _log.LogWarning(
                        "Record {Key} waits to be planned again but the ledger holds no source key for it (it predates the ingestion source); deliver the scope to plan it.",
                        record.DeliveryKey);
                }
            }

            after = page[^1].DeliveryKey;
        }

        return keys;
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
                    else if (!string.Equals(record.ClaimedTargetId, record.TargetId, StringComparison.Ordinal))
                    {
                        // Only an id the flow claimed is the flow's to remove: a record that was only ever held never
                        // wrote to OSDU, and the id it names can be another flow's record.
                        results.Add(RemovalRecordResult.Skipped(
                            key, record.SourceKey, record.Label, null,
                            "the record never queued a document, so this flow wrote nothing to OSDU to remove", record.LastSubmissionId));
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

                // One correlation id for the chunk's calls, named on each removal attempt, so a removal can be followed
                // into OSDU's own logs like a delivery.
                using var correlation = OsduCorrelation.Begin();
                var outcomes = await protocol.DeleteBatchAsync(removals, scope, ct).ConfigureAwait(false);

                // The ledger settles the whole chunk in one write, and only for the records the target actually
                // answered for: a record whose call failed keeps the state it had, so a retry of the removal is
                // still the removal of a record that is still there.
                var settled = outcomes.Where(o => o.Succeeded).Select(o => o.Removal.Key).ToList();
                await ledger.MarkRemovedAsync(Flow.Id, settled, scope, Actor, _context.Time.GetUtcNow().UtcDateTime, correlation.Id, ct).ConfigureAwait(false);
                foreach (var outcome in outcomes)
                {
                    results.Add(await AnnounceRemovalAsync(outcome, scope, records[outcome.Removal.Key], ct).ConfigureAwait(false));
                }
            }

            var summary = RemovalSummary.Of(scope, keys.Count, results);
            return (summary, summary.Describe(), results.Count == 1 ? results[0].SubmissionId : null);
        }, ct);
    }

    /// <summary>
    /// Sends what a stopped run left leased in a submission. The platform runs one execution of a flow at a time, so a
    /// record still leased when a run of that flow starts belongs to a worker that is gone: a control plane or node
    /// stopped mid-delivery, whose run was recovered and requeued. Each such lease is waited out, reclaimed (the ledger
    /// notes why) and the record sent, its completed steps resumed. A lease running out further ahead than the flow's
    /// own lease is not a stopped run's, so it is left alone with a warning, and records in backoff are not waited for.
    /// </summary>
    private async Task<WorkerSummary> SendOrphanedLeasesAsync(DeliveryWorker worker, Guid submissionId, CancellationToken ct)
    {
        var ledger = RequireLedger();
        var longest = TimeSpan.FromSeconds(Flow.Reliability.LeaseSeconds) + LeaseExpiryMargin;
        var total = WorkerSummary.Empty;
        while (await ledger.NextLeaseExpiryAsync(Flow.Id, submissionId, ct).ConfigureAwait(false) is { } expiry)
        {
            ct.ThrowIfCancellationRequested();
            var now = _context.Time.GetUtcNow().UtcDateTime;
            if (expiry >= now)
            {
                var wait = expiry - now + LeaseExpiryMargin;
                if (wait > longest)
                {
                    _log.LogWarning(
                        "A record of submission {SubmissionId} is leased until {Expiry:o}, further ahead than this flow's lease of {Seconds}s, so a running worker holds it and it is left alone.",
                        submissionId, expiry, Flow.Reliability.LeaseSeconds);
                    break;
                }

                _log.LogInformation(
                    "A record of submission {SubmissionId} is still leased by a run that stopped; waiting {Seconds}s for the lease to run out.",
                    submissionId, (int)Math.Ceiling(wait.TotalSeconds));
                await Task.Delay(wait, _context.Time, ct).ConfigureAwait(false);
            }

            var reclaimed = await ledger.RecoverExpiredLeasesAsync(Flow.Id, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            var sent = await PassUntilNothingClaimableAsync(worker, submissionId, ct).ConfigureAwait(false);
            total = total.Add(sent);
            if (reclaimed == 0 && sent.Processed == 0)
            {
                // The wait ended without the lease running out, or the lease was reclaimed elsewhere and nothing of it is
                // left to send: either way there is nothing this run can take over.
                _log.LogWarning(
                    "The lease on a record of submission {SubmissionId} (due to run out at {Expiry:o}) could not be reclaimed, so the record is left for a later run.",
                    submissionId, expiry);
                break;
            }

            if (reclaimed > 0)
            {
                _log.LogInformation("Recovered the leases a stopped run left behind ({Count} record(s) settled or handed back), and sent {Sent}.", reclaimed, sent.Processed);
            }
        }

        return total;
    }

    /// <summary>Claim passes over one submission until nothing of it is claimable, without waiting for records in backoff.</summary>
    private static async Task<WorkerSummary> PassUntilNothingClaimableAsync(DeliveryWorker worker, Guid submissionId, CancellationToken ct)
    {
        var total = WorkerSummary.Empty;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var pass = await worker.PassAsync(submissionId, ct).ConfigureAwait(false);
            total = total.Add(pass);
            if (pass.Processed == 0 && pass.Batches == 0 && pass.Waiting == 0)
            {
                return total;
            }
        }
    }

    /// <summary>
    /// Sends what settled submissions of the flow still hold. A record released back to pending with its rendered document
    /// after its submission completed or failed belongs to no run: its own run is over, and a newer plan skips it because
    /// its row is what the record already queues. The run takes the due records of up to
    /// <see cref="SettledSubmissionsPerRun"/> such submissions, passes over each until nothing of it is claimable (records
    /// in backoff are not waited for) and recomputes the totals of each one it sent anything from.
    /// </summary>
    private async Task<WorkerSummary> SendSettledLeftoversAsync(DeliveryWorker worker, Guid current, CancellationToken ct)
    {
        var ledger = RequireLedger();
        var settled = await ledger.ListSettledSubmissionsWithDueWorkAsync(Flow.Id, current, _context.Time.GetUtcNow().UtcDateTime, SettledSubmissionsPerRun, ct).ConfigureAwait(false);
        var total = WorkerSummary.Empty;
        foreach (var submissionId in settled)
        {
            var sent = await PassUntilNothingClaimableAsync(worker, submissionId, ct).ConfigureAwait(false);
            if (sent.Processed > 0 || sent.Waiting > 0)
            {
                await Intake.CompleteAsync(submissionId, Flow.Id, ct).ConfigureAwait(false);
                _log.LogInformation(
                    "Sent {Count} record(s) that submission {SubmissionId} still held after it settled (released back to pending with their rendered documents).",
                    sent.Processed, submissionId);
            }

            total = total.Add(sent);
        }

        return total;
    }

    /// <summary>
    /// Asks the legal service about the mapping's legal tags before a run plans or sends anything. Every record the
    /// mapping renders carries the same tags, and storage refuses a record whose tag is unknown or expired, so a run
    /// that starts with a bad tag would fail each record it plans with the same error; refusing the run names the tag
    /// and the service's reason once, before anything reaches the ledger or OSDU. A target that does not ask (see
    /// <see cref="IDeliveryProtocol.InvalidLegalTagsAsync"/>) is logged as not checked, never taken as valid.
    /// </summary>
    public Task CheckLegalTagsAsync(CancellationToken ct = default) => EnsureLegalTagsAsync(ct);

    /// <summary>The legal tag check, asked once per runtime; see <see cref="CheckLegalTagsAsync"/>.</summary>
    private async Task EnsureLegalTagsAsync(CancellationToken ct)
    {
        if (_legalTagsChecked)
        {
            return;
        }

        if (Mapping.Mapping.Envelope.LegalTags.Count == 0)
        {
            // A mapping of DSPDM business object rows puts no legal tags on anything: its rows are no OSDU records.
            _legalTagsChecked = true;
            return;
        }

        var tags = Mapping.Mapping.Envelope.LegalTags.Select(tag => MappingEntry.ExpandParameters(tag, Mapping.Renderer.ParameterValue)).ToList();
        var protocol = await ProtocolAsync(ct).ConfigureAwait(false);
        var invalid = await protocol.InvalidLegalTagsAsync(tags, ct).ConfigureAwait(false);
        if (invalid is null)
        {
            _log.LogInformation(
                "The mapping's legal tags ({Tags}) were not checked with the legal service before the run: the target does not ask it (validateLegalTags is off, or a well log endpoint names neither ddmsRoot nor legalValidatePath).",
                string.Join(", ", tags));
            _legalTagsChecked = true;
            return;
        }

        if (invalid.Count > 0)
        {
            throw new DeliveryException(
                string.Create(CultureInfo.InvariantCulture, $"The legal service refuses {invalid.Count} of the legal tag(s) mapping {Mapping.Mapping.Reference} puts on every record, so nothing was planned or sent: ")
                + string.Join("; ", invalid.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + ": " + kv.Value)));
        }

        _legalTagsChecked = true;
    }

    /// <summary>
    /// The intake, spread across the fleet when the flow declares a fan-out, this run can enqueue members and the read
    /// is big enough: the candidate keys are cut into contiguous slices, every member plans its share of them, this run
    /// plans its own, waits for the members, and finalises the submission with the totals.
    /// </summary>
    private async Task<(IntakeResult Result, int Members)> IntakeWithFanOutAsync(bool force, Action<FanOutHandle?> track, CancellationToken ct)
    {
        var intake = Intake;
        var prepared = await intake.PrepareAsync(Flow, Mapping, Parameters, Request, force, ct).ConfigureAwait(false);
        if (prepared.Done is { } done)
        {
            SubmissionId = done.Submission.SubmissionId;
            return (done, 0);
        }

        var submission = prepared.Submission;
        SubmissionId = submission.SubmissionId;
        var header = prepared.Header;
        var dispatcher = _context.Dispatcher;
        var fanOut = Flow.Reliability.FanOut;
        var slices = KeySlices.Count(header.Source.EstimatedCandidates, Flow.Reliability.BatchRecords);
        var applies = fanOut > 0 && dispatcher.Available && RunId is not null && slices >= 2
            && header.Source.EstimatedCandidates >= Flow.Reliability.FanOutMinRecords
            && slices - 1 <= SubmissionIntake.MaxFanOutPartition;
        // The candidates are cut on the record table's identity primary key; when they sit so close together that fewer
        // than two ranges come out, the read is one run's.
        var ranges = applies ? await Planner.SliceBoundsAsync(header, slices, ct).ConfigureAwait(false) : [];
        if (ranges.Count < 2)
        {
            var own = await intake.PlanSlicesAsync(Flow, prepared, null, ct).ConfigureAwait(false);
            var finalized = await intake.FinalizePlanningAsync(Flow, submission.SubmissionId, Parameters, own, ct).ConfigureAwait(false);
            return (new IntakeResult(finalized, header, own, AlreadyProcessed: false), 0);
        }

        prepared = await intake.RecordSlicesAsync(prepared, ranges, ct).ConfigureAwait(false);
        var shares = KeySlices.Shares(ranges.Count, fanOut + 1);
        var members = shares.Skip(1).Where(s => s.Count > 0).Select(share => new RunParameters
        {
            Operation = DeliveryOperations.Intake,
            Values = Parameters,
            Payload = new DeliveryRunPayload { SubmissionId = submission.SubmissionId, Slices = share, Force = force, Interface = Flow.Interface }.ToJson(),
        }).ToList();

        var totals = await intake.PlanSlicesAsync(Flow, prepared, shares[0], ct).ConfigureAwait(false);
        var handle = await dispatcher.EnqueueAsync(members, ct).ConfigureAwait(false);
        track(handle);
        _log.LogInformation(
            "Fanned the intake of {Records} candidate record(s) in {Slices} slice(s) out to {Members} member run(s) (group {GroupId}); this run planned slices {Own}.",
            header.Source.EstimatedCandidates, ranges.Count, members.Count, handle.GroupId, KeySlices.Describe(shares[0]));
        var state = await WaitForMembersAsync(handle, "intake", ct).ConfigureAwait(false);
        track(null);

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

        var settledSubmission = await intake.FinalizePlanningAsync(Flow, submission.SubmissionId, Parameters, totals, ct).ConfigureAwait(false);
        return (new IntakeResult(settledSubmission, header, totals, AlreadyProcessed: false), members.Count);
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
                .Select(_ => new RunParameters
                {
                    Operation = DeliveryOperations.Drain,
                    Payload = new DeliveryRunPayload { SubmissionId = submission.SubmissionId, Interface = Flow.Interface }.ToJson(),
                })
                .ToList();
            handle = await dispatcher.EnqueueAsync(parameters, ct).ConfigureAwait(false);
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
            var reclaimed = await ledger.RecoverExpiredLeasesAsync(Flow.Id, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            if (reclaimed > 0)
            {
                _log.LogInformation("Recovered the leases that ran out: {Count} record(s) settled or handed back.", reclaimed);
            }

            var more = await worker.DrainAsync(submission.SubmissionId, ct).ConfigureAwait(false);
            total = total.Add(more);
            if (more.Processed == 0 && more.Batches == 0 && more.Waiting == 0)
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
            FlowName = Flow.Label,
            Interface = Flow.Interface,
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
            FlowName = Flow.Label,
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
            await SettleAsync(ledger, activity.ActivityId, "cancelled", "cancelled").ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await SettleAsync(ledger, activity.ActivityId, "failed", HeaderRedaction.RedactMessage(ex.Message)).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Ends an activity the operation did not finish. Every start is settled, whatever was thrown and from wherever in
    /// the engine, so an action never keeps reading as running once its run is over; a ledger that cannot be written
    /// here is logged and never replaces the failure the caller is about to see.
    /// </summary>
    private async Task SettleAsync(ILedger ledger, long activityId, string outcome, string summary)
    {
        try
        {
            await CompleteActivityAsync(ledger, activityId, outcome, summary, null).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException or InvalidOperationException or System.Data.Common.DbException)
        {
            _log.LogWarning("Could not settle activity {ActivityId} as {Outcome} in the ledger: {Message}", activityId, outcome, ex.Message);
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
            await _context.Listener.OnEventAsync(new DeliveryEvent { AtUtc = _context.Time.GetUtcNow().UtcDateTime, FlowId = Flow.Id, FlowName = Flow.Label, Interface = Flow.Interface, Kind = kind, Worker = Actor, Detail = detail + " (all blocked records)" }, ct).ConfigureAwait(false);
            return;
        }

        foreach (var key in keys)
        {
            await _context.Listener.OnEventAsync(new DeliveryEvent { AtUtc = _context.Time.GetUtcNow().UtcDateTime, FlowId = Flow.Id, FlowName = Flow.Label, Interface = Flow.Interface, Kind = kind, DeliveryKey = key, Worker = Actor, Detail = detail }, ct).ConfigureAwait(false);
        }
    }

    private ILedger RequireLedger()
        => _context.Ledger ?? throw new DeliveryException(DeliveryServices.NoLedgerMessage);

    public void Dispose()
    {
        (_search as IDisposable)?.Dispose();
        _target.Dispose();
    }
}
