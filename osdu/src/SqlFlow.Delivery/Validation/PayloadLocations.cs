using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Storage;

namespace SqlFlow.Delivery.Validation;

/// <summary>Where a record's payload files are, or why the record cannot say.</summary>
/// <param name="Location">The folder and pattern the files are listed from; null when <paramref name="Refusal"/> is set.</param>
/// <param name="Refusal">Why the record is held rather than planned; null when the location resolved.</param>
public readonly record struct PayloadResolution(PayloadLocation? Location, string? Refusal);

/// <summary>
/// Resolves a record's payload folder from its row (docs/stage4-design.md section 2.5): the value of the payload's location
/// column joined to the payload's root when it is relative, or taken as it is when it is absolute, and in either case
/// checked against the flow's roots (<see cref="PayloadRoots"/>). An empty value, a value that climbs, and a folder
/// outside every root hold the record with the reason.
/// </summary>
public static class PayloadLocations
{
    public static PayloadResolution Resolve(FlowDefinition flow, IReadOnlyDictionary<string, string> values, SourceRow row, string payloadName)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadName);
        if (!flow.Source.Payloads.TryGetValue(payloadName, out var payload))
        {
            return new PayloadResolution(null, $"payload '{payloadName}' is not declared under source.payloads of flow '{flow.Name}'");
        }

        if (payload.LocationColumn is not { } column)
        {
            return new PayloadResolution(null, $"payload '{payloadName}' declares no locationColumn, so no record can say where its files are");
        }

        var value = row.GetString(column)?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return new PayloadResolution(null, $"payload '{payloadName}' takes its folder from column '{column}', which this row leaves empty; there is nowhere to read its files from");
        }

        if (value.Contains("..", StringComparison.Ordinal))
        {
            return new PayloadResolution(null, $"payload '{payloadName}' column '{column}' holds '{value}', which climbs out of its folder with '..'");
        }

        var root = FlowParameters.ResolvePath(flow, payload.Root, values, $"source.payloads.{payloadName}.root");
        var folder = IsAbsolute(value) ? Absolute(value) : Join(root, value);
        if (PayloadRoots.Refusal(flow, values, folder) is { } refusal)
        {
            return new PayloadResolution(null, $"payload '{payloadName}' column '{column}': {refusal}");
        }

        return new PayloadResolution(new PayloadLocation(folder, payload.Pattern), null);
    }

    private static bool IsAbsolute(string value) => value.Contains("://", StringComparison.Ordinal) || Path.IsPathRooted(value);

    private static string Absolute(string value)
        => value.Contains("://", StringComparison.Ordinal) ? value.TrimEnd('/') : Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string Join(string root, string relative)
    {
        var trimmed = relative.Trim('/', '\\');
        return root.Contains("://", StringComparison.Ordinal)
            ? root.TrimEnd('/') + "/" + trimmed.Replace('\\', '/')
            : Path.GetFullPath(Path.Combine(root, trimmed.Replace('/', Path.DirectorySeparatorChar))).TrimEnd(Path.DirectorySeparatorChar);
    }
}
