using System.Text.Json;
using SqlFlow.Core.Runs;

namespace SqlFlow.Catalog;

/// <summary>
/// The flow parameter values a schedule fires its members with, as the catalog stores them: a JSON object on the
/// schedule row, null when the schedule supplies none. Kept in one place so the document sync, the API and the
/// scheduler all read and write the column the same way.
/// </summary>
public static class ScheduleValues
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>The stored form, or null when there is nothing to store (so an empty map is not a JSON literal).</summary>
    public static string? ToJson(IReadOnlyDictionary<string, string>? values)
        => values is null || values.Count == 0
            ? null
            : JsonSerializer.Serialize(new SortedDictionary<string, string>(values.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal), StringComparer.Ordinal), Options);

    /// <summary>
    /// Reads the stored form back. A row written by an older build, or hand-edited into something that is not a
    /// JSON object, yields no values rather than failing the fire: a schedule that cannot read its values still
    /// fires, and the member run reports the missing parameter itself.
    /// </summary>
    public static IReadOnlyDictionary<string, string> FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json, Options);
            return parsed is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(parsed, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>Validates values the way a manual trigger's are, so a schedule can never queue what a trigger would reject.</summary>
    public static void Validate(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        new RunParameters { Values = values }.Validate();
    }
}
