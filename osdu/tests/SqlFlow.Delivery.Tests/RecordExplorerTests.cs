using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core.Compute;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The explorer's reads of what OSDU holds (osdu/docs/explorer.md): what a reader typed read once into a query, a page of the
/// records it finds with their names and the groups of a property, the kinds they are of, the properties a kind's records
/// hold, a query the service refuses answered plainer or with its words, and a record read from the storage service.
/// </summary>
public class RecordExplorerTests
{
    private const string Wellbore = "dev:master-data--Wellbore:NO-33-9-C-28-B";

    private static ExplorerReading Read(string? text, string? kind = null, bool lucene = false)
        => RecordExplorer.Interpret(new ExplorerSearch { Text = text, Kind = kind, Lucene = lucene }, "dev");

    [Fact]
    public void Nothing_typed_is_every_record_and_a_lucene_query_goes_as_written()
    {
        Assert.Equal(new ExplorerReading("everything", null), Read("  "));
        Assert.Equal(new ExplorerReading("lucene", "data.FacilityName:\"NO 33*\""), Read(" data.FacilityName:\"NO 33*\" ", lucene: true));
    }

    [Fact]
    public void A_whole_id_finds_the_record_the_ids_that_go_on_from_it_and_its_unique_part_under_another_type()
    {
        var reading = Read(Wellbore + ":1712345678901234");

        Assert.Equal("id", reading.Reading);
        Assert.Equal(
            "id:\"dev:master-data--Wellbore:NO-33-9-C-28-B\" OR id:dev\\:master\\-data\\-\\-Wellbore\\:NO\\-33\\-9\\-C\\-28\\-B* OR id:dev\\:*\\:NO\\-33\\-9\\-C\\-28\\-B",
            reading.Query);
        Assert.Equal("id:\"dev:master-data--Wellbore:NO-33-9-C-28-B\" OR id:dev\\:master\\-data\\-\\-Wellbore\\:NO\\-33\\-9\\-C\\-28\\-B*", reading.Plainer);
        Assert.Equal("the same unique part under another type", reading.Dropped);
    }

    [Fact]
    public void The_start_of_an_id_finds_the_ids_that_start_with_it_in_the_partition_when_it_starts_at_its_type()
    {
        Assert.Equal(new ExplorerReading("idPrefix", "id:dev\\:master\\-data\\-\\-Wellbore\\:NO\\-33*"), Read("master-data--Wellbore:NO-33"));
        Assert.Equal(new ExplorerReading("idPrefix", "id:test\\:work\\-product\\-component\\-\\-WellLog\\:*"), Read("test:work-product-component--WellLog:"));
    }

    [Fact]
    public void Text_is_a_phrase_and_one_word_in_a_kind_of_one_type_may_also_be_the_end_of_an_id_of_it()
    {
        Assert.Equal(new ExplorerReading("text", "\"NO 33/9-C-28 B\""), Read("NO 33/9-C-28 B", "*:*:master-data--Wellbore:*"));

        var reading = Read("NO-33-9-C-28-B", "*:*:master-data--Wellbore:*");
        Assert.Equal(
            "\"NO-33-9-C-28-B\" OR id:\"dev:master-data--Wellbore:NO-33-9-C-28-B\" OR id:dev\\:master\\-data\\-\\-Wellbore\\:NO\\-33\\-9\\-C\\-28\\-B*",
            reading.Query);
        Assert.Equal("\"NO-33-9-C-28-B\"", reading.Plainer);

        // In every kind, a word is a phrase alone; a minted unique part is looked for at the end of any id of the partition.
        Assert.Equal(new ExplorerReading("text", "\"Gullfaks\""), Read("Gullfaks"));
        var minted = Read("26c5ab12-4f1e-4d2a-9b7e-0c1d2e3f4a5b");
        Assert.Equal("\"26c5ab12-4f1e-4d2a-9b7e-0c1d2e3f4a5b\" OR id:dev\\:*\\:26c5ab12\\-4f1e\\-4d2a\\-9b7e\\-0c1d2e3f4a5b*", minted.Query);
        Assert.Equal("\"26c5ab12-4f1e-4d2a-9b7e-0c1d2e3f4a5b\"", minted.Plainer);
    }

    [Fact]
    public void The_mentions_of_a_record_are_the_records_holding_its_id_the_record_itself_left_out()
    {
        var reading = RecordExplorer.Interpret(new ExplorerSearch { Mentions = Wellbore + ":" }, "dev");

        Assert.Equal(new ExplorerReading("mentions", $"\"{Wellbore}\" AND NOT id:\"{Wellbore}\""), reading);
    }

    [Fact]
    public void A_value_with_an_angle_bracket_is_asked_as_a_phrase_since_no_escape_carries_it()
    {
        Assert.Equal(new ExplorerReading("text", "\"dev:x--Y<1\""), Read("dev:x--Y<1"));
    }

    [Fact]
    public void Property_values_narrow_the_reading_each_asked_exactly_as_the_platform_indexes_it()
    {
        var filters = new[]
        {
            new ExplorerFilter { Path = "data.FacilityTypeID", Value = "dev:reference-data--FacilityType:Wellbore:" },
            new ExplorerFilter { Path = "legal.legaltags", Index = OsduFieldIndex.Keyword, Value = "dev-equinor-private" },
        };

        Assert.Equal(
            "(\"NO 33\") AND data.FacilityTypeID.keyword:\"dev:reference-data--FacilityType:Wellbore:\" AND legal.legaltags:\"dev-equinor-private\"",
            RecordExplorer.Compose("\"NO 33\"", filters));
        Assert.Equal("legal.legaltags:\"dev-equinor-private\"", RecordExplorer.Compose(null, filters[1..]));
        Assert.Null(RecordExplorer.Compose(null, []));
    }

    [Theory]
    [InlineData("{\"kind\":\"osdu:wks\"}", "is not a kind")]
    [InlineData("{\"offset\":9950,\"limit\":100}", "first 10,000 records")]
    [InlineData("{\"limit\":500}", "1 to 200 records")]
    [InlineData("{\"text\":\"a\",\"mentions\":\"dev:master-data--Well:1\"}", "not both")]
    [InlineData("{\"mentions\":\"not an id\"}", "is not an OSDU record id")]
    [InlineData("{\"filters\":[{\"path\":\"data.Name\",\"value\":\"null\"}]}", "cannot narrow to data.Name")]
    [InlineData("{\"facet\":{\"path\":\"data..Name\"}}", "cannot be grouped by")]
    [InlineData("{\"sort\":\"newest\"}", "is not one the explorer reads")]
    [InlineData("{\"unknown\":true}", "is not one the explorer reads")]
    public void A_search_that_cannot_be_asked_is_refused_with_why(string json, string said)
    {
        var refused = Assert.Throws<DeliveryException>(() => ExplorerSearch.Parse(json));
        Assert.Contains(said, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_search_crosses_to_the_node_as_it_was_asked()
    {
        var search = new ExplorerSearch
        {
            Text = "NO 33",
            Kind = "*:*:master-data--Wellbore:*",
            Filters = [new ExplorerFilter { Path = "acl.viewers", Index = OsduFieldIndex.Keyword, Value = "data.default.viewers@dev.dataservices.energy" }],
            Sort = ExplorerSort.Modified,
            Offset = 200,
            Limit = 100,
            Facet = new ExplorerField { Path = "data.FacilityTypeID" },
        };

        var json = search.ToJson();
        var back = ExplorerSearch.Parse(json);

        Assert.Contains("\"sort\":\"modified\"", json, StringComparison.Ordinal);
        Assert.Contains("\"index\":\"keyword\"", json, StringComparison.Ordinal);
        Assert.Equal(json, back.ToJson());
    }

    [Fact]
    public async Task A_page_asks_the_kind_query_order_and_window_and_lists_each_record_by_its_name()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/api/search/v2/query", HttpStatusCode.OK, new JsonObject
        {
            ["results"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = Wellbore,
                    ["kind"] = "osdu:wks:master-data--Wellbore:1.1.0",
                    ["version"] = 1712345678901234,
                    ["modifyTime"] = "2026-09-01T10:00:00.000Z",
                    ["modifyUser"] = "loader@dev",
                    ["data"] = new JsonObject { ["FacilityName"] = "NO 33/9-C-28 B", ["Name"] = "not this one" },
                },
                new JsonObject { ["id"] = "dev:reference-data--UnitOfMeasure:m", ["kind"] = "osdu:wks:reference-data--UnitOfMeasure:1.0.0", ["data.Code"] = "m" },
                new JsonObject { ["kind"] = "osdu:wks:master-data--Wellbore:1.1.0" }),
            ["aggregations"] = new JsonArray(new JsonObject { ["key"] = "dev:reference-data--FacilityType:Wellbore:", ["count"] = 2 }),
            ["totalCount"] = 1234567,
        }.ToJsonString());
        var (explorer, runtime) = Explorer(handler);
        using (runtime)
        {
            var page = await explorer.SearchAsync(new ExplorerSearch
            {
                Text = "NO 33",
                Kind = "*:*:master-data--Wellbore:*",
                Sort = ExplorerSort.Modified,
                Offset = 100,
                Limit = 100,
                Facet = new ExplorerField { Path = "data.FacilityTypeID" },
            }, CancellationToken.None);

            Assert.Equal(("text", "\"NO 33\"", 1234567L, 100), (page.Reading, page.Query, page.Total, page.Offset));
            Assert.Null(page.Refusal);
            Assert.Empty(page.Notes);
            Assert.Equal(2, page.Hits.Count);
            Assert.Equal(new ExplorerHit(Wellbore, "osdu:wks:master-data--Wellbore:1.1.0", 1712345678901234, "NO 33/9-C-28 B", "data.FacilityName", null, null, "2026-09-01T10:00:00.000Z", "loader@dev"), page.Hits[0]);
            Assert.Equal(("m", "data.Code"), (page.Hits[1].Name, page.Hits[1].NameField));
            Assert.Equal(new ExplorerBucket("dev:reference-data--FacilityType:Wellbore:", 2), Assert.Single(page.Facet!));

            var body = JsonNode.Parse(Assert.Single(handler.Calls).Body!)!;
            Assert.Equal("*:*:master-data--Wellbore:*", body["kind"]!.GetValue<string>());
            Assert.Equal("\"NO 33\"", body["query"]!.GetValue<string>());
            Assert.Equal((100, 100, true), (body["offset"]!.GetValue<int>(), body["limit"]!.GetValue<int>(), body["trackTotalCount"]!.GetValue<bool>()));
            Assert.Equal("data.FacilityTypeID.keyword", body["aggregateBy"]!.GetValue<string>());
            Assert.Equal(["modifyTime"], body["sort"]!["field"]!.AsArray().Select(f => f!.GetValue<string>()));
            Assert.Equal(["DESC"], body["sort"]!["order"]!.AsArray().Select(f => f!.GetValue<string>()));
            Assert.Contains("data.FacilityName", body["returnedFields"]!.AsArray().Select(f => f!.GetValue<string>()));
            Assert.Equal("dev", Assert.Single(handler.Calls).Headers["data-partition-id"]);
        }

        OsduContracts.AssertConform(handler.Calls, null, OsduContracts.Search);
    }

    [Fact]
    public async Task An_order_the_service_will_not_sort_by_is_dropped_with_a_note()
    {
        var sorted = new FakeHttpHandler()
            .OnMatch(r => Body(r).Contains("\"sort\"", StringComparison.Ordinal), _ => FakeHttpHandler.Json(HttpStatusCode.BadRequest, "{\"code\":400,\"message\":\"No mapping found for [modifyTime] in order to sort on\"}"))
            .OnMatch(_ => true, _ => FakeHttpHandler.Json(HttpStatusCode.OK, Result(3, Wellbore)));
        var (explorer, runtime) = Explorer(sorted);
        using (runtime)
        {
            var page = await explorer.SearchAsync(new ExplorerSearch { Kind = "*:*:master-data--Wellbore:*", Sort = ExplorerSort.Modified }, CancellationToken.None);

            Assert.Equal(3, page.Total);
            Assert.Null(page.Refusal);
            Assert.Contains(page.Notes, n => n.Contains("would not order these records by modifyTime", StringComparison.Ordinal));
            Assert.Contains(page.Notes, n => n.Contains("No mapping found for [modifyTime]", StringComparison.Ordinal));
            Assert.Equal(2, sorted.Calls.Count);
        }
    }

    [Fact]
    public async Task A_clause_the_service_refuses_is_dropped_for_the_core_of_the_reading_with_a_note()
    {
        var handler = new FakeHttpHandler()
            .OnMatch(r => Body(r).Contains("id:dev\\\\:*", StringComparison.Ordinal), _ => FakeHttpHandler.Json(HttpStatusCode.BadRequest, "{\"message\":\"wildcard queries are not allowed\"}"))
            .OnMatch(_ => true, _ => FakeHttpHandler.Json(HttpStatusCode.OK, Result(1, Wellbore)));
        var (explorer, runtime) = Explorer(handler);
        using (runtime)
        {
            var page = await explorer.SearchAsync(new ExplorerSearch { Text = "26c5ab12-4f1e-4d2a" }, CancellationToken.None);

            Assert.Equal(("\"26c5ab12-4f1e-4d2a\"", 1L), (page.Query, page.Total));
            Assert.Contains(page.Notes, n => n.Contains("would not look for the records whose id ends with the word", StringComparison.Ordinal));
            Assert.Equal(2, handler.Calls.Count);
        }
    }

    [Fact]
    public async Task A_query_still_refused_is_the_page_with_the_services_words_not_a_failure()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/api/search/v2/query", HttpStatusCode.BadRequest, "{\"message\":\"Failed to parse query [data.Name:(]\"}");
        var (explorer, runtime) = Explorer(handler);
        using (runtime)
        {
            var page = await explorer.SearchAsync(new ExplorerSearch { Text = "data.Name:(", Lucene = true }, CancellationToken.None);

            Assert.Equal(0, page.Total);
            Assert.Empty(page.Hits);
            Assert.Contains("Failed to parse query", page.Refusal, StringComparison.Ordinal);
            Assert.Single(handler.Calls);
        }
    }

    [Fact]
    public async Task A_failure_other_than_a_refusal_fails_the_page_rather_than_passing_for_nothing_found()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/api/search/v2/query", HttpStatusCode.Forbidden, "{\"message\":\"no access\"}");
        var (explorer, runtime) = Explorer(handler);
        using (runtime)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => explorer.SearchAsync(new ExplorerSearch(), CancellationToken.None));
        }
    }

    [Fact]
    public async Task The_kinds_of_what_a_text_finds_are_counted_across_every_kind_in_order()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/api/search/v2/query", HttpStatusCode.OK, new JsonObject
        {
            ["results"] = new JsonArray(new JsonObject { ["id"] = Wellbore }),
            ["aggregations"] = new JsonArray(
                new JsonObject { ["key"] = "osdu:wks:work-product-component--WellLog:1.4.0", ["count"] = 12 },
                new JsonObject { ["key"] = "osdu:wks:master-data--Wellbore:1.1.0", ["count"] = 3 }),
            ["totalCount"] = 20,
        }.ToJsonString());
        var (explorer, runtime) = Explorer(handler);
        using (runtime)
        {
            var types = await explorer.TypesAsync(new ExplorerSearch { Text = "NO 33", Kind = "*:*:master-data--Wellbore:*", Offset = 300 }, CancellationToken.None);

            Assert.Equal(("text", 20L, 15L), (types.Reading, types.Total, types.Listed));
            Assert.Equal(["osdu:wks:master-data--Wellbore:1.1.0", "osdu:wks:work-product-component--WellLog:1.4.0"], types.Kinds.Select(k => k.Kind));

            // The list is what a kind is picked from, so the kind in view, its order and its page do not narrow it.
            var body = JsonNode.Parse(Assert.Single(handler.Calls).Body!)!;
            Assert.Equal(("*:*:*:*", "kind", "\"NO 33\""), (body["kind"]!.GetValue<string>(), body["aggregateBy"]!.GetValue<string>(), body["query"]!.GetValue<string>()));
            Assert.Null(body["offset"]);
            Assert.Null(body["sort"]);
        }

        OsduContracts.AssertConform(handler.Calls, null, OsduContracts.Search);
    }

    [Fact]
    public async Task The_properties_of_a_kind_are_read_from_one_record_each_typed_by_its_value()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/api/search/v2/query", HttpStatusCode.OK, new JsonObject
        {
            ["results"] = new JsonArray(new JsonObject
            {
                ["id"] = Wellbore,
                ["data"] = new JsonObject
                {
                    ["FacilityName"] = "NO 33/9-C-28 B",
                    ["SpudDate"] = "2019-03-01T00:00:00Z",
                    ["TotalDepth"] = 3120.5,
                    ["IsActive"] = true,
                    ["GeoContexts"] = new JsonArray(new JsonObject { ["GeoPoliticalEntityID"] = "dev:x--Y:z:" }),
                    ["SourceKeys"] = new JsonArray("a", "b"),
                    ["VerticalMeasurement"] = new JsonObject { ["Depth"] = 12 },
                    ["odd key"] = "left out",
                },
            }),
            ["totalCount"] = 1,
        }.ToJsonString());
        var (explorer, runtime) = Explorer(handler);
        using (runtime)
        {
            var fields = await explorer.FieldsAsync("osdu:wks:master-data--Wellbore:1.1.0", CancellationToken.None);

            Assert.Equal(Wellbore, fields.SampleId);
            Assert.Equal(RecordExplorer.EnvelopeFields, fields.Fields.Take(RecordExplorer.EnvelopeFields.Count));
            Assert.Equal(
                [
                    new ExplorerFieldInfo("data.FacilityName", "text"),
                    new ExplorerFieldInfo("data.IsActive", "boolean"),
                    new ExplorerFieldInfo("data.SourceKeys", "text"),
                    new ExplorerFieldInfo("data.SpudDate", "date"),
                    new ExplorerFieldInfo("data.TotalDepth", "number"),
                    new ExplorerFieldInfo("data.VerticalMeasurement.Depth", "number"),
                ],
                fields.Fields.Skip(RecordExplorer.EnvelopeFields.Count));

            // One whole record, nothing projected away.
            var body = JsonNode.Parse(Assert.Single(handler.Calls).Body!)!;
            Assert.Null(body["returnedFields"]);
            Assert.Equal(1, body["limit"]!.GetValue<int>());
        }
    }

    [Fact]
    public async Task The_task_reads_a_record_from_the_storage_service_whatever_route_the_flow_delivers_by()
    {
        var root = Samples.NewTempDirectory();
        var flowFile = System.IO.Path.Combine(root, "explorer-route.yaml");
        await File.WriteAllTextAsync(flowFile, """
            flowType: delivery
            name: explorer-route
            partitions:
              - dev
            source:
              connection: ${env:OSDU_DATA_DB}
              record: { object: OsduData.ing.Record, key: [record_id] }
              work: work
            render:
              mapping: Targeting@1.0.0
            target:
              endpoint: http://localhost
              protocol: ddms
            """);
        var record = new JsonObject { ["id"] = Wellbore, ["kind"] = "osdu:wks:master-data--Wellbore:1.1.0", ["version"] = 2, ["data"] = new JsonObject { ["FacilityName"] = "NO 33/9-C-28 B" } };
        var handler = new FakeHttpHandler()
            .OnMatch(r => Path(r) == "/api/storage/v2/records/versions/" + Wellbore, _ => FakeHttpHandler.Json(HttpStatusCode.OK, "{\"recordId\":\"" + Wellbore + "\",\"versions\":[1,2]}"))
            .OnMatch(r => Path(r) == "/api/storage/v2/records/" + Wellbore, _ => FakeHttpHandler.Json(HttpStatusCode.OK, record.ToJsonString()))
            .On(HttpMethod.Post, "/api/search/v2/query", HttpStatusCode.OK, Result(7, Wellbore));
        var operation = new ExploreOperation(Samples.Engine(ledger: null), handler, allowLoopback: true);

        var read = JsonNode.Parse(await operation.ExecuteAsync(Task(flowFile, ExploreOperation.ReadAction, new() { ["targetId"] = Wellbore + ":" }), CancellationToken.None))!;
        Assert.True(read["found"]!.GetValue<bool>());
        Assert.Equal((Wellbore, "explorer-route"), (read["targetId"]!.GetValue<string>(), read["flow"]!.GetValue<string>()));
        Assert.Equal([2L, 1L], read["versions"]!.AsArray().Select(v => v!.GetValue<long>()));
        Assert.Equal("NO 33/9-C-28 B", read["record"]!["data"]!["FacilityName"]!.GetValue<string>());

        var searched = JsonNode.Parse(await operation.ExecuteAsync(
            Task(flowFile, ExploreOperation.SearchAction, new() { [ExploreOperation.SearchArgument] = new ExplorerSearch { Text = "master-data--Wellbore:NO" }.ToJson() }),
            CancellationToken.None))!;
        Assert.Equal(("dev", "explorer-route"), (searched["partition"]!.GetValue<string>(), searched["connection"]!.GetValue<string>()));
        Assert.Equal(7, searched["answer"]!["total"]!.GetValue<long>());
        Assert.Equal("id:dev\\:master\\-data\\-\\-Wellbore\\:NO*", searched["answer"]!["query"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(searched["correlationId"]!.GetValue<string>()));

        // Every request carried the partition the flow is bound to, and each read is the storage or search service's own.
        Assert.All(handler.Calls, call => Assert.Equal("dev", call.Headers["data-partition-id"]));
        OsduContracts.AssertConform(handler.Calls, null, OsduContracts.Search, OsduContracts.Storage);

        var refused = await Assert.ThrowsAnyAsync<Exception>(() => operation.ExecuteAsync(Task(flowFile, "purge", new()), CancellationToken.None));
        Assert.Contains("is not a read the explorer makes", refused.Message, StringComparison.Ordinal);
    }

    private static ComputeTaskPayload Task(string flowFile, string action, Dictionary<string, string> arguments)
    {
        arguments["flowFile"] = flowFile;
        arguments[ExploreOperation.ActionArgument] = action;
        arguments[DeliveryOperation.PartitionArgument] = "dev";
        return new ComputeTaskPayload { Operation = ExploreOperation.OperationName, SourceRef = "explorer-route", Arguments = arguments };
    }

    private static (RecordExplorer Explorer, HttpRuntime Runtime) Explorer(FakeHttpHandler handler)
    {
        var runtime = new HttpRuntime(
            new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
            new SecretResolver([new EnvSecretProvider()]), new TestClock(), handler, allowLoopback: true);
        var client = new OsduHttpClient(
            runtime, "http://localhost", new TargetAuth { Type = TargetAuthType.None },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
        return (new RecordExplorer(client, "dev", NullLogger.Instance), runtime);
    }

    private static string Result(long total, params string[] ids)
        => new JsonObject
        {
            ["results"] = new JsonArray(ids.Select(id => (JsonNode?)new JsonObject { ["id"] = id }).ToArray()),
            ["totalCount"] = total,
        }.ToJsonString();

    /// <summary>A request's body; the handler has read it already, and buffered content reads again from the start.</summary>
    private static string Body(HttpRequestMessage request)
    {
        if (request.Content is null)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(request.Content.ReadAsStream());
        return reader.ReadToEnd();
    }

    /// <summary>A request's path with its escapes undone, so a rule names an id as it is written.</summary>
    private static string Path(HttpRequestMessage request) => Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
}
