using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What a dimension asks the search service (openapi search v2): an aggregation in the form the service parses, over the
/// dimension's query with the slice's range added; a cursor read returning the field and nothing more; and the counts that
/// say how complete a read is. Every request is checked against the pinned search contract.
/// </summary>
public class SearchDistinctSourceTests
{
    private const string Kind = "osdu:wks:work-product-component--WellLog:*";
    private static readonly OsduField Mnemonic = OsduField.Text("data.Curves.Mnemonic", "data.Curves");
    private static readonly OsduField Name = OsduField.Text("data.FacilityName");

    private static (SearchDistinctSource Source, HttpRuntime Runtime) Source(FakeHttpHandler handler, OsduField field, string? query)
    {
        var runtime = new HttpRuntime(
            new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
            new SecretResolver([new EnvSecretProvider()]), new TestClock(), handler, allowLoopback: true);
        var client = new OsduHttpClient(
            runtime, "http://localhost", new TargetAuth { Type = TargetAuthType.None },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
        var search = new OsduSearch(client, "/api/search/v2/query", "/api/search/v2/query_with_cursor", NullLogger.Instance);
        return (new SearchDistinctSource(search, Kind, query, field), runtime);
    }

    private static string Aggregation(long total, params (string? Key, long Count)[] buckets)
        => new JsonObject
        {
            ["results"] = new JsonArray(),
            ["totalCount"] = total,
            ["aggregations"] = new JsonArray(buckets.Select(b => (JsonNode?)new JsonObject { ["key"] = b.Key is null ? null : JsonValue.Create(b.Key), ["count"] = b.Count }).ToArray()),
        }.ToJsonString();

    [Fact]
    public async Task An_aggregation_asks_the_nested_form_over_the_dimensions_query_and_the_slices_range()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/api/search/v2/query", HttpStatusCode.OK, Aggregation(7, ("DT", 3), ("GR", 4)));
        var (source, runtime) = Source(handler, Mnemonic, "data.WellboreID:\"dev:master-data--Wellbore:1:\"");
        using (runtime)
        {
            var (total, buckets) = await source.AggregateAsync(new DistinctSlice("DT", "RHOB"), CancellationToken.None);

            Assert.Equal(7, total);
            Assert.Equal([new OsduSearchBucket("DT", 3), new OsduSearchBucket("GR", 4)], buckets);
        }

        var body = JsonNode.Parse(Assert.Single(handler.Calls).Body!)!;
        Assert.Equal(Kind, body["kind"]!.GetValue<string>());
        Assert.Equal("nested(data.Curves, Mnemonic.keyword)", body["aggregateBy"]!.GetValue<string>());
        Assert.Equal(
            "(data.WellboreID:\"dev:master-data--Wellbore:1:\") AND (nested(data.Curves, (Mnemonic.keyword:[\"DT\" TO \"RHOB\"})))",
            body["query"]!.GetValue<string>());
        Assert.True(body["trackTotalCount"]!.GetValue<bool>());
        OsduContracts.AssertConform(handler.Calls, null, OsduContracts.Search);
    }

    [Fact]
    public async Task A_query_that_takes_everything_adds_nothing_to_a_whole_slice()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/api/search/v2/query", HttpStatusCode.OK, Aggregation(1, ("A", 1)));
        var (source, runtime) = Source(handler, Name, "*");
        using (runtime)
        {
            _ = await source.AggregateAsync(DistinctSlice.Whole, CancellationToken.None);
        }

        var body = JsonNode.Parse(Assert.Single(handler.Calls).Body!)!;
        Assert.Null(body["query"]);
        Assert.Equal("data.FacilityName.keyword", body["aggregateBy"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_group_the_service_names_no_key_for_is_kept_apart_from_the_text_null()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/api/search/v2/query", HttpStatusCode.OK, Aggregation(2, (null, 1), ("null", 1)));
        var (source, runtime) = Source(handler, Name, null);
        using (runtime)
        {
            var (_, buckets) = await source.AggregateAsync(DistinctSlice.Whole, CancellationToken.None);

            Assert.Equal([new OsduSearchBucket(null, 1), new OsduSearchBucket("null", 1)], buckets);
        }
    }

    [Fact]
    public async Task A_scan_returns_only_the_field_and_counts_each_object_of_a_nested_array_as_one()
    {
        var first = new JsonObject
        {
            ["totalCount"] = 2,
            ["cursor"] = "c1",
            ["results"] = new JsonArray(
                new JsonObject { ["id"] = "dev:wpc--WellLog:1", ["data"] = new JsonObject { ["Curves"] = new JsonArray(new JsonObject { ["Mnemonic"] = "GR" }, new JsonObject { ["Mnemonic"] = "DT" }) } },
                new JsonObject { ["id"] = "dev:wpc--WellLog:2", ["data"] = new JsonObject { ["Curves"] = new JsonArray(new JsonObject { ["Mnemonic"] = null }, new JsonObject { ["Other"] = "x" }) } }),
        }.ToJsonString();
        var last = new JsonObject { ["cursor"] = "c2", ["results"] = new JsonArray() }.ToJsonString();
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/api/search/v2/query_with_cursor", hit => FakeHttpHandler.Json(HttpStatusCode.OK, hit == 0 ? first : last));
        var (source, runtime) = Source(handler, Mnemonic, null);
        var units = new List<DistinctUnit>();
        using (runtime)
        {
            await foreach (var page in source.ScanAsync(new DistinctSlice("A", null), CancellationToken.None))
            {
                units.AddRange(page);
            }
        }

        // Three objects hold the field (one of them null); the object without it holds nothing to count.
        Assert.Equal(3, units.Count);
        Assert.Equal([ScannedValue.OfString("GR")], units[0].Values);
        Assert.Equal([ScannedValue.OfString("DT")], units[1].Values);
        Assert.Equal([ScannedValue.Null], units[2].Values);

        var body = JsonNode.Parse(handler.Calls[0].Body!)!;
        Assert.Equal(["id", "data.Curves.Mnemonic"], body["returnedFields"]!.AsArray().Select(f => f!.GetValue<string>()));
        Assert.Equal("nested(data.Curves, (Mnemonic.keyword:[\"A\" TO *]))", body["query"]!.GetValue<string>());
        OsduContracts.AssertConform(handler.Calls, null, OsduContracts.Search);
    }

    [Fact]
    public void A_record_outside_a_nested_array_is_one_unit_holding_every_value_on_its_path()
    {
        using var hit = JsonDocument.Parse("""{"id":"x","acl":{"viewers":["a@x","b@x",null]},"legal":{"legaltags":"t"}}""");

        var viewers = Assert.Single(SearchDistinctSource.Units(hit.RootElement, OsduField.Keyword("acl.viewers")));
        Assert.Equal([ScannedValue.OfString("a@x"), ScannedValue.OfString("b@x"), ScannedValue.Null], viewers.Values);

        Assert.Empty(SearchDistinctSource.Units(hit.RootElement, OsduField.Keyword("ancestry.parents")));
    }

    [Fact]
    public void Numbers_booleans_and_objects_are_read_as_what_they_are()
    {
        using var hit = JsonDocument.Parse("""{"data":{"V":[1.50,true,{"a":1},"t"]}}""");

        var unit = Assert.Single(SearchDistinctSource.Units(hit.RootElement, OsduField.Keyword("data.V")));
        Assert.Equal([ScannedValue.OfNumber("1.50"), ScannedValue.OfBoolean(true), ScannedValue.Composite, ScannedValue.OfString("t")], unit.Values);
    }

    [Fact]
    public async Task The_completeness_counts_ask_the_exact_field_and_its_analysed_one()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/api/search/v2/query", HttpStatusCode.OK, Aggregation(5));
        var (source, runtime) = Source(handler, Name, "data.Status:\"active\"");
        using (runtime)
        {
            Assert.Equal(5, await source.CountAsync(DistinctCheck.Records, CancellationToken.None));
            Assert.Equal(5, await source.CountAsync(DistinctCheck.WithValue, CancellationToken.None));
            Assert.Equal(5, await source.CountAsync(DistinctCheck.TooLong, CancellationToken.None));
        }

        var queries = handler.Calls.Select(c => JsonNode.Parse(c.Body!)!["query"]!.GetValue<string>()).ToList();
        Assert.Equal(
            [
                "data.Status:\"active\"",
                "(data.Status:\"active\") AND (_exists_:data.FacilityName.keyword)",
                "(data.Status:\"active\") AND (_exists_:data.FacilityName AND NOT _exists_:data.FacilityName.keyword)",
            ],
            queries);
        OsduContracts.AssertConform(handler.Calls, null, OsduContracts.Search);
    }

    [Fact]
    public async Task Inside_a_nested_array_only_the_records_are_counted()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/api/search/v2/query", HttpStatusCode.OK, Aggregation(9));
        var (source, runtime) = Source(handler, Mnemonic, null);
        using (runtime)
        {
            Assert.Equal(9, await source.CountAsync(DistinctCheck.Records, CancellationToken.None));
            Assert.Null(await source.CountAsync(DistinctCheck.WithValue, CancellationToken.None));
            Assert.Null(await source.CountAsync(DistinctCheck.TooLong, CancellationToken.None));
        }

        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task A_number_is_never_counted_as_too_long()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/api/search/v2/query", HttpStatusCode.OK, Aggregation(9));
        var (source, runtime) = Source(handler, OsduField.Number("data.TotalDepth"), null);
        using (runtime)
        {
            Assert.Null(await source.CountAsync(DistinctCheck.TooLong, CancellationToken.None));
        }

        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task A_refused_request_fails_the_call_rather_than_passing_for_no_values()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/api/search/v2/query", HttpStatusCode.BadRequest, """{"code":400,"message":"bad query"}""");
        var (source, runtime) = Source(handler, Name, null);
        using (runtime)
        {
            await Assert.ThrowsAnyAsync<DeliveryException>(() => source.AggregateAsync(DistinctSlice.Whole, CancellationToken.None));
        }
    }
}
