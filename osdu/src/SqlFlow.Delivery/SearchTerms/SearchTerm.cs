using System.Text.Json;
using System.Text.Json.Serialization;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.SearchTerms;

/// <summary>
/// What identifies a search term (osdu/docs/reference/concepts/search-terms.md): a column of a source table a delivery
/// flow reads, as the flow names the table (its record table, or the table of a child dataset).
/// <c>OsduData.arc.WellLog</c>'s <c>wellbore_uwi</c> is one term whichever pipeline reads the table, whichever mapping
/// (and mapping version) renders it and into whichever entity type, so two flows rendering one table under two versions
/// of a schema give one term, and what a person makes of it (a name, leaving it out) is kept by this identity across
/// syncs, pipelines and versions.
/// </summary>
/// <param name="Source">The table, its parts without brackets (<c>OsduData.arc.WellLog</c>).</param>
/// <param name="Column">The column as the mapping names it.</param>
public sealed record SearchTermKey(string Source, string Column)
{
    /// <summary>The longest key text: the table and the column, each well inside its own bound.</summary>
    public const int MaxTextLength = 600;

    private static readonly Guid IdNamespace = DeterministicGuid.Namespace("search-term");

    /// <summary>
    /// The key as text, <c>osdudata.arc.welllog/wellbore_uwi</c>: the table and the column in lower case, since a database
    /// names its tables and their columns regardless of case.
    /// </summary>
    public string Text => $"{Source.ToLowerInvariant()}/{Column.ToLowerInvariant()}";

    /// <summary>The term's identity, derived from <see cref="Text"/>, so every host and every sync names the term alike.</summary>
    public Guid Id => DeterministicGuid.V5(IdNamespace, Text);

    /// <summary>The table's own name, the last part of its name: <c>WellLog</c> of <c>OsduData.arc.WellLog</c>.</summary>
    public string Table => TableOf(Source);

    /// <summary>
    /// The column as a person who knows the source names it, the table before it: <c>WellLog.wellbore_uwi</c>, or
    /// <c>WellLogCurve.curve_unit</c>. It is the term's name until a person gives it another.
    /// </summary>
    public string ColumnLabel => $"{Table}.{Column}";

    /// <summary>The key of the column <paramref name="column"/> of the table <paramref name="source"/>.</summary>
    public static SearchTermKey Of(string source, string column)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        return new SearchTermKey(NormalizeSource(source), column.Trim());
    }

    /// <summary>A table's name with its parts trimmed and their brackets or quotes taken off: <c>[OsduData].[arc].[WellLog]</c> reads <c>OsduData.arc.WellLog</c>.</summary>
    public static string NormalizeSource(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        return string.Join('.', source.Split('.').Select(part => part.Trim().Trim('[', ']', '"', '`').Trim()));
    }

    /// <summary>
    /// The parts of a key a term had while terms were keyed by their mapping's source system: <c>system/entityType/dataset/column</c>,
    /// the dataset empty for the record's own row; null for a key of today's form (<c>table/column</c>). A refinement made
    /// then is moved to the term its column is now (<c>DeliverySearchTermCatalog</c>).
    /// </summary>
    public static (string System, string EntityType, string Dataset, string Column)? Legacy(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parts = text.Split('/');
        return parts.Length == 4 && parts[1].Contains("--", StringComparison.Ordinal) && parts[3].Length > 0
            ? (parts[0], parts[1], parts[2], parts[3])
            : null;
    }

    /// <summary>The last part of a table's name: <c>WellLog</c> of <c>OsduData.arc.WellLog</c>.</summary>
    public static string TableOf(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var normalized = NormalizeSource(source);
        var dot = normalized.LastIndexOf('.');
        return dot < 0 ? normalized : normalized[(dot + 1)..];
    }
}

/// <summary>How a route carries a source value to what the record holds.</summary>
public enum SearchRouteKind
{
    /// <summary>The value is written as it stands (<c>LogRun: { $from: log_run }</c>).</summary>
    Copy,

    /// <summary>The value goes through the mapping's modifiers first (<c>trim</c>, <c>split</c>, <c>replace</c>, <c>ref</c>...).</summary>
    Steps,

    /// <summary>The value finds a record in a cached type, and the record holds what is written (<c>$lookup: wellbore.id</c>).</summary>
    Lookup,

    /// <summary>The value finds a record by searching the platform, and its id is written (<c>$search: Wellbore</c>).</summary>
    Search,

    /// <summary>The value is a part of the dataset's key, from which the record's id is made.</summary>
    Key,

    /// <summary>The value enters an expression that computes what is written, which no search can run backwards.</summary>
    Expression,
}

/// <summary>
/// One line a lookup or a search finds its record by: the property of the record compared, as the platform indexes it
/// (<c>data.FacilityName</c>), with the column compared to it.
/// </summary>
/// <param name="Field">The property of the record found, from the record root.</param>
public sealed record SearchRouteLine(string Field);

/// <summary>
/// The record set a lookup or a search finds its record in, how it finds it, and what it reads of it: the record's
/// <c>id</c> (written in the reference form OSDU relationships use, ending in a colon) or a property of it.
/// </summary>
/// <param name="Name">The cached type (<c>Wellbore</c>) or the mapping's search (<c>Wellbore</c>).</param>
/// <param name="Kind">The kind, or kind pattern, of the records searched (<c>osdu:wks:master-data--Wellbore:*</c>).</param>
/// <param name="Lines">The lines the record is found by, in the order they are tried.</param>
/// <param name="Read"><c>id</c>, or the path of the property of the record found that is written (<c>data.GeoContexts.FieldID</c>).</param>
public sealed record SearchRouteFind(string Name, string Kind, IReadOnlyList<SearchRouteLine> Lines, string Read)
{
    /// <summary>Whether the route writes the found record's id, in reference form.</summary>
    [JsonIgnore]
    public bool ReadsId => string.Equals(Read, "id", StringComparison.Ordinal);
}

/// <summary>The dataset key the record's id is made from: every column of it, in order, and how the id is made.</summary>
/// <param name="Systems">
/// The source systems the delivery key is derived over, in lower case: one per mapping that renders the table under a
/// system of its own (two versions of a schema delivered side by side), so an id is made as each of them makes it.
/// </param>
/// <param name="Columns">The key's columns, in order.</param>
/// <param name="FromKey">True when the id's last part is the key's own values (<c>idFrom: key</c>); false for the delivery key.</param>
/// <param name="Others">
/// For a key of several columns: where the record holds each of the others, by column, so their values can be read from
/// the platform and the ids of every combination made; null for a key of one column.
/// </param>
public sealed record SearchRouteKey(IReadOnlyList<string> Systems, IReadOnlyList<string> Columns, bool FromKey, IReadOnlyDictionary<string, string>? Others);

/// <summary>
/// One way a column reaches what a record holds: the variable it fills (<c>osdu.data.WellboreID</c>), the mapping node that
/// fills it, and how a value of the column becomes the value written. A column a mapping reads in several places has a
/// route for each, and a route a person picks (or the likeliest one) is the one its term is searched by.
/// </summary>
public sealed record SearchRoute
{
    /// <summary>The entity type the route fills (<c>work-product-component--WellLog</c>).</summary>
    public required string EntityType { get; init; }

    /// <summary>
    /// The kinds the route fills, as the templates of its mappings name them (<c>osdu:wks:work-product-component--WellLog:1.5.0</c>),
    /// each once, in order: what the explorer prefers the route for while one of them is in view.
    /// </summary>
    public IReadOnlyList<string> Kinds { get; init; } = [];

    /// <summary>The source systems (<c>dataset.system</c>) of its mappings, in lower case, each once.</summary>
    public IReadOnlyList<string> Systems { get; init; } = [];

    /// <summary>The child dataset the mapping reads the column under (<c>curves</c>); null for the record's own row.</summary>
    public string? Dataset { get; init; }

    /// <summary>The template variable filled (<c>osdu.data.Curves[].CurveUnit</c>), or <c>id</c> for a key.</summary>
    public required string Target { get; init; }

    /// <summary>The variable's path in the record (<c>data.Curves.CurveUnit</c>), or <c>id</c> for a key.</summary>
    public required string Path { get; init; }

    public required SearchRouteKind Kind { get; init; }

    /// <summary>The mappings (<c>WellLog@1.4.0</c>) whose node this is, each once, the newest version last.</summary>
    public required IReadOnlyList<string> Mappings { get; init; }

    /// <summary>Where the mapping writes the node (<c>record.data.Curves.$item.CurveUnit</c>), which finds it again.</summary>
    public string? Location { get; init; }

    /// <summary>For an alternative of a <c>$coalesce</c> node: its place among them, counting from one; null for a node of its own.</summary>
    public int? Alternative { get; init; }

    /// <summary>The modifiers the value goes through, as the mapping writes them (<c>replace(DEPTH: Depth)</c>, <c>ref</c>).</summary>
    public IReadOnlyList<string> Steps { get; init; } = [];

    /// <summary>
    /// What the steps keep of a value, which decides what a search may ask of it besides a whole value:
    /// <see cref="SearchRouteKeeps.Text"/> when they only trim, change case or split it, so its words and its start survive;
    /// <see cref="SearchRouteKeeps.Order"/> when they only trim or read it as a number or a date, so a range survives; and
    /// <see cref="SearchRouteKeeps.Value"/> when they make another value of it, which is compared whole.
    /// </summary>
    public SearchRouteKeeps Keeps { get; init; } = SearchRouteKeeps.Text;

    /// <summary>For a lookup or a search: the records searched, how one is found, and what is read of it.</summary>
    public SearchRouteFind? Find { get; init; }

    /// <summary>For a key: the key the id is made from.</summary>
    public SearchRouteKey? Key { get; init; }

    /// <summary>The node's <c>$when</c>, as written: a record the condition does not hold for leaves the variable out.</summary>
    public string? When { get; init; }

    /// <summary>The node's <c>$description</c>, as written.</summary>
    public string? Description { get; init; }

    /// <summary>Why no value typed can be carried by this route, in words; null when one can.</summary>
    public string? Problem { get; init; }

    /// <summary>
    /// The route's identity within its term: the entity type, the variable and the way the value reaches it; for a key, how
    /// the id is made from it too, since a key whose values are the id and one derived from them make different ids.
    /// </summary>
    [JsonIgnore]
    public string Id => Kind == SearchRouteKind.Key && Key is { FromKey: true } ? $"{EntityType}|{Target}|{Kind}|value" : $"{EntityType}|{Target}|{Kind}";

    /// <summary>Whether the route fills <paramref name="kind"/>, a kind with no wildcard.</summary>
    public bool Fills(string kind) => Kinds.Contains(kind, StringComparer.OrdinalIgnoreCase);

    /// <summary>The routes as they are kept beside their term.</summary>
    public static string ToJson(IReadOnlyList<SearchRoute> routes) => JsonSerializer.Serialize(routes, SearchTermJson.Options);

    /// <summary>The routes kept beside a term; none for text that holds none, which a sync then writes again.</summary>
    public static IReadOnlyList<SearchRoute> FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<SearchRoute>>(json, SearchTermJson.Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>What a route's steps keep of a value, which decides the conditions a search may ask of it.</summary>
public enum SearchRouteKeeps
{
    /// <summary>The value's text: its words and its start, its case aside or changed alike for every value.</summary>
    Text,

    /// <summary>The value's order: it is read as a number or a date, so a range of values is a range of what is written.</summary>
    Order,

    /// <summary>Only the value itself: what is written is another value made of it, compared whole.</summary>
    Value,
}

/// <summary>
/// A term as the pipelines give it for one entity type: its key, the entity type, and every route by which its column
/// reaches the records of that type.
/// </summary>
public sealed record CompiledSearchTerm(SearchTermKey Key, string EntityType, IReadOnlyList<SearchRoute> Routes);

/// <summary>
/// One pipeline's reading of its tables, as the search terms are compiled from it: the mapping it renders with, and the
/// tables its source reads, the record table and each child dataset's by the name the mapping reads it under.
/// </summary>
/// <param name="Mapping">The mapping the delivery flow (or its interface) renders with.</param>
/// <param name="RecordObject">The record table (<c>source.record.object</c>).</param>
/// <param name="DatasetObjects">The child datasets' tables (<c>source.datasets.{name}.object</c>), by name.</param>
public sealed record SearchTermSource(MappingDefinition Mapping, string RecordObject, IReadOnlyDictionary<string, string> DatasetObjects);

/// <summary>How search terms and their routes are written as JSON: the web's conventions, nulls left out.</summary>
public static class SearchTermJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
