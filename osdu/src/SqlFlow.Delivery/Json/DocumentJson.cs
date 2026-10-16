using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SqlFlow.Delivery.Json;

/// <summary>
/// JSON written as the document was authored: object keys stay in the order they were written, and nulls and numbers are
/// kept as they are. For documents people read back, such as a template's schema, where <see cref="CanonicalJson"/> would
/// sort the keys. Hashes are still taken over the canonical form, so the order here never moves a version.
/// </summary>
public static class DocumentJson
{
    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions IndentedOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The node without whitespace, in its authored key order.</summary>
    public static string Compact(JsonNode? node) => node?.ToJsonString(CompactOptions) ?? "null";

    /// <summary>The node indented for reading, in its authored key order.</summary>
    public static string Indented(JsonNode? node) => node?.ToJsonString(IndentedOptions) ?? "null";
}
