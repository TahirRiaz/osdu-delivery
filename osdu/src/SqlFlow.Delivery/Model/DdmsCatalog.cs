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
/// data beside its records. Only a bulk collection takes bulk writes and sessions, keeps a bulk link on its records and
/// purges on DELETE.
/// </summary>
/// <param name="EntityType">The entity type of the records it serves (<c>work-product-component--WellLog</c>).</param>
/// <param name="Segment">The path segment it is served under (<c>welllogs</c>); never derived from the entity type.</param>
/// <param name="Bulk">Whether it stores bulk data for its records.</param>
public sealed record DdmsCollectionEntry(string EntityType, string Segment, bool Bulk)
{
    /// <summary>What the record's bulk data columns are checked against before a bulk write.</summary>
    public DdmsBulkColumns Columns { get; init; } = DdmsBulkColumns.Unchecked;
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
/// PPFGDataset under <c>ppfgdataset</c>.
/// </summary>
public static partial class DdmsCatalog
{
    /// <summary>The name the Wellbore DDMS goes by when a flow names no DDMS of its own.</summary>
    public const string WellboreDdmsName = "wellbore";

    /// <summary>The path every Wellbore DDMS v3 collection is served under, below the DDMS root.</summary>
    public const string WellboreDdmsV3Prefix = "/ddms/v3/";

    /// <summary>The Wellbore DDMS's unauthenticated service description, below the DDMS root (<c>GET /about</c>).</summary>
    public const string WellboreDdmsAboutPath = "/about";

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

    /// <summary>The Wellbore DDMS under <paramref name="root"/>, with the collections its contract serves.</summary>
    public static DdmsService WellboreDdms(string? root) => new(WellboreDdmsName, root, DdmsShape.WellboreDdmsV3, WellboreDdmsCollections);

    /// <summary>The collections a DDMS of <paramref name="shape"/> serves when a flow declares none of its own.</summary>
    public static IReadOnlyList<DdmsCollectionEntry> DefaultCollections(DdmsShape shape) => shape switch
    {
        DdmsShape.WellboreDdmsV3 => WellboreDdmsCollections,
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "not a DDMS shape"),
    };

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
