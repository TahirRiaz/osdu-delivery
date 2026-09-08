using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.Delivery.Tests;

public class YamlDocumentLoaderTests
{
    private const string Flow = """
        flowType: delivery
        name: demo
        parameters:
          logSource: { required: true }
        source:
          location: drops/{logSource}
          payloads: { curves: "curves/{deliveryKey}/chunk_*.parquet" }
          fingerprint: update_date
        render:
          mapping: WellLog@1.4.0
          parameters: { dataPartition: dev }
        target:
          endpoint: https://example.org/petrodb
          protocol: osduWellLog
          protocolOptions: { payload: curves, recordMethod: POST }
        reliability:
          concurrency: 2
          retry: { attempts: 5, backoff: fixed }
        """;

    [Fact]
    public void Loads_the_sample_flow_and_mapping()
    {
        var loader = new DeliveryDocumentLoader();
        var flow = loader.LoadFlow(Samples.Flow);
        Assert.Equal("recall-welllog", flow.Name);
        Assert.Equal(DeliveryProtocol.OsduWellLog, flow.Target.Protocol);
        Assert.Equal("WellLog", flow.Render.MappingName);
        Assert.Equal("1.4.0", flow.Render.MappingVersion);
        Assert.Equal(TargetAuthType.OAuth2ClientCredentials, flow.Target.Auth.Type);
        Assert.Equal(["Datasets", "DDMSDatasets", "ExtensionProperties"], flow.Target.ProtocolOptions.PreserveDataKeys);
        Assert.Equal("samples/recall-welllog/out/{logSource}/known-state", flow.Source.KnownState);

        var mapping = new MappingCatalog(Samples.Mappings, loader).Load("WellLog@1.4.0");
        Assert.Equal("osdu:wks:work-product-component--WellLog:1.4.0", mapping.Kind);
        Assert.Contains(mapping.Properties, p => p.Target == "data.Curves" && p.Collection && p.Definition == "Curve");
        Assert.Single(mapping.Fixtures);
    }

    [Fact]
    public void Parses_inline_flow_with_defaults()
    {
        var flow = new DeliveryDocumentLoader().ParseFlow(Flow, "inline.yaml");
        Assert.Equal(2, flow.Reliability.Concurrency);
        Assert.Equal(BackoffKind.Fixed, flow.Reliability.Retry.Backoff);
        Assert.Equal(5, flow.Reliability.Retry.Attempts);
        Assert.Equal("manifest.json", flow.Source.Manifest);
        Assert.Equal("POST", flow.Target.ProtocolOptions.RecordMethod);
        Assert.Equal(ChangeDetection.RenderedHash, flow.Change.Detect);
    }

    [Fact]
    public void Unknown_keys_are_a_parse_error_with_the_path()
    {
        var ex = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseFlow(Flow.Replace("concurrency: 2", "concurency: 2", StringComparison.Ordinal), "flow.yaml"));
        Assert.StartsWith("flow.yaml:", ex.Message, StringComparison.Ordinal);
        Assert.Contains("concurency", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Floating_mappings_and_unknown_protocols_are_rejected()
    {
        var loader = new DeliveryDocumentLoader();
        var floating = Assert.Throws<FlowValidationException>(() => loader.ParseFlow(Flow.Replace("WellLog@1.4.0", "WellLog", StringComparison.Ordinal), "f"));
        Assert.Contains("pinned", floating.Message, StringComparison.Ordinal);
        Assert.Equal(DeliveryProtocol.OsduManifest, loader.ParseFlow(Flow.Replace("osduWellLog", "osduManifest", StringComparison.Ordinal), "f").Target.Protocol);
        Assert.Equal(DeliveryProtocol.OsduFile, loader.ParseFlow(Flow.Replace("osduWellLog", "osduFile", StringComparison.Ordinal), "f").Target.Protocol);
        var unknownProtocol = Assert.Throws<FlowValidationException>(() => loader.ParseFlow(Flow.Replace("osduWellLog", "ftp", StringComparison.Ordinal), "f"));
        Assert.Contains("target.protocol", unknownProtocol.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Undeclared_location_tokens_and_payload_templates_are_rejected()
    {
        var loader = new DeliveryDocumentLoader();
        Assert.Throws<FlowValidationException>(() => loader.ParseFlow(Flow.Replace("drops/{logSource}", "drops/{other}", StringComparison.Ordinal), "f"));
        Assert.Throws<FlowValidationException>(() => loader.ParseFlow(Flow.Replace("curves/{deliveryKey}/chunk_*.parquet", "curves/chunk_*.parquet", StringComparison.Ordinal), "f"));
        Assert.Throws<FlowValidationException>(() => loader.ParseFlow(Flow.Replace("payload: curves", "payload: grids", StringComparison.Ordinal), "f"));
        Assert.Throws<FlowValidationException>(() => loader.ParseFlow(Flow.Replace("fingerprint: update_date", "fingerprint: update_date\n  knownState: known/{other}", StringComparison.Ordinal), "f"));
    }

    [Fact]
    public void Mapping_parse_validates_transform_configuration()
    {
        var loader = new DeliveryDocumentLoader();
        var mapping = loader.ParseMapping(TestSchema.MappingYaml, "m.yaml");
        Assert.Equal(MappingTransform.Reference, mapping.Properties[2].Transform);
        Assert.Equal(["Code"], mapping.Properties[2].Config.MatchBy);

        Assert.Throws<FlowValidationException>(() => loader.ParseMapping(TestSchema.MappingYaml.Replace("config: { type: UnitOfMeasure, matchBy: [Code] }", "config: { matchBy: [Code] }", StringComparison.Ordinal), "m"));
        Assert.Throws<FlowValidationException>(() => loader.ParseMapping(TestSchema.MappingYaml.Replace("identity: { naturalKey: [data.Name] }", "identity: { naturalKey: [data.Curves] }", StringComparison.Ordinal), "m"));
        Assert.Throws<FlowValidationException>(() => loader.ParseMapping(TestSchema.MappingYaml.Replace("dataPartition: { required: true }", "other: { required: true }", StringComparison.Ordinal), "m"));
        var wrongKind = Assert.Throws<FlowValidationException>(() => loader.ParseMapping(TestSchema.MappingYaml.Replace("documentType: mapping", "flowType: delivery", StringComparison.Ordinal), "m"));
        Assert.Contains("expected 'documentType: mapping'", wrongKind.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Catalog_requires_file_name_and_document_to_agree()
    {
        var dir = Samples.NewTempDirectory();
        File.WriteAllText(Path.Combine(dir, "Thing@2.0.0.yaml"), TestSchema.MappingYaml);
        var catalog = new MappingCatalog(dir, new DeliveryDocumentLoader());
        var ex = Assert.Throws<FlowValidationException>(() => catalog.Load("Thing@2.0.0"));
        Assert.Contains("filed as", ex.Message, StringComparison.Ordinal);
        Assert.Throws<FlowValidationException>(() => catalog.Load("Thing@9.9.9"));
        Assert.Contains("Thing@2.0.0", catalog.List());
    }

    [Fact]
    public void Flow_parameters_resolve_defaults_and_reject_unknowns()
    {
        var flow = new DeliveryDocumentLoader().ParseFlow(Flow, "f");
        Assert.Throws<FlowValidationException>(() => FlowParameters.Resolve(flow, null));
        var values = FlowParameters.Resolve(flow, new Dictionary<string, string> { ["logSource"] = "STAT_COMP" });
        Assert.Equal("drops/STAT_COMP", FlowParameters.DropLocation(flow, values));
        Assert.Null(FlowParameters.KnownStateLocation(flow, values));
        var withKnownState = new DeliveryDocumentLoader().ParseFlow(Flow.Replace("fingerprint: update_date", "fingerprint: update_date\n  knownState: known/{logSource}", StringComparison.Ordinal), "f");
        Assert.Equal("known/STAT_COMP", FlowParameters.KnownStateLocation(withKnownState, values));
        Assert.Throws<FlowValidationException>(() => FlowParameters.Resolve(flow, new Dictionary<string, string> { ["logSource"] = "x", ["nope"] = "y" }));
    }
}
