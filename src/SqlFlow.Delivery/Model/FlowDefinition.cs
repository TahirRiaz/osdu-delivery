using SqlFlow.Core;

namespace SqlFlow.Delivery.Model;

/// <summary>
/// The operational half of the document model (design.md section 9.2): where the drop is, which pinned mapping
/// renders it, where it goes and how reliably. Only <see cref="Render"/> affects what a document is; everything
/// else changes only how it gets there and stays out of the content hash (section 9.3).
/// </summary>
public sealed record FlowDefinition
{
    public const string FlowTypeName = "delivery";

    /// <summary>Path of the file the flow was loaded from, for error messages. Null for inline documents.</summary>
    public string? SourcePath { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>The platform batch the flow belongs to (grouping in listings and batch runs), from the envelope.</summary>
    public string? Batch { get; init; }

    /// <summary>Stable id derived from <see cref="Name"/> (see <see cref="Identity.FlowId"/>).</summary>
    public Guid Id => Identity.FlowId.Of(Name);

    public IReadOnlyDictionary<string, FlowParameter> Parameters { get; init; } = new Dictionary<string, FlowParameter>(StringComparer.Ordinal);

    public required FlowSource Source { get; init; }

    public required FlowRender Render { get; init; }

    public FlowChange Change { get; init; } = new();

    public required FlowTarget Target { get; init; }

    public FlowReliability Reliability { get; init; } = new();

    public FlowVerify Verify { get; init; } = new();

    /// <summary>
    /// The secret references the target declares (auth secrets, header values, token body values, the endpoint),
    /// keyed by their document path: what the platform lists as the flow's credential references. Only references
    /// (<c>${env:...}</c>, <c>${keyvault:...}</c>) are listed, never resolved values.
    /// </summary>
    public IEnumerable<KeyValuePair<string, string>> CredentialReferences()
    {
        if (Target.Auth.SecretRef is { } secret)
        {
            yield return new("target.auth.secretRef", secret);
        }

        if (Target.Auth.SecondarySecretRef is { } secondary)
        {
            yield return new("target.auth.secondarySecretRef", secondary);
        }

        if (IsReference(Target.Endpoint))
        {
            yield return new("target.endpoint", Target.Endpoint);
        }

        foreach (var (name, value) in Target.Headers.Where(kv => IsReference(kv.Value)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            yield return new($"target.headers.{name}", value);
        }

        if (Target.Auth.Token is { } token)
        {
            foreach (var (name, value) in token.Body.Where(kv => IsReference(kv.Value)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                yield return new($"target.auth.token.body.{name}", value);
            }
        }
    }

    private static bool IsReference(string value) => value.Contains("${", StringComparison.Ordinal);
}

public sealed record FlowParameter
{
    public bool Required { get; init; }

    public string? Default { get; init; }

    public string? Description { get; init; }
}

/// <summary>Where the drop lives and how its parts are laid out. Paths are relative to <see cref="Location"/>.</summary>
public sealed record FlowSource
{
    /// <summary>The drop root: an abfss:// or https:// Azure Storage URI, or a local path. Supports {parameter} tokens.</summary>
    public required string Location { get; init; }

    /// <summary>The manifest file name inside the drop. Default manifest.json.</summary>
    public string Manifest { get; init; } = "manifest.json";

    /// <summary>Glob for the root-scope record files, relative to the drop. Overrides the manifest when set.</summary>
    public string? Records { get; init; }

    /// <summary>Child scopes (one row set per record, keyed by the delivery key). Overrides the manifest when set.</summary>
    public IReadOnlyDictionary<string, FlowScope> Scopes { get; init; } = new Dictionary<string, FlowScope>(StringComparer.Ordinal);

    /// <summary>Payload sets: name to a path template with {deliveryKey} and a chunk glob (curves/{deliveryKey}/chunk_*.parquet).</summary>
    public IReadOnlyDictionary<string, string> Payloads { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The root-scope column carrying the source fingerprint for the tier-1 gate (design.md section 6.6).</summary>
    public string? Fingerprint { get; init; }

    /// <summary>Where a known-state publication is written when the run names no location: a directory or storage prefix
    /// the preparing side reads before its next drop. Supports {parameter} tokens. Null leaves it to the run.</summary>
    public string? KnownState { get; init; }
}

public sealed record FlowScope
{
    public required string Records { get; init; }

    /// <summary>The column holding the parent record's delivery key. Default deliveryKey.</summary>
    public string Key { get; init; } = "deliveryKey";
}

public sealed record FlowRender
{
    /// <summary>Pinned mapping reference in the form Name@version. Never floating (design.md section 9.2).</summary>
    public required string Mapping { get; init; }

    /// <summary>
    /// "pinned" resolves the snapshot store's current reference version at the start of each render and records
    /// it; an explicit version string pins that version.
    /// </summary>
    public string References { get; init; } = "pinned";

    /// <summary>Values for the parameters the mapping declares. They enter the content hash (design.md section 9.5).</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The mappings directory, relative to the flow file. Null finds the nearest <c>mappings</c> directory walking up from the flow file.</summary>
    public string? MappingsDirectory { get; init; }

    /// <summary>The snapshot store root (a directory relative to the flow file, or a storage URI). Null finds the nearest <c>snapshots</c> directory walking up from the flow file.</summary>
    public string? SnapshotsDirectory { get; init; }

    public string MappingName => SplitMapping().Name;

    public string MappingVersion => SplitMapping().Version;

    private (string Name, string Version) SplitMapping()
    {
        var at = Mapping.IndexOf('@', StringComparison.Ordinal);
        return at <= 0 || at == Mapping.Length - 1
            ? throw new FlowValidationException($"render.mapping '{Mapping}' must be pinned as 'Name@version'.")
            : (Mapping[..at], Mapping[(at + 1)..]);
    }
}

public enum ChangeDetection
{
    /// <summary>Compare the hash of the rendered document (metadata) or the logical payload content.</summary>
    RenderedHash,

    /// <summary>Same as <see cref="RenderedHash"/> for payloads; kept as the documented name.</summary>
    ContentHash,

    /// <summary>Always deliver.</summary>
    Always,
}

public enum UnchangedAction
{
    Skip,
    Deliver,
}

public sealed record FlowChange
{
    public ChangeDetection Detect { get; init; } = ChangeDetection.RenderedHash;

    public ChangeDetection PayloadDetect { get; init; } = ChangeDetection.ContentHash;

    public UnchangedAction OnUnchanged { get; init; } = UnchangedAction.Skip;

    /// <summary>Enable the tier-0 whole-run gate on the manifest's source table versions.</summary>
    public bool UseSourceVersions { get; init; } = true;
}

public sealed record FlowTarget
{
    /// <summary>Base URL of the delivery endpoint. Supports ${env:...} references.</summary>
    public required string Endpoint { get; init; }

    public TargetAuth Auth { get; init; } = new() { Type = TargetAuthType.None };

    /// <summary>Extra headers on every delivery request (for example an APIM subscription key or data-partition-id).</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public required Protocols.DeliveryProtocol Protocol { get; init; }

    public ProtocolOptions ProtocolOptions { get; init; } = new();
}

public enum TargetAuthType
{
    None,
    Bearer,
    ApiKeyHeader,
    Basic,
    OAuth2ClientCredentials,
}

public sealed record TargetAuth
{
    public required TargetAuthType Type { get; init; }

    /// <summary>Primary secret reference: the token (Bearer/api key), the password (Basic) or the client secret (OAuth2).</summary>
    public string? SecretRef { get; init; }

    /// <summary>Secondary secret reference: the username (Basic) or the client id (OAuth2).</summary>
    public string? SecondarySecretRef { get; init; }

    public string? HeaderName { get; init; }

    public string? ValuePrefix { get; init; }

    public TargetTokenEndpoint? Token { get; init; }
}

public sealed record TargetTokenEndpoint
{
    public string? Url { get; init; }

    public string? DiscoveryUrl { get; init; }

    public IReadOnlyDictionary<string, string> Body { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public bool BasicAuthClient { get; init; }

    public string TokenPath { get; init; } = "access_token";

    public string ApplyPrefix { get; init; } = "Bearer ";
}

/// <summary>Protocol-specific knobs, parameterised by the flow rather than authored as steps (design.md section 8.4).</summary>
public sealed record ProtocolOptions
{
    /// <summary>Path (under the endpoint) that accepts an array of records. Default depends on the protocol.</summary>
    public string? RecordPath { get; init; }

    /// <summary>HTTP method for the record write: PUT (storage) or POST (wellbore DDMS). Default depends on the protocol.</summary>
    public string? RecordMethod { get; init; }

    /// <summary>Path template for the single-request bulk upload; {id} is the target id.</summary>
    public string? DataPath { get; init; }

    /// <summary>Path template for creating a bulk session.</summary>
    public string? SessionPath { get; init; }

    /// <summary>Path template for one chunk in a session; {sessionId} is the session id.</summary>
    public string? SessionDataPath { get; init; }

    /// <summary>Path template for committing or abandoning a session.</summary>
    public string? SessionCommitPath { get; init; }

    /// <summary>Path template for reading a record back (verify).</summary>
    public string? VerifyPath { get; init; }

    /// <summary>Path template for the logical (revertible) delete. Default depends on the protocol.</summary>
    public string? DeletePath { get; init; }

    /// <summary>Path template for the physical purge. Default depends on the protocol.</summary>
    public string? PurgePath { get; init; }

    /// <summary>Path of the service's info endpoint the probe calls. Default depends on the protocol.</summary>
    public string? ProbePath { get; init; }

    /// <summary>Which payload set (from source.payloads) the protocol streams. Null for record-only protocols.</summary>
    public string? Payload { get; init; }

    /// <summary>Use a session when the chunk count exceeds this. Default 1: single-chunk payloads go in one request.</summary>
    public int SessionThresholdChunks { get; init; } = 1;

    /// <summary>Media type of the payload chunks.</summary>
    public string PayloadContentType { get; init; } = "application/x-parquet";

    /// <summary>The JSON path in the write response holding id:version strings.</summary>
    public string VersionPath { get; init; } = "recordIdVersions[0]";

    /// <summary>
    /// Data keys OSDU owns that must be copied forward from the existing record when updating (design.md section
    /// 7.6). Empty means the rendered document replaces the whole data block.
    /// </summary>
    public IReadOnlyList<string> PreserveDataKeys { get; init; } = [];
}

public sealed record FlowReliability
{
    public int Concurrency { get; init; } = 8;

    public FlowRetry Retry { get; init; } = new();

    /// <summary>Non-retryable statuses that mark a record held instead of failing the run.</summary>
    public IReadOnlyList<int> SkipStatusCodes { get; init; } = [];

    public int TimeoutSeconds { get; init; } = 100;

    public double RateLimitRps { get; init; }

    public bool VerifyTls { get; init; } = true;

    public IReadOnlyList<string> UrlAllowlist { get; init; } = [];

    public long MaxResponseBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>
    /// The target's declared request body ceiling (design.md section 14.3). A payload chunk above it is held before
    /// anything is sent, instead of surfacing as a 413 mid-session. 0 means not declared.
    /// </summary>
    public long MaxRequestBodyBytes { get; init; }

    /// <summary>How long a worker's lease on a record lasts before a sweep may reclaim it.</summary>
    public int LeaseSeconds { get; init; } = 300;

    /// <summary>Records claimed per ledger round trip.</summary>
    public int BatchSize { get; init; } = 50;
}

public enum BackoffKind
{
    Exponential,
    Fixed,
}

public sealed record FlowRetry
{
    public int Attempts { get; init; } = 4;

    public BackoffKind Backoff { get; init; } = BackoffKind.Exponential;

    public int BaseDelayMs { get; init; } = 500;

    public int MaxDelayMs { get; init; } = 30_000;

    public bool HonorRetryAfter { get; init; } = true;

    /// <summary>Backoff between record-level attempts across worker passes (minutes, exponential per attempt).</summary>
    public int RecordBaseDelayMinutes { get; init; } = 1;

    public int RecordMaxDelayMinutes { get; init; } = 60;
}

public sealed record FlowVerify
{
    /// <summary>Re-queue drifted or missing records for redelivery.</summary>
    public bool Reconcile { get; init; }
}
