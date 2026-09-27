using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Model;

/// <summary>
/// A cache flow (<c>flowType: cache</c>, design.md section 6.2): reference and master data the mappings resolve against,
/// declared in the repository and captured into the cache of the flow's partition in the catalog. The document is the one
/// place what the flow caches is defined: the OSDU platform and partition to search, the types to cache and, for each type,
/// the paths of a record to keep. A refresh run captures every declared type in full and merges it into the partition's
/// cache, which writes a new version when the cached content moved. The partition has one cache, filled by every cache flow
/// that searches it and read by every delivery flow that delivers to it.
/// </summary>
public sealed record CacheDefinition
{
    public const string FlowTypeName = "cache";

    /// <summary>Path of the file the flow was loaded from, for error messages. Null for inline documents.</summary>
    public string? SourcePath { get; init; }

    /// <summary>The flow's name: its pipeline identity, and the name its versions and its hold on cached records are recorded under.</summary>
    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>The platform batch the flow belongs to, from the envelope.</summary>
    public string? Batch { get; init; }

    /// <summary>Stable id derived from <see cref="Name"/>, the same way every flow's is.</summary>
    public Guid Id => Identity.FlowId.Of(Name);

    /// <summary>Parameters a type's query may use as <c>{name}</c> tokens.</summary>
    public IReadOnlyDictionary<string, FlowParameter> Parameters { get; init; } = new Dictionary<string, FlowParameter>(StringComparer.Ordinal);

    /// <summary>The OSDU platform the types are searched on.</summary>
    public required CacheSource Source { get; init; }

    /// <summary>
    /// The partitions the flow builds a cache for (<c>partitions</c>, docs/partitions-design.md section 2.2), in document
    /// order; empty for a flow that names none and fills the cache of the partition its <c>source.headers</c> name.
    /// </summary>
    public IReadOnlyList<string> Partitions { get; init; } = [];

    /// <summary>
    /// The partition this definition is bound to (<see cref="ForPartition"/>): the one a refresh builds the cache of. Null for
    /// a flow that names no partitions, and for one that names some before it is bound.
    /// </summary>
    public string? Partition { get; init; }

    /// <summary>True when the flow names the partitions it builds a cache for.</summary>
    public bool DeclaresPartitions => Partitions.Count > 0;

    /// <summary>
    /// The partition whose cache the flow fills: the partition it is bound to, or for a flow that names none, the
    /// <c>data-partition-id</c> its searches carry, as the document writes it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The flow names its partitions and this definition is bound to none.</exception>
    public string Scope => Partition
        ?? (DeclaresPartitions
            ? throw new InvalidOperationException(
                $"Cache flow '{Name}' builds a cache for each of {PartitionNames.Listed(Partitions)}; bind it to the partition a refresh builds before asking for its scope.")
            : CacheScope.Of(Source.Headers, SourcePath ?? Name));

    /// <summary>
    /// The types the flow captures into its partition's cache; a cache flow declares at least one. A definition bound to a
    /// partition holds only the types built for it.
    /// </summary>
    public required IReadOnlyList<ReferenceTypeSpec> Types { get; init; }

    /// <summary>
    /// This definition bound to <paramref name="partition"/>, one of the partitions the flow names: its searches carry the
    /// partition as <c>data-partition-id</c>, and it holds only the types built for that partition.
    /// </summary>
    /// <exception cref="DeliveryException">The flow names no partitions, or not this one.</exception>
    public CacheDefinition ForPartition(string partition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        var wanted = partition.Trim();
        if (!DeclaresPartitions)
        {
            throw new DeliveryException(
                $"Cache flow '{Name}' names no partitions; it fills the cache of the partition its source.headers name, so it cannot be bound to '{wanted}'.");
        }

        var declared = Partitions.FirstOrDefault(p => string.Equals(p, wanted, StringComparison.OrdinalIgnoreCase))
            ?? throw new DeliveryException($"Cache flow '{Name}' builds no cache for partition '{wanted}'; it names {PartitionNames.Listed(Partitions)}.");
        var headers = new Dictionary<string, string>(Source.Headers, StringComparer.OrdinalIgnoreCase)
        {
            [CacheScope.PartitionHeader] = declared,
        };
        return this with
        {
            Partition = declared,
            Source = Source with { Headers = headers },
            Types = Types.Where(t => t.IsBuiltFor(declared)).ToList(),
        };
    }

    /// <summary>
    /// The flow as a run refreshes it (docs/partitions-design.md section 6): bound to the partition the run names, or to every
    /// partition the flow names, in document order, when the run names none; a flow that names no partitions is refreshed as
    /// it is. Each partition's cache is merged on its own, so refreshing them one after another never contends.
    /// </summary>
    /// <exception cref="DeliveryException">A partition is named for a flow that names none, or the one named is not the flow's.</exception>
    public IReadOnlyList<CacheDefinition> ForRun(string? partition)
    {
        var wanted = string.IsNullOrWhiteSpace(partition) ? null : partition.Trim();
        if (!DeclaresPartitions)
        {
            return wanted is null
                ? [this]
                : throw new DeliveryException(
                    $"Cache flow '{Name}' names no partitions; it fills the cache of the partition its source.headers name, so a run cannot target '{wanted}'. Leave the partition out, or name the flow's partitions under 'partitions'.");
        }

        return wanted is null ? Partitions.Select(ForPartition).ToList() : [ForPartition(wanted)];
    }

    /// <summary>The default <c>onChange</c> for the types that do not state one.</summary>
    public CacheChangeMode OnChange { get; init; } = CacheChangeMode.Auto;

    public FlowReliability Reliability { get; init; } = new();

    /// <summary>The secret references the source declares, keyed by document path; references only, never values.</summary>
    public IEnumerable<KeyValuePair<string, string>> CredentialReferences()
    {
        if (Source.Auth.SecretRef is { } secret)
        {
            yield return new("source.auth.secretRef", secret);
        }

        if (Source.Auth.SecondarySecretRef is { } secondary)
        {
            yield return new("source.auth.secondarySecretRef", secondary);
        }

        if (Source.Endpoint is { } endpoint && IsReference(endpoint))
        {
            yield return new("source.endpoint", endpoint);
        }

        if (Source.Connection is { } connection && IsReference(connection))
        {
            yield return new("source.connection", connection);
        }

        foreach (var (name, value) in Source.Headers.Where(kv => IsReference(kv.Value)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            yield return new($"source.headers.{name}", value);
        }

        if (Source.Auth.Token is { } token)
        {
            foreach (var (name, value) in token.Body.Where(kv => IsReference(kv.Value)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                yield return new($"source.auth.token.body.{name}", value);
            }
        }
    }

    private static bool IsReference(string value) => value.Contains("${", StringComparison.Ordinal);
}

/// <summary>
/// Where a cache flow's types come from: the OSDU platform its OSDU types are searched on and how to authenticate there, and
/// the database its table types are read from. The headers name the partition whose cache the flow fills, whatever the
/// origins of its types.
/// </summary>
public sealed record CacheSource
{
    /// <summary>The platform base URL, declared when the flow has an OSDU type; ${env:NAME} and ${keyvault:NAME} references allowed.</summary>
    public string? Endpoint { get; init; }

    /// <summary>
    /// The database the flow's table types are read from, declared exactly as a delivery flow's <c>source.connection</c> is:
    /// a whole secret reference, or a SQL Server connection string whose secrets are references.
    /// </summary>
    public string? Connection { get; init; }

    public TargetAuth Auth { get; init; } = new() { Type = TargetAuthType.None };

    /// <summary>The headers every search carries, the data partition among them.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
