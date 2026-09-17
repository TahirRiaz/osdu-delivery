using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The storage check of <c>target.verifyReferences: storage</c> (docs/interfaces-design.md section 7): the ids a record
/// refers to that no record of the ledger holds are looked up in OSDU's storage service, and a record naming one storage
/// does not hold is not sent.
/// </summary>
public class ReferenceCheckTests : IDisposable
{
    private static readonly SecretResolver Secrets = new([new EnvSecretProvider()]);

    private readonly SqliteOsdu _db = new();
    private readonly TestClock _clock = new();
    private readonly FakeOsduPlatform _platform = new();
    private readonly HttpRuntime _runtime;
    private readonly Guid _flow = FlowId.Of("references");

    public ReferenceCheckTests()
    {
        _runtime = new HttpRuntime(
            new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
            Secrets, TimeProvider.System, _platform, allowLoopback: true);
    }

    private OsduLedger Ledger => _db.Ledger(_clock);

    private ReferenceCheck Check() => new(
        Ledger,
        new OsduHttpClient(
            _runtime, FakeOsduPlatform.Endpoint, new TargetAuth { Type = TargetAuthType.None },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "opendes" }),
        "/api/storage/v2/query/records");

    private static RecordState Record(Guid flowId, string sourceKey, params string[] references) => new()
    {
        DeliveryKey = DeliveryKey.Derive("references", [sourceKey]),
        FlowId = flowId,
        SourceKey = sourceKey,
        MappingName = "Thing",
        TargetId = $"opendes:work-product-component--WellLog:{sourceKey}",
        PendingDocumentRef = "0:0:10",
        PendingRenderContext = "{}",
        PendingMetadataHash = "mh",
        PendingMetadata = true,
        PendingReferences = references.Select(id => new RecordReference(id, "data.WellboreID")).ToList(),
    };

    [Fact]
    public async Task An_id_neither_the_ledger_nor_storage_holds_is_named_and_the_others_are_not()
    {
        _platform.Records["opendes:master-data--Wellbore:in-storage"] = new JsonObject
        {
            ["id"] = "opendes:master-data--Wellbore:in-storage",
            ["kind"] = "osdu:wks:master-data--Wellbore:1.3.0",
            ["version"] = 1,
        };
        var wellbores = FlowId.Of("wellbores");
        await Ledger.UpsertPendingAsync(wellbores, [Record(wellbores, "in-ledger") with { TargetId = "opendes:master-data--Wellbore:in-ledger", PendingReferences = [] }]);
        var known = Record(_flow, "L-1", "opendes:master-data--Wellbore:in-storage", "opendes:master-data--Wellbore:in-ledger");
        var dangling = Record(_flow, "L-2", "opendes:master-data--Wellbore:nowhere");

        var missing = await Check().MissingAsync([known, dangling], CancellationToken.None);

        var named = Assert.Single(missing);
        Assert.Equal(dangling.DeliveryKey, named.Key);
        Assert.Equal("opendes:master-data--Wellbore:nowhere", Assert.Single(named.Value).Id);
        Assert.Contains("data.WellboreID", ReferenceCheck.Describe(named.Value), StringComparison.Ordinal);

        // Only the ids the ledger does not hold are asked of storage.
        var asked = _platform.Calls.Where(c => c.Uri.AbsolutePath == "/api/storage/v2/query/records").ToList();
        var body = Assert.Single(asked).Body!;
        Assert.Contains("in-storage", body, StringComparison.Ordinal);
        Assert.Contains("nowhere", body, StringComparison.Ordinal);
        Assert.DoesNotContain("in-ledger", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_record_that_refers_to_nothing_asks_storage_nothing()
    {
        Assert.Empty(await Check().MissingAsync([Record(_flow, "L-1")], CancellationToken.None));
        Assert.Empty(_platform.Calls);
    }

    [Theory]
    [InlineData("storage", ReferenceVerification.Storage)]
    [InlineData("none", ReferenceVerification.None)]
    [InlineData(null, ReferenceVerification.None)]
    public void A_flow_says_what_its_references_are_checked_against(string? declared, ReferenceVerification expected)
    {
        var yaml = Flow(declared is null ? string.Empty : $"  verifyReferences: {declared}\n");
        Assert.Equal(expected, new DeliveryDocumentLoader().ParseFlow(yaml, "flow.yaml").Target.VerifyReferences);
    }

    [Fact]
    public void A_value_that_is_not_a_check_and_the_route_that_writes_no_storage_record_are_refused()
    {
        var loader = new DeliveryDocumentLoader();
        var unknown = Assert.Throws<FlowValidationException>(() => loader.ParseFlow(Flow("  verifyReferences: osdu\n"), "flow.yaml"));
        Assert.Contains("target.verifyReferences", unknown.Message, StringComparison.Ordinal);
        Assert.Contains("none, storage", unknown.Message, StringComparison.Ordinal);

        var dspdm = Assert.Throws<FlowValidationException>(
            () => loader.ParseFlow(Flow("  verifyReferences: storage\n").Replace("protocol: storage", "protocol: dspdm", StringComparison.Ordinal), "flow.yaml"));
        Assert.Contains("dspdm route", dspdm.Message, StringComparison.Ordinal);
    }

    /// <summary>A flow in the single form with <paramref name="target"/> added to its target block.</summary>
    private static string Flow(string target) => ($$"""
        flowType: delivery
        name: references
        source:
          connection: ${env:OSDU_SAMPLE_DB}
          record: { object: Db.ing.WellLog, key: [uwi] }
          work: work
        render:
          mapping: WellLog@1.4.0
        target:
          endpoint: https://osdu.example.com
          headers: { data-partition-id: opendes }
          protocol: storage
        {{target}}
        """).ReplaceLineEndings("\n");

    public void Dispose()
    {
        _runtime.Dispose();
        _platform.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }
}
