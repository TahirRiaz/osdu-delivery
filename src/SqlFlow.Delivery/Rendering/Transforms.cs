using System.Globalization;
using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Rendering;

/// <summary>
/// The closed transform vocabulary, interpreted (design.md section 4.2). Each transform turns a source value (or the
/// current row) into a raw value the renderer then coerces to the schema type. A transform that cannot produce a
/// value records a hold reason and omits the property.
/// </summary>
public static partial class Transforms
{
    /// <summary>Applies the property's transform. Returns the raw value, or null when omitted.</summary>
    public static object? Apply(MappingProperty property, SourceRow row, MappingRenderer renderer, string path, List<string> holds, out bool omit)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(holds);
        omit = false;
        var config = property.Config;
        var source = property.Source is null ? null : row.Get(property.Source);
        var text = SourceRow.Stringify(source);

        switch (property.Transform)
        {
            case MappingTransform.None:
                return source;

            case MappingTransform.Constant:
                return ExpandParameters(config.Value, renderer);

            case MappingTransform.Trim:
                return text?.Trim();

            case MappingTransform.Upper:
                return text?.ToUpperInvariant();

            case MappingTransform.Lower:
                return text?.ToLowerInvariant();

            case MappingTransform.Split:
                {
                    if (text is null)
                    {
                        return null;
                    }

                    var delimiter = config.Delimiter ?? " ";
                    var index = config.Index ?? 0;
                    var parts = delimiter == " "
                        ? Whitespace().Split(text.Trim())
                        : text.Split(delimiter, StringSplitOptions.None);
                    if (index < 0 || index >= parts.Length)
                    {
                        // A missing optional segment is an omission, not a hold: "123" has no unit part.
                        omit = true;
                        return null;
                    }

                    var part = parts[index].Trim();
                    return part.Length == 0 ? null : part;
                }

            case MappingTransform.Equals:
                if (text is null)
                {
                    return null;
                }

                return string.Equals(text.Trim(), config.Resolve?.Trim(), StringComparison.OrdinalIgnoreCase);

            case MappingTransform.Map:
                {
                    if (text is null)
                    {
                        return null;
                    }

                    if (config.Values.TryGetValue(text.Trim(), out var mapped))
                    {
                        return mapped;
                    }

                    if (config.Default is not null)
                    {
                        return config.Default.Length == 0 ? null : config.Default;
                    }

                    return Miss(config.OnMiss, $"{path}: value '{text}' is not in the map", holds, out omit);
                }

            case MappingTransform.Reference:
                {
                    if (text is null)
                    {
                        return null;
                    }

                    var typeName = config.Type ?? throw new FlowValidationException($"{path}: reference transform needs config.type.");
                    var type = renderer.References.Type(typeName);
                    if (type is null)
                    {
                        holds.Add($"{path}: reference type '{typeName}' is not in reference snapshot {renderer.Context.ReferenceSnapshotVersion}");
                        omit = true;
                        return null;
                    }

                    var value = text.Trim();
                    if (config.Delimiter is { } splitOn)
                    {
                        // A unit carried inside a compound value ("23.5 M"): split first, then resolve the part.
                        var parts = splitOn == " " ? Whitespace().Split(value) : value.Split(splitOn, StringSplitOptions.None);
                        var index = config.Index ?? 0;
                        if (index < 0 || index >= parts.Length || parts[index].Trim().Length == 0)
                        {
                            omit = true;
                            return null;
                        }

                        value = parts[index].Trim();
                    }

                    if (config.ValueMap.TryGetValue(value, out var normalized))
                    {
                        value = normalized;
                    }

                    // Already a well-formed OSDU reference: pass through untouched.
                    if (OsduId().IsMatch(value))
                    {
                        return value.EndsWith(':') ? value : value + ":";
                    }

                    var matchBy = config.MatchBy.Count > 0 ? config.MatchBy : ["id", "Code", "Name"];
                    foreach (var field in matchBy)
                    {
                        var hit = type.Match(field, value);
                        if (hit is not null)
                        {
                            return hit.Id.EndsWith(':') ? hit.Id : hit.Id + ":";
                        }
                    }

                    return Miss(config.OnMiss, $"{path}: no {typeName} matches '{text}' by {string.Join("/", matchBy)} in reference snapshot {renderer.Context.ReferenceSnapshotVersion}", holds, out omit);
                }

            case MappingTransform.DeliveredReference:
                {
                    var entityType = config.Type ?? throw new FlowValidationException($"{path}: deliveredReference transform needs config.type (the entity type).");
                    var columns = config.Keys.Count > 0 ? config.Keys : property.Source is null ? [] : [property.Source];
                    if (columns.Count == 0)
                    {
                        throw new FlowValidationException($"{path}: deliveredReference transform needs config.keys or a source column.");
                    }

                    var values = columns.Select(row.GetString).ToList();
                    if (values.Any(string.IsNullOrWhiteSpace))
                    {
                        return null;
                    }

                    var key = DeliveryKey.Derive(config.System ?? renderer.SourceSystem, values);
                    return TargetId.Reference(renderer.DataPartition, entityType, key.Value.ToString("N"));
                }

            case MappingTransform.Template:
                {
                    var format = config.Format ?? throw new FlowValidationException($"{path}: template transform needs config.format.");
                    var missing = new List<string>();
                    var rendered = Token().Replace(format, m =>
                    {
                        var name = m.Groups["name"].Value;
                        var v = name.StartsWith("param:", StringComparison.Ordinal)
                            ? renderer.ParameterValue(name[6..])
                            : row.GetString(name);
                        if (v is null)
                        {
                            missing.Add(name);
                        }

                        return v ?? string.Empty;
                    });
                    if (missing.Count > 0)
                    {
                        return Miss(config.OnMiss, $"{path}: template token(s) {string.Join(", ", missing)} are empty", holds, out omit);
                    }

                    return rendered;
                }

            case MappingTransform.DateTime:
                {
                    if (source is DateTimeOffset dto)
                    {
                        return Json.CanonicalJson.FormatDateTime(dto);
                    }

                    if (source is DateTime dt)
                    {
                        return SourceRow.Stringify(dt);
                    }

                    if (text is null)
                    {
                        return null;
                    }

                    var ok = config.InputFormat is { } fmt
                        ? DateTimeOffset.TryParseExact(text.Trim(), fmt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                        : DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out parsed);
                    if (!ok)
                    {
                        holds.Add($"{path}: '{text}' is not a date/time");
                        omit = true;
                        return null;
                    }

                    return Json.CanonicalJson.FormatDateTime(parsed);
                }

            default:
                throw new FlowValidationException($"{path}: transform '{property.Transform}' is not supported.");
        }
    }

    private static object? Miss(ReferenceMiss onMiss, string reason, List<string> holds, out bool omit)
    {
        omit = true;
        switch (onMiss)
        {
            case ReferenceMiss.Omit:
                return null;
            case ReferenceMiss.Error:
                throw new DeliveryException(reason);
            default:
                holds.Add(reason);
                return null;
        }
    }

    private static string? ExpandParameters(string? value, MappingRenderer renderer)
    {
        if (value is null)
        {
            return null;
        }

        return Token().Replace(value, m =>
        {
            var name = m.Groups["name"].Value;
            if (name.StartsWith("param:", StringComparison.Ordinal))
            {
                return renderer.ParameterValue(name[6..]) ?? m.Value;
            }

            return m.Value;
        });
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\{(?<name>[A-Za-z0-9_:\-\.]+)\}")]
    private static partial Regex Token();

    [GeneratedRegex(@"^[\w\-\.]+:[\w\-\.]+--[\w\-\.]+:[\w\-\.\:\%]+$")]
    private static partial Regex OsduId();
}
