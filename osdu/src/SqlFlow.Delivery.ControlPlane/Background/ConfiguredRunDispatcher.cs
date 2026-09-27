using Microsoft.Extensions.Logging;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core;
using SqlFlow.Core.Compute;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.ControlPlane.Background;

/// <summary>
/// The run dispatcher with the central configuration attached: every run of a flow this module owns is queued carrying
/// the properties its repository resolves to, so a flow naming <c>${env:NAME}</c> gets its value from the control plane
/// and falls back to the node only for what the control plane does not hold. A run carries the values set for no partition
/// and the values set for each partition, and resolves with the ones of the partition it is bound to first
/// (docs/partitions-design.md section 5); a node task of the module carries the values of the one partition it names.
/// </summary>
/// <remarks>
/// <para>
/// This decorates the dispatcher rather than the endpoints because a run reaches the queue by more ways than one: an
/// endpoint, a schedule fire, a fan-out group, the CLI through the API. All of them call
/// <see cref="IRunDispatcher.EnqueueAsync"/> or <see cref="IRunDispatcher.EnqueueGroupAsync"/>, so attaching here is the
/// only way a scheduled delivery and a delivery someone pressed a button for resolve the same references. A node task
/// reaches the queue through <see cref="IRunDispatcher.EnqueueComputeTaskAsync"/>, and is given its configuration here
/// for the same reason. Runs of SQLFlow's own flow kinds pass through untouched.
/// </para>
/// <para>
/// A run that already carries properties keeps them: a caller that composed a payload deliberately is not overruled.
/// Nothing is attached when the module holds no configuration, so an estate that keeps its values on its nodes queues
/// exactly the runs it queued before.
/// </para>
/// </remarks>
public sealed partial class ConfiguredRunDispatcher : IRunDispatcher
{
    /// <summary>The task argument naming the repository whose configuration a node task of the module is given.</summary>
    public const string RepoArgument = "repoId";

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
        return await _inner.EnqueueAsync(catalog, request with { Parameters = With(request.Parameters, supplied, request.FlowName) }, ct).ConfigureAwait(false);
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
        if (supplied.IsEmpty)
        {
            return await _inner.EnqueueGroupAsync(catalog, request, ct).ConfigureAwait(false);
        }

        var members = new Dictionary<string, RunParameters>(
            request.MemberParameters ?? new Dictionary<string, RunParameters>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var name in owned)
        {
            members[name] = With(members.GetValueOrDefault(name), supplied, name);
        }

        return await _inner.EnqueueGroupAsync(catalog, request with { MemberParameters = members }, ct).ConfigureAwait(false);
    }

    public async Task<Guid> EnqueueComputeTaskAsync(CatalogDbContext catalog, ComputeTaskEnqueueRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Owns(request.ProviderKind) || string.IsNullOrWhiteSpace(request.ArgumentsJson))
        {
            return await _inner.EnqueueComputeTaskAsync(catalog, request, ct).ConfigureAwait(false);
        }

        var task = ComputeTaskPayload.FromJson(request.ArgumentsJson, [request.Operation]);
        if (task.Argument(DeliveryOperation.ReferencesArgument) is not null
            || !Guid.TryParse(task.Argument(RepoArgument), out var repoId))
        {
            // A task that carries its configuration already, or names no repository to read one for, goes as it is.
            return await _inner.EnqueueComputeTaskAsync(catalog, request, ct).ConfigureAwait(false);
        }

        var supplied = await SuppliedAsync(repoId, request.SourceRef, ct).ConfigureAwait(false);
        var own = supplied.For(task.Argument(DeliveryOperation.PartitionArgument));
        if (own.Count == 0)
        {
            return await _inner.EnqueueComputeTaskAsync(catalog, request, ct).ConfigureAwait(false);
        }

        var carried = new DeliveryRunPayload { References = own }.ToJson()!;
        if (carried.Length > ComputeTaskPayload.MaxArgumentLength)
        {
            LogTaskTooLarge(_log, request.SourceRef, carried.Length, ComputeTaskPayload.MaxArgumentLength);
            return await _inner.EnqueueComputeTaskAsync(catalog, request, ct).ConfigureAwait(false);
        }

        var arguments = new Dictionary<string, string>(task.Arguments ?? new Dictionary<string, string>(), StringComparer.Ordinal)
        {
            [DeliveryOperation.ReferencesArgument] = carried,
        };
        var configured = task with { Arguments = arguments };
        configured.Validate([request.Operation]);
        return await _inner.EnqueueComputeTaskAsync(catalog, request with { ArgumentsJson = configured.ToJson() }, ct).ConfigureAwait(false);
    }

    // Everything that is not a queueing decision passes straight through: this decorator exists to attach configuration
    // to a run, and nothing else about dispatching changes.
    public Task<CancelOutcome> CancelAsync(CatalogDbContext catalog, Guid runId, CancellationToken ct = default)
        => _inner.CancelAsync(catalog, runId, ct);

    public Task<GroupCancelResult> CancelGroupAsync(CatalogDbContext catalog, Guid groupId, CancellationToken ct = default)
        => _inner.CancelGroupAsync(catalog, groupId, ct);

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
    private async Task<DeliveryConfiguration> SuppliedAsync(Guid repoId, string flowName, CancellationToken ct)
    {
        try
        {
            var supplied = await _config.ConfigurationAsync(repoId, ct).ConfigureAwait(false);
            var widest = supplied.Partitions.Values.Select(p => p.Count).Append(supplied.Base.Count).Max();
            if (widest > DeliveryConfigNames.MaxPerRun || supplied.Partitions.Count > PartitionNames.MaxPerFlow)
            {
                LogTooMany(_log, flowName, widest, DeliveryConfigNames.MaxPerRun);
                return DeliveryConfiguration.None;
            }

            return supplied;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogUnread(_log, flowName, ex.Message);
            return DeliveryConfiguration.None;
        }
    }

    /// <summary>
    /// <paramref name="parameters"/> carrying <paramref name="supplied"/> in its payload: the values set for no partition,
    /// and every partition's own. A payload that already names properties is left alone, nothing is written when there is
    /// nothing to write, and a configuration too large for a run's payload is left to the node, with a warning.
    /// </summary>
    private RunParameters With(RunParameters? parameters, DeliveryConfiguration supplied, string flowName)
    {
        var current = parameters ?? RunParameters.None;
        if (supplied.IsEmpty)
        {
            return current;
        }

        var payload = DeliveryRunPayload.Parse(current.Payload);
        if (payload.References.Count > 0 || payload.PartitionReferences.Count > 0)
        {
            return current;
        }

        var carried = (payload with { References = supplied.Base, PartitionReferences = supplied.Partitions }).ToJson();
        if (carried is { Length: > RunParameters.MaxPayloadLength })
        {
            LogRunTooLarge(_log, flowName, carried.Length, RunParameters.MaxPayloadLength);
            return current;
        }

        return current with { Payload = carried };
    }

    [LoggerMessage(EventId = 7401, Level = LogLevel.Warning,
        Message = "Flow '{FlowName}' resolves {Count} configuration properties in one scope, more than the {Max} a run carries, so this run resolves every reference on the node. Remove properties the estate does not use.")]
    private static partial void LogTooMany(ILogger logger, string flowName, int count, int max);

    [LoggerMessage(EventId = 7402, Level = LogLevel.Warning,
        Message = "The central configuration could not be read for flow '{FlowName}' ({Reason}), so this run resolves every reference on the node.")]
    private static partial void LogUnread(ILogger logger, string flowName, string reason);

    [LoggerMessage(EventId = 7403, Level = LogLevel.Warning,
        Message = "The central configuration of flow '{FlowName}' is {Length} characters as a run carries it, more than the {Max} a run's payload holds, so this run resolves every reference on the node. Remove properties the estate does not use, or set fewer for single partitions.")]
    private static partial void LogRunTooLarge(ILogger logger, string flowName, int length, int max);

    [LoggerMessage(EventId = 7404, Level = LogLevel.Warning,
        Message = "The central configuration of flow '{FlowName}' is {Length} characters as a node task carries it, more than the {Max} a task argument holds, so this task resolves every reference on the node. Remove properties the estate does not use.")]
    private static partial void LogTaskTooLarge(ILogger logger, string flowName, int length, int max);
}
