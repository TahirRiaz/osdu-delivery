using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SqlFlow.Delivery.Engine.Protocols;

namespace SqlFlow.Delivery.Engine.Assertions;

/// <summary>The shape of one record's bulk data, as the DDMS describes it: its columns (curves) and its rows.</summary>
public sealed record BulkShape(IReadOnlyList<string> Columns, long Rows);

/// <summary>
/// One page of a record's bulk data in the DDMS's split orientation: the columns it holds and its rows, each a list of the
/// row's values in column order. The rows are valid until the next page is asked for.
/// </summary>
public sealed class BulkFrame
{
    internal BulkFrame(IReadOnlyList<string> columns, JsonElement data, long offset)
    {
        Columns = columns;
        Data = data;
        Offset = offset;
    }

    public IReadOnlyList<string> Columns { get; }

    /// <summary>The rows, a JSON array of arrays.</summary>
    public JsonElement Data { get; }

    /// <summary>The position of the first row of the page among the record's rows.</summary>
    public long Offset { get; }
}

/// <summary>
/// Reads a record's bulk data from the Wellbore DDMS (osdu/specs/wellbore-ddms, <c>GET /ddms/v3/{collection}/{id}/data</c>):
/// its shape with <c>describe</c>, and its rows a page at a time, only the columns asked for, in the DDMS's split JSON. A
/// record with no bulk data answers 404, which is read as having none. Every request asks for JSON exactly, since the DDMS
/// answers Parquet to anything else.
/// </summary>
public sealed class WellboreBulk
{
    /// <summary>The values one page reads at most: its rows times its columns, well inside the DDMS's own limit per request.</summary>
    public const int ValuesPerPage = 500_000;

    private readonly OsduHttpClient _client;
    private readonly string _root;

    public WellboreBulk(OsduHttpClient client, string ddmsRoot)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(ddmsRoot);
        _client = client;
        _root = ddmsRoot.TrimEnd('/');
    }

    /// <summary>The shape of a record's bulk data, or null when the DDMS holds none for it.</summary>
    public async Task<BulkShape?> DescribeAsync(string collection, string id, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var url = OsduHttpClient.WithQuery(DataUrl(collection, id), "describe", "true");
        var result = await _client.SendJsonAsync(HttpMethod.Get, url, null, new HashSet<int> { 404 }, ct).ConfigureAwait(false);
        if ((int)result.Status == 404)
        {
            return null;
        }

        var root = OsduHttpClient.ParseJson(result, url);
        var columns = root.TryGetProperty("columns", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.String ? c.GetString() ?? string.Empty : c.GetRawText()).ToList()
            : throw new DeliveryException($"{url.AbsolutePath}?describe=true did not list the columns of {id}.");
        var rows = root.TryGetProperty("numberOfRows", out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt64(out var count)
            ? count
            : throw new DeliveryException($"{url.AbsolutePath}?describe=true did not give the number of rows of {id}.");
        return new BulkShape(columns, rows);
    }

    /// <summary>
    /// The first <paramref name="rows"/> rows of <paramref name="columns"/> of a record's bulk data, a page at a time; every
    /// column when <paramref name="columns"/> is empty.
    /// </summary>
    public async IAsyncEnumerable<BulkFrame> ReadAsync(
        string collection, string id, IReadOnlyList<string> columns, long rows, int columnCount, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(columns);
        var perPage = Math.Max(1, ValuesPerPage / Math.Max(1, columnCount));
        for (long offset = 0; offset < rows; offset += perPage)
        {
            ct.ThrowIfCancellationRequested();
            var limit = Math.Min(perPage, rows - offset);
            var url = DataUrl(collection, id);
            if (columns.Count > 0)
            {
                url = OsduHttpClient.WithQuery(url, "curves", string.Join(',', columns));
            }

            url = OsduHttpClient.WithQuery(url, "offset", offset.ToString(CultureInfo.InvariantCulture));
            url = OsduHttpClient.WithQuery(url, "limit", limit.ToString(CultureInfo.InvariantCulture));
            url = OsduHttpClient.WithQuery(url, "orient", "split");
            var result = await _client.SendJsonAsync(HttpMethod.Get, url, null, null, ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(result.Body);
            var root = document.RootElement;
            var names = root.TryGetProperty("columns", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.String ? c.GetString() ?? string.Empty : c.GetRawText()).ToList()
                : throw new DeliveryException($"{url.AbsolutePath} answered without the columns of its split orientation.");
            var data = root.TryGetProperty("data", out var values) && values.ValueKind == JsonValueKind.Array
                ? values
                : throw new DeliveryException($"{url.AbsolutePath} answered without the rows of its split orientation.");
            yield return new BulkFrame(names, data, offset);
            if (data.GetArrayLength() < limit)
            {
                yield break;
            }
        }
    }

    private Uri DataUrl(string collection, string id) => _client.Url(_root + "/ddms/v3/" + collection + "/{id}/data", id);
}
