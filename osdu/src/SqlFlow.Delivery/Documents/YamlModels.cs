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

    public FailWhenYaml? FailWhen { get; set; }

    /// <summary>The source's interfaces, by name, in document order; null for the single form.</summary>
    public OrderedDictionary<string, InterfaceYaml?>? Interfaces { get; set; }
}

/// <summary>
/// One interface of a source: its record table, its mapping and what its records carry, and the overrides of the
/// source's shared blocks. Everything else (the connection, the work location, the target) is the source's.
/// </summary>
internal sealed class InterfaceYaml
{
    public string? Description { get; set; }

    public string? Ledger { get; set; }

    public FlowSourceTableYaml? Record { get; set; }

    public Dictionary<string, FlowSourceDatasetYaml>? Datasets { get; set; }

    public FlowPayloadYaml? Files { get; set; }

    public FlowPayloadYaml? Bulk { get; set; }

    public string? LastModified { get; set; }

    public FlowSystemColumnsYaml? SystemColumns { get; set; }

    public FlowIncrementalYaml? Incremental { get; set; }

    public string? Mapping { get; set; }

    public string? Route { get; set; }

    public ProtocolOptionsYaml? ProtocolOptions { get; set; }

    public List<string>? After { get; set; }

    public FailWhenYaml? FailWhen { get; set; }

    public InterfaceRenderYaml? Render { get; set; }

    public FlowChangeYaml? Change { get; set; }

    public FlowReliabilityYaml? Reliability { get; set; }

    public FlowVerifyYaml? Verify { get; set; }

    /// <summary>How the workflow route delivers the interface.</summary>
    public WorkflowYaml? Workflow { get; set; }
}

/// <summary>The workflow route's declaration (<c>interfaces.&lt;name&gt;.workflow</c>, or <c>target.workflow</c> in the single form).</summary>
internal sealed class WorkflowYaml
{
    /// <summary>How the record is written first: dataset (registered with its files) or storage.</summary>
    public string? Anchor { get; set; }

    public List<WorkflowStageYaml>? Stages { get; set; }

    /// <summary>The payload sets registered as the workflow's inputs, by name, in document order.</summary>
    public OrderedDictionary<string, WorkflowInputYaml?>? Inputs { get; set; }

    public WorkflowResultsYaml? Results { get; set; }

    /// <summary>changed (the default), created or requested.</summary>
    public string? RunWhen { get; set; }

    /// <summary>The secrets a context names with {secret:name}, as references.</summary>
    public Dictionary<string, string>? Secrets { get; set; }

    /// <summary>The tag key the route writes on the record with its anchor tag.</summary>
    public string? AnchorTag { get; set; }
}

internal sealed class WorkflowStageYaml
{
    public string? Workflow { get; set; }

    public string? Contract { get; set; }

    /// <summary>The execution context: any YAML, with placeholders in its strings.</summary>
    public Dictionary<string, object?>? Context { get; set; }

    public int? TimeoutMinutes { get; set; }

    public int? PollSeconds { get; set; }

    public OrderedDictionary<string, WorkflowOutputYaml?>? Outputs { get; set; }
}

internal sealed class WorkflowInputYaml
{
    public string? Root { get; set; }

    public string? LocationColumn { get; set; }

    public string? Pattern { get; set; }

    public string? HashColumn { get; set; }

    public string? ChunkCountColumn { get; set; }

    public string? DatasetKind { get; set; }

    public bool? Optional { get; set; }
}

internal sealed class WorkflowOutputYaml
{
    public string? Value { get; set; }

    public WorkflowXComYaml? Xcom { get; set; }
}

internal sealed class WorkflowXComYaml
{
    public string? Task { get; set; }

    public string? Key { get; set; }

    public string? Match { get; set; }
}

internal sealed class WorkflowResultsYaml
{
    /// <summary>True when the run writes the record itself, which is read back.</summary>
    public bool? Anchor { get; set; }

    public string? Ids { get; set; }

    public WorkflowArtefactYaml? Artefact { get; set; }

    public WorkflowSearchYaml? Search { get; set; }

    public string? Manifest { get; set; }

    public WorkflowXComYaml? Xcom { get; set; }

    public int? Minimum { get; set; }

    public int? WaitSeconds { get; set; }

    public int? Keep { get; set; }

    public bool? Remove { get; set; }
}

internal sealed class WorkflowArtefactYaml
{
    public string? Role { get; set; }

    public string? Kind { get; set; }
}

internal sealed class WorkflowSearchYaml
{
    public string? Kind { get; set; }

    public string? Query { get; set; }
}

/// <summary>The Airflow instance behind the target's Workflow service (<c>target.airflow</c>).</summary>
internal sealed class AirflowYaml
{
    public string? Endpoint { get; set; }

    public TargetAuthYaml? Auth { get; set; }

    public Dictionary<string, string>? Headers { get; set; }

    public string? ApiVersion { get; set; }
}

/// <summary>The External Data Services deployment behind the target (<c>target.eds</c>).</summary>
internal sealed class EdsYaml
{
    /// <summary>Whether the records that configure EDS are checked before they are sent. Default true.</summary>
    public bool? Checks { get; set; }

    /// <summary>Whether proxy datasets are retrieved through eds-dms, which needs every registry entry's DatasetURL. Default true.</summary>
    public bool? Retrieval { get; set; }

    /// <summary>The eds-dms build the partition runs: corePlus, azure or gc.</summary>
    public string? Build { get; set; }
}

/// <summary>What an interface may override of the source's render block: the mapping is the interface's own key.</summary>
internal sealed class InterfaceRenderYaml
{
    public string? CacheVersion { get; set; }

    public Dictionary<string, string>? Parameters { get; set; }
}

internal sealed class FailWhenYaml
{
    public double? FailedPercent { get; set; }

    public int? MinRecords { get; set; }

    public int? ConsecutiveFailures { get; set; }

    public int? OutageFailures { get; set; }
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

    /// <summary>The DDMSs the flow delivers to, by the names the flow gives them, in document order.</summary>
    public OrderedDictionary<string, DdmsYaml?>? Ddms { get; set; }

    /// <summary>The workflow route's declaration of a document in the single form.</summary>
    public WorkflowYaml? Workflow { get; set; }

    public AirflowYaml? Airflow { get; set; }

    /// <summary>The External Data Services deployment behind the target, and how the records that configure it are checked.</summary>
    public EdsYaml? Eds { get; set; }

    /// <summary>The Production DDMS core service the dspdm route writes business object rows to.</summary>
    public DspdmYaml? Dspdm { get; set; }

    /// <summary>The Reservoir DDMS the etp route writes Energistics objects to.</summary>
    public EtpYaml? Etp { get; set; }

    /// <summary>What a record's references are checked against before it is sent: none (the ledger alone) or storage.</summary>
    public string? VerifyReferences { get; set; }
}

/// <summary>The Reservoir DDMS behind the target (<c>target.etp</c>).</summary>
internal sealed class EtpYaml
{
    /// <summary>Where the ETP WebSocket answers under the endpoint; the OSDU deployment's path by default.</summary>
    public string? Path { get; set; }

    /// <summary>The dataspace records go into when their document names none (<c>project/study</c>).</summary>
    public string? Dataspace { get; set; }

    /// <summary>Objects per message, which is also the batch the worker hands the route. Default 100.</summary>
    public int? ObjectsPerMessage { get; set; }

    /// <summary>The largest message this side sends or accepts, before the server's own maximum narrows it.</summary>
    public long? MaxMessageBytes { get; set; }

    /// <summary>The most bytes of one array a delivery reads into memory. Default 256 MiB.</summary>
    public long? MaxArrayBytes { get; set; }

    /// <summary>Whether a delivery leaves the dataspace locked, unlocking it before it writes. Default false.</summary>
    public bool? Lock { get; set; }
}

/// <summary>The Production DDMS core service behind the target (<c>target.dspdm</c>).</summary>
internal sealed class DspdmYaml
{
    /// <summary>Where DSPDM sits under the endpoint (<c>/api/dspdm/v1</c>); left out when the endpoint is DSPDM.</summary>
    public string? Root { get; set; }

    /// <summary>The time zone every request names: GMT+hh:mm. Default GMT+00:00.</summary>
    public string? Timezone { get; set; }

    /// <summary>The business objects the flow's kinds are rows of, by entity type.</summary>
    public OrderedDictionary<string, DspdmBusinessObjectYaml?>? BusinessObjects { get; set; }

    /// <summary>What happens to a row found by a record's key that the record did not write: hold (default) or update.</summary>
    public string? ExistingRows { get; set; }
}

/// <summary>One business object under <c>target.dspdm.businessObjects</c>.</summary>
internal sealed class DspdmBusinessObjectYaml
{
    /// <summary>Its name in DSPDM (<c>WELL TEST</c>); the entity type's when left out.</summary>
    public string? Name { get; set; }

    /// <summary>The attributes a row is found again by: one of its unique constraints.</summary>
    public List<string>? Key { get; set; }
}

/// <summary>One DDMS a flow declares under <c>target.ddms</c>.</summary>
internal sealed class DdmsYaml
{
    /// <summary>Where the DDMS is under the endpoint (<c>/api/os-wellbore-ddms</c>); null when the endpoint is the DDMS.</summary>
    public string? Root { get; set; }

    /// <summary>Its call pattern (<c>wellboreDdmsV3</c>, the default, <c>wellDeliveryV1</c>, <c>rafsV2</c>, <c>productionTimeSeriesV1</c>, <c>seismicStoreV3</c> or <c>reservoirManagement</c>).</summary>
    public string? Shape { get; set; }

    /// <summary>The id the DDMS is registered under in the Register service, to look up what the flow does not declare.</summary>
    public string? Register { get; set; }

    /// <summary>The entity types it serves, each with the collection it serves it under; the shape's own when left out.</summary>
    public OrderedDictionary<string, DdmsCollectionYaml?>? Collections { get; set; }

    /// <summary>Well Delivery DDMS: whether the deployment copies every entity into Storage. Default true.</summary>
    public bool? Mirror { get; set; }

    /// <summary>Well Delivery DDMS and Seismic Store: the provider the deployment runs on (azure, aws, gc, anthos, ibm, as the shape takes them).</summary>
    public string? Provider { get; set; }

    /// <summary>Well Delivery DDMS: the writes one process sends to the deployment at a time. Default 1.</summary>
    public int? Concurrency { get; set; }

    /// <summary>Production DDMS historian: where its query service is under the endpoint. Default <c>/api/pddms/query/v1</c>.</summary>
    public string? QueryRoot { get; set; }

    /// <summary>
    /// Production DDMS historian: how long a delivery reads accepted points back for, in seconds (default 60; 0 does not read
    /// them back). Reservoir Management DDMS: how long a delivery with rows waits for the service to take its record in.
    /// </summary>
    public int? SettleSeconds { get; set; }

    /// <summary>Production DDMS historian and Reservoir Management DDMS: the pause between two asks, in seconds. Default 5.</summary>
    public int? PollSeconds { get; set; }

    /// <summary>Production DDMS historian: the largest request body points are sent in, in bytes. Default 8000000.</summary>
    public long? MaxRequestBytes { get; set; }

    /// <summary>Seismic Store: the tenant the datasets are registered under. Default: the flow's <c>data-partition-id</c>.</summary>
    public string? Tenant { get; set; }

    /// <summary>Seismic Store: the subproject the datasets are registered in. Required.</summary>
    public string? Subproject { get; set; }

    /// <summary>Seismic Store: the folder under the subproject the datasets are registered in (<c>seismic/raw</c>). Default: the subproject's root.</summary>
    public string? Folder { get; set; }

    /// <summary>Seismic Store: the object store endpoint on anthos and IBM, or another Google Cloud Storage endpoint.</summary>
    public string? ObjectStore { get; set; }

    /// <summary>Seismic Store: the region S3 requests are signed for. Default us-east-1.</summary>
    public string? Region { get; set; }

    /// <summary>Seismic Store: the size in MiB of each object a single file is cut into on Azure (0 keeps it whole), and of each block or part. Default 32.</summary>
    public int? ChunkMiB { get; set; }

    /// <summary>Seismic Store: whether a delivered dataset is closed read-only. Default false.</summary>
    public bool? ReadOnly { get; set; }
}

/// <summary>One collection of a declared DDMS.</summary>
internal sealed class DdmsCollectionYaml
{
    /// <summary>The path segment the collection is served under (<c>welllogs</c>).</summary>
    public string? Path { get; set; }

    /// <summary>Whether the collection stores bulk data beside its records. Default false.</summary>
    public bool? Bulk { get; set; }

    /// <summary>What the bulk data's columns are checked against: unchecked (the default), curveIds or trajectoryStations.</summary>
    public string? Columns { get; set; }

    /// <summary>RAFS content collection: whether it holds several content types, each under its own path segment. Default false.</summary>
    public bool? TypedContent { get; set; }
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

    public string? FilesContentType { get; set; }

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

    public string? RegisterPath { get; set; }

    public string? DatasetInstructionsPath { get; set; }

    public string? DatasetRegisterPath { get; set; }

    public string? DatasetRetrievalPath { get; set; }

    public string? DatasetSoftDeletePath { get; set; }

    public string? ManifestByReference { get; set; }

    public int? ManifestInlineLimitKb { get; set; }

    public string? ByReferenceWorkflowName { get; set; }

    public string? WorkflowPath { get; set; }

    public string? ContentSchemaVersion { get; set; }
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

    /// <summary>A source's setting only: how many of its interfaces run at once.</summary>
    public int? ParallelInterfaces { get; set; }
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

    /// <summary>What each <c>search.&lt;name&gt;</c> source searches, by the name entries write it as.</summary>
    public Dictionary<string, MappingSearchYaml>? Searches { get; set; }

    public List<MappingEntryYaml>? Mappings { get; set; }

    public List<MappingFixtureYaml>? Fixtures { get; set; }
}

/// <summary>
/// One record set a mapping resolves against by searching the platform, rather than out of the partition's cache.
/// A cache is for a closed vocabulary a capture can hold whole; this is for the records a delivery refers to, which
/// are business data and grow without bound.
/// </summary>
internal sealed class MappingSearchYaml
{
    /// <summary>The OSDU kind searched, as a flow writes a kind (<c>osdu:wks:master-data--Wellbore:*</c>).</summary>
    public string? Kind { get; set; }

    /// <summary>The saved template whose schema says how the kind's properties are indexed, pinned like the mapping's own.</summary>
    public MappingTemplateYaml? Schema { get; set; }

    /// <summary>What this set is, shown wherever the mapping is listed.</summary>
    public string? Description { get; set; }
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

    public List<string>? Identity { get; set; }
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

    /// <summary>What the fixture assumes the platform answers to each search its render asks.</summary>
    public List<MappingFixtureSearchYaml>? Searches { get; set; }

    public string? Expected { get; set; }
}

/// <summary>One answer a fixture assumes: <c>{ search: Wellbore, field: data.FacilityName, value: NO 15/9-F-1, id: ... }</c>.</summary>
internal sealed class MappingFixtureSearchYaml
{
    public string? Search { get; set; }

    public string? Field { get; set; }

    public string? Value { get; set; }

    /// <summary>The one record found; left out when the platform holds no such record.</summary>
    public string? Id { get; set; }
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

    /// <summary>The dictionary document the type holds, in place of a kind: a lookup table kept in the repository.</summary>
    public string? Dictionary { get; set; }
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
