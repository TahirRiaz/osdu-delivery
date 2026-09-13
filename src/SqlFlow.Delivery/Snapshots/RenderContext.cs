using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Delivery.Hashing;
using ContentHash = SqlFlow.Delivery.Hashing.ContentHash;
using SqlFlow.Delivery.Json;

namespace SqlFlow.Delivery.Snapshots;

/// <summary>
/// The three non-source inputs pinned together for a render (design.md section 4.1), plus the mapping parameter
/// values the flow supplied (section 9.5). Recorded in the ledger against every document and part of every
/// content hash.
/// </summary>
public sealed partial record RenderContext
{
    /// <summary>Well-known parameter every mapping receives: the OSDU data partition ids are minted in.</summary>
    public const string DataPartitionParameter = "dataPartition";

    public required string MappingReference { get; init; }

    /// <summary>The cache the render read, named by its cache flow; null when the mapping reads no cache.</summary>
    public string? CacheName { get; init; }

    /// <summary>The version of the cache the render read; <c>none</c> when it read no cache.</summary>
    public required string CacheVersion { get; init; }

    public required string SchemaSnapshotVersion { get; init; }

    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// The partition every record id is minted in. It is the first segment of the id, and the storage service
    /// holds ids to <c>^[\w\-\.]+:[\w\-\.]+:[\w\-\.\:\%]+$</c> (openapi storage v2, Record.id), so a
    /// partition carrying anything else would mint an id the service refuses on every record of the run. Saying so
    /// here costs one check and turns a whole failed run into one legible message.
    /// </summary>
    public string DataPartition
    {
        get
        {
            if (!Parameters.TryGetValue(DataPartitionParameter, out var partition) || string.IsNullOrWhiteSpace(partition))
            {
                throw new FlowValidationException($"The mapping parameter '{DataPartitionParameter}' is required and must be supplied by the flow under render.parameters.");
            }

            if (!IdSegment().IsMatch(partition))
            {
                throw new FlowValidationException(
                    $"The mapping parameter '{DataPartitionParameter}' is '{partition}', which is not a valid OSDU id segment (letters, digits, underscore, hyphen and dot). Record ids are minted as {{partition}}:{{entityType}}:{{key}} and the storage service would refuse every one of them.");
            }

            return partition;
        }
    }

    [GeneratedRegex(@"^[\w\-\.]+$")]
    private static partial Regex IdSegment();

    /// <summary>Canonical JSON form, stored verbatim in the ledger and fed into hashes.</summary>
    public string Canonical()
    {
        var parameters = new JsonObject();
        foreach (var kv in Parameters.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            parameters[kv.Key] = kv.Value;
        }

        var node = new JsonObject
        {
            ["mapping"] = MappingReference,
            ["cacheVersion"] = CacheVersion,
            ["schema"] = SchemaSnapshotVersion,
            ["parameters"] = parameters,
        };
        if (CacheName is not null)
        {
            node["cache"] = CacheName;
        }

        return CanonicalJson.ToString(node);
    }

    public string Hash() => ContentHash.Of(Canonical());

    public static RenderContext Parse(string canonical)
    {
        var node = JsonNode.Parse(canonical) as JsonObject ?? throw new DeliveryException("Render context is not a JSON object.");
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node["parameters"] is JsonObject p)
        {
            foreach (var kv in p)
            {
                parameters[kv.Key] = kv.Value?.GetValue<string>() ?? string.Empty;
            }
        }

        return new RenderContext
        {
            MappingReference = node["mapping"]?.GetValue<string>() ?? string.Empty,
            CacheName = node["cache"]?.GetValue<string>(),
            CacheVersion = node["cacheVersion"]?.GetValue<string>() ?? string.Empty,
            SchemaSnapshotVersion = node["schema"]?.GetValue<string>() ?? string.Empty,
            Parameters = parameters,
        };
    }

    public override string ToString() => Canonical();
}
