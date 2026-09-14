using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Drops;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Validation;

/// <summary>
/// The preflight gate (docs/delivery/mapping-templates.md, Checks). Before any render, and with no OSDU call, checks that
/// the mapping, its pinned template, the cache and the drop agree. If the combination does not validate, nothing renders.
/// </summary>
public static partial class Preflight
{
    /// <summary>
    /// Runs every check and returns the issues. <paramref name="dropColumns"/> maps a scope name (<c>record</c> or a child
    /// dataset) to the columns the drop declares; pass null to check without a drop.
    /// </summary>
    public static IReadOnlyList<ValidationIssue> Check(
        MappingDefinition mapping,
        SchemaSnapshot schema,
        ReferenceSnapshot references,
        RenderContext context,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? dropColumns)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(references);
        ArgumentNullException.ThrowIfNull(context);

        var issues = new List<ValidationIssue>();
        var where = mapping.SourcePath ?? mapping.Reference;

        // 1. The pinned template version is the one given.
        if (!string.Equals(schema.Kind, mapping.Template.Kind, StringComparison.Ordinal) || !string.Equals(schema.Version, mapping.Template.Version, StringComparison.Ordinal))
        {
            issues.Add(ValidationIssue.Error($"{where}: the mapping pins template {mapping.Template}, but template {schema.Kind} version {schema.Version} was given."));
            return issues;
        }

        MappingRenderer renderer;
        try
        {
            renderer = new MappingRenderer(mapping, schema, references, context);
        }
        catch (FlowValidationException ex)
        {
            issues.Add(ValidationIssue.Error(ex.Message));
            return issues;
        }

        var template = OsduTemplate.From(schema);
        foreach (var entry in mapping.Entries)
        {
            CheckEntry(entry, template, references, renderer, issues, where);
        }

        // 5. Every property the schema requires has an entry that is allowed to be empty only if the schema allows it.
        foreach (var required in schema.RequiredAt("data"))
        {
            var target = $"{TemplatePath.Prefix}.data.{required}";
            var entry = mapping.Entries.FirstOrDefault(e => e.Target.Text == target);
            if (entry is null)
            {
                issues.Add(ValidationIssue.Error($"{where}: template {mapping.Template.Kind} requires {target}, which the mapping does not fill."));
            }
            else if (!entry.IsStatic && !entry.Required)
            {
                issues.Add(ValidationIssue.Error($"{where}: {entry.Where} is required: false, but the template requires {target}, so a record without it cannot be sent."));
            }
        }

        // 6. Every dataset column and child dataset exists in the drop.
        if (dropColumns is not null)
        {
            CheckColumns(mapping, dropColumns, issues, where);
        }

        foreach (var (name, parameter) in mapping.Parameters)
        {
            if (parameter.Required && parameter.Default is null && !context.Parameters.ContainsKey(name))
            {
                issues.Add(ValidationIssue.Error($"{where}: mapping parameter '{name}' is required and the flow supplies no value."));
            }
        }

        foreach (var name in context.Parameters.Keys)
        {
            if (!mapping.Parameters.ContainsKey(name))
            {
                issues.Add(ValidationIssue.Error($"{where}: the flow supplies parameter '{name}', which the mapping does not declare. Undeclared values would be overrides, and overrides are not allowed (design.md section 9.5)."));
            }
        }

        if (issues.Any(i => i.Severity == IssueSeverity.Error))
        {
            return issues;
        }

        // 10. Every fixture renders exactly as declared under this context.
        CheckFixtures(mapping, renderer, issues, where);
        return issues;
    }

    /// <summary>Throws a <see cref="FlowValidationException"/> listing every error when any is present.</summary>
    public static void ThrowIfFailed(IReadOnlyList<ValidationIssue> issues, string where)
    {
        ArgumentNullException.ThrowIfNull(issues);
        var errors = issues.Where(i => i.Severity == IssueSeverity.Error).ToList();
        if (errors.Count == 0)
        {
            return;
        }

        throw new FlowValidationException(
            $"{where}: preflight failed with {errors.Count} error(s):" + Environment.NewLine
            + string.Join(Environment.NewLine, errors.Select(e => "  - " + e.Message)));
    }

    private static void CheckEntry(MappingEntry entry, OsduTemplate template, ReferenceSnapshot references, MappingRenderer renderer, List<ValidationIssue> issues, string where)
    {
        var name = $"{where}: {entry.Where}";

        // 2. The target is a variable of the template, with an agreeing shape; 3. and one a mapping may fill.
        var variable = template.Find(entry.Target);
        if (variable is null)
        {
            issues.Add(ValidationIssue.Error($"{name} fills a variable that template {template.Kind} version {template.Version} does not have."));
            return;
        }

        switch (variable.Role)
        {
            case TemplateVariableRole.Engine:
                issues.Add(ValidationIssue.Error($"{name}: {entry.Target.Text} is written by OSDU Delivery ({(entry.Target.Leaf == "id" ? "from dataset.key" : "from the template")}), not by a mapping."));
                return;
            case TemplateVariableRole.Osdu:
                issues.Add(ValidationIssue.Error($"{name}: {entry.Target.Text} is set by OSDU when the record is stored, not by a mapping."));
                return;
        }

        if (variable.Nested)
        {
            issues.Add(ValidationIssue.Error($"{name}: {entry.Target.Text} is a list inside a repeated item; a repeater inside a repeater is not supported."));
            return;
        }

        var shapeProblem = ShapeProblem(entry, variable);
        if (shapeProblem is not null)
        {
            issues.Add(ValidationIssue.Error($"{name}: {shapeProblem}"));
            return;
        }

        if (entry.Modifiers.Count > 0 && entry.Modifiers[^1].Kind == ModifierKind.Equals && entry.Source?.Kind == MappingSourceKind.DatasetColumn
            && variable.Type is not ("boolean" or "any"))
        {
            issues.Add(ValidationIssue.Error($"{name}: the last modifier is equals, which gives true or false, but the template takes a {variable.Type} at {entry.Target.Text}."));
        }

        CheckWrittenForm(entry, variable, renderer, issues, name);

        if (entry.IsStatic)
        {
            CheckStatic(entry, variable, references, renderer, issues, name);
        }
        else if (entry.Source!.Kind == MappingSourceKind.Cache)
        {
            CheckCache(entry, variable, references, issues, name);
        }
    }

    /// <summary>
    /// Whether a dataset value's last modifier gives what the variable takes, judged by the variable itself or, for a list
    /// of values, by each item's type and format: a number modifier must fill a number, an integer or unformatted text, and a
    /// date modifier text whose format a date is written as. Text filling a date or date-time without the date modifier is
    /// written as it arrives, which is warned about because whatever reads the record expects the RFC 3339 form.
    /// </summary>
    private static void CheckWrittenForm(MappingEntry entry, TemplateVariable variable, MappingRenderer renderer, List<ValidationIssue> issues, string name)
    {
        if (entry.Source?.Kind != MappingSourceKind.DatasetColumn)
        {
            return;
        }

        var target = entry.Target.Text;
        var listed = variable.Shape == TemplateVariableShape.ValueList;
        var written = listed ? variable.ItemType ?? "any" : variable.Type;
        var format = listed ? renderer.Schema.Resolve(entry.Target.SchemaPath)?.ItemFormat : variable.Format;
        string Described(string textFormat) => listed ? $"a list of {textFormat} strings" : $"a {textFormat} string";
        var last = entry.Modifiers.Count > 0 ? entry.Modifiers[^1].Kind : (ModifierKind?)null;

        if (last == ModifierKind.Number)
        {
            if (written is not ("number" or "integer" or "string" or "any"))
            {
                issues.Add(ValidationIssue.Error($"{name}: the last modifier is number, which gives a number, but the template takes a {written} at {target}."));
            }
            else if (written == "string" && format is { } numberFormat)
            {
                issues.Add(ValidationIssue.Error($"{name}: the last modifier is number, but {target} takes {Described(numberFormat)}, which a number is not written as."));
            }
        }

        if (last == ModifierKind.Date)
        {
            if (written is not ("string" or "any"))
            {
                issues.Add(ValidationIssue.Error($"{name}: the last modifier is date, which gives a date written as text, but the template takes a {written} at {target}."));
            }
            else if (format is { } dateFormat && !Rendering.DateValues.Writes(dateFormat))
            {
                issues.Add(ValidationIssue.Error($"{name}: the last modifier is date, but {target} takes {Described(dateFormat)}, which a date is not written as."));
            }
        }
        else if (variable.Shape is TemplateVariableShape.Value or TemplateVariableShape.ValueList && written == "string"
            && format is Rendering.DateValues.DateFormat or Rendering.DateValues.DateTimeFormat
            && !entry.Modifiers.Any(m => m.Kind == ModifierKind.Date))
        {
            // A non-ISO value is only right when the record's meta describes its format, so this warns rather than refuses.
            issues.Add(ValidationIssue.Warning(
                $"{name}: {target} takes {Described(format)}, and a text value from {entry.Source} is written as it arrives; add the date modifier so every record carries the RFC 3339 form."));
        }
    }

    /// <summary>Why the entry's value cannot take the variable's shape, or null when it can.</summary>
    private static string? ShapeProblem(MappingEntry entry, TemplateVariable variable)
    {
        var shape = variable.Shape;
        var target = entry.Target.Text;
        if (entry.IsRepeater)
        {
            return shape == TemplateVariableShape.GroupList
                ? null
                : $"{entry.Source} repeats rows into {target}, but a repeater fills a list of objects and {target} is {Describe(variable)}.";
        }

        if (entry.Static is JsonObject)
        {
            return shape is TemplateVariableShape.Group or TemplateVariableShape.Whole && variable.Type is "object" or "any"
                ? null
                : $"a static object cannot be written to {target}, which is {Describe(variable)}.";
        }

        if (entry.Static is JsonArray)
        {
            return shape is TemplateVariableShape.ValueList or TemplateVariableShape.GroupList or TemplateVariableShape.Whole && variable.Type is "array" or "any"
                ? null
                : $"a static list cannot be written to {target}, which is {Describe(variable)}.";
        }

        if (entry.IsStatic)
        {
            return shape is TemplateVariableShape.Value or TemplateVariableShape.ValueList
                ? null
                : $"a single static value cannot be written to {target}, which is {Describe(variable)}.";
        }

        if (entry.Source!.Kind == MappingSourceKind.DatasetColumn)
        {
            return shape is TemplateVariableShape.Value or TemplateVariableShape.ValueList
                ? null
                : $"{entry.Source} is one value, and {target} is {Describe(variable)}; fill the properties inside it instead.";
        }

        return shape == TemplateVariableShape.GroupList
            ? $"{target} is {Describe(variable)}, which a repeater fills from a child dataset, not a cache value."
            : null;
    }

    private static string Describe(TemplateVariable variable) => variable.Shape switch
    {
        TemplateVariableShape.Value => $"one {variable.Type}",
        TemplateVariableShape.ValueList => $"a list of {variable.ItemType}",
        TemplateVariableShape.Group => "an object with properties of its own",
        TemplateVariableShape.GroupList => "a list of objects",
        _ => variable.Type == "array" ? "a list the schema does not break into properties" : "an object the schema does not break into properties",
    };

    private static void CheckStatic(MappingEntry entry, TemplateVariable variable, ReferenceSnapshot references, MappingRenderer renderer, List<ValidationIssue> issues, string name)
    {
        var texts = MappingMapper.StaticTexts(entry.Static).ToList();
        foreach (var text in texts)
        {
            foreach (var parameter in MappingEntry.ParameterNames(text))
            {
                if (renderer.ParameterValue(parameter) is null)
                {
                    issues.Add(ValidationIssue.Error($"{name} uses {{param.{parameter}}}, which the flow supplies no value for."));
                }
            }
        }

        // 9. A static id on a relationship exists in the cache, when the cache holds that entity type.
        if (variable.Relationships.Count == 0 || entry.Static is JsonObject)
        {
            return;
        }

        foreach (var text in texts)
        {
            var value = MappingEntry.ExpandParameters(text, renderer.ParameterValue).Trim();
            var match = RecordId().Match(value);
            if (!match.Success)
            {
                issues.Add(ValidationIssue.Error($"{name}: '{value}' is not an OSDU record id, and {entry.Target.Text} points to {string.Join(" or ", variable.Relationships)}."));
                continue;
            }

            var entityType = match.Groups["entity"].Value;
            if (!Points(variable.Relationships, entityType))
            {
                issues.Add(ValidationIssue.Error($"{name}: '{value}' is a {entityType} record, and {entry.Target.Text} points to {string.Join(" or ", variable.Relationships)}."));
                continue;
            }

            // Only an entity type the cache holds can be checked. Many fixed ids point at reference data nobody caches
            // (alias name types, say), and a finding on every plan for a value that cannot be checked is noise.
            var cached = references.Types.Where(t => string.Equals(t.EntityType, entityType, StringComparison.Ordinal)).ToList();
            if (cached.Count > 0 && cached.All(t => t.Match("id", match.Groups["id"].Value) is null))
            {
                issues.Add(ValidationIssue.Error($"{name}: '{value}' is not in cache version '{references.Version}', which caches {entityType} as {string.Join(", ", cached.Select(t => t.Name))}."));
            }
        }
    }

    private static void CheckCache(MappingEntry entry, TemplateVariable variable, ReferenceSnapshot references, List<ValidationIssue> issues, string name)
    {
        var source = entry.Source!;
        // 7. The cache type exists and holds the fields findBy compares and the field the source reads.
        if (references.Type(source.CacheType!) is not { } cached)
        {
            var available = references.Types.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            issues.Add(ValidationIssue.Error(
                $"{name} reads cache.{source.CacheType}, which cache version '{references.Version}' does not hold. Cached: {(available.Count == 0 ? "nothing" : string.Join(", ", available))}."));
            return;
        }

        var cachedFields = string.Join(", ", cached.FieldNames.Prepend("id"));
        var fields = entry.FindBy.Select(f => f.Field).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var known = fields.Where(f => cached.HasField(f) || cached.MeansRecordId(f)).ToList();
        if (known.Count == 0)
        {
            issues.Add(ValidationIssue.Error($"{name} finds {cached.Name} by {string.Join(" or ", fields)}, and cache version '{references.Version}' caches none of those. Cached: {cachedFields}."));
        }
        else
        {
            foreach (var field in fields.Except(known, StringComparer.OrdinalIgnoreCase))
            {
                issues.Add(ValidationIssue.Warning($"{name} finds {cached.Name} by '{field}', which cache version '{references.Version}' does not cache. Cached: {cachedFields}."));
            }
        }

        if (!source.ReadsRecordId && !cached.MeansRecordId(source.CacheField!) && cached.Items.All(item => cached.Value(item, source.CacheField!) is null))
        {
            issues.Add(ValidationIssue.Error($"{name} reads '{source.CacheField}' out of {cached.Name}, which cache version '{references.Version}' does not cache. Cached: {cachedFields}."));
        }

        // 8. A cached id resolves to the entity type the schema expects for the target.
        if (!source.ReadsRecordId)
        {
            return;
        }

        if (variable.Relationships.Count > 0)
        {
            if (!Points(variable.Relationships, cached.EntityType))
            {
                issues.Add(ValidationIssue.Error(
                    $"{name} writes the id of a cached {cached.Name} ({cached.EntityType}), but the template points {entry.Target.Text} to {string.Join(" or ", variable.Relationships)}."));
            }
        }
        else if (variable.Pattern is null)
        {
            issues.Add(ValidationIssue.Warning($"{name} writes an OSDU reference, but the template does not mark {entry.Target.Text} as a relationship."));
        }
    }

    /// <summary>Whether an entity type (<c>master-data--Wellbore</c>) is one a relationship allows; a group type alone (<c>dataset</c>) allows every entity of the group.</summary>
    private static bool Points(IReadOnlyList<string> relationships, string entityType)
        => relationships.Any(r => string.Equals(r, entityType, StringComparison.Ordinal)
            || (!r.Contains("--", StringComparison.Ordinal) && entityType.StartsWith(r + "--", StringComparison.Ordinal)));

    private static void CheckColumns(MappingDefinition mapping, IReadOnlyDictionary<string, IReadOnlySet<string>> dropColumns, List<ValidationIssue> issues, string where)
    {
        void Require(DatasetColumn column, string reader)
        {
            var scope = column.Child ?? DropManifest.RootScope;
            if (!dropColumns.TryGetValue(scope, out var columns))
            {
                issues.Add(ValidationIssue.Error($"{where}: {reader} reads child dataset '{column.Child}', which the drop does not declare."));
            }
            else if (!columns.Contains(column.Column))
            {
                issues.Add(ValidationIssue.Error(
                    $"{where}: {reader} reads {column}, which {(column.Child is null ? "the dataset's row" : "child dataset '" + column.Child + "'")} in the drop does not declare. Declared: {string.Join(", ", columns.OrderBy(c => c, StringComparer.Ordinal))}."));
            }
        }

        foreach (var key in mapping.Dataset.Key)
        {
            Require(new DatasetColumn(null, key), "dataset.key");
        }

        if (mapping.Dataset.Label is { } label)
        {
            foreach (Match token in MappingMapper.LabelToken().Matches(label))
            {
                Require(new DatasetColumn(null, token.Groups["column"].Value[(DatasetColumn.Prefix.Length + 1)..]), "dataset.label");
            }
        }

        foreach (var entry in mapping.Entries)
        {
            if (entry.IsRepeater && !dropColumns.ContainsKey(entry.Source!.Child!))
            {
                issues.Add(ValidationIssue.Error($"{where}: {entry.Where} repeats child dataset '{entry.Source.Child}', which the drop does not declare."));
            }

            foreach (var column in entry.Columns)
            {
                Require(column, entry.Where);
            }
        }
    }

    private static void CheckFixtures(MappingDefinition mapping, MappingRenderer renderer, List<ValidationIssue> issues, string where)
    {
        foreach (var fixture in mapping.Fixtures)
        {
            var datasets = fixture.Datasets.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<SourceRow>)kv.Value.Select(SourceRow.FromStrings).ToList(),
                StringComparer.OrdinalIgnoreCase);
            var record = new SourceRecord { Row = SourceRow.FromStrings(fixture.Record), Scopes = datasets };

            var fixtureRenderer = renderer;
            if (fixture.Parameters.Count > 0)
            {
                var parameters = new Dictionary<string, string>(renderer.Context.Parameters, StringComparer.Ordinal);
                foreach (var kv in fixture.Parameters)
                {
                    parameters[kv.Key] = kv.Value;
                }

                fixtureRenderer = new MappingRenderer(mapping, renderer.Schema, renderer.References, renderer.Context with { Parameters = parameters });
            }

            RenderResult result;
            try
            {
                result = fixtureRenderer.Render(record);
            }
            catch (DeliveryException ex)
            {
                issues.Add(ValidationIssue.Error($"{where}: fixture '{fixture.Name}' failed to render: {ex.Message}"));
                continue;
            }

            JsonNode? expected;
            try
            {
                expected = CanonicalJson.Normalize(JsonNode.Parse(fixture.Expected));
            }
            catch (JsonException ex)
            {
                issues.Add(ValidationIssue.Error($"{where}: fixture '{fixture.Name}' has invalid expected JSON: {ex.Message}"));
                continue;
            }

            var expectedText = CanonicalJson.ToString(expected);
            if (!string.Equals(result.Canonical, expectedText, StringComparison.Ordinal))
            {
                var diff = Planning.DocumentDiff.Compute(expected, result.Document);
                var holds = result.Holds.Count > 0 ? Environment.NewLine + "    holds: " + string.Join("; ", result.Holds) : string.Empty;
                issues.Add(ValidationIssue.Error($"{where}: fixture '{fixture.Name}' rendered a different document:{Environment.NewLine}{diff.Indent("    ")}{holds}"));
            }
            else if (result.Holds.Count > 0)
            {
                issues.Add(ValidationIssue.Error($"{where}: fixture '{fixture.Name}' renders the expected document but holds the record: {string.Join("; ", result.Holds)}"));
            }
        }
    }

    [GeneratedRegex(@"^(?<id>[\w\-\.]+:(?<entity>[\w\-\.]+--[\w\-\.]+):[\w\-\.\%]+):?[0-9]*$")]
    private static partial Regex RecordId();
}

public enum IssueSeverity
{
    Warning,
    Error,
}

public sealed record ValidationIssue(IssueSeverity Severity, string Message)
{
    public static ValidationIssue Error(string message) => new(IssueSeverity.Error, message);

    public static ValidationIssue Warning(string message) => new(IssueSeverity.Warning, message);

    public override string ToString() => $"{Severity.ToString().ToLowerInvariant()}: {Message}";
}

internal static class StringExtensions
{
    public static string Indent(this string text, string indent)
        => string.Join(Environment.NewLine, text.Split('\n').Select(l => indent + l.TrimEnd('\r')));
}
