using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Templates;

/// <summary>Where a draft entry's value comes from, as the mapping builder edits it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MappingDraftInput>))]
public enum MappingDraftInput
{
    /// <summary>A column of the dataset's row, or of a child dataset's row inside a repeater.</summary>
    Dataset,

    /// <summary>The rows of a child dataset, one array item each.</summary>
    Repeat,

    /// <summary>A field of a cached record.</summary>
    Cache,

    /// <summary>A fixed value.</summary>
    Static,

    /// <summary>The id of a record found on the platform by searching, as the render needs it.</summary>
    Search,

    /// <summary>A value an expression computes from the row (<c>$expr</c>).</summary>
    Expression,
}

/// <summary>
/// A mapping as the builder edits it: the header, the parameters, one entry per variable it fills, and the fixtures
/// (osdu/docs/mapping-templates.md). The entries are flat, each naming its variable by target path; the document the draft
/// is written as lays them out as the record tree. Every text a person types is written in the mapping language as the
/// document reads it: a literal's <c>{$param.name}</c>, an id template's tokens and the label's <c>{column}</c>.
/// </summary>
public sealed record MappingDraft
{
    public string Name { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public string TemplateKind { get; init; } = string.Empty;

    public string TemplateVersion { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string System { get; init; } = string.Empty;

    /// <summary>The dataset columns of the key, without the <c>dataset.</c> prefix.</summary>
    public IReadOnlyList<string> Key { get; init; } = [];

    /// <summary>The label as written, with <c>{column}</c> tokens naming columns of the dataset's own row.</summary>
    public string? Label { get; init; }

    /// <summary>The dataset columns an operator finds a record by, without the <c>dataset.</c> prefix.</summary>
    public IReadOnlyList<string> Identity { get; init; } = [];

    public IReadOnlyList<MappingDraftParameter> Parameters { get; init; } = [];

    /// <summary>The record sets the mapping's search entries look in.</summary>
    public IReadOnlyList<MappingDraftSearch> Searches { get; init; } = [];

    public IReadOnlyList<MappingDraftEntry> Entries { get; init; } = [];

    /// <summary>The parameter values every fixture renders with unless it gives its own (<c>fixtureDefaults.parameters</c>).</summary>
    public IReadOnlyDictionary<string, string> FixtureParameters { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyList<MappingDraftFixture> Fixtures { get; init; } = [];
}

public sealed record MappingDraftParameter(string Name, bool Required, string? Default, string? Description);

/// <summary>One search: the name entries read it by, the kind it looks in, and the saved template whose schema says how that kind is indexed.</summary>
public sealed record MappingDraftSearch(string Name, string Kind, string SchemaKind, string SchemaVersion, string? Description);

/// <summary>What a fixture assumes the platform answers when a search compares <c>Field</c> with <c>Value</c>: the record found, or none.</summary>
public sealed record MappingDraftFixtureSearch(string Search, string Field, string Value, string? Id);

/// <summary>One entry as the builder edits it.</summary>
public sealed record MappingDraftEntry
{
    public string Target { get; init; } = string.Empty;

    public MappingDraftInput Input { get; init; }

    /// <summary>For a dataset input: <c>column</c>, or <c>child.column</c> inside a repeater.</summary>
    public string? Column { get; init; }

    /// <summary>For a repeat input: the child dataset.</summary>
    public string? Child { get; init; }

    public string? CacheType { get; init; }

    public string? CacheField { get; init; }

    public IReadOnlyList<MappingDraftFind> FindBy { get; init; } = [];

    public IReadOnlyList<MappingDraftModifier> Modifiers { get; init; } = [];

    /// <summary>For an expression input: the expression, as the record tree writes it in the entry's scope.</summary>
    public string? Expression { get; init; }

    /// <summary>The entry's condition (<c>$when</c>), as the record tree writes it in the entry's scope, or null to write it always.</summary>
    public string? When { get; init; }

    /// <summary>For a repeat input: the condition a child row must hold to become an item (<c>$where</c>), or null for every row.</summary>
    public string? Where { get; init; }

    public bool Required { get; init; } = true;

    public bool IgnoreSeparators { get; init; }

    /// <summary>For a static input: the value as JSON text.</summary>
    public string? Static { get; init; }

    public string? Description { get; init; }

    /// <summary>True when the builder proposed the entry from the cache, so the page can say so until someone edits it.</summary>
    public bool Prefilled { get; init; }
}

/// <summary>One findBy line: the cached field, and the column (<c>column</c> or <c>child.column</c>) or literal it must equal.</summary>
public sealed record MappingDraftFind(string Field, string? Column, string? Literal);

/// <summary>
/// One modifier: trim, upper, lower, split, replace, equals, date, number or id, with its settings; an id carries its template
/// in <see cref="Text"/>. A replace carries its pairs,
/// or the cached type it reads its table from in <see cref="Table"/> with the fields it matches on (<see cref="Match"/>)
/// and replaces by (<see cref="Field"/>), either left out for the table to settle; and what an unlisted value becomes:
/// <see cref="OtherwiseKind"/> is keep (the default), empty (no value) or text, with the text in <see cref="OtherwiseText"/>.
/// </summary>
public sealed record MappingDraftModifier(
    string Kind, string? Separator = null, int? Part = null, IReadOnlyList<MappingDraftReplacement>? Replacements = null, string? Text = null,
    string? DecimalSeparator = null, string? GroupSeparator = null, string? OtherwiseKind = null, string? OtherwiseText = null,
    string? Table = null, string? Match = null, string? Field = null);

/// <summary>One pair of a replace: the incoming value, and what it becomes; a null <see cref="To"/> is no value.</summary>
public sealed record MappingDraftReplacement(string From, string? To);

public sealed record MappingDraftFixture(
    string Name,
    IReadOnlyDictionary<string, string> Parameters,
    IReadOnlyDictionary<string, string?> Record,
    IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>> Datasets,
    string Expected,
    IReadOnlyList<MappingDraftFixtureSearch>? Searches = null);

/// <summary>What the builder found about a draft: an error stops the mapping from loading, a warning does not.</summary>
public sealed record MappingDraftIssue(string Severity, string Message, string? Target = null)
{
    public const string ErrorSeverity = "error";

    public const string WarningSeverity = "warning";
}

/// <summary>
/// A type a cache holds: the name mappings read it by, its entity type, the fields it captures, and for a lookup table the
/// name its key is kept under (null for a type of OSDU records).
/// </summary>
public sealed record CachedTypeInfo(string Name, string EntityType, IReadOnlyList<string> Fields, string? Key = null);

/// <summary>
/// The mapping builder's logic (docs/delivery/mapping-templates.md, The mapping builder): a draft prefilled from the
/// template and the cache, the checks that say what is still missing, the YAML the draft is written as, and the draft of
/// an existing mapping. The YAML is the one written form; what it loads to is decided by the document loader alone.
/// </summary>
public static partial class MappingBuilder
{
    /// <summary>The access and legal variables every record carries, which a mapping gives as static lists.</summary>
    public static readonly IReadOnlyList<string> EnvelopeTargets =
    [
        "osdu.acl.owners",
        "osdu.acl.viewers",
        "osdu.legal.legaltags",
        "osdu.legal.otherRelevantDataCountries",
    ];

    private static readonly HashSet<string> ReservedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "true", "false", "yes", "no", "on", "off", "y", "n", "null", "~",
    };

    /// <summary>
    /// A new draft for a template: the envelope entries to fill, and a cache entry for every variable outside a repeater
    /// that points to an entity type the cache holds, finding the record by the type's first cached field.
    /// </summary>
    public static MappingDraft Draft(OsduTemplate template, IReadOnlyList<CachedTypeInfo> cache, string name, string version, string system)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(cache);
        var entries = EnvelopeTargets.Select(target => new MappingDraftEntry { Target = target, Input = MappingDraftInput.Static, Static = "[]" }).ToList();
        foreach (var variable in template.Fillable)
        {
            if (variable.Path.IsRepeated || variable.Shape is not (TemplateVariableShape.Value or TemplateVariableShape.ValueList)
                || variable.Relationships.Count == 0 || EnvelopeTargets.Contains(variable.Path.Text, StringComparer.Ordinal))
            {
                continue;
            }

            if (CacheTypesFor(variable, cache) is not { Count: > 0 } answering)
            {
                continue;
            }

            var cached = answering[0];

            entries.Add(new MappingDraftEntry
            {
                Target = variable.Path.Text,
                Input = MappingDraftInput.Cache,
                CacheType = cached.Name,
                CacheField = "id",
                FindBy = [new MappingDraftFind(cached.Fields.Count > 0 ? cached.Fields[0] : "id", string.Empty, null)],
                Prefilled = true,
            });
        }

        return new MappingDraft
        {
            Name = name?.Trim() ?? string.Empty,
            Version = version?.Trim() ?? string.Empty,
            TemplateKind = template.Kind,
            TemplateVersion = template.Version,
            System = system?.Trim() ?? string.Empty,
            Parameters = [new MappingDraftParameter(Snapshots.RenderContext.DataPartitionParameter, true, null, "The OSDU data partition record ids and references are minted in.")],
            Entries = entries,
        };
    }

    /// <summary>The cached types whose entity type a variable's relationships allow.</summary>
    public static IReadOnlyList<CachedTypeInfo> CacheTypesFor(TemplateVariable variable, IReadOnlyList<CachedTypeInfo> cache)
    {
        ArgumentNullException.ThrowIfNull(variable);
        ArgumentNullException.ThrowIfNull(cache);
        return cache.Where(c => variable.Relationships.Any(r => string.Equals(r, c.EntityType, StringComparison.Ordinal)
                || (!r.Contains("--", StringComparison.Ordinal) && c.EntityType.StartsWith(r + "--", StringComparison.Ordinal))))
            .ToList();
    }


    /// <summary>What the draft still lacks before it can be written as a mapping that loads, entry by entry.</summary>
    public static IReadOnlyList<MappingDraftIssue> Incomplete(MappingDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var issues = new List<MappingDraftIssue>();
        void Error(string message, string? target = null) => issues.Add(new MappingDraftIssue(MappingDraftIssue.ErrorSeverity, message, target));

        if (!FileNamePart().IsMatch(draft.Name))
        {
            Error("Give the mapping a name of letters, digits, underscore, hyphen and dot; it names the file as Name@version.yaml.");
        }

        if (!FileNamePart().IsMatch(draft.Version))
        {
            Error("Give the mapping a version of letters, digits, underscore, hyphen and dot, such as 1.0.0.");
        }

        if (string.IsNullOrWhiteSpace(draft.TemplateKind) || string.IsNullOrWhiteSpace(draft.TemplateVersion))
        {
            Error("Pick the saved template the mapping fills.");
        }

        if (string.IsNullOrWhiteSpace(draft.System))
        {
            Error("Name the source system; it is part of every record's key.");
        }

        if (draft.Key.Count == 0 || draft.Key.Any(k => !ColumnName().IsMatch(k)))
        {
            Error("Name the dataset columns that identify a record, such as log_id.");
        }

        if (draft.Identity.Any(k => !ColumnName().IsMatch(k)))
        {
            Error("Name each dataset column an operator finds a record by, such as wellbore_uwi.");
        }

        if (!string.IsNullOrWhiteSpace(draft.Label))
        {
            foreach (Match token in LabelTokenPattern().Matches(draft.Label))
            {
                var column = token.Groups["token"].Value.Trim();
                if (!ColumnName().IsMatch(column) && !(column.StartsWith(DatasetReference + ".", StringComparison.Ordinal) && ColumnName().IsMatch(column[(DatasetReference.Length + 1)..])))
                {
                    Error($"The label's {token.Value} names no column; a label token is a column of the dataset's own row, such as {{wellbore_uwi}}.");
                }
            }
        }

        foreach (var search in draft.Searches)
        {
            if (!ColumnName().IsMatch(search.Name ?? string.Empty))
            {
                Error($"Name each search with letters, digits, underscores and hyphens; entries read it as {MappingMapper.SearchKey}: <name>.");
            }

            if (string.IsNullOrWhiteSpace(search.Kind))
            {
                Error($"Search '{search.Name}': give the kind it looks in, such as osdu:wks:master-data--Wellbore:*.");
            }

            if (string.IsNullOrWhiteSpace(search.SchemaKind) || string.IsNullOrWhiteSpace(search.SchemaVersion))
            {
                Error($"Search '{search.Name}': pick the saved template whose schema says how the kind it searches is indexed.");
            }
        }

        var layout = Layout(draft);
        foreach (var left in layout.LeftOut.Where(l => l.Reason is not null))
        {
            Error(left.Reason!, left.Target);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (entry, index) in draft.Entries.Select((entry, index) => (entry, index)))
        {
            var target = entry.Target.Trim();
            if (!TemplatePath.TryParse(target, out _, out var targetError))
            {
                Error(char.ToUpperInvariant(targetError![0]) + targetError[1..] + ".", target);
                continue;
            }

            if (!seen.Add(target))
            {
                Error($"{target} has more than one entry.", target);
            }

            var scope = layout.Scopes.GetValueOrDefault(index);
            switch (entry.Input)
            {
                case MappingDraftInput.Dataset when !DatasetColumnPattern().IsMatch(entry.Column ?? string.Empty):
                    Error($"{target}: choose the dataset column the value comes from.", target);
                    break;
                case MappingDraftInput.Dataset:
                    ScopeIssue(entry.Column!, scope, target, Error);
                    break;
                case MappingDraftInput.Repeat when !ColumnName().IsMatch(entry.Child ?? string.Empty):
                    Error($"{target}: choose the child dataset whose rows become the items.", target);
                    break;
                case MappingDraftInput.Repeat when layout.EmptyRepeats.Contains(index):
                    Error($"{target}: add an entry for each property an item takes from its row, such as {target}[].Name.", target);
                    break;
                case MappingDraftInput.Cache:
                    if (!ColumnName().IsMatch(entry.CacheType ?? string.Empty) || !FieldPath().IsMatch(entry.CacheField ?? string.Empty))
                    {
                        Error($"{target}: choose the cached type and the field to read.", target);
                    }

                    if (entry.FindBy.Count == 0)
                    {
                        Error($"{target}: say which cached record to read with at least one findBy line.", target);
                    }

                    foreach (var find in entry.FindBy)
                    {
                        if (!FieldPath().IsMatch(find.Field ?? string.Empty) || find.Field!.StartsWith(Marker, StringComparison.Ordinal))
                        {
                            Error($"{target}: a findBy line needs the cached field it compares.", target);
                        }
                        else if (string.IsNullOrWhiteSpace(find.Literal) && !DatasetColumnPattern().IsMatch(find.Column ?? string.Empty))
                        {
                            Error($"{target}: findBy {find.Field} needs the dataset column, or a fixed text, it must equal.", target);
                        }
                        else if (string.IsNullOrWhiteSpace(find.Literal))
                        {
                            ScopeIssue(find.Column!, scope, target, Error);
                        }
                    }

                    break;
                case MappingDraftInput.Search:
                    if (!draft.Searches.Any(s => string.Equals(s.Name, entry.CacheType, StringComparison.Ordinal)))
                    {
                        Error($"{target}: choose one of the mapping's searches to look in.", target);
                    }

                    if (!string.Equals(entry.CacheField, "id", StringComparison.Ordinal))
                    {
                        Error($"{target}: a search reads the id of the record it finds.", target);
                    }

                    if (entry.FindBy.Count == 0)
                    {
                        Error($"{target}: say which record to find with at least one findBy line.", target);
                    }

                    foreach (var find in entry.FindBy)
                    {
                        if (!(find.Field ?? string.Empty).StartsWith("data.", StringComparison.Ordinal) || !Search.OsduPath.IsPath(find.Field))
                        {
                            Error($"{target}: a findBy line compares a property under data, such as data.FacilityName.", target);
                        }
                        else if (string.IsNullOrWhiteSpace(find.Literal) && !DatasetColumnPattern().IsMatch(find.Column ?? string.Empty))
                        {
                            Error($"{target}: findBy {find.Field} needs the dataset column, or a fixed text, it must equal.", target);
                        }
                        else if (string.IsNullOrWhiteSpace(find.Literal))
                        {
                            ScopeIssue(find.Column!, scope, target, Error);
                        }
                    }

                    break;
                case MappingDraftInput.Static:
                    StaticIssue(entry, target, Error);
                    break;
                case MappingDraftInput.Expression:
                    if (string.IsNullOrWhiteSpace(entry.Expression))
                    {
                        Error($"{target}: write the expression the value is computed with, such as coalesce(log_name, log_source).", target);
                    }
                    else if (MappingMapper.ReadExpression(entry.Expression.Trim(), MappingMapper.ExprKey, scope, out var expressionProblem) is null)
                    {
                        Error($"{target}: {expressionProblem}", target);
                    }

                    break;
            }

            foreach (var modifier in entry.Modifiers)
            {
                ModifierIssue(modifier, scope, target, Error);
            }

            // id and ref both build the id an entry writes, so the same rules hold for either.
            var builders = entry.Modifiers.Where(m => m.Kind is "id" or "ref").ToList();
            if (builders.Count > 0 && entry.Input is not (MappingDraftInput.Dataset or MappingDraftInput.Expression))
            {
                Error($"{target}: the {builders[0].Kind} modifier builds the id an entry writes from a dataset value; choose a dataset column or an expression as the input.", target);
            }
            else if (builders.Count > 1)
            {
                Error($"{target}: an entry builds one id, and this one lists {string.Join(" and ", builders.Select(m => m.Kind))}; keep one.", target);
            }
            else if (builders.Count == 1 && entry.Modifiers[^1].Kind is not ("id" or "ref"))
            {
                Error($"{target}: {builders[0].Kind} builds what the entry writes, so it is the last modifier; move it to the end.", target);
            }

            // A repeat's own condition decides for the whole array, so it reads the row the array is in; its $where reads each child row.
            if (!string.IsNullOrWhiteSpace(entry.When)
                && MappingMapper.ReadCondition(entry.When.Trim(), MappingMapper.WhenKey, entry.Input == MappingDraftInput.Repeat ? null : scope, out var whenProblem) is null)
            {
                Error($"{target}: {whenProblem}", target);
            }

            if (!string.IsNullOrWhiteSpace(entry.Where))
            {
                if (entry.Input != MappingDraftInput.Repeat)
                {
                    Error($"{target}: {MappingMapper.WhereKey} keeps the child rows a repeat makes items of; only a repeat input takes it.", target);
                }
                else if (ColumnName().IsMatch(entry.Child?.Trim() ?? string.Empty)
                    && MappingMapper.ReadCondition(entry.Where.Trim(), MappingMapper.WhereKey, entry.Child!.Trim(), out var whereProblem) is null)
                {
                    Error($"{target}: {whereProblem}", target);
                }
            }
        }

        return issues;
    }

    /// <summary>
    /// The draft written as a mapping document, in the documented style: the header, then the record tree, each entry at
    /// the place its target names, then the fixtures. Incomplete values are written as they stand, so the page shows what
    /// was typed and the loader reports it; an entry that has no place in the tree is left out with a comment saying why,
    /// and <see cref="Incomplete"/> reports it.
    /// </summary>
    public static string ToYaml(MappingDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var yaml = new StringBuilder();
        void Line(string text) => yaml.Append(text).Append('\n');

        Line("documentType: mapping");
        Line("name: " + Scalar(draft.Name));
        Line("version: " + Scalar(draft.Version));
        Line("template:");
        Line("  kind: " + Scalar(draft.TemplateKind));
        Line("  version: " + Scalar(draft.TemplateVersion));
        if (!string.IsNullOrWhiteSpace(draft.Description))
        {
            Line("description: " + Scalar(draft.Description.Trim()));
        }

        Line(string.Empty);
        Line("dataset:");
        Line("  system: " + Scalar(draft.System));
        Line("  key: [" + string.Join(", ", draft.Key.Select(k => FlowScalar(k.Trim()))) + "]");
        if (!string.IsNullOrWhiteSpace(draft.Label))
        {
            Line("  label: " + Scalar(draft.Label.Trim()));
        }

        if (draft.Identity.Count > 0)
        {
            Line("  identity: [" + string.Join(", ", draft.Identity.Select(k => FlowScalar(k.Trim()))) + "]");
        }

        Line(string.Empty);
        Line("parameters:");
        foreach (var parameter in draft.Parameters)
        {
            Line("  " + Scalar(parameter.Name) + ":");
            Line("    required: " + (parameter.Required ? "true" : "false"));
            if (parameter.Default is not null)
            {
                Line("    default: " + Scalar(parameter.Default));
            }

            if (!string.IsNullOrWhiteSpace(parameter.Description))
            {
                Line("    description: " + Scalar(parameter.Description.Trim()));
            }
        }

        if (draft.Searches.Count > 0)
        {
            Line(string.Empty);
            Line("searches:");
            foreach (var search in draft.Searches)
            {
                Line("  " + Scalar(search.Name) + ":");
                Line("    kind: " + Scalar(search.Kind));
                Line("    schema:");
                Line("      kind: " + Scalar(search.SchemaKind));
                Line("      version: " + Scalar(search.SchemaVersion));
                if (!string.IsNullOrWhiteSpace(search.Description))
                {
                    Line("    description: " + Scalar(search.Description.Trim()));
                }
            }
        }

        Line(string.Empty);
        Line("record:");
        var layout = Layout(draft);
        foreach (var left in layout.LeftOut)
        {
            Line($"  # Left out: {Comment(left.Reason ?? $"{left.Target} has another entry, which is written")}.");
        }

        WriteProperties(layout.Root, 2, null, Line);

        if (draft.FixtureParameters.Count > 0)
        {
            Line(string.Empty);
            Line("fixtureDefaults:");
            Line("  parameters:");
            foreach (var (name, value) in draft.FixtureParameters)
            {
                Line("    " + Scalar(name) + ": " + Scalar(value));
            }
        }

        if (draft.Fixtures.Count > 0)
        {
            Line(string.Empty);
            Line("fixtures:");
            foreach (var fixture in draft.Fixtures)
            {
                WriteFixture(fixture, Line);
            }
        }

        return yaml.ToString();
    }

    /// <summary>The draft of a loaded mapping, for opening it in the builder.</summary>
    public static MappingDraft FromDefinition(MappingDefinition mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        return new MappingDraft
        {
            Name = mapping.Name,
            Version = mapping.Version,
            TemplateKind = mapping.Template.Kind,
            TemplateVersion = mapping.Template.Version,
            Description = mapping.Description,
            System = mapping.Dataset.System,
            Key = mapping.Dataset.Key,
            Label = mapping.Dataset.Label,
            Identity = mapping.Dataset.Identity,
            Parameters = mapping.Parameters.Select(kv => new MappingDraftParameter(kv.Key, kv.Value.Required, kv.Value.Default, kv.Value.Description)).ToList(),
            Searches = mapping.Searches.Values
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .Select(s => new MappingDraftSearch(s.Name, s.Kind, s.Schema.Kind, s.Schema.Version, s.Description))
                .ToList(),
            Entries = mapping.Entries.Select(Draft).ToList(),
            FixtureParameters = mapping.FixtureParameters,
            Fixtures = mapping.Fixtures.Select(f => new MappingDraftFixture(
                f.Name,
                OwnParameters(f.Parameters, mapping.FixtureParameters),
                f.Record,
                f.Datasets,
                f.Expected,
                f.Searches.Count == 0 ? null : f.Searches.Select(a => new MappingDraftFixtureSearch(a.Search, a.Field, a.Value, a.Id)).ToList())).ToList(),
        };
    }

    /// <summary>The parameters a fixture gives beyond the defaults: those the defaults do not hold, or hold another value for.</summary>
    private static IReadOnlyDictionary<string, string> OwnParameters(IReadOnlyDictionary<string, string> parameters, IReadOnlyDictionary<string, string> defaults)
        => parameters
            .Where(kv => !defaults.TryGetValue(kv.Key, out var shared) || !string.Equals(shared, kv.Value, StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    private static MappingDraftEntry Draft(MappingEntry entry)
    {
        var source = entry.Source;
        return new MappingDraftEntry
        {
            Target = entry.Target.Text,
            Input = source is null
                ? MappingDraftInput.Static
                : source.Kind switch
                {
                    MappingSourceKind.DatasetColumn => MappingDraftInput.Dataset,
                    MappingSourceKind.DatasetRows => MappingDraftInput.Repeat,
                    MappingSourceKind.Search => MappingDraftInput.Search,
                    MappingSourceKind.Expression => MappingDraftInput.Expression,
                    _ => MappingDraftInput.Cache,
                },
            Expression = source?.Expression?.Text,
            Column = source?.Column is { } column ? ColumnText(column) : null,
            Child = source?.Child,
            CacheType = source?.CacheType,
            CacheField = source?.CacheField,
            FindBy = entry.FindBy.Select(f => new MappingDraftFind(f.Field, f.Column is null ? null : ColumnText(f.Column), f.Literal)).ToList(),
            Modifiers = entry.Modifiers.Select(m => new MappingDraftModifier(
                m.Kind.ToString().ToLowerInvariant(),
                m.Separator,
                m.Part,
                m.Replacements.Count == 0 ? null : m.Replacements.Select(kv => new MappingDraftReplacement(kv.Key, kv.Value)).ToList(),
                m.Kind switch
                {
                    ModifierKind.Id => m.Id!.Text,
                    ModifierKind.Ref => m.EntityType,
                    _ => m.Text,
                },
                m.DecimalSeparator,
                m.GroupSeparator,
                m.Kind == ModifierKind.Replace ? FallbackKind(m.Otherwise) : null,
                m.Kind == ModifierKind.Replace && m.Otherwise.Kind == ReplaceFallbackKind.Text ? m.Otherwise.Text : null,
                m.Table?.CacheType,
                m.Table?.Match,
                m.Table?.Field)).ToList(),
            When = entry.AppliesWhen?.Text,
            Where = entry.RowFilter?.Text,
            Required = entry.Required,
            IgnoreSeparators = entry.IgnoreSeparators,
            Static = entry.Static?.ToJsonString(),
            Description = entry.Description,
        };
    }

    /// <summary>One property of the record tree the draft is written as: an object of properties, or an entry, which for a repeat holds the item's properties too.</summary>
    private sealed class TreeSlot
    {
        public MappingDraftEntry? Entry { get; init; }

        public List<(string Name, TreeSlot Slot)>? Properties { get; init; }
    }

    /// <summary>An entry the tree has no place for, with why: null when it only repeats a target another entry already fills.</summary>
    private sealed record LeftOutEntry(string Target, string? Reason);

    /// <summary>
    /// The draft's entries laid out as the record tree. <see cref="Scopes"/> gives, by entry index, the child dataset whose
    /// rows an entry inside a repeated array reads, and <see cref="EmptyRepeats"/> the repeats in the tree whose items hold
    /// no entry yet.
    /// </summary>
    private sealed record DraftLayout(
        List<(string Name, TreeSlot Slot)> Root, IReadOnlyList<LeftOutEntry> LeftOut, IReadOnlyDictionary<int, string?> Scopes, IReadOnlySet<int> EmptyRepeats);

    /// <summary>
    /// Places every entry of the draft at the property its target names. The entries outside repeated arrays go first, in
    /// draft order, so a repeat is in place before the entries of its items; each of those then goes under the repeat of its
    /// array. An entry that names a property inside one another entry fills whole, or that fills whole a property other
    /// entries fill inside, has no place, and neither has an entry of an array's items that nothing repeats.
    /// </summary>
    private static DraftLayout Layout(MappingDraft draft)
    {
        var root = new List<(string Name, TreeSlot Slot)>();
        var leftOut = new List<LeftOutEntry>();
        var scopes = new Dictionary<int, string?>();
        var repeatSlots = new Dictionary<TreeSlot, int>();
        var parsed = draft.Entries
            .Select((entry, index) => (Entry: entry, Index: index, Path: TemplatePath.TryParse(entry.Target, out var path, out _) ? path : null))
            .ToList();

        foreach (var (entry, index, path) in parsed.Where(p => p.Path is { IsRepeated: false }))
        {
            if (Place(root, path!.Segments.Select(s => s.Name).ToList(), entry, path.Text, leftOut) is { } slot && entry.Input == MappingDraftInput.Repeat)
            {
                repeatSlots[slot] = index;
            }
        }

        foreach (var (entry, index, path) in parsed.Where(p => p.Path is { IsRepeated: true }))
        {
            var array = path!.Repeater!;
            if (Find(root, array.Segments.Select(s => s.Name).ToList()) is not { Entry.Input: MappingDraftInput.Repeat } repeat)
            {
                leftOut.Add(new LeftOutEntry(path.Text, $"{path.Text} fills a property of the items of {array.Text}, and no entry repeats a child dataset's rows at {array.Text}"));
                continue;
            }

            if (Place(repeat.Properties!, path.WithinItem, entry, path.Text, leftOut) is not null)
            {
                scopes[index] = string.IsNullOrWhiteSpace(repeat.Entry!.Child) ? null : repeat.Entry.Child.Trim();
            }
        }

        var empty = repeatSlots.Where(kv => kv.Key.Properties is { Count: 0 }).Select(kv => kv.Value).ToHashSet();
        return new DraftLayout(root, leftOut, scopes, empty);
    }

    /// <summary>Places one entry at <paramref name="names"/> below <paramref name="properties"/>, or says why it has no place there.</summary>
    private static TreeSlot? Place(List<(string Name, TreeSlot Slot)> properties, IReadOnlyList<string> names, MappingDraftEntry entry, string target, List<LeftOutEntry> leftOut)
    {
        var current = properties;
        for (var i = 0; i < names.Count - 1; i++)
        {
            var existing = current.FirstOrDefault(p => p.Name == names[i]).Slot;
            if (existing?.Entry is { } whole)
            {
                leftOut.Add(new LeftOutEntry(target, $"{target} lies inside {whole.Target.Trim()}, which an entry fills whole"));
                return null;
            }

            if (existing is null)
            {
                existing = new TreeSlot { Properties = [] };
                current.Add((names[i], existing));
            }

            current = existing.Properties!;
        }

        var leaf = current.FirstOrDefault(p => p.Name == names[^1]).Slot;
        if (leaf is not null)
        {
            leftOut.Add(leaf.Entry is null
                ? new LeftOutEntry(target, $"{target} is filled whole, and other entries fill properties inside it; fill it whole or fill its properties")
                : new LeftOutEntry(target, null));
            return null;
        }

        var slot = new TreeSlot { Entry = entry, Properties = entry.Input == MappingDraftInput.Repeat ? [] : null };
        current.Add((names[^1], slot));
        return slot;
    }

    private static TreeSlot? Find(List<(string Name, TreeSlot Slot)> properties, IReadOnlyList<string> names)
    {
        TreeSlot? slot = null;
        var current = properties;
        foreach (var name in names)
        {
            slot = current?.FirstOrDefault(p => p.Name == name).Slot;
            if (slot is null)
            {
                return null;
            }

            current = slot.Properties;
        }

        return slot;
    }

    private static void WriteProperties(List<(string Name, TreeSlot Slot)> properties, int indent, string? scope, Action<string> line)
    {
        var pad = new string(' ', indent);
        foreach (var (name, slot) in properties)
        {
            var key = pad + PropertyKey(name);
            if (slot.Entry is null)
            {
                line(key + ":");
                WriteProperties(slot.Properties!, indent + 2, scope, line);
                continue;
            }

            WriteNode(key, slot, indent, scope, line);
        }
    }

    /// <summary>A record property as a key of the tree: its name, with one more '$' when the name starts with one.</summary>
    private static string PropertyKey(string name) => Scalar(MappingMapper.KeyOfProperty(name));

    /// <summary>One entry at its property: a literal as the value it is, or a node of the mapping language with its settings.</summary>
    private static void WriteNode(string key, TreeSlot slot, int indent, string? scope, Action<string> line)
    {
        var entry = slot.Entry!;
        var inner = new string(' ', indent + 2);
        if (entry.Input == MappingDraftInput.Static)
        {
            WriteLiteral(key, entry, indent, scope, line);
            return;
        }

        if (entry.Input == MappingDraftInput.Repeat)
        {
            line(key + ":");
            line(inner + MappingMapper.ForEachKey + ": " + Scalar(entry.Child?.Trim() ?? string.Empty));
            if (!string.IsNullOrWhiteSpace(entry.Where))
            {
                line(inner + MappingMapper.WhereKey + ": " + ExpressionScalar(entry.Where.Trim()));
            }

            WriteSettings(entry, inner, line);
            if (slot.Properties is { Count: > 0 } items)
            {
                line(inner + MappingMapper.ItemKey + ":");
                WriteProperties(items, indent + 4, string.IsNullOrWhiteSpace(entry.Child) ? null : entry.Child.Trim(), line);
            }
            else
            {
                line(inner + MappingMapper.ItemKey + ": {}");
            }

            return;
        }

        if (entry.Input == MappingDraftInput.Expression)
        {
            // Always the block form: an expression reads best on a line of its own, and a flow mapping would need it quoted.
            line(key + ":");
            line(inner + MappingMapper.ExprKey + ": " + ExpressionScalar(entry.Expression?.Trim() ?? string.Empty));
            WriteModifiers(entry, inner, line);
            WriteSettings(entry, inner, line);
            return;
        }

        var (sourceKey, read) = entry.Input switch
        {
            MappingDraftInput.Dataset => (MappingMapper.FromKey, ScopedColumn(entry.Column ?? string.Empty, scope)),
            MappingDraftInput.Search => (MappingMapper.SearchKey, entry.CacheType?.Trim() ?? string.Empty),
            _ => (MappingMapper.CacheKey, $"{entry.CacheType?.Trim()}.{entry.CacheField?.Trim()}"),
        };

        var bare = entry.FindBy.Count == 0 && entry.Modifiers.Count == 0 && string.IsNullOrWhiteSpace(entry.When) && entry.Required
            && !(entry.Input == MappingDraftInput.Cache && entry.IgnoreSeparators) && string.IsNullOrWhiteSpace(entry.Description);
        if (bare)
        {
            line(key + ": { " + sourceKey + ": " + FlowScalar(read) + " }");
            return;
        }

        line(key + ":");
        line(inner + sourceKey + ": " + Scalar(read));
        if (entry.Input is MappingDraftInput.Cache or MappingDraftInput.Search)
        {
            var lines = entry.FindBy.Select(f => FindByText(f, scope)).ToList();
            if (lines.Count == 1)
            {
                line(inner + MappingMapper.FindByKey + ": " + Scalar(lines[0]));
            }
            else if (lines.Count > 1)
            {
                line(inner + MappingMapper.FindByKey + ":");
                foreach (var text in lines)
                {
                    line(inner + "  - " + Scalar(text));
                }
            }
        }

        WriteModifiers(entry, inner, line);
        WriteSettings(entry, inner, line);
    }

    private static void WriteModifiers(MappingDraftEntry entry, string inner, Action<string> line)
    {
        if (entry.Modifiers.Count == 0)
        {
            return;
        }

        line(inner + MappingMapper.ModifiersKey + ":");
        foreach (var modifier in entry.Modifiers)
        {
            var lines = ModifierLines(modifier);
            line(inner + "  - " + lines[0]);
            foreach (var setting in lines.Skip(1))
            {
                line(inner + "    " + setting);
            }
        }
    }

    /// <summary>The settings a node writes after its value: its condition, whether it may be left out, and its description.</summary>
    private static void WriteSettings(MappingDraftEntry entry, string inner, Action<string> line)
    {
        if (!string.IsNullOrWhiteSpace(entry.When))
        {
            line(inner + MappingMapper.WhenKey + ": " + ExpressionScalar(entry.When.Trim()));
        }

        if (entry.Input != MappingDraftInput.Static && !entry.Required)
        {
            line(inner + MappingMapper.RequiredKey + ": false");
        }

        if (entry.Input == MappingDraftInput.Cache && entry.IgnoreSeparators)
        {
            line(inner + MappingMapper.IgnoreSeparatorsKey + ": true");
        }

        if (!string.IsNullOrWhiteSpace(entry.Description))
        {
            line(inner + MappingMapper.DescriptionKey + ": " + Scalar(entry.Description.Trim()));
        }
    }

    /// <summary>
    /// A literal: a text, number, boolean or list as the value it is, and an object, or a literal with a condition or a
    /// description, under <c>$value</c>, which holds it verbatim and keeps it one entry.
    /// </summary>
    private static void WriteLiteral(string key, MappingDraftEntry entry, int indent, string? scope, Action<string> line)
    {
        JsonNode? node;
        try
        {
            node = string.IsNullOrWhiteSpace(entry.Static) ? null : JsonNode.Parse(entry.Static);
        }
        catch (JsonException)
        {
            // A value that is not JSON is written as the text it is, so the page shows what was typed and the loader reports it.
            node = JsonValue.Create(entry.Static ?? string.Empty);
        }

        var inner = new string(' ', indent + 2);
        var settled = string.IsNullOrWhiteSpace(entry.When) && string.IsNullOrWhiteSpace(entry.Description);
        switch (node)
        {
            case null:
                line(key + ": \"\"");
                return;
            case JsonValue value when settled:
                line(key + ": " + ValueText(value, flow: false));
                return;
            case JsonArray array when settled && array.All(item => item is JsonValue):
                line(key + ": [" + string.Join(", ", array.Select(item => ValueText((JsonValue)item!, flow: true))) + "]");
                return;
            case JsonArray array when settled:
                line(key + ":");
                WriteArray(array, indent + 2, escape: true, line);
                return;
        }

        line(key + ":");
        switch (node)
        {
            case JsonObject { Count: 0 }:
                line(inner + MappingMapper.ValueKey + ": {}");
                break;
            case JsonObject obj:
                line(inner + MappingMapper.ValueKey + ":");
                WriteObject(obj, indent + 4, escape: false, line);
                break;
            case JsonArray array when array.All(item => item is JsonValue):
                line(inner + MappingMapper.ValueKey + ": [" + string.Join(", ", array.Select(item => ValueText((JsonValue)item!, flow: true))) + "]");
                break;
            case JsonArray array:
                line(inner + MappingMapper.ValueKey + ":");
                WriteArray(array, indent + 4, escape: false, line);
                break;
            case JsonValue value:
                line(inner + MappingMapper.ValueKey + ": " + ValueText(value, flow: false));
                break;
        }

        WriteSettings(entry, inner, line);
    }

    /// <summary>
    /// An object's properties, one per line. A literal in the tree names a property that starts with '$' with one more
    /// (<paramref name="escape"/>); what <c>$value</c> holds is written verbatim.
    /// </summary>
    private static void WriteObject(JsonObject obj, int indent, bool escape, Action<string> line)
    {
        var pad = new string(' ', indent);
        foreach (var (name, child) in obj)
        {
            var key = pad + Scalar(escape ? MappingMapper.KeyOfProperty(name) : name);
            switch (child)
            {
                case JsonObject { Count: 0 }:
                    line(key + ": {}");
                    break;
                case JsonObject nested:
                    line(key + ":");
                    WriteObject(nested, indent + 2, escape, line);
                    break;
                case JsonArray array when array.All(item => item is JsonValue):
                    line(key + ": [" + string.Join(", ", array.Select(item => ValueText((JsonValue)item!, flow: true))) + "]");
                    break;
                case JsonArray array:
                    line(key + ":");
                    WriteArray(array, indent + 2, escape, line);
                    break;
                case JsonValue value:
                    line(key + ": " + ValueText(value, flow: false));
                    break;
                default:
                    line(key + ": null");
                    break;
            }
        }
    }

    private static void WriteArray(JsonArray array, int indent, bool escape, Action<string> line)
    {
        var pad = new string(' ', indent);
        foreach (var item in array)
        {
            switch (item)
            {
                case JsonObject obj:
                    var first = true;
                    var inner = new List<string>();
                    WriteObject(obj, indent + 2, escape, inner.Add);
                    foreach (var text in inner)
                    {
                        line(first ? pad + "- " + text.TrimStart() : text);
                        first = false;
                    }

                    if (first)
                    {
                        line(pad + "- {}");
                    }

                    break;
                case JsonArray nested when nested.All(value => value is JsonValue):
                    line(pad + "- [" + string.Join(", ", nested.Select(value => ValueText((JsonValue)value!, flow: true))) + "]");
                    break;
                case JsonArray nested:
                    line(pad + "-");
                    WriteArray(nested, indent + 2, escape, line);
                    break;
                case JsonValue value:
                    line(pad + "- " + ValueText(value, flow: false));
                    break;
                default:
                    line(pad + "- null");
                    break;
            }
        }
    }

    private static void WriteFixture(MappingDraftFixture fixture, Action<string> line)
    {
        line("  - name: " + Scalar(fixture.Name));
        if (fixture.Parameters.Count > 0)
        {
            line("    parameters: { " + string.Join(", ", fixture.Parameters.Select(kv => FlowScalar(kv.Key) + ": " + FlowScalar(kv.Value))) + " }");
        }

        if (fixture.Searches is { Count: > 0 } searches)
        {
            line("    searches:");
            foreach (var answer in searches)
            {
                var id = answer.Id is null ? string.Empty : ", id: " + FlowScalar(answer.Id);
                line("      - { search: " + FlowScalar(answer.Search) + ", field: " + FlowScalar(answer.Field) + ", value: " + FlowScalar(answer.Value) + id + " }");
            }
        }

        line("    row:");
        foreach (var (column, value) in fixture.Record)
        {
            line("      " + Scalar(column) + ": " + (value is null ? "null" : Scalar(value)));
        }

        if (fixture.Datasets.Count > 0)
        {
            line("    datasets:");
            foreach (var (child, rows) in fixture.Datasets)
            {
                line("      " + Scalar(child) + ":");
                foreach (var row in rows)
                {
                    line("        - { " + string.Join(", ", row.Select(kv => FlowScalar(kv.Key) + ": " + (kv.Value is null ? "null" : FlowScalar(kv.Value)))) + " }");
                }
            }
        }

        line("    expected: |");
        foreach (var text in fixture.Expected.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n'))
        {
            line(text.Length == 0 ? string.Empty : "      " + text);
        }
    }

    private static void StaticIssue(MappingDraftEntry entry, string target, Action<string, string?> error)
    {
        JsonNode? node;
        try
        {
            node = string.IsNullOrWhiteSpace(entry.Static) ? null : JsonNode.Parse(entry.Static);
        }
        catch (JsonException)
        {
            error($"{target}: the fixed value is not valid JSON.", target);
            return;
        }

        if (node is null || (node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length == 0))
        {
            error($"{target}: give the fixed value.", target);
        }
        else if (EnvelopeTargets.Contains(target, StringComparer.Ordinal) && (node is not JsonArray list || list.Count == 0))
        {
            error($"{target}: every record carries it, so give at least one value.", target);
        }
        else if (MappingMapper.LiteralTokenProblem(node) is { } problem)
        {
            error($"{target}: {problem}", target);
        }
    }

    /// <summary>
    /// A column an entry names read in the entry's scope: <c>child.column</c> names a column of a child dataset's rows, which
    /// only an entry inside the items of the array repeating that child reads.
    /// </summary>
    private static void ScopeIssue(string column, string? scope, string target, Action<string, string?> error)
    {
        var dot = column.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
        {
            return;
        }

        var child = column[..dot];
        if (!string.Equals(child, scope, StringComparison.Ordinal))
        {
            error(
                $"{target}: {column.Trim()} reads a column of child dataset '{child}', which only an entry inside the items of the array repeating {child} reads.",
                target);
        }
    }

    private static void ModifierIssue(MappingDraftModifier modifier, string? scope, string target, Action<string, string?> error)
    {
        switch (modifier.Kind)
        {
            case "trim" or "upper" or "lower":
                break;
            case "date" when !string.IsNullOrEmpty(modifier.Text) && Rendering.DateValues.FormatProblem(modifier.Text) is { } problem:
                error($"{target}: the date format '{modifier.Text}' {problem}.", target);
                break;
            case "date":
                break;
            case "number" when Rendering.NumberValues.SeparatorsProblem(modifier.DecimalSeparator, modifier.GroupSeparator) is { } separators:
                error($"{target}: number {separators}.", target);
                break;
            case "number":
                break;
            case "split" when string.IsNullOrEmpty(modifier.Separator) || modifier.Part is not > 0:
                error($"{target}: split needs a separator and the part to keep, counting from one.", target);
                break;
            case "split":
                break;
            case "replace" when !string.IsNullOrWhiteSpace(modifier.Table) && modifier.Replacements is { Count: > 0 }:
                error($"{target}: a replace reads its table from the cache or lists its values, not both.", target);
                break;
            case "replace" when !string.IsNullOrWhiteSpace(modifier.Table) && !ColumnName().IsMatch(modifier.Table.Trim()):
                error($"{target}: replace reads cache type '{modifier.Table}', and a cached type is named by letters, digits, '_' and '-', such as RecallUnits.", target);
                break;
            case "replace" when !string.IsNullOrWhiteSpace(modifier.Match) && !FieldPath().IsMatch(modifier.Match.Trim()):
                error($"{target}: replace's match names the cached field a value is compared with, such as mnemonic, not '{modifier.Match}'.", target);
                break;
            case "replace" when !string.IsNullOrWhiteSpace(modifier.Field) && !FieldPath().IsMatch(modifier.Field.Trim()):
                error($"{target}: replace's field names the cached field that replaces a value, such as curve_family, not '{modifier.Field}'.", target);
                break;
            case "replace" when string.IsNullOrWhiteSpace(modifier.Table) && (!string.IsNullOrWhiteSpace(modifier.Match) || !string.IsNullOrWhiteSpace(modifier.Field)):
                error($"{target}: match and field choose the fields of a table read from the cache; choose the cached type too.", target);
                break;
            case "replace" when string.IsNullOrWhiteSpace(modifier.Table)
                && (modifier.Replacements is not { Count: > 0 } || modifier.Replacements.Any(r => string.IsNullOrWhiteSpace(r.From))):
                error($"{target}: replace needs at least one incoming value and what it becomes, or a cached table to read them from.", target);
                break;
            case "replace" when (modifier.Replacements ?? []).GroupBy(r => r.From.Trim(), StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } twice:
                error($"{target}: replace lists '{twice.Key}' more than once; a value is matched trimmed, so each incoming value is listed once.", target);
                break;
            case "replace" when modifier.OtherwiseKind is not (null or FallbackKeep or FallbackEmpty or FallbackText):
                error($"{target}: replace's otherwise is keep, empty or text, not '{modifier.OtherwiseKind}'.", target);
                break;
            case "replace" when modifier.OtherwiseKind == FallbackText && string.IsNullOrWhiteSpace(modifier.OtherwiseText):
                error($"{target}: replace's otherwise needs the text an unlisted value becomes; choose no value for none.", target);
                break;
            case "replace":
                break;
            case "equals" when string.IsNullOrEmpty(modifier.Text):
                error($"{target}: equals needs the text to compare with.", target);
                break;
            case "equals":
                break;
            case "id" when IdTemplate.TryParse(modifier.Text, scope, out var idProblem) is null:
                error($"{target}: id: {idProblem}.", target);
                break;
            case "id":
                break;
            case "ref" when !string.IsNullOrWhiteSpace(modifier.Text) && !IdTemplate.IsEntityType(modifier.Text.Trim()) && !EntityName().IsMatch(modifier.Text.Trim()):
                error($"{target}: ref '{modifier.Text.Trim()}' is not an entity type; name it as the template does, such as UnitOfMeasure, or in full, such as reference-data--UnitOfMeasure.", target);
                break;
            case "ref":
                break;
            default:
                error($"{target}: '{modifier.Kind}' is not a modifier.", target);
                break;
        }
    }

    private const string FallbackKeep = "keep";

    private const string FallbackEmpty = "empty";

    private const string FallbackText = "text";

    private const string Marker = MappingMapper.Marker;

    private const string DatasetReference = MappingMapper.DatasetReference;

    private static string FallbackKind(ReplaceFallback fallback) => fallback.Kind switch
    {
        ReplaceFallbackKind.Keep => FallbackKeep,
        ReplaceFallbackKind.Empty => FallbackEmpty,
        _ => FallbackText,
    };

    /// <summary>
    /// A modifier as the lines of its list item: the modifier itself, then the settings written beside it. Only a replace has
    /// any: the fields of a cached table it names, and its <c>otherwise</c>, written beside the table so no incoming value is
    /// ever read as a setting.
    /// </summary>
    private static IReadOnlyList<string> ModifierLines(MappingDraftModifier modifier)
    {
        var lines = new List<string> { ModifierText(modifier) };
        if (modifier.Kind != "replace")
        {
            return lines;
        }

        if (!string.IsNullOrWhiteSpace(modifier.Table))
        {
            if (!string.IsNullOrWhiteSpace(modifier.Match))
            {
                lines.Add("match: " + modifier.Match.Trim());
            }

            if (!string.IsNullOrWhiteSpace(modifier.Field))
            {
                lines.Add("field: " + modifier.Field.Trim());
            }
        }

        switch (modifier.OtherwiseKind)
        {
            case FallbackEmpty:
                lines.Add("otherwise: ~");
                break;
            case FallbackText when !string.IsNullOrWhiteSpace(modifier.OtherwiseText):
                lines.Add("otherwise: " + Scalar(modifier.OtherwiseText));
                break;
        }

        return lines;
    }

    /// <summary>A replacement as a flow scalar: its text, or <c>~</c> for no value.</summary>
    private static string ReplacementText(string? to) => to is null ? "~" : FlowScalar(to);

    private static string ModifierText(MappingDraftModifier modifier) => modifier.Kind switch
    {
        "split" => "split: { separator: " + FlowScalar(modifier.Separator ?? string.Empty) + ", part: " + (modifier.Part ?? 0).ToString(CultureInfo.InvariantCulture) + " }",
        "replace" when !string.IsNullOrWhiteSpace(modifier.Table) => $"replace: {MappingMapper.CacheReference}.{modifier.Table.Trim()}",
        "replace" => "replace: { " + string.Join(", ", (modifier.Replacements ?? []).Select(r => FlowScalar(r.From) + ": " + ReplacementText(r.To))) + " }",
        "equals" => "equals: " + Scalar(modifier.Text ?? string.Empty),
        "date" when !string.IsNullOrEmpty(modifier.Text) => "date: " + Scalar(modifier.Text),
        "id" => "id: " + Quote(modifier.Text ?? string.Empty),
        "ref" when !string.IsNullOrWhiteSpace(modifier.Text) => "ref: " + Scalar(modifier.Text.Trim()),
        "number" when modifier.GroupSeparator is not null || modifier.DecimalSeparator is not (null or ".")
            => "number: { decimal: " + FlowScalar(modifier.DecimalSeparator ?? ".") + (modifier.GroupSeparator is null ? string.Empty : ", group: " + FlowScalar(modifier.GroupSeparator)) + " }",
        _ => modifier.Kind,
    };

    /// <summary>A findBy line as the document writes it: the field of the record read, then the column in the entry's scope or a quoted text.</summary>
    private static string FindByText(MappingDraftFind find, string? scope)
    {
        var operand = !string.IsNullOrWhiteSpace(find.Literal)
            ? (find.Literal.Contains('\'', StringComparison.Ordinal) ? "\"" + find.Literal + "\"" : "'" + find.Literal + "'")
            : ScopedColumn(find.Column ?? string.Empty, scope);
        return $"{find.Field?.Trim()} = {operand}";
    }

    /// <summary>
    /// An expression as a YAML value: plain where YAML reads it back as the same text (<c>curve_id != "MD"</c>), which is
    /// most expressions, and otherwise in single quotes, which keep the double quotes an expression writes its texts in as
    /// they stand. A plain value starts with a letter, a digit, '_', '$' or '(', and holds no ": " and no " #", which YAML
    /// would read as a key or a comment.
    /// </summary>
    internal static string ExpressionScalar(string value)
    {
        var plain = value.Length > 0
            && (char.IsAsciiLetterOrDigit(value[0]) || value[0] is '_' or '$' or '(')
            && value.Trim().Length == value.Length
            && value.AsSpan().IndexOfAny('\r', '\n', '\t') < 0
            && !value.Contains(": ", StringComparison.Ordinal)
            && !value.Contains(" #", StringComparison.Ordinal)
            && !value.EndsWith(':')
            && !ReservedWords.Contains(value)
            && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        if (plain)
        {
            return value;
        }

        return value.AsSpan().IndexOfAny('\r', '\n') >= 0 ? Quote(value) : "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    /// <summary>
    /// A draft column (<c>column</c> for the dataset's own row, <c>child.column</c> for a child dataset's) as the node that
    /// reads it in <paramref name="scope"/> writes it: inside the items of the array repeating <c>child</c>, its columns are
    /// named bare and the dataset's own row as <c>$dataset.column</c>. A child column read anywhere else is written as it
    /// stands, for the loader to report.
    /// </summary>
    private static string ScopedColumn(string column, string? scope)
    {
        var trimmed = column.Trim();
        var dot = trimmed.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
        {
            return scope is null || trimmed.Length == 0 ? trimmed : DatasetReference + "." + trimmed;
        }

        return string.Equals(trimmed[..dot], scope, StringComparison.Ordinal) ? trimmed[(dot + 1)..] : trimmed;
    }

    private static string ColumnText(DatasetColumn column) => column.Child is null ? column.Column : column.Child + "." + column.Column;

    private static string ValueText(JsonValue value, bool flow) => value.GetValueKind() switch
    {
        JsonValueKind.String => flow ? FlowScalar(value.GetValue<string>()) : Scalar(value.GetValue<string>()),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => value.ToJsonString(),
        _ => "null",
    };

    /// <summary>Text for a YAML comment, on one line.</summary>
    private static string Comment(string text) => text.ReplaceLineEndings(" ");

    /// <summary>A text value as a YAML scalar: plain where YAML reads it back as the same text, double-quoted otherwise.</summary>
    internal static string Scalar(string value) => Plain(value, BlockPlain()) ? value : Quote(value);

    /// <summary>A text value as a YAML scalar inside a flow collection, where brackets, braces and commas are not plain.</summary>
    internal static string FlowScalar(string value) => Plain(value, FlowPlain()) ? value : Quote(value);

    private static bool Plain(string value, Regex pattern)
        => value.Length > 0
           && pattern.IsMatch(value)
           && !ReservedWords.Contains(value)
           && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
           && !value.StartsWith('.');

    private static string Quote(string value)
        => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "\"";

    [GeneratedRegex(@"^[A-Za-z0-9_$(][A-Za-z0-9_$.\/@%+()\[\]\-]*(?: [A-Za-z0-9_$.\/@%+()\[\]\-='=]+)*$")]
    private static partial Regex BlockPlain();

    [GeneratedRegex(@"^[A-Za-z0-9_$(][A-Za-z0-9_$.\/@%+()\-]*$")]
    private static partial Regex FlowPlain();

    [GeneratedRegex(@"^[A-Za-z0-9_\-\.]+$")]
    private static partial Regex FileNamePart();

    [GeneratedRegex(@"^[A-Za-z0-9_\-]+$")]
    private static partial Regex ColumnName();

    /// <summary>The name of an OSDU entity, as a relationship names it after its group.</summary>
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*$")]
    private static partial Regex EntityName();

    [GeneratedRegex(@"^[A-Za-z0-9_\-]+(\.[A-Za-z0-9_\-]+)?$")]
    private static partial Regex DatasetColumnPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_\-\$]+(\.[A-Za-z0-9_\-\$]+)*$")]
    private static partial Regex FieldPath();

    [GeneratedRegex(@"\{(?<token>[^{}]*)\}")]
    private static partial Regex LabelTokenPattern();
}
