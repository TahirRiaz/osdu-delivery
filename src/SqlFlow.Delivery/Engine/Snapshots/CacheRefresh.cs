using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Engine.Snapshots;

/// <summary>
/// Refreshes a cache (design.md section 6.2). The cache flow says which reference and master-data types the mappings
/// resolve against and which paths of them to keep; a refresh captures every declared type in full and writes a new
/// version of the cache into the catalog when the content moved. It then compares the new version with the one it
/// replaces and tags each changed value that delivered records were built from, so what a refresh does to the delivered
/// estate is decided change by change.
/// </summary>
public sealed class CacheRefresher
{
    /// <summary>The offset search a plan counts with (openapi search v2, POST /query with trackTotalCount).</summary>
    private const string QueryPath = "/api/search/v2/query";

    private readonly EngineContext _context;
    private readonly ILogger _logger;

    public CacheRefresher(EngineContext context, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(logger);
        _context = context;
        _logger = logger;
    }

    /// <summary>Captures the cache flow's types, writes the new version when the content moved, and tags what its changes reach.</summary>
    public async Task<CacheRefreshOutcome> RefreshAsync(
        CacheDefinition flow, IReadOnlyDictionary<string, string> values, Guid runId, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var store = _context.Cache ?? throw new DeliveryException(
            $"Cache flow '{flow.Name}' writes its versions into the catalog, which this host was started without. Run it through the control plane or a node, or start the CLI with the catalog connection (--db, or the catalog variable).");
        var spec = CaptureSpec(flow, values);

        // The version being replaced, read before the capture writes the new one: it is what the delivered estate was
        // built from, and the only thing the new version can be compared against.
        var previousVersion = await store.CurrentVersionAsync(flow.Name, ct).ConfigureAwait(false);
        var previous = previousVersion is null ? null : await store.LoadAsync(flow.Name, previousVersion, ct).ConfigureAwait(false);

        using var osdu = await ConnectAsync(flow, ct).ConfigureAwait(false);
        var builder = new SnapshotBuilder(store, flow.Name, _context.Time, _context.Loggers.CreateLogger<SnapshotBuilder>());
        var write = await builder.CaptureAsync(osdu, spec, new CacheCapture(runId, actor, flow.Source.Endpoint), flow.MakeCurrent, ct).ConfigureAwait(false);
        var snapshot = write.Snapshot;

        var types = new List<CachedTypeOutcome>(spec.Types.Count);
        foreach (var typeSpec in spec.Types)
        {
            if (snapshot.Type(typeSpec.Name) is not { } type)
            {
                continue;
            }

            var impact = !write.Written || _context.Ledger is null
                ? new CacheImpactResult(type.Name, 0, 0, 0, 0)
                : await new CacheImpactAnalyzer(_context.Ledger, _context.Time, _logger)
                    .AnalyzeAsync(flow.Name, previous?.Type(type.Name), type, typeSpec.OnChange, previousVersion, snapshot.Version, ct)
                    .ConfigureAwait(false);
            types.Add(new CachedTypeOutcome(
                type.Name, type.EntityType, typeSpec.Kind, type.Items.Count, typeSpec.Fields.Select(f => f.Name).ToList(), ModeText(typeSpec.OnChange),
                impact.ChangedItems, impact.Changes, impact.AffectedRecords));
        }

        var current = write.Written ? flow.MakeCurrent : string.Equals(snapshot.Version, previousVersion, StringComparison.Ordinal);
        var outcome = new CacheRefreshOutcome(
            RunParameters.RefreshOperation, flow.Name, snapshot.Version, previousVersion, write.Written, current, snapshot.CapturedUtc.UtcDateTime, types);
        _logger.LogInformation(
            "Cache {Cache} refreshed: {Outcome}, {Types} type(s), {Items} record(s). {Changed} cached record(s) moved, reaching {Records} delivered record(s) through {Changes} change(s).",
            flow.Name, write.Written ? $"version {snapshot.Version} written{(current ? " and made current" : string.Empty)}" : $"unchanged at version {snapshot.Version}",
            types.Count, outcome.Items, types.Sum(t => t.ChangedItems), outcome.AffectedRecords, types.Sum(t => t.Changes));
        return outcome;
    }

    /// <summary>What a refresh would capture: how many records of each declared type the search matches. Writes nothing.</summary>
    public async Task<CachePlanOutcome> PlanAsync(CacheDefinition flow, IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(values);
        var spec = CaptureSpec(flow, values);
        var currentVersion = _context.Cache is { } store ? await store.CurrentVersionAsync(flow.Name, ct).ConfigureAwait(false) : null;

        using var osdu = await ConnectAsync(flow, ct).ConfigureAwait(false);
        var types = new List<CachePlanType>(spec.Types.Count);
        foreach (var type in spec.Types)
        {
            var body = new JsonObject { ["kind"] = type.Kind, ["query"] = type.Query, ["limit"] = 1, ["trackTotalCount"] = true };
            var page = await osdu.PostJsonAsync(QueryPath, body, ct).ConfigureAwait(false);
            var total = page["totalCount"] is JsonValue count && count.TryGetValue<long>(out var value)
                ? value
                : throw new DeliveryException($"{QueryPath} did not report totalCount for kind {type.Kind}.");
            _logger.LogInformation("plan {Type}: {Total} record(s) of kind {Kind} match the query {Query}", type.Name, total, type.Kind, type.Query);
            types.Add(new CachePlanType(type.Name, type.Kind, type.Query, type.Fields.Select(f => f.Name).ToList(), total));
        }

        return new CachePlanOutcome(RunParameters.PlanOperation, flow.Name, currentVersion, types, types.Sum(t => t.Records));
    }

    /// <summary>The declared types with the run's parameter values substituted into each query, as a refresh captures them.</summary>
    private static ReferenceCaptureSpec CaptureSpec(CacheDefinition flow, IReadOnlyDictionary<string, string> values)
        => new() { Types = flow.Types.Select(type => type with { Query = FlowParameters.Substitute(type.Query, values) }).ToList() };

    private Task<OsduConnection> ConnectAsync(CacheDefinition flow, CancellationToken ct)
        => OsduConnection.CreateAsync(
            flow.Source.Endpoint, flow.Source.Auth, flow.Source.Headers, flow.Reliability, _context.Secrets, allowLoopback: EngineContext.LoopbackAllowed, ct: ct);

    private static string ModeText(CacheChangeMode mode) => mode == CacheChangeMode.Auto ? "auto" : "approve";
}

/// <summary>
/// The <c>result</c> of a refresh run: the version the cache holds after it, the version it replaced, whether a version was
/// written at all, and per type what was captured and what its changes reach in the delivered estate.
/// </summary>
public sealed record CacheRefreshOutcome(
    string Operation, string Cache, string Version, string? PreviousVersion, bool Written, bool Current, DateTime CapturedUtc,
    IReadOnlyList<CachedTypeOutcome> Types)
{
    public long Items => Types.Sum(t => (long)t.Items);

    /// <summary>Delivered records the refresh found no longer matching the cache.</summary>
    public long AffectedRecords => Types.Sum(t => t.AffectedRecords);
}

/// <summary>One cached type as the refresh left it, with what its changes did to the delivered estate.</summary>
public sealed record CachedTypeOutcome(
    string Name, string EntityType, string Kind, int Items, IReadOnlyList<string> Fields, string OnChange, int ChangedItems, int Changes,
    long AffectedRecords);

/// <summary>One declared type as a plan counts it.</summary>
public sealed record CachePlanType(string Name, string Kind, string Query, IReadOnlyList<string> Fields, long Records);

/// <summary>The <c>result</c> of a plan run on a cache flow: what each type's search matches, and the version the cache holds now.</summary>
public sealed record CachePlanOutcome(string Operation, string Cache, string? CurrentVersion, IReadOnlyList<CachePlanType> Types, long Records);
