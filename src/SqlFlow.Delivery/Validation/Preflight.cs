using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Validation;

/// <summary>
/// The preflight gate (design.md section 10.2). Before any render, and with no OSDU call, checks that the four
/// inputs are mutually consistent. If the combination does not validate, nothing renders.
/// </summary>
public static class Preflight
{
    /// <summary>
    /// Runs every check and returns the issues. <paramref name="dropColumns"/> maps scope name to the columns the
    /// drop declares; pass null to skip the source-binding check (validate without a drop).
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

        if (!string.Equals(schema.Kind, mapping.Kind, StringComparison.Ordinal))
        {
            issues.Add(ValidationIssue.Error($"{where}: mapping kind '{mapping.Kind}' does not match the schema snapshot kind '{schema.Kind}'."));
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

        var boundPaths = new HashSet<string>(StringComparer.Ordinal);
        WalkProperties(mapping, mapping.Properties, string.Empty, "record", (property, path, scope) =>
        {
            boundPaths.Add(path);

            // 1. Every source binding exists in the drop's declared schema.
            if (dropColumns is not null && property.Source is { } source && !property.Collection && !property.IsObject)
            {
                if (!dropColumns.TryGetValue(scope, out var columns))
                {
                    issues.Add(ValidationIssue.Error($"{where}: property '{path}' binds to scope '{scope}', which the drop does not declare."));
                }
                else if (!columns.Contains(source))
                {
                    issues.Add(ValidationIssue.Error($"{where}: property '{path}' binds to column '{source}', which scope '{scope}' of the drop does not declare. Declared: {string.Join(", ", columns.OrderBy(c => c, StringComparer.Ordinal))}."));
                }
            }

            if (dropColumns is not null && property.Transform is MappingTransform.DeliveredReference or MappingTransform.Template)
            {
                foreach (var column in MappingColumns.UsedBy(property))
                {
                    if (dropColumns.TryGetValue(scope, out var columns) && !columns.Contains(column))
                    {
                        issues.Add(ValidationIssue.Error($"{where}: property '{path}' uses column '{column}', which scope '{scope}' of the drop does not declare."));
                    }
                }
            }

            // 2. Every reference type the mapping resolves against exists in the cache, with the fields it matches
            //    and selects by. A path the cache does not hold would miss on every record, so it is caught here.
            if (property.Transform is MappingTransform.Reference or MappingTransform.Lookup)
            {
                var transformName = property.Transform == MappingTransform.Lookup ? "lookup" : "reference";
                if (string.IsNullOrWhiteSpace(property.Config.Type))
                {
                    issues.Add(ValidationIssue.Error($"{where}: property '{path}' uses the {transformName} transform without config.type."));
                }
                else if (references.Type(property.Config.Type) is not { } cached)
                {
                    issues.Add(ValidationIssue.Error($"{where}: property '{path}' resolves against reference type '{property.Config.Type}', which reference snapshot '{references.Version}' does not contain. Available: {string.Join(", ", references.Types.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal))}."));
                }
                else
                {
                    CheckCachedFields(property, cached, references.Version, path, where, transformName, issues);
                }
            }

            // 4. Every target path resolves to a real field in the pinned schema, with agreeing shapes.
            var schemaProperty = schema.Resolve(path);
            if (schemaProperty is null)
            {
                issues.Add(ValidationIssue.Error($"{where}: property '{path}' does not exist in schema '{schema.Kind}' (snapshot {schema.Version})."));
            }
            else
            {
                if (property.Collection && schemaProperty.Type != SchemaType.Array)
                {
                    issues.Add(ValidationIssue.Error($"{where}: property '{path}' is declared as a collection but the schema type is {schemaProperty.Type}."));
                }

                if (!property.Collection && property.IsObject && schemaProperty.Type is not (SchemaType.Object or SchemaType.Any))
                {
                    issues.Add(ValidationIssue.Error($"{where}: property '{path}' is declared as an object but the schema type is {schemaProperty.Type}."));
                }

                if (!property.Collection && !property.IsObject && schemaProperty.Type is SchemaType.Object)
                {
                    issues.Add(ValidationIssue.Error($"{where}: property '{path}' is a scalar binding but the schema type is object; declare nested properties."));
                }

                if (property.Transform == MappingTransform.Equals && schemaProperty.Type is not (SchemaType.Boolean or SchemaType.Any))
                {
                    issues.Add(ValidationIssue.Error($"{where}: property '{path}' uses the equals transform (boolean) but the schema type is {schemaProperty.Type}."));
                }

                if (property.Transform is MappingTransform.Reference or MappingTransform.DeliveredReference && !schemaProperty.IsRelationship && schemaProperty.Pattern is null)
                {
                    issues.Add(ValidationIssue.Warning($"{where}: property '{path}' renders an OSDU reference but the schema does not mark it as a relationship."));
                }
            }

            if (property.Collection && string.IsNullOrWhiteSpace(property.Scope ?? property.Source))
            {
                issues.Add(ValidationIssue.Error($"{where}: collection property '{path}' names no scope."));
            }

            if (property.Collection && (property.Scope ?? property.Source) is { } s && !mapping.Source.Scopes.Contains(s, StringComparer.OrdinalIgnoreCase))
            {
                issues.Add(ValidationIssue.Error($"{where}: collection property '{path}' iterates scope '{s}', which source.scopes does not declare."));
            }

            if (property.Definition is { } definition && !mapping.Definitions.ContainsKey(definition))
            {
                issues.Add(ValidationIssue.Error($"{where}: property '{path}' references definition '{definition}', which does not exist."));
            }
        });

        // 3. Every schema-required property has a binding that resolves.
        foreach (var required in schema.RequiredAt("data"))
        {
            if (!boundPaths.Contains("data." + required))
            {
                issues.Add(ValidationIssue.Error($"{where}: schema '{schema.Kind}' requires data.{required}, which the mapping does not bind."));
            }
        }

        foreach (var required in schema.RequiredAt(string.Empty))
        {
            if (required is not ("kind" or "acl" or "legal" or "id" or "data") && !boundPaths.Contains(required))
            {
                issues.Add(ValidationIssue.Error($"{where}: schema '{schema.Kind}' requires {required} at the record root, which the mapping does not bind."));
            }
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

        // 5. The mapping's own example fixtures still render correctly under this exact context.
        CheckExamples(mapping, renderer, issues, where);
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

    /// <summary>
    /// The fields a property matches by, and the value it selects, against what the cached type actually holds. A
    /// type that holds none of the fields can never match, which is an error; a single missing field is a warning,
    /// because a mapping may list fields that only some snapshots carry.
    /// </summary>
    private static void CheckCachedFields(
        MappingProperty property, ReferenceType cached, string version, string path, string where, string transformName, List<ValidationIssue> issues)
    {
        var matchBy = property.Config.MatchBy;
        if (matchBy.Count > 0)
        {
            var known = matchBy.Where(cached.HasField).ToList();
            if (known.Count == 0)
            {
                issues.Add(ValidationIssue.Error(
                    $"{where}: property '{path}' matches {cached.Name} by {string.Join("/", matchBy)}, and reference snapshot '{version}' caches none of those. Cached: {string.Join(", ", cached.FieldNames.Prepend("id"))}."));
            }
            else
            {
                foreach (var field in matchBy.Where(f => !cached.HasField(f)))
                {
                    issues.Add(ValidationIssue.Warning(
                        $"{where}: property '{path}' matches {cached.Name} by '{field}', which reference snapshot '{version}' does not cache. Cached: {string.Join(", ", cached.FieldNames.Prepend("id"))}."));
                }
            }
        }

        if (property.Transform != MappingTransform.Lookup)
        {
            return;
        }

        var select = string.IsNullOrWhiteSpace(property.Config.Select) ? "id" : property.Config.Select!;
        if (!cached.MeansRecordId(select) && cached.Items.All(item => cached.Value(item, select) is null))
        {
            issues.Add(ValidationIssue.Error(
                $"{where}: property '{path}' reads '{select}' out of {cached.Name}, which reference snapshot '{version}' does not cache. Cached: {string.Join(", ", cached.FieldNames.Prepend("id"))}."));
        }
    }

    private static void CheckExamples(MappingDefinition mapping, MappingRenderer renderer, List<ValidationIssue> issues, string where)
    {
        WalkProperties(mapping, mapping.Properties, string.Empty, "record", (property, path, _) =>
        {
            if (property.Collection || property.IsObject)
            {
                return;
            }

            var prefix = path.Length > property.Target.Length ? path[..^(property.Target.Length + 1)] : string.Empty;
            foreach (var example in property.Examples)
            {
                var row = example.Row.Count > 0
                    ? SourceRow.FromStrings(example.Row)
                    : SourceRow.FromStrings(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [property.Source ?? "value"] = example.Source });
                var holds = new List<string>();
                JsonNode? actual;
                try
                {
                    actual = renderer.RenderScalar(property, row, prefix, holds);
                }
                catch (DeliveryException ex)
                {
                    issues.Add(ValidationIssue.Error($"{where}: example for '{path}' (source '{example.Source}') failed: {ex.Message}"));
                    continue;
                }

                var expected = ParseExpected(example.Target);
                var actualText = actual is null ? "null" : CanonicalJson.ToString(actual);
                var expectedText = expected is null ? "null" : CanonicalJson.ToString(expected);
                if (!string.Equals(actualText, expectedText, StringComparison.Ordinal))
                {
                    var detail = holds.Count > 0 ? " (" + string.Join("; ", holds) + ")" : string.Empty;
                    issues.Add(ValidationIssue.Error($"{where}: example for '{path}' (source '{example.Source ?? "<row>"}') rendered {actualText}, expected {expectedText}{detail}."));
                }
            }
        });
    }

    private static void CheckFixtures(MappingDefinition mapping, MappingRenderer renderer, List<ValidationIssue> issues, string where)
    {
        foreach (var fixture in mapping.Fixtures)
        {
            var scopes = fixture.Scopes.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<SourceRow>)kv.Value.Select(SourceRow.FromStrings).ToList(),
                StringComparer.OrdinalIgnoreCase);
            var record = new SourceRecord { Row = SourceRow.FromStrings(fixture.Record), Scopes = scopes };

            var fixtureRenderer = renderer;
            if (fixture.Parameters.Count > 0)
            {
                var parameters = new Dictionary<string, string>(renderer.Context.Parameters, StringComparer.Ordinal);
                foreach (var kv in fixture.Parameters)
                {
                    parameters[kv.Key] = kv.Value;
                }

                fixtureRenderer = new MappingRenderer(mapping, SchemaOf(renderer), renderer.References, renderer.Context with { Parameters = parameters });
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
        }
    }

    private static SchemaSnapshot SchemaOf(MappingRenderer renderer) => renderer.Schema;

    private static JsonNode? ParseExpected(string target)
    {
        if (target.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return CanonicalJson.Normalize(JsonNode.Parse(target));
        }
        catch (JsonException)
        {
            return JsonValue.Create(target);
        }
    }

    /// <summary>Visits every property with its full dotted path and the scope its row comes from.</summary>
    public static void WalkProperties(
        MappingDefinition mapping,
        IReadOnlyList<MappingProperty> properties,
        string prefix,
        string scope,
        Action<MappingProperty, string, string> visit)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(visit);
        foreach (var property in properties)
        {
            var path = MappingRenderer.Join(prefix, property.Target);
            visit(property, path, scope);

            IReadOnlyList<MappingProperty> children = property.Definition is { } d && mapping.Definitions.TryGetValue(d, out var defined)
                ? defined
                : property.Properties;
            if (children.Count > 0)
            {
                var childScope = property.Collection ? property.Scope ?? property.Source ?? scope : scope;
                WalkProperties(mapping, children, path, childScope, visit);
            }
        }
    }
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
