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

    /// <summary>
    /// What each <c>search.&lt;name&gt;</c> source searches, by the name entries write it as. Empty for a mapping that
    /// resolves everything out of the cache.
    /// </summary>
    public IReadOnlyDictionary<string, MappingSearch> Searches { get; init; } = new Dictionary<string, MappingSearch>(StringComparer.Ordinal);

    /// <summary>The entries, in the order the document lists them.</summary>
    public required IReadOnlyList<MappingEntry> Entries { get; init; }

    /// <summary>
    /// Every type of the partition's cache the mapping reads, in name order: those its cache sources read and their findBy
    /// lines compare, and those a replace reads its table from, whatever the entry's source. A mapping that reads none of
    /// them renders against no cache at all; one that reads any renders against a version, whose label enters its records'
    /// render context. The render, lineage and the intake all ask this one question, so none of them misses a type.
    /// </summary>
    public IReadOnlyList<string> CacheTypesRead()
    {
        var types = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries)
        {
            if (entry.Source is { Kind: MappingSourceKind.Cache, CacheType: { } type })
            {
                types.Add(type);
                foreach (var find in entry.FindBy)
                {
                    types.Add(find.Type);
                }
            }

            foreach (var modifier in entry.Modifiers)
            {
                if (modifier.Table is { } table)
                {
                    types.Add(table.CacheType);
                }
            }
        }

        return types.ToList();
    }

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

    /// <summary>
    /// The dataset's own columns whose values identify the record to a person (a wellbore id, a well name, a survey
    /// name), without the <c>dataset.</c> prefix. The ledger indexes each value so the record is found by it across
    /// every flow; search only, never part of the record.
    /// </summary>
    public IReadOnlyList<string> Identity { get; init; } = [];
}

public sealed record MappingParameter
{
    public bool Required { get; init; }

    public string? Default { get; init; }

    public string? Description { get; init; }
}

/// <summary>A column of the dataset's row (<c>dataset.log_source</c>) or of a child dataset's row (<c>dataset.curves.curve_id</c>).</summary>
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

    /// <summary>
    /// A field of a record the platform is searched for as the run needs it, rather than one captured into the
    /// partition's cache. For a set that is business data rather than a vocabulary: a partition's wellbores grow
    /// without bound and change constantly, so capturing them to answer one lookup costs more every day, while the
    /// units and the type codes a cache is for are closed sets that a capture holds cheaply.
    /// </summary>
    Search,
}

/// <summary>Where an entry's value comes from.</summary>
public sealed record MappingSource
{
    public const string CachePrefix = "cache";

    /// <summary>The prefix of a source resolved by searching the platform: <c>search.Wellbore.id</c>.</summary>
    public const string SearchPrefix = "search";

    public required MappingSourceKind Kind { get; init; }

    /// <summary>The column a dataset column source reads.</summary>
    public DatasetColumn? Column { get; init; }

    /// <summary>The child dataset a repeater reads.</summary>
    public string? Child { get; init; }

    /// <summary>The cached type a cache source reads (<c>UnitOfMeasure</c>).</summary>
    public string? CacheType { get; init; }

    /// <summary>The cached field a cache source reads: <c>id</c> for the record id, or a field or path into one.</summary>
    public string? CacheField { get; init; }

    /// <summary>The prefix this source is written with: <c>cache</c> or <c>search</c>.</summary>
    public string Prefix => Kind == MappingSourceKind.Search ? SearchPrefix : CachePrefix;

    public override string ToString() => Kind switch
    {
        MappingSourceKind.DatasetColumn => Column!.ToString(),
        MappingSourceKind.DatasetRows => $"{DatasetColumn.Prefix}.{Child}",
        _ => $"{Prefix}.{CacheType}.{CacheField}",
    };

    /// <summary>True when a resolved source reads the record id, which renders in the reference form OSDU relationships use.</summary>
    public bool ReadsRecordId
        => Kind is MappingSourceKind.Cache or MappingSourceKind.Search && string.Equals(CacheField, "id", StringComparison.Ordinal);

    /// <summary>True when this source is resolved by matching a record, whether from the cache or from a search.</summary>
    public bool Resolves => Kind is MappingSourceKind.Cache or MappingSourceKind.Search;
}

/// <summary>
/// One line of a cache or a search source's <c>findBy</c>: the field compared, and the dataset value or literal it must
/// equal. <see cref="Type"/> is the cached type, or the search, the line compares a record of.
/// </summary>
public sealed record FindBy(string Type, string Field, DatasetColumn? Column, string? Literal)
{
    /// <summary>The prefix the line is written with: <c>cache</c>, or <c>search</c> for a line of a search source.</summary>
    public string Prefix { get; init; } = MappingSource.CachePrefix;

    public override string ToString()
        => $"{Prefix}.{Type}.{Field} = {(Column is not null ? Column.ToString() : "'" + Literal + "'")}";
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

    /// <summary>
    /// For replace: incoming values and what each becomes. A null value becomes no value, so the entry's required flag
    /// decides; what a value the table does not list becomes is <see cref="Otherwise"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Replacements { get; init; } = new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>For replace: what a value the table does not list becomes; by default it passes on unchanged.</summary>
    public ReplaceFallback Otherwise { get; init; } = ReplaceFallback.Keep;

    /// <summary>
    /// For replace: the cached type the table is read from, in place of <see cref="Replacements"/>: a value is matched on
    /// one of its fields and replaced by another of the matched row. Null for a table written in the mapping.
    /// </summary>
    public CachedReplaceTable? Table { get; init; }

    /// <summary>For equals: the text the value is compared with; for date: the .NET format the value is written in, or null for an ISO 8601 date or date-time.</summary>
    public string? Text { get; init; }

    /// <summary>For number: the separator before the decimals, '.' or ','.</summary>
    public string? DecimalSeparator { get; init; }

    /// <summary>For number: the separator between groups of three digits, or null when the value is written without one.</summary>
    public string? GroupSeparator { get; init; }

    public override string ToString() => Kind switch
    {
        ModifierKind.Split => $"split(separator '{Separator}', part {Part})",
        ModifierKind.Replace when Table is { } table => $"replace from {table}"
            + (Otherwise.Kind == ReplaceFallbackKind.Keep ? string.Empty : $", otherwise {Otherwise}"),
        ModifierKind.Replace => "replace(" + string.Join(", ", Replacements.Select(kv => kv.Key + ": " + (kv.Value ?? "~")))
            + (Otherwise.Kind == ReplaceFallbackKind.Keep ? string.Empty : $"; otherwise {Otherwise}") + ")",
        ModifierKind.Equals => $"equals({Text})",
        ModifierKind.Date => Text is null ? "date" : $"date({Text})",
        ModifierKind.Number when GroupSeparator is null && DecimalSeparator is null or "." => "number",
        ModifierKind.Number => $"number(decimal '{DecimalSeparator ?? "."}'" + (GroupSeparator is null ? string.Empty : $", group '{GroupSeparator}'") + ")",
        _ => Kind.ToString().ToLowerInvariant(),
    };
}

/// <summary>
/// A replace reading its table from the partition's cache (<c>replace: cache.CurveClasses</c>): the value is matched on
/// <see cref="Match"/> and replaced by the matched row's <see cref="Field"/>. Either may be left for the cached type to
/// decide: a lookup table matches on its key, and replaces by the one field it holds beside it (a dictionary of pairs'
/// value).
/// </summary>
/// <param name="CacheType">The cached type the table is read from.</param>
/// <param name="Match">The field a value is matched on, or null for the type's key.</param>
/// <param name="Field">The field of the matched row a value is replaced by, or null for the one field beside the key.</param>
public sealed record CachedReplaceTable(string CacheType, string? Match, string? Field)
{
    /// <summary>The table as the mapping names it, with the fields it names: <c>cache.CurveClasses (mnemonic to curve_family)</c>.</summary>
    public override string ToString()
        => $"{MappingSource.CachePrefix}.{CacheType}" + (Match is null && Field is null ? string.Empty : $" ({Match ?? "its key"} to {Field ?? "its value"})");
}

/// <summary>What a replace does with a value its table does not list.</summary>
public enum ReplaceFallbackKind
{
    /// <summary>The value passes on unchanged, trimmed.</summary>
    Keep,

    /// <summary>The value becomes no value, so the entry's required flag decides what that does.</summary>
    Empty,

    /// <summary>The value becomes a fixed text.</summary>
    Text,
}

/// <summary>
/// A replace's <c>otherwise</c>: what a value its table does not list becomes. It applies only to a value that is there and
/// unlisted; an empty incoming value stays empty, and a listed value that becomes no value is not "unlisted".
/// </summary>
public sealed record ReplaceFallback(ReplaceFallbackKind Kind, string? Text = null)
{
    /// <summary>An unlisted value passes on unchanged: what a replace without <c>otherwise</c> does.</summary>
    public static ReplaceFallback Keep { get; } = new(ReplaceFallbackKind.Keep);

    /// <summary>An unlisted value becomes no value (<c>otherwise: ~</c>).</summary>
    public static ReplaceFallback Empty { get; } = new(ReplaceFallbackKind.Empty);

    /// <summary>An unlisted value becomes <paramref name="text"/>; text that is empty or blank is no value.</summary>
    public static ReplaceFallback Of(string? text)
        => string.IsNullOrWhiteSpace(text) ? Empty : new ReplaceFallback(ReplaceFallbackKind.Text, text);

    public override string ToString() => Kind switch
    {
        ReplaceFallbackKind.Keep => "keep",
        ReplaceFallbackKind.Empty => "~",
        _ => Text ?? string.Empty,
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

    /// <summary>
    /// What the fixture assumes the platform answers to each search its render asks, so a fixture checks the mapping and
    /// never the platform's data of the day. A render asking a question the fixture does not answer fails the fixture.
    /// </summary>
    public IReadOnlyList<FixtureSearchAnswer> Searches { get; init; } = [];

    /// <summary>The expected record (JSON text, compared canonically).</summary>
    public required string Expected { get; init; }
}

/// <summary>What a fixture assumes the platform answers when a search compares one property with one value.</summary>
/// <param name="Search">The search, by the name the mapping declares it under.</param>
/// <param name="Field">The property compared, as findBy writes it.</param>
/// <param name="Value">The value compared, after the entry's modifiers.</param>
/// <param name="Id">The one record found, or null when the platform holds no such record.</param>
public sealed record FixtureSearchAnswer(string Search, string Field, string Value, string? Id);

/// <summary>
/// One record set a mapping resolves by searching the platform. The kind is what a search is issued against; the name
/// is what entries write, so a mapping can search two sets of the same kind under different names.
/// </summary>
/// <param name="Name">The name entries write, as in <c>search.Wellbore.id</c>.</param>
/// <param name="Kind">
/// The OSDU kind searched: one entity type, at one version or at every version (<c>osdu:wks:master-data--Wellbore:*</c>).
/// </param>
/// <param name="Schema">
/// The saved template whose schema says how the searched kind's properties are indexed, pinned the way a mapping pins its
/// own template, so the query a render sends is fixed by the mapping's version and not by whatever the platform says on
/// the day. It is a version of the same entity type as <paramref name="Kind"/>.
/// </param>
/// <param name="Description">What the set is, shown wherever the mapping is listed.</param>
public sealed record MappingSearch(string Name, string Kind, TemplateReference Schema, string? Description);

/// <summary>
/// One question a render needs answered before it can finish: the query to run against a kind, with the property and
/// value it compares for anyone reading the answer. Two renders asking the same question are the same question, so a run
/// answers it once.
/// </summary>
/// <param name="Kind">The OSDU kind searched.</param>
/// <param name="Field">The property compared, as the mapping writes it.</param>
/// <param name="Value">What that property must equal, after the entry's modifiers have run.</param>
/// <param name="Query">The query sent, built from the property as the searched kind's schema says it is indexed.</param>
public sealed record SearchQuestion(string Kind, string Field, string Value, string Query);
