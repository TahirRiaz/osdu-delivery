using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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
}

/// <summary>A mapping as the builder edits it: the header, the parameters, the entries and the fixtures (docs/delivery/mapping-templates.md).</summary>
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

    /// <summary>The label as written, with <c>{dataset.column}</c> tokens.</summary>
    public string? Label { get; init; }

    /// <summary>The dataset columns an operator finds a record by, without the <c>dataset.</c> prefix.</summary>
    public IReadOnlyList<string> Identity { get; init; } = [];

    public IReadOnlyList<MappingDraftParameter> Parameters { get; init; } = [];

    /// <summary>The record sets the mapping's search entries look in.</summary>
    public IReadOnlyList<MappingDraftSearch> Searches { get; init; } = [];

    public IReadOnlyList<MappingDraftEntry> Entries { get; init; } = [];

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

    public MappingDraftCondition? AppliesWhen { get; init; }

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
/// One modifier: trim, upper, lower, split, replace, equals, date or number, with its settings. A replace carries its pairs,
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

/// <summary>An appliesWhen: the column, the operator (is, isNot, isEmpty, isNotEmpty) and the text for is and isNot.</summary>
public sealed record MappingDraftCondition(string Column, string Operator, string? Text);

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

        foreach (var search in draft.Searches)
        {
            if (!ColumnName().IsMatch(search.Name ?? string.Empty))
            {
                Error("Name each search with letters, digits, underscores and hyphens; entries read it as search.<name>.id.");
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

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in draft.Entries)
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

            switch (entry.Input)
            {
                case MappingDraftInput.Dataset when !DatasetColumnPattern().IsMatch(entry.Column ?? string.Empty):
                    Error($"{target}: choose the dataset column the value comes from.", target);
                    break;
                case MappingDraftInput.Repeat when !ColumnName().IsMatch(entry.Child ?? string.Empty):
                    Error($"{target}: choose the child dataset whose rows become the items.", target);
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
                        if (!FieldPath().IsMatch(find.Field ?? string.Empty))
                        {
                            Error($"{target}: a findBy line needs the cached field it compares.", target);
                        }
                        else if (string.IsNullOrWhiteSpace(find.Literal) && !DatasetColumnPattern().IsMatch(find.Column ?? string.Empty))
                        {
                            Error($"{target}: findBy {find.Field} needs the dataset column, or a fixed text, it must equal.", target);
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
                    }

                    break;
                case MappingDraftInput.Static:
                    StaticIssue(entry, target, Error);
                    break;
            }

            foreach (var modifier in entry.Modifiers)
            {
                ModifierIssue(modifier, target, Error);
            }

            if (entry.AppliesWhen is { } condition)
            {
                if (!DatasetColumnPattern().IsMatch(condition.Column ?? string.Empty))
                {
                    Error($"{target}: appliesWhen needs the dataset column it tests.", target);
                }

                if (condition.Operator is "is" or "isNot" && string.IsNullOrEmpty(condition.Text))
                {
                    Error($"{target}: appliesWhen needs the text the column is compared with.", target);
                }
                else if (condition.Text is { } conditionText && conditionText.AsSpan().IndexOfAny('\r', '\n') >= 0)
                {
                    Error($"{target}: appliesWhen compares with text on one line.", target);
                }
                else if (condition.Operator is not ("is" or "isNot" or "isEmpty" or "isNotEmpty"))
                {
                    Error($"{target}: appliesWhen compares with is, is not, is empty or is not empty.", target);
                }
            }
        }

        return issues;
    }

    /// <summary>The draft written as a mapping document, in the documented style. Incomplete parts are written as they stand.</summary>
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
        Line("  key: [" + string.Join(", ", draft.Key.Select(k => FlowScalar(DatasetColumnText(k)))) + "]");
        if (!string.IsNullOrWhiteSpace(draft.Label))
        {
            Line("  label: " + Scalar(draft.Label.Trim()));
        }

        if (draft.Identity.Count > 0)
        {
            Line("  identity: [" + string.Join(", ", draft.Identity.Select(k => FlowScalar(DatasetColumnText(k)))) + "]");
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
        Line("mappings:");
        foreach (var entry in draft.Entries)
        {
            WriteEntry(entry, Line);
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
            Fixtures = mapping.Fixtures.Select(f => new MappingDraftFixture(
                f.Name,
                f.Parameters,
                f.Record,
                f.Datasets,
                f.Expected,
                f.Searches.Count == 0 ? null : f.Searches.Select(a => new MappingDraftFixtureSearch(a.Search, a.Field, a.Value, a.Id)).ToList())).ToList(),
        };
    }

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
                    _ => MappingDraftInput.Cache,
                },
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
                m.Text,
                m.DecimalSeparator,
                m.GroupSeparator,
                m.Kind == ModifierKind.Replace ? FallbackKind(m.Otherwise) : null,
                m.Kind == ModifierKind.Replace && m.Otherwise.Kind == ReplaceFallbackKind.Text ? m.Otherwise.Text : null,
                m.Table?.CacheType,
                m.Table?.Match,
                m.Table?.Field)).ToList(),
            AppliesWhen = entry.AppliesWhen is { } condition
                ? new MappingDraftCondition(
                    ColumnText(condition.Column),
                    condition.Operator switch
                    {
                        ConditionOperator.Is => "is",
                        ConditionOperator.IsNot => "isNot",
                        ConditionOperator.IsEmpty => "isEmpty",
                        _ => "isNotEmpty",
                    },
                    condition.Text)
                : null,
            Required = entry.Required,
            IgnoreSeparators = entry.IgnoreSeparators,
            Static = entry.Static?.ToJsonString(),
            Description = entry.Description,
        };
    }

    private static void WriteEntry(MappingDraftEntry entry, Action<string> line)
    {
        line("  - target: " + Scalar(entry.Target.Trim()));
        switch (entry.Input)
        {
            case MappingDraftInput.Dataset:
                line("    source: " + Scalar(DatasetColumnText(entry.Column ?? string.Empty)));
                break;
            case MappingDraftInput.Repeat:
                line("    source: " + Scalar(DatasetColumnText(entry.Child ?? string.Empty)));
                break;
            case MappingDraftInput.Cache or MappingDraftInput.Search:
                var prefix = entry.Input == MappingDraftInput.Search ? MappingSource.SearchPrefix : MappingSource.CachePrefix;
                line("    source: " + Scalar($"{prefix}.{entry.CacheType}.{entry.CacheField}"));
                var lines = entry.FindBy.Select(f => FindByText(prefix, entry.CacheType ?? string.Empty, f)).ToList();
                if (lines.Count == 1)
                {
                    line("    findBy: " + Scalar(lines[0]));
                }
                else if (lines.Count > 1)
                {
                    line("    findBy:");
                    foreach (var text in lines)
                    {
                        line("      - " + Scalar(text));
                    }
                }

                break;
            default:
                WriteStatic(entry.Static, line);
                break;
        }

        if (entry.Input != MappingDraftInput.Static && entry.Input != MappingDraftInput.Repeat && entry.Modifiers.Count > 0)
        {
            line("    modifiers:");
            foreach (var modifier in entry.Modifiers)
            {
                var lines = ModifierLines(modifier);
                line("      - " + lines[0]);
                foreach (var setting in lines.Skip(1))
                {
                    line("        " + setting);
                }
            }
        }

        if (entry.AppliesWhen is { } condition)
        {
            line("    appliesWhen: " + Scalar(ConditionText(condition)));
        }

        if (entry.Input != MappingDraftInput.Static && !entry.Required)
        {
            line("    required: false");
        }

        if (entry.Input == MappingDraftInput.Cache && entry.IgnoreSeparators)
        {
            line("    ignoreSeparators: true");
        }

        if (!string.IsNullOrWhiteSpace(entry.Description))
        {
            line("    description: " + Scalar(entry.Description.Trim()));
        }
    }

    private static void WriteStatic(string? json, Action<string> line)
    {
        JsonNode? node;
        try
        {
            node = string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            // A value that is not JSON is written as the text it is, so the page shows what was typed and the loader reports it.
            line("    static: " + Scalar(json ?? string.Empty));
            return;
        }

        switch (node)
        {
            case null:
                line("    static: \"\"");
                break;
            case JsonObject obj:
                line("    static:");
                WriteObject(obj, 6, line);
                break;
            case JsonArray array when array.All(item => item is JsonValue):
                line("    static: [" + string.Join(", ", array.Select(item => ValueText((JsonValue)item!, flow: true))) + "]");
                break;
            case JsonArray array:
                line("    static:");
                WriteArray(array, 6, line);
                break;
            case JsonValue value:
                line("    static: " + ValueText(value, flow: false));
                break;
        }
    }

    private static void WriteObject(JsonObject obj, int indent, Action<string> line)
    {
        var pad = new string(' ', indent);
        foreach (var (key, child) in obj)
        {
            switch (child)
            {
                case JsonObject nested:
                    line(pad + Scalar(key) + ":");
                    WriteObject(nested, indent + 2, line);
                    break;
                case JsonArray array when array.All(item => item is JsonValue):
                    line(pad + Scalar(key) + ": [" + string.Join(", ", array.Select(item => ValueText((JsonValue)item!, flow: true))) + "]");
                    break;
                case JsonArray array:
                    line(pad + Scalar(key) + ":");
                    WriteArray(array, indent + 2, line);
                    break;
                case JsonValue value:
                    line(pad + Scalar(key) + ": " + ValueText(value, flow: false));
                    break;
                default:
                    line(pad + Scalar(key) + ": null");
                    break;
            }
        }
    }

    private static void WriteArray(JsonArray array, int indent, Action<string> line)
    {
        var pad = new string(' ', indent);
        foreach (var item in array)
        {
            switch (item)
            {
                case JsonObject obj:
                    var first = true;
                    var inner = new List<string>();
                    WriteObject(obj, indent + 2, inner.Add);
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

        line("    record:");
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

        if (fixture.Searches is { Count: > 0 } searches)
        {
            line("    searches:");
            foreach (var answer in searches)
            {
                var id = answer.Id is null ? string.Empty : ", id: " + FlowScalar(answer.Id);
                line("      - { search: " + FlowScalar(answer.Search) + ", field: " + FlowScalar(answer.Field) + ", value: " + FlowScalar(answer.Value) + id + " }");
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
            error($"{target}: the static value is not valid JSON.", target);
            return;
        }

        if (node is null || (node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length == 0))
        {
            error($"{target}: give the static value.", target);
        }
        else if (EnvelopeTargets.Contains(target, StringComparer.Ordinal) && (node is not JsonArray list || list.Count == 0))
        {
            error($"{target}: every record carries it, so give at least one value.", target);
        }
    }

    private static void ModifierIssue(MappingDraftModifier modifier, string target, Action<string, string?> error)
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
            default:
                error($"{target}: '{modifier.Kind}' is not a modifier.", target);
                break;
        }
    }

    private const string FallbackKeep = "keep";

    private const string FallbackEmpty = "empty";

    private const string FallbackText = "text";

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
        "replace" when !string.IsNullOrWhiteSpace(modifier.Table) => $"replace: {MappingSource.CachePrefix}.{modifier.Table.Trim()}",
        "replace" => "replace: { " + string.Join(", ", (modifier.Replacements ?? []).Select(r => FlowScalar(r.From) + ": " + ReplacementText(r.To))) + " }",
        "equals" => "equals: " + Scalar(modifier.Text ?? string.Empty),
        "date" when !string.IsNullOrEmpty(modifier.Text) => "date: " + Scalar(modifier.Text),
        "number" when modifier.GroupSeparator is not null || modifier.DecimalSeparator is not (null or ".")
            => "number: { decimal: " + FlowScalar(modifier.DecimalSeparator ?? ".") + (modifier.GroupSeparator is null ? string.Empty : ", group: " + FlowScalar(modifier.GroupSeparator)) + " }",
        _ => modifier.Kind,
    };

    private static string FindByText(string prefix, string type, MappingDraftFind find)
    {
        var operand = !string.IsNullOrWhiteSpace(find.Literal)
            ? (find.Literal.Contains('\'', StringComparison.Ordinal) ? "\"" + find.Literal + "\"" : "'" + find.Literal + "'")
            : DatasetColumnText(find.Column ?? string.Empty);
        return $"{prefix}.{type}.{find.Field} = {operand}";
    }

    private static string ConditionText(MappingDraftCondition condition)
    {
        var column = DatasetColumnText(condition.Column);
        return condition.Operator switch
        {
            "isEmpty" => column + " is empty",
            "isNotEmpty" => column + " is not empty",
            _ => column + (condition.Operator == "isNot" ? " is not " : " is ") + ConditionOperand(condition.Text ?? string.Empty),
        };
    }

    /// <summary>A condition's text as the condition syntax reads it: quoted when it would otherwise read as a keyword or lose spaces.</summary>
    private static string ConditionOperand(string text)
        => text == "empty" || text.StartsWith("not ", StringComparison.Ordinal) || text.Trim().Length != text.Length || text.Contains('"', StringComparison.Ordinal)
            ? (text.Contains('\'', StringComparison.Ordinal) ? "\"" + text + "\"" : "'" + text + "'")
            : text;

    private static string DatasetColumnText(string column) => DatasetColumn.Prefix + "." + column.Trim();

    private static string ColumnText(Model.DatasetColumn column) => column.Child is null ? column.Column : column.Child + "." + column.Column;

    private static string ValueText(JsonValue value, bool flow) => value.GetValueKind() switch
    {
        JsonValueKind.String => flow ? FlowScalar(value.GetValue<string>()) : Scalar(value.GetValue<string>()),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => value.ToJsonString(),
        _ => "null",
    };

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

    [GeneratedRegex(@"^[A-Za-z0-9_\-]+(\.[A-Za-z0-9_\-]+)?$")]
    private static partial Regex DatasetColumnPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_\-\$]+(\.[A-Za-z0-9_\-\$]+)*$")]
    private static partial Regex FieldPath();
}
