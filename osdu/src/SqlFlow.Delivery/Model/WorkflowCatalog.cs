using System.Text.RegularExpressions;

namespace SqlFlow.Delivery.Model;

/// <summary>The JSON shape a workflow reads under an execution context key.</summary>
public enum ContextValueType
{
    /// <summary>Any JSON value.</summary>
    Any,

    /// <summary>A JSON string.</summary>
    Text,

    /// <summary>A string, or a list of strings.</summary>
    TextOrList,

    /// <summary>A JSON array.</summary>
    List,

    /// <summary>A JSON object.</summary>
    Map,

    /// <summary>An object, or a list of objects (an inline manifest, or a batch of them).</summary>
    MapOrList,

    /// <summary>true or false.</summary>
    Flag,

    /// <summary>A JSON number.</summary>
    Number,
}

/// <summary>One key of an execution context a workflow reads.</summary>
/// <param name="Path">The key, with <c>/</c> between the levels of a nested key (<c>Payload/AppKey</c>).</param>
/// <param name="Type">The shape the workflow reads it as.</param>
/// <param name="Required">True when the workflow fails without it.</param>
/// <param name="Secret">True for a credential: the flow gives it as a secret reference only, and it is redacted wherever it is kept.</param>
/// <param name="Pattern">A pattern a string value must match, as the workflow or its callers check it.</param>
/// <param name="Allowed">The values a string may take.</param>
/// <param name="NotEmpty">True when an empty string or list is refused.</param>
/// <param name="Source">Where the brief states it.</param>
public sealed record ContextKey(
    string Path,
    ContextValueType Type,
    bool Required,
    string Source,
    bool Secret = false,
    string? Pattern = null,
    IReadOnlyList<string>? Allowed = null,
    bool NotEmpty = false);

/// <summary>
/// What one OSDU workflow reads from its execution context and how it behaves, from the workflows brief
/// (osdu/specs/workflows/INTEGRATION.md) or, for the External Data Services workflows, the EDS brief
/// (osdu/specs/eds-dms/INTEGRATION.md). The Workflow contract types the context loosely (every value an object), so each
/// workflow's own code is the contract a route's payload is checked against, before the run is triggered.
/// </summary>
public sealed record WorkflowContract
{
    /// <summary>The name a flow names the contract by (<c>contract:</c>), when its workflow is registered under another name.</summary>
    public required string Name { get; init; }

    /// <summary>The names deployments register the workflow under (osdu/specs/workflows/INTEGRATION.md section 1.4).</summary>
    public required IReadOnlyList<string> Workflows { get; init; }

    public required string Description { get; init; }

    /// <summary>The brief section the contract is read from.</summary>
    public required string Source { get; init; }

    /// <summary>False for a workflow that is not a delivery target (the test DAG).</summary>
    public bool Deliverable { get; init; } = true;

    /// <summary>Why the workflow is not a delivery target, when it is not.</summary>
    public string? NotDeliverable { get; init; }

    public IReadOnlyList<ContextKey> Keys { get; init; } = [];

    /// <summary>Groups of keys of which the context names at least one.</summary>
    public IReadOnlyList<IReadOnlyList<string>> AtLeastOne { get; init; } = [];

    /// <summary>Groups of keys the context names all of or none of.</summary>
    public IReadOnlyList<IReadOnlyList<string>> AllOrNone { get; init; } = [];

    /// <summary>
    /// Whether the route adds <c>Payload {AppKey, data-partition-id}</c> when the context does not set it: the
    /// osdu-airflow status operators read it, and a context without it fails the first status task (section 3.1).
    /// </summary>
    public bool AddsPayload { get; init; } = true;

    /// <summary>The run timeout the DAG sets, in minutes; 0 when it sets none. The route polls at least that long by default.</summary>
    public int TimeoutMinutes { get; init; }

    /// <summary>
    /// True for a workflow that only translates: it writes a manifest and ingests nothing, so a route that uses it runs
    /// <c>Osdu_ingest_by_reference</c> as a later stage (sections 3.6 and 3.8).
    /// </summary>
    public bool TranslatesOnly { get; init; }

    public ContextKey? Key(string path) => Keys.FirstOrDefault(k => string.Equals(k.Path, path, StringComparison.Ordinal));
}

/// <summary>The OSDU workflows OSDU Delivery knows the payload contracts of.</summary>
public static partial class WorkflowCatalog
{
    public const string OsduIngest = "osduIngest";
    public const string OsduIngestByReference = "osduIngestByReference";
    public const string ManifestIngestionTest = "manifestIngestionTest";
    public const string CsvParser = "csvParser";
    public const string EnergymlConverter = "energymlConverter";
    public const string EnergymlDelivery = "energymlDelivery";
    public const string EnyparserTranslation = "enyparserTranslation";
    public const string SegyToVds = "segyToVds";
    public const string SegyToZgy = "segyToZgy";
    public const string SegyToMdio = "segyToMdio";
    public const string EdsIngest = "edsIngest";
    public const string EdsScheduler = "edsScheduler";
    public const string EdsNaturalization = "edsNaturalization";

    /// <summary>
    /// The pattern the by-reference manifest id must match, as External Data Services validates it
    /// (osdu/specs/workflows/INTEGRATION.md section 3.3).
    /// </summary>
    public const string ManifestDatasetIdPattern = @"^[\w\-\.]+:dataset\-\-File\.Generic:[\w\-\.\:\%]+$";

    /// <summary>A record id without a version number, with or without the trailing colon of a latest-version reference (section 3.10).</summary>
    public const string LatestReferencePattern = @"^[\w\-\.]+:[\w\-\.]+:[\w\-\.\%]+:?$";

    private const string Workflows = "osdu/specs/workflows/INTEGRATION.md";
    private const string Eds = "osdu/specs/eds-dms/INTEGRATION.md";

    private static readonly ContextKey[] Payload =
    [
        new("Payload/AppKey", ContextValueType.Text, Required: true, $"{Workflows} section 3.1"),
        new("Payload/data-partition-id", ContextValueType.Text, Required: true, $"{Workflows} section 3.1", NotEmpty: true),
        new("userId", ContextValueType.Text, Required: false, $"{Workflows} section 3.1"),
    ];

    /// <summary>The configuration overlay keys the Energistics parser reads from the context (section 3.6).</summary>
    private static readonly string[] EnergisticsOverlay =
    [
        "acl", "legal", "tags_every_entity_keys", "data-partition-id", "namespace", "schema_authority", "schema_version", "authors",
        "app_name", "app_key", "wgs84_projected_epsg_code", "wgs84_vertical_epsg_code", "use_vertical_crs", "compute_spatials",
        "override_files_acl", "override_files_legals", "store_complete_geo_json", "store_complete_geo_json_wgs84", "ignore",
    ];

    public static IReadOnlyList<WorkflowContract> All { get; } =
    [
        new()
        {
            Name = OsduIngest,
            Workflows = ["Osdu_ingest"],
            Description = "Manifest ingestion: the records of an inline manifest, or of a list of manifests, written through Storage.",
            Source = $"{Workflows} section 3.2",
            TimeoutMinutes = 180,
            Keys =
            [
                .. Payload,
                new("manifest", ContextValueType.MapOrList, Required: true, $"{Workflows} section 3.2"),
                new("acl", ContextValueType.Map, Required: false, $"{Workflows} section 3.2"),
                new("legal", ContextValueType.Map, Required: false, $"{Workflows} section 3.2"),
            ],
        },
        new()
        {
            Name = OsduIngestByReference,
            Workflows = ["Osdu_ingest_by_reference"],
            Description = "Manifest ingestion by reference: the manifest stored as a dataset--File.Generic and named by its id.",
            Source = $"{Workflows} section 3.3",
            TimeoutMinutes = 60,
            Keys =
            [
                .. Payload,
                new("manifest", ContextValueType.Text, Required: true, $"{Workflows} section 3.3", Pattern: ManifestDatasetIdPattern),
                new("acl", ContextValueType.Map, Required: false, $"{Workflows} section 3.3"),
                new("legal", ContextValueType.Map, Required: false, $"{Workflows} section 3.3"),
            ],
        },
        new()
        {
            Name = ManifestIngestionTest,
            Workflows = ["manifest_ingestion"],
            Description = "The Workflow service's test DAG: one task that echoes and reports OK.",
            Source = $"{Workflows} section 3.4",
            Deliverable = false,
            NotDeliverable = "manifest_ingestion is the Workflow service's test DAG: it echoes and returns OK without creating anything, and it has no status task of its own, so it is not a delivery target",
        },
        new()
        {
            Name = CsvParser,
            Workflows = ["csv_ingestion", "csv-parser", "csv-parser-pipeline"],
            Description = "The CSV parser: every row of one CSV file becomes a record of the descriptor's TargetKind.",
            Source = $"{Workflows} section 3.5",
            Keys =
            [
                new("id", ContextValueType.Text, Required: true, $"{Workflows} section 3.5", NotEmpty: true),
                new("dataPartitionId", ContextValueType.Text, Required: true, $"{Workflows} section 3.5", NotEmpty: true),
                new("data_service_to_use", ContextValueType.Text, Required: false, $"{Workflows} section 3.5", Allowed: ["file", "dataset"]),
                new("userId", ContextValueType.Text, Required: false, $"{Workflows} section 3.5"),
                new("Payload", ContextValueType.Map, Required: false, $"{Workflows} section 3.5"),
            ],
        },
        new()
        {
            Name = EnergymlConverter,
            Workflows = ["Energyml_Converter"],
            Description = "Energistics translation: EPC or XML parts and HDF5 files translated into a manifest dataset; it ingests nothing.",
            Source = $"{Workflows} section 3.6",
            TimeoutMinutes = 1440,
            TranslatesOnly = true,
            Keys =
            [
                .. Payload,
                new("dataset_xml", ContextValueType.TextOrList, Required: true, $"{Workflows} section 3.6", NotEmpty: true),
                new("dataset_h5", ContextValueType.TextOrList, Required: true, $"{Workflows} section 3.6"),
                new("data_partition_id", ContextValueType.Text, Required: false, $"{Workflows} section 3.6"),
                .. EnergisticsOverlay.Select(k => new ContextKey(k, ContextValueType.Any, Required: false, $"{Workflows} section 3.6")),
            ],
        },
        new()
        {
            Name = EnergymlDelivery,
            Workflows = ["Energyml_Delivery"],
            Description = "Energistics delivery: an EPC and HDF5 pair exported from records already in Storage.",
            Source = $"{Workflows} section 3.7",
            TimeoutMinutes = 60,
            Keys =
            [
                .. Payload,
                new("ids", ContextValueType.Any, Required: true, $"{Workflows} section 3.7"),
                new("name", ContextValueType.Text, Required: false, $"{Workflows} section 3.7"),
                .. EnergisticsOverlay.Select(k => new ContextKey(k, ContextValueType.Any, Required: false, $"{Workflows} section 3.7")),
            ],
        },
        new()
        {
            Name = EnyparserTranslation,
            Workflows = ["Enyparser_Translation"],
            Description = "enyparser translation: RESQML, WITSML, PRODML or EML translated into a manifest dataset; it ingests nothing.",
            Source = $"{Workflows} section 3.8",
            TranslatesOnly = true,
            Keys =
            [
                .. Payload,
                new("source_dataset_ids", ContextValueType.TextOrList, Required: false, $"{Workflows} section 3.8"),
                new("sourceDatasetIds", ContextValueType.TextOrList, Required: false, $"{Workflows} section 3.8"),
                new("epc_dataset_id", ContextValueType.TextOrList, Required: false, $"{Workflows} section 3.8"),
                new("epcDatasetId", ContextValueType.TextOrList, Required: false, $"{Workflows} section 3.8"),
                new("h5_dataset_id", ContextValueType.TextOrList, Required: false, $"{Workflows} section 3.8"),
                new("h5DatasetId", ContextValueType.TextOrList, Required: false, $"{Workflows} section 3.8"),
                new("hdf5_dataset_id", ContextValueType.TextOrList, Required: false, $"{Workflows} section 3.8"),
                new("manifest_dataset_id", ContextValueType.Text, Required: false, $"{Workflows} section 3.8"),
                new("manifestDatasetId", ContextValueType.Text, Required: false, $"{Workflows} section 3.8"),
                new("work_product_name", ContextValueType.Text, Required: false, $"{Workflows} section 3.8"),
                new("workProductName", ContextValueType.Text, Required: false, $"{Workflows} section 3.8"),
                new("enyparserConfig", ContextValueType.Map, Required: false, $"{Workflows} section 3.8"),
                new("enyparser_config", ContextValueType.Map, Required: false, $"{Workflows} section 3.8"),
            ],
            AtLeastOne = [["source_dataset_ids", "sourceDatasetIds", "epc_dataset_id", "epcDatasetId"]],
        },
        new()
        {
            Name = SegyToVds,
            Workflows = ["Segy_to_vds_conversion_sdms", "segy-to-vds-conversion", "openvds_import"],
            Description = "SEG-Y to OpenVDS: a SEG-Y file in Seismic DMS converted, and a metadata manifest ingested.",
            Source = $"{Workflows} section 3.9",
            TimeoutMinutes = 1440,
            Keys =
            [
                .. Payload,
                new("file_record_id", ContextValueType.Text, Required: true, $"{Workflows} section 3.9", NotEmpty: true),
                new("work_product_id", ContextValueType.Text, Required: true, $"{Workflows} section 3.9", NotEmpty: true),
                new("id_token", ContextValueType.Text, Required: false, $"{Workflows} section 3.9", Secret: true),
                new("segyimport_arguments", ContextValueType.List, Required: false, $"{Workflows} section 3.9"),
            ],
        },
        new()
        {
            Name = SegyToZgy,
            Workflows = ["Segy_to_zgy_conversion", "segy-to-zgy-conversion", "sgy-to-zgy", "sgy-to-zgy-pipeline"],
            Description = "SEG-Y to ZGY: a SEG-Y file in Seismic DMS converted, its SeismicTraceData given an OpenZGY artefact.",
            Source = $"{Workflows} section 3.10",
            TimeoutMinutes = 1440,
            Keys =
            [
                .. Payload,
                new("data_partition_id", ContextValueType.Text, Required: true, $"{Workflows} section 3.10", NotEmpty: true),
                new("filecollection_segy_id", ContextValueType.Text, Required: true, $"{Workflows} section 3.10", Pattern: LatestReferencePattern),
                new("work_product_id", ContextValueType.Text, Required: true, $"{Workflows} section 3.10", Pattern: LatestReferencePattern),
                new("sd_svc_api_key", ContextValueType.Text, Required: true, $"{Workflows} section 3.10", Secret: true),
                new("storage_svc_api_key", ContextValueType.Text, Required: true, $"{Workflows} section 3.10", Secret: true),
                new("id_token", ContextValueType.Text, Required: false, $"{Workflows} section 3.10", Secret: true),
                new("access_token", ContextValueType.Text, Required: false, $"{Workflows} section 3.10", Secret: true),
            ],
        },
        new()
        {
            Name = SegyToMdio,
            Workflows = ["segy_to_mdio_conversion"],
            Description = "SEG-Y to MDIO: a SEG-Y file in Seismic DMS converted to an MDIO dataset, and its manifest ingested.",
            Source = $"{Workflows} section 3.11",
            Keys =
            [
                .. Payload,
                new("work_product_id", ContextValueType.Text, Required: true, $"{Workflows} section 3.11", NotEmpty: true),
                new("filecollection_segy_id", ContextValueType.Text, Required: true, $"{Workflows} section 3.11", NotEmpty: true),
                new("mdio_sd_path", ContextValueType.Text, Required: true, $"{Workflows} section 3.11", NotEmpty: true),
                new("client_id", ContextValueType.Text, Required: false, $"{Workflows} section 3.11", Secret: true),
                new("client_secret", ContextValueType.Text, Required: false, $"{Workflows} section 3.11", Secret: true),
                new("refresh_token", ContextValueType.Text, Required: false, $"{Workflows} section 3.11", Secret: true),
                new("refresh_url", ContextValueType.Text, Required: false, $"{Workflows} section 3.11", Secret: true),
                new("lossless", ContextValueType.Flag, Required: false, $"{Workflows} section 3.11"),
                new("compression_tolerance", ContextValueType.Number, Required: false, $"{Workflows} section 3.11"),
                new("grid_overrides", ContextValueType.Map, Required: false, $"{Workflows} section 3.11"),
                new("chunksize", ContextValueType.List, Required: false, $"{Workflows} section 3.11"),
            ],
            AllOrNone = [["client_id", "client_secret", "refresh_token", "refresh_url"]],
        },
        new()
        {
            Name = EdsIngest,
            Workflows = ["eds_ingest", "Eds_ingest"],
            Description = "External Data Services fetch and ingest: one connected source data job run now.",
            Source = $"{Eds} sections 2.2, 4.3 and 5.5",
            AddsPayload = false,
            Keys =
            [
                new("connectedSourceDataJobId", ContextValueType.Text, Required: true, $"{Eds} section 5.5 (the key is inferred there)", NotEmpty: true),
            ],
        },
        new()
        {
            Name = EdsScheduler,
            Workflows = ["eds_scheduler"],
            Description = "External Data Services scheduler: every active connected source data job of the partition run now.",
            Source = $"{Eds} sections 2.2, 4.3 and 5.5",
            AddsPayload = false,
        },
        new()
        {
            Name = EdsNaturalization,
            Workflows = ["eds_naturalization"],
            Description = "External Data Services naturalization: the datasets of the listed work product components copied into the partition.",
            Source = $"{Eds} sections 4.3 and 5.5",
            AddsPayload = false,
            Keys =
            [
                new("items", ContextValueType.List, Required: true, $"{Eds} section 5.5", NotEmpty: true),
            ],
        },
    ];

    /// <summary>The contract a flow names, or the one whose workflow names include <paramref name="workflow"/>; null when neither is known.</summary>
    public static WorkflowContract? Find(string workflow, string? contract)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflow);
        if (contract is not null)
        {
            return All.FirstOrDefault(c => string.Equals(c.Name, contract, StringComparison.OrdinalIgnoreCase));
        }

        return All.FirstOrDefault(c => c.Workflows.Contains(workflow, StringComparer.Ordinal));
    }

    /// <summary>True when <paramref name="name"/> could be a workflow name: letters, digits, '_', '-' and '.', at most 128 characters.</summary>
    public static bool IsWorkflowName(string name) => !string.IsNullOrEmpty(name) && name.Length <= 128 && WorkflowName().IsMatch(name);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_\-\.]*$", RegexOptions.CultureInvariant)]
    private static partial Regex WorkflowName();
}
