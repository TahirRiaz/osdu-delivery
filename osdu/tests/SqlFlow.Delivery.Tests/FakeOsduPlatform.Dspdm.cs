using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The Production DDMS core service (DSPDM) as osdu/specs/production-dspdm/INTEGRATION.md reads its code: business objects
/// described by the metadata tables and read through <c>POST /common</c>; a save that is one transaction, upper-cases
/// attribute names and trims text, converts each value to its attribute's type (a decimal rounded to its scale, an ISO time
/// with an offset moved into the request's zone, any other time kept as written), checks mandatory attributes and unique
/// constraints, inserts a row without a primary key under the next key of its sequence, updates only what changed in a
/// row that has one (and refuses one whose key is not there), stamps the UTC time on what it inserts and changes, and reads
/// the rows back only when it changed something; a hard delete by primary key; and error answers that carry a stack trace.
/// </summary>
public sealed partial class FakeOsduPlatform
{
    /// <summary>Where the flows under test find DSPDM (the GC gateway's prefix).</summary>
    public const string DspdmRoot = "/api/dspdm/v1";

    private DateTime _dspdmClock = new(2026, 3, 1, 8, 0, 0, DateTimeKind.Unspecified);
    private int _dspdmSaveCalls;

    /// <summary>The business objects DSPDM holds, by name.</summary>
    public Dictionary<string, FakeDspdmObject> DspdmObjects { get; } = new(StringComparer.Ordinal);

    /// <summary>Saves, counted from 1, that are committed and whose answer is lost (a gateway's 502).</summary>
    public HashSet<int> DspdmLostSaves { get; } = [];

    /// <summary>Saves, counted from 1, that fail on the database connection and commit nothing (ERROR with a cause).</summary>
    public HashSet<int> DspdmBrokenSaves { get; } = [];

    /// <summary>A refusal DSPDM raises itself for a value (WARNING), by attribute and value; null takes the value.</summary>
    public Func<string, JsonNode?, string?>? DspdmRefuses { get; set; }

    /// <summary>A value the database refuses with a check constraint (ERROR, in SQLState's fixed text), by attribute and value.</summary>
    public Func<string, JsonNode?, bool>? DspdmViolates { get; set; }

    /// <summary>How the database keeps text beyond trimming it, applied to saved values and to query values alike.</summary>
    public Func<string, string, string>? DspdmStoredText { get; set; }

    /// <summary>How many save calls DSPDM answered or lost.</summary>
    public int DspdmSaves => _dspdmSaveCalls;

    /// <summary>The rows of a business object, by primary key.</summary>
    public SortedDictionary<long, JsonObject> DspdmRows(string businessObject) => DspdmObjects[businessObject].Rows;

    /// <summary>Moves the time DSPDM stamps rows with.</summary>
    public void DspdmAdvance(TimeSpan by) => _dspdmClock += by;

    /// <summary>
    /// The WELL business object (entity <c>well</c>): a sequence key, UWI unique and mandatory, a name, a spud time, a depth
    /// kept to two places, a flag, a remark, a read-only status, and the four audit attributes.
    /// </summary>
    public FakeDspdmObject DspdmWell()
    {
        var well = new FakeDspdmObject("WELL", "well")
        {
            Attributes =
            {
                new("WELL_ID", "integer", PrimaryKey: true, Mandatory: true),
                new("UWI", "character varying(20)", Mandatory: true),
                new("WELL_NAME", "character varying(30)"),
                new("SPUD_DATE", "timestamp without time zone"),
                new("DEPTH", "numeric(10,2)"),
                new("IS_ACTIVE", "boolean"),
                new("REMARK", "character varying(200)"),
                new("OPERATOR", "character varying(30)", Mandatory: true),
                new("STATUS_CODE", "character varying(10)", ReadOnly: true),
                new("ROW_CREATED_BY", "character varying(50)"),
                new("ROW_CREATED_DATE", "timestamp without time zone"),
                new("ROW_CHANGED_BY", "character varying(50)"),
                new("ROW_CHANGED_DATE", "timestamp without time zone"),
            },
        };
        well.Constraints["UK_WELL_UWI"] = ["UWI"];
        DspdmObjects[well.Name] = well;
        return well;
    }

    /// <summary>The WELL VOL DAILY business object (entity <c>well_vol_daily</c>), found by a well and a day together.</summary>
    public FakeDspdmObject DspdmDailyVolumes()
    {
        var daily = new FakeDspdmObject("WELL VOL DAILY", "well_vol_daily")
        {
            Attributes =
            {
                new("WELL_VOL_DAILY_ID", "bigint", PrimaryKey: true, Mandatory: true),
                new("UWI", "character varying(20)", Mandatory: true),
                new("VOLUME_DATE", "date", Mandatory: true),
                new("OIL_VOLUME", "double precision"),
                new("ROW_CREATED_DATE", "timestamp without time zone"),
                new("ROW_CHANGED_DATE", "timestamp without time zone"),
            },
        };
        daily.Constraints["UK_WVD"] = ["UWI", "VOLUME_DATE"];
        DspdmObjects[daily.Name] = daily;
        return daily;
    }

    private HttpResponseMessage? DspdmRoute(string method, string path, string? body, HttpRequestMessage request)
    {
        if (!path.StartsWith(DspdmRoot + "/", StringComparison.Ordinal))
        {
            return null;
        }

        var rest = Uri.UnescapeDataString(path[DspdmRoot.Length..]);
        if (rest == "/health" && method == "GET")
        {
            return DspdmAnswer(HttpStatusCode.OK, 1, "SUCCESS", ["OK"], new JsonObject());
        }

        // The request filter: every other path needs a partition (RequestContextFilter).
        if (!request.Headers.TryGetValues("data-partition-id", out var partitions) || string.IsNullOrWhiteSpace(partitions.FirstOrDefault()))
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("NO_TENANT_ID_FOUND_IN_REQUEST_HEADER", Encoding.UTF8, "text/plain") };
        }

        if (rest == "/common" && method == "POST")
        {
            return DspdmQuery(JsonNode.Parse(body!)!.AsObject());
        }

        if (rest == "/save" && method == "POST")
        {
            return DspdmSave(JsonNode.Parse(body!)!.AsObject());
        }

        if (rest.StartsWith("/delete/", StringComparison.Ordinal) && method == "DELETE")
        {
            var parts = rest.Split('/');
            return parts.Length == 4 && long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                ? DspdmDelete(parts[2].ToUpperInvariant(), id)
                : DspdmFailure(HttpStatusCode.BadRequest, -1, "Invalid id");
        }

        return DspdmFailure(HttpStatusCode.NotFound, -1, "HTTP 404 Not Found");
    }

    private HttpResponseMessage DspdmQuery(JsonObject query)
    {
        var name = query["boName"]!.GetValue<string>().Trim().ToUpperInvariant();
        var zone = DspdmZone(query["timezone"]?.GetValue<string>());
        IEnumerable<JsonObject> rows;
        Func<string, string?> typeOf;
        Func<JsonObject, JsonObject> write = r => r;
        switch (name)
        {
            case "BUSINESS OBJECT":
                rows = DspdmObjects.Values.Select(o => new JsonObject
                {
                    ["BO_NAME"] = o.Name,
                    ["ENTITY"] = o.Entity,
                    ["IS_ACTIVE"] = o.Active,
                    ["IS_METADATA_TABLE"] = o.MetadataTable,
                    ["IS_SPECIFICATION_CATALOG_TABLE"] = o.CatalogTable,
                    ["IS_MEASUREMENT_CATALOG_TABLE"] = false,
                });
                typeOf = _ => "character varying(50)";
                break;
            case "BUSINESS OBJECT ATTR":
                rows = DspdmObjects.Values.SelectMany(o => o.Attributes.Select(a => new JsonObject
                {
                    ["BO_NAME"] = o.Name,
                    ["BO_ATTR_NAME"] = a.Name,
                    ["ATTRIBUTE_DATATYPE"] = a.DataType,
                    ["IS_MANDATORY"] = a.Mandatory,
                    ["IS_PRIMARY_KEY"] = a.PrimaryKey,
                    ["IS_READ_ONLY"] = a.ReadOnly,
                    ["IS_ACTIVE"] = a.Active,
                }));
                typeOf = _ => "character varying(50)";
                break;
            case "BUS OBJ ATTR UNIQ CONSTRAINTS":
                rows = DspdmObjects.Values.SelectMany(o => o.Constraints.SelectMany(c => c.Value.Select(attribute => new JsonObject
                {
                    ["BO_NAME"] = o.Name,
                    ["CONSTRAINT_NAME"] = c.Key,
                    ["BO_ATTR_NAME"] = attribute,
                    ["IS_ACTIVE"] = true,
                })));
                typeOf = _ => "character varying(200)";
                break;
            default:
                if (!DspdmObjects.TryGetValue(name, out var business))
                {
                    return DspdmFailure(HttpStatusCode.InternalServerError, -1, $"Business object '{name}' not found");
                }

                rows = business.Rows.Values;
                write = r => DspdmWritten(business, r, zone);
                typeOf = attribute => business.Attributes.FirstOrDefault(a => a.Name == attribute)?.DataType;
                break;
        }

        var filtered = rows.ToList();
        foreach (var filter in query["criteriaFilters"] as JsonArray ?? [])
        {
            var attribute = filter!["boAttrName"]!.GetValue<string>().ToUpperInvariant();
            if (typeOf(attribute) is not { } type)
            {
                return DspdmFailure(HttpStatusCode.InternalServerError, -1, $"Invalid attribute '{attribute}' for business object '{name}'");
            }

            var op = filter["operator"]!.GetValue<string>();
            if (op is not ("EQUALS" or "IN"))
            {
                return DspdmFailure(HttpStatusCode.BadRequest, -1, $"The fake takes EQUALS and IN, not {op}");
            }

            var wanted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in filter["values"]!.AsArray())
            {
                if (DspdmStored(attribute, type, value, zone, out var stored) is { } problem)
                {
                    return DspdmFailure(HttpStatusCode.InternalServerError, -1, problem);
                }

                wanted.Add(DspdmComparable(stored));
            }

            filtered = filtered.Where(r => wanted.Contains(DspdmComparable(r[attribute]))).ToList();
        }

        if (query["orderBy"] is JsonArray { Count: > 0 } order)
        {
            var by = order[0]!["boAttrName"]!.GetValue<string>().ToUpperInvariant();
            filtered = filtered.OrderBy(r => r[by]?.ToJsonString(), StringComparer.Ordinal).ToList();
            if (by.EndsWith("_ID", StringComparison.Ordinal))
            {
                filtered = filtered.OrderBy(r => r[by]?.GetValue<long>() ?? 0).ToList();
            }
        }

        var size = query["pagination"]?["recordsPerPage"]?.GetValue<int>() ?? 20;
        var page = query["pagination"]?["pages"]?[0]?.GetValue<int>() ?? 1;
        var selected = filtered.Skip((page - 1) * size).Take(size).Select(write).Select(r =>
        {
            if (query["selectList"] is not JsonArray select)
            {
                return (JsonNode?)r.DeepClone();
            }

            var projected = new JsonObject();
            foreach (var column in select)
            {
                var attribute = column!.GetValue<string>().ToUpperInvariant();
                if (r[attribute] is { } value)
                {
                    projected[attribute] = value.DeepClone();
                }
            }

            return projected;
        }).ToArray();
        return DspdmAnswer(HttpStatusCode.OK, 1, "SUCCESS", [], new JsonObject { [name] = new JsonObject { ["totalRecords"] = filtered.Count, ["list"] = new JsonArray(selected) } });
    }

    private HttpResponseMessage DspdmSave(JsonObject request)
    {
        var call = ++_dspdmSaveCalls;
        var data = new JsonObject();
        var inserted = 0;
        var updated = 0;
        var ignored = 0;
        var staged = new Dictionary<string, SortedDictionary<long, JsonObject>>(StringComparer.Ordinal);
        foreach (var (key, value) in request)
        {
            var name = key.ToUpperInvariant();
            var map = value!.AsObject();
            if (map["language"]?.GetValue<string>() != "en")
            {
                return DspdmFailure(HttpStatusCode.BadRequest, -1, "Only language 'en' is supported");
            }

            var timezone = map["timezone"]?.GetValue<string>();
            if (timezone is null || !GmtZone().IsMatch(timezone))
            {
                return DspdmFailure(HttpStatusCode.BadRequest, -1, $"Invalid timezone '{timezone}'");
            }

            if (!DspdmObjects.TryGetValue(name, out var business))
            {
                return DspdmFailure(HttpStatusCode.InternalServerError, -1, $"Business object '{name}' not found");
            }

            if (business.MetadataTable)
            {
                return DspdmFailure(HttpStatusCode.InternalServerError, -1, $"Cannot save metadata business object '{name}'");
            }

            if (map["data"] is not JsonArray { Count: > 0 } rows)
            {
                return DspdmFailure(HttpStatusCode.InternalServerError, -1, $"No records found for business object '{name}'");
            }

            var zone = DspdmZone(timezone);
            var table = new SortedDictionary<long, JsonObject>(business.Rows.ToDictionary(r => r.Key, r => (JsonObject)r.Value.DeepClone()));
            staged[name] = table;
            var pk = business.Attributes.Single(a => a.PrimaryKey).Name;
            var back = new List<(JsonObject Sent, long? Id, string? Flag)>();
            var index = 0;
            foreach (var item in rows)
            {
                index++;
                var sent = new JsonObject();
                foreach (var (attributeName, attributeValue) in item!.AsObject())
                {
                    var upper = attributeName.ToUpperInvariant();
                    if (business.Attributes.FirstOrDefault(a => a.Name == upper) is not { } attribute)
                    {
                        continue;
                    }

                    if (DspdmRefuses?.Invoke(upper, attributeValue) is { } refusal)
                    {
                        return DspdmFailure(HttpStatusCode.InternalServerError, -1, refusal);
                    }

                    if (DspdmViolates?.Invoke(upper, attributeValue) == true)
                    {
                        return DspdmFailure(HttpStatusCode.InternalServerError, -2, "These record(s) cannot be inserted or updated due to check constraint violation. Please check the values provided.", cause: "org.postgresql.util.PSQLException: ERROR: new row violates check constraint");
                    }

                    if (DspdmStored(upper, attribute.DataType, attributeValue, zone, out var stored) is { } problem)
                    {
                        return DspdmFailure(HttpStatusCode.InternalServerError, -1, problem);
                    }

                    sent[upper] = stored;
                }

                var id = sent[pk] is JsonValue given ? given.GetValue<long>() : (long?)null;
                sent.Remove(pk);
                if (id is null)
                {
                    foreach (var attribute in business.Attributes.Where(a => a.Mandatory && !a.PrimaryKey && !a.Audit))
                    {
                        if (sent[attribute.Name] is null || (sent[attribute.Name] is JsonValue text && text.GetValueKind() == JsonValueKind.String && text.GetValue<string>().Trim().Length == 0))
                        {
                            return DspdmFailure(HttpStatusCode.InternalServerError, -1, $"save : {business.Name} field {attribute.Name} for record {index} must have a value");
                        }
                    }

                    if (DspdmTaken(business, table, sent, null) is { } taken)
                    {
                        return DspdmFailure(HttpStatusCode.Conflict, -1, $"Cannot perform save operation on business object '{business.Name}'. Value '{taken}' already exists");
                    }

                    var row = new JsonObject();
                    foreach (var (attributeName, attributeValue) in sent)
                    {
                        if (attributeValue is not null && !business.Attributes.Single(a => a.Name == attributeName).ReadOnly)
                        {
                            row[attributeName] = attributeValue.DeepClone();
                        }
                    }

                    var newId = ++business.Sequence;
                    row[pk] = newId;
                    row["ROW_CREATED_BY"] = "fake-user";
                    row["ROW_CREATED_DATE"] = Stamp(_dspdmClock);
                    table[newId] = row;
                    inserted++;
                    back.Add((sent, newId, "isInserted"));
                    continue;
                }

                if (!table.TryGetValue(id.Value, out var current))
                {
                    return DspdmFailure(HttpStatusCode.InternalServerError, -1, $"No business object '{business.Name}' already exists with the id '{id}'");
                }

                var changed = sent.Where(s => business.Attributes.Single(a => a.Name == s.Key) is { ReadOnly: false, Audit: false }
                    && DspdmComparable(s.Value) != DspdmComparable(current[s.Key])).ToList();
                if (changed.Count == 0)
                {
                    ignored++;
                    back.Add((sent, id, null));
                    continue;
                }

                foreach (var attribute in business.Attributes.Where(a => a.Mandatory && !a.PrimaryKey && !a.Audit && sent.ContainsKey(a.Name) && sent[a.Name] is null))
                {
                    return DspdmFailure(HttpStatusCode.InternalServerError, -1, $"update : {business.Name} field {attribute.Name} for record {index} must have a value");
                }

                var next = (JsonObject)current.DeepClone();
                foreach (var (attributeName, attributeValue) in changed)
                {
                    if (attributeValue is null)
                    {
                        next.Remove(attributeName);
                    }
                    else
                    {
                        next[attributeName] = attributeValue.DeepClone();
                    }
                }

                if (DspdmTaken(business, table, next, id) is { } clash)
                {
                    return DspdmFailure(HttpStatusCode.Conflict, -1, $"Cannot perform update operation on business object '{business.Name}'. Value '{clash}' already exists");
                }

                next["ROW_CHANGED_BY"] = "fake-user";
                next["ROW_CHANGED_DATE"] = Stamp(_dspdmClock);
                table[id.Value] = next;
                updated++;
                back.Add((sent, id, "isUpdated"));
            }

            var readBack = map["readBack"]?.GetValue<bool>() == true;
            var list = new JsonArray();
            foreach (var (sent, id, flag) in back)
            {
                // DSPDM reads the saved rows again only when the call inserted or updated one; otherwise the rows go back as sent.
                var row = inserted + updated > 0 ? DspdmWritten(business, table[id!.Value], zone) : DspdmSent(business, sent, id, zone);
                if (flag is not null)
                {
                    row[flag] = true;
                }

                list.Add(row);
            }

            if (readBack)
            {
                data[name] = list;
            }
        }

        if (DspdmBrokenSaves.Contains(call))
        {
            return DspdmFailure(HttpStatusCode.InternalServerError, -2, "Unable to acquire a database connection", cause: "java.sql.SQLTransientConnectionException: connection is not available");
        }

        foreach (var (name, table) in staged)
        {
            DspdmObjects[name].Rows.Clear();
            foreach (var (id, row) in table)
            {
                DspdmObjects[name].Rows[id] = row;
            }
        }

        _dspdmClock = _dspdmClock.AddSeconds(1);
        if (DspdmLostSaves.Contains(call))
        {
            return new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html><body>502 Bad Gateway</body></html>", Encoding.UTF8, "text/html") };
        }

        var messages = new List<string>();
        if (inserted > 0)
        {
            messages.Add($"{inserted} record(s) inserted successfully.");
        }

        if (updated > 0)
        {
            messages.Add($"{updated} record(s) updated successfully.");
        }

        if (ignored > 0 && inserted + updated == 0)
        {
            messages.Add($"{ignored} record(s) ignored because nothing changed.");
        }

        return DspdmAnswer(HttpStatusCode.OK, 1, "SUCCESS", messages, data);
    }

    private HttpResponseMessage DspdmDelete(string name, long id)
    {
        if (!DspdmObjects.TryGetValue(name, out var business))
        {
            return DspdmFailure(HttpStatusCode.InternalServerError, -1, $"Business object '{name}' not found");
        }

        if (business.MetadataTable)
        {
            return DspdmFailure(HttpStatusCode.Forbidden, -1, "Cannot delete metadata business object");
        }

        if (!business.Rows.Remove(id))
        {
            return DspdmFailure(HttpStatusCode.NotFound, -1, $"No record found for business object '{name}' with id '{id}'");
        }

        return DspdmAnswer(HttpStatusCode.OK, 1, "SUCCESS", ["1 record(s) deleted successfully."], new JsonObject());
    }

    /// <summary>The value of another row that already holds the unique key a row gives, or null.</summary>
    private static string? DspdmTaken(FakeDspdmObject business, SortedDictionary<long, JsonObject> table, JsonObject row, long? self)
    {
        foreach (var (_, members) in business.Constraints)
        {
            if (members.Any(m => row[m] is null))
            {
                continue;
            }

            foreach (var (id, other) in table)
            {
                if (id != self && members.All(m => other[m]?.ToJsonString() == row[m]!.ToJsonString()))
                {
                    return string.Join(", ", members.Select(m => row[m]!.ToJsonString()));
                }
            }
        }

        return null;
    }

    /// <summary>
    /// A request value as the database keeps it, or why it cannot be kept: text trimmed (and as <see cref="DspdmStoredText"/>
    /// keeps it) and within its length, numbers of the attribute's type (a decimal rounded half up to its scale), flags, and
    /// times as the wall-clock time in the request's zone for an ISO time with an offset, as written otherwise.
    /// </summary>
    private string? DspdmStored(string attribute, string dataType, JsonNode? value, TimeSpan zone, out JsonNode? stored)
    {
        stored = null;
        if (value is null || value.GetValueKind() == JsonValueKind.Null)
        {
            return null;
        }

        var text = value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>().Trim() : value.ToJsonString();
        var type = dataType.ToLowerInvariant();
        if (type.StartsWith("character", StringComparison.Ordinal))
        {
            text = DspdmStoredText?.Invoke(attribute, text) ?? text;
            var length = int.Parse(Regex.Match(type, @"\((\d+)\)").Groups[1].Value, CultureInfo.InvariantCulture);
            if (text.Length > length)
            {
                return $"save : value '{text}' exceeds the length of {attribute}";
            }

            stored = text;
            return null;
        }

        if (type is "integer" or "bigint")
        {
            if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole))
            {
                return $"Unable to convert string value '{text}' to the java data type 'java.lang.Long' for bo attribute '{attribute}'";
            }

            stored = whole;
            return null;
        }

        if (type.StartsWith("numeric", StringComparison.Ordinal) || type == "double precision")
        {
            if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                return $"Unable to convert string value '{text}' to the java data type 'java.math.BigDecimal' for bo attribute '{attribute}'";
            }

            var scale = Regex.Match(type, @",\s*(\d+)\)") is { Success: true } s ? int.Parse(s.Groups[1].Value, CultureInfo.InvariantCulture) : (int?)null;
            stored = scale is { } places ? decimal.Round(number, places, MidpointRounding.AwayFromZero) : number;
            return null;
        }

        if (type == "boolean")
        {
            stored = text.ToUpperInvariant() switch
            {
                "TRUE" or "1" or "Y" => true,
                "FALSE" or "0" or "N" => false,
                _ => null,
            };
            return stored is null ? $"Value '{text}' is illegal to be considered as true or false predicate" : null;
        }

        if (type.StartsWith("timestamp", StringComparison.Ordinal) || type == "date")
        {
            DateTime local;
            if (DateTimeOffset.TryParseExact(text, ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mmK"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant)
                && (text.EndsWith('Z') || Regex.IsMatch(text, @"[+-]\d{2}:\d{2}$")))
            {
                local = instant.ToOffset(zone).DateTime;
            }
            else
            {
                var written = Regex.Replace(text, @"(?<=\d{2}:\d{2}(:\d{2}(\.\d+)?)?) ?(Z|[+-]\d{2}(:?\d{2})?)$", string.Empty);
                if (!DateTime.TryParseExact(written, ["yyyy-MM-dd", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.fff", "yyyy/MM/dd", "yyyy/MM/dd HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out local))
                {
                    return $"The input date format '{text}' is not supported, please use proper date format.";
                }
            }

            stored = type == "date" ? local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : Stamp(local);
            return null;
        }

        stored = text;
        return null;
    }

    /// <summary>A kept value in one comparable form, whatever JSON it was written in.</summary>
    private static string DspdmComparable(JsonNode? value)
        => value switch
        {
            null => "null",
            JsonValue number when number.GetValueKind() == JsonValueKind.Number => decimal.Parse(number.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture).ToString("0.############################", CultureInfo.InvariantCulture),
            _ => value.ToJsonString(),
        };

    /// <summary>A kept row as a read writes it: the serializer's type and id, then the attributes, times labelled with the request's zone.</summary>
    private static JsonObject DspdmWritten(FakeDspdmObject business, JsonObject row, TimeSpan zone)
    {
        var pk = business.Attributes.Single(a => a.PrimaryKey).Name;
        var written = new JsonObject { ["type"] = business.Name, ["id"] = row[pk]?.DeepClone() };
        foreach (var attribute in business.Attributes)
        {
            if (row[attribute.Name] is not { } value)
            {
                continue;
            }

            written[attribute.Name] = attribute.DataType.StartsWith("timestamp", StringComparison.Ordinal)
                ? Label(value.GetValue<string>(), zone)
                : value.DeepClone();
        }

        return written;
    }

    /// <summary>A row as it was sent, which is what a save that changed nothing answers with.</summary>
    private static JsonObject DspdmSent(FakeDspdmObject business, JsonObject sent, long? id, TimeSpan zone)
    {
        var row = new JsonObject { ["type"] = business.Name, ["id"] = id };
        row[business.Attributes.Single(a => a.PrimaryKey).Name] = id;
        foreach (var (name, value) in sent)
        {
            if (value is not null)
            {
                row[name] = business.Attributes.Single(a => a.Name == name).DataType.StartsWith("timestamp", StringComparison.Ordinal) ? Label(value.GetValue<string>(), zone) : value.DeepClone();
            }
        }

        return row;
    }

    private static string Stamp(DateTime local) => local.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <summary>A kept time written with the request's zone after it, as <c>yyyy-MM-dd'T'HH:mm:ss.SSSXXX</c> writes it (Z for zero).</summary>
    private static string Label(string stamp, TimeSpan zone)
        => stamp + (zone == TimeSpan.Zero ? "Z" : (zone < TimeSpan.Zero ? "-" : "+") + zone.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture));

    private static TimeSpan DspdmZone(string? timezone)
    {
        var match = GmtZone().Match(timezone ?? "GMT+00:00");
        var offset = new TimeSpan(int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture), 0);
        return match.Groups[1].Value == "-" ? -offset : offset;
    }

    private static HttpResponseMessage DspdmAnswer(HttpStatusCode status, int code, string label, IReadOnlyList<string> messages, JsonObject data)
        => Json(status, DspdmBody(code, label, messages, data));

    private static JsonObject DspdmBody(int code, string label, IReadOnlyList<string> messages, JsonObject data) => new()
    {
        ["status"] = new JsonObject { ["statusCode"] = code, ["severity"] = code < 0 ? "ERROR" : "INFO", ["statusLabel"] = label },
        ["messages"] = new JsonArray(messages.Select(m => (JsonNode?)new JsonObject
        {
            ["message"] = m,
            ["status"] = new JsonObject { ["statusCode"] = code, ["statusLabel"] = label },
        }).ToArray()),
        ["data"] = data,
        ["version"] = "1.0",
        ["threadName"] = "grizzly-http-server-0",
        ["requestTime"] = "2026-03-01 08:00:00.000 Z",
        ["responseTime"] = "2026-03-01 08:00:00.010 Z",
    };

    /// <summary>A refusal as DSPDMResponseSerializer writes it: the status, the message twice over, and a stack trace.</summary>
    private static HttpResponseMessage DspdmFailure(HttpStatusCode status, int code, string message, string? cause = null)
    {
        var body = DspdmBody(code, code == -1 ? "WARNING" : "ERROR", [message], new JsonObject());
        body["exception"] = new JsonObject
        {
            ["message"] = message,
            ["stackTrace"] = $"com.lgc.dspdm.core.common.exception.DSPDMException: {message}\n\tat com.lgc.dspdm.msp.mainservice.MainserviceImpl.saveOrUpdate(MainserviceImpl.java:3040)"
                + (cause is null ? string.Empty : $"\nCaused by: {cause}\n\tat org.postgresql.core.v3.QueryExecutorImpl.receiveErrorResponse(QueryExecutorImpl.java:2713)"),
        };
        return Json(status, body);
    }

    [GeneratedRegex(@"^GMT([+-])(\d{2}):(\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex GmtZone();
}

/// <summary>A business object of the fake DSPDM: its metadata and its rows.</summary>
public sealed class FakeDspdmObject(string name, string entity)
{
    public string Name { get; } = name;

    public string Entity { get; } = entity;

    public bool Active { get; set; } = true;

    public bool MetadataTable { get; set; }

    public bool CatalogTable { get; set; }

    public List<FakeDspdmColumn> Attributes { get; } = [];

    /// <summary>The unique constraints, by name, each with its attributes.</summary>
    public Dictionary<string, List<string>> Constraints { get; } = new(StringComparer.Ordinal);

    public SortedDictionary<long, JsonObject> Rows { get; } = [];

    public long Sequence { get; set; } = 1000;
}

/// <summary>An attribute of a fake DSPDM business object.</summary>
public sealed record FakeDspdmColumn(string Name, string DataType, bool Mandatory = false, bool PrimaryKey = false, bool ReadOnly = false, bool Active = true)
{
    public bool Audit => Name is "ROW_CREATED_BY" or "ROW_CREATED_DATE" or "ROW_CHANGED_BY" or "ROW_CHANGED_DATE";
}
