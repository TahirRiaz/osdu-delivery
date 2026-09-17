using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine;

/// <summary>The states an interface ends a run of its source in.</summary>
public static class InterfaceStates
{
    /// <summary>The interface did everything the run asked of it.</summary>
    public const string Completed = "completed";

    /// <summary>Something failed for the interface as a whole, or its records' failures crossed its failWhen rules.</summary>
    public const string Stopped = "stopped";

    /// <summary>The interface waits for an interface that did not complete, so it was not run.</summary>
    public const string Skipped = "skipped";
}

/// <summary>What one interface of a source did in a run: how it is delivered, what it waited for, how it ended and why.</summary>
/// <param name="Interface">The interface's name.</param>
/// <param name="FlowId">Its ledger identity.</param>
/// <param name="Ledger">The name its ledger identity is derived from.</param>
/// <param name="Route">The route it is delivered by (storage, file, dataset, manifest, ddms, fileAndDdms, manifestAndDdms, workflow).</param>
/// <param name="RouteReason">Why it goes by that route.</param>
/// <param name="WaitsFor">The interfaces of this run it waited for.</param>
/// <param name="WaitReasons">Why it waited for each of them: the <c>after:</c> that names it, or the references its mapping fills.</param>
/// <param name="Wave">The wave it ran in, from 1.</param>
/// <param name="State">One of <see cref="InterfaceStates"/>.</param>
/// <param name="Reason">Why it stopped or was skipped; null when it completed.</param>
/// <param name="StartedUtc">When it started; null when it was skipped.</param>
/// <param name="CompletedUtc">When it ended; null when it was skipped.</param>
/// <param name="Result">What the operation returned for it (the outcome a run of the interface alone returns), when it completed.</param>
public sealed record InterfaceOutcome(
    string Interface,
    Guid FlowId,
    string Ledger,
    string Route,
    string? RouteReason,
    IReadOnlyList<string> WaitsFor,
    IReadOnlyList<string> WaitReasons,
    int Wave,
    string State,
    string? Reason,
    DateTime? StartedUtc,
    DateTime? CompletedUtc,
    object? Result);

/// <summary>
/// The <c>result</c> of a run of a source (docs/interfaces-design.md section 4): every interface it ran with its state and
/// its own outcome, and the totals the run row projects.
/// </summary>
public sealed record SourceRunOutcome(string Operation, string Source, IReadOnlyList<InterfaceOutcome> Interfaces)
{
    public int Completed => Interfaces.Count(i => i.State == InterfaceStates.Completed);

    public int Stopped => Interfaces.Count(i => i.State == InterfaceStates.Stopped);

    public int Skipped => Interfaces.Count(i => i.State == InterfaceStates.Skipped);

    /// <summary>Records planned across the interfaces.</summary>
    public long Planned => Sum(r => r switch
    {
        DeliverOutcome d => d.Planned,
        IntakeOutcome i => i.Planned,
        PlanOutcome p => p.Deliveries,
        _ => 0,
    });

    /// <summary>Records delivered across the interfaces.</summary>
    public long Delivered => Sum(r => r switch
    {
        DeliverOutcome d => d.Delivered,
        DrainOutcome d => d.Delivered,
        _ => 0,
    });

    /// <summary>Records held across the interfaces.</summary>
    public long Held => Sum(r => r switch
    {
        DeliverOutcome d => d.Held,
        IntakeOutcome i => i.Held,
        DrainOutcome d => d.Held,
        PlanOutcome p => p.Holds,
        _ => 0,
    });

    /// <summary>Records failed across the interfaces.</summary>
    public long Failed => Sum(r => r switch
    {
        DeliverOutcome d => d.Failed,
        DrainOutcome d => d.Failed,
        _ => 0,
    });

    /// <summary>Records left waiting, across the interfaces, for a record they refer to that has not landed.</summary>
    public long Waiting => Sum(r => r switch
    {
        DeliverOutcome d => d.Waiting,
        DrainOutcome d => d.Waiting,
        _ => 0,
    });

    /// <summary>The run's headline count on the run row (<c>result.rowsLoaded</c>): the records the run delivered.</summary>
    public long RowsLoaded => Delivered;

    /// <summary>True when every interface completed.</summary>
    public bool Succeeded => Interfaces.All(i => i.State == InterfaceStates.Completed);

    /// <summary>One paragraph: how many interfaces completed, and which stopped or were skipped and why.</summary>
    public string Describe()
    {
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"{Operation} of '{Source}': {Completed} of {Interfaces.Count} interface(s) completed ({Delivered} record(s) delivered, {Held} held, {Failed} failed, {Waiting} waiting)");
        var stopped = Interfaces.Where(i => i.State == InterfaceStates.Stopped).Select(i => $"{i.Interface} ({i.Reason})").ToList();
        var skipped = Interfaces.Where(i => i.State == InterfaceStates.Skipped).Select(i => $"{i.Interface} ({i.Reason})").ToList();
        if (stopped.Count > 0)
        {
            text += "; stopped: " + string.Join("; ", stopped);
        }

        if (skipped.Count > 0)
        {
            text += "; skipped: " + string.Join("; ", skipped);
        }

        return Succeeded
            ? text + "."
            : text + ". What completed is in the ledger, and the next run does what is left.";
    }

    private long Sum(Func<object, long> count)
        => Interfaces.Where(i => i.Result is not null).Sum(i => count(i.Result!));
}

/// <summary>A run of a source whose interfaces did not all complete: the run fails, and its outcome says what each did.</summary>
public sealed class SourceRunIncompleteException : DeliveryException
{
    public SourceRunIncompleteException(SourceRunOutcome outcome)
        : base((outcome ?? throw new ArgumentNullException(nameof(outcome))).Describe())
    {
        Outcome = outcome;
    }

    public SourceRunOutcome Outcome { get; }
}

/// <summary>An interface's failure guard tripped: its records' failures add up to a failure of the interface itself.</summary>
public sealed class InterfaceStoppedException : DeliveryException
{
    public InterfaceStoppedException(FlowDefinition flow, string reason)
        : base($"'{(flow ?? throw new ArgumentNullException(nameof(flow))).Label}' stopped: {reason}. What it delivered is in the ledger, and the next run carries on from there.")
    {
        Reason = reason;
    }

    /// <summary>Why the guard tripped.</summary>
    public string Reason { get; }
}

/// <summary>
/// A run of a source (docs/interfaces-design.md sections 6 and 8): the interfaces the run selects, checked together
/// before anything is planned or sent, then run in waves of their dependencies, the interfaces of a wave side by side,
/// each one planning, fanning out and draining exactly as a run of that interface alone does. An interface that stops
/// stops only itself and the interfaces waiting for it; the others carry on, and the run ends failed with every
/// interface's state in its outcome. A record's own failure never stops anything but the record, unless the interface's
/// failure guard judges the records together.
/// </summary>
public sealed class SourceRuntime
{
    private readonly EngineContext _context;
    private readonly SourceDefinition _source;
    private readonly ILogger _log;

    public SourceRuntime(EngineContext context, SourceDefinition source)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        _context = context;
        _source = source;
        _log = context.Loggers.CreateLogger("run");
    }

    /// <summary>Who asked for the run; recorded on every activity of every interface.</summary>
    public string Actor { get; init; } = "unknown";

    /// <summary>The platform run this is.</summary>
    public Guid? RunId { get; init; }

    /// <summary>Supplies the run's captured log for the activities being completed.</summary>
    public Func<string?>? ActivityLog { get; init; }

    /// <summary>The run's parameter values, shared by every interface.</summary>
    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Runs <paramref name="operation"/> over the interfaces <paramref name="payload"/> selects (every interface when it
    /// names none). Throws <see cref="DeliveryException"/> with every preflight finding when the run cannot start, and
    /// <see cref="SourceRunIncompleteException"/> when an interface did not complete.
    /// </summary>
    public async Task<SourceRunOutcome> ExecuteAsync(string operation, DeliveryRunPayload payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(payload);
        var selected = _source.Select(payload.Interfaces);
        var (runtimes, order) = await PreflightAsync(selected, operation, ct).ConfigureAwait(false);
        _log.LogInformation(
            "{Operation} of {Count} interface(s) of '{Source}' in {Waves} wave(s): {Order}.",
            operation, selected.Count, _source.Name, order.Waves.Count, order.Describe());
        foreach (var dependency in order.Dependencies)
        {
            _log.LogInformation("'{Interface}' waits for '{DependsOn}': {Why}.", dependency.Interface, dependency.DependsOn, dependency.Why);
        }

        foreach (var reference in order.NotWaitedFor)
        {
            _log.LogInformation("'{Interface}' does not wait for '{DependsOn}': {Why}.", reference.Interface, reference.DependsOn, reference.Why);
        }

        try
        {
            var outcomes = new ConcurrentDictionary<string, InterfaceOutcome>(StringComparer.OrdinalIgnoreCase);
            foreach (var wave in order.Waves)
            {
                var runnable = new List<FlowDefinition>();
                foreach (var name in wave)
                {
                    var flow = selected.First(f => string.Equals(f.Interface, name, StringComparison.OrdinalIgnoreCase));
                    var blocking = order.WaitsFor(name).Select(d => d.DependsOn).Where(d => outcomes[d].State != InterfaceStates.Completed).ToList();
                    if (blocking.Count == 0)
                    {
                        runnable.Add(flow);
                        continue;
                    }

                    var reason = $"it waits for {string.Join(", ", blocking)}, which did not complete";
                    outcomes[name] = Outcome(flow, order, InterfaceStates.Skipped, reason, null, null, null);
                    _log.LogWarning("Interface '{Interface}' is skipped: {Reason}.", name, reason);
                    await EmitAsync(flow, "interface.skipped", reason).ConfigureAwait(false);
                }

                await Parallel.ForEachAsync(
                    runnable,
                    new ParallelOptions { MaxDegreeOfParallelism = _source.ParallelInterfaces, CancellationToken = ct },
                    async (flow, token) =>
                    {
                        var name = flow.Interface ?? string.Empty;
                        outcomes[name] = await RunInterfaceAsync(flow, runtimes[name], operation, payload, order, token).ConfigureAwait(false);
                    }).ConfigureAwait(false);
            }

            var outcome = new SourceRunOutcome(operation, _source.Name, selected.Select(f => outcomes[f.Interface ?? string.Empty]).ToList());
            _log.LogInformation("{Summary}", outcome.Describe());
            return outcome.Succeeded ? outcome : throw new SourceRunIncompleteException(outcome);
        }
        finally
        {
            foreach (var runtime in runtimes.Values)
            {
                runtime.Dispose();
            }
        }
    }

    /// <summary>
    /// Opens a runtime for every selected interface and checks, before anything is planned or sent, everything that would
    /// otherwise fail an interface halfway (docs/interfaces-design.md section 8.1): the ledger, each mapping, template and
    /// cache, each route against the kind its mapping renders, the order the interfaces run in, each record table's shape,
    /// each route's service and the credentials, and each mapping's legal tags. Every finding is reported at once.
    /// </summary>
    private async Task<(Dictionary<string, FlowRuntime> Runtimes, InterfaceOrderPlan Order)> PreflightAsync(
        IReadOnlyList<FlowDefinition> selected, string operation, CancellationToken ct)
    {
        var findings = new List<string>();
        var runtimes = new Dictionary<string, FlowRuntime>(StringComparer.OrdinalIgnoreCase);
        var readsSource = DeliveryExecutor.ReadsSource(operation);
        InterfaceOrderPlan order;
        try
        {
            await CheckLedgerAsync(selected[0], findings, ct).ConfigureAwait(false);
            foreach (var flow in selected)
            {
                var name = flow.Interface ?? string.Empty;
                var context = _context.ForInterface(name);
                try
                {
                    var runtime = readsSource
                        ? await FlowRuntime.CreateAsync(context, flow, Values, ct).ConfigureAwait(false)
                        : FlowRuntime.ForTarget(context, flow);
                    runtime.Actor = Actor;
                    runtime.RunId = RunId;
                    runtime.ActivityLog = ActivityLog;
                    runtime.SourceDocument = _source;
                    runtimes[name] = runtime;
                    await runtime.CheckRouteAsync(readsSource ? runtime.Mapping.Mapping.Kind : runtime.Mappings.Load(flow.Render.Mapping).Kind, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (Found(ex, ct))
                {
                    findings.Add($"interface '{name}': {RunFailure.Describe(ex)}");
                }
            }

            order = await OrderAsync(selected, runtimes, findings, ct).ConfigureAwait(false);
            foreach (var (name, runtime) in runtimes)
            {
                if (readsSource)
                {
                    await FindAsync(findings, name, () => runtime.Source.VerifyAsync(ct), ct).ConfigureAwait(false);
                }
            }

            if (operation != DeliveryOperations.Plan)
            {
                await CheckTargetsAsync(runtimes, findings, ct).ConfigureAwait(false);
            }

            if (operation is DeliveryOperations.Deliver or DeliveryOperations.Replan or DeliveryOperations.Intake)
            {
                foreach (var (name, runtime) in runtimes)
                {
                    await FindAsync(findings, name, () => runtime.CheckLegalTagsAsync(ct), ct).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            Dispose(runtimes);
            throw;
        }

        if (findings.Count > 0)
        {
            Dispose(runtimes);
            throw new DeliveryException(
                string.Create(CultureInfo.InvariantCulture, $"The preflight of '{_source.Name}' found {findings.Count} problem(s), so nothing was planned or sent: ")
                + string.Join(" | ", findings));
        }

        _log.LogInformation("Preflight passed for {Interfaces}.", string.Join(", ", runtimes.Keys));
        return (runtimes, order);
    }

    /// <summary>
    /// The order the selected interfaces run in: what the document declares with <c>after:</c>, and what the relationships
    /// their mappings fill imply. Interfaces that wait for each other in a way no rule cuts are a finding. When an
    /// interface could not be opened, or only one runs, <c>after:</c> alone orders the run: a failed preflight stops it
    /// anyway, and one interface waits for nothing.
    /// </summary>
    private async Task<InterfaceOrderPlan> OrderAsync(
        IReadOnlyList<FlowDefinition> selected, Dictionary<string, FlowRuntime> runtimes, List<string> findings, CancellationToken ct)
    {
        var names = selected.Select(f => f.Interface ?? string.Empty).ToList();
        var declared = InterfaceOrder.Declared(_source);
        if (selected.Count > 1 && runtimes.Count == selected.Count)
        {
            var schemas = new List<InterfaceSchema>(selected.Count);
            foreach (var name in names)
            {
                await FindAsync(findings, name, async () => schemas.Add(await runtimes[name].SchemaAsync(ct).ConfigureAwait(false)), ct).ConfigureAwait(false);
            }

            if (schemas.Count == selected.Count)
            {
                try
                {
                    return InterfaceOrder.Plan(names, declared, schemas);
                }
                catch (DeliveryException ex)
                {
                    findings.Add($"the order of the interfaces: {ex.Message}");
                }
            }
        }

        return InterfaceOrder.Plan(names, declared, []);
    }

    /// <summary>The ledger answers a read the way every run reads it, under snapshot isolation.</summary>
    private async Task CheckLedgerAsync(FlowDefinition flow, List<string> findings, CancellationToken ct)
    {
        if (_context.Ledger is not { } ledger)
        {
            findings.Add(DeliveryServices.NoLedgerMessage);
            return;
        }

        try
        {
            await ledger.GetWatermarkAsync(flow.Id, string.Empty, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (Found(ex, ct))
        {
            findings.Add($"the ledger: {RunFailure.Describe(ex)}");
        }
    }

    /// <summary>
    /// Each route's service, once however many interfaces share it: a service that cannot be reached, answers 5xx or
    /// refuses the credentials is a finding. One that answers its probe path with another client error still answered,
    /// and is noted rather than refused.
    /// </summary>
    private async Task CheckTargetsAsync(Dictionary<string, FlowRuntime> runtimes, List<string> findings, CancellationToken ct)
    {
        var probed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, runtime) in runtimes)
        {
            var flow = runtime.Flow;
            var options = flow.Target.ProtocolOptions;
            if (!probed.Add($"{flow.Target.Protocol}|{options.ProbePath}|{options.DdmsRoot}"))
            {
                continue;
            }

            await FindAsync(findings, name, async () =>
            {
                var protocol = await runtime.ProtocolAsync(ct).ConfigureAwait(false);
                var probe = await protocol.ProbeAsync(ct).ConfigureAwait(false);
                var route = RouteChecks.Name(flow.Target.Protocol);
                if (probe.Reachable)
                {
                    return;
                }

                if (probe.Status is 0 or 401 or 403 or >= 500)
                {
                    var why = probe.Status switch
                    {
                        0 => "could not be reached",
                        401 or 403 => string.Create(CultureInfo.InvariantCulture, $"refused the credentials (HTTP {probe.Status})"),
                        _ => string.Create(CultureInfo.InvariantCulture, $"is failing (HTTP {probe.Status})"),
                    };
                    throw new DeliveryException($"the {route} route's service {why} at {probe.Path}: {probe.Detail}");
                }

                _log.LogInformation(
                    "The {Route} route's service answered its probe path {Path} with HTTP {Status}; it is reachable, so the run goes on.",
                    route, probe.Path, probe.Status);
            }, ct).ConfigureAwait(false);
        }
    }

    private static async Task FindAsync(List<string> findings, string name, Func<Task> check, CancellationToken ct)
    {
        try
        {
            await check().ConfigureAwait(false);
        }
        catch (Exception ex) when (Found(ex, ct))
        {
            findings.Add($"interface '{name}': {RunFailure.Describe(ex)}");
        }
    }

    /// <summary>A failure a check reports as a finding: anything but the run being cancelled.</summary>
    private static bool Found(Exception ex, CancellationToken ct)
        => (ex is not OperationCanceledException || !ct.IsCancellationRequested)
           && (RunFailure.IsExpected(ex) || ex is DbException or TimeoutException or OperationCanceledException);

    /// <summary>One interface's run: its operation under its failure guard, its trace events, and how it ended.</summary>
    private async Task<InterfaceOutcome> RunInterfaceAsync(
        FlowDefinition flow, FlowRuntime runtime, string operation, DeliveryRunPayload payload, InterfaceOrderPlan order, CancellationToken ct)
    {
        var log = runtime.Context.Loggers.CreateLogger("run");
        var route = RouteChecks.Name(flow.Target.Protocol);
        var started = _context.Time.GetUtcNow().UtcDateTime;
        log.LogInformation("Interface started: {Operation} by the {Route} route ({Reason}).", operation, route, flow.RouteReason);
        await EmitAsync(flow, "interface.started", $"{operation} by the {route} route").ConfigureAwait(false);

        // What the run asked of the source applies to the interface as a run of it alone takes it.
        var own = new DeliveryRunPayload { Force = payload.Force, Interface = flow.Interface };
        try
        {
            var result = await DeliveryExecutor.GuardedAsync(
                runtime, operation, token => DeliveryExecutor.ExecuteInterfaceAsync(runtime, operation, own, null, log, token), ct).ConfigureAwait(false);
            var completed = _context.Time.GetUtcNow().UtcDateTime;
            var summary = Summarize(result);
            log.LogInformation("Interface completed in {Seconds:0.#}s: {Summary}.", (completed - started).TotalSeconds, summary);
            await EmitAsync(flow, "interface.completed", summary).ConfigureAwait(false);
            return Outcome(flow, order, InterfaceStates.Completed, null, started, completed, result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var reason = ex is InterfaceStoppedException stopped ? stopped.Reason : RunFailure.Describe(ex);
            log.LogError(RunFailure.IsExpected(ex) ? null : ex, "Interface stopped: {Reason}", reason);
            await EmitAsync(flow, "interface.stopped", reason).ConfigureAwait(false);
            return Outcome(flow, order, InterfaceStates.Stopped, reason, started, _context.Time.GetUtcNow().UtcDateTime, null);
        }
    }

    private static InterfaceOutcome Outcome(
        FlowDefinition flow, InterfaceOrderPlan order, string state, string? reason, DateTime? started, DateTime? completed, object? result)
    {
        var name = flow.Interface ?? string.Empty;
        var waits = order.WaitsFor(name);
        return new InterfaceOutcome(
            name, flow.Id, flow.LedgerName, RouteChecks.Name(flow.Target.Protocol), flow.RouteReason,
            waits.Select(w => w.DependsOn).ToList(), waits.Select(w => $"{w.DependsOn}: {w.Why}").ToList(), order.WaveOf(name),
            state, reason, started, completed, result);
    }

    private static string Summarize(object result) => result switch
    {
        DeliverOutcome d => string.Create(
            CultureInfo.InvariantCulture,
            $"{d.Planned} planned, {d.Delivered} delivered, {d.SkippedUnchanged + d.UnchangedAtPush} unchanged, {d.Held} held, {d.Failed} failed, {d.Waiting} waiting (submission {d.SubmissionId:D})"),
        PlanOutcome p => string.Create(CultureInfo.InvariantCulture, $"{p.Records} record(s) read, {p.Deliveries} to deliver, {p.Skips} unchanged, {p.Holds} held"),
        IntakeOutcome i => string.Create(CultureInfo.InvariantCulture, $"{i.Planned} planned in {i.Batches} batch(es), {i.Held} held (submission {i.SubmissionId:D})"),
        DrainOutcome d => string.Create(CultureInfo.InvariantCulture, $"{d.Processed} processed, {d.Delivered} delivered, {d.Held} held, {d.Failed} failed, {d.Waiting} waiting"),
        VerifyRunOutcome v => string.Create(CultureInfo.InvariantCulture, $"{v.Checked} checked, {v.Matched} matched, {v.Drifted} drifted, {v.Missing} missing"),
        _ => result.ToString() ?? string.Empty,
    };

    private async Task EmitAsync(FlowDefinition flow, string kind, string detail)
        => await _context.Listener.OnEventAsync(new DeliveryEvent
        {
            AtUtc = _context.Time.GetUtcNow().UtcDateTime,
            FlowId = flow.Id,
            FlowName = flow.Label,
            Interface = flow.Interface,
            Kind = kind,
            Worker = Actor,
            Detail = detail,
        }, CancellationToken.None).ConfigureAwait(false);

    private static void Dispose(Dictionary<string, FlowRuntime> runtimes)
    {
        foreach (var runtime in runtimes.Values)
        {
            runtime.Dispose();
        }

        runtimes.Clear();
    }
}
