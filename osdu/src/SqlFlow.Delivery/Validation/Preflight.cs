using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Validation;

/// <summary>
/// The preflight gate (docs/delivery/mapping-templates.md, Checks). Before any render, and with no OSDU call, checks that
/// the mapping, its pinned template, the cache and the flow's source tables agree. If the combination does not validate,
/// nothing renders.
/// </summary>
public static partial class Preflight
{
    /// <summary>
    /// Runs every check and returns the issues. <paramref name="sourceColumns"/> maps a scope name (<c>record</c> or a child
    /// dataset) to the columns that table holds; pass null to check without a source.
    /// </summary>
    /// <param name="mapping">The mapping checked.</param>
    /// <param name="schema">The template it pins.</param>
    /// <param name="references">The cache version it renders against.</param>
    /// <param name="context">The render context, with the flow's parameters.</param>
    /// <param name="sourceColumns">The flow's source tables and their columns, when known.</param>
    /// <param name="searches">
    /// The mapping's searches resolved against the schemas they pin. A mapping that declares searches is checked only
    /// with them, since without them nothing says whether its lookups can be asked at all.
    /// </param>
    /// <param name="fixtures">
    /// False leaves the fixtures unchecked, for a caller that renders them itself (<see cref="RenderFixtures"/>).
    /// </param>
    public static IReadOnlyList<ValidationIssue> Check(
        MappingDefinition mapping,
        SchemaSnapshot schema,
        ReferenceSnapshot references,
        RenderContext context,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? sourceColumns,
        ResolvedSearches? searches = null,
        bool fixtures = true)
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

        // 4b. Every search resolves against the schema it pins, and every property it compares can be asked for.
        if (mapping.Searches.Count > 0)
        {
            if (searches is null)
            {
                issues.Add(ValidationIssue.Error(
                    $"{where}: the mapping searches {string.Join(", ", mapping.Searches.Keys.Order(StringComparer.Ordinal))}, and the schemas those searches pin were not loaded for this check, so nothing says whether its lookups can be asked."));
                return issues;
            }

            issues.AddRange(searches.Problems.Select(problem => ValidationIssue.Error(problem)));
        }

        MappingRenderer renderer;
        try
        {
            renderer = new MappingRenderer(mapping, schema, references, context, searches);
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

        // 5. Every property the schema requires has an entry that is allowed to be empty only if the schema allows it. The
        //    rule is MappingCoverage's, so the gate and the view of what a mapping covers judge a required property alike.
        issues.AddRange(MappingCoverage.RequiredIssues(mapping, schema, where));

        // 6. Every dataset column and child dataset exists in the flow's source tables.
        if (sourceColumns is not null)
        {
            CheckColumns(mapping, sourceColumns, issues, where);
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

        // 10. Every fixture renders exactly as declared under this context, unless the caller renders them itself (the
        //     fixtures update verb, which writes what they render).
        if (fixtures)
        {
            CheckFixtures(mapping, renderer, issues, where);
        }

        return issues;
    }

    /// <summary>
    /// The answers a fixture declares, as a search: what it declares is known, and anything else was never asked, so a
    /// render of the fixture that needs more comes back unfinished and names what it needed.
    /// </summary>
    private sealed class FixtureSearch : IRecordSearch
    {
        private readonly Dictionary<(string Kind, string Field, string Value), SearchAnswer> _answers = [];

        public FixtureSearch(MappingDefinition mapping, MappingFixture fixture)
        {
            foreach (var answer in fixture.Searches)
            {
                if (mapping.Searches.TryGetValue(answer.Search, out var search))
                {
                    _answers[(search.Kind, answer.Field, answer.Value)] = answer.Id is { } id ? SearchAnswer.Found(id) : SearchAnswer.None;
                }
            }
        }

        public bool TryAnswer(SearchQuestion question, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SearchAnswer? answer)
            => _answers.TryGetValue((question.Kind, question.Field, question.Value), out answer);

        public Task AnswerAsync(IReadOnlyCollection<SearchQuestion> questions, CancellationToken ct = default)
            => throw new DeliveryException("A fixture renders against the answers it declares and never asks the platform.");
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

    /// <summary>
    /// Everything an entry's checks find concerns the variable the entry fills, so each finding carries that target and a
    /// view of the template can show it on the variable itself.
    /// </summary>
    private static void CheckEntry(MappingEntry entry, OsduTemplate template, ReferenceSnapshot references, MappingRenderer renderer, List<ValidationIssue> issues, string where)
    {
        var found = new List<ValidationIssue>();
        CheckEntryInto(entry, template, references, renderer, found, where);
        issues.AddRange(found.Select(issue => issue with { Target = entry.Target.Text }));
    }

    private static void CheckEntryInto(MappingEntry entry, OsduTemplate template, ReferenceSnapshot references, MappingRenderer renderer, List<ValidationIssue> issues, string where)
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
            issues.Add(ValidationIssue.Error($"{name}: {entry.Target.Text} is a list inside a repeated item; a $forEach inside a $forEach is not supported."));
            return;
        }

        var shapeProblem = ShapeProblem(entry, variable);
        if (shapeProblem is not null)
        {
            issues.Add(ValidationIssue.Error($"{name}: {shapeProblem}"));
            return;
        }

        if (entry.Modifiers.Count > 0 && entry.Modifiers[^1].Kind == ModifierKind.Equals && entry.Source?.ReadsRow == true
            && variable.Type is not ("boolean" or "any"))
        {
            issues.Add(ValidationIssue.Error($"{name}: the last modifier is equals, which gives true or false, but the template takes a {variable.Type} at {entry.Target.Text}."));
        }

        CheckWrittenForm(entry, variable, renderer, issues, name);
        CheckReplaces(entry, references, issues, name);
        CheckId(entry, variable, references, renderer, issues, name);
        CheckExpressionParameters(entry, renderer, issues, name);

        if (entry.IsStatic)
        {
            CheckStatic(entry, variable, references, renderer, issues, name);
        }
        else if (entry.Source!.Kind == MappingSourceKind.Cache)
        {
            CheckCache(entry, variable, references, issues, name);
        }
        else if (entry.Source.Kind == MappingSourceKind.Search)
        {
            CheckSearch(entry, variable, renderer, issues, name);
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
        if (entry.Source?.ReadsRow != true)
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
                : $"{entry.Source} repeats rows into {target}, but a $forEach fills a list of objects and {target} is {Describe(variable)}.";
        }

        if (entry.Static is JsonObject)
        {
            return shape is TemplateVariableShape.Group or TemplateVariableShape.Whole && variable.Type is "object" or "any"
                ? null
                : $"a literal object cannot be written to {target}, which is {Describe(variable)}.";
        }

        if (entry.Static is JsonArray)
        {
            return shape is TemplateVariableShape.ValueList or TemplateVariableShape.GroupList or TemplateVariableShape.Whole && variable.Type is "array" or "any"
                ? null
                : $"a literal list cannot be written to {target}, which is {Describe(variable)}.";
        }

        if (entry.IsStatic)
        {
            return shape is TemplateVariableShape.Value or TemplateVariableShape.ValueList
                ? null
                : $"a single literal value cannot be written to {target}, which is {Describe(variable)}.";
        }

        if (entry.Source!.ReadsRow)
        {
            return shape is TemplateVariableShape.Value or TemplateVariableShape.ValueList
                ? null
                : $"{entry.Source} is one value, and {target} is {Describe(variable)}; fill the properties inside it instead.";
        }

        if (entry.Source.Kind == MappingSourceKind.Search)
        {
            return shape is TemplateVariableShape.Value or TemplateVariableShape.ValueList
                ? null
                : $"{entry.Source} is the id of the record a search finds, and {target} is {Describe(variable)}.";
        }

        return shape == TemplateVariableShape.GroupList
            ? $"{target} is {Describe(variable)}, which a $forEach fills from a child dataset, not a cache value."
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

    /// <summary>
    /// Every parameter an expression of the entry reads has a value in the flow: one without reads as no value, so a
    /// condition on it would never hold and a value computed from it would be missing, on every record.
    /// </summary>
    private static void CheckExpressionParameters(MappingEntry entry, MappingRenderer renderer, List<ValidationIssue> issues, string name)
    {
        foreach (var expression in entry.Expressions)
        {
            foreach (var parameter in expression.Parameters.Where(p => string.IsNullOrWhiteSpace(renderer.ParameterValue(p))))
            {
                issues.Add(ValidationIssue.Error($"{name} reads $param.{parameter} in '{expression}', which the flow supplies no value for."));
            }
        }
    }

    private static void CheckStatic(MappingEntry entry, TemplateVariable variable, ReferenceSnapshot references, MappingRenderer renderer, List<ValidationIssue> issues, string name)
    {
        var texts = MappingMapper.StaticTexts(entry.Static).ToList();
        foreach (var text in texts)
        {
            foreach (var parameter in MappingEntry.ParameterNames(text))
            {
                if (renderer.ParameterValue(parameter) is null)
                {
                    issues.Add(ValidationIssue.Error($"{name} uses {{$param.{parameter}}}, which the flow supplies no value for."));
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
            var cached = CachedReferences.Holding(references, entityType);
            if (cached.Count > 0 && CachedReferences.Find(cached, new CachedReference(match.Groups["id"].Value, entityType)) is null)
            {
                issues.Add(ValidationIssue.Error($"{name}: '{value}' is not in cache version '{references.Version}', which caches {entityType} as {string.Join(", ", cached.Select(t => t.Name))}."));
            }
        }
    }

    /// <summary>
    /// 8, for a search: the id a search finds is of the entity type it searches, which has to be one the template points
    /// the target to. Whether each property its findBy lines compare can be asked for at all is the searches' resolution
    /// against their schemas, reported with the mapping's other problems.
    /// </summary>
    private static void CheckSearch(MappingEntry entry, TemplateVariable variable, MappingRenderer renderer, List<ValidationIssue> issues, string name)
    {
        var searchName = entry.Source!.CacheType!;
        if (!renderer.Mapping.Searches.TryGetValue(searchName, out var search))
        {
            return;
        }

        var entityType = search.Kind.Split(':')[2];
        if (variable.Relationships.Count > 0)
        {
            if (!Points(variable.Relationships, entityType))
            {
                issues.Add(ValidationIssue.Error(
                    $"{name} writes the id of a {entityType} record search '{searchName}' finds, but the template points {entry.Target.Text} to {string.Join(" or ", variable.Relationships)}."));
            }
        }
        else if (variable.Pattern is null)
        {
            issues.Add(ValidationIssue.Warning($"{name} writes an OSDU reference, but the template does not mark {entry.Target.Text} as a relationship."));
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
                $"{name} reads {source.CacheType} from the cache, and cache version '{references.Version}' does not hold it. Cached: {(available.Count == 0 ? "nothing" : string.Join(", ", available))}."));
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

        if (source.ReadsRecordId && cached.IsLookup)
        {
            issues.Add(ValidationIssue.Error(
                $"{name} reads the id of a {cached.Name} row, and {cached.Name} is a lookup table whose rows are not OSDU records, so it has no id to write. Read one of its fields: {string.Join(", ", cached.FieldNames)}."));
            return;
        }

        if (!source.ReadsRecordId && !cached.MeansRecordId(source.CacheField!) && cached.Items.All(item => cached.Value(item, source.CacheField!) is null))
        {
            issues.Add(ValidationIssue.Error($"{name} reads '{source.CacheField}' out of {cached.Name}, which cache version '{references.Version}' does not cache. Cached: {cachedFields}."));
        }

        // 8. A cached id resolves to the entity type the schema expects for the target; so does every id a cached field
        //    written to a relationship holds.
        if (!source.ReadsRecordId)
        {
            if (variable.Relationships.Count > 0)
            {
                CheckCachedReferences(entry, variable, cached, references, issues, name);
            }

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

    /// <summary>
    /// 7c. An id or a ref modifier: a ref's entity type is one the template settles; the variable takes text; each cache
    /// token reads a lookup table the cache version holds, at a field its rows hold; each parameter has a value; and the id
    /// the template builds, with a stand-in for what a row gives, is one the variable takes: of an entity type its
    /// relationship allows, and matching its pattern.
    /// </summary>
    private static void CheckId(MappingEntry entry, TemplateVariable variable, ReferenceSnapshot references, MappingRenderer renderer, List<ValidationIssue> issues, string name)
    {
        if (entry.Modifiers.LastOrDefault(m => m.BuildsId) is not { } builder)
        {
            return;
        }

        IdTemplate? template;
        if (builder.Kind == ModifierKind.Ref)
        {
            template = renderer.ReferenceTemplate(entry, out var problem);
            if (template is null)
            {
                issues.Add(ValidationIssue.Error($"{name}: {problem}."));
                return;
            }
        }
        else
        {
            template = builder.Id!;
        }

        var target = entry.Target.Text;
        var property = renderer.Schema.Resolve(entry.Target.SchemaPath);
        var listed = variable.Shape == TemplateVariableShape.ValueList;
        var written = listed ? variable.ItemType ?? "any" : variable.Type;
        var format = listed ? property?.ItemFormat : variable.Format;
        if (written is not ("string" or "any") || format is not null)
        {
            issues.Add(ValidationIssue.Error(
                $"{name}: the id modifier gives an OSDU id, written as text, but the template takes {(format is null ? $"a {written}" : $"a {format} string")} at {target}."));
            return;
        }

        foreach (var token in template.Tokens.Where(t => t.Kind == IdTokenKind.Cache).DistinctBy(t => t.Text, StringComparer.Ordinal))
        {
            CheckIdCacheToken(token, references, issues, name);
        }

        foreach (var parameter in template.Parameters.Where(p => string.IsNullOrWhiteSpace(renderer.ParameterValue(p))))
        {
            issues.Add(ValidationIssue.Error($"{name}: the id {template} reads {{$param.{parameter}}}, which the flow supplies no value for."));
        }

        if (template.EntityType is not { } entityType)
        {
            if (variable.Relationships.Count > 0 || variable.Pattern is not null)
            {
                issues.Add(ValidationIssue.Warning(
                    $"{name}: a token writes the entity type of the id {template}, so whether each id is one {target} takes is checked record by record, as it is built."));
            }

            return;
        }

        if (variable.Relationships.Count > 0 && !Points(variable.Relationships, entityType))
        {
            issues.Add(ValidationIssue.Error(
                $"{name}: the id {template} names a {entityType} record, but the template points {target} to {string.Join(" or ", variable.Relationships)}."));
            return;
        }

        if (variable.Relationships.Count == 0 && variable.Pattern is null)
        {
            issues.Add(ValidationIssue.Warning($"{name} writes an OSDU id, but the template does not mark {target} as a relationship."));
        }

        if (property is null || IdValues.PatternText(property) is not { } patternText)
        {
            return;
        }

        if (IdValues.Pattern(patternText) is null)
        {
            issues.Add(ValidationIssue.Warning(
                $"{name}: the pattern the template gives {target}, {patternText}, cannot be read as a regular expression, so each id is checked for OSDU's id shape and entity type only."));
        }
        else if (IdValues.Sample(template, renderer.ParameterValue) is { } sample && IdValues.Problem(sample, property) is { } problem)
        {
            issues.Add(ValidationIssue.Error(
                $"{name}: the id {template} gives ids such as '{sample}', which {target} does not take: {problem}. A reference ends with ':' and, when it pins one, the version."));
        }
    }

    /// <summary>A cache token of an id: a lookup table the cache version holds, looked up by its key, at a field its rows hold.</summary>
    private static void CheckIdCacheToken(IdToken token, ReferenceSnapshot references, List<ValidationIssue> issues, string name)
    {
        if (references.Type(token.CacheType!) is not { } type)
        {
            var available = references.Types.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            issues.Add(ValidationIssue.Error(
                $"{name}: the id reads {token}, and cache version '{references.Version}' holds no {token.CacheType}. Cached: {(available.Count == 0 ? "nothing" : string.Join(", ", available))}."));
            return;
        }

        if (type.Key is not { } key)
        {
            issues.Add(ValidationIssue.Error(
                $"{name}: the id reads {token}, and {type.Name} holds OSDU records ({type.EntityType}), which have no key to look the value up by; a cache token reads a lookup table, "
                + $"keyed by the value. Turn the value into the field an id needs with a replace before the id, such as replace: $cache.{type.Name}, match: Code, field: Code."));
            return;
        }

        var field = ReferenceField.Normalize(token.CacheField!);
        if (field.Equals(ReferenceField.Normalize(key), StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(ValidationIssue.Warning($"{name}: the id reads {token}, the key {type.Name} is looked up by, which is the value itself; write {{$value}}."));
            return;
        }

        if (type.Items.Count == 0)
        {
            issues.Add(ValidationIssue.Warning(
                $"{name}: the id reads {token}, and {type.Name} holds no rows in cache version '{references.Version}', so no record gets an id from it."));
            return;
        }

        if (type.Items.All(item => type.Value(item, field) is null))
        {
            issues.Add(ValidationIssue.Error(
                $"{name}: the id reads {token}, and no {type.Name} row holds '{field}' in cache version '{references.Version}'. Cached: {string.Join(", ", type.FieldNames)}."));
            return;
        }

        var several = type.Items.Where(item => type.Value(item, field) is { } value && value.Node is not JsonArray { Count: 0 } && ReplaceTables.SingleText(value) is null)
            .Select(item => item.Id).ToList();
        if (several.Count > 0)
        {
            issues.Add(ValidationIssue.Warning(
                $"{name}: {several.Count} {type.Name} row(s) hold several values or an object at '{field}' in cache version '{references.Version}' ({Listed(several)}); a record whose value is one of them is held, since {token} writes one value."));
        }
    }

    /// <summary>
    /// 7b. Every replace reading a cached table reads one the cache version holds, on fields it can settle and holds. And
    /// where the replaced value is then found in the cache, as a findBy value, every value a replace can give (inline or
    /// cached) is looked up in the type found, and the ones that find nothing are listed: each is a record a render holds,
    /// or leaves without its reference, and the listing names them before any row arrives.
    /// </summary>
    private static void CheckReplaces(MappingEntry entry, ReferenceSnapshot references, List<ValidationIssue> issues, string name)
    {
        for (var i = 0; i < entry.Modifiers.Count; i++)
        {
            var modifier = entry.Modifiers[i];
            if (modifier.Kind != ModifierKind.Replace)
            {
                continue;
            }

            IReadOnlyCollection<string> gives;
            if (modifier.Table is { } table)
            {
                if (CachedReplaceValues(table, references, issues, name) is not { } produced)
                {
                    continue;
                }

                gives = produced;
            }
            else
            {
                gives = modifier.Replacements.Values.OfType<string>().Distinct(StringComparer.Ordinal).ToList();
            }

            if (modifier.Otherwise.Kind == ReplaceFallbackKind.Text)
            {
                gives = gives.Append(modifier.Otherwise.Text!).Distinct(StringComparer.Ordinal).ToList();
            }

            CheckReplacedValuesResolve(entry, i, gives, references, issues, name);
        }
    }

    /// <summary>
    /// The values a replace reading <paramref name="table"/> can give, each once, or null (with the error) when the cache
    /// version does not hold the table or it cannot be read as the replace names it.
    /// </summary>
    private static List<string>? CachedReplaceValues(CachedReplaceTable table, ReferenceSnapshot references, List<ValidationIssue> issues, string name)
    {
        if (references.Type(table.CacheType) is not { } type)
        {
            var available = references.Types.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            issues.Add(ValidationIssue.Error(
                $"{name}: replace reads $cache.{table.CacheType}, which cache version '{references.Version}' does not hold. Cached: {(available.Count == 0 ? "nothing" : string.Join(", ", available))}."));
            return null;
        }

        if (ReplaceTables.Fields(table, type, out var problem) is not { } fields)
        {
            issues.Add(ValidationIssue.Error($"{name}: {problem}, in cache version '{references.Version}'."));
            return null;
        }

        var cachedFields = string.Join(", ", type.FieldNames.Prepend("id"));
        if (type.Items.Count == 0)
        {
            issues.Add(ValidationIssue.Warning(
                $"{name}: replace reads $cache.{type.Name}, which holds no rows in cache version '{references.Version}', so every value becomes what its otherwise says."));
            return [];
        }

        if (!type.HasField(fields.Match) && !type.MeansRecordId(fields.Match))
        {
            issues.Add(ValidationIssue.Error(
                $"{name}: replace matches the value on '{fields.Match}' of {type.Name}, which cache version '{references.Version}' does not cache. Cached: {cachedFields}."));
            return null;
        }

        // A field the replace names has to be one some row holds. One it settles is held by definition, or is the value of a
        // dictionary whose every entry gives no value, which is exactly what it says.
        if (table.Field is not null && !type.MeansRecordId(fields.Field) && type.Items.All(item => type.Value(item, fields.Field) is null))
        {
            issues.Add(ValidationIssue.Error(
                $"{name}: replace replaces the value by '{fields.Field}' of {type.Name}, which cache version '{references.Version}' does not cache. Cached: {cachedFields}."));
            return null;
        }

        if (type.IsAmbiguous(fields.Match))
        {
            issues.Add(ValidationIssue.Warning(
                $"{name}: several {type.Name} rows hold the same {fields.Match} in cache version '{references.Version}', and a value they give differently replaces nothing: the record is held."));
        }

        var gives = new SortedSet<string>(StringComparer.Ordinal);
        var several = new List<string>();
        foreach (var item in type.Items)
        {
            if (type.Value(item, fields.Field) is not { } value || value.Node is JsonArray { Count: 0 })
            {
                continue;
            }

            if (ReplaceTables.SingleText(value) is { } text)
            {
                gives.Add(text);
            }
            else
            {
                several.Add(item.Id);
            }
        }

        if (several.Count > 0)
        {
            issues.Add(ValidationIssue.Warning(
                $"{name}: {several.Count} {type.Name} row(s) hold several values or an object at '{fields.Field}' in cache version '{references.Version}' ({Listed(several)}); a value matching one of them holds its record, since a replace gives one value."));
        }

        return gives.ToList();
    }

    /// <summary>
    /// When the value the replace at <paramref name="index"/> gives is what the entry's findBy lines look up in the cache,
    /// the values it can give that find nothing, listed as a warning. Only a replace followed by nothing, or by case and
    /// spacing modifiers the check applies itself, is checked; any other modifier after it changes the value in a way only
    /// a row shows.
    /// </summary>
    private static void CheckReplacedValuesResolve(
        MappingEntry entry, int index, IReadOnlyCollection<string> gives, ReferenceSnapshot references, List<ValidationIssue> issues, string name)
    {
        if (gives.Count == 0 || entry.Source is not { Kind: MappingSourceKind.Cache, CacheType: { } typeName } || references.Type(typeName) is not { } target)
        {
            return;
        }

        var lines = entry.FindBy.Where(f => f.Column is not null).ToList();
        if (lines.Count == 0)
        {
            return;
        }

        var after = entry.Modifiers.Skip(index + 1).ToList();
        if (after.Any(m => m.Kind is not (ModifierKind.Trim or ModifierKind.Upper or ModifierKind.Lower)))
        {
            return;
        }

        var unresolved = new List<string>();
        foreach (var given in gives)
        {
            var value = after.Aggregate(given, (text, m) => m.Kind switch
            {
                ModifierKind.Upper => text.ToUpperInvariant(),
                ModifierKind.Lower => text.ToLowerInvariant(),
                _ => text.Trim(),
            }).Trim();
            if (value.Length == 0)
            {
                continue;
            }

            var resolves = (!target.IsLookup && RecordId().Match(value) is { Success: true } id && target.Match("id", id.Groups["id"].Value) is not null)
                || lines.Any(find => target.Find(find.Field, value, entry.IgnoreSeparators).Item is not null);
            if (!resolves)
            {
                unresolved.Add(value);
            }
        }

        if (unresolved.Count > 0)
        {
            var by = string.Join("/", lines.Select(f => ReferenceField.Normalize(f.Field)).Distinct(StringComparer.OrdinalIgnoreCase));
            issues.Add(ValidationIssue.Warning(
                $"{name}: {unresolved.Count} of the {gives.Count} value(s) its replace can give find no {target.Name} by {by} in cache version '{references.Version}': {Listed(unresolved)}. A record whose value becomes one of them is {(entry.Required ? "held" : "left without it")}."));
        }
    }

    /// <summary>Values as a finding lists them: the first ten, quoted, and how many more.</summary>
    private static string Listed(IReadOnlyList<string> values)
        => string.Join(", ", values.Take(10).Select(v => $"'{v}'")) + (values.Count > 10 ? $" and {values.Count - 10} more" : string.Empty);

    /// <summary>
    /// 8, for a cached field written to a relationship (OSDU's own translations cache the id of the record a value stands
    /// for, <c>ExternalUnitOfMeasure.UnitOfMeasureID</c>): every value the field holds in the cache version is an OSDU record
    /// id of an entity type the relationship allows, or the mapping is refused, since a render would write a reference that
    /// is no reference, or one to the wrong kind of record. Where the cache holds the entity type an id names, an id it holds
    /// no record for is listed as a warning: a render holds the records that meet it.
    /// </summary>
    private static void CheckCachedReferences(
        MappingEntry entry, TemplateVariable variable, ReferenceType cached, ReferenceSnapshot references, List<ValidationIssue> issues, string name)
    {
        var field = entry.Source!.CacheField!;
        var values = cached.Items
            .Select(item => cached.Value(item, field))
            .OfType<ReferenceValue>()
            .SelectMany(value => value.Terms)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (values.Count == 0)
        {
            return;
        }

        var points = string.Join(" or ", variable.Relationships);
        var parsed = values.Select(value => (Value: value, Reference: CachedReferences.Parse(value))).ToList();
        var notIds = parsed.Where(p => p.Reference is null).Select(p => p.Value).ToList();
        if (notIds.Count > 0)
        {
            issues.Add(ValidationIssue.Error(
                $"{name} writes '{field}' of {cached.Name} to {entry.Target.Text}, which points to {points}, and {notIds.Count} of the {values.Count} value(s) it holds in cache version '{references.Version}' are not OSDU record ids: {Listed(notIds)}. Read a field that holds record ids, or the id of the cached record."));
        }

        var ids = parsed.Where(p => p.Reference is not null).Select(p => (p.Value, Reference: p.Reference!.Value)).ToList();
        var otherType = ids.Where(p => !Points(variable.Relationships, p.Reference.EntityType)).ToList();
        if (otherType.Count > 0)
        {
            var kinds = string.Join(", ", otherType.Select(p => p.Reference.EntityType).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
            issues.Add(ValidationIssue.Error(
                $"{name} writes '{field}' of {cached.Name} to {entry.Target.Text}, which points to {points}, and {otherType.Count} of the ids it holds in cache version '{references.Version}' name records of another entity type ({kinds}): {Listed(otherType.Select(p => p.Value).ToList())}."));
        }

        var missing = new List<string>();
        foreach (var (value, reference) in ids.Where(p => Points(variable.Relationships, p.Reference.EntityType)))
        {
            var holding = CachedReferences.Holding(references, reference.EntityType);
            if (holding.Count > 0 && CachedReferences.Find(holding, reference) is null)
            {
                missing.Add(value);
            }
        }

        if (missing.Count > 0)
        {
            issues.Add(ValidationIssue.Warning(
                $"{name} writes '{field}' of {cached.Name} to {entry.Target.Text}, and {missing.Count} of the ids it holds name records cache version '{references.Version}' does not hold: {Listed(missing)}. A record whose value is one of them is held, since its reference would point at nothing."));
        }
    }

    /// <summary>Whether an entity type (<c>master-data--Wellbore</c>) is one a relationship allows; a group type alone (<c>dataset</c>) allows every entity of the group.</summary>
    private static bool Points(IReadOnlyList<string> relationships, string entityType) => Rendering.IdValues.Allows(relationships, entityType);

    private static void CheckColumns(MappingDefinition mapping, IReadOnlyDictionary<string, IReadOnlySet<string>> sourceColumns, List<ValidationIssue> issues, string where)
    {
        void Require(DatasetColumn column, string reader)
        {
            var scope = column.Child ?? SourceDatasets.Record;
            if (!sourceColumns.TryGetValue(scope, out var columns))
            {
                issues.Add(ValidationIssue.Error($"{where}: {reader} reads child dataset '{column.Child}', which the flow does not declare under source.datasets."));
            }
            else if (!columns.Contains(column.Column))
            {
                issues.Add(ValidationIssue.Error(
                    $"{where}: {reader} reads {column}, which {(column.Child is null ? "the record table" : "child dataset '" + column.Child + "'")} does not hold. Columns: {string.Join(", ", columns.OrderBy(c => c, StringComparer.Ordinal))}."));
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
                Require(new DatasetColumn(null, token.Groups["column"].Value), "dataset.label");
            }
        }

        foreach (var entry in mapping.Entries)
        {
            if (entry.IsRepeater && !sourceColumns.ContainsKey(entry.Source!.Child!))
            {
                issues.Add(ValidationIssue.Error($"{where}: {entry.Where} repeats child dataset '{entry.Source.Child}', which the flow does not declare under source.datasets."));
            }

            foreach (var column in entry.Columns)
            {
                Require(column, entry.Where);
            }
        }
    }

    /// <summary>
    /// Renders every fixture of <paramref name="mapping"/> as the preflight gate does: over its own rows, with its
    /// parameters over the render's, against the search answers it declares and never the platform. A fixture that fails
    /// to render, or asks a search it declares no answer to, carries why instead of a result. The gate compares what each
    /// renders with what it expects; <c>sqlflow fixtures update</c> writes it.
    /// </summary>
    public static IReadOnlyList<FixtureRender> RenderFixtures(MappingDefinition mapping, MappingRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(renderer);
        var renders = new List<FixtureRender>(mapping.Fixtures.Count);
        foreach (var fixture in mapping.Fixtures)
        {
            var datasets = fixture.Datasets.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<SourceRow>)kv.Value.Select(SourceRow.FromStrings).ToList(),
                StringComparer.OrdinalIgnoreCase);
            var record = new SourceRecord { Row = SourceRow.FromStrings(fixture.Record), Scopes = datasets };

            RenderContext? fixtureContext = null;
            if (fixture.Parameters.Count > 0)
            {
                var parameters = new Dictionary<string, string>(renderer.Context.Parameters, StringComparer.Ordinal);
                foreach (var kv in fixture.Parameters)
                {
                    parameters[kv.Key] = kv.Value;
                }

                fixtureContext = renderer.Context with { Parameters = parameters };
            }

            // A fixture renders against the answers it declares, never the platform: what it checks is the mapping.
            var fixtureRenderer = renderer.With(fixtureContext, new FixtureSearch(mapping, fixture));

            RenderResult result;
            try
            {
                result = fixtureRenderer.Render(record);
            }
            catch (DeliveryException ex)
            {
                renders.Add(new FixtureRender(fixture, null, $"failed to render: {ex.Message}"));
                continue;
            }

            if (result.IsIncomplete)
            {
                var searchesOf = mapping.Searches.Values.ToDictionary(v => v.Kind, v => v.Name, StringComparer.Ordinal);
                var missing = string.Join("; ", result.Unanswered.Select(q =>
                    $"{{ search: {searchesOf.GetValueOrDefault(q.Kind, q.Kind)}, field: {q.Field}, value: {q.Value}, id: <the record found, or leave it out for none> }}"));
                renders.Add(new FixtureRender(
                    fixture, null, $"searches for what it declares no answer to; a fixture says what the platform answers to every search its render asks, under 'searches': {missing}"));
                continue;
            }

            renders.Add(new FixtureRender(fixture, result, null));
        }

        return renders;
    }

    private static void CheckFixtures(MappingDefinition mapping, MappingRenderer renderer, List<ValidationIssue> issues, string where)
    {
        foreach (var (fixture, rendered, problem) in RenderFixtures(mapping, renderer))
        {
            if (problem is not null)
            {
                issues.Add(ValidationIssue.Error($"{where}: fixture '{fixture.Name}' {problem}"));
                continue;
            }

            var result = rendered!;
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

/// <summary>
/// One fixture as the preflight renders it: the render, or why there is none (it failed, or it asked a search the
/// fixture declares no answer to).
/// </summary>
/// <param name="Fixture">The fixture rendered.</param>
/// <param name="Result">What it rendered, or null when <paramref name="Problem"/> says why it did not.</param>
/// <param name="Problem">Why the fixture has no render, as a phrase that follows "fixture 'name'", or null.</param>
public sealed record FixtureRender(MappingFixture Fixture, RenderResult? Result, string? Problem);

public enum IssueSeverity
{
    Warning,
    Error,
}

/// <summary>
/// One finding of a check. <paramref name="Target"/> is the template variable it concerns (<c>osdu.data.FacilityName</c>)
/// for a finding about one variable, so a view of the template can show it there; null for a finding about the mapping as
/// a whole, such as a parameter or a fixture.
/// </summary>
public sealed record ValidationIssue(IssueSeverity Severity, string Message, string? Target = null)
{
    public static ValidationIssue Error(string message, string? target = null) => new(IssueSeverity.Error, message, target);

    public static ValidationIssue Warning(string message, string? target = null) => new(IssueSeverity.Warning, message, target);

    public override string ToString() => $"{Severity.ToString().ToLowerInvariant()}: {Message}";
}

internal static class StringExtensions
{
    public static string Indent(this string text, string indent)
        => string.Join(Environment.NewLine, text.Split('\n').Select(l => indent + l.TrimEnd('\r')));
}
