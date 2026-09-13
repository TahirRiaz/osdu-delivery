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

    /// <summary>
    /// The root-scope column saying when the source row last changed (a timestamp, or RFC 3339 / ISO 8601 text; text
    /// without an offset is read as UTC). An alternative to <see cref="Fingerprint"/> that is ordered as well as
    /// compared: a row modified after the version the ledger holds is planned through the whole pipeline, a row
    /// carrying the same moment is skipped without rendering, and a row older than what was delivered or queued is
    /// skipped as stale, so a replayed or late drop never takes OSDU back to an earlier version.
    /// </summary>
    public string? LastModified { get; init; }

    /// <summary>The column the per-record source gate reads: the last-modified column when declared, else the fingerprint.</summary>
    public string? ChangeColumn => LastModified ?? Fingerprint;

    /// <summary>Where a known-state publication is written when the run names no location: a directory or storage prefix
    /// the preparing side reads before its next drop. Supports {parameter} tokens. Null leaves it to the run.</summary>
    public string? KnownState { get; init; }

    /// <summary>
    /// Where the intake writes its work batches (the rendered documents the drains read back, design.md section
    /// 16.2): a directory or storage prefix the nodes can write. Supports {parameter} tokens. Null writes under
    /// <c>{location}/.work</c>, which then needs write access on the drop container.
    /// </summary>
    public string? Work { get; init; }

    /// <summary>
    /// Whether the flow takes records sent in a submission request rather than prepared as a drop (design.md section
    /// 3.4): an operator through the GUI, or a source system through the API. It is opt-in, because a flow fed by a
    /// prepared drop should not also accept hand-written records unless the estate says so, and it cannot be turned on
    /// for a flow whose protocol streams payload files, which only a drop can carry.
    /// </summary>
    public bool ManualSubmission { get; init; }

    /// <summary>
    /// Where a submission may point at payload files: prefixes (a container, a folder) the node opens with its own
    /// identity. A submission names locations rather than uploading bytes, so without a bound a caller could have any
    /// file the node can read shipped to OSDU. Empty means the flow's own drop location is the only root allowed.
    /// </summary>
    public IReadOnlyList<string> ManualSubmissionFileRoots { get; init; } = [];

    /// <summary>The resolved work root for a drop location: the declared one, or the drop's own <c>.work</c> folder.</summary>
    public static string WorkRoot(string? declaredWork, string dropLocation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dropLocation);
        if (!string.IsNullOrWhiteSpace(declaredWork))
        {
            return declaredWork.TrimEnd('/', '\\');
        }

        var root = dropLocation.TrimEnd('/', '\\');
        return root.Contains("://", StringComparison.Ordinal) ? root + "/.work" : Path.Combine(root, ".work");
    }
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

    /// <summary>The <see cref="CacheVersion"/> that takes whichever version of the cache is current when the run starts.</summary>
    public const string CurrentCacheVersion = "current";

    /// <summary>
    /// The cache the mapping's <c>cache.</c> sources read: the name of a cache flow (<c>flowType: cache</c>). Null for a flow
    /// whose mapping reads nothing from a cache.
    /// </summary>
    public string? Cache { get; init; }

    /// <summary>
    /// The version of the cache a render reads: <c>current</c> (the default) takes the cache's current version when the run
    /// starts and records it in the render context; a version label pins that version.
    /// </summary>
    public string CacheVersion { get; init; } = CurrentCacheVersion;

    /// <summary>Values for the parameters the mapping declares. They enter the content hash (design.md section 9.5).</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The mappings directory, relative to the flow file. Null finds the nearest <c>mappings</c> directory walking up from the flow file.</summary>
    public string? MappingsDirectory { get; init; }

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

    /// <summary>
    /// Payloads only: the payload's chunk files are its watermark. A payload is reconsidered when a chunk file was
    /// modified after the ones OSDU's payload was delivered from, or the set of chunk files changed; the drop's hash
    /// column, when it declares one, is still the final check, so a rewrite with the same content is not uploaded
    /// again. Chunk files older than what was delivered are stale and never sent.
    /// </summary>
    LastModified,
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

    /// <summary>Path template for the physical purge of the record and all its versions. Default depends on the protocol.</summary>
    public string? PurgePath { get; init; }

    /// <summary>
    /// Path template for the physical purge of a record's earlier versions, the latest one left live (openapi
    /// storage v2, <c>DELETE /records/{id}/versions</c>). Versions are owned by the storage service for every kind
    /// of record, so this defaults to the storage path even for protocols that deliver through another service.
    /// </summary>
    public string? PurgeVersionsPath { get; init; }

    /// <summary>
    /// Path of the storage service's bulk soft delete (<c>POST /records/delete</c>), which takes a list of record
    /// ids in one request. Used only for the reversible scope; the purges have no bulk endpoint.
    /// </summary>
    public string? BulkDeletePath { get; init; }

    /// <summary>Path of the service's info endpoint the probe calls. Default depends on the protocol.</summary>
    public string? ProbePath { get; init; }

    /// <summary>Which payload set (from source.payloads) the protocol streams. Null for record-only protocols.</summary>
    public string? Payload { get; init; }

    /// <summary>Use a session when the chunk count exceeds this. Default 1: single-chunk payloads go in one request.</summary>
    public int SessionThresholdChunks { get; init; } = 1;

    /// <summary>
    /// Cells (rows times columns) one parquet payload chunk may carry, checked before anything is sent. Defaults to
    /// the wellbore DDMS ceiling (<see cref="WellboreDdmsBulkLimits.MaxChunkValues"/>); 0 does not check.
    /// </summary>
    public long MaxChunkValues { get; init; } = WellboreDdmsBulkLimits.MaxChunkValues;

    /// <summary>
    /// Columns one parquet payload chunk may carry, checked before anything is sent. Defaults to the wellbore DDMS
    /// ceiling from OSDU M26 (<see cref="WellboreDdmsBulkLimits.MaxChunkColumns"/>); a target on M23 or M25 declares
    /// <see cref="WellboreDdmsBulkLimits.MaxChunkColumnsThroughM25"/>; 0 does not check.
    /// </summary>
    public int MaxChunkColumns { get; init; } = WellboreDdmsBulkLimits.MaxChunkColumns;

    /// <summary>Media type of the payload chunks.</summary>
    public string PayloadContentType { get; init; } = "application/x-parquet";

    /// <summary>The JSON path in the write response holding id:version strings.</summary>
    public string VersionPath { get; init; } = "recordIdVersions[0]";

    /// <summary>
    /// Sends <c>skipdupes=true</c> on the record write (openapi storage v2, PUT /records): "Skip duplicates when
    /// updating records with the same value". A record the service skips is named under <c>skippedRecordIds</c>
    /// and keeps its current version, and the ledger settles it at the version it already held.
    ///
    /// Off by default, because the spec does not say which parts of a record the service compares. Deliveries are
    /// already gated on the hash of the whole rendered document, so a write that reaches storage is a document that
    /// changed. If the service judged sameness by <c>data</c> alone, a change to only <c>acl</c>, <c>legal</c> or
    /// <c>tags</c> would be skipped: OSDU would keep the old access control or legal tags while the ledger recorded
    /// the change as delivered. What skipping buys is small (no extra version on a forced redelivery, or on a retry
    /// after a write that landed), so a flow opts in only once the comparison is confirmed for its target.
    /// </summary>
    public bool SkipDuplicates { get; init; }

    /// <summary>
    /// Path of the storage service's batched header read (<c>POST /query/records</c> by default), used to verify
    /// many records' versions in one request instead of one read per record.
    /// </summary>
    public string? VerifyBatchPath { get; init; }

    /// <summary>
    /// Well log protocol: where the wellbore DDMS sits under the flow's endpoint, when that endpoint is the OSDU
    /// platform root rather than the DDMS itself. The DDMS is deployed under <c>/api/os-wellbore-ddms</c> (the
    /// platform's ingress route, and the OSDU C# client's ServiceRegistry), and its own paths
    /// (<c>/ddms/v3/welllogs</c>, <c>/about</c>) are relative to that. Null, the default, means the endpoint already
    /// is the DDMS or a facade serving its paths. Set, every DDMS default path is taken under it, and the operations
    /// the storage service owns (the history purge) resolve under the endpoint as they do for every other protocol.
    /// A path option the flow sets explicitly is used as written either way.
    /// </summary>
    public string? DdmsRoot { get; init; }

    /// <summary>
    /// Whether a deliver or intake run asks the legal service about the mapping's legal tags before planning anything
    /// (openapi legal v1, <c>POST /legaltags:validate</c>). Default true. Off, the run starts without asking and says
    /// so in its log; storage still refuses a record whose tag is invalid, one record at a time.
    /// </summary>
    public bool ValidateLegalTags { get; init; } = true;

    /// <summary>
    /// The legal service's validate endpoint, when the default (<c>/api/legal/v1/legaltags:validate</c> under a
    /// platform-root endpoint) is not where it is: a path under the endpoint, or an absolute URL for a well log flow
    /// whose endpoint is the DDMS itself.
    /// </summary>
    public string? LegalValidatePath { get; init; }

    /// <summary>
    /// Data keys OSDU owns that must be copied forward from the existing record when updating (design.md section
    /// 7.6). Empty means the rendered document replaces the whole data block.
    /// </summary>
    public IReadOnlyList<string> PreserveDataKeys { get; init; } = [];

    /// <summary>Records per write request for the protocols that accept arrays (the storage service takes up to 500). Default 100.</summary>
    public int BatchSize { get; init; } = 100;

    /// <summary>The largest array the target accepts in one write; a flow cannot raise <see cref="BatchSize"/> above it.</summary>
    public const int MaxBatchSize = 500;

    /// <summary>File and manifest protocols: the path that hands out a signed upload location (file service v2).</summary>
    public string? UploadUrlPath { get; init; }

    /// <summary>File and manifest protocols: the path that registers a file's metadata record after the upload.</summary>
    public string? FileMetadataPath { get; init; }

    /// <summary>The kind of the dataset record registered per uploaded file. Default osdu:wks:dataset--File.Generic:1.0.0.</summary>
    public string DatasetKind { get; init; } = "osdu:wks:dataset--File.Generic:1.0.0";

    /// <summary>Extra headers on the signed-URL upload itself (the Azure landing zone needs x-ms-blob-type: BlockBlob).</summary>
    public IReadOnlyDictionary<string, string> UploadHeaders { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The data property of the record that lists the dataset ids of its files. Default Datasets.</summary>
    public string DatasetsProperty { get; init; } = "Datasets";

    /// <summary>Manifest protocol: the workflow (DAG) name the manifest is handed to. Default Osdu_ingest.</summary>
    public string WorkflowName { get; init; } = "Osdu_ingest";

    /// <summary>Manifest protocol: the path that triggers a workflow run; {workflow} is the workflow name.</summary>
    public string? WorkflowRunPath { get; init; }

    /// <summary>Manifest protocol: the path that reports a run's status; {workflow} and {runId} are substituted.</summary>
    public string? WorkflowStatusPath { get; init; }

    /// <summary>Manifest protocol: seconds between status polls. Default 10.</summary>
    public int WorkflowPollSeconds { get; init; } = 10;

    /// <summary>Manifest protocol: how long a workflow run may take before the record is retried. Default 60 minutes.</summary>
    public int WorkflowTimeoutMinutes { get; init; } = 60;

    /// <summary>
    /// Manifest protocol: how long to wait, after registering a record's files, for the search index to list the new
    /// datasets before the manifest names them. Ingestion checks references against the index and drops a record whose
    /// dataset it cannot find yet (observed on a live M26 service). 0 does not wait. Default 120 seconds.
    /// <para>
    /// File and manifest protocols: it also bounds how long a registration resumed after an interrupted try waits for
    /// the index to list the dataset that try may have created, before registering the file again.
    /// </para>
    /// </summary>
    public int DatasetIndexWaitSeconds { get; init; } = 120;

    /// <summary>
    /// File and manifest protocols: the search query path the waits for registered datasets ask (the manifest's wait
    /// for its own datasets, and a resumed registration's lookup by landing-zone path). Default /api/search/v2/query.
    /// </summary>
    public string? SearchQueryPath { get; init; }

    /// <summary>Manifest protocol: the manifest kind. Default osdu:wks:Manifest:1.0.0.</summary>
    public string ManifestKind { get; init; } = "osdu:wks:Manifest:1.0.0";

    /// <summary>
    /// File and manifest protocols: how long the signed upload URL stays valid (openapi file v2 expiryTime: 30M,
    /// 12H, 2D). Null takes the service default of one hour; a large file over a slow link needs more.
    /// </summary>
    public string? UploadUrlExpiry { get; init; }

    /// <summary>File and manifest protocols: the path that deletes a dataset record and its file on purge; {id} is the dataset id.</summary>
    public string? FileDeletePath { get; init; }

    /// <summary>
    /// Manifest protocol: the manifest section the records go into (one of <see cref="ManifestSections"/>). Null
    /// derives it from each record's kind.
    /// </summary>
    public string? ManifestSection { get; init; }

    /// <summary>Manifest protocol: the AppKey in the workflow's execution context. Default osdu-delivery.</summary>
    public string WorkflowAppKey { get; init; } = "osdu-delivery";

    /// <summary>Manifest protocol: extra entries of the execution context's Payload, beside AppKey and data-partition-id.</summary>
    public IReadOnlyDictionary<string, string> WorkflowPayload { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Manifest protocol: the storage path that reads records back by id after a run. Default /api/storage/v2/query/records.</summary>
    public string? RecordQueryPath { get; init; }

    /// <summary>The sections of an osdu:wks:Manifest:1.0.0 a record can be placed in.</summary>
    public static readonly IReadOnlyList<string> ManifestSections = ["ReferenceData", "MasterData", "WorkProduct", "WorkProductComponents", "Datasets"];
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

    /// <summary>Records claimed per ledger round trip when retrying individual records.</summary>
    public int BatchSize { get; init; } = 50;

    /// <summary>
    /// Records per work batch: one file of rendered documents the intake writes, one claim a drain takes, one unit of
    /// progress the submission page shows (design.md section 16.2). Default 500.
    /// </summary>
    public int BatchRecords { get; init; } = 500;

    /// <summary>
    /// How many additional runs a deliver run fans its work out to across the fleet: intake partitions first, then
    /// drains (design.md section 16.4). 0 runs everything on the node that claimed the run.
    /// </summary>
    public int FanOut { get; init; }

    /// <summary>Fan out only when the drop or the submission holds at least this many records. Default 1000.</summary>
    public int FanOutMinRecords { get; init; } = 1000;

    /// <summary>Drop partitions rendered concurrently on one node. 0 means half the processors, at least one.</summary>
    public int RenderParallelism { get; init; }

    /// <summary>The largest fan-out a flow may declare.</summary>
    public const int MaxFanOut = 64;

    /// <summary>The effective render parallelism for this host.</summary>
    public int EffectiveRenderParallelism => RenderParallelism > 0 ? RenderParallelism : Math.Max(1, Environment.ProcessorCount / 2);
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
