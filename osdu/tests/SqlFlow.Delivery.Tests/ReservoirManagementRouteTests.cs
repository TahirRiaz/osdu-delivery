using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Protocols.Ddms;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The ddms route to the Reservoir Management DDMS (osdu/specs/reservoir-management-ddms/INTEGRATION.md) against a fake of
/// the service built from its brief: the header record written through Storage, the service's copy of it taken in by its
/// list call, the rows of the tables below it posted one per call with the keys fed down, resumed without duplicates,
/// replaced on redelivery, and removed without ever calling the service's record write or its purging delete.
/// </summary>
public sealed class ReservoirManagementRouteTests
{
    private const string PhiKId = "dev:work-product-component--PersistedCollection:phik-1";
    private const string PhiKKind = "osdu:wks:work-product-component--PersistedCollection:1.2.0";
    private const string ForecastId = "dev:work-product-component--ProductionValues:fc-1";
    private const string ForecastKind = "osdu:wks:work-product-component--ProductionValues:1.0.0";
    private const string Reservoir = "dev:master-data--Reservoir:r1:";
    private const string Root = FakeOsduPlatform.ReservoirManagementRoot + "/ddms/";
    private const string Service = "the DDMS 'rm' (" + FakeOsduPlatform.ReservoirManagementRoot + ")";

    private sealed class Rig : IDisposable
    {
        public Rig(
            FakeOsduPlatform platform, ReservoirManagementSettings? settings = null, IReadOnlyList<DdmsCollectionEntry>? collections = null,
            ProtocolOptions? options = null, bool partition = true)
        {
            Runtime = new HttpRuntime(
                new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
                new SecretResolver([new EnvSecretProvider()]), new TestClock(), platform, allowLoopback: true);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (partition)
            {
                headers["data-partition-id"] = "dev";
            }

            var client = new OsduHttpClient(Runtime, FakeOsduPlatform.Endpoint, new TargetAuth { Type = TargetAuthType.None }, headers);
            options ??= new ProtocolOptions { WorkflowPollSeconds = 1, DatasetIndexWaitSeconds = 0 };
            var rm = new DdmsService("rm", FakeOsduPlatform.ReservoirManagementRoot, DdmsShape.ReservoirManagement, collections ?? DdmsCatalog.ReservoirManagementCollections)
            {
                ReservoirManagement = settings ?? new ReservoirManagementSettings { SettleSeconds = 30, PollSeconds = 5 },
            };
            Flow = Samples.Targeting(new FlowTarget { Endpoint = FakeOsduPlatform.Endpoint, Protocol = DeliveryProtocol.Ddms, Ddms = [rm], ProtocolOptions = options }, "reservoir");
            Clock = new ProductionTimeSeriesRouteTests.SteppingClock();
            Protocol = new OsduDdmsProtocol(client, options, NullLogger.Instance, time: Clock, routing: DdmsRouting.Of(Flow));
        }

        public HttpRuntime Runtime { get; }

        public FlowDefinition Flow { get; }

        public ProductionTimeSeriesRouteTests.SteppingClock Clock { get; }

        public OsduDdmsProtocol Protocol { get; }

        public void Dispose() => Runtime.Dispose();
    }

    private static JsonObject PhiK(string name = "Phi-K 1", string? parent = Reservoir, string id = PhiKId, string kind = PhiKKind)
    {
        var data = new JsonObject { ["name"] = name };
        if (parent is not null)
        {
            data["ParentObjectID"] = parent;
        }

        return FakeOsduPlatform.Record(id, kind, data);
    }

    private static JsonObject Forecast() => FakeOsduPlatform.Record(ForecastId, ForecastKind, new JsonObject { ["ParentObjectID"] = Reservoir, ["fore_name"] = "Base case" });

    /// <summary>Two rock types, the first with two Phi-K rows and the second with one.</summary>
    private const string TwoRockTypes = """
        {
          "phi-k-synthesis-rt": [
            { "rt_tab_name": "RT1", "rt_phi_k_tab": 1, "phi-k-synthesis-phi-k": [ { "phie": 0.21, "kgas": 12.5 }, { "phie": 0.18, "kgas": 8.1 } ] },
            { "rt_tab_name": "RT2", "rt_phi_k_tab": 2, "rt_name": "tight", "phi-k-synthesis-phi-k": [ { "phie": 0.25, "comment": null } ] }
          ]
        }
        """;

    private static RafsRouteTests.NamedFiles Rows(string json, string name = "rows.json") => new((name, Encoding.UTF8.GetBytes(json)));

    private static DeliveryWork Work(
        JsonObject document, IPayloadSource? rows = null, bool metadata = true, long? existing = null,
        IReadOnlyDictionary<string, string>? state = null,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? completed = null,
        Dictionary<string, IReadOnlyDictionary<string, string>>? reported = null) => new()
        {
            Key = DeliveryKey.Derive("reservoir", [document["id"]!.GetValue<string>()]),
            TargetId = document["id"]!.GetValue<string>(),
            Document = document,
            DeliverMetadata = metadata,
            DeliverPayload = rows is not null,
            Payload = rows,
            ExistingVersion = existing,
            TargetState = state ?? new Dictionary<string, string>(StringComparer.Ordinal),
            CompletedSteps = completed ?? new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal),
            StepCompleted = reported is null
                ? null
                : (step, values, _) =>
                {
                    reported[step] = values;
                    return Task.CompletedTask;
                },
        };

    private static List<string> Sent(FakeOsduPlatform platform, int from = 0)
        => platform.Calls.Skip(from).Select(c => c.Method + " " + Uri.UnescapeDataString(c.Uri.PathAndQuery)).ToList();

    private static string ReadCopy(string segment, string id) => $"GET {Root}{segment}/{id}?data_partition_id=dev&catalog_entity_id={id}";

    private static string List(string segment) => $"GET {Root}{segment}/?data_partition_id=dev&parent_type=Reservoir";

    private static string Delete(string table, long key) => $"DELETE {Root}{table}/{key}?catalog_entity_id={key}";

    /// <summary>The service's record write and purging delete, which a delivery never calls.</summary>
    private static void NeverWritesOrPurgesThroughTheService(FakeOsduPlatform platform)
        => Assert.DoesNotContain(platform.Calls, c => c.Uri.AbsolutePath.StartsWith(Root, StringComparison.Ordinal)
            && ReservoirManagementTables.Header(c.Uri.AbsolutePath[Root.Length..].Split('/')[0]) is not null
            && (c.Method == HttpMethod.Put || c.Method == HttpMethod.Delete));

    private static double Number(JsonObject row, string column) => double.Parse(row[column]!.ToJsonString(), CultureInfo.InvariantCulture);

    [Fact]
    public async Task A_record_goes_through_storage_is_taken_in_by_the_list_call_and_its_rows_go_one_per_call_with_the_keys_fed_down()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var reported = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);

        var outcome = await rig.Protocol.DeliverAsync(Work(PhiK(), Rows(TwoRockTypes), reported: reported));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);

        Assert.Equal(
            [
                "PUT /api/storage/v2/records",
                ReadCopy("phi-k-synthesis", PhiKId),
                List("phi-k-synthesis"),
                ReadCopy("phi-k-synthesis", PhiKId),
                $"POST {Root}phi-k-synthesis-rt",
                $"POST {Root}phi-k-synthesis-phi-k",
                $"POST {Root}phi-k-synthesis-phi-k",
                $"POST {Root}phi-k-synthesis-rt",
                $"POST {Root}phi-k-synthesis-phi-k",
            ],
            Sent(platform));

        // The record is in Storage under its own id; the service holds its copy, and the rows under it with their keys.
        Assert.Equal("Phi-K 1", platform.Records[PhiKId]["data"]!["name"]!.GetValue<string>());
        Assert.Equal(Reservoir, platform.RmHeaders["phi-k-synthesis"][PhiKId]["parent_object_id"]!.GetValue<string>());
        var types = platform.Rows("phi-k-synthesis-rt");
        var samples = platform.Rows("phi-k-synthesis-phi-k");
        Assert.Equal([101L, 104L], types.Keys);
        Assert.Equal([102L, 103L, 105L], samples.Keys);
        Assert.Equal((PhiKId, Reservoir, "RT1", 1L), (types[101]["id_phi_k_synthesis"]!.GetValue<string>(), types[101]["parent_object_id"]!.GetValue<string>(), types[101]["rt_tab_name"]!.GetValue<string>(), (long)Number(types[101], "rt_phi_k_tab")));
        Assert.Equal("tight", types[104]["rt_name"]!.GetValue<string>());
        Assert.Equal([101.0, 101.0, 104.0], samples.Values.Select(r => Number(r, "id_phi_k_synthesis_rt")));
        Assert.Equal([0.21, 0.18, 0.25], samples.Values.Select(r => Number(r, "phie")));
        Assert.All(samples.Values, r => Assert.Equal((PhiKId, Reservoir), (r["id_phi_k_synthesis"]!.GetValue<string>(), r["parent_object_id"]!.GetValue<string>())));

        Assert.Equal(["metadata", "sync", "rows-begin", "rows-0", "rows-done"], outcome.Steps.Select(s => s.Name));
        Assert.Equal("101,102,103,104,105", reported["rows-0"]["keys"]);
        Assert.Equal("1", reported["sync"]["lists"]);
        Assert.Equal("5", reported["rows-begin"]["rows"]);
        Assert.Equal(
            "phi-k-synthesis-rt=101;phi-k-synthesis-phi-k=102-103;phi-k-synthesis-rt=104;phi-k-synthesis-phi-k=105",
            outcome.Returned[ReservoirManagementShape.RowsKey]);
        Assert.Equal(Reservoir, outcome.Returned[ReservoirManagementShape.ParentKey]);
        Assert.Equal(platform.Records[PhiKId]["version"]!.GetValue<long>(), outcome.TargetVersion);
        Assert.Equal((true, true, 5), (outcome.MetadataDelivered, outcome.PayloadDelivered, outcome.ChunksSent));
        Assert.Equal("5 row(s), 5 posted by this try", outcome.Detail);
        NeverWritesOrPurgesThroughTheService(platform);

        Assert.Equal(
            [
                "core/storage PUT /records",
                "reservoir-management-ddms GET /ddms/phi-k-synthesis/",
                "reservoir-management-ddms GET /ddms/phi-k-synthesis/{phi_k_synthesis_id}",
                "reservoir-management-ddms POST /ddms/phi-k-synthesis-phi-k",
                "reservoir-management-ddms POST /ddms/phi-k-synthesis-rt",
            ],
            OsduContracts.AssertConform(platform.Calls, null, OsduContracts.Storage, OsduContracts.ReservoirManagementDdms));
    }

    [Fact]
    public async Task A_forecast_names_its_forecast_base_and_the_rows_below_a_forecast_step_name_it_too()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var rows = """
            {
              "forecast-fluid": [ { "id_fluid": 1, "qf_fore": 10.5, "fluid_method": "decline" } ],
              "forecast-det": [ { "dt": "2026-01-31", "optime": 30, "forecast-det-fluid": [ { "id_fluid": 1, "fluid": 120.0 }, { "id_fluid": 2, "fluid": 3.5 } ] } ]
            }
            """;
        var outcome = await rig.Protocol.DeliverAsync(Work(Forecast(), Rows(rows)));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);

        var fluid = Assert.Single(platform.Rows("forecast-fluid").Values);
        var step = Assert.Single(platform.Rows("forecast-det").Values);
        var stepFluids = platform.Rows("forecast-det-fluid").Values.ToList();
        Assert.Equal((0.0, ForecastId, Reservoir), (Number(fluid, "id_forecast_base"), fluid["id_forecast"]!.GetValue<string>(), fluid["parent_object_id"]!.GetValue<string>()));
        Assert.Equal("2026-01-31", step["dt"]!.GetValue<string>());
        Assert.Equal(2, stepFluids.Count);
        Assert.All(stepFluids, r => Assert.Equal((Number(step, "id_forecast_det"), 0.0, ForecastId), (Number(r, "id_forecast_det"), Number(r, "id_forecast_base"), r["id_forecast"]!.GetValue<string>())));
        Assert.Equal("0", outcome.Steps.Single(s => s.Name == "sync").Returned["id_forecast_base"]);
        Assert.Equal("forecast-fluid=101;forecast-det=102;forecast-det-fluid=103-104", outcome.Returned[ReservoirManagementShape.RowsKey]);
        OsduContracts.AssertConform(platform.Calls, null, OsduContracts.Storage, OsduContracts.ReservoirManagementDdms);
    }

    [Fact]
    public async Task A_fluid_synthesis_posts_the_rows_of_both_its_tables_and_an_aquifer_needs_its_name()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        const string FluidId = "dev:work-product-component--FluidSystemCharacterization:fs-1";
        var fluid = FakeOsduPlatform.Record(FluidId, "osdu:wks:work-product-component--FluidSystemCharacterization:1.0.0", new JsonObject { ["ParentObjectID"] = Reservoir });
        var rows = """
            {
              "fluid-synthesis-tank-pvt": [ { "depth": 2500, "pressure": 310.2, "bo": 1.21 }, { "depth": 2600, "pressure": 320.9 } ],
              "fluid-synthesis-tank-blackoil": [ { "fluid_unit": "METRIC", "flu_tab_type": "PVTO", "pvto_pb": 250 } ]
            }
            """;
        var outcome = await rig.Protocol.DeliverAsync(Work(fluid, Rows(rows)));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal(2, platform.Rows("fluid-synthesis-tank-pvt").Count);
        Assert.Equal("PVTO", Assert.Single(platform.Rows("fluid-synthesis-tank-blackoil").Values)["flu_tab_type"]!.GetValue<string>());
        Assert.Equal("fluid-synthesis-tank-pvt=101-102;fluid-synthesis-tank-blackoil=103", outcome.Returned[ReservoirManagementShape.RowsKey]);

        const string TankId = "dev:work-product-component--AcquiferInterpretation:tank-1";
        var tank = FakeOsduPlatform.Record(TankId, "osdu:wks:work-product-component--AcquiferInterpretation:1.1.0", new JsonObject { ["ParentObjectID"] = Reservoir });
        var calls = platform.Calls.Count;
        var held = await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(tank, Rows("""{ "aquifer-datum": [ { "aqui_poro": 0.2 } ] }"""))));
        Assert.Equal("rows.json: aquifer-datum[0] gives no name, which every aquifer-datum row needs", held.Message);
        Assert.Equal(calls, platform.Calls.Count);

        var aquifer = await rig.Protocol.DeliverAsync(Work(tank, Rows("""{ "aquifer-datum": [ { "name": "North flank", "aqui_poro": 0.2 } ] }""")));
        Assert.True(aquifer.Succeeded, aquifer.Failure?.Message);
        Assert.Equal((TankId, "North flank"), (Assert.Single(platform.Rows("aquifer-datum").Values)["id_tank_datum"]!.GetValue<string>(), platform.Rows("aquifer-datum").Values.Single()["name"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_try_whose_post_lost_its_answer_finds_the_rows_it_posted_and_posts_only_the_rest()
    {
        var platform = new FakeOsduPlatform();
        platform.RmLostPosts.Add(3);
        using var rig = new Rig(platform);
        var reported = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);

        var failed = await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(Work(PhiK(), Rows(TwoRockTypes), reported: reported)));
        Assert.Equal(502, failed.StatusCode);
        Assert.Equal(["metadata", "sync", "rows-begin"], reported.Keys);
        Assert.Equal(3, platform.Rows("phi-k-synthesis-rt").Count + platform.Rows("phi-k-synthesis-phi-k").Count);

        var calls = platform.Calls.Count;
        var again = new Dictionary<string, IReadOnlyDictionary<string, string>>(reported, StringComparer.Ordinal);
        var outcome = await rig.Protocol.DeliverAsync(Work(PhiK(), Rows(TwoRockTypes), completed: reported, reported: again));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal(
            [
                $"GET {Root}phi-k-synthesis-rt/header-entity/{PhiKId}?header_entity_id={PhiKId}",
                $"GET {Root}phi-k-synthesis-phi-k/header-entity/101?header_entity_id=101",
                $"POST {Root}phi-k-synthesis-rt",
                $"POST {Root}phi-k-synthesis-phi-k",
            ],
            Sent(platform, calls));

        // No row twice: the three rows of the first try are taken as posted.
        Assert.Equal([101L, 104L], platform.Rows("phi-k-synthesis-rt").Keys);
        Assert.Equal([102L, 103L, 105L], platform.Rows("phi-k-synthesis-phi-k").Keys);
        Assert.Equal("101,102,103,104,105", again["rows-0"]["keys"]);
        Assert.Equal(2, outcome.ChunksSent);
        Assert.Equal("5 row(s), 2 posted by this try, 3 by an earlier one (3 found among the service's rows)", outcome.Detail);
        Assert.Equal(["metadata", "sync", "rows-begin", "rows-0", "rows-done"], outcome.Steps.Select(s => s.Name));
        Assert.Equal([true, true, true, false, false], outcome.Steps.Select(s => s.Resumed));
        OsduContracts.AssertConform(platform.Calls, null, OsduContracts.Storage, OsduContracts.ReservoirManagementDdms);
    }

    [Fact]
    public async Task A_try_resumed_past_a_recorded_block_searches_only_the_block_after_it()
    {
        var platform = new FakeOsduPlatform();
        platform.RmLostPosts.Add(76);
        using var rig = new Rig(platform);
        var samples = string.Join(", ", Enumerable.Range(0, 120).Select(i => string.Create(CultureInfo.InvariantCulture, $"{{ \"phie\": {i / 1000.0} }}")));
        var rows = $$"""{ "phi-k-synthesis-rt": [ { "rt_tab_name": "RT1", "rt_phi_k_tab": 1, "phi-k-synthesis-phi-k": [ {{samples}} ] } ] }""";
        var reported = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);

        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(Work(PhiK(), Rows(rows), reported: reported)));
        Assert.Equal(["metadata", "sync", "rows-begin", "rows-0"], reported.Keys);
        Assert.Equal(76, platform.Rows("phi-k-synthesis-rt").Count + platform.Rows("phi-k-synthesis-phi-k").Count);

        var calls = platform.Calls.Count;
        var again = new Dictionary<string, IReadOnlyDictionary<string, string>>(reported, StringComparer.Ordinal);
        var outcome = await rig.Protocol.DeliverAsync(Work(PhiK(), Rows(rows), completed: reported, reported: again));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);

        // One read of the rows under the rock type, then the 45 rows no try posted.
        Assert.Single(Sent(platform, calls), c => c.StartsWith("GET ", StringComparison.Ordinal));
        Assert.Equal(45, Sent(platform, calls).Count(c => c.StartsWith("POST ", StringComparison.Ordinal)));
        Assert.Equal(120, platform.Rows("phi-k-synthesis-phi-k").Count);
        Assert.Single(platform.Rows("phi-k-synthesis-rt"));
        Assert.Equal(Enumerable.Range(0, 120).Select(i => i / 1000.0), platform.Rows("phi-k-synthesis-phi-k").Values.Select(r => Number(r, "phie")));
        Assert.Equal(["rows-0", "rows-1", "rows-2"], again.Keys.Where(k => k.StartsWith("rows-", StringComparison.Ordinal) && char.IsAsciiDigit(k[^1])).Order());
        Assert.Equal("121 row(s), 45 posted by this try, 76 by an earlier one (26 found among the service's rows)", outcome.Detail);
        Assert.Equal("phi-k-synthesis-rt=101;phi-k-synthesis-phi-k=102-221", outcome.Returned[ReservoirManagementShape.RowsKey]);
    }

    [Fact]
    public async Task A_redelivery_of_the_rows_posts_the_new_ones_then_deletes_the_old_ones_below_first()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var first = await rig.Protocol.DeliverAsync(Work(PhiK(), Rows(TwoRockTypes)));
        Assert.True(first.Succeeded, first.Failure?.Message);

        var calls = platform.Calls.Count;
        var second = await rig.Protocol.DeliverAsync(Work(PhiK(), Rows("""{ "phi-k-synthesis-rt": [ { "rt_tab_name": "RT9", "rt_phi_k_tab": 9 } ] }"""), metadata: false, existing: first.TargetVersion, state: first.Returned));
        Assert.True(second.Succeeded, second.Failure?.Message);
        Assert.Equal(
            [
                $"GET /api/storage/v2/records/{PhiKId}",
                ReadCopy("phi-k-synthesis", PhiKId),
                $"POST {Root}phi-k-synthesis-rt",
                Delete("phi-k-synthesis-phi-k", 105),
                Delete("phi-k-synthesis-rt", 104),
                Delete("phi-k-synthesis-phi-k", 103),
                Delete("phi-k-synthesis-phi-k", 102),
                Delete("phi-k-synthesis-rt", 101),
            ],
            Sent(platform, calls));
        Assert.Equal([106L], platform.Rows("phi-k-synthesis-rt").Keys);
        Assert.Empty(platform.Rows("phi-k-synthesis-phi-k"));
        Assert.Equal("phi-k-synthesis-rt=106", second.Returned[ReservoirManagementShape.RowsKey]);
        Assert.Equal("1 row(s), 1 posted by this try; 5 row(s) of an earlier delivery deleted", second.Detail);
        Assert.Equal(first.TargetVersion, second.TargetVersion);
        Assert.Equal("0", second.Steps.Single(s => s.Name == "sync").Returned["lists"]);

        // The record alone changes: its rows stay.
        calls = platform.Calls.Count;
        var third = await rig.Protocol.DeliverAsync(Work(PhiK("renamed"), existing: second.TargetVersion, state: second.Returned));
        Assert.Equal(["PUT /api/storage/v2/records"], Sent(platform, calls));
        Assert.DoesNotContain(ReservoirManagementShape.RowsKey, third.Returned.Keys);
        Assert.Equal([106L], platform.Rows("phi-k-synthesis-rt").Keys);

        // A rows file without rows deletes what is left, and takes nothing in.
        calls = platform.Calls.Count;
        var fourth = await rig.Protocol.DeliverAsync(Work(PhiK("renamed"), Rows("{}"), metadata: false, existing: third.TargetVersion, state: second.Returned));
        Assert.True(fourth.Succeeded, fourth.Failure?.Message);
        Assert.Equal([Delete("phi-k-synthesis-rt", 106)], Sent(platform, calls));
        Assert.Equal(string.Empty, fourth.Returned[ReservoirManagementShape.RowsKey]);
        Assert.Equal("0 row(s), 0 posted by this try; 1 row(s) of an earlier delivery deleted", fourth.Detail);
        Assert.Empty(platform.Rows("phi-k-synthesis-rt"));
        NeverWritesOrPurgesThroughTheService(platform);
        OsduContracts.AssertConform(platform.Calls, null, OsduContracts.Storage, OsduContracts.ReservoirManagementDdms);
    }

    [Fact]
    public async Task The_record_is_taken_in_once_search_serves_it_and_is_left_for_a_later_try_while_search_does_not()
    {
        // Search serves the record from the third list call on; the delivery asks until then.
        var platform = new FakeOsduPlatform();
        platform.RmSearchServes = _ => platform.RmLists >= 3;
        using (var rig = new Rig(platform))
        {
            var outcome = await rig.Protocol.DeliverAsync(Work(PhiK(), Rows(TwoRockTypes)));
            Assert.True(outcome.Succeeded, outcome.Failure?.Message);
            Assert.Equal("3", outcome.Steps.Single(s => s.Name == "sync").Returned["lists"]);
            Assert.Equal(TimeSpan.FromSeconds(10), rig.Clock.Waited);
        }

        // Search never serves it: the record is written, and the rows wait for the next try.
        var unserved = new FakeOsduPlatform { RmSearchServes = _ => false };
        using (var rig = new Rig(unserved))
        {
            var reported = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
            var waiting = await Assert.ThrowsAsync<DeliveryException>(() => rig.Protocol.DeliverAsync(Work(PhiK(), Rows(TwoRockTypes), reported: reported)));
            Assert.Equal(
                $"Search does not serve {PhiKId} yet, and {Service} takes into its database only the records Search serves; the next try asks again.",
                waiting.Message);
            Assert.Equal(["metadata"], reported.Keys);
            Assert.Equal(TimeSpan.FromSeconds(30), rig.Clock.Waited);
            Assert.Equal(7, unserved.RmLists);
            Assert.Equal("POST /api/search/v2/query", Sent(unserved)[^1]);
            Assert.Empty(unserved.Rows("phi-k-synthesis-rt"));
        }

        // settleSeconds 0 asks once.
        var once = new FakeOsduPlatform { RmSearchServes = _ => false };
        using (var rig = new Rig(once, new ReservoirManagementSettings { SettleSeconds = 0 }))
        {
            await Assert.ThrowsAsync<DeliveryException>(() => rig.Protocol.DeliverAsync(Work(PhiK(), Rows(TwoRockTypes))));
            Assert.Equal(1, once.RmLists);
            Assert.Equal(TimeSpan.Zero, rig.Clock.Waited);
        }

        OsduContracts.AssertConform(unserved.Calls, null, OsduContracts.Storage, OsduContracts.Search, OsduContracts.ReservoirManagementDdms);
    }

    [Fact]
    public async Task A_record_search_serves_that_the_service_does_not_take_in_holds_the_record()
    {
        // No pool row for the record's parent: the list call fails.
        var platform = new FakeOsduPlatform();
        platform.RmPools.Clear();
        platform.Search = (kind, query) => kind == PhiKKind && query == $"id:\"{PhiKId}\"" ? [PhiKId] : [];
        using (var rig = new Rig(platform))
        {
            var held = await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(PhiK(), Rows(TwoRockTypes))));
            Assert.StartsWith(
                $"Search serves {PhiKId}, and {Service} did not take it into its database (its list call answered: 500 Internal Server Error). "
                + $"Its list call takes only the first 100 {PhiKKind} records Search returns, and fails for a record without a pool row for its parent",
                held.Message);
            Assert.EndsWith("release the record once the service holds its copy", held.Message, StringComparison.Ordinal);
        }

        // Beyond the first 100 records of the kind the service takes.
        var beyond = new FakeOsduPlatform { RmSearchLimit = 0 };
        beyond.Search = (_, _) => [PhiKId];
        using (var rig = new Rig(beyond))
        {
            var held = await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(PhiK(), Rows(TwoRockTypes))));
            Assert.StartsWith($"Search serves {PhiKId}, and {Service} did not take it into its database. Its list call", held.Message, StringComparison.Ordinal);
        }

        // A forecast needs the forecast base 0 the list call gives it.
        var forecasts = new FakeOsduPlatform { RmForecastBaseZero = false };
        forecasts.Search = (_, _) => [ForecastId];
        using (var rig = new Rig(forecasts))
        {
            await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(Forecast(), Rows("""{ "forecast-det": [ { "dt": "2026-01-31" } ] }"""))));
            Assert.Empty(forecasts.Rows("forecast-det"));
        }
    }

    [Fact]
    public async Task A_kr_synthesis_is_never_taken_in_by_the_service_and_its_rows_go_under_a_copy_an_operator_inserted()
    {
        const string KrId = "dev:work-product-component--PersistedCollection:kr-1";
        var kr = new[] { new DdmsCollectionEntry("work-product-component--PersistedCollection", "kr-synthesis", Bulk: true) };
        var rows = """
            { "kr-synthesis-rt": [ { "rt_tab_name": "SAT1", "swi": 0.2, "rt_kr_table": true, "kr-synthesis-kr": [ { "sat_tab_type": "SWOF", "swof_sw": 0.2, "swof_krw": 0 } ] } ] }
            """;
        var platform = new FakeOsduPlatform();
        platform.Search = (_, _) => [KrId];
        using (var rig = new Rig(platform, collections: kr))
        {
            var held = await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(PhiK(id: KrId), Rows(rows))));
            Assert.Contains("and it cannot take in any Kr synthesis, whose row an operator inserts too;", held.Message, StringComparison.Ordinal);

            platform.RmHeaders["kr-synthesis"] = new Dictionary<string, JsonObject>(StringComparer.Ordinal)
            {
                [KrId] = new JsonObject { ["id"] = KrId, ["parent_object_id"] = Reservoir, ["name"] = "Kr 1", ["dt"] = "2026-01-01", ["description"] = "inserted by an operator" },
            };
            var outcome = await rig.Protocol.DeliverAsync(Work(PhiK(id: KrId), Rows(rows)));
            Assert.True(outcome.Succeeded, outcome.Failure?.Message);
            Assert.Equal("0", outcome.Steps.Single(s => s.Name == "sync").Returned["lists"]);
            var rt = Assert.Single(platform.Rows("kr-synthesis-rt").Values);
            var table = Assert.Single(platform.Rows("kr-synthesis-kr").Values);
            Assert.Equal((Number(rt, "id_kr_synthesis_rt"), KrId, "SWOF"), (Number(table, "id_kr_synthesis_rt"), table["id_kr_synthesis"]!.GetValue<string>(), table["sat_tab_type"]!.GetValue<string>()));
            Assert.True(rt["rt_kr_table"]!.GetValue<bool>());
        }
    }

    public static TheoryData<string, string> RefusedRows => new()
    {
        { """{ "kr-synthesis-rt": [] }""", "rows.json: 'kr-synthesis-rt' is not a table below phi-k-synthesis; the tables there are phi-k-synthesis-rt" },
        { """{ "phi-k-synthesis-rt": {} }""", "rows.json: phi-k-synthesis-rt is not an array of rows" },
        { """{ "phi-k-synthesis-rt": [ 1 ] }""", "rows.json: phi-k-synthesis-rt[0] is not an object of columns" },
        { """{ "phi-k-synthesis-rt": [ { "rt_tab_name": "RT1", "rt_phi_k_tab": 1, "foo": 1 } ] }""", "rows.json: phi-k-synthesis-rt[0] gives foo, which is not a column of phi-k-synthesis-rt nor a table below it (phi-k-synthesis-phi-k); its columns are rt_tab_name, rt_phi_k_tab, rt_name, rt_geol_desc, phi_k_model" },
        { """{ "phi-k-synthesis-rt": [ { "rt_tab_name": "RT1", "rt_phi_k_tab": 1, "id_phi_k_synthesis": "x" } ] }""", "rows.json: phi-k-synthesis-rt[0] gives id_phi_k_synthesis, which the route fills from the rows above it; leave it out" },
        { """{ "phi-k-synthesis-rt": [ { "rt_tab_name": "RT1", "rt_phi_k_tab": 1, "id_phi_k_synthesis_rt": 7 } ] }""", "rows.json: phi-k-synthesis-rt[0] gives id_phi_k_synthesis_rt, which the route fills" },
        { """{ "phi-k-synthesis-rt": [ { "rt_tab_name": "RT1", "rt_phi_k_tab": 1, "parent_object_id": "x" } ] }""", "rows.json: phi-k-synthesis-rt[0] gives parent_object_id, which the route fills" },
        { """{ "phi-k-synthesis-rt": [ { "rt_tab_name": "RT1" } ] }""", "rows.json: phi-k-synthesis-rt[0] gives no rt_phi_k_tab, which every phi-k-synthesis-rt row needs" },
        { """{ "phi-k-synthesis-rt": [ { "rt_tab_name": null, "rt_phi_k_tab": 1 } ] }""", "rows.json: phi-k-synthesis-rt[0] gives no rt_tab_name" },
        { """{ "phi-k-synthesis-rt": [ { "rt_tab_name": "RT1", "rt_phi_k_tab": 1.5 } ] }""", "rows.json: phi-k-synthesis-rt[0] gives rt_phi_k_tab as something other than a whole number" },
        { """{ "phi-k-synthesis-rt": [ { "rt_tab_name": 5, "rt_phi_k_tab": 1 } ] }""", "rows.json: phi-k-synthesis-rt[0] gives rt_tab_name as something other than a string" },
        { """{ "phi-k-synthesis-rt": [ { "rt_tab_name": "RT1", "rt_phi_k_tab": 1, "rt_name": ["a"] } ] }""", "rows.json: phi-k-synthesis-rt[0] gives rt_name as an object or an array, and the column takes one value" },
        { """{ "phi-k-synthesis-rt": [ { "rt_tab_name": "RT1", "rt_phi_k_tab": 1, "phi-k-synthesis-phi-k": [ { "phie": "0.2" } ] } ] }""", "rows.json: phi-k-synthesis-rt[0].phi-k-synthesis-phi-k[0] gives phie as something other than a number" },
        { """{ "phi-k-synthesis-rt": [ { "rt_tab_name": "RT1", "rt_phi_k_tab": 1, "phi-k-synthesis-phi-k": [ { "id_r_ori": true } ] } ] }""", "rows.json: phi-k-synthesis-rt[0].phi-k-synthesis-phi-k[0] gives id_r_ori as something other than a string or a number" },
        { """[]""", "the rows file rows.json is not a JSON object of the tables below phi-k-synthesis" },
        { """{ "phi-k-synthesis-rt": [ """, "the rows file rows.json is not JSON: " },
    };

    [Theory]
    [MemberData(nameof(RefusedRows))]
    public async Task A_rows_file_the_service_would_refuse_holds_the_record_before_anything_is_sent(string rows, string expected)
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var held = await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(PhiK(), Rows(rows))));
        Assert.StartsWith(expected, held.Message, StringComparison.Ordinal);
        Assert.Empty(platform.Calls);
    }

    [Fact]
    public async Task A_record_the_service_could_not_take_in_holds_its_rows_and_a_record_without_rows_goes_to_storage_alone()
    {
        var cases = new (JsonObject Document, IPayloadSource Rows, bool Partition, string Expected)[]
        {
            (PhiK(kind: "osdu:wks:work-product-component--PersistedCollection:1.3.0"), Rows(TwoRockTypes), true,
                $"{Service} takes only {PhiKKind} records into its database, where a phi-k-synthesis record's rows go, and the record's kind is 'osdu:wks:work-product-component--PersistedCollection:1.3.0'"),
            (PhiK(parent: null), Rows(TwoRockTypes), true, $"the record names no data.ParentObjectID, which {Service} takes its copy's parent from"),
            (PhiK(id: "dev:work-product-component--PersistedCollection:phik 1"), Rows(TwoRockTypes), true,
                $"the id 'dev:work-product-component--PersistedCollection:phik 1' is not one {Service} reads a phi-k-synthesis record by"),
            (PhiK(), Rows(TwoRockTypes), false, $"{Service} takes the partition as data_partition_id, and the flow sends no data-partition-id to take it from"),
            (PhiK(), Rows(TwoRockTypes, "rows.csv"), true, "the rows file rows.csv is not a .json file; the Reservoir Management DDMS takes rows as JSON"),
            (PhiK(), new RafsRouteTests.NamedFiles(), true, "no rows file was found for the record (a .json file of the tables below phi-k-synthesis: phi-k-synthesis-rt)"),
            (FakeOsduPlatform.Record("dev:master-data--FluidSystem:pvt-1", "osdu:wks:master-data--FluidSystem:1.0.0"), Rows("{}"), true,
                $"{Service} keeps no rows for pvt-properties records, and the record comes with a rows file"),
        };

        foreach (var (document, rows, partition, expected) in cases)
        {
            var platform = new FakeOsduPlatform();
            using var rig = new Rig(platform, partition: partition);
            var held = await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(document, rows)));
            Assert.True(held.Message.StartsWith(expected, StringComparison.Ordinal), $"expected '{expected}', got: {held.Message}");
            Assert.Empty(platform.Calls);
        }

        // Without rows, nothing but Storage is asked, whatever the kind and whether the partition is named.
        var alone = new FakeOsduPlatform();
        using var noPartition = new Rig(alone, partition: false);
        var record = await noPartition.Protocol.DeliverAsync(Work(PhiK(kind: "osdu:wks:work-product-component--PersistedCollection:1.3.0", parent: null)));
        Assert.True(record.Succeeded, record.Failure?.Message);
        Assert.Equal(["PUT /api/storage/v2/records"], Sent(alone));
        Assert.Equal(["metadata"], record.Steps.Select(s => s.Name));
        Assert.False(record.PayloadDelivered);
        Assert.True(noPartition.Protocol.CarryLink(PhiKId, null, PhiK()));
    }

    [Fact]
    public async Task A_row_the_service_refuses_holds_the_record_and_rows_whose_parent_went_hold_it_too()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        await rig.Protocol.DeliverAsync(Work(PhiK(), Rows(TwoRockTypes)));

        // The service's copy was removed by hand: the first row finds no parent.
        platform.RmHeaders["phi-k-synthesis"][PhiKId]["parent_object_id"] = "dev:master-data--Reservoir:other:";
        var refused = await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(PhiK(), Rows(TwoRockTypes), completed: new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["sync"] = new Dictionary<string, string> { ["parent_object_id"] = Reservoir },
        })));
        Assert.StartsWith($"{Service} refused rows.json: phi-k-synthesis-rt[0] (", refused.Message, StringComparison.Ordinal);
        Assert.Contains("One or several attributes mandatory are NULL", refused.Message, StringComparison.Ordinal);

        platform.RmHeaders["phi-k-synthesis"].Clear();
        var gone = await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(PhiK(), Rows(TwoRockTypes), completed: new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["sync"] = new Dictionary<string, string> { ["parent_object_id"] = Reservoir },
        })));
        Assert.StartsWith($"{Service} no longer holds the row rows.json: phi-k-synthesis-rt[0] belongs to (id_phi_k_synthesis has not been found)", gone.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_removal_takes_the_record_through_storage_and_everything_deletes_the_rows_first()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var delivered = await rig.Protocol.DeliverAsync(Work(PhiK(), Rows(TwoRockTypes)));

        var calls = platform.Calls.Count;
        var removed = await rig.Protocol.DeleteAsync(PhiKId, RemovalScope.Record, delivered.Returned);
        Assert.Equal("removed from OSDU (reversible); the service's rows and its copy of the record stay, since it has no reversible delete", removed.Detail);
        Assert.Equal([$"POST /api/storage/v2/records/{PhiKId}:delete"], Sent(platform, calls));
        Assert.Equal(5, platform.Rows("phi-k-synthesis-rt").Count + platform.Rows("phi-k-synthesis-phi-k").Count);

        calls = platform.Calls.Count;
        var history = await rig.Protocol.DeleteAsync(PhiKId, RemovalScope.History, delivered.Returned);
        Assert.Equal([$"DELETE /api/storage/v2/records/{PhiKId}/versions"], Sent(platform, calls));
        Assert.Equal("earlier versions purged; the latest version is still live", history.Detail);

        calls = platform.Calls.Count;
        var everything = await rig.Protocol.DeleteAsync(PhiKId, RemovalScope.Everything, delivered.Returned);
        Assert.Equal(
            [
                Delete("phi-k-synthesis-phi-k", 105),
                Delete("phi-k-synthesis-rt", 104),
                Delete("phi-k-synthesis-phi-k", 103),
                Delete("phi-k-synthesis-phi-k", 102),
                Delete("phi-k-synthesis-rt", 101),
                $"DELETE /api/storage/v2/records/{PhiKId}",
            ],
            Sent(platform, calls));
        Assert.Equal((true, false), (everything.Deleted, everything.AlreadyGone));
        Assert.Equal(
            "5 row(s) deleted from the Reservoir Management DDMS; the record purged from OSDU; the service's copy of the record stays in its database, since only its own purge removes it",
            everything.Detail);
        Assert.Empty(platform.Rows("phi-k-synthesis-rt"));
        Assert.Empty(platform.Rows("phi-k-synthesis-phi-k"));
        Assert.Contains(PhiKId, platform.Purged);

        // Again: nothing is left to delete.
        var again = await rig.Protocol.DeleteAsync(PhiKId, RemovalScope.Everything, delivered.Returned);
        Assert.Equal((false, true), (again.Deleted, again.AlreadyGone));
        Assert.StartsWith("0 row(s) deleted from the Reservoir Management DDMS; the record was already gone from OSDU", again.Detail, StringComparison.Ordinal);
        NeverWritesOrPurgesThroughTheService(platform);

        var endpoints = RemovalEndpoints.Of(rig.Flow, PhiKKind);
        Assert.Equal(("/api/storage/v2/records/{id}:delete", "POST"), (endpoints.Record, endpoints.RecordMethod));
        Assert.Equal("/api/storage/v2/records/{id}/versions", endpoints.History);
        Assert.Equal("/api/rm-ddms/ddms/{table}/{key} (each row the record's deliveries posted), then /api/storage/v2/records/{id}", endpoints.Everything);
        Assert.Equal("/api/storage/v2/records/{id}", RemovalEndpoints.Of(rig.Flow, "osdu:wks:master-data--FluidSystem:1.0.0").Everything);
        OsduContracts.AssertConform(platform.Calls, null, OsduContracts.Storage, OsduContracts.ReservoirManagementDdms);
    }

    [Fact]
    public async Task The_probe_asks_the_health_check_and_a_read_behind_the_token_and_verify_reads_storage()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var probe = await rig.Protocol.ProbeAsync();
        Assert.True(probe.Reachable, probe.Detail);
        Assert.Equal(
            [$"GET {FakeOsduPlatform.ReservoirManagementRoot}/", $"GET {Root}estimated-volumes-det/header-entity/probe?header_entity_id=probe"],
            Sent(platform));
        Assert.Equal("the DDMS answered all 2 probes", probe.Detail);

        var delivered = await rig.Protocol.DeliverAsync(Work(PhiK()));
        Assert.Equal(VerifyOutcome.Match, (await rig.Protocol.VerifyAsync(PhiKId, delivered.TargetVersion)).Outcome);
        Assert.Equal(PhiKId, (await rig.Protocol.ReadAsync(PhiKId))!["id"]!.GetValue<string>());
        OsduContracts.AssertConform(platform.Calls, null, OsduContracts.Storage, OsduContracts.ReservoirManagementDdms);
    }

    [Fact]
    public async Task A_record_written_again_keeps_the_data_keys_osdu_owns()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform, options: new ProtocolOptions { PreserveDataKeys = ["Owned"] });
        var first = await rig.Protocol.DeliverAsync(Work(PhiK()));
        platform.Records[PhiKId]["data"]!["Owned"] = "kept";
        var calls = platform.Calls.Count;
        await rig.Protocol.DeliverAsync(Work(PhiK("renamed"), existing: first.TargetVersion));
        Assert.Equal([$"GET /api/storage/v2/records/{PhiKId}", "PUT /api/storage/v2/records"], Sent(platform, calls));
        Assert.Equal(("renamed", "kept"), (platform.Records[PhiKId]["data"]!["name"]!.GetValue<string>(), platform.Records[PhiKId]["data"]!["Owned"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData("dev:master-data--Reservoir:r1:", "Reservoir")]
    [InlineData("dev:master-data--ReservoirSegment:s1:", "Segment")]
    [InlineData("dev:master-data--Sector:s1:", "Sector")]
    [InlineData("dev:master-data--PersistedCollection:c1:", "Reservoir")]
    [InlineData(null, "Reservoir")]
    public void The_list_call_is_asked_for_the_parent_type_the_parent_names(string? parent, string expected)
        => Assert.Equal(expected, ReservoirManagementShape.ParentTypeOf(parent));

    [Fact]
    public void The_rows_a_delivery_posted_are_kept_as_runs_of_keys_in_the_order_they_were_posted()
    {
        (string, long)[] rows = [("phi-k-synthesis-rt", 7), ("phi-k-synthesis-phi-k", 8), ("phi-k-synthesis-phi-k", 9), ("phi-k-synthesis-phi-k", 12), ("phi-k-synthesis-rt", 13), ("phi-k-synthesis-phi-k", 14)];
        var text = ReservoirManagementRows.Encode(rows);
        Assert.Equal("phi-k-synthesis-rt=7;phi-k-synthesis-phi-k=8-9,12;phi-k-synthesis-rt=13;phi-k-synthesis-phi-k=14", text);
        Assert.Equal(rows, ReservoirManagementRows.Parse(text));
        Assert.Equal(string.Empty, ReservoirManagementRows.Encode([]));
        Assert.Empty(ReservoirManagementRows.Parse(null));
        Assert.Empty(ReservoirManagementRows.Parse(string.Empty));
        Assert.Empty(ReservoirManagementRows.Parse("unknown-table=1"));
        Assert.Empty(ReservoirManagementRows.Parse("phi-k-synthesis-rt=9-7"));
        Assert.Empty(ReservoirManagementRows.Parse("phi-k-synthesis-rt=x"));
        Assert.Empty(ReservoirManagementRows.Parse("phi-k-synthesis-rt"));
        Assert.Empty(ReservoirManagementRows.Parse("phi-k-synthesis-rt=1-1000000"));
    }

    [Fact]
    public void Every_table_is_below_a_header_the_catalog_names_and_the_route_fills_the_keys_the_service_links_rows_by()
    {
        foreach (var header in ReservoirManagementTables.Headers)
        {
            Assert.All(header.Tables, t => Assert.Equal(header.Segment, ReservoirManagementTables.Table(t)!.Header));
            Assert.Equal(header.Tables.Count > 0, DdmsCatalog.ReservoirManagementCollections.FirstOrDefault(c => c.Segment == header.Segment)?.Bulk ?? header.Tables.Count > 0);
        }

        var phik = ReservoirManagementTables.Table("phi-k-synthesis-phi-k")!;
        Assert.Equal(("id_phi_k_synthesis_phi_k", "id_phi_k_synthesis", "id_phi_k_synthesis_rt"), (phik.KeyColumn, phik.HeaderColumn, phik.ParentColumn));
        Assert.Equal(["id_phi_k_synthesis_phi_k", "id_phi_k_synthesis", "id_phi_k_synthesis_rt", "parent_object_id"], phik.RouteColumns);
        Assert.Equal(
            ["id_forecast_det_fluid", "id_forecast", "id_forecast_det", "parent_object_id", "id_forecast_base"],
            ReservoirManagementTables.Table("forecast-det-fluid")!.RouteColumns);
        Assert.Equal(["id_estimated_volumes_det", "id_estimated_volumes", "parent_object_id"], ReservoirManagementTables.Table("estimated-volumes-det")!.RouteColumns);
        Assert.Equal(["forecast-fluid", "forecast-det"], ReservoirManagementTables.Below("forecast").Select(t => t.Segment));
        Assert.Equal(["forecast-det-fluid"], ReservoirManagementTables.Below("forecast-det").Select(t => t.Segment));
        Assert.Empty(ReservoirManagementTables.Below("pvt-properties"));
        Assert.Empty(ReservoirManagementTables.Below("forecast-det-fluid"));

        // Every table is one the contract serves, with its create and its read under a parent.
        foreach (var table in ReservoirManagementTables.Tables)
        {
            Assert.Contains(OsduContracts.ReservoirManagementDdms.Operations, o => o.Method == "POST" && o.Template == "/ddms/" + table.Segment);
            Assert.Contains(OsduContracts.ReservoirManagementDdms.Operations, o => o.Method == "GET" && o.Template.StartsWith($"/ddms/{table.Segment}/header-entity/", StringComparison.Ordinal));
            Assert.Contains(OsduContracts.ReservoirManagementDdms.Operations, o => o.Method == "DELETE" && o.Template.StartsWith($"/ddms/{table.Segment}/{{", StringComparison.Ordinal));
        }

        foreach (var header in ReservoirManagementTables.Headers)
        {
            Assert.Contains(OsduContracts.ReservoirManagementDdms.Operations, o => o.Method == "GET" && o.Template == $"/ddms/{header.Segment}/");
            Assert.Contains(OsduContracts.ReservoirManagementDdms.Operations, o => o.Method == "GET" && o.Template.StartsWith($"/ddms/{header.Segment}/{{", StringComparison.Ordinal));
        }
    }
}
