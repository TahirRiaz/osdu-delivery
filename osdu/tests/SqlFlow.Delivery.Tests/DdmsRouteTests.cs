using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Storage;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The ddms route across the Wellbore DDMS's collections (osdu/specs/wellbore-ddms/INTEGRATION.md): each record goes to
/// the collection serving its entity type, a bulk collection's rules and columns are checked before anything is sent,
/// a metadata update carries the bulk link, a record-only collection takes no bulk data and purges through storage, and
/// a DDMS named by its registration is read from the Register service.
/// </summary>
public sealed class DdmsRouteTests
{
    private const string Root = "/api/os-wellbore-ddms";
    private const string LogId = "dev:work-product-component--WellLog:log-1";
    private const string TrajectoryId = "dev:work-product-component--WellboreTrajectory:traj-1";
    private const string WellboreId = "dev:master-data--Wellbore:wb-1";
    private const string BulkUri = "urn:wdms-1:uuid:38f0438e-71b8-4806-924b-9753796a77c1";

    private static (OsduHttpClient Client, HttpRuntime Runtime) Client(FakeHttpHandler handler)
    {
        var runtime = new HttpRuntime(
            new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
            new SecretResolver([new EnvSecretProvider()]), new TestClock(), handler, allowLoopback: true);
        var client = new OsduHttpClient(
            runtime, "http://localhost", new TargetAuth { Type = TargetAuthType.None },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
        return (client, runtime);
    }

    private static OsduWellLogProtocol Protocol(OsduHttpClient client, ProtocolOptions? options = null, DdmsRouting? routing = null)
        => new(client, options ?? new ProtocolOptions { DdmsRoot = Root }, NullLogger.Instance, routing: routing);

    private static string Envelope(string id, string entityType, string data) =>
        "{\"id\":\"" + id + "\",\"kind\":\"osdu:wks:" + entityType + ":1.0.0\","
        + "\"acl\":{\"viewers\":[\"data.default.viewers@dev.example.com\"],\"owners\":[\"data.default.owners@dev.example.com\"]},"
        + "\"legal\":{\"legaltags\":[\"dev-public\"],\"otherRelevantDataCountries\":[\"NO\"]},\"data\":" + data + "}";

    private static string Log(string curves = "[{\"CurveID\":\"MD\"},{\"CurveID\":\"GR\"}]")
        => Envelope(LogId, "work-product-component--WellLog", "{\"Name\":\"GR run\",\"Curves\":" + curves + "}");

    private static string Trajectory(string stations = "[{\"Name\":\"MD\"},{\"Name\":\"INC\"},{\"Name\":\"AZI\"}]")
        => Envelope(TrajectoryId, "work-product-component--WellboreTrajectory", "{\"Name\":\"survey\",\"AvailableTrajectoryStationProperties\":" + stations + "}");

    private static string Wellbore() => Envelope(WellboreId, "master-data--Wellbore", "{\"FacilityName\":\"15/9-F-1\"}");

    private static DeliveryWork Work(string id, string document, bool metadata, bool payload, IPayloadSource? source = null, long? existing = null) => new()
    {
        Key = SqlFlow.Delivery.Identity.DeliveryKey.Derive("ddms", [id]),
        TargetId = id,
        Document = TestSchema.Doc(document),
        DeliverMetadata = metadata,
        DeliverPayload = payload,
        Payload = source,
        ExistingVersion = existing,
    };

    private static string Written(string id, int version)
        => "{\"recordCount\":1,\"recordIds\":[\"" + id + "\"],\"recordIdVersions\":[\"" + id + ":" + version.ToString(CultureInfo.InvariantCulture) + "\"]}";

    [Fact]
    public async Task A_trajectory_goes_to_the_trajectory_collection_with_its_stations()
    {
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, "/ddms/v3/wellboretrajectories", HttpStatusCode.OK, Written(TrajectoryId, 4))
            .On(HttpMethod.Post, "/wellboretrajectories/" + TrajectoryId + "/data", HttpStatusCode.OK, "{}")
            .On(HttpMethod.Get, "/wellboretrajectories/" + TrajectoryId, HttpStatusCode.OK, "{\"id\":\"" + TrajectoryId + "\",\"version\":5}");
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var outcome = await Protocol(client).DeliverAsync(Work(TrajectoryId, Trajectory(), true, true, new ColumnChunks(1, "MD", "INC", "AZI")));

            Assert.True(outcome.Succeeded, outcome.Failure?.Message);
            Assert.Equal(5, outcome.TargetVersion);
            Assert.Equal(
                [
                    "POST " + Root + "/ddms/v3/wellboretrajectories",
                    "POST " + Root + "/ddms/v3/wellboretrajectories/" + TrajectoryId + "/data",
                    "GET " + Root + "/ddms/v3/wellboretrajectories/" + TrajectoryId,
                ],
                handler.Calls.Select(c => c.Method + " " + c.Uri.AbsolutePath));
        }

        OsduContracts.AssertConform(handler.Calls, null, OsduContracts.WellboreDdms);
    }

    [Fact]
    public async Task Bulk_data_whose_columns_the_record_does_not_describe_is_held_before_anything_is_sent()
    {
        var handler = new FakeHttpHandler();
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = Protocol(client);
            var stations = await Assert.ThrowsAsync<RecordHeldException>(
                () => protocol.DeliverAsync(Work(TrajectoryId, Trajectory(), true, true, new ColumnChunks(1, "MD", "TVD"))));
            Assert.Contains("the bulk column(s) TVD match no data.AvailableTrajectoryStationProperties[].Name", stations.Message, StringComparison.Ordinal);

            var curves = await Assert.ThrowsAsync<RecordHeldException>(
                () => protocol.DeliverAsync(Work(LogId, Log("[{\"CurveID\":\"MD\"}]"), true, true, new ColumnChunks(2, "MD", "GR"))));
            Assert.Contains("the bulk column(s) GR match no data.Curves[].CurveID", curves.Message, StringComparison.Ordinal);

            // An array curve is one curve with a column per element, whatever chunk carries them.
            var widths = await Assert.ThrowsAsync<RecordHeldException>(
                () => protocol.DeliverAsync(Work(LogId, Log("[{\"CurveID\":\"MD\"},{\"CurveID\":\"ARR\"}]"), false, true, new ColumnChunks(2, "MD", "ARR[0]", "ARR[1]"))));
            Assert.Contains("ARR has 2 column(s) in the bulk data and NumberOfColumns 1 (not given)", widths.Message, StringComparison.Ordinal);

            var unnamed = await Assert.ThrowsAsync<RecordHeldException>(
                () => protocol.DeliverAsync(Work(LogId, Log("[{\"CurveID\":\"MD\"},{\"Mnemonic\":\"GR\"}]"), true, true, new ColumnChunks(1, "MD"))));
            Assert.Contains("data.Curves[1] has no CurveID", unnamed.Message, StringComparison.Ordinal);
        }

        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task A_wellbore_goes_to_the_wellbores_collection_takes_no_bulk_data_and_purges_through_storage()
    {
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, "/ddms/v3/wellbores", HttpStatusCode.OK, Written(WellboreId, 2))
            .On(HttpMethod.Get, "/wellbores/" + WellboreId, HttpStatusCode.OK, "{\"id\":\"" + WellboreId + "\",\"version\":2}")
            .On(HttpMethod.Delete, "/wellbores/" + WellboreId, HttpStatusCode.NoContent, null)
            .On(HttpMethod.Delete, "/records/" + WellboreId + "/versions", HttpStatusCode.NoContent, null)
            .On(HttpMethod.Delete, "/records/" + WellboreId, HttpStatusCode.NoContent, null);
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = Protocol(client);
            var outcome = await protocol.DeliverAsync(Work(WellboreId, Wellbore(), true, false, existing: 1));
            Assert.True(outcome.Succeeded, outcome.Failure?.Message);
            Assert.Equal(2, outcome.TargetVersion);

            var held = await Assert.ThrowsAsync<RecordHeldException>(() => protocol.DeliverAsync(Work(WellboreId, Wellbore(), true, true, new ColumnChunks(1, "MD"))));
            Assert.Contains("goes to the wellbores collection of the DDMS 'wellbore' (" + Root + "), which holds records alone", held.Message, StringComparison.Ordinal);

            Assert.Equal(VerifyOutcome.Match, (await protocol.VerifyAsync(WellboreId, 2)).Outcome);
            Assert.True((await protocol.DeleteAsync(WellboreId, RemovalScope.Record)).Deleted);
            Assert.True((await protocol.DeleteAsync(WellboreId, RemovalScope.History)).Deleted);
            Assert.True((await protocol.DeleteAsync(WellboreId, RemovalScope.Everything)).Deleted);
        }

        // A record collection needs no read of the stored record before a write: it keeps no bulk link.
        Assert.Equal(
            [
                "POST " + Root + "/ddms/v3/wellbores",
                "GET " + Root + "/ddms/v3/wellbores/" + WellboreId,
                "DELETE " + Root + "/ddms/v3/wellbores/" + WellboreId,
                "DELETE /api/storage/v2/records/" + WellboreId + "/versions",
                "DELETE /api/storage/v2/records/" + WellboreId,
            ],
            handler.Calls.Select(c => c.Method + " " + c.Uri.AbsolutePath));
        Assert.All(handler.Calls, c => Assert.Equal(string.Empty, c.Uri.Query));
        Assert.Equal(
            [
                "core/storage DELETE /records/{id}",
                "core/storage DELETE /records/{id}/versions",
                "wellbore-ddms DELETE /ddms/v3/wellbores/{record_id}",
                "wellbore-ddms GET /ddms/v3/wellbores/{record_id}",
                "wellbore-ddms POST /ddms/v3/wellbores",
            ],
            OsduContracts.AssertConform(handler.Calls, null, OsduContracts.WellboreDdms, OsduContracts.Storage));
    }

    [Fact]
    public async Task A_record_collection_on_a_ddms_endpoint_refuses_a_purge_it_cannot_reach()
    {
        var handler = new FakeHttpHandler();
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var held = await Assert.ThrowsAsync<RecordHeldException>(() => Protocol(client, new ProtocolOptions()).DeleteAsync(WellboreId, RemovalScope.Everything));
            Assert.Contains("whose DELETE is logical only", held.Message, StringComparison.Ordinal);
            Assert.Contains("purgePath", held.Message, StringComparison.Ordinal);

            // Told where storage is, it purges there.
            handler.On(HttpMethod.Delete, "/records/" + WellboreId, HttpStatusCode.NoContent, null);
            var options = new ProtocolOptions { PurgePath = "http://localhost/api/storage/v2/records/{id}" };
            Assert.True((await Protocol(client, options).DeleteAsync(WellboreId, RemovalScope.Everything)).Deleted);
        }

        Assert.Equal("DELETE /api/storage/v2/records/" + WellboreId, Assert.Single(handler.Calls).Method + " " + handler.Calls[0].Uri.AbsolutePath);
    }

    [Fact]
    public async Task A_bulk_record_purges_through_its_own_collection()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Delete, "/wellboretrajectories/" + TrajectoryId, HttpStatusCode.NoContent, null);
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var outcome = await Protocol(client).DeleteAsync(TrajectoryId, RemovalScope.Everything);
            Assert.Equal("purged from OSDU (the record, every version and its bulk data)", outcome.Detail);
        }

        Assert.Equal("?purge=true", Assert.Single(handler.Calls).Uri.Query);
    }

    [Fact]
    public async Task An_update_carries_the_bulk_link_the_ddms_holds()
    {
        var stored = "{\"id\":\"" + LogId + "\",\"version\":3,\"data\":{\"ExtensionProperties\":{\"wdms\":{\"bulkURI\":\"" + BulkUri + "\"}},"
            + "\"DDMSDatasets\":[\"urn://wdms-1/uuid:38f0438e-71b8-4806-924b-9753796a77c1\"]}}";
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Get, "/welllogs/" + LogId, HttpStatusCode.OK, stored)
            .On(HttpMethod.Post, "/ddms/v3/welllogs", HttpStatusCode.OK, Written(LogId, 4));
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var outcome = await Protocol(client).DeliverAsync(Work(LogId, Log(), true, false, existing: 3));
            Assert.True(outcome.Succeeded, outcome.Failure?.Message);
            Assert.Equal(4, outcome.TargetVersion);
        }

        Assert.Equal(["GET", "POST"], handler.Calls.Select(c => c.Method.Method));
        var sent = System.Text.Json.Nodes.JsonNode.Parse(handler.Calls[1].Body!)!.AsArray().Single()!.AsObject();
        Assert.Equal(BulkUri, WellboreDdmsBulkLink.Of(sent));
        Assert.Equal(["urn://wdms-1/uuid:38f0438e-71b8-4806-924b-9753796a77c1"], sent["data"]!["DDMSDatasets"]!.AsArray().Select(n => (string?)n));
        Assert.Equal("GR run", (string?)sent["data"]!["Name"]);
        OsduContracts.AssertConform(handler.Calls, null, OsduContracts.WellboreDdms);
    }

    [Fact]
    public async Task A_write_refused_for_a_bulk_link_the_ledger_did_not_know_is_sent_again_with_it()
    {
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, "/ddms/v3/welllogs", hit => hit == 0
                ? FakeHttpHandler.Json(HttpStatusCode.BadRequest, """{"detail":"Record[0] error : Bulk URI isn't matching with the previous version one"}""")
                : FakeHttpHandler.Json(HttpStatusCode.OK, Written(LogId, 9)))
            .On(HttpMethod.Get, "/welllogs/" + LogId, HttpStatusCode.OK, "{\"id\":\"" + LogId + "\",\"version\":8,\"data\":{\"ExtensionProperties\":{\"wdms\":{\"bulkURI\":\"" + BulkUri + "\"}}}}");
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var outcome = await Protocol(client).DeliverAsync(Work(LogId, Log(), true, false));
            Assert.True(outcome.Succeeded, outcome.Failure?.Message);
            Assert.Equal(9, outcome.TargetVersion);
        }

        Assert.Equal(["POST", "GET", "POST"], handler.Calls.Select(c => c.Method.Method));
        Assert.DoesNotContain("bulkURI", handler.Calls[0].Body, StringComparison.Ordinal);
        Assert.Contains(BulkUri, handler.Calls[2].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_write_refused_for_anything_else_is_not_sent_again()
    {
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, "/ddms/v3/welllogs", HttpStatusCode.BadRequest, """{"detail":"All CurveID in WellLog[0] should be unique"}""")
            .On(HttpMethod.Get, "/welllogs/" + LogId, HttpStatusCode.NotFound, """{"detail":"WellLog not found"}""");
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var refused = await Assert.ThrowsAsync<OsduStatusException>(() => Protocol(client).DeliverAsync(Work(LogId, Log(), true, false)));
            Assert.Equal(400, refused.StatusCode);
        }

        Assert.Equal(["POST", "GET"], handler.Calls.Select(c => c.Method.Method));
    }

    [Fact]
    public async Task A_record_rendered_with_a_bulk_link_of_its_own_is_created_without_it()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/ddms/v3/welllogs", HttpStatusCode.OK, Written(LogId, 1));
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var document = Envelope(LogId, "work-product-component--WellLog", "{\"Name\":\"GR\",\"ExtensionProperties\":{\"wdms\":{\"bulkURI\":\"" + BulkUri + "\"}}}");
            Assert.True((await Protocol(client).DeliverAsync(Work(LogId, document, true, false))).Succeeded);
        }

        Assert.DoesNotContain("bulkURI", Assert.Single(handler.Calls).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_ddms_the_flow_declares_receives_the_types_it_serves()
    {
        var petro = new DdmsService("petro", "/petro", DdmsShape.WellboreDdmsV3, [new DdmsCollectionEntry("work-product-component--WellLog", "logs", Bulk: true)]);
        var flow = Samples.Targeting(
            new FlowTarget { Endpoint = "http://localhost", Protocol = DeliveryProtocol.OsduWellLog, ProtocolOptions = new ProtocolOptions { DdmsRoot = Root }, Ddms = [petro] },
            "logs");
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, "/petro/ddms/v3/logs", HttpStatusCode.OK, Written(LogId, 1))
            .On(HttpMethod.Post, Root + "/ddms/v3/wellbores", HttpStatusCode.OK, Written(WellboreId, 1))
            .On(HttpMethod.Get, "/about", HttpStatusCode.OK, "{}");
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = Protocol(client, flow.Target.ProtocolOptions, DdmsRouting.Of(flow));
            Assert.True((await protocol.DeliverAsync(Work(LogId, Log(), true, false))).Succeeded);
            Assert.True((await protocol.DeliverAsync(Work(WellboreId, Wellbore(), true, false))).Succeeded);

            var probe = await protocol.ProbeAsync();
            Assert.True(probe.Reachable);
            Assert.Equal("all 2 DDMSs answered", probe.Detail);
            Assert.Equal("/petro/about, " + Root + "/about", probe.Path);

            // A legal check goes to the platform the declared DDMSs sit under.
            Assert.Equal("/api/legal/v1/legaltags:validate", protocol.Routing.LegalValidatePath);
        }

        Assert.Equal(
            ["POST /petro/ddms/v3/logs", "POST " + Root + "/ddms/v3/wellbores", "GET /petro/about", "GET " + Root + "/about"],
            handler.Calls.Select(c => c.Method + " " + c.Uri.AbsolutePath));
    }

    [Fact]
    public async Task A_probe_names_the_ddms_that_did_not_answer()
    {
        var petro = new DdmsService("petro", "/petro", DdmsShape.WellboreDdmsV3, [new DdmsCollectionEntry("work-product-component--WellLog", "logs", Bulk: true)]);
        var flow = Samples.Targeting(
            new FlowTarget { Endpoint = "http://localhost", Protocol = DeliveryProtocol.OsduWellLog, ProtocolOptions = new ProtocolOptions { DdmsRoot = Root }, Ddms = [petro] },
            "logs");
        var handler = new FakeHttpHandler().On(HttpMethod.Get, "/petro/about", HttpStatusCode.OK, "{}");
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var probe = await Protocol(client, flow.Target.ProtocolOptions, DdmsRouting.Of(flow)).ProbeAsync();
            Assert.False(probe.Reachable);
            Assert.Equal(404, probe.Status);
            Assert.Equal(Root + "/about", probe.Path);
        }
    }

    /// <summary>
    /// A Wellbore DDMS registration as the Register service returns it (openapi register v1, Ddms), with an interface per
    /// entity type, each an OpenAPI document naming one server and one retrieval operation.
    /// </summary>
    private static string Registration(string servers, params (string EntityType, string Retrieval, bool Bulk)[] interfaces)
        => Registration(interfaces.Select(i => Interface(i.EntityType, i.Retrieval, i.Bulk, servers)).ToArray());

    private static string Registration(params string[] interfaces)
        => "{\"id\":\"wellbore\",\"name\":\"Wellbore DDMS\",\"interfaces\":[" + string.Join(",", interfaces) + "]}";

    /// <summary>One registered interface: its OpenAPI document names <paramref name="servers"/> and one retrieval operation.</summary>
    private static string Interface(string entityType, string retrieval, bool bulk, string servers)
        => "{\"entityType\":\"" + entityType + "\",\"schema\":{\"openapi\":\"3.0.0\",\"info\":{\"title\":\"wdms\",\"version\":\"1\"},"
            + "\"servers\":[" + servers + "],\"paths\":{\"" + retrieval + "\":{\"get\":{\"x-ddms-retrieve-entity\":true}}"
            + (bulk ? ",\"" + retrieval.Replace("{id}", "{record_id}", StringComparison.Ordinal) + "/data\":{\"post\":{}}" : string.Empty)
            + "}}}";

    private static FlowDefinition Registered(string? root = null, IReadOnlyList<DdmsCollectionEntry>? collections = null, string? registerPath = null, params DdmsService[] others)
        => Samples.Targeting(
            new FlowTarget
            {
                Endpoint = "http://localhost",
                Protocol = DeliveryProtocol.OsduWellLog,
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" },
                ProtocolOptions = new ProtocolOptions { RegisterPath = registerPath },
                Ddms = [.. others, new DdmsService("wellbore", root, DdmsShape.WellboreDdmsV3, collections ?? []) { Registration = "wellbore" }],
            },
            "logs");

    /// <summary>The protocol the node builds for <paramref name="flow"/>, over the fake OSDU <paramref name="runtime"/> talks to.</summary>
    private static async Task<OsduWellLogProtocol> BuildAsync(FlowDefinition flow, HttpRuntime runtime)
        => (OsduWellLogProtocol)await ProtocolFactory.CreateAsync(flow, runtime, new SecretResolver([new EnvSecretProvider()]), NullLoggerFactory.Instance);

    [Fact]
    public async Task A_ddms_named_by_its_registration_is_read_from_the_register_service_when_the_protocol_is_built()
    {
        var registration = Registration(
            "{\"url\":\"/api/os-wellbore-ddms/\"}",
            ("welllog", "/ddms/v3/welllogs/{id}", true),
            ("wellbore", "/ddms/v3/wellbores/{id}", false),
            ("Seismic2D", "/ddms/v3/seismic2d/{id}", true));
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Get, "/api/register/v1/ddms/wellbore", HttpStatusCode.OK, registration)
            .On(HttpMethod.Post, Root + "/ddms/v3/welllogs", HttpStatusCode.OK, Written(LogId, 1));
        var (_, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = await BuildAsync(Registered(), runtime);
            var service = Assert.Single(protocol.Routing.Services);
            Assert.True(service.Discovered);
            Assert.Equal(Root, service.Root);

            // Collections the shape knows by their path take the shape's entity type; another keeps the registered name.
            Assert.Equal(
                ["work-product-component--WellLog:welllogs:True", "master-data--Wellbore:wellbores:False", "Seismic2D:seismic2d:True"],
                service.Collections.Select(c => $"{c.EntityType}:{c.Segment}:{c.Bulk}"));
            Assert.Equal(Root + "/ddms/v3/seismic2d", protocol.Routing.For("work-product-component--Seismic2D").Records);

            Assert.True((await protocol.DeliverAsync(Work(LogId, Log(), true, false))).Succeeded);
        }

        Assert.Equal(["GET /api/register/v1/ddms/wellbore", "POST " + Root + "/ddms/v3/welllogs"], handler.Calls.Select(c => c.Method + " " + c.Uri.AbsolutePath));
        Assert.Equal(
            ["core/register GET /ddms/{id}", "wellbore-ddms POST /ddms/v3/welllogs"],
            OsduContracts.AssertConform(handler.Calls, null, OsduContracts.Register, OsduContracts.WellboreDdms));
    }

    [Fact]
    public async Task What_a_flow_declares_of_a_registered_ddms_wins_over_its_registration()
    {
        var registration = Registration("{\"url\":\"https://{host}/wdms\",\"variables\":{\"host\":{\"default\":\"ddms.example.com\"}}}", ("welllog", "/ddms/v3/welllogs/{id}", true));
        var handler = new FakeHttpHandler().On(HttpMethod.Get, "/registry/wellbore", HttpStatusCode.OK, registration);
        var (_, runtime) = Client(handler);
        using (runtime)
        {
            // The registered server is used as it stands, on its own host.
            var discovered = await BuildAsync(Registered(registerPath: "http://localhost/registry/{id}"), runtime);
            Assert.Equal("https://ddms.example.com/wdms", discovered.Routing.Services.Single().Root);
            Assert.Equal("https://ddms.example.com/wdms/ddms/v3/welllogs/{id}", discovered.Routing.For("work-product-component--WellLog").Record);

            // A declared root replaces the registered server, and declared collections the registered interfaces.
            var declared = await BuildAsync(Registered(root: "/mine", registerPath: "http://localhost/registry/{id}"), runtime);
            Assert.Equal("/mine/ddms/v3/welllogs", declared.Routing.For("work-product-component--WellLog").Records);

            var collections = await BuildAsync(
                Registered(collections: [new DdmsCollectionEntry("work-product-component--WellLog", "logs", Bulk: true)], registerPath: "http://localhost/registry/{id}"),
                runtime);
            Assert.Equal("https://ddms.example.com/wdms/ddms/v3/logs", collections.Routing.For("work-product-component--WellLog").Records);
        }
    }

    public static TheoryData<string, string> BrokenRegistrations => new()
    {
        { Registration("{\"url\":\"/a\"},{\"url\":\"/b\"}", ("welllog", "/ddms/v3/welllogs/{id}", true)), "does not name exactly one server" },
        { Registration("{\"url\":\"ftp://ddms.example.com\"}", ("welllog", "/ddms/v3/welllogs/{id}", true)), "which is not an http(s) URL" },
        { Registration("{\"url\":\"https://{host}/wdms\"}", ("welllog", "/ddms/v3/welllogs/{id}", true)), "whose variable host has no default" },
        { Registration("{\"url\":\"/wdms\"}", ("welllog", "/api/v2/logs/{id}", true)), "which is not a collection of a WellboreDdmsV3 DDMS" },
        { Registration("{\"url\":\"/a\"}", ("welllog", "/ddms/v3/welllogs/{id}", true)).Replace("\"x-ddms-retrieve-entity\":true", "\"summary\":\"read\"", StringComparison.Ordinal), "has 0 GET operations marked x-ddms-retrieve-entity" },
        { Registration(Interface("welllog", "/ddms/v3/welllogs/{id}", true, "{\"url\":\"/a\"}"), Interface("wellbore", "/ddms/v3/wellbores/{id}", false, "{\"url\":\"/b/\"}")), "names 2 servers (/a, /b)" },
        { """{"id":"wellbore","name":"Wellbore DDMS","interfaces":[]}""", "lists no interface" },
        { """{"id":"wellbore","name":"Wellbore DDMS","interfaces":[{"schema":{}}]}""", "has an interface without an entityType" },
        { """{"id":"wellbore","name":"Wellbore DDMS","interfaces":[{"entityType":"welllog"}]}""", "carries no OpenAPI document" },
    };

    [Theory]
    [MemberData(nameof(BrokenRegistrations))]
    public async Task A_registration_that_does_not_say_where_the_ddms_is_or_what_it_serves_is_refused(string registration, string expected)
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Get, "/api/register/v1/ddms/wellbore", HttpStatusCode.OK, registration);
        var (_, runtime) = Client(handler);
        using (runtime)
        {
            var refused = await Assert.ThrowsAsync<DeliveryException>(() => BuildAsync(Registered(), runtime));
            Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
            Assert.StartsWith("targeting (interface 'logs'): the registration 'wellbore' of target.ddms.wellbore", refused.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_missing_registration_or_one_that_overlaps_a_declared_ddms_is_refused()
    {
        var handler = new FakeHttpHandler();
        var (_, runtime) = Client(handler);
        using (runtime)
        {
            var missing = await Assert.ThrowsAsync<DeliveryException>(() => BuildAsync(Registered(), runtime));
            Assert.Contains("the Register service (/api/register/v1/ddms/wellbore) holds no DDMS registered as 'wellbore' in partition dev", missing.Message, StringComparison.Ordinal);
        }

        var overlapping = new FakeHttpHandler().On(HttpMethod.Get, "/api/register/v1/ddms/wellbore", HttpStatusCode.OK, Registration("{\"url\":\"/wdms\"}", ("welllog", "/ddms/v3/welllogs/{id}", true)));
        var (_, other) = Client(overlapping);
        using (other)
        {
            var petro = new DdmsService("petro", "/petro", DdmsShape.WellboreDdmsV3, [new DdmsCollectionEntry("work-product-component--WellLog", "logs", Bulk: true)]);
            var refused = await Assert.ThrowsAsync<DeliveryException>(() => BuildAsync(Registered(others: petro), other));
            Assert.Contains("serves work-product-component--WellLog, which target.ddms.petro serves as well", refused.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_route_check_of_a_run_reads_the_registration_first()
    {
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Get, "/api/register/v1/ddms/wellbore", HttpStatusCode.OK, Registration("{\"url\":\"/wdms\"}", ("welllog", "/ddms/v3/welllogs/{id}", true)));
        using var factory = new FakeOsduProtocols(handler);
        var context = Samples.Engine(null, protocols: factory);
        using var runtime = FlowRuntime.ForTarget(context, Registered());

        await runtime.CheckRouteAsync("osdu:wks:work-product-component--WellLog:1.4.0");
        var unserved = await Assert.ThrowsAsync<DeliveryException>(() => runtime.CheckRouteAsync("osdu:wks:master-data--Wellbore:1.3.0"));
        Assert.Contains("no DDMS this flow reaches serves master-data--Wellbore: it reaches the DDMS 'wellbore' (/wdms), serving work-product-component--WellLog", unserved.Message, StringComparison.Ordinal);

        // The registration was read once, for the protocol both checks used.
        Assert.Single(handler.Calls);

        // Without a registration, the check needs no protocol at all.
        using var plain = FlowRuntime.ForTarget(context, Samples.Targeting(new FlowTarget { Endpoint = "http://localhost", Protocol = DeliveryProtocol.OsduWellLog }));
        await plain.CheckRouteAsync("osdu:wks:master-data--Wellbore:1.3.0");
        Assert.Single(handler.Calls);
    }

    /// <summary>Parquet chunks carrying the named columns, their rows continuing from chunk to chunk.</summary>
    internal sealed class ColumnChunks(int chunks, params string[] columns) : IPayloadSource
    {
        public Task<IReadOnlyList<PayloadFile>> ListChunksAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PayloadFile>>(Enumerable.Range(0, chunks).Select(i => new PayloadFile(i, $"mem://chunk_{i}.parquet", Bytes(i).Length)).ToList());

        public Task<Stream> OpenAsync(PayloadFile chunk, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream(Bytes(chunk.Index), writable: false));

        private byte[] Bytes(int index)
        {
            var culture = CultureInfo.InvariantCulture;
            var row = columns.ToDictionary(c => c, c => (object?)(double)index, StringComparer.Ordinal);
            var metadata = new Dictionary<string, string>
            {
                [ParquetFiles.PandasMetadataKey] = $$"""{"index_columns": [{"kind": "range", "name": null, "start": {{index.ToString(culture)}}, "stop": {{(index + 1).ToString(culture)}}, "step": 1}]}""",
            };
            using var buffer = new MemoryStream();
            ParquetFiles.WriteAsync(buffer, columns.Select(c => (c, typeof(double))).ToList(), [row], metadata).GetAwaiter().GetResult();
            return buffer.ToArray();
        }
    }
}
