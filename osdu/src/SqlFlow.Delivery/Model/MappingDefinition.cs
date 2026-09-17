using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Model;

/// <summary>
/// A mapping (docs/delivery/mapping-templates.md): which saved template version it fills, what identifies a record of
/// the incoming dataset, and one entry per template variable it fills, each saying where the value comes from.
/// </summary>
public sealed record MappingDefinition
{
    public const string DocumentTypeName = "mapping";

    public string? SourcePath { get; init; }

    public required string Name { get; init; }

    /// <summary>Semantic version of the mapping. Part of the render context and so of every record's render.</summary>
    public required string Version { get; init; }

    /// <summary>The template version the mapping fills.</summary>
    public required TemplateReference Template { get; init; }

    public string? Description { get; init; }

    public required MappingDataset Dataset { get; init; }

    /// <summary>Parameters the mapping accepts from the flow under <c>render.parameters</c>.</summary>
    public IReadOnlyDictionary<string, MappingParameter> Parameters { get; init; } = new Dictionary<string, MappingParameter>(StringComparer.Ordinal);

    /// <summary>The entries, in the order the document lists them.</summary>
    public required IReadOnlyList<MappingEntry> Entries { get; init; }

    /// <summary>The access list and legal block, as the static entries for them declare them (parameter tokens unexpanded).</summary>
    public required MappingEnvelope Envelope { get; init; }

    /// <summary>Example rows and the exact record each must render to.</summary>
    public IReadOnlyList<MappingFixture> Fixtures { get; init; } = [];

    public string Reference => Name + "@" + Version;

    /// <summary>The OSDU kind of the records the mapping renders.</summary>
    public string Kind => Template.Kind;

    public string EntityType => Identity.TargetId.EntityTypeFromKind(Kind);

    /// <summary>The child datasets the mapping's repeaters read, in the order they are first named.</summary>
    public IReadOnlyList<string> ChildDatasets => Entries
        .Where(e => e.IsRepeater)
        .Select(e => e.Source!.Child!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>The entries written into each item of a repeater's array.</summary>
    public IEnumerable<MappingEntry> ItemEntries(MappingEntry repeater)
    {
        ArgumentNullException.ThrowIfNull(repeater);
        return Entries.Where(e => repeater.Target.Equals(e.Target.Repeater));
    }
}

/// <summary>
/// The four envelope values OSDU requires on every record, which a mapping gives as static lists. A mapping of a DSPDM
/// business object's rows (<see cref="DspdmKinds"/>) has none: its rows are no OSDU records.
/// </summary>
public sealed record MappingEnvelope(
    IReadOnlyList<string> Owners, IReadOnlyList<string> Viewers, IReadOnlyList<string> LegalTags, IReadOnlyList<string> OtherRelevantDataCountries);

/// <summary>What identifies a record of the incoming dataset.</summary>
public sealed record MappingDataset
{
    /// <summary>The source system, entering the delivery key.</summary>
    public required string System { get; init; }

    /// <summary>The columns of the dataset's row the delivery key is derived from, in order.</summary>
    public required IReadOnlyList<string> Key { get; init; }

    /// <summary>Display text with <c>{dataset.column}</c> tokens, for the ledger and the GUI; never part of the record.</summary>
    public string? Label { get; init; }
}

public sealed record MappingParameter
{
    public bool Required { get; init; }

    public string? Default { get; init; }

    public string? Description { get; init; }
}

/// <summary>A column of the dataset's row (<c>dataset.log_name</c>) or of a child dataset's row (<c>dataset.curves.curve_id</c>).</summary>
public sealed record DatasetColumn(string? Child, string Column)
{
    public const string Prefix = "dataset";

    public override string ToString() => Child is null ? $"{Prefix}.{Column}" : $"{Prefix}.{Child}.{Column}";
}

public enum MappingSourceKind
{
    /// <summary>A column of the dataset's row or of a child dataset's row.</summary>
    DatasetColumn,

    /// <summary>The rows of a child dataset, one array item each: a repeater.</summary>
    DatasetRows,

    /// <summary>A field of a cached record.</summary>
    Cache,
}

/// <summary>Where an entry's value comes from.</summary>
public sealed record MappingSource
{
    public const string CachePrefix = "cache";

    public required MappingSourceKind Kind { get; init; }

    /// <summary>The column a dataset column source reads.</summary>
    public DatasetColumn? Column { get; init; }

    /// <summary>The child dataset a repeater reads.</summary>
    public string? Child { get; init; }

    /// <summary>The cached type a cache source reads (<c>UnitOfMeasure</c>).</summary>
    public string? CacheType { get; init; }

    /// <summary>The cached field a cache source reads: <c>id</c> for the record id, or a field or path into one.</summary>
    public string? CacheField { get; init; }

    public override string ToString() => Kind switch
    {
        MappingSourceKind.DatasetColumn => Column!.ToString(),
        MappingSourceKind.DatasetRows => $"{DatasetColumn.Prefix}.{Child}",
        _ => $"{CachePrefix}.{CacheType}.{CacheField}",
    };

    /// <summary>True when a cache source reads the record id, which renders in the reference form OSDU relationships use.</summary>
    public bool ReadsRecordId => Kind == MappingSourceKind.Cache && string.Equals(CacheField, "id", StringComparison.Ordinal);
}

/// <summary>One line of a cache source's <c>findBy</c>: the cached field compared, and the dataset value or literal it must equal.</summary>
public sealed record FindBy(string Type, string Field, DatasetColumn? Column, string? Literal)
{
    public override string ToString()
        => $"{MappingSource.CachePrefix}.{Type}.{Field} = {(Column is not null ? Column.ToString() : "'" + Literal + "'")}";
}

public enum ConditionOperator
{
    Is,
    IsNot,
    IsEmpty,
    IsNotEmpty,
}

/// <summary>An entry's <c>appliesWhen</c>: a dataset value compared with text, or tested for emptiness.</summary>
public sealed record EntryCondition(DatasetColumn Column, ConditionOperator Operator, string? Text)
{
    public override string ToString() => Operator switch
    {
        ConditionOperator.Is => $"{Column} is {Text}",
        ConditionOperator.IsNot => $"{Column} is not {Text}",
        ConditionOperator.IsEmpty => $"{Column} is empty",
        _ => $"{Column} is not empty",
    };
}

public enum ModifierKind
{
    Trim,
    Upper,
    Lower,
    Split,
    Replace,
    Equals,
    Date,
    Number,
}

/// <summary>One change to an incoming dataset value.</summary>
public sealed record Modifier
{
    public required ModifierKind Kind { get; init; }

    /// <summary>For split: the separator; a single space splits on any run of whitespace.</summary>
    public string? Separator { get; init; }

    /// <summary>For split: which part to keep, counting from one.</summary>
    public int? Part { get; init; }

    /// <summary>For replace: incoming values and what each becomes; values it does not list pass unchanged.</summary>
    public IReadOnlyDictionary<string, string> Replacements { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>For equals: the text the value is compared with; for date: the .NET format the value is written in, or null for an ISO 8601 date or date-time.</summary>
    public string? Text { get; init; }

    /// <summary>For number: the separator before the decimals, '.' or ','.</summary>
    public string? DecimalSeparator { get; init; }

    /// <summary>For number: the separator between groups of three digits, or null when the value is written without one.</summary>
    public string? GroupSeparator { get; init; }

    public override string ToString() => Kind switch
    {
        ModifierKind.Split => $"split(separator '{Separator}', part {Part})",
        ModifierKind.Replace => "replace(" + string.Join(", ", Replacements.Select(kv => kv.Key + ": " + kv.Value)) + ")",
        ModifierKind.Equals => $"equals({Text})",
        ModifierKind.Date => Text is null ? "date" : $"date({Text})",
        ModifierKind.Number when GroupSeparator is null && DecimalSeparator is null or "." => "number",
        ModifierKind.Number => $"number(decimal '{DecimalSeparator ?? "."}'" + (GroupSeparator is null ? string.Empty : $", group '{GroupSeparator}'") + ")",
        _ => Kind.ToString().ToLowerInvariant(),
    };
}

/// <summary>One mapping entry: a template variable and where its value comes from.</summary>
public sealed partial record MappingEntry
{
    /// <summary>The entry's position under <c>mappings</c>, for messages.</summary>
    public required int Index { get; init; }

    public required TemplatePath Target { get; init; }

    /// <summary>The source, or null for a static entry.</summary>
    public MappingSource? Source { get; init; }

    /// <summary>The static value, or null for an entry with a source.</summary>
    public JsonNode? Static { get; init; }

    public IReadOnlyList<FindBy> FindBy { get; init; } = [];

    public IReadOnlyList<Modifier> Modifiers { get; init; } = [];

    public EntryCondition? AppliesWhen { get; init; }

    /// <summary>What an empty value does: true holds the record, false leaves the variable out.</summary>
    public bool Required { get; init; } = true;

    /// <summary>For a cache source: a last matching attempt with punctuation and spacing folded away, for names.</summary>
    public bool IgnoreSeparators { get; init; }

    public string? Description { get; init; }

    public bool IsStatic => Source is null;

    public bool IsRepeater => Source?.Kind == MappingSourceKind.DatasetRows;

    /// <summary>How messages name the entry.</summary>
    public string Where => $"mappings[{Index}] ({Target.Text})";

    /// <summary>Every dataset column the entry reads: its source, its findBy values and its condition.</summary>
    public IEnumerable<DatasetColumn> Columns
    {
        get
        {
            if (Source?.Column is { } column)
            {
                yield return column;
            }

            foreach (var find in FindBy)
            {
                if (find.Column is { } findColumn)
                {
                    yield return findColumn;
                }
            }

            if (AppliesWhen is { } condition)
            {
                yield return condition.Column;
            }
        }
    }

    /// <summary>A <c>{param.name}</c> token inside a static text.</summary>
    public const string ParameterTokenPattern = @"\{param\.(?<name>[A-Za-z0-9_]+)\}";

    /// <summary>Replaces <c>{param.name}</c> tokens in a static text; a parameter without a value leaves its token, which the preflight reports.</summary>
    public static string ExpandParameters(string text, Func<string, string?> parameter)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(parameter);
        return ParameterToken().Replace(text, m => parameter(m.Groups["name"].Value) ?? m.Value);
    }

    /// <summary>The parameter names a static text's tokens name.</summary>
    public static IEnumerable<string> ParameterNames(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return ParameterToken().Matches(text).Select(m => m.Groups["name"].Value);
    }

    [GeneratedRegex(ParameterTokenPattern)]
    private static partial Regex ParameterToken();
}

public sealed record MappingFixture
{
    public required string Name { get; init; }

    /// <summary>The dataset's row.</summary>
    public required IReadOnlyDictionary<string, string?> Record { get; init; }

    /// <summary>Child dataset rows by child dataset name.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>> Datasets { get; init; }
        = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>>(StringComparer.Ordinal);

    /// <summary>Parameter values for the fixture render.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The expected record (JSON text, compared canonically).</summary>
    public required string Expected { get; init; }
}
