using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.SearchTerms;

/// <summary>A value typed for a search term as the record holds it, or why it cannot be: what the render would hold the record for.</summary>
/// <param name="Value">The value the record holds, as a search compares it; null with <paramref name="Problem"/> when there is none.</param>
/// <param name="Problem">Why the value cannot be carried, in words; null when it is.</param>
public sealed record SearchValue(string? Value, string? Problem)
{
    public static SearchValue Of(string value) => new(value, null);

    public static SearchValue Refused(string problem) => new(null, problem);
}

/// <summary>
/// Puts a value typed for a search term through the mapping node its route names, exactly as the render puts a value of the
/// column through it (osdu/docs/search-terms.md): the same modifiers, the same rule for an empty value, and the same
/// conversion to the type the template gives the variable, by the render's own code, so a value searched for is the value
/// a delivered record holds. <c>DEPTH</c> through <c>replace(DEPTH: Depth)</c> and <c>ref</c> is
/// <c>dev:reference-data--WellLogSamplingDomainType:Depth:</c>.
/// </summary>
/// <remarks>
/// The node's <c>$when</c> is not asked: a search looks for the value wherever a record holds it. No cache is read, so a
/// route that reads one (a table, an id built from a table) is not translated here; the compiler says so of it first. For
/// a lookup or a search, the value is put through the node's modifiers, which the render applies to the value it finds
/// the record by.
/// </remarks>
public sealed class SearchTermValues
{
    private readonly MappingDefinition _mapping;
    private readonly MappingRenderer _renderer;
    private readonly string _partition;

    private SearchTermValues(MappingDefinition mapping, MappingRenderer renderer, string partition)
    {
        _mapping = mapping;
        _renderer = renderer;
        _partition = partition;
    }

    /// <summary>The values of <paramref name="mapping"/>'s nodes in <paramref name="partition"/>, typed by <paramref name="schema"/>, the template it pins.</summary>
    /// <exception cref="FlowValidationException">The schema is not the template the mapping pins, or the partition is not one an id can carry.</exception>
    public static SearchTermValues For(MappingDefinition mapping, SchemaSnapshot schema, string partition)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal) { [RenderContext.DataPartitionParameter] = partition.Trim() };
        var renderer = MappingRenderer.ForValues(mapping, schema, parameters);
        // The partition is checked as the render checks it, so an id is never built in one an id cannot carry.
        _ = renderer.Context.DataPartition;
        return new SearchTermValues(mapping, renderer, partition.Trim());
    }

    /// <summary>The partition the values' ids are built in.</summary>
    public string Partition => _partition;

    /// <summary>
    /// <paramref name="value"/>, a value of <paramref name="key"/>'s column, as the record holds it through
    /// <paramref name="route"/>: through the node's modifiers for a copy, steps, a lookup or a search, and trimmed for a key,
    /// as the delivery key trims it.
    /// </summary>
    public SearchValue Translate(SearchTermKey key, SearchRoute route, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(value);
        if (route.Problem is { } problem)
        {
            return SearchValue.Refused(problem);
        }

        if (route.Kind == SearchRouteKind.Key)
        {
            var trimmed = value.Trim();
            return trimmed.Length == 0 ? SearchValue.Refused($"{key.ColumnLabel} is empty, and a key holds a value") : SearchValue.Of(trimmed);
        }

        if (route.Kind == SearchRouteKind.Expression)
        {
            return SearchValue.Refused($"{key.ColumnLabel} enters an expression, which a search cannot run backwards");
        }

        if (NodeOf(route) is not { } node)
        {
            return SearchValue.Refused($"{route.Mappings[^1]} no longer writes {route.Target} from {key.ColumnLabel}; sync the repository again");
        }

        var column = new DatasetColumn(key.Dataset, key.Column);
        // The node as it reads the column: its own modifiers, nothing else asked of it, and required, so an empty value says so.
        var reading = node with
        {
            Source = new MappingSource { Kind = MappingSourceKind.DatasetColumn, Column = column },
            FindBy = [],
            FindAll = null,
            Alternatives = [],
            AppliesWhen = null,
            Required = true,
        };
        var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [key.Column] = value };
        var (root, item) = key.Dataset is null ? (SourceRow.FromStrings(row), (SourceRow?)null) : (SourceRow.Empty, SourceRow.FromStrings(row));
        var holds = new List<string>();
        JsonNode? written;
        try
        {
            written = EntryValues.Evaluate(reading, root, item, _renderer, holds, [], new RenderTrail());
        }
        catch (FlowValidationException ex)
        {
            return SearchValue.Refused(ex.Message);
        }

        if (holds.Count > 0)
        {
            return SearchValue.Refused(Reason(holds[0], node));
        }

        return written switch
        {
            null => SearchValue.Refused($"'{value}' gives {route.Target} no value"),
            JsonValue scalar => SearchValue.Of(Text(scalar)),
            _ => SearchValue.Refused($"'{value}' gives {route.Target} {written.GetValueKind().ToString().ToLowerInvariant()}, not one value a search compares"),
        };
    }

    /// <summary>The node the route names in the mapping: by where the mapping writes it, the variable it fills and its place among a node's alternatives.</summary>
    internal MappingEntry? NodeOf(SearchRoute route)
    {
        foreach (var entry in _mapping.Entries)
        {
            var alternatives = entry.Alternatives.Count;
            var place = 0;
            foreach (var node in entry.ValueNodes)
            {
                place++;
                var alternative = alternatives > 0 ? place : (int?)null;
                if (alternative == route.Alternative
                    && string.Equals(node.Target.Text, route.Target, StringComparison.Ordinal)
                    && string.Equals(node.Location ?? entry.Location, route.Location, StringComparison.Ordinal))
                {
                    return node;
                }
            }
        }

        return null;
    }

    /// <summary>A hold as a reason for the value: the render names the variable before it, which the search says by itself.</summary>
    private static string Reason(string hold, MappingEntry node)
    {
        var prefix = node.Target.Text + ": ";
        return hold.StartsWith(prefix, StringComparison.Ordinal) ? hold[prefix.Length..] : hold;
    }

    /// <summary>A value as a search compares it: text as it is, a number as JSON writes it, a boolean as true or false.</summary>
    private static string Text(JsonValue value) => value.GetValueKind() switch
    {
        JsonValueKind.String => value.GetValue<string>(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.ToJsonString(),
    };
}
