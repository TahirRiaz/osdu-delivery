using System.Text.RegularExpressions;
using SqlFlow.Delivery.Drops;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;

namespace SqlFlow.Delivery.Validation;

/// <summary>The source columns a mapping reads from one scope, in the order the mapping first names them.</summary>
public sealed record MappingScopeColumns(string Scope, IReadOnlyList<string> Columns);

/// <summary>
/// The source columns a mapping reads: the root row's, each child scope's, and the natural key's. It is the column half
/// of a flow's source contract (what a source sends for the flow), the column list an inline submission's drop declares
/// (design.md section 3.4), and the same walk the preflight gate checks a drop's declared columns with.
/// </summary>
public sealed record MappingSourceColumns(IReadOnlyList<string> Record, IReadOnlyList<MappingScopeColumns> Scopes, IReadOnlyList<string> NaturalKey)
{
    /// <summary>The columns read from <paramref name="scope"/>: the root row's for <c>record</c>, none for a scope the mapping does not iterate.</summary>
    public IReadOnlyList<string> For(string scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return scope.Equals(DropManifest.RootScope, StringComparison.OrdinalIgnoreCase)
            ? Record
            : Scopes.FirstOrDefault(s => s.Scope.Equals(scope, StringComparison.OrdinalIgnoreCase))?.Columns ?? [];
    }
}

public static partial class MappingColumns
{
    /// <summary>
    /// Every column the mapping reads, per scope: scalar bindings, the columns a template or a delivered reference
    /// names, the columns the identity label names, and the natural key's source columns. A natural key property that
    /// names no mapped property is left out here; the renderer refuses the mapping for it.
    /// </summary>
    public static MappingSourceColumns Read(MappingDefinition mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        var byScope = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        List<string> Scope(string name)
        {
            if (!byScope.TryGetValue(name, out var columns))
            {
                columns = [];
                byScope[name] = columns;
                order.Add(name);
            }

            return columns;
        }

        var root = Scope(DropManifest.RootScope);
        var naturalKey = NaturalKey(mapping);
        foreach (var column in naturalKey)
        {
            Add(root, column);
        }

        Preflight.WalkProperties(mapping, mapping.Properties, string.Empty, DropManifest.RootScope, (property, _, scope) =>
        {
            var columns = Scope(scope);
            if (property.Source is { } source && !property.Collection && !property.IsObject)
            {
                Add(columns, source);
            }

            foreach (var column in UsedBy(property))
            {
                Add(columns, column);
            }

            if (property.Collection && (property.Scope ?? property.Source) is { } child)
            {
                Scope(child);
            }
        });

        if (!string.IsNullOrWhiteSpace(mapping.Identity.Label))
        {
            foreach (Match match in LabelToken().Matches(mapping.Identity.Label))
            {
                Add(root, match.Groups["name"].Value);
            }
        }

        return new MappingSourceColumns(
            root,
            order.Where(n => !n.Equals(DropManifest.RootScope, StringComparison.OrdinalIgnoreCase)).Select(n => new MappingScopeColumns(n, byScope[n])).ToList(),
            naturalKey);
    }

    /// <summary>The natural key's source columns, in key order (the columns a delivery key is derived from, design.md section 5.2).</summary>
    public static IReadOnlyList<string> NaturalKey(MappingDefinition mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        return mapping.Identity.NaturalKey
            .Select(path => mapping.Properties.FirstOrDefault(p => p.Target.Equals(path, StringComparison.Ordinal))?.Source)
            .OfType<string>()
            .ToList();
    }

    /// <summary>The columns a property reads beyond its own source binding: a delivered reference's key columns, a template's tokens.</summary>
    public static IEnumerable<string> UsedBy(MappingProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);
        if (property.Transform == MappingTransform.DeliveredReference)
        {
            return property.Config.Keys.Count > 0 ? property.Config.Keys : property.Source is null ? [] : [property.Source];
        }

        if (property.Transform == MappingTransform.Template && property.Config.Format is { } format)
        {
            return TemplateToken().Matches(format)
                .Select(m => m.Groups["name"].Value)
                .Where(n => !n.StartsWith("param:", StringComparison.Ordinal))
                .ToList();
        }

        return [];
    }

    private static void Add(List<string> columns, string column)
    {
        if (!columns.Contains(column, StringComparer.OrdinalIgnoreCase))
        {
            columns.Add(column);
        }
    }

    [GeneratedRegex(MappingRenderer.LabelTokenPattern)]
    private static partial Regex LabelToken();

    [GeneratedRegex(@"\{(?<name>[A-Za-z0-9_\-\.]+)\}")]
    private static partial Regex TemplateToken();
}
