using System.Globalization;
using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Rendering;

/// <summary>
/// The closed transform vocabulary, interpreted (design.md section 4.2). Each transform turns a source value (or the
/// current row) into a raw value the renderer then coerces to the schema type. A transform that cannot produce a
/// value records a hold reason and omits the property.
/// </summary>
public static partial class Transforms
{
    private static readonly string[] DefaultMatchBy = ["id", "Code", "Name"];

    /// <summary>Applies the property's transform. Returns the raw value, or null when omitted.</summary>
    public static object? Apply(
        MappingProperty property, SourceRow row, MappingRenderer renderer, string path, List<string> holds, List<CacheUsage>? usages, out bool omit)
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

                    var resolved = ResolveCached(config, renderer, text, path, holds, usages, out omit);
                    if (resolved.Failed)
                    {
                        return null;
                    }

                    if (resolved.Item is not null)
                    {
                        Record(usages, resolved, "id", resolved.Item.Id, CacheUsageKind.Value);
                    }

                    // An OSDU relationship carries the id with its version separator, matched or passed through.
                    return resolved.PassedThrough ?? WithVersionSeparator(resolved.Item!.Id);
                }

            case MappingTransform.Lookup:
                {
                    if (text is null)
                    {
                        return null;
                    }

                    var resolved = ResolveCached(config, renderer, text, path, holds, usages, out omit);
                    if (resolved.Failed)
                    {
                        return null;
                    }

                    var select = string.IsNullOrWhiteSpace(config.Select) ? "id" : config.Select!;
                    if (resolved.Item is null)
                    {
                        // The source already carried an OSDU id, so only the id itself can be answered from it.
                        return ReferenceField.IsId(select)
                            ? resolved.PassedThrough
                            : Miss(config.OnMiss, $"{path}: '{text}' is already an OSDU id, so '{select}' cannot be read from the cache", holds, out omit);
                    }

                    if (resolved.Type!.Value(resolved.Item, select) is not { } cached)
                    {
                        return Miss(
                            config.OnMiss,
                            $"{path}: {resolved.TypeName} '{resolved.Item.Id}' caches nothing at '{select}' in reference snapshot {renderer.Context.ReferenceSnapshotVersion}. Cached: {string.Join(", ", CachedNames(renderer, resolved.TypeName))}",
                            holds,
                            out omit);
                    }

                    Record(usages, resolved, select, cached.Text, CacheUsageKind.Value);
                    return cached.Node.DeepClone();
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

    /// <summary>
    /// The one path from a source value to a cached record: normalise it (split, map), pass a well-formed OSDU id
    /// through, then match it against the cached type by each field in turn. Both the reference transform (which
    /// wants the id) and the lookup transform (which wants a cached value) resolve through here.
    /// </summary>
    private static CachedHit ResolveCached(
        TransformConfig config, MappingRenderer renderer, string text, string path, List<string> holds, List<CacheUsage>? usages, out bool omit)
    {
        omit = false;
        var typeName = config.Type ?? throw new FlowValidationException($"{path}: the reference and lookup transforms need config.type.");
        var type = renderer.References.Type(typeName);
        if (type is null)
        {
            holds.Add($"{path}: reference type '{typeName}' is not in reference snapshot {renderer.Context.ReferenceSnapshotVersion}");
            omit = true;
            return CachedHit.Missed(typeName);
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
                return CachedHit.Missed(typeName);
            }

            value = parts[index].Trim();
        }

        if (config.ValueMap.TryGetValue(value, out var normalized))
        {
            value = normalized;
        }

        // Already a well-formed OSDU reference: it identifies the record without a lookup.
        if (OsduId().IsMatch(value))
        {
            var byId = type.Match("id", value.TrimEnd(':'));
            return byId is not null
                ? new CachedHit(typeName, type, byId, null)
                : new CachedHit(typeName, type, null, WithVersionSeparator(value));
        }

        var matchBy = config.MatchBy.Count > 0 ? config.MatchBy : DefaultMatchBy;
        (string Field, ReferenceMatch Found)? undecided = null;
        foreach (var field in matchBy)
        {
            var found = type.Find(field, value);
            if (found.Item is { } hit)
            {
                // The value that matched is a dependency too: if the cache stops holding it, this record
                // stops resolving, which is a change worth catching before a run holds it.
                usages?.Add(new CacheUsage(typeName, hit.Id, ReferenceField.Normalize(field), value, CacheUsageKind.Match));
                return new CachedHit(typeName, type, hit, null);
            }

            if (found.IsCaseAmbiguous)
            {
                undecided ??= (field, found);
            }
        }

        if (undecided is { } choice)
        {
            // Codes that differ only by case are different records (ft the foot, fT the femtotesla). Taking the first
            // would deliver the wrong one without a trace, so the value stays unresolved and the reason names both.
            var candidates = string.Join(", ", choice.Found.CaseVariants.Select(c => c.Id));
            Miss(config.OnMiss, $"{path}: '{text}' matches {choice.Found.CaseVariants.Count} {typeName} records by {ReferenceField.Normalize(choice.Field)} only when case is ignored ({candidates}); map it to the exact value with valueMap. Reference snapshot {renderer.Context.ReferenceSnapshotVersion}", holds, out omit);
            return CachedHit.Missed(typeName);
        }

        Miss(config.OnMiss, $"{path}: no {typeName} matches '{text}' by {string.Join("/", matchBy)} in reference snapshot {renderer.Context.ReferenceSnapshotVersion}", holds, out omit);
        return CachedHit.Missed(typeName);
    }

    /// <summary>Records one dependency of the render on the cache.</summary>
    private static void Record(List<CacheUsage>? usages, CachedHit resolved, string path, string value, CacheUsageKind kind)
    {
        if (usages is not null && resolved.Item is not null)
        {
            usages.Add(new CacheUsage(resolved.TypeName, resolved.Item.Id, ReferenceField.Normalize(path), value, kind));
        }
    }

    private static IReadOnlyList<string> CachedNames(MappingRenderer renderer, string typeName)
    {
        var type = renderer.References.Type(typeName);
        return type is null ? ["id"] : type.FieldNames.Prepend("id").ToList();
    }

    private static string WithVersionSeparator(string id) => id.EndsWith(':') ? id : id + ":";

    /// <summary>What a resolve produced: the cached item, an id the source already carried, or nothing.</summary>
    private readonly record struct CachedHit(string TypeName, ReferenceType? Type, ReferenceItem? Item, string? PassedThrough)
    {
        public bool Failed => Item is null && PassedThrough is null;

        public static CachedHit Missed(string typeName) => new(typeName, null, null, null);
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
