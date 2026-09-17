using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Protocols.Ddms;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Storage;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The ddms route to the Rock and Fluid Sample DDMS (osdu/specs/rafs-ddms/INTEGRATION.md) against a fake of the service
/// built from its brief: records in arrays typed exactly as it requires, each content table under its type and schema
/// version once the service's catalogue says it has a model for it, the URNs it answers read in both storage modes, the
/// content URNs carried on every rewrite, and a removal that takes the content datasets RAFS leaves behind.
/// </summary>
public sealed class RafsRouteTests
{
    private const string AnalysisId = "opendes:work-product-component--SamplesAnalysis:sa-1";
    private const string AnalysisKind = "osdu:wks:work-product-component--SamplesAnalysis:1.0.0";
    private const string ShiftId = "opendes:work-product-component--DepthShift:ds-1";
    private const string ShiftKind = "osdu:wks:work-product-component--DepthShift:1.0.0";
    private const string V2 = FakeOsduPlatform.RafsRoot + "/v2/";
    private const string Storage = "/api/storage/v2/records/";

    private static readonly DdmsService Rafs = new("rafs", FakeOsduPlatform.RafsRoot, DdmsShape.RafsV2, DdmsCatalog.RafsCollections);

    private sealed class Rig : IDisposable
    {
        public Rig(FakeOsduPlatform platform, ProtocolOptions? options = null)
        {
            Runtime = new HttpRuntime(
                new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
                new SecretResolver([new EnvSecretProvider()]), new TestClock(), platform, allowLoopback: true);
            Client = new OsduHttpClient(
                Runtime, FakeOsduPlatform.Endpoint, new TargetAuth { Type = TargetAuthType.None },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "opendes" });
            Options = options ?? new ProtocolOptions { WorkflowPollSeconds = 1, DatasetIndexWaitSeconds = 0 };
            Flow = Samples.Targeting(new FlowTarget { Endpoint = FakeOsduPlatform.Endpoint, Protocol = DeliveryProtocol.OsduWellLog, Ddms = [Rafs], ProtocolOptions = Options }, "samples");
            Protocol = new OsduWellLogProtocol(Client, Options, NullLogger.Instance, routing: DdmsRouting.Of(Flow));
        }

        public HttpRuntime Runtime { get; }

        public OsduHttpClient Client { get; }

        public ProtocolOptions Options { get; }

        public FlowDefinition Flow { get; }

        public OsduWellLogProtocol Protocol { get; }

        public void Dispose() => Runtime.Dispose();
    }

    /// <summary>A record's content files held in memory, each opened fresh for every request.</summary>
    internal sealed class NamedFiles(params (string Name, byte[] Bytes)[] files) : IPayloadSource
    {
        public Task<IReadOnlyList<PayloadFile>> ListChunksAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PayloadFile>>(files.Select((f, i) => new PayloadFile(i, "mem://content/" + f.Name, f.Bytes.Length)).ToList());

        public Task<Stream> OpenAsync(PayloadFile file, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(file);
            return Task.FromResult<Stream>(new MemoryStream(files[file.Index].Bytes, writable: false));
        }
    }

    /// <summary>A parquet table of <paramref name="rows"/> rows naming the record in its id column.</summary>
    internal static byte[] Table(string idColumn, string recordId, int rows)
    {
        using var buffer = new MemoryStream();
        var data = Enumerable.Range(0, rows)
            .Select(i => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { [idColumn] = recordId + ":", ["Value"] = (double)i })
            .ToList();
        ParquetFiles.WriteAsync(buffer, [(idColumn, typeof(string)), ("Value", typeof(double))], data).GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    /// <summary>A SamplesAnalysis table in the split JSON form the contract describes.</summary>
    private static byte[] SplitJson(string recordId)
        => Encoding.UTF8.GetBytes(
            "{\"columns\":[\"SamplesAnalysisID\",\"SampleID\",\"Meta\"],\"index\":[0],\"data\":[[\"" + recordId + ":\",\"opendes:master-data--Sample:s-1:\",[]]]}");

    private static JsonObject Analysis(string name = "NMR run") => FakeOsduPlatform.Record(AnalysisId, AnalysisKind, new JsonObject
    {
        ["Name"] = name,
        ["SampleAnalysisTypeIDs"] = new JsonArray("opendes:reference-data--SampleAnalysisType:NMR:"),
    });

    private static JsonObject Shift() => FakeOsduPlatform.Record(ShiftId, ShiftKind, new JsonObject { ["Name"] = "gamma ray alignment" });

    private static DeliveryWork Work(
        JsonObject document, IPayloadSource? content = null, bool metadata = true, long? existing = null,
        IReadOnlyDictionary<string, string>? state = null, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? completed = null) => new()
        {
            Key = SqlFlow.Delivery.Identity.DeliveryKey.Derive("rafs", [document["id"]!.GetValue<string>()]),
            TargetId = document["id"]!.GetValue<string>(),
            Document = document,
            DeliverMetadata = metadata,
            DeliverPayload = content is not null,
            Payload = content,
            ExistingVersion = existing,
            TargetState = state ?? new Dictionary<string, string>(StringComparer.Ordinal),
            CompletedSteps = completed ?? new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal),
        };

    private static List<string> Sent(FakeOsduPlatform platform, int from = 0)
        => platform.Calls.Skip(from).Select(c => c.Method + " " + Uri.UnescapeDataString(c.Uri.PathAndQuery)).ToList();

    private static string[] Urns(JsonObject record) => record["data"]!["DDMSDatasets"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    /// <summary>
    /// The differences the brief records between the RAFS contract and the service's content writes (section 8, item 3): the
    /// contract declares no body on two of them and only the split JSON form on the others, and the service takes parquet
    /// (as <c>application/x-parquet</c>) and JSON records on all five.
    /// </summary>
    internal static bool ContentForms(string violation)
        => violation.Contains("/data", StringComparison.Ordinal)
           && (violation.Contains("is not one of application/json", StringComparison.Ordinal) || violation.Contains("the operation takes no body", StringComparison.Ordinal));

    [Fact]
    public async Task A_samples_analysis_goes_to_its_collection_then_each_table_under_its_type_and_version()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var content = new NamedFiles(("nmr.parquet", Table("SamplesAnalysisID", AnalysisId, 3)), ("capillarypressure.1.1.0.json", SplitJson(AnalysisId)));

        var outcome = await rig.Protocol.DeliverAsync(Work(Analysis(), content));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);

        // The catalogue is read before anything is written; the tables go in type order, and the record is read back last.
        Assert.Equal(
            [
                "GET " + V2 + "samplesanalysis/analysistypes",
                "POST " + V2 + "samplesanalysis",
                "POST " + V2 + $"samplesanalysis/{AnalysisId}/data/capillarypressure?content_schema_version=1.1.0",
                "POST " + V2 + $"samplesanalysis/{AnalysisId}/data/nmr?content_schema_version=1.0.0",
                "GET " + V2 + $"samplesanalysis/{AnalysisId}",
            ],
            Sent(platform));
        Assert.Equal("application/json", platform.Calls[1].ContentType);
        Assert.Equal("application/json", platform.Calls[2].ContentType);
        Assert.Equal("application/x-parquet", platform.Calls[3].ContentType);
        Assert.Equal("no-store", platform.Calls[4].Headers["Cache-Control"]);
        Assert.Equal("no-store", platform.Calls[0].Headers["Cache-Control"]);

        var stored = platform.Records[AnalysisId];
        Assert.Equal(stored["version"]!.GetValue<long>(), outcome.TargetVersion);
        Assert.Equal(2, Urns(stored).Length);
        Assert.Equal(2, outcome.ChunksSent);
        Assert.Equal("2 content table(s)", outcome.Detail);

        var nmrUrn = outcome.Returned["content.nmr.urn"];
        Assert.Contains(nmrUrn, Urns(stored));
        Assert.Equal("1.0.0", outcome.Returned["content.nmr.schemaVersion"]);
        Assert.Equal(nmrUrn.Split('/')[^2], outcome.Returned["content.nmr.contentId"]);
        var datasets = outcome.Returned[RafsShape.DatasetsKey].Split(',');
        Assert.Equal(2, datasets.Length);
        Assert.All(datasets, d => Assert.StartsWith("opendes:dataset--File.Generic:", d, StringComparison.Ordinal));
        Assert.All(datasets, d => Assert.True(platform.Records.ContainsKey(d)));
        Assert.Equal(("application/x-parquet", "1.0.0"), (platform.RafsContent[AnalysisId + "|nmr"].MediaType, platform.RafsContent[AnalysisId + "|nmr"].SchemaVersion));

        Assert.Equal(
            [
                "rafs-ddms GET /api/rafs-ddms/v2/samplesanalysis/analysistypes",
                "rafs-ddms GET /api/rafs-ddms/v2/samplesanalysis/{record_id}",
                "rafs-ddms POST /api/rafs-ddms/v2/samplesanalysis",
                "rafs-ddms POST /api/rafs-ddms/v2/samplesanalysis/{record_id}/data/{analysis_type}",
            ],
            OsduContracts.AssertConform(platform.Calls, null, ContentForms, OsduContracts.RafsDdms));
    }

    [Fact]
    public async Task A_depth_shift_takes_one_row_under_its_collection_and_its_schema_is_asked_for_first()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var outcome = await rig.Protocol.DeliverAsync(Work(Shift(), new NamedFiles(("depthshift.parquet", Table("DepthShiftWPCID", ShiftId, 1)))));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal(
            [
                "GET " + V2 + "depthshift/data/schema?content_schema_version=1.0.0",
                "POST " + V2 + "depthshift",
                "POST " + V2 + $"depthshift/{ShiftId}/data?content_schema_version=1.0.0",
                "GET " + V2 + $"depthshift/{ShiftId}",
            ],
            Sent(platform));

        var calls = platform.Calls.Count;
        var twoRows = await Assert.ThrowsAsync<RecordHeldException>(
            () => rig.Protocol.DeliverAsync(Work(Shift(), new NamedFiles(("depthshift.parquet", Table("DepthShiftWPCID", ShiftId, 2))))));
        Assert.Contains("holds 2 rows; RAFS stores a depth shift of exactly one row", twoRows.Message, StringComparison.Ordinal);
        Assert.Empty(Sent(platform, calls));

        Assert.Contains(
            "rafs-ddms POST /api/rafs-ddms/v2/depthshift/{record_id}/data",
            OsduContracts.AssertConform(platform.Calls, null, ContentForms, OsduContracts.RafsDdms));
    }

    public static TheoryData<string, string, string> UnknownContent => new()
    {
        { "samples", "unknowntype.parquet", "RAFS serves no unknowntype content in the samplesanalysis collection" },
        { "samples", "nmr.2.0.0.parquet", "RAFS serves nmr content at the schema version(s) 1.0.0, not 2.0.0" },
        { "samples", "nmr.csv", "is neither .json nor .parquet" },
        { "samples", "NMR.parquet", "does not start with a content type (NMR)" },
        { "samples", "nmr.one.parquet", "names the content schema version 'one'" },
        { "shift", "depthshift.2.0.0.json", "RAFS has no depthshift content schema at version 2.0.0" },
        { "shift", "nmr.parquet", "holds nmr content, and the depthshift collection of the DDMS 'rafs' (/api/rafs-ddms) holds depthshift content alone" },
    };

    [Theory]
    [MemberData(nameof(UnknownContent))]
    public async Task Content_rafs_has_no_model_for_is_held_before_anything_is_written(string record, string file, string expected)
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var document = record == "shift" ? Shift() : Analysis();
        var held = await Assert.ThrowsAsync<RecordHeldException>(
            () => rig.Protocol.DeliverAsync(Work(document, new NamedFiles((file, Encoding.UTF8.GetBytes("{}"))))));
        Assert.Contains(expected, held.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(platform.Calls, c => c.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task A_record_rafs_would_refuse_is_held_and_a_table_given_twice_too()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);

        var untyped = Analysis();
        untyped["data"]!.AsObject().Remove("SampleAnalysisTypeIDs");
        var missing = await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(untyped)));
        Assert.Contains("data.SampleAnalysisTypeIDs names no analysis type", missing.Message, StringComparison.Ordinal);

        var foreign = Analysis();
        foreign["kind"] = "osdu:custom:work-product-component--SamplesAnalysis:1.0.0";
        var kind = await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(foreign)));
        Assert.Contains("is not one RAFS accepts: <authority>:wks:<entity type>", kind.Message, StringComparison.Ordinal);

        var extra = Analysis();
        extra["acl"]!["editors"] = new JsonArray("data.default.editors@opendes.example.com");
        var acl = await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(extra)));
        Assert.Contains("acl must hold non-empty viewers and owners and nothing else", acl.Message, StringComparison.Ordinal);

        var twice = await Assert.ThrowsAsync<RecordHeldException>(
            () => rig.Protocol.DeliverAsync(Work(Analysis(), new NamedFiles(("nmr.parquet", Table("SamplesAnalysisID", AnalysisId, 1)), ("nmr.1.0.0.json", SplitJson(AnalysisId))))));
        Assert.Contains("holds nmr content more than once", twice.Message, StringComparison.Ordinal);

        var report = FakeOsduPlatform.Record("opendes:work-product-component--SamplesAnalysesReport:r-1", "osdu:wks:work-product-component--SamplesAnalysesReport:1.0.0");
        var alone = await Assert.ThrowsAsync<RecordHeldException>(
            () => rig.Protocol.DeliverAsync(Work(report, new NamedFiles(("report.json", Encoding.UTF8.GetBytes("{}"))))));
        Assert.Contains("which holds records alone and takes no content", alone.Message, StringComparison.Ordinal);
        Assert.Empty(platform.Calls);
    }

    [Fact]
    public async Task A_metadata_update_carries_the_content_urns_the_stored_record_holds_and_a_retry_resumes_past_written_tables()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var first = await rig.Protocol.DeliverAsync(Work(Analysis(), new NamedFiles(("nmr.parquet", Table("SamplesAnalysisID", AnalysisId, 2)))));
        var urn = first.Returned["content.nmr.urn"];

        var calls = platform.Calls.Count;
        var renamed = await rig.Protocol.DeliverAsync(Work(Analysis("NMR run, corrected"), existing: first.TargetVersion, state: first.Returned));
        Assert.True(renamed.Succeeded, renamed.Failure?.Message);
        Assert.Equal(["GET " + V2 + $"samplesanalysis/{AnalysisId}", "POST " + V2 + "samplesanalysis"], Sent(platform, calls));
        var sent = JsonNode.Parse(platform.Calls[^1].Body!)!.AsArray().Single()!.AsObject();
        Assert.Equal([urn], Urns(sent));
        Assert.Equal([urn], Urns(platform.Records[AnalysisId]));
        Assert.Equal("NMR run, corrected", platform.Records[AnalysisId]["data"]!["Name"]!.GetValue<string>());

        // The record and one table landed on the earlier try: only the other table goes now.
        var completed = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["metadata"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["recordId"] = AnalysisId, ["version"] = renamed.TargetVersion!.Value.ToString(CultureInfo.InvariantCulture) },
            ["content-nmr"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["urn"] = urn, ["contentId"] = first.Returned["content.nmr.contentId"], ["schemaVersion"] = "1.0.0", ["dataset"] = first.Returned[RafsShape.DatasetsKey] },
        };
        calls = platform.Calls.Count;
        var resumed = await rig.Protocol.DeliverAsync(Work(
            Analysis("NMR run, corrected"),
            new NamedFiles(("nmr.parquet", Table("SamplesAnalysisID", AnalysisId, 2)), ("routinecoreanalysis.json", SplitJson(AnalysisId))),
            existing: renamed.TargetVersion,
            state: renamed.Returned,
            completed: completed));
        Assert.True(resumed.Succeeded, resumed.Failure?.Message);

        // The type catalogue was read by the first delivery and is not asked for again.
        Assert.Equal(
            ["POST " + V2 + $"samplesanalysis/{AnalysisId}/data/routinecoreanalysis?content_schema_version=1.0.0", "GET " + V2 + $"samplesanalysis/{AnalysisId}"],
            Sent(platform, calls));
        Assert.Equal(2, resumed.Returned[RafsShape.DatasetsKey].Split(',').Length);
        Assert.Equal(urn, resumed.Returned["content.nmr.urn"]);
    }

    [Fact]
    public async Task Blob_mode_names_the_content_by_its_uuid_and_registers_no_dataset()
    {
        var platform = new FakeOsduPlatform { RafsBlobMode = true };
        using var rig = new Rig(platform);
        var outcome = await rig.Protocol.DeliverAsync(Work(Analysis(), new NamedFiles(("nmr.parquet", Table("SamplesAnalysisID", AnalysisId, 1)))));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        var urn = outcome.Returned["content.nmr.urn"];
        Assert.StartsWith($"urn://rafs/{AnalysisId}/samplesanalysis/nmr/1.0.0/", urn, StringComparison.Ordinal);
        Assert.Equal(urn.Split('/')[^1], outcome.Returned["content.nmr.contentId"]);
        Assert.Equal("1.0.0", outcome.Returned["content.nmr.schemaVersion"]);
        Assert.False(outcome.Returned.ContainsKey(RafsShape.DatasetsKey));
        Assert.DoesNotContain(platform.Records.Keys, k => k.Contains("dataset--", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_removal_takes_the_content_datasets_rafs_leaves_behind()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var delivered = await rig.Protocol.DeliverAsync(Work(Analysis(), new NamedFiles(("nmr.parquet", Table("SamplesAnalysisID", AnalysisId, 1)))));
        var dataset = delivered.Returned[RafsShape.DatasetsKey];

        var calls = platform.Calls.Count;
        var removed = await rig.Protocol.DeleteAsync(AnalysisId, RemovalScope.Record, delivered.Returned);
        Assert.Equal("removed through RAFS (reversible); 1 of 1 content dataset(s) removed (reversible), the rest already gone", removed.Detail);
        Assert.Equal(["DELETE " + V2 + $"samplesanalysis/{AnalysisId}", "POST " + Storage + dataset + ":delete"], Sent(platform, calls));
        Assert.Contains(AnalysisId, platform.Removed);
        Assert.Contains(dataset, platform.Removed);
        Assert.Equal(VerifyOutcome.Missing, (await rig.Protocol.VerifyAsync(AnalysisId, delivered.TargetVersion)).Outcome);

        var again = await rig.Protocol.DeleteAsync(AnalysisId, RemovalScope.Record, delivered.Returned);
        Assert.True(again.AlreadyGone);

        calls = platform.Calls.Count;
        Assert.True((await rig.Protocol.DeleteAsync(AnalysisId, RemovalScope.History, delivered.Returned)).Deleted);
        var purged = await rig.Protocol.DeleteAsync(AnalysisId, RemovalScope.Everything, delivered.Returned);
        Assert.Equal("purged from OSDU (the record and every version); 1 of 1 content dataset(s) purged, the rest already gone", purged.Detail);
        Assert.Equal(
            ["DELETE " + Storage + AnalysisId + "/versions", "DELETE " + Storage + AnalysisId, "DELETE " + Storage + dataset],
            Sent(platform, calls));
        Assert.Contains(dataset, platform.Purged);

        OsduContracts.AssertConform(platform.Calls, null, ContentForms, OsduContracts.RafsDdms, OsduContracts.Storage);
    }

    [Fact]
    public async Task A_fluid_model_without_its_type_is_delivered_with_the_warning_rafs_gives()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var model = FakeOsduPlatform.Record("opendes:work-product-component--FluidModel:fm-1", "osdu:wks:work-product-component--FluidModel:1.0.0");
        var outcome = await rig.Protocol.DeliverAsync(Work(model, new NamedFiles(("blackoilfluidmodel.parquet", Table("FluidModelID", "opendes:work-product-component--FluidModel:fm-1", 4)))));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal("1 content table(s); RAFS warned: Records missing FluidModelTypeID will not be included in outputs produced by search endpoints", outcome.Detail);
        Assert.Contains("GET " + V2 + "fluidmodel/fluidmodeltypes", Sent(platform));
        Assert.Contains("POST " + V2 + "fluidmodel/opendes:work-product-component--FluidModel:fm-1/data/blackoilfluidmodel?content_schema_version=1.0.0", Sent(platform));
    }

    [Fact]
    public async Task The_probe_asks_for_the_service_information_then_its_type_catalogue()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var probe = await rig.Protocol.ProbeAsync();
        Assert.True(probe.Reachable, probe.Detail);
        Assert.Equal("the DDMS answered all 2 probes", probe.Detail);
        Assert.Equal(["GET " + FakeOsduPlatform.RafsRoot + "/info", "GET " + V2 + "samplesanalysis/analysistypes"], Sent(platform));
        Assert.Equal(
            ["rafs-ddms GET /api/rafs-ddms/info", "rafs-ddms GET /api/rafs-ddms/v2/samplesanalysis/analysistypes"],
            OsduContracts.AssertConform(platform.Calls, null, OsduContracts.RafsDdms));
    }

    [Fact]
    public async Task A_manifest_that_rewrites_a_samples_analysis_carries_its_content_urns()
    {
        var platform = new FakeOsduPlatform();
        platform.Register("Osdu_ingest", new FakeOsduPlatform.Script { Effect = ComposedRouteTests.Ingest });
        using var rig = new Rig(platform);
        var protocol = new OsduManifestAndDdmsProtocol(rig.Client, rig.Options, NullLogger.Instance, routing: DdmsRouting.Of(rig.Flow));
        WorkPayloadPart Tables(string hash) => new(PayloadParts.Bulk, "tables", new NamedFiles(("nmr.parquet", Table("SamplesAnalysisID", AnalysisId, 2))), hash);
        DeliveryWork Composed(JsonObject document, string hash, long? existing = null, IReadOnlyDictionary<string, string>? state = null)
            => Work(document) with
            {
                DeliverPayload = true,
                Payload = null,
                ExistingVersion = existing,
                TargetState = state ?? new Dictionary<string, string>(StringComparer.Ordinal),
                Parts = [Tables(hash)],
            };

        var first = (await protocol.DeliverBatchAsync([Composed(Analysis(), "h1")]))[0];
        Assert.True(first.Succeeded, first.Failure?.Message);
        var urn = first.Returned["content.nmr.urn"];
        Assert.Equal([urn], Urns(platform.Records[AnalysisId]));

        // The tables are as delivered; the record alone changes, and its manifest keeps the content URN.
        var second = (await protocol.DeliverBatchAsync([Composed(Analysis("renamed"), "h1", first.TargetVersion, first.Returned)]))[0];
        Assert.True(second.Succeeded, second.Failure?.Message);
        Assert.Equal([urn], Urns(platform.Records[AnalysisId]));
        Assert.Equal("renamed", platform.Records[AnalysisId]["data"]!["Name"]!.GetValue<string>());
        Assert.Equal(2, platform.Triggers("Osdu_ingest").Count());
        Assert.Single(platform.Calls, c => c.Method == HttpMethod.Post && c.Uri.AbsolutePath.EndsWith("/data/nmr", StringComparison.Ordinal));
    }
}
