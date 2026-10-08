using System.Text.Json;
using System.Text.Json.Serialization;
using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.SearchTerms;

/// <summary>
/// What identifies a search term (osdu/docs/search-terms.md): a column of a source system, read by a mapping that fills an
/// entity type, in the dataset's own row or in a child dataset's rows. <c>recall</c>'s <c>wellbore_uwi</c> on
/// <c>work-product-component--WellLog</c> is one term whichever mapping version reads it, and whichever repository holds the
/// mapping, so what a person makes of it (a name, leaving it out) is kept by this identity across syncs and versions.
/// </summary>
/// <param name="System">The source system (<c>dataset.system</c>), in lower case as the delivery key folds it.</param>
/// <param name="EntityType">The entity type the mapping fills (<c>work-product-component--WellLog</c>).</param>
/// <param name="Dataset">The child dataset whose rows hold the column (<c>curves</c>); null for the dataset's own row.</param>
/// <param name="Column">The column as the mapping names it.</param>
public sealed record SearchTermKey(string System, string EntityType, string? Dataset, string Column)
{
    /// <summary>The longest key text: the system, the entity type, the dataset and the column, each well inside its own bound.</summary>
    public const int MaxTextLength = 600;

    private static readonly Guid IdNamespace = DeterministicGuid.Namespace("search-term");

    /// <summary>
    /// The key as text, <c>recall/work-product-component--WellLog/curves/curve_unit</c>: the dataset left empty for the
    /// dataset's own row, and the column in lower case, since a table's columns are named regardless of case.
    /// </summary>
    public string Text => $"{System}/{EntityType}/{Dataset ?? string.Empty}/{Column.ToLowerInvariant()}";

    /// <summary>The term's identity, derived from <see cref="Text"/>, so every host and every sync names the term alike.</summary>
    public Guid Id => DeterministicGuid.V5(IdNamespace, Text);

    /// <summary>The column as a person who knows the source names it: <c>curves.curve_unit</c>, or <c>log_source</c>.</summary>
    public string ColumnLabel => Dataset is null ? Column : $"{Dataset}.{Column}";

    /// <summary>The key of the column <paramref name="column"/> of <paramref name="system"/> on <paramref name="entityType"/>.</summary>
    public static SearchTermKey Of(string system, string entityType, string? dataset, string column)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(system);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        return new SearchTermKey(system.Trim().ToLowerInvariant(), entityType.Trim(), string.IsNullOrWhiteSpace(dataset) ? null : dataset.Trim(), column.Trim());
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
/// <param name="System">The source system the delivery key is derived over.</param>
/// <param name="Columns">The key's columns, in order.</param>
/// <param name="FromKey">True when the id's last part is the key's own values (<c>idFrom: key</c>); false for the delivery key.</param>
/// <param name="Others">
/// For a key of several columns: where the record holds each of the others, by column, so their values can be read from
/// the platform and the ids of every combination made; null for a key of one column.
/// </param>
public sealed record SearchRouteKey(string System, IReadOnlyList<string> Columns, bool FromKey, IReadOnlyDictionary<string, string>? Others);

/// <summary>
/// One way a column reaches what a record holds: the variable it fills (<c>osdu.data.WellboreID</c>), the mapping node that
/// fills it, and how a value of the column becomes the value written. A column a mapping reads in several places has a
/// route for each, and a route a person picks (or the likeliest one) is the one its term is searched by.
/// </summary>
public sealed record SearchRoute
{
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

    /// <summary>The route's identity within its term: the variable and the way the value reaches it.</summary>
    [JsonIgnore]
    public string Id => $"{Target}|{Kind}";

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

/// <summary>A term as a mapping gives it: its key, and every route by which its column reaches the record.</summary>
public sealed record CompiledSearchTerm(SearchTermKey Key, IReadOnlyList<SearchRoute> Routes);

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
