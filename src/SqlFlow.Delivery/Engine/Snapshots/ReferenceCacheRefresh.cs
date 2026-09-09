using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Storage;

namespace SqlFlow.Delivery.Engine.Snapshots;

/// <summary>
/// Refreshes the cache a retrieval flow declares (design.md section 6.2). The flow that syncs OSDU metadata is the
/// flow that knows where that metadata lives, so its <c>cache</c> section says which reference and master-data
/// types the mappings resolve against and which paths of them to keep. A refresh sweeps each declared type in full,
/// merges the result onto the current snapshot and mints a new immutable version, so a version always describes the
/// whole cache rather than the slice one run happened to touch.
/// </summary>
public sealed class ReferenceCacheRefresher
{
    private readonly EngineContext _context;
    private readonly ILogger _logger;

    public ReferenceCacheRefresher(EngineContext context, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(logger);
        _context = context;
        _logger = logger;
    }

    /// <summary>Captures the flow's cached types and mints the new snapshot version. Never null: a flow with no cache section is not refreshed.</summary>
    public async Task<ReferenceCacheOutcome> RefreshAsync(
        RetrievalDefinition flow, IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(values);
        var cache = flow.Cache ?? throw new DeliveryException($"Retrieval flow '{flow.Name}' declares no cache to refresh.");

        var root = DeliveryLayout.ResolveSnapshots(flow.SourcePath, cache.SnapshotsDirectory);
        var store = new FileSnapshotStore(root, _context.Stores);
        var builder = new SnapshotBuilder(store, _context.Time, _context.Loggers.CreateLogger<SnapshotBuilder>());

        var spec = Resolve(cache, values);
        using var osdu = new OsduConnection(flow.Source.Endpoint, flow.Source.Auth, flow.Source.Headers, flow.Reliability, _context.Secrets);
        var snapshot = await builder.ReferencesFromOsduAsync(osdu, spec, cache.MakeCurrent, ct).ConfigureAwait(false);

        var types = spec.Types
            .Select(t => snapshot.Type(t.Name))
            .Where(t => t is not null)
            .Select(t => new CachedTypeOutcome(t!.Name, t.EntityType, t.Items.Count, t.FieldNames))
            .ToList();
        _logger.LogInformation(
            "Cache refreshed into reference snapshot {Version}: {Types} type(s), {Items} item(s){Current}, store {Store}.",
            snapshot.Version, types.Count, types.Sum(t => t.Items), cache.MakeCurrent ? " (now current)" : string.Empty, root);
        return new ReferenceCacheOutcome(snapshot.Version, snapshot.CapturedUtc.UtcDateTime, cache.MakeCurrent, root, types);
    }

    /// <summary>The capture spec with the run's parameter values substituted into each type's query.</summary>
    private static ReferenceCaptureSpec Resolve(RetrievalCache cache, IReadOnlyDictionary<string, string> values)
    {
        var types = cache.Types
            .Select(type => type with { Query = FlowParameters.Substitute(type.Query, values) })
            .ToList();
        return new ReferenceCaptureSpec { Types = types };
    }
}

/// <summary>What a cache refresh produced, reported on the run and recorded in the catalog.</summary>
public sealed record ReferenceCacheOutcome(
    string Version, DateTime CapturedUtc, bool Current, string Store, IReadOnlyList<CachedTypeOutcome> Types)
{
    public long Items => Types.Sum(t => t.Items);
}

/// <summary>One cached type as the refresh left it.</summary>
public sealed record CachedTypeOutcome(string Name, string EntityType, int Items, IReadOnlyList<string> Fields);
