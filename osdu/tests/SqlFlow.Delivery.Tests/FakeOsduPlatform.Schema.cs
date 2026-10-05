using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The Schema service of the fake platform (openapi schema_service): <c>GET /schema/{id}</c> answers the schemas it holds
/// as they were put, a 404 for any other, and an answer of raw text or a failure where a test needs one; <c>GET /schema</c>
/// lists them as <c>SchemaInfo</c>, one status and scope at a time (published and internal when the listing names neither,
/// as the specification defaults them), a page of <c>limit</c> from <c>offset</c>, in id order.
/// </summary>
public sealed partial class FakeOsduPlatform
{
    private const string SchemaList = "/api/schema-service/v1/schema";
    private const string SchemaRoot = SchemaList + "/";

    /// <summary>The schemas the service holds, by schema id (<c>osdu:wks:master-data--Wellbore:1.3.0</c>).</summary>
    public Dictionary<string, JsonObject> Schemas { get; } = new(StringComparer.Ordinal);

    /// <summary>Answers the service gives as raw text for an id, in place of a schema.</summary>
    public Dictionary<string, string> SchemaTexts { get; } = new(StringComparer.Ordinal);

    /// <summary>The status and scope each schema is listed in; a schema not named here is published and shared.</summary>
    public Dictionary<string, (string Status, string Scope)> SchemaInfos { get; } = new(StringComparer.Ordinal);

    /// <summary>Reads of a schema by id answered with a failure of this status instead.</summary>
    public Dictionary<string, HttpStatusCode> SchemaFailures { get; } = new(StringComparer.Ordinal);

    /// <summary>Listings answered with a refusal of this status instead, by <c>STATUS SCOPE</c> (<c>PUBLISHED SHARED</c>).</summary>
    public Dictionary<string, HttpStatusCode> SchemaListingRefusals { get; } = new(StringComparer.Ordinal);

    /// <summary>The reads of one schema by id the service answered, of a schema it holds or not.</summary>
    public int SchemaReads(string id)
    {
        lock (_gate)
        {
            return Calls.Count(c => c.Method == HttpMethod.Get && Uri.UnescapeDataString(c.Uri.AbsolutePath) == SchemaRoot + id);
        }
    }

    /// <summary>The listings of the service, one per page asked.</summary>
    public IReadOnlyList<Uri> SchemaListings()
    {
        lock (_gate)
        {
            return Calls.Where(c => c.Method == HttpMethod.Get && c.Uri.AbsolutePath == SchemaList).Select(c => c.Uri).ToList();
        }
    }

    private HttpResponseMessage? SchemaServiceRoute(string method, string path, Uri uri)
    {
        if (method != "GET")
        {
            return null;
        }

        if (path == SchemaList)
        {
            return SchemaListing(uri);
        }

        if (!path.StartsWith(SchemaRoot, StringComparison.Ordinal))
        {
            return null;
        }

        var id = Uri.UnescapeDataString(path[SchemaRoot.Length..]);
        if (SchemaFailures.TryGetValue(id, out var failure))
        {
            return Json(failure, new JsonObject { ["code"] = (int)failure, ["reason"] = "Failed", ["message"] = $"Schema {id} could not be read" });
        }

        if (SchemaTexts.TryGetValue(id, out var text))
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }

        return Schemas.TryGetValue(id, out var schema)
            ? Json(HttpStatusCode.OK, (JsonObject)schema.DeepClone())
            : Json(HttpStatusCode.NotFound, new JsonObject { ["code"] = 404, ["reason"] = "Not found", ["message"] = $"Schema {id} is not present" });
    }

    private HttpResponseMessage SchemaListing(Uri uri)
    {
        var query = HttpUtility.ParseQueryString(uri.Query);
        var status = (query["status"] ?? "PUBLISHED").ToUpperInvariant();
        var scope = (query["scope"] ?? "INTERNAL").ToUpperInvariant();
        if (SchemaListingRefusals.TryGetValue($"{status} {scope}", out var refusal))
        {
            return Json(refusal, new JsonObject { ["code"] = (int)refusal, ["reason"] = "Refused", ["message"] = $"Listing {status} {scope} schemas is refused" });
        }

        var offset = int.Parse(query["offset"] ?? "0", CultureInfo.InvariantCulture);
        var limit = int.Parse(query["limit"] ?? "100", CultureInfo.InvariantCulture);
        var held = Schemas.Keys.Concat(SchemaTexts.Keys).Concat(SchemaFailures.Keys).Distinct(StringComparer.Ordinal)
            .Where(id => SchemaInfos.TryGetValue(id, out var info) ? info.Status == status && info.Scope == scope : status == "PUBLISHED" && scope == "SHARED")
            .Order(StringComparer.Ordinal)
            .ToList();
        var page = held.Skip(offset).Take(limit).ToList();
        var infos = new JsonArray();
        foreach (var id in page)
        {
            var parts = id.Split(':');
            var version = parts[3].Split('.').Select(p => long.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            infos.Add(new JsonObject
            {
                ["schemaIdentity"] = new JsonObject
                {
                    ["authority"] = parts[0],
                    ["source"] = parts[1],
                    ["entityType"] = parts[2],
                    ["schemaVersionMajor"] = version[0],
                    ["schemaVersionMinor"] = version[1],
                    ["schemaVersionPatch"] = version[2],
                    ["id"] = id,
                },
                ["createdBy"] = "fake@example.com",
                ["dateCreated"] = "2026-01-01T00:00:00Z",
                ["status"] = status,
                ["scope"] = scope,
            });
        }

        return Json(HttpStatusCode.OK, new JsonObject { ["schemaInfos"] = infos, ["offset"] = offset, ["count"] = page.Count, ["totalCount"] = held.Count });
    }
}
