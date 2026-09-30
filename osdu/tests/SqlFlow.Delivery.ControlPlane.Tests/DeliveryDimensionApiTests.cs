using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The dimensions of dimension flows as the control plane serves them (docs/dimension-plan.md, Stage 5): the board of every
/// flow and of one, a dimension with its declaration and builds, its values and keys a page at a time in both orders, a
/// value with its keys, filter and history, each key with its label and its own filter, the change log, the filter of a set
/// of values, the search composed from values picked across dimensions, the exports, and the builds of a platform run. The
/// builds are written to the ledger as a run writes them, so what these tests hold the API to is what a real build leaves;
/// the tests never reach an OSDU.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryDimensionApiTests
{
    private const string WellLog = "osdu:wks:work-product-component--WellLog:*";

    private static readonly DimensionFieldState Field = new("text", "data.Curves", "nested(data.Curves, Mnemonic.keyword)", Repeats: true);

    private static readonly DimensionFieldState WellboreField = new("text", null, "data.WellboreID.keyword", Repeats: false);

    [Fact]
    public async Task A_dimension_flow_s_dimensions_values_keys_builds_filters_and_searches_are_served_in_its_partition()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var flowName = "api-dimension-" + suffix;
        var partition = "dm" + suffix;
        var repoId = FlowIdentity.FromName("repo/cp-dimensions-" + suffix);
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var otherPipelineId = CatalogIdentity.Pipeline(repoId, flowName + "-ingestion");
        var yaml = $$"""
            flowType: dimension
            name: {{flowName}}
            description: The curve mnemonics and wellbores the well logs hold.
            partitions: [{{partition}}]
            parameters:
              logSource: { default: STAT_COMP, description: The log source read. }
            source:
              endpoint: http://localhost
            dimensions:
              - name: CurveMnemonic
                description: Every curve mnemonic, in capitals.
                kind: "{{WellLog}}"
                path: data.Curves.Mnemonic
                clean: [trim, upper]
              - name: Wellbore
                description: The wellbore each log belongs to, by its name.
                kind: "{{WellLog}}"
                path: data.WellboreID
                label: data.FacilityName
            """;
        var flow = new DeliveryDocumentLoader().ParseDimension(yaml, "flows/" + flowName + ".yaml").ForRun(partition, RegisteredPartitions.None);
        var spec = flow.Dimensions[0];
        var now = new DateTime(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);

        await using (var db = CatalogDatabase.Create(cs))
        {
            db.Repos.Add(new CatalogRepo
            {
                Id = repoId, Name = "cp-dimensions-" + suffix, RemoteUrl = "https://example/cp-dimensions.git",
                RootPath = Path.GetTempPath(), FirstSeenUtc = now, LastSyncUtc = now,
            });
            db.Pipelines.Add(Pipeline(pipelineId, repoId, flowName, DimensionFlowDefinition.FlowTypeName, yaml, now));
            db.Pipelines.Add(Pipeline(otherPipelineId, repoId, flowName + "-ingestion", "ingestion", "flowType: ingestion", now));
            await db.SaveChangesAsync();
        }

        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        var dimensionId = 0;
        try
        {
            await ledger.RegisterLedgerAsync(new LedgerEntry
            {
                FlowId = flow.LedgerId, Partition = partition, Kind = LedgerKinds.Dimension, FlowName = flow.Name, LedgerName = flow.LedgerName,
            });

            // The first build found three spellings of GR, DT, and a key cleaning left nothing of.
            var firstRunId = Guid.NewGuid();
            var first = await BuildAsync(ledger, flow, firstRunId, now.AddHours(-3),
                ("GR", "GR", 5), ("gr", "GR", 3), ("Gamma Ray", "GR", 2), ("DT", "DT", 4), ("   ", null, 1));
            dimensionId = first.DimensionId;

            // The second no longer found "Gamma Ray", and found RHOB for the first time.
            var secondRunId = Guid.NewGuid();
            var second = await BuildAsync(ledger, flow, secondRunId, now.AddHours(-2),
                ("GR", "GR", 6), ("gr", "GR", 3), ("DT", "DT", 4), ("RHOB", "RHOB", 9), ("   ", null, 1));

            // The third failed before it wrote, which leaves the dimension as the second left it.
            var (_, failed) = await ledger.StartDimensionRunAsync(Declaration(flow, spec), Guid.NewGuid(), "dimension api tests", now.AddHours(-1));
            await ledger.CloseDimensionRunAsync(failed.DimensionRunId, DimensionRunStatus.Failed, DimensionReadCounts.None, "search answered 503", now.AddHours(-1).AddMinutes(1));

            // The wellbores are keyed by the id each log refers to, and valued by the name read from the wellbore; one wellbore
            // the label search did not find is valued by its id.
            var wellbores = await WellboresAsync(ledger, flow, now.AddMinutes(-30),
                ("dev:master-data--Wellbore:1001:", "15/9-F-1", 7), ("dev:master-data--Wellbore:1002:", "15/9-F-4", 3), ("dev:master-data--Wellbore:9999:", null, 1));
            var wellboreId = wellbores.DimensionId;

            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            // The board names the flow in the partition asked for, with its dimensions as their builds left them and the
            // failed build beside that; the totals count it failing.
            var board = await JsonAsync(client, token, $"/api/v1/delivery/dimensions?partition={partition}");
            Assert.Equal(partition, board.GetProperty("partition").GetString());
            var listed = board.GetProperty("flows").EnumerateArray().Single(f => f.GetProperty("name").GetString() == flowName);
            Assert.True(listed.GetProperty("buildsPartition").GetBoolean());
            Assert.Equal(flow.LedgerId, listed.GetProperty("ledgerId").GetGuid());
            Assert.Equal("logSource", listed.GetProperty("parameters")[0].GetProperty("name").GetString());
            var dimensions = listed.GetProperty("dimensions").EnumerateArray().ToList();
            Assert.Equal(["CurveMnemonic", "Wellbore"], dimensions.Select(d => d.GetProperty("name").GetString()));
            var dimension = dimensions[0];
            Assert.Equal(dimensionId, dimension.GetProperty("dimensionId").GetInt32());
            Assert.Equal(["trim", "upper"], dimension.GetProperty("clean").EnumerateArray().Select(s => s.GetString()));
            Assert.Empty(dimension.GetProperty("label").EnumerateArray());
            Assert.True(dimension.GetProperty("declared").GetBoolean());
            Assert.False(dimension.GetProperty("changed").GetBoolean());
            Assert.Equal((3L, 5L), (dimension.GetProperty("values").GetInt64(), dimension.GetProperty("keys").GetInt64()));
            Assert.Equal(second.DimensionRunId, dimension.GetProperty("current").GetProperty("buildId").GetInt64());
            Assert.Equal(DimensionRunStatus.Failed, dimension.GetProperty("latest").GetProperty("status").GetString());
            Assert.Equal("search answered 503", dimension.GetProperty("latest").GetProperty("error").GetString());
            Assert.Equal("nested(data.Curves, Mnemonic.keyword)", dimension.GetProperty("field").GetProperty("aggregateBy").GetString());
            Assert.Equal(["data.FacilityName"], dimensions[1].GetProperty("label").EnumerateArray().Select(l => l.GetString()));
            Assert.Equal((2L, 1L), (dimensions[1].GetProperty("current").GetProperty("labelled").GetInt64(), dimensions[1].GetProperty("current").GetProperty("unlabelled").GetInt64()));
            var own = await JsonAsync(client, token, $"/api/v1/delivery/flows/{pipelineId}/dimensions?partition={partition}");
            var totals = own.GetProperty("totals");
            Assert.Equal((2, 2, 1, 0), (totals.GetProperty("dimensions").GetInt32(), totals.GetProperty("built").GetInt32(), totals.GetProperty("failing").GetInt32(), totals.GetProperty("notBuilt").GetInt32()));

            // A dimension read alone names the pipeline that builds it, for the page's Build button.
            var detail = await JsonAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}");
            Assert.Equal(pipelineId, detail.GetProperty("pipelineId").GetGuid());
            Assert.Equal(partition, detail.GetProperty("partition").GetString());
            Assert.Equal("CurveMnemonic", detail.GetProperty("dimension").GetProperty("name").GetString());

            // Values in value order, each with the keys most records hold beside it.
            var values = await JsonAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/values");
            var items = values.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(["DT", "GR", "RHOB"], items.Select(m => m.GetProperty("value").GetString()));
            var gr = items[1];
            Assert.Equal(9, gr.GetProperty("records").GetInt64());
            Assert.Equal([("GR", 6L), ("gr", 3L)], gr.GetProperty("top").EnumerateArray().Select(t => (t.GetProperty("key").GetString(), t.GetProperty("count").GetInt64())));
            Assert.Equal(JsonValueKind.Null, values.GetProperty("next").ValueKind);

            // With the most records first, a page at a time: the cursor of one page starts the next.
            var top = await JsonAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/values?order=records&limit=2");
            Assert.Equal(["GR", "RHOB"], top.GetProperty("items").EnumerateArray().Select(m => m.GetProperty("value").GetString()));
            var rest = await JsonAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/values?order=records&limit=2&after={Uri.EscapeDataString(top.GetProperty("next").GetString()!)}");
            Assert.Equal(["DT"], rest.GetProperty("items").EnumerateArray().Select(m => m.GetProperty("value").GetString()));

            // A search finds a value by a key it holds now; one no build finds any more only when removed ones are asked.
            Assert.Equal(["GR"], (await JsonAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/values?search=g")).GetProperty("items").EnumerateArray().Select(m => m.GetProperty("value").GetString()));
            Assert.Empty((await JsonAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/values?search=gamma")).GetProperty("items").EnumerateArray());
            Assert.Single((await JsonAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/values?search=gamma&removed=true")).GetProperty("items").EnumerateArray());

            // A value's page: its keys, its filter, and the changes that took a key from it.
            var grId = gr.GetProperty("valueId").GetInt64();
            var value = await JsonAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/values/{grId}");
            Assert.Equal(["GR", "gr"], value.GetProperty("keys").EnumerateArray().Select(o => o.GetProperty("key").GetString()));
            var valueFilter = value.GetProperty("filter");
            var field = OsduField.Text("data.Curves.Mnemonic", "data.Curves");
            var expected = DimensionFilters.Of(field, ["GR", "gr"]);
            Assert.Equal(expected, valueFilter.GetProperty("filters").EnumerateArray().Select(f => f.GetString()));
            Assert.Equal(expected[0], gr.GetProperty("filter").GetString());
            var history = Assert.Single(value.GetProperty("history").EnumerateArray());
            Assert.Equal(("Gamma Ray", DimensionChangeKinds.Removed), (history.GetProperty("key").GetString(), history.GetProperty("change").GetString()));

            // Keys: those of no value, a value's with the most records first, and every one a page at a time, each with the
            // filter finding exactly its records.
            var left = await JsonAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/keys?leftOut=true");
            Assert.Equal(DimensionLeftOut.Empty, Assert.Single(left.GetProperty("items").EnumerateArray()).GetProperty("leftOut").GetString());
            var ofGr = (await JsonAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/keys?value={grId}&order=count")).GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(["GR", "gr"], ofGr.Select(o => o.GetProperty("key").GetString()));
            Assert.Equal(DimensionFilters.Of(field, ["gr"])[0], ofGr[1].GetProperty("filter").GetString());
            Assert.Equal(JsonValueKind.Null, ofGr[1].GetProperty("label").ValueKind);
            var page = await JsonAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/keys?limit=3");
            var next = await JsonAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/keys?limit=3&after={Uri.EscapeDataString(page.GetProperty("next").GetString()!)}");
            Assert.Equal(5, page.GetProperty("items").GetArrayLength() + next.GetProperty("items").GetArrayLength());

            // A key of a reference is the id the index holds, its value the name read from the record it names; a search finds
            // it by either.
            var wellboreField = OsduField.Text("data.WellboreID");
            var named = Assert.Single((await JsonAsync(client, token, $"/api/v1/delivery/dimensions/{wellboreId}/keys?search=F-4")).GetProperty("items").EnumerateArray());
            Assert.Equal(("dev:master-data--Wellbore:1002:", "15/9-F-4", "dev:master-data--Wellbore:1002", "15/9-F-4"),
                (named.GetProperty("key").GetString(), named.GetProperty("label").GetString(), named.GetProperty("labelFrom").GetString(), named.GetProperty("value").GetString()));
            Assert.Equal(DimensionFilters.Of(wellboreField, ["dev:master-data--Wellbore:1002:"])[0], named.GetProperty("filter").GetString());
            var unnamed = Assert.Single((await JsonAsync(client, token, $"/api/v1/delivery/dimensions/{wellboreId}/keys?search=9999")).GetProperty("items").EnumerateArray());
            Assert.Equal(("dev:master-data--Wellbore:9999:", JsonValueKind.Null), (unnamed.GetProperty("value").GetString(), unnamed.GetProperty("label").ValueKind));

            // The builds, newest first, and the change log, newest first, narrowed by build and by kind.
            var builds = await JsonAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/builds");
            Assert.Equal([failed.DimensionRunId, second.DimensionRunId, first.DimensionRunId], builds.EnumerateArray().Select(b => b.GetProperty("buildId").GetInt64()));
            Assert.Equal(1, builds[1].GetProperty("changes").GetProperty("keysAdded").GetInt64());
            var changes = await JsonAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/changes?build={second.DimensionRunId}");
            Assert.Equal(["Gamma Ray", "RHOB"], changes.GetProperty("items").EnumerateArray().Select(c => c.GetProperty("key").GetString()).Order(StringComparer.Ordinal));
            var added = await JsonAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/changes?change=added");
            Assert.Equal(("RHOB", "RHOB"), (Assert.Single(added.GetProperty("items").EnumerateArray()).GetProperty("key").GetString(), added.GetProperty("items")[0].GetProperty("toValue").GetString()));

            // The filter of a set of values, named as the dimension holds them; a name that is no value is said to be missing.
            var filter = await PostJsonAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/filter", new { values = new[] { "GR", "DT", "NOPE" } });
            Assert.Equal(WellLog, filter.GetProperty("kind").GetString());
            Assert.Equal(["DT", "GR"], filter.GetProperty("values").EnumerateArray().Select(m => m.GetProperty("value").GetString()));
            Assert.Equal(3, filter.GetProperty("keys").GetInt32());
            Assert.Equal(["NOPE"], filter.GetProperty("missing").EnumerateArray().Select(m => m.GetString()));
            var searched = Assert.Single(filter.GetProperty("searches").EnumerateArray()).GetString()!;
            Assert.Contains("\"gr\"", searched, StringComparison.Ordinal);
            Assert.Contains("\"DT\"", searched, StringComparison.Ordinal);

            // The search across dimensions: a record holding a key of GR, of a log of wellbore 15/9-F-1, in the kind both read.
            var search = await PostJsonAsync(client, token, "/api/v1/delivery/dimensions/search", new
            {
                picks = new object[]
                {
                    new { dimensionId, values = new[] { "GR" } },
                    new { dimensionId = wellboreId, values = new[] { "15/9-F-1", "15/9-F-9" } },
                },
            });
            var curveFilter = DimensionFilters.Of(field, ["GR", "gr"])[0];
            var wellboreFilter = DimensionFilters.Of(wellboreField, ["dev:master-data--Wellbore:1001:"])[0];
            Assert.Equal(WellLog, search.GetProperty("kind").GetString());
            Assert.Equal($"({curveFilter}) AND ({wellboreFilter})", search.GetProperty("query").GetString());
            Assert.Equal(3, search.GetProperty("clauses").GetInt32());
            Assert.Equal(["Wellbore: 15/9-F-9"], search.GetProperty("missing").EnumerateArray().Select(m => m.GetString()));
            var request = JsonDocument.Parse(search.GetProperty("request").GetString()!).RootElement;
            Assert.Equal((WellLog, search.GetProperty("query").GetString()), (request.GetProperty("kind").GetString(), request.GetProperty("query").GetString()));
            Assert.Equal(["CurveMnemonic", "Wellbore"], search.GetProperty("parts").EnumerateArray().Select(p => p.GetProperty("dimension").GetString()));
            var picked = Assert.Single(search.GetProperty("parts")[1].GetProperty("values").EnumerateArray());
            Assert.Equal(("15/9-F-1", 7L), (picked.GetProperty("value").GetString(), picked.GetProperty("records").GetInt64()));

            // A search reads one kind: one the dimensions' kind does not cover is refused, and so is a dimension picked twice.
            using (var outside = await PostAsync(client, token, "/api/v1/delivery/dimensions/search", new
            {
                kind = "osdu:wks:master-data--Wellbore:1.0.0",
                picks = new[] { new { dimensionId, values = new[] { "GR" } } },
            }))
            {
                Assert.Equal(HttpStatusCode.BadRequest, outside.StatusCode);
                Assert.Contains("other records", await outside.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            using (var twice = await PostAsync(client, token, "/api/v1/delivery/dimensions/search", new
            {
                picks = new[] { new { dimensionId, values = new[] { "GR" } }, new { dimensionId, values = new[] { "DT" } } },
            }))
            {
                Assert.Equal(HttpStatusCode.BadRequest, twice.StatusCode);
                Assert.Contains("picked in once", await twice.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            // The exports: the values as CSV with a header row, and every key as JSON Lines.
            using (var csv = await SendAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/export?set=values&format=csv"))
            {
                var body = await csv.Content.ReadAsStringAsync();
                Assert.True(csv.StatusCode == HttpStatusCode.OK, body);
                Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
                var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                Assert.StartsWith("value_id,value,records", lines[0], StringComparison.Ordinal);
                Assert.Equal(4, lines.Length);
                Assert.Contains("CurveMnemonic", csv.Content.Headers.ContentDisposition?.FileNameStar ?? csv.Content.Headers.ContentDisposition?.FileName ?? string.Empty, StringComparison.Ordinal);
            }

            using (var jsonl = await SendAsync(client, token, $"/api/v1/delivery/dimensions/{wellboreId}/export?set=keys&format=jsonl"))
            {
                var lines = (await jsonl.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                Assert.Equal(3, lines.Length);
                var row = lines.Select(l => JsonDocument.Parse(l).RootElement).Single(r => r.GetProperty("key").GetString() == "dev:master-data--Wellbore:1001:");
                Assert.Equal(("15/9-F-1", "15/9-F-1"), (row.GetProperty("label").GetString(), row.GetProperty("value").GetString()));
                Assert.Equal(DimensionFilters.Of(wellboreField, ["dev:master-data--Wellbore:1001:"])[0], row.GetProperty("filter").GetString());
            }

            // The builds a platform run made, named by the dimension each built.
            var ofRun = await JsonAsync(client, token, $"/api/v1/delivery/runs/{secondRunId}/dimension-builds");
            Assert.Equal("CurveMnemonic", Assert.Single(ofRun.EnumerateArray()).GetProperty("dimension").GetString());

            // What the API cannot answer, it says why.
            await ProblemAsync(client, token, "/api/v1/delivery/dimensions/2147483000", HttpStatusCode.NotFound, "No dimension");
            await ProblemAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/values?order=size", HttpStatusCode.BadRequest, "not one of value, records");
            await ProblemAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/values?after=not-a-cursor", HttpStatusCode.BadRequest, "is not a cursor");
            await ProblemAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/keys?value={grId}&leftOut=true", HttpStatusCode.BadRequest, "not both");
            await ProblemAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/changes?change=renamed", HttpStatusCode.BadRequest, "not one of added");
            await ProblemAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/export?set=members", HttpStatusCode.BadRequest, "not one of values, keys");
            await ProblemAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/export?format=pdf", HttpStatusCode.BadRequest, "not one of csv, jsonl");
            await ProblemAsync(client, token, $"/api/v1/delivery/flows/{otherPipelineId}/dimensions", HttpStatusCode.Conflict, "not a dimension flow");
            using (var none = await PostAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/filter", new { values = Array.Empty<string>() }))
            {
                Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
                Assert.Contains("at least one value", await none.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            using (var nothing = await PostAsync(client, token, "/api/v1/delivery/dimensions/search", new { picks = Array.Empty<object>() }))
            {
                Assert.Equal(HttpStatusCode.BadRequest, nothing.StatusCode);
                Assert.Contains("at least one value", await nothing.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            using (var unknown = await PostAsync(client, token, "/api/v1/delivery/dimensions/search", new { picks = new[] { new { dimensionId = 2147483000, values = new[] { "GR" } } } }))
            {
                Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            }
        }
        finally
        {
            await using (var osdu = SampleEstate.Context(cs))
            {
                var ids = await osdu.DeliveryDimensions.Where(d => d.FlowId == flow.LedgerId).Select(d => d.DimensionId).ToListAsync();
                await osdu.DeliveryDimensionChanges.Where(c => ids.Contains(c.DimensionId)).ExecuteDeleteAsync();
                await osdu.DeliveryDimensionValues.Where(v => ids.Contains(v.DimensionId)).ExecuteDeleteAsync();
                await osdu.DeliveryDimensionMembers.Where(m => ids.Contains(m.DimensionId)).ExecuteDeleteAsync();
                await osdu.DeliveryDimensionRuns.Where(r => r.FlowId == flow.LedgerId).ExecuteDeleteAsync();
                await osdu.DeliveryDimensions.Where(d => d.FlowId == flow.LedgerId).ExecuteDeleteAsync();
                await osdu.DeliveryLedgers.Where(l => l.FlowId == flow.LedgerId).ExecuteDeleteAsync();
                await osdu.DeliveryLedgerPartitions.Where(p => p.Name == partition).ExecuteDeleteAsync();
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
                await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
            }
        }
    }

    private static DimensionDeclaration Declaration(DimensionFlowDefinition flow, DimensionSpec spec) => new()
    {
        FlowId = flow.LedgerId,
        FlowName = flow.Name,
        Name = spec.Name,
        Description = spec.Description,
        Kind = spec.Kind,
        Path = spec.Path,
        CleanJson = spec.Clean.Count == 0 ? "[]" : """[{"kind":"trim"},{"kind":"upper"}]""",
        LabelJson = spec.Label.Count == 0 ? null : JsonSerializer.Serialize(spec.Label),
        DefinitionHash = spec.DefinitionHash,
    };

    /// <summary>
    /// Writes a completed build as the runner does: registered, then its keys (key, value or none, count), each with its own
    /// filter, and the values they add up to, each value's filter written by the filters the API writes too.
    /// </summary>
    private static async Task<DimensionRunState> BuildAsync(
        OsduLedger ledger, DimensionFlowDefinition flow, Guid runId, DateTime startedUtc, params (string Key, string? Value, long Count)[] found)
    {
        var (dimension, run) = await ledger.StartDimensionRunAsync(Declaration(flow, flow.Dimensions[0]), runId, "dimension api tests", startedUtc);
        var field = OsduField.Text("data.Curves.Mnemonic", "data.Curves");
        var keys = found
            .Select(f => new DimensionOriginalWrite(f.Key, f.Value, f.Value is null ? DimensionLeftOut.Empty : null, null, f.Count, Filterable: true,
                Filter: DimensionFilters.Of(field, [f.Key])[0]))
            .ToList();
        return await WriteAsync(ledger, flow, dimension, run, Field, field, keys, new DimensionReadCounts { Records = 20, WithValue = 19, Aggregations = 1, Slices = 1 }, startedUtc);
    }

    /// <summary>
    /// Writes a completed build of the Wellbore dimension: each key a wellbore's id, valued by the name read for it, or by the id
    /// itself when the label search found none.
    /// </summary>
    private static async Task<DimensionRunState> WellboresAsync(
        OsduLedger ledger, DimensionFlowDefinition flow, DateTime startedUtc, params (string Key, string? Label, long Count)[] found)
    {
        var (dimension, run) = await ledger.StartDimensionRunAsync(Declaration(flow, flow.Dimensions[1]), Guid.NewGuid(), "dimension api tests", startedUtc);
        var field = OsduField.Text("data.WellboreID");
        var keys = found
            .Select(f => new DimensionOriginalWrite(f.Key, f.Label ?? f.Key, null, null, f.Count, Filterable: true,
                Label: f.Label, LabelFrom: f.Label is null ? null : f.Key.TrimEnd(':'), Filter: DimensionFilters.Of(field, [f.Key])[0]))
            .ToList();
        var read = new DimensionReadCounts
        {
            Records = 11, WithValue = 11, Aggregations = 1, Slices = 1, Labelled = found.Count(f => f.Label is not null), Unlabelled = found.Count(f => f.Label is null), LabelQueries = 1,
        };
        return await WriteAsync(ledger, flow, dimension, run, WellboreField, field, keys, read, startedUtc);
    }

    private static async Task<DimensionRunState> WriteAsync(
        OsduLedger ledger, DimensionFlowDefinition flow, DimensionState dimension, DimensionRunState run, DimensionFieldState state, OsduField field,
        IReadOnlyList<DimensionOriginalWrite> keys, DimensionReadCounts read, DateTime startedUtc)
    {
        var values = keys.Where(o => o.CleanValue is not null)
            .GroupBy(o => o.CleanValue!, StringComparer.Ordinal)
            .Select(g => new DimensionMemberWrite(g.Key, g.Sum(o => o.Count), RecordsExact: false, g.Count(), 0, DimensionFilters.Of(field, g.Select(o => o.Original).ToList())[0], 1))
            .ToList();
        return await ledger.WriteDimensionAsync(new DimensionWrite
        {
            DimensionRunId = run.DimensionRunId,
            DimensionId = dimension.DimensionId,
            FlowId = flow.LedgerId,
            Field = state,
            Originals = keys,
            Members = values,
            Read = read,
            CompletedUtc = startedUtc.AddMinutes(1),
        });
    }

    private static CatalogPipeline Pipeline(Guid id, Guid repoId, string name, string kind, string yaml, DateTime now) => new()
    {
        Id = id,
        RepoId = repoId,
        Name = name,
        Kind = kind,
        RelativePath = "flows/" + name + ".yaml",
        ContentHash = new string('0', 64),
        Yaml = yaml,
        DefinitionJson = string.Create(CultureInfo.InvariantCulture, $$"""{"name":"{{name}}","flowKind":"{{kind}}"}"""),
        Active = true,
        Wave = 0,
        FirstSeenUtc = now,
        LastSeenUtc = now,
    };

    private static async Task ProblemAsync(HttpClient client, string token, string path, HttpStatusCode status, string says)
    {
        using var response = await SendAsync(client, token, path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"GET {path} answered {(int)response.StatusCode}, not {(int)status}: {body}");
        Assert.Contains(says, body, StringComparison.Ordinal);
    }

    private static async Task<JsonElement> JsonAsync(HttpClient client, string token, string path)
    {
        using var response = await SendAsync(client, token, path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {path} answered {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<JsonElement> PostJsonAsync(HttpClient client, string token, string path, object body)
    {
        using var response = await PostAsync(client, token, path, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"POST {path} answered {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string token, string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative)) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<string> TokenAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["read"]));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }
}
