using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Http;

namespace SqlFlow.Delivery.Engine.Search;

/// <summary>What a search asks for (openapi search v2, <c>QueryRequest</c>): the kind, the query, a spatial filter, the order and the fields returned.</summary>
public sealed record OsduSearchQuery
{
    /// <summary>The kind searched, <c>authority:source:entityType:version</c>, wildcards allowed per segment.</summary>
    public required string Kind { get; init; }

    /// <summary>A Lucene query narrowing the kind, or null for every record of it.</summary>
    public string? Query { get; init; }

    /// <summary>The search's <c>spatialFilter</c>, as a document wrote it, or null.</summary>
    public JsonObject? Spatial { get; init; }

    /// <summary>The fields the search orders by, each descending or not; empty leaves the order to the service.</summary>
    public IReadOnlyList<(string Field, bool Descending)> Sort { get; init; } = [];

    /// <summary>The fields each hit is projected onto; empty returns whole hits.</summary>
    public IReadOnlyList<string> ReturnedFields { get; init; } = [];

    /// <summary>The request body of this query with the paging properties a call adds.</summary>
    internal JsonObject Body(int limit)
    {
        var body = new JsonObject { ["kind"] = Kind, ["limit"] = limit };
        if (!string.IsNullOrWhiteSpace(Query))
        {
            body["query"] = Query;
        }

        if (Spatial is not null)
        {
            body["spatialFilter"] = Spatial.DeepClone();
        }

        if (Sort.Count > 0)
        {
            body["sort"] = new JsonObject
            {
                ["field"] = new JsonArray(Sort.Select(s => (JsonNode?)JsonValue.Create(s.Field)).ToArray()),
                ["order"] = new JsonArray(Sort.Select(s => (JsonNode?)JsonValue.Create(s.Descending ? "DESC" : "ASC")).ToArray()),
            };
        }

        if (ReturnedFields.Count > 0)
        {
            body["returnedFields"] = new JsonArray(ReturnedFields.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray());
        }

        return body;
    }
}

/// <summary>One page of a cursor search: its hits, valid until the next page is asked for, and the count the first page reports.</summary>
public sealed class OsduSearchPage
{
    internal OsduSearchPage(IReadOnlyList<JsonElement> hits, long? totalCount, int returned)
    {
        Hits = hits;
        TotalCount = totalCount;
        Returned = returned;
    }

    /// <summary>The page's hits: every one, or those not seen on an earlier page when the search deduplicates.</summary>
    public IReadOnlyList<JsonElement> Hits { get; }

    /// <summary>The records the query matches, as the first page reports it; null on later pages.</summary>
    public long? TotalCount { get; }

    /// <summary>The hits the service returned on this page, repeats included.</summary>
    public int Returned { get; }
}

/// <summary>One group of an aggregation: a distinct value of the field and how many records hold it.</summary>
public sealed record OsduSearchBucket(string Key, long Count);

/// <summary>
/// The OSDU search service as the module reads it (openapi search v2): the exact count of what a query matches, the distinct
/// values of a field with their counts, one page of hits, and every hit paged through a cursor. One implementation serves
/// every reader: a retrieval's pages and an assertion's reads go through the same requests, the same cursor handling and
/// the same release of a cursor abandoned part way through.
/// </summary>
public sealed class OsduSearch
{
    /// <summary>The largest page the search service returns (openapi search v2, limit).</summary>
    public const int MaxPage = 1000;

    /// <summary>
    /// Pages in a row that bring nothing new before a deduplicating read takes the search as finished. A deployment may hand
    /// back the same cursor for every page while the context behind it advances (Azure Data Manager for Energy did on
    /// 2026-09-18), so a page with nothing new, not a repeated cursor, is what says a read is going in circles.
    /// </summary>
    public const int BarrenPages = 3;

    private readonly OsduHttpClient _client;
    private readonly string _queryPath;
    private readonly string _cursorPath;
    private readonly ILogger _logger;

    public OsduSearch(OsduHttpClient client, string queryPath, string cursorPath, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(queryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(cursorPath);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _queryPath = queryPath;
        _cursorPath = cursorPath;
        _logger = logger;
    }

    /// <summary>The exact number of records the query matches (openapi search v2, POST /query with trackTotalCount).</summary>
    public async Task<long> CountAsync(OsduSearchQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var (total, _, _) = await QueryAsync(query, 1, null, ct).ConfigureAwait(false);
        return total;
    }

    /// <summary>
    /// The distinct values of <paramref name="field"/> among the records the query matches, each with its count (openapi
    /// search v2, POST /query with aggregateBy), and the query's total. The service decides how many groups it returns.
    /// </summary>
    public async Task<(long Total, IReadOnlyList<OsduSearchBucket> Buckets)> AggregateAsync(OsduSearchQuery query, string field, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        var (total, _, buckets) = await QueryAsync(query, 1, field, ct).ConfigureAwait(false);
        return (total, buckets);
    }

    /// <summary>The first <paramref name="limit"/> hits the query matches (openapi search v2, POST /query), each as its own JSON, and the total.</summary>
    public async Task<(long Total, IReadOnlyList<JsonObject> Hits)> FirstAsync(OsduSearchQuery query, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var (total, hits, _) = await QueryAsync(query, Math.Clamp(limit, 1, MaxPage), null, ct).ConfigureAwait(false);
        return (total, hits);
    }

    private async Task<(long Total, IReadOnlyList<JsonObject> Hits, IReadOnlyList<OsduSearchBucket> Buckets)> QueryAsync(
        OsduSearchQuery query, int limit, string? aggregateBy, CancellationToken ct)
    {
        var url = _client.Url(_queryPath);
        var body = query.Body(limit);
        body["trackTotalCount"] = true;
        if (aggregateBy is not null)
        {
            body["aggregateBy"] = aggregateBy;
        }

        var result = await _client.SendJsonAsync(HttpMethod.Post, url, body, null, ct, idempotent: true).ConfigureAwait(false);
        var root = OsduHttpClient.ParseJson(result, url);
        var total = root.TryGetProperty("totalCount", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt64(out var value)
            ? value
            : throw new DeliveryException($"{url.AbsolutePath} did not report totalCount for kind {query.Kind}.");
        var hits = new List<JsonObject>();
        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var hit in results.EnumerateArray())
            {
                if (JsonNode.Parse(hit.GetRawText()) is JsonObject obj)
                {
                    hits.Add(obj);
                }
            }
        }

        var buckets = new List<OsduSearchBucket>();
        if (aggregateBy is not null && root.TryGetProperty("aggregations", out var aggregations) && aggregations.ValueKind == JsonValueKind.Array)
        {
            foreach (var bucket in aggregations.EnumerateArray())
            {
                if (bucket.ValueKind == JsonValueKind.Object
                    && bucket.TryGetProperty("key", out var key)
                    && bucket.TryGetProperty("count", out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt64(out var c))
                {
                    buckets.Add(new OsduSearchBucket(key.ValueKind == JsonValueKind.String ? key.GetString() ?? string.Empty : key.GetRawText(), c));
                }
            }
        }

        return (total, hits, buckets);
    }

    /// <summary>
    /// Every hit the query matches, a page at a time (openapi search v2, POST /query_with_cursor): the first page asks for
    /// the total, the next ones follow the cursor, and the read ends on an empty page or when the service hands back no
    /// cursor. With <paramref name="deduplicate"/>, a hit whose id an earlier page returned is left out, and
    /// <see cref="BarrenPages"/> pages in a row with nothing new end the read. A page's hits are valid until the next page is
    /// asked for. A read stopped part way through (an exception, or the caller leaving the loop) releases its cursor.
    /// </summary>
    public async IAsyncEnumerable<OsduSearchPage> PagesAsync(
        OsduSearchQuery query, int pageSize, bool deduplicate, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var url = _client.Url(_cursorPath);
        var limit = Math.Clamp(pageSize, 1, MaxPage);
        var seen = deduplicate ? new HashSet<string>(StringComparer.Ordinal) : null;
        string? cursor = null;
        var finished = false;
        var barren = 0;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var body = query.Body(limit);
                if (cursor is null)
                {
                    body["trackTotalCount"] = true;
                }
                else
                {
                    body["cursor"] = cursor;
                }

                var result = await _client.SendJsonAsync(HttpMethod.Post, url, body, null, ct, idempotent: true).ConfigureAwait(false);
                using var document = JsonDocument.Parse(result.Body);
                var root = document.RootElement;
                long? total = cursor is null && root.TryGetProperty("totalCount", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt64(out var t)
                    ? t
                    : null;
                var hits = root.TryGetProperty("results", out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().ToList() : [];
                var kept = seen is null ? hits : hits.Where(h => IdOf(h) is not { } id || seen.Add(id)).ToList();
                cursor = root.TryGetProperty("cursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
                if (hits.Count == 0 || string.IsNullOrEmpty(cursor))
                {
                    finished = true;
                }

                yield return new OsduSearchPage(kept, total, hits.Count);
                if (finished)
                {
                    yield break;
                }

                barren = seen is not null && kept.Count == 0 ? barren + 1 : 0;
                if (barren >= BarrenPages)
                {
                    _logger.LogWarning(
                        "search of {Kind}: {Pages} page(s) in a row brought no record not already read; the read ends there.",
                        query.Kind, barren);
                    yield break;
                }
            }
        }
        finally
        {
            if (!finished && cursor is not null)
            {
                await CloseCursorAsync(cursor).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The id of a hit, or null when it carries none.</summary>
    public static string? IdOf(JsonElement hit)
        => hit.ValueKind == JsonValueKind.Object && hit.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;

    /// <summary>
    /// Releases a search context a read stopped part way through (openapi search v2, DELETE /query_with_cursor/{cursor}), so
    /// an abandoned read does not hold index resources until it expires. The read's own outcome is what its caller reports;
    /// failing to close is logged and nothing more.
    /// </summary>
    private async Task CloseCursorAsync(string cursor)
    {
        try
        {
            var url = _client.Url(_cursorPath.TrimEnd('/') + "/{id}", cursor);
            await _client.SendJsonAsync(HttpMethod.Delete, url, null, new HashSet<int> { 404 }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlFlowException or HttpRequestException)
        {
            _logger.LogWarning("Could not close the search cursor after the read stopped: {Message}", HeaderRedaction.RedactMessage(ex.Message));
        }
    }

    /// <summary>A date and time as a Lucene range bound takes it, or <c>*</c> for an open end.</summary>
    public static string LuceneTime(DateTime? value)
        => value is { } v ? v.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) : "*";
}

/// <summary>What one storage read returned: the records it holds, and the ids it does not hand over.</summary>
/// <param name="Records">The records storage returned, in no particular order.</param>
/// <param name="Missing">The ids asked for that storage did not return: deleted, never written, or hidden from the caller.</param>
/// <param name="Invalid">The ids storage named under invalidRecords, a subset of <paramref name="Missing"/>.</param>
public sealed record StorageRead(IReadOnlyList<JsonElement> Records, IReadOnlyList<string> Missing, IReadOnlyList<string> Invalid);

/// <summary>
/// Reads records by id from the storage service (openapi storage v2, POST /query/records), a hundred ids per request, and
/// asks once more for the ids storage says to retry. An id storage does not return (deleted, never written, or one the
/// caller may not read, which storage leaves out without a word) is reported missing, never guessed at.
/// </summary>
public sealed class StorageRecords
{
    /// <summary>The most ids one request reads (openapi storage v2, MultiRecordIds.records maxItems).</summary>
    public const int Batch = 100;

    private readonly OsduHttpClient _client;
    private readonly string _path;

    public StorageRecords(OsduHttpClient client, string path)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _client = client;
        _path = path;
    }

    /// <summary>Reads up to <see cref="Batch"/> ids in one request (and one more for the ids storage asks to retry).</summary>
    public async Task<StorageRead> ReadAsync(IReadOnlyList<string> ids, IReadOnlyList<string>? attributes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count > Batch)
        {
            throw new ArgumentException($"One storage read takes at most {Batch} ids.", nameof(ids));
        }

        var url = _client.Url(_path);
        var records = new List<JsonElement>(ids.Count);
        var found = new HashSet<string>(StringComparer.Ordinal);
        var invalid = new List<string>();
        var pending = ids;
        for (var attempt = 0; attempt < 2 && pending.Count > 0; attempt++)
        {
            var body = new JsonObject { ["records"] = new JsonArray(pending.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()) };
            if (attributes is { Count: > 0 })
            {
                body["attributes"] = new JsonArray(attributes.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray());
            }

            // A service that holds none of the ids may answer 404 rather than an empty list; either way, none was found.
            var result = await _client.SendJsonAsync(HttpMethod.Post, url, body, new HashSet<int> { 404 }, ct, idempotent: true).ConfigureAwait(false);
            if ((int)result.Status == 404 || result.Body.Length == 0)
            {
                break;
            }

            using var document = JsonDocument.Parse(result.Body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new DeliveryException($"{url.AbsolutePath} answered with something other than a JSON object.");
            }

            if (root.TryGetProperty("records", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var record in array.EnumerateArray())
                {
                    if (OsduSearch.IdOf(record) is { } id && found.Add(id))
                    {
                        records.Add(record.Clone());
                    }
                }
            }

            var retry = new List<string>();
            if (root.TryGetProperty("retryRecords", out var retries) && retries.ValueKind == JsonValueKind.Array)
            {
                foreach (var id in retries.EnumerateArray())
                {
                    if (id.ValueKind == JsonValueKind.String && id.GetString() is { } text && !found.Contains(text))
                    {
                        retry.Add(text);
                    }
                }
            }

            if (root.TryGetProperty("invalidRecords", out var rejected) && rejected.ValueKind == JsonValueKind.Array)
            {
                foreach (var id in rejected.EnumerateArray())
                {
                    if (id.ValueKind == JsonValueKind.String && id.GetString() is { } text && !invalid.Contains(text, StringComparer.Ordinal))
                    {
                        invalid.Add(text);
                    }
                }
            }

            pending = retry;
        }

        return new StorageRead(records, ids.Where(id => !found.Contains(id)).ToList(), invalid);
    }
}
