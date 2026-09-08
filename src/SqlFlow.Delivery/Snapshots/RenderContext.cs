using System.Text.Json.Nodes;
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
public sealed record RenderContext
{
    /// <summary>Well-known parameter every mapping receives: the OSDU data partition ids are minted in.</summary>
    public const string DataPartitionParameter = "dataPartition";

    public required string MappingReference { get; init; }

    public required string ReferenceSnapshotVersion { get; init; }

    public required string SchemaSnapshotVersion { get; init; }

    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public string DataPartition => Parameters.TryGetValue(DataPartitionParameter, out var p) && !string.IsNullOrWhiteSpace(p)
        ? p
        : throw new FlowValidationException($"The mapping parameter '{DataPartitionParameter}' is required and must be supplied by the flow under render.parameters.");

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
            ["references"] = ReferenceSnapshotVersion,
            ["schema"] = SchemaSnapshotVersion,
            ["parameters"] = parameters,
        };
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
            ReferenceSnapshotVersion = node["references"]?.GetValue<string>() ?? string.Empty,
            SchemaSnapshotVersion = node["schema"]?.GetValue<string>() ?? string.Empty,
            Parameters = parameters,
        };
    }

    public override string ToString() => Canonical();
}
