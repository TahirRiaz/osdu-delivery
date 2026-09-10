using System.Text.Json;
using System.Text.Json.Serialization;
using SqlFlow.Core;

namespace SqlFlow.Delivery.Drops;

/// <summary>
/// The manifest Databricks writes when a drop is complete (design.md section 3.3). It carries the idempotency key,
/// the mapping to use, the declared source schema per scope (the preflight gate's input 1), the source table
/// versions for the tier-0 gate, and where the payload chunks live. Unknown keys are a parse error.
/// </summary>
public sealed record DropManifest
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("manifestVersion")]
    public int ManifestVersion { get; init; } = CurrentVersion;

    [JsonPropertyName("submissionId")]
    public required Guid SubmissionId { get; init; }

    [JsonPropertyName("flow")]
    public required string Flow { get; init; }

    [JsonPropertyName("parameters")]
    public Dictionary<string, string> Parameters { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("mapping")]
    public required string Mapping { get; init; }

    [JsonPropertyName("createdUtc")]
    public DateTimeOffset CreatedUtc { get; init; }

    [JsonPropertyName("recordCount")]
    public int RecordCount { get; init; }

    /// <summary>Delta commit version per source table, for the tier-0 whole-run gate.</summary>
    [JsonPropertyName("sourceVersions")]
    public Dictionary<string, long> SourceVersions { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Row scopes: the root scope is named <c>record</c>; others are child scopes keyed by the delivery key.</summary>
    [JsonPropertyName("scopes")]
    public required Dictionary<string, ManifestScope> Scopes { get; init; }

    /// <summary>Payload sets by name.</summary>
    [JsonPropertyName("payloads")]
    public Dictionary<string, ManifestPayload> Payloads { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// True when every scope is co-partitioned with the root scope (design.md section 16.1): file i of a child scope
    /// holds exactly the children of the records in file i of the root scope, and every file is sorted by its key
    /// (the delivery key for the root, the parent key for a child). The intake then streams each partition as a
    /// merge join in constant memory, and partitions can be spread across nodes. Without it, children are joined
    /// through a disk-backed hash partition, which is bounded but slower.
    /// </summary>
    [JsonPropertyName("partitioned")]
    public bool Partitioned { get; init; }

    /// <summary>The number of root-scope files: the unit an intake can be split by.</summary>
    public int PartitionCount => Root.Files.Count;

    public const string RootScope = "record";

    public ManifestScope Root => Scopes.TryGetValue(RootScope, out var s)
        ? s
        : throw new FlowValidationException($"manifest: scopes must declare the root scope '{RootScope}'.");

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static DropManifest Parse(string json, string source)
    {
        ArgumentNullException.ThrowIfNull(json);
        DropManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<DropManifest>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new FlowValidationException($"{source}: invalid manifest - {ex.Message}", ex);
        }

        if (manifest is null)
        {
            throw new FlowValidationException($"{source}: the manifest is empty.");
        }

        manifest.Validate(source);
        return manifest;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public void Validate(string source)
    {
        if (ManifestVersion != CurrentVersion)
        {
            throw new FlowValidationException($"{source}: manifestVersion {ManifestVersion} is not supported (expected {CurrentVersion}).");
        }

        if (SubmissionId == Guid.Empty)
        {
            throw new FlowValidationException($"{source}: submissionId must be a non-empty UUID; it is the idempotency key.");
        }

        if (string.IsNullOrWhiteSpace(Flow))
        {
            throw new FlowValidationException($"{source}: flow is required.");
        }

        if (string.IsNullOrWhiteSpace(Mapping) || !Mapping.Contains('@', StringComparison.Ordinal))
        {
            throw new FlowValidationException($"{source}: mapping must be pinned as 'Name@version'.");
        }

        if (!Scopes.ContainsKey(RootScope))
        {
            throw new FlowValidationException($"{source}: scopes must declare the root scope '{RootScope}'.");
        }

        foreach (var (name, scope) in Scopes)
        {
            if (scope.Files.Count == 0)
            {
                throw new FlowValidationException($"{source}: scope '{name}' declares no files.");
            }

            if (scope.Columns.Count == 0)
            {
                throw new FlowValidationException($"{source}: scope '{name}' declares no columns; the drop must declare its schema.");
            }

            if (name != RootScope && string.IsNullOrWhiteSpace(scope.ParentKey))
            {
                throw new FlowValidationException($"{source}: child scope '{name}' must name its parentKey column.");
            }

            foreach (var file in scope.Files)
            {
                if (Path.IsPathRooted(file) || file.Contains("..", StringComparison.Ordinal))
                {
                    throw new FlowValidationException($"{source}: scope '{name}' file '{file}' must be a relative path inside the drop.");
                }
            }

            if (Partitioned && name != RootScope && scope.Files.Count != Scopes[RootScope].Files.Count)
            {
                throw new FlowValidationException(
                    $"{source}: the manifest is partitioned, so scope '{name}' must list one file per root file ({Scopes[RootScope].Files.Count}); it lists {scope.Files.Count}.");
            }
        }

        foreach (var (name, payload) in Payloads)
        {
            if (string.IsNullOrWhiteSpace(payload.PathTemplate) || !payload.PathTemplate.Contains("{deliveryKey}", StringComparison.Ordinal))
            {
                throw new FlowValidationException($"{source}: payload '{name}' pathTemplate must contain '{{deliveryKey}}'.");
            }

            // Whether a hash column is required depends on how the flow detects payload changes, which the manifest
            // cannot know: the planner insists on it unless the flow takes the chunk files' dates as the watermark.
            if (payload.HashColumn is not null && string.IsNullOrWhiteSpace(payload.HashColumn))
            {
                throw new FlowValidationException($"{source}: payload '{name}' hashColumn must name a root-scope column when it is given.");
            }
        }
    }

    /// <summary>The declared columns per scope, for the preflight gate.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>> DeclaredColumns()
        => Scopes.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlySet<string>)new HashSet<string>(kv.Value.Columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
}

public sealed record ManifestScope
{
    [JsonPropertyName("files")]
    public required List<string> Files { get; init; }

    [JsonPropertyName("columns")]
    public required List<ManifestColumn> Columns { get; init; }

    /// <summary>Child scopes only: the column holding the parent record's delivery key.</summary>
    [JsonPropertyName("parentKey")]
    public string? ParentKey { get; init; }

    /// <summary>Child scopes only: a column that orders the rows within a parent (for example a curve ordinal).</summary>
    [JsonPropertyName("orderBy")]
    public string? OrderBy { get; init; }
}

public sealed record ManifestColumn
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Logical type: string, long, double, boolean, timestamp.</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "string";
}

public sealed record ManifestPayload
{
    /// <summary>Drop-relative template with <c>{deliveryKey}</c>; the last segment is a glob over chunk files.</summary>
    [JsonPropertyName("pathTemplate")]
    public required string PathTemplate { get; init; }

    /// <summary>
    /// Root-scope column carrying the hash of the logical payload content (design.md section 6.4). Required unless
    /// the flow detects payload changes by the chunk files' modified times (<c>change.payloadDetect: lastModified</c>).
    /// </summary>
    [JsonPropertyName("hashColumn")]
    public string? HashColumn { get; init; }

    /// <summary>Root-scope column carrying the chunk count, when known; null means enumerate.</summary>
    [JsonPropertyName("chunkCountColumn")]
    public string? ChunkCountColumn { get; init; }

    [JsonPropertyName("contentType")]
    public string ContentType { get; init; } = "application/x-parquet";
}
