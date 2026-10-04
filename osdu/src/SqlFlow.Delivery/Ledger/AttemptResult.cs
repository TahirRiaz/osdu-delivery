using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The shape of an attempt's result (<c>ResultJson</c>): the correlation id its OSDU requests carried, the steps it took,
/// what the target returned, and, for an attempt that did not fail, the note that says what it did. The attempt's error
/// is kept for errors: a note such as "1 chunk(s)" or "removed from OSDU" in that column reads as a failure.
/// </summary>
public static class AttemptResult
{
    /// <summary>
    /// <paramref name="resultJson"/> with <paramref name="detail"/> added. A result that held nothing yet is started with
    /// an empty steps array, so every reader finds the same shape.
    /// </summary>
    public static string? WithDetail(string? resultJson, string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return resultJson;
        }

        var result = ObjectOrEmpty(resultJson);
        result["detail"] = detail;
        return result.ToJsonString();
    }

    /// <summary>
    /// The result of a removal: the correlation id its call carried, what OSDU held for the record when it was removed (the
    /// record's target state, under <c>returned</c>), and the note saying what went.
    /// </summary>
    public static string Removal(string? correlationId, string? targetStateJson, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        var result = new JsonObject();
        if (correlationId is not null)
        {
            result["correlationId"] = correlationId;
        }

        result["steps"] = new JsonArray();
        if (Parse(targetStateJson) is { } held)
        {
            result["returned"] = held;
        }

        result["detail"] = detail;
        return result.ToJsonString();
    }

    /// <summary>
    /// <paramref name="resultJson"/> with the verdict the gate reached on the try's document added under <c>validation</c>
    /// (docs/validation-plan.md), so every attempt says what its document was checked against and what the check found.
    /// </summary>
    public static string? WithValidation(string? resultJson, ValidationVerdict? verdict)
    {
        if (verdict is null)
        {
            return resultJson;
        }

        var result = ObjectOrEmpty(resultJson);
        result["validation"] = verdict.ToJson();
        return result.ToJsonString();
    }

    /// <summary>The verdict an attempt's result carries, or null for an attempt that carries none.</summary>
    public static ValidationVerdict? Validation(string? resultJson) => ValidationVerdict.FromJson(Parse(resultJson)?["validation"]);

    private static JsonObject ObjectOrEmpty(string? json)
    {
        var result = Parse(json) ?? new JsonObject();
        if (result["steps"] is not JsonArray)
        {
            result["steps"] = new JsonArray();
        }

        return result;
    }

    private static JsonObject? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            // A value that is not JSON is not the shape anything wrote; it is left out rather than failing the attempt.
            return null;
        }
    }
}
