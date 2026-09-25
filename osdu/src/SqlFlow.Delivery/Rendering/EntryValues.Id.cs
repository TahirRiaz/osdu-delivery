using System.Text;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Rendering;

internal static partial class EntryValues
{
    /// <summary>
    /// Builds the id an id modifier's template describes from the value the modifiers before it gave: each token's value,
    /// trimmed and percent-encoded (<see cref="IdValues.Encode"/>), in its place, then the whole checked against OSDU's id
    /// shape and what the template gives the variable (<see cref="IdValues.Problem"/>). A value that already is an OSDU id
    /// names its record and is written as it is, when the template reads it and builds ids of its entity type.
    /// </summary>
    /// <remarks>
    /// A token with no value leaves no id to write: a required entry holds the record, naming the token, and an optional one
    /// leaves the variable out; a half-built id is never written. A parameter the flow gives no value, a cached lookup the
    /// cache version cannot answer for certain, text that is not valid Unicode and an id the check refuses hold the record
    /// whatever the required flag says: each is a mistake in the mapping or its data, not a value that is simply absent.
    /// </remarks>
    private static bool TryBuildId(
        IdTemplate template, string? text, MappingEntry entry, SourceRow root, SourceRow? item, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages,
        out object? result)
    {
        result = null;
        var path = entry.Target.Text;
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        string id;
        if (template.ReadsValue && OsduId().IsMatch(value))
        {
            var named = value.Split(':')[1];
            if (template.EntityType is { } built && !string.Equals(built, named, StringComparison.Ordinal))
            {
                holds.Add($"{path}: '{value}' is already an OSDU id, of a {named} record, and the id modifier builds {built} ids ({template})");
                return false;
            }

            id = WithVersionSeparator(value);
        }
        else
        {
            var written = new StringBuilder();
            var missing = new List<string>();
            foreach (var token in template.Tokens)
            {
                string? part;
                switch (token.Kind)
                {
                    case IdTokenKind.Text:
                        written.Append(token.Text);
                        continue;
                    case IdTokenKind.Value:
                        part = value;
                        break;
                    case IdTokenKind.Dataset:
                        part = SourceRow.Stringify(Read(token.Column!, root, item))?.Trim();
                        break;
                    case IdTokenKind.Parameter:
                        part = renderer.ParameterValue(token.Parameter!)?.Trim();
                        if (string.IsNullOrEmpty(part))
                        {
                            holds.Add($"{path}: the id {template} reads {token}, which the flow supplies no value for");
                            return false;
                        }

                        break;
                    default:
                        if (!TryIdCacheValue(token, value, path, renderer, holds, usages, out part))
                        {
                            return false;
                        }

                        break;
                }

                if (string.IsNullOrEmpty(part))
                {
                    missing.Add(token.Text);
                    continue;
                }

                if (IdValues.Encode(part) is not { } encoded)
                {
                    holds.Add($"{path}: {token} holds text that is not valid Unicode, which no id can carry");
                    return false;
                }

                written.Append(encoded);
            }

            if (missing.Count > 0)
            {
                if (entry.Required)
                {
                    var tokens = missing.Distinct(StringComparer.Ordinal).ToList();
                    holds.Add($"{path}: the id {template} cannot be built, since {string.Join(", ", tokens)} {(tokens.Count == 1 ? "has" : "have")} no value, and the entry is required");
                    return false;
                }

                return true;
            }

            id = written.ToString();
        }

        if (IdValues.Problem(id, renderer.Schema.Resolve(entry.Target.SchemaPath)) is { } problem)
        {
            holds.Add($"{path}: the id {template} gives an id that cannot be written: {problem}");
            return false;
        }

        result = id;
        return true;
    }

    /// <summary>
    /// What a cache token gives for the entry's value: the field it names, of the row of the lookup table whose key is the
    /// value, matched as a replace matches it (<see cref="ReplaceTables.Find"/>). Null, with true, when no row is listed
    /// under the value or the row holds nothing there; every row read and every key not listed is recorded among the
    /// render's cache usages, so a later version of the table tags exactly the records it changes.
    /// </summary>
    private static bool TryIdCacheValue(
        IdToken token, string value, string path, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages, out string? part)
    {
        part = null;
        var version = CacheLabel(renderer.Context);
        if (renderer.References.Type(token.CacheType!) is not { } type)
        {
            holds.Add($"{path}: the id reads {token}, and {version} holds no type '{token.CacheType}'");
            return false;
        }

        if (type.Key is not { } key)
        {
            holds.Add(
                $"{path}: the id reads {token}, and {type.Name} holds OSDU records ({type.EntityType}) in {version}, which have no key to look the value up by; a cache token reads a lookup table");
            return false;
        }

        var field = ReferenceField.Normalize(token.CacheField!);
        var lookup = ReplaceTables.Find(type, key, field, value);
        switch (lookup.Kind)
        {
            case ReplaceLookupKind.Ambiguous:
                holds.Add(
                    $"{path}: '{value}' matches the {type.Name} rows {string.Join(", ", lookup.Rows.Select(row => $"'{row.Id}'"))} only once case is ignored in {version}, and they hold different values at '{field}'; make the incoming value exact");
                return false;
            case ReplaceLookupKind.NotListed:
                if (value.Length <= LookupKeys.MaxLength)
                {
                    usages.Add(new CacheUsage(type.Name, CacheUsage.ListingKey(value), field, value, CacheUsageKind.Unlisted));
                }

                return true;
        }

        foreach (var row in lookup.Rows)
        {
            usages.Add(new CacheUsage(type.Name, row.Id, ReferenceField.Normalize(key), value, CacheUsageKind.Match));
        }

        if (lookup.Value is not { } given || given.Node is System.Text.Json.Nodes.JsonArray { Count: 0 })
        {
            foreach (var row in lookup.Rows)
            {
                usages.Add(new CacheUsage(type.Name, row.Id, field, string.Empty, CacheUsageKind.Empty));
            }

            return true;
        }

        if (ReplaceTables.SingleText(given) is not { } single)
        {
            holds.Add(
                $"{path}: {type.Name} '{lookup.Rows[0].Id}' holds several values or an object at '{field}' in {version} ({Clip(given.Text)}), and {token} writes one value into the id");
            return false;
        }

        foreach (var row in lookup.Rows)
        {
            usages.Add(new CacheUsage(type.Name, row.Id, field, given.Text, CacheUsageKind.Value));
        }

        part = single.Trim();
        return true;
    }
}
