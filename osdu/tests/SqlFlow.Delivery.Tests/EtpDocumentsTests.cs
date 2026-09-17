using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Protocols.Etp;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What a flow declares to deliver by the etp route (osdu/specs/reservoir-ddms/INTEGRATION.md sections 1.1, 4.3 and
/// 8.6): the route name, the Reservoir DDMS block and its bounds, and the rule that an Energistics object and an OSDU
/// record never travel by each other's route.
/// </summary>
public class EtpDocumentsTests
{
    private readonly DeliveryDocumentLoader _loader = new();

    [Fact]
    public void The_route_is_named_etp_and_its_block_says_where_the_reservoir_ddms_is()
    {
        var flow = _loader.ParseFlow(Flow("""
              etp:
                dataspace: volve/study
                objectsPerMessage: 25
                maxMessageBytes: 4000000
                maxArrayBytes: 8000000
                lock: true
            """), "grids.yaml");

        Assert.Equal(DeliveryProtocol.OsduEtp, flow.Target.Protocol);
        Assert.Equal("volve/study", flow.Target.Etp.Dataspace);
        Assert.Equal(25, flow.Target.Etp.ObjectsPerMessage);
        Assert.Equal(4_000_000, flow.Target.Etp.MaxMessageBytes);
        Assert.Equal(8_000_000, flow.Target.Etp.MaxArrayBytes);
        Assert.True(flow.Target.Etp.Lock);
        Assert.Equal(EtpTarget.DefaultPath, flow.Target.Etp.Path);
    }

    [Fact]
    public void A_flow_without_the_block_takes_every_default_and_each_record_names_its_own_dataspace()
    {
        var flow = _loader.ParseFlow(Flow(string.Empty), "grids.yaml");
        Assert.Null(flow.Target.Etp.Dataspace);
        Assert.Equal(EtpTarget.DefaultObjectsPerMessage, flow.Target.Etp.ObjectsPerMessage);
        Assert.False(flow.Target.Etp.Lock);
    }

    [Theory]
    [InlineData("    dataspace: ab", "is not a dataspace path")]
    [InlineData("    objectsPerMessage: 0", "objectsPerMessage is 0")]
    [InlineData("    maxMessageBytes: 1000", "maxMessageBytes is 1000")]
    [InlineData("    maxArrayBytes: 8", "maxArrayBytes is 8")]
    public void A_bound_the_reservoir_ddms_would_refuse_is_refused_when_the_flow_is_read(string line, string expected)
    {
        var refused = Assert.Throws<FlowValidationException>(() => _loader.ParseFlow(Flow("  etp:\n" + line), "grids.yaml"));
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
        Assert.StartsWith("grids.yaml:", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_block_belongs_to_the_etp_route_alone()
    {
        var refused = Assert.Throws<FlowValidationException>(
            () => _loader.ParseFlow(Flow("  etp:\n    dataspace: volve/study").Replace("protocol: etp", "protocol: storage", StringComparison.Ordinal), "grids.yaml"));
        Assert.Contains("target.etp declares the Reservoir DDMS", refused.Message, StringComparison.Ordinal);
        Assert.Contains("this flow's route is storage", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_energistics_object_and_an_osdu_record_never_travel_by_each_other_route()
    {
        var etp = _loader.ParseFlow(Flow(string.Empty), "grids.yaml");
        var storage = _loader.ParseFlow(Flow(string.Empty).Replace("protocol: etp", "protocol: storage", StringComparison.Ordinal), "grids.yaml");

        RouteChecks.Check(etp, "energistics:etp:obj_Grid2dRepresentation:2.0.1");
        RouteChecks.Check(storage, "osdu:wks:master-data--Wellbore:1.3.0");

        var record = Assert.Throws<DeliveryException>(() => RouteChecks.Check(etp, "osdu:wks:master-data--Wellbore:1.3.0"));
        Assert.Contains("the etp route writes Energistics objects", record.Message, StringComparison.Ordinal);
        var @object = Assert.Throws<DeliveryException>(() => RouteChecks.Check(storage, "energistics:etp:obj_Grid2dRepresentation:2.0.1"));
        Assert.Contains("only the etp route writes", @object.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_route_sends_its_object_and_its_arrays_as_parts_it_can_redeliver_on_their_own()
    {
        var flow = _loader.ParseFlow(Flow(string.Empty).Replace(
            "  work: work\n",
            "  work: work\n  payloads:\n    files: { root: /objects, locationColumn: object_path, hashColumn: object_hash }\n    bulk: { root: /arrays, locationColumn: array_path, hashColumn: array_hash }\n",
            StringComparison.Ordinal), "grids.yaml");

        var parts = PayloadParts.Of(flow);
        Assert.NotNull(parts);
        Assert.Equal([PayloadParts.Files, PayloadParts.Bulk], parts.Select(p => p.Role));
        Assert.All(parts, part => Assert.True(part.Optional));
        Assert.True(PayloadParts.Composed(DeliveryProtocol.OsduEtp));
    }

    [Theory]
    [InlineData("https://osdu.example.com", "/api/reservoir-ddms-etp/v2/", "wss://osdu.example.com/api/reservoir-ddms-etp/v2/")]
    [InlineData("https://osdu.example.com/", "api/reservoir-ddms-etp/v2/", "wss://osdu.example.com/api/reservoir-ddms-etp/v2/")]
    [InlineData("http://127.0.0.1:8080", "/etp", "ws://127.0.0.1:8080/etp")]
    public void The_endpoint_and_the_path_make_the_websocket_url(string endpoint, string path, string expected)
        => Assert.Equal(expected, EtpConnection.WebSocketUri(endpoint, path).ToString());

    [Fact]
    public void An_endpoint_that_is_no_url_is_refused_where_it_is_declared()
    {
        var refused = Assert.Throws<FlowValidationException>(() => EtpConnection.WebSocketUri("ftp://files.example.com", "/etp"));
        Assert.Contains("is not an http or https URL", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A flow in the single form on the etp route, with <paramref name="etp"/> added to its target block.</summary>
    private static string Flow(string etp) => ($$"""
        flowType: delivery
        name: grids
        source:
          connection: ${env:OSDU_SAMPLE_DB}
          record: { object: Db.ing.Grid, key: [grid_id] }
          work: work
        render:
          mapping: Grid@1.0.0
        target:
          endpoint: https://osdu.example.com
          headers: { data-partition-id: opendes }
          protocol: etp
        {{etp}}
        """).ReplaceLineEndings("\n");
}
