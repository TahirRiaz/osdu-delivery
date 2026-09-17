using System.Text.RegularExpressions;

namespace SqlFlow.Delivery.Model;

/// <summary>A call pattern a DDMS serves its records and their bulk data by (docs/interfaces-design.md section 5.4).</summary>
public enum DdmsShape
{
    /// <summary>
    /// The Wellbore DDMS v3 (osdu/specs/wellbore-ddms/INTEGRATION.md): <c>POST /ddms/v3/{collection}</c> with an array of
    /// records; on a bulk collection the bulk data under <c>/{id}/data</c> or in a session under <c>/{id}/sessions</c>;
    /// <c>GET</c> and <c>DELETE</c> on <c>/{id}</c>, with <c>purge</c> on a bulk collection only.
    /// </summary>
    WellboreDdmsV3,

    /// <summary>
    /// The Well Delivery DDMS (osdu/specs/well-delivery-ddms/INTEGRATION.md): <c>PUT /storage/v1/{type}</c> with one entity
    /// per request under a version the writer chooses; <c>GET</c> and <c>DELETE</c> on <c>/storage/v1/{type}/{entityId}</c>,
    /// the purge on <c>:purge</c>. It keeps records alone, and indexes the references that name a version.
    /// </summary>
    WellDeliveryV1,

    /// <summary>
    /// The Rock and Fluid Sample DDMS v2 (osdu/specs/rafs-ddms/INTEGRATION.md): <c>POST /v2/{collection}</c> with an array
    /// of records; a content collection takes each content table in one request under <c>/{id}/data</c>, or
    /// <c>/{id}/data/{contentType}</c> where it holds several types, with the content schema version in the query;
    /// <c>GET</c> and a logical <c>DELETE</c> on <c>/{id}</c>.
    /// </summary>
    RafsV2,
}

/// <summary>The cloud a DDMS deployment runs on, where the API cannot tell and its behaviour depends on it.</summary>
public enum DdmsProvider
{
    Azure,
    Aws,
    Gc,
    Anthos,
    Ibm,
}

/// <summary>
/// What a DDMS checks the columns of a record's bulk data against when the bulk is written or a session commits
/// (osdu/specs/wellbore-ddms/INTEGRATION.md section 4.3). A column labelled <c>NAME[...]</c> is one column of the array
/// curve <c>NAME</c>.
/// </summary>
public enum DdmsBulkColumns
{
    /// <summary>Nothing the engine checks before sending.</summary>
    Unchecked,

    /// <summary>Every column belongs to a <c>data.Curves[].CurveID</c> of the record, and every curve carries one.</summary>
    CurveIds,

    /// <summary>
    /// As <see cref="CurveIds"/>, and a curve the bulk carries has as many columns as its <c>NumberOfColumns</c>
    /// (1 when absent).
    /// </summary>
    CurveIdsAndWidths,

    /// <summary>Every column is a <c>data.AvailableTrajectoryStationProperties[].Name</c> of the record.</summary>
    TrajectoryStations,
}

/// <summary>
/// One collection of a DDMS: the entity type it serves, the path segment it is served under, and whether it holds bulk
/// data beside its records. Only a bulk collection takes bulk writes, keeps a link to them on its records and, on the
/// Wellbore DDMS, takes sessions and purges on DELETE.
/// </summary>
/// <param name="EntityType">The entity type of the records it serves (<c>work-product-component--WellLog</c>).</param>
/// <param name="Segment">The path segment it is served under (<c>welllogs</c>); never derived from the entity type.</param>
/// <param name="Bulk">Whether it stores bulk data for its records.</param>
public sealed record DdmsCollectionEntry(string EntityType, string Segment, bool Bulk)
{
    /// <summary>What the record's bulk data columns are checked against before a bulk write.</summary>
    public DdmsBulkColumns Columns { get; init; } = DdmsBulkColumns.Unchecked;

    /// <summary>
    /// A RAFS content collection holding several content types, each written under its own path segment
    /// (<c>/{id}/data/{contentType}</c>); a collection that holds one type is written under <c>/{id}/data</c>, and its
    /// content type is its segment.
    /// </summary>
    public bool TypedContent { get; init; }
}

/// <summary>
/// What the Well Delivery DDMS's API cannot tell about a deployment and the route depends on
/// (osdu/specs/well-delivery-ddms/INTEGRATION.md sections 4, 6 and 8).
/// </summary>
public sealed record WellDeliverySettings
{
    public const int DefaultConcurrency = 1;

    public const int MaxConcurrency = 16;

    /// <summary>
    /// Whether the deployment copies every entity into Storage (<c>app.entity.storage</c>, on in every provider's chart).
    /// The copy is a record of its own that the DDMS never deletes, so a removal takes it too.
    /// </summary>
    public bool Mirror { get; init; } = true;

    /// <summary>
    /// The provider the deployment runs on, when the flow says. IBM's store refuses a second write of the same version,
    /// so an entity is never written again in place there.
    /// </summary>
    public DdmsProvider? Provider { get; init; }

    /// <summary>
    /// Writes one process sends to this DDMS at a time. The Mongo and Cosmos stores keep the collection of the current
    /// write in shared state, so writes of different types at once can land in each other's collection; one is safe.
    /// </summary>
    public int Concurrency { get; init; } = DefaultConcurrency;
}

/// <summary>
/// A DDMS a flow delivers to: the name the flow gives it, where it is under the flow's endpoint (null when the endpoint
/// is the DDMS itself, or an absolute URL when the Register service places it on another host), its call pattern and
/// the collections it serves.
/// </summary>
public sealed record DdmsService(string Name, string? Root, DdmsShape Shape, IReadOnlyList<DdmsCollectionEntry> Collections)
{
    /// <summary>
    /// The id the DDMS is registered under in the Register service, when the flow has it looked up there: what the
    /// flow does not declare (its root, its collections) comes from the registration.
    /// </summary>
    public string? Registration { get; init; }

    /// <summary>Whether the registration has been read, so the root and collections are what the DDMS serves.</summary>
    public bool Discovered { get; init; }

    /// <summary>Whether the flow listed the collections itself, rather than taking the ones its shape serves.</summary>
    public bool DeclaresCollections { get; init; }

    /// <summary>The Well Delivery DDMS's deployment settings; null for every other shape.</summary>
    public WellDeliverySettings? WellDelivery { get; init; }

    /// <summary>Whether the registration still has to be read before the DDMS's root and collections are known.</summary>
    public bool AwaitsDiscovery => Registration is not null && !Discovered;

    /// <summary>
    /// The collection serving <paramref name="entityType"/>, or null when this DDMS serves none. A collection a
    /// registration names by the type alone (<c>wellbore</c>) serves the entity type of that name in any group.
    /// </summary>
    public DdmsCollectionEntry? CollectionFor(string entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        var exact = Collections.FirstOrDefault(c => string.Equals(c.EntityType, entityType, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var separator = entityType.IndexOf("--", StringComparison.Ordinal);
        var type = separator < 0 ? entityType : entityType[(separator + 2)..];
        return Collections.FirstOrDefault(c => !c.EntityType.Contains("--", StringComparison.Ordinal) && string.Equals(c.EntityType, type, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// The DDMSs OSDU Delivery knows the collections of without a flow declaring them. The collection a record goes to is
/// always stated, never derived from its entity type: the Wellbore DDMS serves WellLog under <c>welllogs</c> and
/// PPFGDataset under <c>ppfgdataset</c>. The one exception is the Well Delivery DDMS, whose write path takes any type and
/// is the type itself, lowercased (osdu/specs/well-delivery-ddms/INTEGRATION.md section 4).
/// </summary>
public static partial class DdmsCatalog
{
    /// <summary>The name the Wellbore DDMS goes by when a flow names no DDMS of its own.</summary>
    public const string WellboreDdmsName = "wellbore";

    /// <summary>The path every Wellbore DDMS v3 collection is served under, below the DDMS root.</summary>
    public const string WellboreDdmsV3Prefix = "/ddms/v3/";

    /// <summary>The Wellbore DDMS's unauthenticated service description, below the DDMS root (<c>GET /about</c>).</summary>
    public const string WellboreDdmsAboutPath = "/about";

    /// <summary>The path every Well Delivery DDMS entity type is written and read under, below the DDMS root.</summary>
    public const string WellDeliveryPrefix = "/storage/v1/";

    /// <summary>The Well Delivery DDMS's version information, below the DDMS root (in the service's code, not its contract).</summary>
    public const string WellDeliveryInfoPath = "/info";

    /// <summary>The path every RAFS v2 collection is served under, below the DDMS root.</summary>
    public const string RafsV2Prefix = "/v2/";

    /// <summary>The RAFS DDMS's application information, below the DDMS root (<c>GET /info</c>, no token).</summary>
    public const string RafsInfoPath = "/info";

    /// <summary>The RAFS DDMS's type catalogue of SamplesAnalysis content, which checks the token and partition (<c>GET /v2/samplesanalysis/analysistypes</c>).</summary>
    public const string RafsAnalysisTypesPath = "/v2/samplesanalysis/analysistypes";

    /// <summary>
    /// The Wellbore DDMS v3's collections, as its pinned contract serves them (osdu/specs/wellbore-ddms/openapi.json and
    /// INTEGRATION.md sections 2 and 4.3): four bulk collections, whose DELETE takes <c>purge</c>, and five that hold
    /// records alone. The contract states the entity type of each collection by its <c>record_id</c> pattern, except for
    /// <c>ppfgdataset</c> and <c>wellpressuretestrawmeasurement</c>, whose patterns the service enforces in code
    /// (<c>app/model/osdu_record_id.py</c>). The column rules are the service's consistency checks
    /// (<c>app/consistency/*_consistency.py</c>): WellLog and WellPressureTestRawMeasurement compare a curve's columns
    /// with its <c>NumberOfColumns</c>, PPFGDataset does not. A test checks every row against the pinned contract.
    /// </summary>
    public static IReadOnlyList<DdmsCollectionEntry> WellboreDdmsCollections { get; } =
    [
        new("work-product-component--WellLog", "welllogs", Bulk: true) { Columns = DdmsBulkColumns.CurveIdsAndWidths },
        new("work-product-component--WellboreTrajectory", "wellboretrajectories", Bulk: true) { Columns = DdmsBulkColumns.TrajectoryStations },
        new("work-product-component--PPFGDataset", "ppfgdataset", Bulk: true) { Columns = DdmsBulkColumns.CurveIds },
        new("work-product-component--WellPressureTestRawMeasurement", "wellpressuretestrawmeasurement", Bulk: true) { Columns = DdmsBulkColumns.CurveIdsAndWidths },
        new("master-data--Well", "wells", Bulk: false),
        new("master-data--Wellbore", "wellbores", Bulk: false),
        new("work-product-component--WellboreMarkerSet", "wellboremarkersets", Bulk: false),
        new("work-product-component--WellboreIntervalSet", "wellboreintervalsets", Bulk: false),
        new("master-data--WellLogAcquisition", "welllogacquisition", Bulk: false),
    ];

    /// <summary>
    /// The entity types the Well Delivery DDMS knows (osdu/specs/well-delivery-ddms/INTEGRATION.md section 4, from the
    /// service's <c>ENTITY_TYPE</c>, its query routes and the types its examples write), each in the group the OSDU data
    /// definitions give it (project 91, <c>E-R</c> at <c>99f8fc88d8ad838b5738ac5ad92ac643538b5766</c>). The service keys an
    /// entity by its type and entity id alone, so TubularAssembly and TubularComponent, which the data definitions define
    /// in both groups, are served in both. The segment is always the type, lowercased; the service's write path takes any
    /// type, so a flow lists the ones it needs beyond these.
    /// </summary>
    public static IReadOnlyList<DdmsCollectionEntry> WellDeliveryCollections { get; } =
    [
        .. new[]
        {
            "master-data--Well",
            "master-data--WellPlanningWell",
            "master-data--WellPlanningWellbore",
            "master-data--Wellbore",
            "master-data--WellActivityProgram",
            "master-data--ActivityPlan",
            "master-data--WellboreArchitecture",
            "work-product-component--WellboreTrajectory",
            "master-data--HoleSection",
            "master-data--BHARun",
            "master-data--TubularAssembly",
            "work-product-component--TubularAssembly",
            "master-data--TubularComponent",
            "work-product-component--TubularComponent",
            "master-data--OperationsReport",
            "master-data--FluidsReport",
            "master-data--FluidsProgram",
            "master-data--CasingDesign",
            "master-data--EvaluationPlan",
            "master-data--PlannedCementJob",
            "master-data--WellBarrierElementTest",
            "work-product-component--WellLog",
            "master-data--GeometricTargetSet",
            "master-data--Risk",
            "master-data--SurveyProgram",
            "work-product-component--PPFGDataset",
            "work-product-component--PlannedLithology",
            "work-product-component--WellboreMarkerSet",
            "master-data--Rig",
        }.Select(WellDeliveryCollection),
    ];

    /// <summary>
    /// The RAFS v2 collections and the entity types each accepts, as the pinned contract's paths and <c>record_id</c>
    /// patterns serve them (osdu/specs/rafs-ddms/INTEGRATION.md section 2.1): six master data types under
    /// <c>masterdata</c> and the samples analyses report hold records alone; SamplesAnalysis and FluidModel hold several
    /// content types each; SaturationFunctionSet, ReservoirSimulationRockPhysicsModel and DepthShift one each, named after
    /// the collection. A test checks every row against the pinned contract.
    /// </summary>
    public static IReadOnlyList<DdmsCollectionEntry> RafsCollections { get; } =
    [
        new("master-data--GenericFacility", "masterdata", Bulk: false),
        new("master-data--GenericSite", "masterdata", Bulk: false),
        new("master-data--Sample", "masterdata", Bulk: false),
        new("master-data--SampleAcquisitionJob", "masterdata", Bulk: false),
        new("master-data--SampleChainOfCustodyEvent", "masterdata", Bulk: false),
        new("master-data--SampleContainer", "masterdata", Bulk: false),
        new("work-product-component--SamplesAnalysesReport", "samplesanalysesreport", Bulk: false),
        new("work-product-component--SamplesAnalysis", "samplesanalysis", Bulk: true) { TypedContent = true },
        new("work-product-component--SaturationFunctionSet", "saturationfunctionset", Bulk: true),
        new("work-product-component--ReservoirSimulationRockPhysicsModel", "reservoirsimulationrockphysicsmodel", Bulk: true),
        new("work-product-component--FluidModel", "fluidmodel", Bulk: true) { TypedContent = true },
        new("work-product-component--DepthShift", "depthshift", Bulk: true),
    ];

    /// <summary>The Wellbore DDMS under <paramref name="root"/>, with the collections its contract serves.</summary>
    public static DdmsService WellboreDdms(string? root) => new(WellboreDdmsName, root, DdmsShape.WellboreDdmsV3, WellboreDdmsCollections);

    /// <summary>The collections a DDMS of <paramref name="shape"/> serves when a flow declares none of its own.</summary>
    public static IReadOnlyList<DdmsCollectionEntry> DefaultCollections(DdmsShape shape) => shape switch
    {
        DdmsShape.WellboreDdmsV3 => WellboreDdmsCollections,
        DdmsShape.WellDeliveryV1 => WellDeliveryCollections,
        DdmsShape.RafsV2 => RafsCollections,
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "not a DDMS shape"),
    };

    /// <summary>Where a DDMS of <paramref name="shape"/> is deployed under the platform, as the service's charts route it.</summary>
    public static string UsualRoot(DdmsShape shape) => shape switch
    {
        DdmsShape.WellboreDdmsV3 => "/api/os-wellbore-ddms",
        DdmsShape.WellDeliveryV1 => "/api/well-delivery",
        DdmsShape.RafsV2 => "/api/rafs-ddms",
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "not a DDMS shape"),
    };

    /// <summary>The name a flow document gives <paramref name="shape"/> (<c>wellboreDdmsV3</c>).</summary>
    public static string ShapeName(DdmsShape shape)
    {
        var name = shape.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>
    /// The service descriptions a probe of <paramref name="service"/> asks, in order, below the endpoint. The Wellbore DDMS
    /// answers <c>/about</c>, the Well Delivery DDMS <c>/info</c>, and RAFS <c>/info</c> without a token, then its type
    /// catalogue, which checks the token and the partition.
    /// </summary>
    public static IReadOnlyList<string> ProbePaths(DdmsService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        var root = service.Root ?? string.Empty;
        return service.Shape switch
        {
            DdmsShape.WellboreDdmsV3 => [root + WellboreDdmsAboutPath],
            DdmsShape.WellDeliveryV1 => [root + WellDeliveryInfoPath],
            DdmsShape.RafsV2 => [root + RafsInfoPath, root + RafsAnalysisTypesPath],
            _ => throw new ArgumentOutOfRangeException(nameof(service), service.Shape, "not a DDMS shape"),
        };
    }

    /// <summary>
    /// The Well Delivery collection of <paramref name="entityType"/>: the type after <c>--</c>, lowercased, which is the
    /// path segment the service takes the type from (osdu/specs/well-delivery-ddms/INTEGRATION.md section 2, step 4).
    /// </summary>
    public static DdmsCollectionEntry WellDeliveryCollection(string entityType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        return new DdmsCollectionEntry(entityType, WellDeliveryType(entityType), Bulk: false);
    }

    /// <summary>The Well Delivery DDMS's type of an entity type: the part after <c>--</c>, lowercased.</summary>
    public static string WellDeliveryType(string entityType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        var separator = entityType.IndexOf("--", StringComparison.Ordinal);
        return (separator < 0 ? entityType : entityType[(separator + 2)..]).ToLowerInvariant();
    }

    /// <summary>True for a name a flow may give a DDMS: a letter followed by letters, digits, '_' and '-', at most 64 characters.</summary>
    public static bool IsName(string name) => NamePattern().IsMatch(name);

    /// <summary>True for an OSDU entity type with its group (<c>work-product-component--WellLog</c>).</summary>
    public static bool IsEntityType(string entityType) => EntityTypePattern().IsMatch(entityType);

    /// <summary>True for a collection path segment: letters, digits, '.', '_' and '-', at most 100 characters.</summary>
    public static bool IsSegment(string segment) => SegmentPattern().IsMatch(segment);

    /// <summary>True for an id the Register service keeps a DDMS under (openapi register v1, Ddms.id: <c>^[A-Za-z0-9-]{2,50}</c>).</summary>
    public static bool IsRegistration(string id) => RegistrationPattern().IsMatch(id);

    /// <summary>The collection of <paramref name="shape"/> served under <paramref name="segment"/>, or null when the shape knows none.</summary>
    public static DdmsCollectionEntry? KnownCollection(DdmsShape shape, string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return DefaultCollections(shape).FirstOrDefault(c => string.Equals(c.Segment, segment, StringComparison.Ordinal));
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_-]{0,63}\z")]
    private static partial Regex NamePattern();

    // The group and type parts of an OSDU record id's middle segment (^[\w\-\.]+:<group>\-\-<Type>:..., the Wellbore
    // DDMS's record_id patterns), joined by the double hyphen.
    [GeneratedRegex(@"^[\w.-]+--[\w.-]+\z")]
    private static partial Regex EntityTypePattern();

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,100}\z")]
    private static partial Regex SegmentPattern();

    [GeneratedRegex(@"^[A-Za-z0-9-]{2,50}\z")]
    private static partial Regex RegistrationPattern();
}
