using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A file location a flow declares is written out: no file location in the product is resolved from a <c>${...}</c>
/// reference, so a delivery flow's work root and every payload root refuse one, as a retrieval flow's target does, with
/// the same message; what varies per run or environment is a declared <c>{parameter}</c>.
/// </summary>
public class LocationReferenceTests
{
    private const string Flow = """
        flowType: delivery
        name: welllog-delivery
        partitions: [dev]
        parameters:
          region: { required: true }
        source:
          connection: ${env:OSDU_DATA_DB}
          record: { object: OsduData.silver.WellLog, key: [log_id] }
          payloads:
            las: { root: "las/{region}", locationColumn: las_folder, hashColumn: las_hash }
          lastModified: update_date
          work: work/{region}
        render:
          mapping: WellLog@1.0.0
        target:
          endpoint: ${env:OSDU_URL}
          protocol: file
          protocolOptions: { payload: las }
        """;

    private static string With(string from, string to)
    {
        var yaml = Flow.ReplaceLineEndings("\n");
        var changed = yaml.Replace(from, to, StringComparison.Ordinal);
        Assert.NotEqual(yaml, changed);
        return changed;
    }

    private static string Refused(string yaml)
        => Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseSource(yaml, "flows/welllog.yaml")).Message;

    [Fact]
    public void Locations_written_out_with_declared_parameters_are_read()
        => Assert.Equal("welllog-delivery", new DeliveryDocumentLoader().ParseSource(Flow.ReplaceLineEndings("\n"), "flows/welllog.yaml").Name);

    [Fact]
    public void A_reference_in_the_work_root_is_refused()
    {
        var message = Refused(With("work: work/{region}", "work: ${env:WORK_ROOT}/{region}"));

        Assert.Equal(
            "flows/welllog.yaml: source.work '${env:WORK_ROOT}/{region}' holds a ${...} reference, and a location is not resolved from one: write the path or storage URI itself, and vary it per run or environment with a {parameter} token declared under parameters.",
            message);
    }

    [Fact]
    public void A_reference_in_a_payload_root_is_refused()
    {
        var message = Refused(With("root: \"las/{region}\"", "root: \"${keyvault:estate/las-root}\""));

        Assert.StartsWith("flows/welllog.yaml: source.payloads.las.root '${keyvault:estate/las-root}' holds a ${...} reference", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reference_in_an_interfaces_files_root_is_refused_naming_the_interface()
    {
        const string Interfaces = """
            flowType: delivery
            name: welllogs
            partitions: [dev]
            source:
              connection: ${env:OSDU_DATA_DB}
              work: work/welllogs
            target:
              endpoint: ${env:OSDU_URL}
            interfaces:
              logs:
                record: { object: OsduData.silver.WellLog, key: [log_id] }
                lastModified: update_date
                files: { root: "${env:LAS_ROOT}", locationColumn: las_folder, hashColumn: las_hash }
                mapping: WellLog@1.0.0
            """;

        var message = Refused(Interfaces.ReplaceLineEndings("\n"));

        Assert.StartsWith("flows/welllog.yaml: interfaces.logs.files.root '${env:LAS_ROOT}' holds a ${...} reference", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_retrieval_target_is_refused_through_the_same_check()
    {
        const string Retrieval = """
            flowType: retrieval
            name: welllog-retrieval
            source:
              endpoint: ${env:OSDU_URL}
              headers: { data-partition-id: dev }
              kind: "osdu:wks:work-product-component--WellLog:1.*.*"
            target:
              location: ${env:EXPORT_ROOT}/welllogs
            """;

        var refused = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseRetrieval(Retrieval.ReplaceLineEndings("\n"), "flows/retrieval.yaml"));

        Assert.Equal(
            "flows/retrieval.yaml: target.location '${env:EXPORT_ROOT}/welllogs' holds a ${...} reference, and a location is not resolved from one: write the path or storage URI itself, and vary it per run or environment with a {parameter} token declared under parameters.",
            refused.Message);
    }
}
