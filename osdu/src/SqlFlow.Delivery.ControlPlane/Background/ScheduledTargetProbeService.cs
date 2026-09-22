using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.ControlPlane.Api;
using SqlFlow.Delivery.ControlPlane.Configuration;
using SqlFlow.Delivery.Diagnostics;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.ControlPlane.Background;

/// <summary>The outcomes a settled probe is recorded and counted under; the vocabulary the metric's <c>outcome</c> tag uses.</summary>
public static class ProbeOutcomes
{
    /// <summary>The service answered under the flow's credentials.</summary>
    public const string Reachable = "reachable";

    /// <summary>The service refused the call or did not answer at all. The target is there to be alerted on.</summary>
    public const string Unreachable = "unreachable";

    /// <summary>The probe itself could not run: no node took it, the flow file was not on the node, a credential would not resolve.</summary>
    public const string Error = "error";

    /// <summary>The probe task was cancelled before it recorded anything.</summary>
    public const string Cancelled = "cancelled";
}

/// <summary>
/// Probes the target of every active delivery flow on a schedule, so a deployment learns that an OSDU stopped answering
/// without an operator pressing "Probe target" (go-live map OPS-2). Each pass queues the same node operation the
/// operator's button queues (<see cref="DeliveryEndpoints.QueueProbeAsync"/>, which runs <c>delivery-probe</c> under the
/// flow's own credentials), once per interface of each flow, since each interface has a target of its own. What comes
/// back is recorded in the ledger's audit trail as a <c>probe</c> activity by <c>service:schedule</c>, and counted on
/// <c>osdu_delivery.probes</c>, so the last result per flow and interface is readable without asking for anything and an
/// alert can be built on either.
/// </summary>
/// <remarks>
/// <para>Off unless <c>Osdu:TargetProbe:Enabled</c> says otherwise: a pass costs a token exchange and a request against a
/// live OSDU for every interface it covers. <c>:IntervalMinutes</c> paces it (never under
/// <see cref="TargetProbeOptions.MinimumIntervalMinutes"/>), <c>:Pipelines</c> narrows it to named flows and
/// <c>:MaxPerPass</c> bounds one pass of a large estate.</para>
/// <para>A probe runs on a node, so its result arrives after the task is queued. A pass therefore settles first (every
/// probe an earlier pass, or an earlier life of this host, left open, found again from the ledger rather than from
/// memory), then queues this pass's probes, then waits up to <c>:SettleSeconds</c> for them; whatever has not come back
/// by then is settled by the next pass. Nothing is lost across a restart, and no probe stays open forever: a task the
/// queue no longer holds settles the activity as an error.</para>
/// <para>Hosted on every replica. The control plane runs one today (go-live map OPS-3); were it ever more, each replica
/// would keep its own schedule, which costs one extra probe per interval per replica and records each outcome correctly,
/// since a pass only settles the probes whose activities it can still find open.</para>
/// </remarks>
public sealed partial class ScheduledTargetProbeService : BackgroundService
{
    /// <summary>The activity kind every probe is recorded under, scheduled or not.</summary>
    public const string ActivityKind = "probe";

    /// <summary>The actor the scheduled probe records its activities and its tasks under.</summary>
    public const string ScheduleActor = "service:schedule";

    /// <summary>The activity outcome of a probe that has been queued and has not come back yet.</summary>
    private const string Running = "running";

    /// <summary>Delivery pipelines one pass reads. Past this an estate is narrowed with <c>:Pipelines</c>.</summary>
    private const int MaxPipelinesPerPass = 1000;

    /// <summary>Open probes one pass carries over, which is at most one per interface probed.</summary>
    private const int MaxOpenProbes = 1000;

    /// <summary>Task rows read in one query, so the id list stays well inside the provider's parameter limit.</summary>
    private const int TaskLookupChunk = 200;

    /// <summary>How often a pass looks again at the probes it just queued, while it waits for them.</summary>
    private static readonly TimeSpan SettlePollStep = TimeSpan.FromSeconds(2);

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly TargetProbeOptions _options;
    private readonly ILogger<ScheduledTargetProbeService> _logger;

    public ScheduledTargetProbeService(
        IServiceProvider services,
        TimeProvider clock,
        IOptions<TargetProbeOptions> options,
        ILogger<ScheduledTargetProbeService> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _services = services;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The floor is the option's own, applied again here so a value that reached the service another way cannot
        // turn the schedule into a flood of live calls.
        var interval = TimeSpan.FromMinutes(Math.Max(TargetProbeOptions.MinimumIntervalMinutes, _options.IntervalMinutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProbePassAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                // Host teardown disposed the DI container out from under this pass; leave quietly.
                break;
            }
            catch (Exception ex)
            {
                LogPassError(Redacted(ex));
            }

            try
            {
                await Task.Delay(interval, _clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// One pass: settle what earlier passes left open, probe every interface this pass covers, and wait up to
    /// <c>:SettleSeconds</c> for those probes to come back.
    /// </summary>
    public async Task ProbePassAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var ledger = scope.ServiceProvider.GetRequiredService<ILedger>();
        var documents = scope.ServiceProvider.GetRequiredService<DeliveryDocumentLoader>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IRunDispatcher>();

        var carried = await OpenProbesAsync(ledger, ct).ConfigureAwait(false);
        await SweepAsync(catalog, ledger, carried, ct).ConfigureAwait(false);

        var queued = await QueueAsync(catalog, ledger, documents, dispatcher, ct).ConfigureAwait(false);
        await AwaitSettlementAsync(catalog, ledger, queued, ct).ConfigureAwait(false);
    }

    // ---- Queueing ----------------------------------------------------------------------------------------------

    /// <summary>Queues a probe for every interface this pass covers, and starts each one's activity.</summary>
    private async Task<List<PendingProbe>> QueueAsync(
        CatalogDbContext catalog, ILedger ledger, DeliveryDocumentLoader documents, IRunDispatcher dispatcher, CancellationToken ct)
    {
        var names = _options.PipelineNames();
        var pipelines = await PipelinesAsync(catalog, names, ct).ConfigureAwait(false);
        if (names.Count > 0)
        {
            var missing = names
                .Where(name => !pipelines.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (missing.Count > 0)
            {
                LogUnknownPipelines(string.Join(", ", missing));
            }
        }

        var queued = new List<PendingProbe>();
        var budget = _options.MaxPerPass;
        var capped = false;
        foreach (var pipeline in pipelines)
        {
            ct.ThrowIfCancellationRequested();
            if (budget <= 0)
            {
                capped = true;
                break;
            }

            var (source, problem) = await DeliveryEndpoints.ResolveSourceAsync(catalog, documents, pipeline.Id, ct).ConfigureAwait(false);
            if (source is null)
            {
                // A flow whose catalog copy does not parse has no target to probe; its pipeline page says the same.
                LogFlowUnreadable(pipeline.Name, problem?.ProblemDetails.Detail ?? "the catalog's copy of the flow could not be read.");
                continue;
            }

            foreach (var flow in source.Source.Interfaces)
            {
                if (budget <= 0)
                {
                    capped = true;
                    break;
                }

                budget--;
                try
                {
                    var pending = await QueueOneAsync(
                        catalog, ledger, dispatcher, new DeliveryEndpoints.FlowContext(source.Pipeline, source.Source, flow), ct).ConfigureAwait(false);
                    if (pending is not null)
                    {
                        queued.Add(pending);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One flow's target must never stop the rest of the estate being probed.
                    LogProbeNotQueued(flow.Label, Redacted(ex));
                }
            }
        }

        if (capped)
        {
            LogCapped(_options.MaxPerPass);
        }

        if (queued.Count > 0)
        {
            LogQueued(queued.Count, pipelines.Count);
        }

        return queued;
    }

    /// <summary>The active delivery pipelines this pass covers, in name order.</summary>
    private static async Task<List<PipelineRef>> PipelinesAsync(CatalogDbContext catalog, IReadOnlyList<string> names, CancellationToken ct)
    {
        var query = catalog.Pipelines.AsNoTracking().Where(p => p.Kind == FlowDefinition.FlowTypeName && p.Active);
        if (names.Count > 0)
        {
            var wanted = names.ToList();
            query = query.Where(p => wanted.Contains(p.Name));
        }

        var rows = await query
            .OrderBy(p => p.Name)
            .Select(p => new { p.Id, p.Name })
            .Take(MaxPipelinesPerPass)
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(r => new PipelineRef(r.Id, r.Name)).ToList();
    }

    /// <summary>Queues one interface's probe and opens the activity that will carry its outcome.</summary>
    private async Task<PendingProbe?> QueueOneAsync(
        CatalogDbContext catalog, ILedger ledger, IRunDispatcher dispatcher, DeliveryEndpoints.FlowContext flow, CancellationToken ct)
    {
        var result = await DeliveryEndpoints.QueueProbeAsync(catalog, dispatcher, flow, ScheduleActor, ScheduleActor, ct).ConfigureAwait(false);
        if (result.Result is not Accepted<ComputeTaskAccepted> accepted || accepted.Value is null)
        {
            LogProbeNotQueued(
                flow.Flow.Label,
                (result.Result as ProblemHttpResult)?.ProblemDetails.Detail ?? "the control plane would not queue the probe.");
            return null;
        }

        var taskId = accepted.Value.TaskId;
        var target = flow.Flow.Target;
        target.Headers.TryGetValue("data-partition-id", out var partition);

        // What the activity records about the target is what the target view already shows: the endpoint as the document
        // declares it (a reference, never a resolved secret), the partition and the protocol. Redacted regardless, since
        // nothing the ledger stores is allowed to carry a credential.
        var parameters = Redacted(JsonSerializer.Serialize(new
        {
            taskId,
            pipelineId = flow.Pipeline.Id,
            @interface = flow.Flow.Interface,
            endpoint = target.Endpoint,
            partition,
            protocol = DeliveryProtocols.Name(target.Protocol),
        }));

        var activity = await ledger.StartActivityAsync(
            new ActivityRecord
            {
                FlowId = flow.FlowId,
                FlowName = flow.Flow.Label,
                Kind = ActivityKind,
                Actor = ScheduleActor,
                StartedUtc = _clock.GetUtcNow().UtcDateTime,
                ParametersJson = parameters,
            },
            ct).ConfigureAwait(false);

        return new PendingProbe(activity.ActivityId, taskId, flow.Flow.Label, flow.Flow.Interface);
    }

    // ---- Settling ----------------------------------------------------------------------------------------------

    /// <summary>
    /// The probes an earlier pass queued and never settled, read from the ledger so a restart loses none. An activity
    /// whose task id cannot be read is settled here rather than carried by every pass from now on.
    /// </summary>
    private async Task<List<PendingProbe>> OpenProbesAsync(ILedger ledger, CancellationToken ct)
    {
        var running = await ledger.ListActivitiesAsync(
            new ActivityQuery { Kind = ActivityKind, Actor = ScheduleActor, Outcome = Running, Max = MaxOpenProbes }, ct).ConfigureAwait(false);

        var open = new List<PendingProbe>(running.Count);
        foreach (var activity in running)
        {
            ct.ThrowIfCancellationRequested();
            var (taskId, interfaceName) = Read(activity.ParametersJson);
            if (taskId is { } id)
            {
                open.Add(new PendingProbe(activity.ActivityId, id, activity.FlowName, interfaceName));
                continue;
            }

            await SettleAsync(
                ledger,
                new PendingProbe(activity.ActivityId, Guid.Empty, activity.FlowName, interfaceName),
                ProbeOutcomes.Error,
                "the probe's task could not be read from its parameters, so its outcome is unknown",
                ct).ConfigureAwait(false);
        }

        return open;
    }

    /// <summary>Waits up to <c>:SettleSeconds</c> for the probes just queued, settling each as it comes back.</summary>
    private async Task AwaitSettlementAsync(CatalogDbContext catalog, ILedger ledger, IReadOnlyList<PendingProbe> queued, CancellationToken ct)
    {
        var deadline = _clock.GetUtcNow() + TimeSpan.FromSeconds(_options.SettleSeconds);
        var open = queued;
        while (open.Count > 0)
        {
            open = await SweepAsync(catalog, ledger, open, ct).ConfigureAwait(false);
            var left = deadline - _clock.GetUtcNow();
            if (open.Count == 0 || left <= TimeSpan.Zero)
            {
                // Whatever is still running is settled by the next pass, which finds it open in the ledger.
                break;
            }

            await Task.Delay(left < SettlePollStep ? left : SettlePollStep, _clock, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Settles every probe of <paramref name="pending"/> whose task has finished, and returns those still running.</summary>
    private async Task<List<PendingProbe>> SweepAsync(
        CatalogDbContext catalog, ILedger ledger, IReadOnlyList<PendingProbe> pending, CancellationToken ct)
    {
        var open = new List<PendingProbe>();
        if (pending.Count == 0)
        {
            return open;
        }

        var tasks = new Dictionary<Guid, TaskRow>();
        foreach (var chunk in pending.Chunk(TaskLookupChunk))
        {
            var ids = chunk.Select(p => p.TaskId).ToList();
            var rows = await catalog.ComputeTasks.AsNoTracking()
                .Where(t => ids.Contains(t.TaskId))
                .Select(t => new { t.TaskId, t.Status, t.Error, t.ResultJson })
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var row in rows)
            {
                tasks[row.TaskId] = new TaskRow(row.Status, row.Error, row.ResultJson);
            }
        }

        foreach (var probe in pending)
        {
            ct.ThrowIfCancellationRequested();
            if (!tasks.TryGetValue(probe.TaskId, out var task))
            {
                // The queue no longer holds the task (retention, or it was removed): nothing can ever report on this
                // probe, so it is closed now instead of being read again by every pass.
                await SettleAsync(
                    ledger, probe, ProbeOutcomes.Error,
                    $"the probe's task {probe.TaskId:D} is no longer in the queue, so its result could not be read", ct).ConfigureAwait(false);
                continue;
            }

            if (!RunStatuses.IsTerminal(task.Status))
            {
                open.Add(probe);
                continue;
            }

            var (outcome, summary) = Settlement(task);
            await SettleAsync(ledger, probe, outcome, summary, ct).ConfigureAwait(false);
        }

        return open;
    }

    /// <summary>Closes one probe's activity with what it found, counts it, and says so in the log.</summary>
    private async Task SettleAsync(ILedger ledger, PendingProbe probe, string outcome, string summary, CancellationToken ct)
    {
        // The audit trail's outcome is what an operator acts on, so a target that would not answer reads failed there;
        // the metric's outcome tag keeps the four cases apart.
        var recorded = outcome switch
        {
            ProbeOutcomes.Reachable => "completed",
            ProbeOutcomes.Cancelled => "cancelled",
            _ => "failed",
        };

        try
        {
            await ledger.CompleteActivityAsync(probe.ActivityId, recorded, summary, null, _clock.GetUtcNow().UtcDateTime, ct: ct).ConfigureAwait(false);
        }
        catch (DeliveryException ex)
        {
            // The activity went (a prune, a restored database): the outcome is still counted, and nothing is carried over.
            LogActivityGone(probe.ActivityId, probe.FlowLabel, Redacted(ex));
        }

        DeliveryMetrics.ProbeSettled(probe.FlowLabel, probe.Interface, outcome);
        if (outcome == ProbeOutcomes.Reachable)
        {
            LogReachable(probe.FlowLabel, summary);
        }
        else
        {
            LogNotReachable(probe.FlowLabel, outcome, summary);
        }
    }

    /// <summary>What a finished task says about the target: the outcome to count and the summary to record.</summary>
    private static (string Outcome, string Summary) Settlement(TaskRow task) => task.Status switch
    {
        RunStatuses.Succeeded => FromResult(task.ResultJson),
        RunStatuses.Cancelled => (ProbeOutcomes.Cancelled, "the probe was cancelled before it recorded a result"),
        _ => (ProbeOutcomes.Error, $"the probe could not run: {Redacted(task.Error ?? "the node recorded no reason")}"),
    };

    /// <summary>The probe's own result, as <c>delivery-probe</c> wrote it: reachable or not, with the status and the path it asked on.</summary>
    private static (string Outcome, string Summary) FromResult(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
        {
            return (ProbeOutcomes.Error, "the probe finished without recording a result");
        }

        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            var reachable = root.TryGetProperty("reachable", out var answered) && answered.ValueKind == JsonValueKind.True;
            var status = root.TryGetProperty("status", out var code) && code.TryGetInt32(out var value) ? value : 0;
            var path = root.TryGetProperty("path", out var asked) ? asked.GetString() ?? string.Empty : string.Empty;
            var detail = root.TryGetProperty("detail", out var said) ? said.GetString() ?? string.Empty : string.Empty;
            var summary =
                $"{(reachable ? ProbeOutcomes.Reachable : ProbeOutcomes.Unreachable)}: {(status > 0 ? $"HTTP {status}" : "no answer")} " +
                $"at {(path.Length == 0 ? "the target" : path)}{(detail.Length == 0 ? string.Empty : $" ({detail})")}";
            return (reachable ? ProbeOutcomes.Reachable : ProbeOutcomes.Unreachable, Redacted(summary));
        }
        catch (JsonException ex)
        {
            return (ProbeOutcomes.Error, $"the probe's result could not be read: {Redacted(ex.Message)}");
        }
    }

    /// <summary>The task and the interface an open activity's parameters name.</summary>
    private static (Guid? TaskId, string? Interface) Read(string? parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(parametersJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("taskId", out var task) || !task.TryGetGuid(out var taskId))
            {
                return (null, null);
            }

            var named = root.TryGetProperty("interface", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null;
            return (taskId, named);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>A message as it may be stored and shown: every resolved secret, token and key redacted out of it.</summary>
    private static string Redacted(string text) => HeaderRedaction.RedactMessage(SecretHygiene.RedactedMessage(text));

    private static string Redacted(Exception ex) => HeaderRedaction.RedactMessage(SecretHygiene.RedactedMessage(ex));

    /// <summary>An active delivery pipeline a pass covers.</summary>
    private sealed record PipelineRef(Guid Id, string Name);

    /// <summary>A probe queued on a node and the activity waiting for its outcome.</summary>
    private sealed record PendingProbe(long ActivityId, Guid TaskId, string FlowLabel, string? Interface);

    /// <summary>A probe task's row, as much of it as settling one needs.</summary>
    private sealed record TaskRow(string Status, string? Error, string? ResultJson);

    [LoggerMessage(Level = LogLevel.Information, Message = "Scheduled probe queued {Probes} target probe(s) across {Flows} delivery flow(s).")]
    private partial void LogQueued(int probes, int flows);

    [LoggerMessage(Level = LogLevel.Information, Message = "Scheduled probe of {Flow}: {Summary}")]
    private partial void LogReachable(string flow, string summary);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scheduled probe of {Flow} settled {Outcome}: {Summary}")]
    private partial void LogNotReachable(string flow, string outcome, string summary);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scheduled probe skipped flow {Flow}: {Detail}")]
    private partial void LogFlowUnreadable(string flow, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scheduled probe could not queue the probe of {Flow}: {Error}")]
    private partial void LogProbeNotQueued(string flow, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scheduled probe covered its whole budget of {MaxPerPass} interface(s) this pass; the rest are probed once the estate is narrowed with Osdu:TargetProbe:Pipelines or the budget is raised.")]
    private partial void LogCapped(int maxPerPass);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Osdu:TargetProbe:Pipelines names no active delivery pipeline: {Names}")]
    private partial void LogUnknownPipelines(string names);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scheduled probe could not record activity {ActivityId} of {Flow}: {Error}")]
    private partial void LogActivityGone(long activityId, string flow, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduled probe pass error: {Error}")]
    private partial void LogPassError(string error);
}
