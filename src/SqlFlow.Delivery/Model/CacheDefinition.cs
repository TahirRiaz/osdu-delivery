using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Model;

/// <summary>
/// A cache flow (<c>flowType: cache</c>, design.md section 6.2): the reference and master data the mappings resolve
/// against, declared in the repository and captured into the catalog. The document is the one place what is cached is
/// defined: the OSDU platform to search, the types to cache and, for each type, the paths of a record to keep. A refresh
/// run captures every declared type in full and writes a new version of the cache when what it found differs from the
/// current version. Delivery flows read the cache by its name, under <c>render.cache</c>.
/// </summary>
public sealed record CacheDefinition
{
    public const string FlowTypeName = "cache";

    /// <summary>Path of the file the flow was loaded from, for error messages. Null for inline documents.</summary>
    public string? SourcePath { get; init; }

    /// <summary>The cache's name: its pipeline identity, and what a delivery flow names under <c>render.cache</c>.</summary>
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

    /// <summary>The types the cache holds; a cache declares at least one.</summary>
    public required IReadOnlyList<ReferenceTypeSpec> Types { get; init; }

    /// <summary>The default <c>onChange</c> for the types that do not state one.</summary>
    public CacheChangeMode OnChange { get; init; } = CacheChangeMode.Auto;

    /// <summary>Whether a refresh makes the version it writes the current one, which delivery flows render against by default.</summary>
    public bool MakeCurrent { get; init; } = true;

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

        if (IsReference(Source.Endpoint))
        {
            yield return new("source.endpoint", Source.Endpoint);
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

/// <summary>The OSDU side of a cache: the platform the declared types are searched on, and how to authenticate there.</summary>
public sealed record CacheSource
{
    /// <summary>The platform base URL; ${env:NAME} and ${keyvault:NAME} references allowed.</summary>
    public required string Endpoint { get; init; }

    public TargetAuth Auth { get; init; } = new() { Type = TargetAuthType.None };

    /// <summary>The headers every search carries, the data partition among them.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
