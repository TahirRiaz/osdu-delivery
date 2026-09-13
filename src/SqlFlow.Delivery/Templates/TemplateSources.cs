using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Templates;

/// <summary>A search of the schemas OSDU publishes (openapi schema_service, <c>GET /schema</c>).</summary>
public sealed record OsduSchemaQuery
{
    /// <summary>The most schemas one search page returns (the service caps <c>limit</c> at 100).</summary>
    public const int MaxLimit = 100;

    public string? Authority { get; init; }

    public string? Source { get; init; }

    public string? EntityType { get; init; }

    /// <summary>PUBLISHED, OBSOLETE or DEVELOPMENT; null asks for the service's default (PUBLISHED).</summary>
    public string? Status { get; init; }

    public bool LatestVersion { get; init; } = true;

    public int Limit { get; init; } = MaxLimit;

    public int Offset { get; init; }
}

/// <summary>One schema OSDU publishes, as its search lists it.</summary>
public sealed record OsduSchemaInfo(
    string Kind, string Authority, string Source, string EntityType, string Version, string? Status, string? Scope, DateTime? CreatedUtc, string? CreatedBy);

/// <summary>A page of schema search results.</summary>
public sealed record OsduSchemaSearch(IReadOnlyList<OsduSchemaInfo> Schemas, int Offset, int Count, int TotalCount);

/// <summary>
/// Where a template's schema comes from: OSDU's schema service, or a bundled schema file. Both produce the same thing, a
/// <see cref="SchemaSnapshot"/> whose every <c>$ref</c> is resolved into its definitions, which the template store saves.
/// </summary>
public static class TemplateSources
{
    /// <summary>The schema service (openapi schema_service v1).</summary>
    public const string SchemaPath = "/api/schema-service/v1/schema";

    /// <summary>Searches OSDU's schemas (openapi schema_service, <c>GET /schema</c>).</summary>
    public static async Task<OsduSchemaSearch> SearchAsync(OsduConnection osdu, OsduSchemaQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(osdu);
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > OsduSchemaQuery.MaxLimit)
        {
            throw new DeliveryException($"A schema search returns between 1 and {OsduSchemaQuery.MaxLimit} schemas per page; {query.Limit} was asked for.");
        }

        if (query.Offset < 0)
        {
            throw new DeliveryException("A schema search offset cannot be negative.");
        }

        var parameters = new List<(string Name, string Value)>();
        Add(parameters, "authority", query.Authority);
        Add(parameters, "source", query.Source);
        Add(parameters, "entityType", query.EntityType);
        Add(parameters, "status", query.Status);
        parameters.Add(("latestVersion", query.LatestVersion ? "true" : "false"));
        parameters.Add(("limit", query.Limit.ToString(CultureInfo.InvariantCulture)));
        parameters.Add(("offset", query.Offset.ToString(CultureInfo.InvariantCulture)));

        var path = new StringBuilder(SchemaPath);
        for (var i = 0; i < parameters.Count; i++)
        {
            path.Append(i == 0 ? '?' : '&').Append(parameters[i].Name).Append('=').Append(Uri.EscapeDataString(parameters[i].Value));
        }

        var response = await osdu.GetJsonAsync(path.ToString(), ct).ConfigureAwait(false);
        var schemas = new List<OsduSchemaInfo>();
        foreach (var info in (response["schemaInfos"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (info["schemaIdentity"] is not JsonObject identity)
            {
                continue;
            }

            var authority = Text(identity, "authority");
            var entityType = Text(identity, "entityType");
            if (authority is null || entityType is null)
            {
                continue;
            }

            var source = Text(identity, "source") ?? string.Empty;
            var version = string.Create(
                CultureInfo.InvariantCulture,
                $"{Number(identity, "schemaVersionMajor")}.{Number(identity, "schemaVersionMinor")}.{Number(identity, "schemaVersionPatch")}");
            var kind = Text(identity, "id") ?? $"{authority}:{source}:{entityType}:{version}";
            schemas.Add(new OsduSchemaInfo(
                kind, authority, source, entityType, version, Text(info, "status"), Text(info, "scope"), Instant(Text(info, "dateCreated")), Text(info, "createdBy")));
        }

        return new OsduSchemaSearch(
            schemas,
            (int)Number(response, "offset", query.Offset),
            (int)Number(response, "count", schemas.Count),
            (int)Number(response, "totalCount", schemas.Count));
    }

    /// <summary>Fetches a kind's schema from OSDU (openapi schema_service, <c>GET /schema/{id}</c>) and every schema it references.</summary>
    public static async Task<SchemaSnapshot> FetchAsync(OsduConnection osdu, string kind, TimeProvider time, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(osdu);
        ArgumentNullException.ThrowIfNull(time);
        RequireKind(kind);
        var root = await osdu.GetJsonAsync($"{SchemaPath}/{Http.UrlPath.EscapeSegment(kind)}", ct).ConfigureAwait(false);
        var bundled = await SchemaBundler.BundleAsync(root, kind, async (reference, _, token) =>
        {
            var id = reference.Replace("#/definitions/", string.Empty, StringComparison.Ordinal);
            var schema = await osdu.GetJsonAsync($"{SchemaPath}/{Http.UrlPath.EscapeSegment(id)}", token).ConfigureAwait(false);
            return (id, schema);
        }, ct).ConfigureAwait(false);
        return Validated(kind, bundled, time.GetUtcNow(), osdu.Endpoint);
    }

    /// <summary>Bundles a kind's schema from a local checkout of the OSDU data definitions.</summary>
    public static async Task<SchemaSnapshot> FromDirectoryAsync(string dataRoot, string kind, TimeProvider time, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(time);
        RequireKind(kind);
        var file = SchemaBundler.LocateKindFile(dataRoot, kind);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false)) as JsonObject
            ?? throw new DeliveryException($"Schema file '{file}' is not a JSON object.");
        var bundled = await SchemaBundler.BundleAsync(root, file, SchemaBundler.DirectoryResolver(dataRoot), ct).ConfigureAwait(false);
        return Validated(kind, bundled, time.GetUtcNow(), file);
    }

    /// <summary>
    /// Reads a bundled schema (every <c>$ref</c> under <c>#/definitions/</c> and present), as a fetch stores it and as
    /// the import accepts it. <paramref name="where"/> names the file or upload in errors.
    /// </summary>
    public static SchemaSnapshot FromBundledJson(string json, string kind, DateTimeOffset capturedUtc, string where)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(where);
        RequireKind(kind);
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject ?? throw new DeliveryException($"{where}: the schema is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new DeliveryException($"{where}: the schema is not valid JSON ({ex.Message}).", ex);
        }

        return Validated(kind, root, capturedUtc, where);
    }

    private static SchemaSnapshot Validated(string kind, JsonObject bundled, DateTimeOffset capturedUtc, string where)
    {
        if (bundled["x-osdu-schema-source"] is JsonValue declared && declared.TryGetValue<string>(out var source)
            && !string.IsNullOrWhiteSpace(source) && !string.Equals(source, kind, StringComparison.Ordinal))
        {
            throw new DeliveryException($"{where}: the schema describes '{source}', not '{kind}'.");
        }

        var definitions = bundled["definitions"] as JsonObject ?? bundled["$defs"] as JsonObject;
        foreach (var reference in References(bundled))
        {
            var name = reference.StartsWith("#/definitions/", StringComparison.Ordinal)
                ? reference["#/definitions/".Length..]
                : reference.StartsWith("#/$defs/", StringComparison.Ordinal) ? reference["#/$defs/".Length..] : null;
            if (name is null)
            {
                throw new DeliveryException(
                    $"{where}: the schema refers to '{reference}' outside itself. A template is saved from a bundled schema, where every reference is resolved into its definitions.");
            }

            if (definitions?[name] is not JsonObject)
            {
                throw new DeliveryException($"{where}: the schema refers to definition '{name}', which it does not contain.");
            }
        }

        var snapshot = new SchemaSnapshot(kind, bundled, capturedUtc);
        if (snapshot.EffectiveRootObject["properties"] is not JsonObject properties || properties["data"] is null)
        {
            throw new DeliveryException($"{where}: the schema declares no 'data' property, so it does not describe an OSDU record.");
        }

        return snapshot;
    }

    private static void RequireKind(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (!FlowMapper.IsRecordKind(kind))
        {
            throw new FlowValidationException($"Kind '{kind}' must be 'authority:source:entityType:major.minor.patch'.");
        }
    }

    private static IEnumerable<string> References(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["$ref"] is JsonValue value && value.TryGetValue<string>(out var reference))
                {
                    yield return reference;
                }

                foreach (var (_, child) in obj)
                {
                    foreach (var inner in References(child))
                    {
                        yield return inner;
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    foreach (var inner in References(item))
                    {
                        yield return inner;
                    }
                }

                break;
        }
    }

    private static void Add(List<(string, string)> parameters, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parameters.Add((name, value.Trim()));
        }
    }

    private static string? Text(JsonObject node, string key)
        => node[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static long Number(JsonObject node, string key, long fallback = 0)
        => node[key] is JsonValue value && value.TryGetValue<long>(out var number) ? number : fallback;

    private static DateTime? Instant(string? text)
        => text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed.UtcDateTime : null;
}
