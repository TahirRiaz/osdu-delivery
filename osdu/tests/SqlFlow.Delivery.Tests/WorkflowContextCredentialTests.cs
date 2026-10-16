using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A workflow stage's context never holds a credential as written, whatever workflow it is sent to: a key the workflow's
/// contract marks a credential, and any other key named as a secret, is a single <c>{secret:name}</c> placeholder naming a
/// reference under the route's secrets. A literal would be sent to the Workflow service and kept in the record's step
/// history; a workflow OSDU Delivery has no contract for is checked by the name test alone.
/// </summary>
public class WorkflowContextCredentialTests
{
    private static string Flow(string workflow, string context) => $$"""
        flowType: delivery
        name: csv-things
        source:
          connection: ${env:ESTATE_DB}
          work: ../.work/csv
          record: { object: Estate.ing.CsvFile, key: [file_id] }
        render:
          mapping: CsvDescriptor@1.0.0
        target:
          endpoint: ${env:OSDU_URL}
          headers:
            data-partition-id: dev
          protocol: workflow
          workflow:
            anchor: storage
            stages:
              - workflow: {{workflow}}
                context:
        {{context}}
            secrets:
              apiKey: ${env:CUSTOM_API_KEY}
        """.ReplaceLineEndings("\n");

    private static string Refused(string yaml)
        => Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseSource(yaml, "flows/csv.yaml")).Message;

    private static void Read(string yaml) => new DeliveryDocumentLoader().ParseSource(yaml, "flows/csv.yaml");

    [Theory]
    [InlineData("api_key: plain-text-key", "'api_key' is named as a secret")]
    [InlineData("connection: { user: loader, password: plain-text }", "'connection.password' is named as a secret")]
    [InlineData("access_token: [plain-text]", "'access_token[0]' is named as a secret")]
    [InlineData("pin_secret: 1234", "'pin_secret' is named as a secret")]
    [InlineData("api_key: \"Bearer {secret:apiKey}\"", "'api_key' is named as a secret")]
    public void A_workflow_without_a_contract_refuses_a_literal_under_a_key_named_as_a_secret(string entry, string expected)
    {
        var message = Refused(Flow("custom_ingest", "              recordId: \"{record:id}\"\n              " + entry));

        Assert.Contains($"target.workflow.stages[0].context: {expected}; give it as {{secret:name}} with the reference under secrets, never as a value in the document.", message, StringComparison.Ordinal);
        Assert.DoesNotContain("plain-text", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_contract_that_does_not_cover_a_key_named_as_a_secret_still_refuses_its_literal()
    {
        var message = Refused(Flow("csv_ingestion", "              id: \"{record:id}\"\n              dataPartitionId: \"{partition}\"\n              x_api_key: plain-text-key"));

        Assert.Contains("target.workflow.stages[0].context: 'x_api_key' is named as a secret", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_secret_placeholder_and_settings_that_are_no_secret_are_read()
        => Read(Flow(
            "custom_ingest",
            "              recordId: \"{record:id}\"\n              api_key: \"{secret:apiKey}\"\n              token_type: bearer\n              mode: full\n              password: ~"));

    [Fact]
    public void A_contract_key_that_does_not_read_as_a_template_is_refused_with_its_reason_rather_than_failing_the_load()
    {
        var message = Refused(Flow("csv_ingestion", "              id: \"{record:id\"\n              dataPartitionId: \"{partition}\""));

        Assert.Contains("target.workflow.stages[0].context.id:", message, StringComparison.Ordinal);
        Assert.Contains("never closed", message, StringComparison.Ordinal);
    }
}
