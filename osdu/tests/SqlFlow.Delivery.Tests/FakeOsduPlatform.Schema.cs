using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The Schema service of the fake platform (openapi schema_service, <c>GET /schema/{id}</c>): the schemas it holds by id,
/// answered as they were put, a 404 for any other, and an answer of raw text where a test needs one the reader refuses.
/// </summary>
public sealed partial class FakeOsduPlatform
{
    private const string SchemaRoot = "/api/schema-service/v1/schema/";

    /// <summary>The schemas the service holds, by schema id (<c>osdu:wks:master-data--Wellbore:1.3.0</c>).</summary>
    public Dictionary<string, JsonObject> Schemas { get; } = new(StringComparer.Ordinal);

    /// <summary>Answers the service gives as raw text for an id, in place of a schema.</summary>
    public Dictionary<string, string> SchemaTexts { get; } = new(StringComparer.Ordinal);

    private HttpResponseMessage? SchemaServiceRoute(string method, string path)
    {
        if (!path.StartsWith(SchemaRoot, StringComparison.Ordinal) || method != "GET")
        {
            return null;
        }

        var id = Uri.UnescapeDataString(path[SchemaRoot.Length..]);
        if (SchemaTexts.TryGetValue(id, out var text))
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }

        return Schemas.TryGetValue(id, out var schema)
            ? Json(HttpStatusCode.OK, (JsonObject)schema.DeepClone())
            : Json(HttpStatusCode.NotFound, new JsonObject { ["code"] = 404, ["reason"] = "Not found", ["message"] = $"Schema {id} is not present" });
    }
}
