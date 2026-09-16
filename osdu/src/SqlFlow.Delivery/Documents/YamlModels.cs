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
    public string? Connection { get; set; }

    public FlowSourceTableYaml? Record { get; set; }

    public Dictionary<string, FlowSourceDatasetYaml>? Datasets { get; set; }

    public Dictionary<string, FlowPayloadYaml>? Payloads { get; set; }

    public string? LastModified { get; set; }

    public FlowSystemColumnsYaml? SystemColumns { get; set; }

    public FlowIncrementalYaml? Incremental { get; set; }

    public string? Work { get; set; }
}

internal sealed class FlowSourceTableYaml
{
    public string? Object { get; set; }

    public List<string>? Key { get; set; }

    public string? PrimaryKey { get; set; }

    public Dictionary<string, string>? Scope { get; set; }
}

internal sealed class FlowSourceDatasetYaml
{
    public string? Object { get; set; }

    public Dictionary<string, string>? Join { get; set; }

    public List<string>? OrderBy { get; set; }

    public int? MaxRowsPerRecord { get; set; }
}

internal sealed class FlowPayloadYaml
{
    public string? Root { get; set; }

    public string? LocationColumn { get; set; }

    public string? Pattern { get; set; }

    public string? HashColumn { get; set; }

    public string? ChunkCountColumn { get; set; }
}

/// <summary>A system column a flow names, opts out of with <c>~</c>, or leaves at its default by not naming it.</summary>
internal sealed class FlowSystemColumnsYaml
{
    private string? _updated;
    private string? _fileName;
    private string? _rowNumber;
    private string? _deleted;

    public string? Updated
    {
        get => _updated;
        set
        {
            _updated = value;
            HasUpdated = true;
        }
    }

    public string? FileName
    {
        get => _fileName;
        set
        {
            _fileName = value;
            HasFileName = true;
        }
    }

    public string? RowNumber
    {
        get => _rowNumber;
        set
        {
            _rowNumber = value;
            HasRowNumber = true;
        }
    }

    public string? Deleted
    {
        get => _deleted;
        set
        {
            _deleted = value;
            HasDeleted = true;
        }
    }

    [YamlDotNet.Serialization.YamlIgnore]
    public bool HasUpdated { get; private set; }

    [YamlDotNet.Serialization.YamlIgnore]
    public bool HasFileName { get; private set; }

    [YamlDotNet.Serialization.YamlIgnore]
    public bool HasRowNumber { get; private set; }

    [YamlDotNet.Serialization.YamlIgnore]
    public bool HasDeleted { get; private set; }
}

internal sealed class FlowIncrementalYaml
{
    public int? OverlapSeconds { get; set; }

    public int? PageSize { get; set; }

    public string? Isolation { get; set; }

    public int? CommandTimeoutSeconds { get; set; }
}

internal sealed class FlowRenderYaml
{
    public string? Mapping { get; set; }

    public string? Cache { get; set; }

    public string? CacheVersion { get; set; }

    public Dictionary<string, string>? Parameters { get; set; }

    public string? Mappings { get; set; }
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

    public bool? SkipDuplicates { get; set; }

    public string? VerifyBatchPath { get; set; }

    public string? DdmsRoot { get; set; }

    public bool? ValidateLegalTags { get; set; }

    public string? LegalValidatePath { get; set; }

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

    public int? DatasetIndexWaitSeconds { get; set; }

    public string? SearchQueryPath { get; set; }

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

    public MappingTemplateYaml? Template { get; set; }

    public string? Description { get; set; }

    public MappingDatasetYaml? Dataset { get; set; }

    public Dictionary<string, MappingParameterYaml>? Parameters { get; set; }

    public List<MappingEntryYaml>? Mappings { get; set; }

    public List<MappingFixtureYaml>? Fixtures { get; set; }
}

internal sealed class MappingTemplateYaml
{
    public string? Kind { get; set; }

    public string? Version { get; set; }
}

internal sealed class MappingParameterYaml
{
    public bool Required { get; set; }

    public string? Default { get; set; }

    public string? Description { get; set; }
}

internal sealed class MappingDatasetYaml
{
    public string? System { get; set; }

    public List<string>? Key { get; set; }

    public string? Label { get; set; }
}

internal sealed class MappingEntryYaml
{
    private object? _static;

    public string? Target { get; set; }

    public string? Source { get; set; }

    /// <summary>The static value in whatever YAML shape it was written: a scalar, a list or a mapping.</summary>
    public object? Static
    {
        get => _static;
        set
        {
            _static = value;
            HasStatic = true;
        }
    }

    /// <summary>True when the document wrote a <c>static</c> key, even with an empty value.</summary>
    [YamlDotNet.Serialization.YamlIgnore]
    public bool HasStatic { get; private set; }

    /// <summary>One findBy line, or a list of them.</summary>
    public object? FindBy { get; set; }

    /// <summary>Each modifier is a name (<c>trim</c>) or a one-key mapping (<c>split: { separator: ",", part: 1 }</c>).</summary>
    public List<object>? Modifiers { get; set; }

    public string? AppliesWhen { get; set; }

    public bool? Required { get; set; }

    public bool? IgnoreSeparators { get; set; }

    public string? Description { get; set; }
}

internal sealed class MappingFixtureYaml
{
    public string? Name { get; set; }

    public Dictionary<string, string?>? Record { get; set; }

    public Dictionary<string, List<Dictionary<string, string?>>>? Datasets { get; set; }

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

    public FlowReliabilityYaml? Reliability { get; set; }
}

internal sealed class CacheYaml
{
    public string? FlowType { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? Batch { get; set; }

    public object? Schedule { get; set; }

    public object? Mode { get; set; }

    public object? Lifecycle { get; set; }

    public Dictionary<string, FlowParameterYaml>? Parameters { get; set; }

    public CacheSourceYaml? Source { get; set; }

    public bool? MakeCurrent { get; set; }

    public string? OnChange { get; set; }

    public List<CachedTypeYaml>? Types { get; set; }

    public FlowReliabilityYaml? Reliability { get; set; }
}

internal sealed class CacheSourceYaml
{
    public string? Endpoint { get; set; }

    public TargetAuthYaml? Auth { get; set; }

    public Dictionary<string, string>? Headers { get; set; }
}

internal sealed class CachedTypeYaml
{
    public string? Name { get; set; }

    public string? EntityType { get; set; }

    public string? Kind { get; set; }

    public string? Query { get; set; }

    public string? OnChange { get; set; }

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
