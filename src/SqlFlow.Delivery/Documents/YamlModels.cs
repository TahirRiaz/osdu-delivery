// Mutable YAML shapes. They exist only so YamlDotNet can populate them; the loader maps them to the immutable model
// records in SqlFlow.Delivery.Model and validates as it goes. Every property must be listed: unknown keys are rejected.
// The platform envelope keys (name, batch, description, schedule, mode, lifecycle) are listed too: the platform
// parses them itself, the delivery loader only has to let them pass.
#pragma warning disable CA1812, CA2227, CA1002, CA1034, CA1720
namespace SqlFlow.Delivery.Documents;

internal sealed class DocumentProbeYaml
{
    public string? FlowType { get; set; }

    public string? DocumentType { get; set; }
}

internal sealed class FlowYaml
{
    public string? FlowType { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? Batch { get; set; }

    public object? Schedule { get; set; }

    public object? Mode { get; set; }

    public object? Lifecycle { get; set; }

    public Dictionary<string, FlowParameterYaml>? Parameters { get; set; }

    public FlowSourceYaml? Source { get; set; }

    public FlowRenderYaml? Render { get; set; }

    public FlowChangeYaml? Change { get; set; }

    public FlowTargetYaml? Target { get; set; }

    public FlowReliabilityYaml? Reliability { get; set; }

    public FlowVerifyYaml? Verify { get; set; }
}

internal sealed class FlowParameterYaml
{
    public bool Required { get; set; }

    public string? Default { get; set; }

    public string? Description { get; set; }
}

internal sealed class FlowSourceYaml
{
    public string? Location { get; set; }

    public string? Manifest { get; set; }

    public string? Records { get; set; }

    public Dictionary<string, FlowScopeYaml>? Scopes { get; set; }

    public Dictionary<string, string>? Payloads { get; set; }

    public string? Fingerprint { get; set; }

    public string? KnownState { get; set; }

    public string? Work { get; set; }
}

internal sealed class FlowScopeYaml
{
    public string? Records { get; set; }

    public string? Key { get; set; }
}

internal sealed class FlowRenderYaml
{
    public string? Mapping { get; set; }

    public string? References { get; set; }

    public Dictionary<string, string>? Parameters { get; set; }

    public string? Mappings { get; set; }

    public string? Snapshots { get; set; }
}

internal sealed class FlowChangeYaml
{
    public string? Detect { get; set; }

    public string? PayloadDetect { get; set; }

    public string? OnUnchanged { get; set; }

    public bool? UseSourceVersions { get; set; }
}

internal sealed class FlowTargetYaml
{
    public string? Endpoint { get; set; }

    public TargetAuthYaml? Auth { get; set; }

    public Dictionary<string, string>? Headers { get; set; }

    public string? Protocol { get; set; }

    public ProtocolOptionsYaml? ProtocolOptions { get; set; }
}

internal sealed class TargetAuthYaml
{
    public string? Type { get; set; }

    public string? SecretRef { get; set; }

    public string? SecondarySecretRef { get; set; }

    public string? HeaderName { get; set; }

    public string? ValuePrefix { get; set; }

    public TokenEndpointYaml? Token { get; set; }
}

internal sealed class TokenEndpointYaml
{
    public string? Url { get; set; }

    public string? DiscoveryUrl { get; set; }

    public Dictionary<string, string>? Body { get; set; }

    public bool BasicAuthClient { get; set; }

    public string? TokenPath { get; set; }

    public string? ApplyPrefix { get; set; }
}

internal sealed class ProtocolOptionsYaml
{
    public string? RecordPath { get; set; }

    public string? RecordMethod { get; set; }

    public string? DataPath { get; set; }

    public string? SessionPath { get; set; }

    public string? SessionDataPath { get; set; }

    public string? SessionCommitPath { get; set; }

    public string? VerifyPath { get; set; }

    public string? DeletePath { get; set; }

    public string? PurgePath { get; set; }

    public string? PurgeVersionsPath { get; set; }

    public string? BulkDeletePath { get; set; }

    public string? ProbePath { get; set; }

    public string? Payload { get; set; }

    public int? SessionThresholdChunks { get; set; }

    public long? MaxChunkValues { get; set; }

    public int? MaxChunkColumns { get; set; }

    public string? PayloadContentType { get; set; }

    public string? VersionPath { get; set; }

    public List<string>? PreserveDataKeys { get; set; }

    public int? BatchSize { get; set; }

    public string? UploadUrlPath { get; set; }

    public string? FileMetadataPath { get; set; }

    public string? DatasetKind { get; set; }

    public Dictionary<string, string>? UploadHeaders { get; set; }

    public string? DatasetsProperty { get; set; }

    public string? WorkflowName { get; set; }

    public string? WorkflowRunPath { get; set; }

    public string? WorkflowStatusPath { get; set; }

    public int? WorkflowPollSeconds { get; set; }

    public int? WorkflowTimeoutMinutes { get; set; }

    public string? ManifestKind { get; set; }

    public string? UploadUrlExpiry { get; set; }

    public string? FileDeletePath { get; set; }

    public string? ManifestSection { get; set; }

    public string? WorkflowAppKey { get; set; }

    public Dictionary<string, string>? WorkflowPayload { get; set; }

    public string? RecordQueryPath { get; set; }
}

internal sealed class FlowReliabilityYaml
{
    public int? Concurrency { get; set; }

    public FlowRetryYaml? Retry { get; set; }

    public List<int>? SkipStatusCodes { get; set; }

    public int? TimeoutSeconds { get; set; }

    public double? RateLimitRps { get; set; }

    public bool? VerifyTls { get; set; }

    public List<string>? UrlAllowlist { get; set; }

    public long? MaxResponseBytes { get; set; }

    public long? MaxRequestBodyBytes { get; set; }

    public int? LeaseSeconds { get; set; }

    public int? BatchSize { get; set; }

    public int? BatchRecords { get; set; }

    public int? FanOut { get; set; }

    public int? FanOutMinRecords { get; set; }

    public int? RenderParallelism { get; set; }
}

internal sealed class FlowRetryYaml
{
    public int? Attempts { get; set; }

    public string? Backoff { get; set; }

    public int? BaseDelayMs { get; set; }

    public int? MaxDelayMs { get; set; }

    public bool? HonorRetryAfter { get; set; }

    public int? RecordBaseDelayMinutes { get; set; }

    public int? RecordMaxDelayMinutes { get; set; }
}

internal sealed class FlowVerifyYaml
{
    public bool? Reconcile { get; set; }
}

internal sealed class MappingYaml
{
    public string? DocumentType { get; set; }

    public string? Name { get; set; }

    public string? Version { get; set; }

    public string? Kind { get; set; }

    public string? Description { get; set; }

    public MappingSourceYaml? Source { get; set; }

    public MappingIdentityYaml? Identity { get; set; }

    public MappingEnvelopeYaml? Envelope { get; set; }

    public Dictionary<string, MappingParameterYaml>? Parameters { get; set; }

    public List<MappingPropertyYaml>? Properties { get; set; }

    public Dictionary<string, List<MappingPropertyYaml>>? Definitions { get; set; }

    public List<MappingFixtureYaml>? Fixtures { get; set; }
}

internal sealed class MappingParameterYaml
{
    public bool Required { get; set; }

    public string? Default { get; set; }

    public string? Description { get; set; }
}

internal sealed class MappingSourceYaml
{
    public string? System { get; set; }

    public List<string>? Scopes { get; set; }
}

internal sealed class MappingIdentityYaml
{
    public List<string>? NaturalKey { get; set; }

    public string? Label { get; set; }
}

internal sealed class MappingEnvelopeYaml
{
    public List<string>? LegalTags { get; set; }

    public List<string>? OtherRelevantDataCountries { get; set; }

    public MappingAclYaml? Acl { get; set; }

    public Dictionary<string, string>? Tags { get; set; }
}

internal sealed class MappingAclYaml
{
    public List<string>? Owners { get; set; }

    public List<string>? Viewers { get; set; }
}

internal sealed class MappingPropertyYaml
{
    public string? Target { get; set; }

    public string? Source { get; set; }

    public string? Description { get; set; }

    public string? Transform { get; set; }

    public TransformConfigYaml? Config { get; set; }

    public bool Collection { get; set; }

    public string? Scope { get; set; }

    public List<MappingPropertyYaml>? Properties { get; set; }

    public string? Definition { get; set; }

    public List<PropertyExampleYaml>? Examples { get; set; }
}

internal sealed class TransformConfigYaml
{
    public string? Value { get; set; }

    public string? Delimiter { get; set; }

    public int? Index { get; set; }

    public string? Resolve { get; set; }

    public Dictionary<string, string>? Values { get; set; }

    public string? Default { get; set; }

    public string? Type { get; set; }

    public List<string>? MatchBy { get; set; }

    public string? Select { get; set; }

    public Dictionary<string, string>? ValueMap { get; set; }

    public string? OnMiss { get; set; }

    public string? System { get; set; }

    public List<string>? Keys { get; set; }

    public string? Format { get; set; }

    public string? InputFormat { get; set; }
}

internal sealed class PropertyExampleYaml
{
    public string? Source { get; set; }

    public Dictionary<string, string?>? Row { get; set; }

    public string? Target { get; set; }
}

internal sealed class MappingFixtureYaml
{
    public string? Name { get; set; }

    public Dictionary<string, string?>? Record { get; set; }

    public Dictionary<string, List<Dictionary<string, string?>>>? Scopes { get; set; }

    public Dictionary<string, string>? Parameters { get; set; }

    public string? Expected { get; set; }
}

internal sealed class RetrievalYaml
{
    public string? FlowType { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? Batch { get; set; }

    public object? Schedule { get; set; }

    public object? Mode { get; set; }

    public object? Lifecycle { get; set; }

    public Dictionary<string, FlowParameterYaml>? Parameters { get; set; }

    public RetrievalSourceYaml? Source { get; set; }

    public RetrievalTargetYaml? Target { get; set; }

    public RetrievalCacheYaml? Cache { get; set; }

    public FlowReliabilityYaml? Reliability { get; set; }
}

internal sealed class RetrievalCacheYaml
{
    public List<CachedTypeYaml>? Types { get; set; }

    public bool? MakeCurrent { get; set; }

    public string? Snapshots { get; set; }
}

internal sealed class CachedTypeYaml
{
    public string? Name { get; set; }

    public string? EntityType { get; set; }

    public string? Kind { get; set; }

    public string? Query { get; set; }

    // A path ("data.Code") or a mapping of path and as ({ path: data.NameAlias.AliasName, as: Alias }).
    public List<object>? Fields { get; set; }
}

internal sealed class RetrievalSourceYaml
{
    public string? Endpoint { get; set; }

    public TargetAuthYaml? Auth { get; set; }

    public Dictionary<string, string>? Headers { get; set; }

    public string? Kind { get; set; }

    public List<string>? Kinds { get; set; }

    public string? Query { get; set; }

    public List<string>? ReturnedFields { get; set; }

    public int? PageSize { get; set; }

    public string? SearchPath { get; set; }

    public string? QueryPath { get; set; }

    public RetrievalIncrementalYaml? Incremental { get; set; }

    public bool? FetchRecords { get; set; }

    public string? RecordQueryPath { get; set; }

    public int? FetchParallelism { get; set; }

    public string? ProbePath { get; set; }
}

internal sealed class RetrievalIncrementalYaml
{
    public string? Field { get; set; }

    public string? Since { get; set; }

    public int? LagMinutes { get; set; }
}

internal sealed class RetrievalTargetYaml
{
    public string? Location { get; set; }

    public string? Format { get; set; }

    public string? Compression { get; set; }

    public long? RollRecords { get; set; }

    public string? Manifest { get; set; }
}
