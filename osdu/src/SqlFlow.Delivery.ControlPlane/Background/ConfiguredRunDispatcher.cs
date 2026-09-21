using Microsoft.Extensions.Logging;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.ControlPlane.Background;

/// <summary>
/// The run dispatcher with the central configuration attached: every run of a flow this module owns is queued carrying
/// the properties its repository resolves to, so a flow naming <c>${env:NAME}</c> gets its value from the control plane
/// and falls back to the node only for what the control plane does not hold.
/// </summary>
/// <remarks>
/// <para>
/// This decorates the dispatcher rather than the endpoints because a run reaches the queue by more ways than one: an
/// endpoint, a schedule fire, a fan-out group, the CLI through the API. All of them call
/// <see cref="IRunDispatcher.EnqueueAsync"/> or <see cref="IRunDispatcher.EnqueueGroupAsync"/>, so attaching here is the
/// only way a scheduled delivery and a delivery someone pressed a button for resolve the same references. Runs of
/// SQLFlow's own flow kinds pass through untouched.
/// </para>
/// <para>
/// A run that already carries properties keeps them: a caller that composed a payload deliberately is not overruled.
/// Nothing is attached when the module holds no configuration, so an estate that keeps its values on its nodes queues
/// exactly the runs it queued before.
/// </para>
/// </remarks>
public sealed partial class ConfiguredRunDispatcher : IRunDispatcher
{
    private readonly IRunDispatcher _inner;
    private readonly DeliveryConfigStore _config;
    private readonly ILogger<ConfiguredRunDispatcher> _log;

    public ConfiguredRunDispatcher(IRunDispatcher inner, DeliveryConfigStore config, ILogger<ConfiguredRunDispatcher> log)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(log);
        _inner = inner;
        _config = config;
        _log = log;
    }

    public async Task<Guid> EnqueueAsync(CatalogDbContext catalog, RunEnqueueRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Owns(request.FlowKind))
        {
            return await _inner.EnqueueAsync(catalog, request, ct).ConfigureAwait(false);
        }

        var supplied = await SuppliedAsync(request.RepoId, request.FlowName, ct).ConfigureAwait(false);
        return await _inner.EnqueueAsync(catalog, request with { Parameters = With(request.Parameters, supplied) }, ct).ConfigureAwait(false);
    }

    public async Task<RunGroupEnqueueResult> EnqueueGroupAsync(
        CatalogDbContext catalog, RunGroupEnqueueRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A group's members can be of several kinds: the whole estate's pre, ingestion and delivery flows run as one
        // ordered set. Only the members this module owns are given the configuration.
        var owned = request.Members.Where(m => Owns(m.FlowKind)).Select(m => m.FlowName).ToList();
        if (owned.Count == 0)
        {
            return await _inner.EnqueueGroupAsync(catalog, request, ct).ConfigureAwait(false);
        }

        var supplied = await SuppliedAsync(request.RepoId, request.Anchor, ct).ConfigureAwait(false);
        if (supplied.Count == 0)
        {
            return await _inner.EnqueueGroupAsync(catalog, request, ct).ConfigureAwait(false);
        }

        var members = new Dictionary<string, RunParameters>(
            request.MemberParameters ?? new Dictionary<string, RunParameters>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var name in owned)
        {
            members[name] = With(members.GetValueOrDefault(name), supplied);
        }

        return await _inner.EnqueueGroupAsync(catalog, request with { MemberParameters = members }, ct).ConfigureAwait(false);
    }

    // Everything that is not a queueing decision passes straight through: this decorator exists to attach configuration
    // to a run, and nothing else about dispatching changes.
    public Task<CancelOutcome> CancelAsync(CatalogDbContext catalog, Guid runId, CancellationToken ct = default)
        => _inner.CancelAsync(catalog, runId, ct);

    public Task<GroupCancelResult> CancelGroupAsync(CatalogDbContext catalog, Guid groupId, CancellationToken ct = default)
        => _inner.CancelGroupAsync(catalog, groupId, ct);

    public Task<Guid> EnqueueComputeTaskAsync(CatalogDbContext catalog, ComputeTaskEnqueueRequest request, CancellationToken ct = default)
        => _inner.EnqueueComputeTaskAsync(catalog, request, ct);

    public Task<CancelOutcome> CancelComputeTaskAsync(CatalogDbContext catalog, Guid taskId, CancellationToken ct = default)
        => _inner.CancelComputeTaskAsync(catalog, taskId, ct);

    /// <summary>Whether <paramref name="flowKind"/> is one of this module's, and so takes the configuration.</summary>
    private static bool Owns(string? flowKind)
        => flowKind is not null
        && (flowKind.Equals(FlowDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase)
            || flowKind.Equals(CacheDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase)
            || flowKind.Equals(RetrievalDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The configuration for a repository, or nothing when the module has no database or the read fails. A run is never
    /// blocked by this: a node resolves from its own environment, which is what every run did before there was a
    /// configuration, and the failure is logged rather than raised.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> SuppliedAsync(Guid repoId, string flowName, CancellationToken ct)
    {
        try
        {
            var supplied = await _config.EffectiveAsync(repoId, ct).ConfigureAwait(false);
            if (supplied.Count > DeliveryConfigNames.MaxPerRun)
            {
                LogTooMany(_log, flowName, supplied.Count, DeliveryConfigNames.MaxPerRun);
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            return supplied;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogUnread(_log, flowName, ex.Message);
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// <paramref name="parameters"/> carrying <paramref name="supplied"/> in its payload. A payload that already names
    /// properties is left alone, and nothing is written when there is nothing to write.
    /// </summary>
    private static RunParameters With(RunParameters? parameters, IReadOnlyDictionary<string, string> supplied)
    {
        var current = parameters ?? RunParameters.None;
        if (supplied.Count == 0)
        {
            return current;
        }

        var payload = DeliveryRunPayload.Parse(current.Payload);
        if (payload.References.Count > 0)
        {
            return current;
        }

        return current with { Payload = (payload with { References = supplied }).ToJson() };
    }

    [LoggerMessage(EventId = 7401, Level = LogLevel.Warning,
        Message = "Flow '{FlowName}' resolves {Count} configuration properties, more than the {Max} a run carries, so this run resolves every reference on the node. Remove properties the estate does not use.")]
    private static partial void LogTooMany(ILogger logger, string flowName, int count, int max);

    [LoggerMessage(EventId = 7402, Level = LogLevel.Warning,
        Message = "The central configuration could not be read for flow '{FlowName}' ({Reason}), so this run resolves every reference on the node.")]
    private static partial void LogUnread(ILogger logger, string flowName, string reason);
}
