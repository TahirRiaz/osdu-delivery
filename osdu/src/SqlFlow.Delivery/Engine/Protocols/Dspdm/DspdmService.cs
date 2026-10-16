using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Protocols.Dspdm;

/// <summary>One condition of a DSPDM query: an attribute, an operator (<c>EQUALS</c>, <c>IN</c>) and its values. Conditions are ANDed.</summary>
internal sealed record DspdmFilter(string Attribute, string Operator, IReadOnlyList<JsonNode?> Values)
{
    public const string EqualsOperator = "EQUALS";
    public const string InOperator = "IN";
}

/// <summary>
/// What DSPDM answered a call with (<c>DSPDMResponseSerializer</c>): its status code (INFO 0, SUCCESS 1, PARTIAL_SUCCESS 2,
/// negative for a warning or an error), its messages and its data, keyed by business object name.
/// </summary>
internal sealed record DspdmAnswer(int StatusCode, IReadOnlyList<string> Messages, JsonObject Data)
{
    public const int Info = 0;
    public const int Success = 1;
    public const int PartialSuccess = 2;
    public const int Warning = -1;
    public const int Error = -2;

    /// <summary>The rows the answer holds for <paramref name="businessObject"/>: a save's list, or a read's page (<c>{totalRecords, list}</c>).</summary>
    public IReadOnlyList<JsonObject> Rows(string businessObject)
    {
        var node = Data[businessObject];
        var list = node switch
        {
            JsonArray array => array,
            JsonObject page => page["list"] as JsonArray,
            null => null,
            _ => throw new DeliveryException($"DSPDM answered data for '{businessObject}' that is neither a list of rows nor a page of them."),
        };
        if (list is null)
        {
            return [];
        }

        var rows = new List<JsonObject>(list.Count);
        foreach (var item in list)
        {
            rows.Add(item as JsonObject ?? throw new DeliveryException($"DSPDM answered a row of '{businessObject}' that is not an object."));
        }

        return rows;
    }

    /// <summary>
    /// The answer a call's body carries, or null when the body is not one of DSPDM's answers (a gateway's page). An answer whose
    /// status is negative is a refusal, whatever its HTTP status said, and is thrown as one.
    /// </summary>
    public static DspdmAnswer? TryParse(HttpFetchResult result, Uri url)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(url);
        JsonObject? root;
        try
        {
            root = result.Body.Length == 0 ? null : JsonNode.Parse(result.Body) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        if (root is null || StatusCodeOf(root["status"]) is not { } code)
        {
            return null;
        }

        var messages = new List<string>();
        foreach (var message in root["messages"] as JsonArray ?? [])
        {
            if (message is JsonObject entry && entry["message"] is JsonValue text && text.TryGetValue<string>(out var said) && !string.IsNullOrWhiteSpace(said))
            {
                messages.Add(said.Trim());
            }
        }

        if (code < 0)
        {
            // The exception's message and never its stack trace, which DSPDM prints into every error it answers.
            if (root["exception"] is JsonObject thrown && thrown["message"] is JsonValue m && m.TryGetValue<string>(out var what) && !string.IsNullOrWhiteSpace(what))
            {
                messages.Add(what.Trim());
            }

            throw new DspdmRefusal(url, (int)result.Status, code, messages.Distinct(StringComparer.Ordinal).ToList());
        }

        return new DspdmAnswer(code, messages, root["data"] as JsonObject ?? new JsonObject());
    }

    /// <summary>The answer a call's body carries; a body that is not one of DSPDM's answers is a failure.</summary>
    public static DspdmAnswer Parse(HttpFetchResult result, Uri url)
        => TryParse(result, url)
            ?? throw new DeliveryException($"{url.AbsolutePath}: DSPDM answered HTTP {(int)result.Status} with a body that is not one of its answers: {HeaderRedaction.RedactMessage(OsduError.Describe(result.BodyText))}");

    /// <summary>The status code of an answer: the serializer's object, or the contract's name (INFO, SUCCESS, ...).</summary>
    private static int? StatusCodeOf(JsonNode? status) => status switch
    {
        JsonObject state when state["statusCode"] is JsonValue value && value.TryGetValue<int>(out var code) => code,
        JsonValue name when name.TryGetValue<string>(out var label) => label.Trim().ToUpperInvariant() switch
        {
            "INFO" => Info,
            "SUCCESS" => Success,
            "PARTIAL_SUCCESS" => PartialSuccess,
            "WARNING" => Warning,
            "ERROR" => Error,
            "FATAL" => -3,
            "SESSION_TIMEOUT" => -4,
            _ => null,
        },
        _ => null,
    };
}

/// <summary>
/// DSPDM refused a call: it answered with a negative status (osdu/specs/production-dspdm/INTEGRATION.md section 6). DSPDM
/// answers WARNING for a refusal it raised itself, without an underlying failure (a mandatory attribute missing, a value it
/// cannot convert, a row it cannot find: <c>DSPDMResponse</c>), and ERROR for a failure with a cause, which is either a
/// constraint the database refused, named in fixed text (<c>SQLState.getActualExceptionForSave</c>), or something that may
/// pass, such as a lost database connection.
/// </summary>
internal sealed class DspdmRefusal : DeliveryException
{
    /// <summary>The texts <c>SQLState.getActualExceptionForSave</c> names the constraints a save broke with; a foreign key is not among them, since a row saved later can satisfy it.</summary>
    private static readonly string[] ConstraintTexts =
    [
        "due to check constraint violation",
        "due to primary key unique violation",
        "due to unique constraint violation",
        "due to not null constraint violation",
        "due to not empty constraint violation",
    ];

    public DspdmRefusal(Uri url, int httpStatus, int status, IReadOnlyList<string> messages)
        : base(Describe(url, httpStatus, status, messages))
    {
        HttpStatus = httpStatus;
        Status = status;
        Messages = messages;
    }

    /// <summary>The HTTP status the refusal came with.</summary>
    public int HttpStatus { get; }

    /// <summary>DSPDM's status code: -1 WARNING, -2 ERROR, -3 FATAL, -4 SESSION_TIMEOUT.</summary>
    public int Status { get; }

    public IReadOnlyList<string> Messages { get; }

    /// <summary>
    /// Whether the refusal is about what was sent, so sending it again cannot help: a refusal DSPDM raised itself, or a
    /// constraint other than a foreign key.
    /// </summary>
    public bool AboutTheRows => Status == DspdmAnswer.Warning
        || (Status == DspdmAnswer.Error && Messages.Any(m => ConstraintTexts.Any(t => m.Contains(t, StringComparison.OrdinalIgnoreCase))));

    private static string Describe(Uri url, int httpStatus, int status, IReadOnlyList<string> messages)
    {
        var label = status switch
        {
            DspdmAnswer.Warning => "WARNING",
            DspdmAnswer.Error => "ERROR",
            -3 => "FATAL",
            -4 => "SESSION_TIMEOUT",
            _ => status.ToString(CultureInfo.InvariantCulture),
        };
        var said = messages.Count == 0 ? "without a message" : string.Join("; ", messages);
        return HeaderRedaction.RedactMessage(string.Create(
            CultureInfo.InvariantCulture, $"{url.AbsolutePath}: DSPDM refused the call (HTTP {httpStatus}, status {label}): {OsduError.Bound(said)}"));
    }
}

/// <summary>What a delete by id did: the row was deleted, or DSPDM held no such row.</summary>
internal enum DspdmRemoval
{
    Deleted,
    Gone,
}

/// <summary>
/// The calls the dspdm route makes (osdu/specs/production-dspdm/INTEGRATION.md sections 3 to 5), under the root the flow
/// names: <c>POST /save</c> with a business object's rows, <c>POST /common</c> to read rows and metadata, one page at a
/// time in a stable order, <c>DELETE /delete/{boName}/{id}</c>, and <c>GET /health</c>. Every request names the language
/// DSPDM's save takes (<c>en</c>) and the flow's time zone.
/// </summary>
internal sealed class DspdmService
{
    public const string SavePath = "/save";
    public const string CommonPath = "/common";
    public const string DeletePath = "/delete/{businessObject}/{id}";
    public const string HealthPath = "/health";

    /// <summary>The only language DSPDM's save takes.</summary>
    public const string Language = "en";

    /// <summary>The most values one IN condition takes (<c>max_sql_in_statement_args_count</c>).</summary>
    public const int MaxInValues = 256;

    /// <summary>Rows per page of a read; DSPDM takes up to <c>max_page_size</c> (10000).</summary>
    public const int PageSize = 1000;

    /// <summary>The most rows one read may return in all its pages (<c>max_records_to_read</c>).</summary>
    public const int MaxRows = 100_000;

    private static readonly IReadOnlySet<int> Refusals = new HashSet<int> { 500 };

    private readonly OsduHttpClient _client;
    private readonly string _root;

    public DspdmService(OsduHttpClient client, string? root, string timezone)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(timezone);
        _client = client;
        _root = root ?? string.Empty;
        Timezone = timezone;
        Zone = Model.DspdmKinds.OffsetOf(timezone);
    }

    /// <summary>The time zone every request names.</summary>
    public string Timezone { get; }

    /// <summary>The offset of <see cref="Timezone"/>.</summary>
    public TimeSpan Zone { get; }

    /// <summary>A path of DSPDM's under the flow's root.</summary>
    public string PathOf(string path) => _root + path;

    /// <summary>
    /// Saves <paramref name="rows"/> of <paramref name="businessObject"/> in one call, which DSPDM runs as one transaction, and
    /// asks for the rows read back. Explicit nulls are sent as written: on an update they clear the attribute. The call is
    /// never repeated, since a row without its key is inserted again. A refusal DSPDM answers (most of them with HTTP 500) is
    /// thrown as a <see cref="DspdmRefusal"/>; a 500 that is not one of its answers is a failure of the service.
    /// </summary>
    public async Task<DspdmAnswer> SaveAsync(string businessObject, IReadOnlyList<JsonObject> rows, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(businessObject);
        ArgumentNullException.ThrowIfNull(rows);
        var request = new JsonObject
        {
            [businessObject] = new JsonObject
            {
                ["language"] = Language,
                ["timezone"] = Timezone,
                ["readBack"] = true,
                ["data"] = new JsonArray(rows.Select(r => (JsonNode?)r.DeepClone()).ToArray()),
            },
        };
        var url = _client.Url(PathOf(SavePath));
        var result = await _client.SendJsonBytesAsync(HttpMethod.Post, url, Encoding.UTF8.GetBytes(request.ToJsonString()), Refusals, ct, idempotent: false).ConfigureAwait(false);
        return Settled(result, url, HttpMethod.Post);
    }

    /// <summary>
    /// Reads the rows of <paramref name="businessObject"/> the filters match, ordered by <paramref name="orderBy"/> so its
    /// pages do not overlap, with the attributes in <paramref name="select"/> (every attribute when it is empty).
    /// </summary>
    public async Task<IReadOnlyList<JsonObject>> ReadAsync(
        string businessObject, IReadOnlyList<string> select, IReadOnlyList<DspdmFilter> filters, string orderBy, CancellationToken ct, int pageSize = PageSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(businessObject);
        ArgumentNullException.ThrowIfNull(select);
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentException.ThrowIfNullOrWhiteSpace(orderBy);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        var url = _client.Url(PathOf(CommonPath));
        var rows = new List<JsonObject>();
        for (var page = 1; ; page++)
        {
            var query = new JsonObject
            {
                ["boName"] = businessObject,
                ["language"] = Language,
                ["timezone"] = Timezone,
                ["criteriaFilters"] = new JsonArray(filters.Select(f => (JsonNode?)new JsonObject
                {
                    ["boAttrName"] = f.Attribute,
                    ["operator"] = f.Operator,
                    ["values"] = new JsonArray(f.Values.Select(v => v?.DeepClone()).ToArray()),
                }).ToArray()),
                ["orderBy"] = new JsonArray(new JsonObject { ["boAttrName"] = orderBy, ["order"] = "ASC" }),
                ["pagination"] = new JsonObject { ["recordsPerPage"] = pageSize, ["pages"] = new JsonArray(page) },
            };
            if (select.Count > 0)
            {
                query["selectList"] = new JsonArray(select.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
            }

            ct.ThrowIfCancellationRequested();

            // The body goes as written: a filter value is typed as the source gave it, and DSPDM converts it to the attribute's type.
            var result = await _client.SendJsonBytesAsync(HttpMethod.Post, url, Encoding.UTF8.GetBytes(query.ToJsonString()), null, ct, idempotent: true).ConfigureAwait(false);
            var found = DspdmAnswer.Parse(result, url).Rows(businessObject);
            rows.AddRange(found);
            if (found.Count < pageSize)
            {
                return rows;
            }

            if (rows.Count >= MaxRows)
            {
                throw new DeliveryException(
                    string.Create(CultureInfo.InvariantCulture, $"{url.AbsolutePath}: more than {MaxRows} rows of '{businessObject}' match, the most DSPDM reads in one query."));
            }
        }
    }

    /// <summary>
    /// Deletes the row <paramref name="id"/> of <paramref name="businessObject"/>, for good: DSPDM keeps no deleted rows. A row
    /// DSPDM does not hold (404, or an INFO answer) is gone already.
    /// </summary>
    public async Task<DspdmRemoval> DeleteAsync(string businessObject, long id, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(businessObject);
        var url = _client.Url(PathOf(DeletePath), new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["businessObject"] = businessObject,
            ["id"] = id.ToString(CultureInfo.InvariantCulture),
        });
        var result = await _client.SendJsonAsync(HttpMethod.Delete, url, null, new HashSet<int> { 404, 500 }, ct, idempotent: true).ConfigureAwait(false);
        if ((int)result.Status == 404)
        {
            return DspdmRemoval.Gone;
        }

        return Settled(result, url, HttpMethod.Delete).StatusCode == DspdmAnswer.Info ? DspdmRemoval.Gone : DspdmRemoval.Deleted;
    }

    /// <summary>
    /// Whether DSPDM answers: its health, which needs no credentials, then a read of its business object metadata, which
    /// needs the flow's credentials, a partition it serves and the entitlement to read.
    /// </summary>
    public async Task<ProbeOutcome> ProbeAsync(CancellationToken ct)
    {
        var health = await RecordWriter.ProbeAsync(_client, PathOf(HealthPath), ct).ConfigureAwait(false);
        if (!health.Reachable)
        {
            return health;
        }

        var url = _client.Url(PathOf(CommonPath));
        try
        {
            await ReadAsync(
                DspdmCatalog.BusinessObjects,
                [DspdmCatalog.BoName],
                [new DspdmFilter(DspdmCatalog.BoName, DspdmFilter.EqualsOperator, [JsonValue.Create(DspdmCatalog.BusinessObjects)])],
                DspdmCatalog.BoName,
                ct,
                pageSize: 10).ConfigureAwait(false);
            return new ProbeOutcome(true, 200, "DSPDM answered its health check and a read of its metadata", url.AbsolutePath);
        }
        catch (OsduStatusException ex)
        {
            return new ProbeOutcome(false, ex.StatusCode, HeaderRedaction.RedactMessage(ex.Message), url.AbsolutePath);
        }
        catch (DspdmRefusal ex)
        {
            return new ProbeOutcome(false, ex.HttpStatus, ex.Message, url.AbsolutePath);
        }
        catch (Exception ex) when (ex is DeliveryException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new ProbeOutcome(false, 0, HeaderRedaction.RedactMessage(ex.Message), url.AbsolutePath);
        }
    }

    /// <summary>A call's answer: a 500 DSPDM answered is its refusal, and a 500 it did not answer is the service failing.</summary>
    private static DspdmAnswer Settled(HttpFetchResult result, Uri url, HttpMethod method)
    {
        if ((int)result.Status != 500)
        {
            return DspdmAnswer.Parse(result, url);
        }

        if (DspdmAnswer.TryParse(result, url) is not null)
        {
            // A 500 whose answer is not negative is no answer DSPDM gives; it is not taken as a success.
            throw new DeliveryException($"{url.AbsolutePath}: DSPDM answered HTTP 500 with an answer that does not say it failed.");
        }

        throw new OsduStatusException(
            500,
            $"HTTP 500 InternalServerError from {method} {url.AbsolutePath}: {HeaderRedaction.RedactMessage(OsduError.Describe(result.BodyText))}");
    }
}
