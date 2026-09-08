using System.Text.Json;
using System.Text.Json.Nodes;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// Shallow merges of the small JSON objects the ledger keeps per record: the target state (what OSDU returned,
/// merged step by step) and the step progress of a pending delivery. A property in the patch replaces the same
/// property in the base; everything else is kept. Text that is not a JSON object counts as empty, so a corrupt
/// value can never wedge a record.
/// </summary>
public static class JsonMerge
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    public static string? Merge(string? existing, string? patch)
    {
        if (string.IsNullOrWhiteSpace(patch))
        {
            return existing;
        }

        var target = Parse(existing);
        var source = Parse(patch);
        foreach (var (name, value) in source.ToList())
        {
            target[name] = value?.DeepClone();
        }

        return target.Count == 0 ? null : target.ToJsonString(Compact);
    }

    /// <summary>A JSON object from string values, or null when there are none.</summary>
    public static string? FromValues(IReadOnlyDictionary<string, string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return null;
        }

        var node = new JsonObject();
        foreach (var (name, value) in values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            node[name] = value;
        }

        return node.ToJsonString(Compact);
    }

    /// <summary>The string-valued properties of a JSON object; nested values are kept as their JSON text.</summary>
    public static IReadOnlyDictionary<string, string> ToValues(string? json)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in Parse(json))
        {
            if (value is null)
            {
                continue;
            }

            result[name] = value is JsonValue v && v.TryGetValue<string>(out var text) ? text : value.ToJsonString(Compact);
        }

        return result;
    }

    public static JsonObject Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }
}
