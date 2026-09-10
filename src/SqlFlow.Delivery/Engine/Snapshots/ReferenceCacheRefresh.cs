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

        // The version being replaced, read before the capture writes the new one: it is what the delivered estate
        // was built from, and the only thing the new version can be compared against.
        var previousVersion = await store.CurrentReferenceVersionAsync(ct).ConfigureAwait(false);
        var previous = previousVersion is null ? null : await store.LoadReferencesAsync(previousVersion, ct).ConfigureAwait(false);

        using var osdu = await OsduConnection.CreateAsync(flow.Source.Endpoint, flow.Source.Auth, flow.Source.Headers, flow.Reliability, _context.Secrets, ct: ct).ConfigureAwait(false);
        var snapshot = await builder.ReferencesFromOsduAsync(osdu, spec, cache.MakeCurrent, ct).ConfigureAwait(false);

        var types = new List<CachedTypeOutcome>();
        var impacts = new List<CacheImpactResult>();
        foreach (var typeSpec in spec.Types)
        {
            if (snapshot.Type(typeSpec.Name) is not { } type)
            {
                continue;
            }

            var impact = _context.Ledger is null
                ? new CacheImpactResult(type.Name, 0, 0, 0, 0)
                : await new CacheImpactAnalyzer(_context.Ledger, _context.Time, _logger)
                    .AnalyzeAsync(previous?.Type(type.Name), type, typeSpec.OnChange, previousVersion, snapshot.Version, ct)
                    .ConfigureAwait(false);
            impacts.Add(impact);
            types.Add(new CachedTypeOutcome(
                type.Name, type.EntityType, type.Items.Count, type.FieldNames, ModeText(typeSpec.OnChange), impact.ChangedItems, impact.Changes, impact.AffectedRecords));
        }

        _logger.LogInformation(
            "Cache refreshed into reference snapshot {Version}: {Types} type(s), {Items} item(s){Current}, store {Store}. {Changed} cached value(s) moved, reaching {Records} delivered record(s) through {Changes} change(s).",
            snapshot.Version, types.Count, types.Sum(t => t.Items), cache.MakeCurrent ? " (now current)" : string.Empty, root,
            impacts.Sum(i => i.ChangedItems), impacts.Sum(i => i.AffectedRecords), impacts.Sum(i => i.Changes));
        return new ReferenceCacheOutcome(snapshot.Version, previousVersion, snapshot.CapturedUtc.UtcDateTime, cache.MakeCurrent, root, types);
    }

    /// <summary>
    /// Why this run must not mint the cache into its store, or null. A run executing from a copy of the repository made
    /// for it (a staged version or a commit checkout) resolves a store named relative to the flow, or found by walking
    /// up from it, inside that copy: the snapshot would reach no delivery that renders against the cache, and the next
    /// refresh would find no earlier version to compare with, so no cached change would ever be tagged. Only a store
    /// named as an absolute path or a storage URI outlives the copy.
    /// </summary>
    internal static string? StoreProblem(RetrievalDefinition flow, RetrievalCache cache, bool ephemeralWorkingCopy)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(cache);
        var declared = cache.SnapshotsDirectory;
        if (!ephemeralWorkingCopy
            || (!string.IsNullOrWhiteSpace(declared) && (declared.Contains("://", StringComparison.Ordinal) || Path.IsPathFullyQualified(declared))))
        {
            return null;
        }

        var resolved = DeliveryLayout.ResolveSnapshots(flow.SourcePath, declared);
        return $"Retrieval flow '{flow.Name}' keeps the OSDU cache current, but this run executes from a copy of the repository made for it, and its snapshot store resolves inside that copy ({resolved}). "
            + "The snapshot it would mint reaches no delivery that renders against the cache, and the next refresh would have no earlier version to compare with. "
            + "Name a durable store both sides share, as cache.snapshots here and render.snapshots on the delivery flows that use the cache: an absolute path on shared storage, or a storage URI such as abfss://. Nothing was retrieved.";
    }

    private static string ModeText(CacheChangeMode mode) => mode == CacheChangeMode.Auto ? "auto" : "approve";

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
    string Version, string? PreviousVersion, DateTime CapturedUtc, bool Current, string Store, IReadOnlyList<CachedTypeOutcome> Types)
{
    public long Items => Types.Sum(t => t.Items);

    /// <summary>Delivered records the refresh found no longer matching the cache.</summary>
    public long AffectedRecords => Types.Sum(t => t.AffectedRecords);
}

/// <summary>One cached type as the refresh left it, with what its changes did to the delivered estate.</summary>
public sealed record CachedTypeOutcome(
    string Name, string EntityType, int Items, IReadOnlyList<string> Fields, string OnChange = "approve", int ChangedItems = 0,
    int Changes = 0, long AffectedRecords = 0);
