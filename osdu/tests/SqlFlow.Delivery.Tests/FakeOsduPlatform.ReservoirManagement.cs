using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Web;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The Reservoir Management DDMS as osdu/specs/reservoir-management-ddms/INTEGRATION.md reads its code: header rows that
/// only the list call creates, from the first 100 records Search serves and only for parents with a pool row; child rows
/// posted one per call under a key from a sequence, their composite parents checked; ids taken from the query while the
/// path segment is ignored; and a record write and a delete that are never to be called.
/// </summary>
public sealed partial class FakeOsduPlatform
{
    /// <summary>Where the flows under test find the Reservoir Management DDMS (its project names no prefix).</summary>
    public const string ReservoirManagementRoot = "/api/rm-ddms";

    private long _rmSequence = 100;
    private int _rmPosts;

    /// <summary>The service's copies of header records, by collection segment and record id.</summary>
    public Dictionary<string, Dictionary<string, JsonObject>> RmHeaders { get; } = new(StringComparer.Ordinal);

    /// <summary>The rows of every table, by table segment and key.</summary>
    public Dictionary<string, SortedDictionary<long, JsonObject>> RmRows { get; } = new(StringComparer.Ordinal);

    /// <summary>The master records the service's database has a pool row for; a copy is taken in only under one of them.</summary>
    public HashSet<string> RmPools { get; } = new(StringComparer.Ordinal) { "dev:master-data--Reservoir:r1:" };

    /// <summary>Whether the forecast base 0, which the list call gives every forecast it takes in, exists.</summary>
    public bool RmForecastBaseZero { get; set; } = true;

    /// <summary>Which records Search serves to the service's list call; every record Storage holds when null.</summary>
    public Func<string, bool>? RmSearchServes { get; set; }

    /// <summary>The most records the list call takes from Search.</summary>
    public int RmSearchLimit { get; set; } = 100;

    /// <summary>Posts, counted from 1, whose row is inserted and whose answer is lost (502).</summary>
    public HashSet<int> RmLostPosts { get; } = [];

    /// <summary>How many list calls the service answered.</summary>
    public int RmLists { get; private set; }

    private HttpResponseMessage? ReservoirManagementRoute(string method, string path, Uri uri, string? body)
    {
        if (!path.StartsWith(ReservoirManagementRoot + "/", StringComparison.Ordinal))
        {
            return null;
        }

        var rest = path[ReservoirManagementRoot.Length..];
        if (rest == "/" && method == "GET")
        {
            return Json(HttpStatusCode.OK, JsonValue.Create("Success"));
        }

        var parts = rest.Split('/');
        if (parts.Length < 3 || parts[1] != "ddms")
        {
            return RmDetail(HttpStatusCode.NotFound, "Not Found");
        }

        var segment = parts[2];
        var query = HttpUtility.ParseQueryString(uri.Query);
        if (ReservoirManagementTables.Header(segment) is { } header)
        {
            return RmHeader(method, header, parts, query);
        }

        return ReservoirManagementTables.Table(segment) is { } table
            ? RmTable(method, table, parts, query, body)
            : RmDetail(HttpStatusCode.NotFound, "Not Found");
    }

    private HttpResponseMessage RmHeader(string method, ReservoirManagementHeader header, string[] parts, System.Collections.Specialized.NameValueCollection query)
    {
        if (query["data_partition_id"] is null)
        {
            return RmDetail((HttpStatusCode)422, "field required: data_partition_id");
        }

        var copies = Copies(header.Segment);
        switch (method, parts.Length)
        {
            // A1: the list call, which takes in what Search serves.
            case ("GET", 4) when parts[3].Length == 0:
                {
                    RmLists++;
                    var parentType = query["parent_type"];
                    if (parentType is not ("Reservoir" or "Segment" or "Sector"))
                    {
                        return RmDetail((HttpStatusCode)422, "This master type does not exists, try with Reservoir, Segment or Sector");
                    }

                    var hits = Records.Values
                        .Where(r => r["kind"]?.GetValue<string>() == header.Kind && !Removed.Contains(r["id"]!.GetValue<string>()))
                        .Select(r => r["id"]!.GetValue<string>())
                        .Where(id => RmSearchServes?.Invoke(id) ?? true)
                        .Take(RmSearchLimit)
                        .ToList();
                    foreach (var id in hits.Where(id => !copies.ContainsKey(id)))
                    {
                        if (Records[id]["data"]?["ParentObjectID"]?.GetValue<string>() is not { } parent)
                        {
                            continue;
                        }

                        if (header.Segment == "kr-synthesis")
                        {
                            return RmText(HttpStatusCode.InternalServerError, "Internal Server Error");
                        }

                        if (!RmPools.Contains(parent) || (header.Segment == "forecast" && !RmForecastBaseZero))
                        {
                            return RmText(HttpStatusCode.InternalServerError, "Internal Server Error");
                        }

                        var copy = new JsonObject { ["id"] = id, ["parent_object_id"] = parent, ["name"] = "default_name", ["dt"] = "default_date" };
                        if (header.Segment == "forecast")
                        {
                            copy["id_forecast_base"] = 0;
                        }

                        copies[id] = copy;
                    }

                    var listed = copies.Values
                        .Where(c => hits.Contains(c["id"]!.GetValue<string>()) && c["parent_object_id"]!.GetValue<string>().Contains(parentType + ":", StringComparison.Ordinal))
                        .Select(c => (JsonNode?)c.DeepClone())
                        .ToArray();
                    return listed.Length == 0
                        ? Json(HttpStatusCode.NotFound, new JsonObject { ["detail"] = new JsonObject { ["message"] = $"No {parentType} objects had been found." } })
                        : Json(HttpStatusCode.OK, new JsonArray(listed));
                }

            // A2: the id is the query's; the segment is ignored.
            case ("GET", 4):
                {
                    var id = query["catalog_entity_id"] ?? string.Empty;
                    if (!Regex.IsMatch(id, $@"^[\w\-\.]+:{Regex.Escape(header.Kind.Split(':')[2])}:[\w\-\.\:\%]+$"))
                    {
                        return RmDetail((HttpStatusCode)422, "string does not match regex");
                    }

                    if (!Records.ContainsKey(id) || Removed.Contains(id))
                    {
                        return Error(HttpStatusCode.NotFound, "Record not found");
                    }

                    return copies.TryGetValue(id, out var copy)
                        ? Json(HttpStatusCode.OK, copy.DeepClone())
                        : RmDetail(HttpStatusCode.NotFound, $"The record {id} doesn't exist");
                }

            // A4 and A5 are never to be called: the first writes records without their ids, the second purges.
            case ("PUT", 3):
            case ("DELETE", 4):
                return RmText(HttpStatusCode.InternalServerError, "A4 and A5 are not to be called by a delivery");
        }

        return RmDetail(HttpStatusCode.MethodNotAllowed, "Method Not Allowed");
    }

    private HttpResponseMessage RmTable(string method, ReservoirManagementTable table, string[] parts, System.Collections.Specialized.NameValueCollection query, string? body)
    {
        var rows = Rows(table.Segment);
        switch (method, parts.Length)
        {
            // B3: the rows under a parent; the segment is ignored.
            case ("GET", 5) when parts[3] == "header-entity":
                {
                    var parent = query["header_entity_id"] ?? string.Empty;
                    var under = rows.Values.Where(r => Plain(r[table.ParentColumn]) == parent).Select(r => (JsonNode?)r.DeepClone()).ToArray();
                    return Json(HttpStatusCode.OK, new JsonArray(under));
                }

            // B4: one row, keyed from the sequence.
            case ("POST", 3):
                {
                    var post = ++_rmPosts;
                    JsonObject row;
                    try
                    {
                        row = JsonNode.Parse(body ?? string.Empty) as JsonObject ?? throw new JsonException("not an object");
                    }
                    catch (JsonException)
                    {
                        return RmText(HttpStatusCode.InternalServerError, "Internal Server Error");
                    }

                    var known = table.Columns.Keys.Concat(table.RouteColumns).ToHashSet(StringComparer.Ordinal);
                    if (row.Any(p => !known.Contains(p.Key)) || row[table.ParentColumn] is null || row.ContainsKey(table.KeyColumn))
                    {
                        return RmText(HttpStatusCode.InternalServerError, "Internal Server Error");
                    }

                    var parentKey = Plain(row[table.ParentColumn]);
                    JsonObject? header;
                    if (table.Parent is null)
                    {
                        Copies(table.Header).TryGetValue(parentKey, out header);
                    }
                    else
                    {
                        header = long.TryParse(parentKey, NumberStyles.None, CultureInfo.InvariantCulture, out var above)
                                 && Rows(table.Parent).TryGetValue(above, out var parentRow)
                                 && Copies(table.Header).TryGetValue(parentRow[ReservoirManagementTables.KeyColumn(table.Header)]!.GetValue<string>(), out var owner)
                            ? owner
                            : null;
                    }

                    if (header is null)
                    {
                        return Json(HttpStatusCode.NotFound, new JsonObject { ["detail"] = new JsonObject { ["message"] = $"{table.ParentColumn} has not been found" } });
                    }

                    var mismatch = Plain(row[ReservoirManagementTables.ParentObjectColumn]) != header["parent_object_id"]!.GetValue<string>()
                                   || Plain(row[table.HeaderColumn]) != header["id"]!.GetValue<string>()
                                   || (table.ForecastBase && Plain(row[ReservoirManagementTables.ForecastBaseColumn]) != Plain(header["id_forecast_base"]))
                                   || table.Required.Any(c => row[c] is null);
                    if (mismatch)
                    {
                        return Json((HttpStatusCode)422, new JsonObject { ["detail"] = new JsonObject { ["One or several attributes mandatory are NULL"] = row.DeepClone() } });
                    }

                    var key = ++_rmSequence;
                    var stored = new JsonObject { [table.KeyColumn] = key };
                    foreach (var column in known.Where(c => c != table.KeyColumn).Order(StringComparer.Ordinal))
                    {
                        stored[column] = row[column]?.DeepClone();
                    }

                    rows[key] = stored;
                    return RmLostPosts.Contains(post)
                        ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                        : Json(HttpStatusCode.OK, stored.DeepClone());
                }

            // B6: the key is the query's.
            case ("DELETE", 4):
                {
                    if (!long.TryParse(query["catalog_entity_id"], NumberStyles.None, CultureInfo.InvariantCulture, out var key))
                    {
                        return RmDetail((HttpStatusCode)422, "value is not a valid integer");
                    }

                    var referring = ReservoirManagementTables.Tables.Where(t => t.Parent == table.Segment)
                        .Any(t => Rows(t.Segment).Values.Any(r => Plain(r[t.ParentColumn]) == key.ToString(CultureInfo.InvariantCulture)));
                    if (referring)
                    {
                        return RmText(HttpStatusCode.InternalServerError, "Internal Server Error");
                    }

                    return rows.Remove(key)
                        ? Json(HttpStatusCode.OK, new JsonArray($"Successfully deleted the object with id: {key}"))
                        : Json(HttpStatusCode.OK, new JsonArray("Error: l'objet que vous souhaitez supprimer n'existe plus"));
                }
        }

        return RmDetail(HttpStatusCode.MethodNotAllowed, "Method Not Allowed");
    }

    private Dictionary<string, JsonObject> Copies(string segment)
    {
        if (!RmHeaders.TryGetValue(segment, out var copies))
        {
            copies = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            RmHeaders[segment] = copies;
        }

        return copies;
    }

    /// <summary>The rows of a table.</summary>
    public SortedDictionary<long, JsonObject> Rows(string segment)
    {
        if (!RmRows.TryGetValue(segment, out var rows))
        {
            rows = [];
            RmRows[segment] = rows;
        }

        return rows;
    }

    private static string Plain(JsonNode? node) => node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : node?.ToJsonString() ?? string.Empty;

    private static HttpResponseMessage RmDetail(HttpStatusCode status, string detail) => Json(status, new JsonObject { ["detail"] = detail });

    private static HttpResponseMessage RmText(HttpStatusCode status, string text)
        => new(status) { Content = new StringContent(text, System.Text.Encoding.UTF8, "text/plain") };
}
