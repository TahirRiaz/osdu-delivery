using System.Text.Json.Nodes;

namespace SqlFlow.Delivery.Model;

/// <summary>
/// How the workflow route delivers an interface (docs/interfaces-design.md section 5.9;
/// osdu/specs/workflows/INTEGRATION.md section 3.13): the interface's own record written first (the anchor), its inputs
/// registered, one workflow run per stage triggered with an execution context built from the declaration, each run
/// polled, and what the runs created found and read back. The Workflow service returns a run's status and never what it
/// produced (section 5.1), so how the results are found is part of the declaration.
/// </summary>
public sealed record WorkflowRoute
{
    /// <summary>The most stages one route runs.</summary>
    public const int MaxStages = 4;

    /// <summary>The most input sets one route registers beside the record's own files.</summary>
    public const int MaxInputs = 8;

    /// <summary>How the interface's own record is written before the first run.</summary>
    public required WorkflowAnchor Anchor { get; init; }

    /// <summary>The runs, in order; a stage's outputs are what the next stage's context reads.</summary>
    public required IReadOnlyList<WorkflowStage> Stages { get; init; }

    /// <summary>The payload sets registered through the Dataset service as the workflow's inputs, beside the record's own files.</summary>
    public IReadOnlyList<WorkflowInput> Inputs { get; init; } = [];

    /// <summary>How the records the runs created are found once the last run finished.</summary>
    public WorkflowResults Results { get; init; } = new();

    /// <summary>When a delivery of the record runs the workflow.</summary>
    public WorkflowRunWhen RunWhen { get; init; } = WorkflowRunWhen.Changed;

    /// <summary>
    /// The secrets a context names with <c>{secret:name}</c>, as references (<c>${keyvault:...}</c>, <c>${env:...}</c>)
    /// resolved when the run is triggered and never stored or shown.
    /// </summary>
    public IReadOnlyDictionary<string, string> Secrets { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// The tag key the route writes on the anchor record with the anchor's tag (<c>{anchorTag}</c>), for workflows that
    /// copy the anchor's tags onto what they create; null writes none.
    /// </summary>
    public string? AnchorTagKey { get; init; }
}

/// <summary>How the workflow route writes the interface's own record.</summary>
public enum WorkflowAnchor
{
    /// <summary>The record is a dataset: its files are stored and it is registered through the Dataset service under its own id.</summary>
    Dataset,

    /// <summary>The record is written through the storage service, and the workflow follows it.</summary>
    Storage,
}

/// <summary>When a delivery of the record runs the workflow.</summary>
public enum WorkflowRunWhen
{
    /// <summary>Whenever the delivery writes the record or one of its inputs, and when a redelivery names the workflow.</summary>
    Changed,

    /// <summary>When the record is created, and when a redelivery names the workflow.</summary>
    Created,

    /// <summary>Only when a redelivery names the workflow: an operator starts each run.</summary>
    Requested,
}

/// <summary>One payload set a workflow reads, registered through the Dataset service, one dataset per file.</summary>
/// <param name="Name">The payload set's name, which the context names with <c>{input:name}</c>.</param>
/// <param name="DatasetKind">The kind each file is registered as.</param>
/// <param name="Optional">True when a record may carry no files for it; the context then reads an empty list.</param>
public sealed record WorkflowInput(string Name, string DatasetKind, bool Optional);

/// <summary>One workflow run of the route.</summary>
public sealed record WorkflowStage
{
    /// <summary>The workflow's name as the target's Workflow service registered it (names differ per deployment).</summary>
    public required string Workflow { get; init; }

    /// <summary>The known workflow whose payload contract the context is checked against (<c>WorkflowCatalog</c>); null when the name is one of its names.</summary>
    public string? Contract { get; init; }

    /// <summary>The execution context, with placeholders in its string values (<c>WorkflowTemplate</c>).</summary>
    public required JsonObject Context { get; init; }

    /// <summary>How long the run may take before the record is retried and the run resumed; 0 takes the contract's, or the flow's workflowTimeoutMinutes.</summary>
    public int TimeoutMinutes { get; init; }

    /// <summary>Seconds between polls; 0 takes the flow's workflowPollSeconds.</summary>
    public int PollSeconds { get; init; }

    /// <summary>The values the run produces that later stages and the results read, by name.</summary>
    public IReadOnlyDictionary<string, WorkflowOutput> Outputs { get; init; } = new Dictionary<string, WorkflowOutput>(StringComparer.Ordinal);
}

/// <summary>
/// One value a run produces. The Workflow contract returns none (osdu/specs/workflows/INTEGRATION.md section 5.1), so an
/// output is either one the route chose before the run (a template, such as the dataset id enyparser writes its
/// manifest to) or one read from an XCom entry of the run through Airflow's REST API.
/// </summary>
public sealed record WorkflowOutput
{
    /// <summary>A template the value is, known before the run.</summary>
    public string? Value { get; init; }

    /// <summary>The Airflow task whose XCom entry holds the value.</summary>
    public string? XComTask { get; init; }

    /// <summary>The XCom key under that task.</summary>
    public string? XComKey { get; init; }

    /// <summary>The entity type the record ids read from the entry must have (<c>dataset--File.Generic</c>); null takes every id.</summary>
    public string? Match { get; init; }
}

/// <summary>How the records a workflow created are found (osdu/specs/workflows/INTEGRATION.md section 5.2).</summary>
public enum WorkflowResultStrategy
{
    /// <summary>Nothing is looked for: the run's status is all the route records.</summary>
    None,

    /// <summary>The anchor record is read back: the workflow writes it (a conversion adds an artefact to it).</summary>
    Anchor,

    /// <summary>A template names the ids the run writes (ids set before the run).</summary>
    Ids,

    /// <summary>The anchor's <c>data.Artefacts</c> entries of a role and kind name the records.</summary>
    Artefact,

    /// <summary>A search query finds them.</summary>
    Search,

    /// <summary>The ids inside a manifest file the Dataset service holds.</summary>
    Manifest,

    /// <summary>An XCom entry of the last run lists them.</summary>
    XCom,
}

/// <summary>How the records a workflow created are found, checked and removed.</summary>
public sealed record WorkflowResults
{
    public const int DefaultMaxRecorded = 100;

    public WorkflowResultStrategy Strategy { get; init; } = WorkflowResultStrategy.None;

    /// <summary>For <see cref="WorkflowResultStrategy.Ids"/>: the template of the ids; for <see cref="WorkflowResultStrategy.Manifest"/>: the template of the manifest's dataset id.</summary>
    public string? Template { get; init; }

    /// <summary>For <see cref="WorkflowResultStrategy.Search"/>: the kind searched (a template).</summary>
    public string? Kind { get; init; }

    /// <summary>For <see cref="WorkflowResultStrategy.Search"/>: the query (a template).</summary>
    public string? Query { get; init; }

    /// <summary>For <see cref="WorkflowResultStrategy.Artefact"/>: the artefact role (<c>ConvertedContent</c>).</summary>
    public string? ArtefactRole { get; init; }

    /// <summary>For <see cref="WorkflowResultStrategy.Artefact"/>: the text the artefact's resource kind contains.</summary>
    public string? ArtefactKind { get; init; }

    /// <summary>For <see cref="WorkflowResultStrategy.XCom"/>: the task, key and entity type, as an output reads them.</summary>
    public WorkflowOutput? XCom { get; init; }

    /// <summary>How many records the run must have created for the delivery to count; a finished run is not a complete one.</summary>
    public int Minimum { get; init; }

    /// <summary>How long a search waits for the index to list the records, in seconds; null takes the flow's datasetIndexWaitSeconds.</summary>
    public int? WaitSeconds { get; init; }

    /// <summary>How many of the ids found are kept on the record; the count is always kept.</summary>
    public int MaxRecorded { get; init; } = DefaultMaxRecorded;

    /// <summary>Whether a removal of the record takes the records its runs created with it.</summary>
    public bool Remove { get; init; } = true;
}

/// <summary>
/// How a flow reaches the Airflow instance behind the Workflow service, for the outputs only Airflow's REST API returns
/// (osdu/specs/workflows/INTEGRATION.md section 5.2.1). Its own endpoint and credentials, as references.
/// </summary>
public sealed record AirflowAccess
{
    public required string Endpoint { get; init; }

    public TargetAuth Auth { get; init; } = new() { Type = TargetAuthType.None };

    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The REST API version: <c>v1</c> (Airflow 2) or <c>v2</c> (Airflow 3).</summary>
    public AirflowApiVersion ApiVersion { get; init; } = AirflowApiVersion.V1;
}

public enum AirflowApiVersion
{
    V1,
    V2,
}
