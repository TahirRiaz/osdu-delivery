using System.Text.RegularExpressions;
using SqlFlow.Delivery.Search;

namespace SqlFlow.Delivery.Engine.Search;

/// <summary>
/// How a condition on a search term reaches the records of the page through other records (osdu/docs/search-terms.md):
/// the records its values find (<see cref="Kind"/>, <see cref="Match"/>, <see cref="Read"/>), as a mapping's lookup or
/// search finds the wellbore a log names, or the record ids its key makes (<see cref="Key"/>). The node reads them from
/// the platform before it asks the page, and the condition then compares the property it names with what was read.
/// </summary>
public sealed partial record ExplorerVia
{
    /// <summary>
    /// The most records a condition reads its values from, and so the most values it then compares: a value that names more
    /// says so, and is narrowed by a value naming fewer.
    /// </summary>
    public const int MaxFound = ExplorerFilter.MaxValues;

    /// <summary>The longest name of a term a condition is read through.</summary>
    public const int MaxTermLength = 200;

    /// <summary>The most properties a record is matched on, and the most columns of a key.</summary>
    public const int MaxParts = 8;

    /// <summary>The search term as a person names it, which the notes say what was read for.</summary>
    public string? Term { get; init; }

    /// <summary>The kind, or kind pattern, of the records the values find (<c>osdu:wks:master-data--Wellbore:*</c>).</summary>
    public string? Kind { get; init; }

    /// <summary>The properties a record is found by, any of them holding the value, each as the platform indexes it.</summary>
    public IReadOnlyList<ExplorerField>? Match { get; init; }

    /// <summary>What is read of each record found: its id (<c>id</c>), compared in the reference form relationships are written in, or a property of it.</summary>
    public ExplorerField? Read { get; init; }

    /// <summary>The key the record ids are made from, for a condition on a column of the dataset's key.</summary>
    public ExplorerViaKey? Key { get; init; }

    /// <summary>Whether the records found are compared by their ids.</summary>
    public bool ReadsId => Read is { Path: "id" };

    /// <summary>
    /// Checks the way <paramref name="filter"/> is read through as the node will read it, and gives the query its values find
    /// records by (none for a key).
    /// </summary>
    /// <exception cref="OsduQueryException">The way cannot be read as given; the message says why.</exception>
    internal string Check(ExplorerFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (Term is { Length: > MaxTermLength })
        {
            throw new OsduQueryException($"the term's name is {Term.Length} characters; it takes at most {MaxTermLength}.");
        }

        if (filter.Compared().Count == 0)
        {
            throw new OsduQueryException("a condition read through other records compares at least one value.");
        }

        if ((Key is null) == (Kind is null && Match is null && Read is null))
        {
            throw new OsduQueryException("a condition is read through the records its values find, or the ids its key makes, and not both.");
        }

        return Key is { } key ? CheckKey(key, filter) : Matching(filter).Text;
    }

    /// <summary>The query the values of <paramref name="filter"/> find records by: any of the properties matched holding them, as the condition asks.</summary>
    /// <exception cref="OsduQueryException">The records cannot be found as asked.</exception>
    internal OsduQuery Matching(ExplorerFilter filter)
    {
        if (Kind is null)
        {
            throw new OsduQueryException("name the kind of the records the values find.");
        }

        if (ExplorerKinds.Problem(Kind) is { } wrongKind)
        {
            throw new OsduQueryException(wrongKind);
        }

        if (Match is not { Count: > 0 and <= MaxParts } match)
        {
            throw new OsduQueryException($"the records the values find are matched on 1 to {MaxParts} properties.");
        }

        if (Read is not { } read || (!ReadsId && !OsduPath.IsPath(read.Path)))
        {
            throw new OsduQueryException("name what is read of the records found: id, or a property path.");
        }

        if (!ReadsId)
        {
            _ = OsduField.Of(read.Path, read.Index, read.Nested);
        }

        _ = OsduField.Of(filter.Path, filter.Index, filter.Nested);
        var values = filter.Compared();
        var lines = new List<OsduQuery>(match.Count);
        foreach (var property in match)
        {
            var field = OsduField.Of(property.Path, property.Index, property.Nested);
            lines.Add(filter.Condition switch
            {
                ExplorerCondition.Is or ExplorerCondition.IsNot => OsduQuery.Equal(field, values[0]),
                ExplorerCondition.AnyOf or ExplorerCondition.NoneOf => OsduQuery.AnyOf(field, Bounded(values)),
                ExplorerCondition.Contains => OsduQuery.Words(field, values[0]),
                ExplorerCondition.StartsWith => OsduQuery.Prefix(field, values[0]),
                _ => throw new OsduQueryException($"a condition read through other records is is, isNot, anyOf, noneOf, contains or startsWith, not {filter.Condition}."),
            });
        }

        return OsduQuery.Any([.. lines]);
    }

    private static string CheckKey(ExplorerViaKey key, ExplorerFilter filter)
    {
        if (filter.Condition is not (ExplorerCondition.Is or ExplorerCondition.IsNot or ExplorerCondition.AnyOf or ExplorerCondition.NoneOf))
        {
            throw new OsduQueryException($"a record id is made from whole values: is, isNot, anyOf or noneOf, not {filter.Condition}.");
        }

        if (!string.Equals(filter.Path, "id", StringComparison.Ordinal))
        {
            throw new OsduQueryException("a condition on a key compares the record's id.");
        }

        if (string.IsNullOrWhiteSpace(key.System) || key.System.Length > 100 || !EntityType().IsMatch(key.EntityType ?? string.Empty))
        {
            throw new OsduQueryException("a key names its source system and the entity type its ids are of (group--Type).");
        }

        if (key.Columns is not { Count: > 0 and <= MaxParts } columns || columns.Any(string.IsNullOrWhiteSpace)
            || !columns.Contains(key.Given, StringComparer.OrdinalIgnoreCase))
        {
            throw new OsduQueryException($"a key names 1 to {MaxParts} columns, the column given among them.");
        }

        foreach (var column in columns.Where(c => !string.Equals(c, key.Given, StringComparison.OrdinalIgnoreCase)))
        {
            var other = key.Others.FirstOrDefault(o => string.Equals(o.Column, column, StringComparison.OrdinalIgnoreCase))
                ?? throw new OsduQueryException($"the key's column {column} is not one the record holds, so its values cannot be read.");
            _ = OsduField.Of(other.Path, other.Index);
        }

        _ = Bounded(filter.Compared());
        return string.Empty;
    }

    private static IReadOnlyList<string> Bounded(IReadOnlyList<string> values)
        => values.Count > MaxFound
            ? throw new OsduQueryException($"a condition read through other records compares at most {MaxFound} values; {values.Count} were given.")
            : values;

    [GeneratedRegex(@"^[\w.-]+--[\w.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex EntityType();
}

/// <summary>
/// The dataset key a record's id is made from (<c>dataset.key</c>), for a condition on one of its columns: the source system
/// the delivery key is derived over, the entity type the ids are of, every column in order, the one the condition gives,
/// and where the record holds each of the others, whose values are read from the platform to make every id.
/// </summary>
public sealed record ExplorerViaKey
{
    public required string System { get; init; }

    public required string EntityType { get; init; }

    public required IReadOnlyList<string> Columns { get; init; }

    /// <summary>The column the condition's values are of.</summary>
    public required string Given { get; init; }

    /// <summary>True when the id's last part is the key's own values (<c>idFrom: key</c>); false for the delivery key.</summary>
    public bool FromKey { get; init; }

    /// <summary>Where the record holds each other column of the key.</summary>
    public IReadOnlyList<ExplorerViaColumn> Others { get; init; } = [];
}

/// <summary>A column of a key and where the record holds its value, as the platform indexes it.</summary>
public sealed record ExplorerViaColumn
{
    public required string Column { get; init; }

    public required string Path { get; init; }

    public OsduFieldIndex Index { get; init; } = OsduFieldIndex.Text;
}
