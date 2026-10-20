using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Engine.Snapshots;

/// <summary>
/// What a delivery run's cache step did (osdu/docs/reference/concepts/partition-cache.md, Deliveries keep a type current):
/// the partition and the types it kept current, how many records the run landed, how many of them it read back from OSDU's
/// storage service and how many storage did not return, the version of the cache it left current and whether it wrote it,
/// and, when it did nothing, why.
/// </summary>
public sealed record CacheKeptOutcome(
    string Scope,
    IReadOnlyList<string> Types,
    int Landed,
    int Read,
    int Missing,
    string? Version,
    bool Written,
    string? Skipped)
{
    /// <summary>The step in one sentence, as the run's result carries it for the run page.</summary>
    public string Summary => Describe();

    /// <summary>The step as a run's log line and its result say it.</summary>
    public string Describe()
    {
        // A step that failed before it knew the types, or the partition, names none.
        var types = Types.Count == 0 ? string.Empty : $" ({string.Join(", ", Types)})";
        var cache = Scope.Length == 0 ? "the partition's cache" : $"the cache of partition '{Scope}'";
        var subject = Scope.Length == 0 ? "The partition's cache" : $"The cache of partition '{Scope}'";
        if (Skipped is not null)
        {
            return $"{subject}{types} was not brought up to date: {Skipped}";
        }

        if (Landed == 0)
        {
            return $"The run landed no record {cache} keeps current{types}.";
        }

        var missing = Missing == 0 ? string.Empty : $"; storage did not return {Missing}";
        return Written
            ? $"{subject}{types} holds the {Read} record(s) the run landed as version {Version}{missing}."
            : $"{subject}{types} already held the {Read} record(s) the run landed, at version {Version}{missing}.";
    }
}

/// <summary>
/// The step a delivery run ends with (osdu/docs/reference/concepts/partition-cache.md, Deliveries keep a type current): when
/// a synced cache flow of the run's partition declares a type of the kind the run delivers with <c>keepCurrent: true</c>,
/// the records the run landed are read back from OSDU's storage service by id and merged into the partition's cache, held
/// by the delivery, so the flows that run after it render against them. Storage is read rather than the search index,
/// which holds a record a moment after it is written. The merge adds records and takes none away; a cache flow's refresh
/// takes out a record a delivery holds only once storage no longer holds it.
/// </summary>
public sealed class CacheKeeper
{
    /// <summary>
    /// The most records one run reads back. A run landing more is a bulk load, for which the cache flow's refresh is the
    /// cheaper way to bring the cache up to date; the step then says so and reads nothing.
    /// </summary>
    public const int MaxRecords = 50_000;

    private readonly EngineContext _context;
    private readonly ILogger _logger;

    public CacheKeeper(EngineContext context, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(logger);
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Brings the types of <paramref name="scope"/>'s cache that the deliveries of <paramref name="kind"/> keep current up to
    /// date with what the ledger <paramref name="ledgerFlowId"/> (named <paramref name="ledgerName"/>) landed since
    /// <paramref name="sinceUtc"/>. Null when no such type is declared, or the host has no ledger or cache.
    /// </summary>
    /// <param name="scope">The partition whose cache the run's records go into: the one the run rendered against.</param>
    /// <param name="kind">The exact kind the run delivers, which the kept types' kinds are matched against.</param>
    /// <param name="ledgerFlowId">The ledger the run delivered into.</param>
    /// <param name="ledgerName">The ledger's name, which the delivery's hold of its records is named after.</param>
    /// <param name="sinceUtc">When the run started, less a margin for the clocks of the nodes that delivered for it.</param>
    /// <param name="client">Opens the connection to OSDU the run delivered through, used only when there is something to read.</param>
    /// <param name="storagePath">The storage service's batch read (openapi storage v2, POST /query/records).</param>
    /// <param name="runId">The run, which the version written records.</param>
    /// <param name="actor">Who asked for the run, which the version written and the retention record.</param>
    /// <param name="ct">Cancels the step; what it merged before stands.</param>
    public async Task<CacheKeptOutcome?> KeepCurrentAsync(
        string scope,
        string kind,
        Guid ledgerFlowId,
        string ledgerName,
        DateTime sinceUtc,
        Func<CancellationToken, Task<OsduHttpClient>> client,
        string storagePath,
        Guid? runId,
        string actor,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(ledgerName);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if (_context.Cache is not { } store || _context.Ledger is not { } ledger)
        {
            return null;
        }

        var declaration = await store.DeclarationAsync(scope, ct).ConfigureAwait(false);
        var kept = declaration.KeptCurrentBy(kind);
        if (kept.Count == 0)
        {
            return null;
        }

        var names = kept.Select(t => t.TypeName).ToList();
        var landed = await ledger.LandedSinceAsync(ledgerFlowId, sinceUtc, MaxRecords + 1, ct).ConfigureAwait(false);
        if (landed.Count == 0)
        {
            return new CacheKeptOutcome(scope, names, 0, 0, 0, null, false, null);
        }

        if (landed.Count > MaxRecords)
        {
            return new CacheKeptOutcome(
                scope, names, landed.Count, 0, 0, null, false,
                $"the run landed more than {MaxRecords:N0} records, which the cache flow's refresh reads more cheaply than storage does one by one. Run the refresh of the cache flow declaring {string.Join(", ", names)}.");
        }

        // A cached record is kept under its id without a version, as the search index answers it.
        var ids = landed.Select(id => TargetId.WithoutVersion(id).TrimEnd(':')).Distinct(StringComparer.Ordinal).ToList();
        var storage = new StorageRecords(await client(ct).ConfigureAwait(false), storagePath);
        var records = new List<JsonObject>(ids.Count);
        var missing = 0;
        foreach (var batch in ids.Chunk(StorageRecords.Batch))
        {
            var read = await storage.ReadAsync(batch, null, ct).ConfigureAwait(false);
            missing += read.Missing.Count;
            foreach (var record in read.Records)
            {
                if (JsonNode.Parse(record.GetRawText()) is JsonObject parsed)
                {
                    records.Add(parsed);
                }
            }
        }

        var pool = new StringPool();
        var captured = kept
            .Select(type => new ReferenceType(
                type.TypeName,
                type.EntityType,
                records
                    .Select(record => SnapshotBuilder.Project(record, type.Fields, pool))
                    .OfType<ReferenceItem>()
                    .GroupBy(item => item.Id, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .OrderBy(item => item.Id, StringComparer.Ordinal)))
            .ToList();

        var holder = CacheHolders.Delivery(ledgerName);
        var builder = new SnapshotBuilder(store, scope, holder, _context.Time, _context.Loggers.CreateLogger<SnapshotBuilder>(), declaration.RetentionDays);
        var capture = new CacheCapture(runId, actor, $"{records.Count} record(s) of {kind} {ledgerName} delivered, read back from OSDU's storage service") { Adds = true };
        var write = await builder.WriteAsync(captured, capture, [], ct).ConfigureAwait(false);

        // What changed reaches the records built from the cache as a refresh's changes do: a reference written unverified that
        // the cache now holds is tagged found, and a moved value is tagged for the records built from it.
        if (write.Written)
        {
            var analyzer = new CacheImpactAnalyzer(ledger, _context.Time, _logger);
            foreach (var name in names)
            {
                var change = write.Changes.Of(name) ?? CacheTypeChange.Unchanged;
                if (change == CacheTypeChange.Unchanged || write.Snapshot.Type(name) is not { } type)
                {
                    continue;
                }

                await analyzer.AnalyzeAsync(
                    scope, write.Previous?.Type(name), type, declaration.ModeOf(name, CacheChangeMode.Auto), write.Previous?.Version, write.Snapshot.Version, ct)
                    .ConfigureAwait(false);
            }
        }

        var outcome = new CacheKeptOutcome(scope, names, ids.Count, records.Count, missing, write.Snapshot.Version, write.Written, null);
        _logger.LogInformation("{Outcome}", outcome.Describe());
        return outcome;
    }
}
