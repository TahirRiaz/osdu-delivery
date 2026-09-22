using System.Text.Json.Nodes;
using SqlFlow.Delivery.Engine.Workflows;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The placeholder language a workflow route fills its execution contexts, outputs and result queries from
/// (docs/interfaces-design.md section 5.9): what it reads, the shapes it gives, the modifiers, the refusals, and secrets
/// that only the request carries.
/// </summary>
public sealed class WorkflowTemplateTests
{
    private const string AnchorId = "dev:work-product-component--Activity:conv-1";

    private static WorkflowValues Values()
    {
        var record = (JsonObject)JsonNode.Parse("""
            {
              "id": "dev:work-product-component--Activity:conv-1",
              "kind": "osdu:wks:work-product-component--Activity:1.3.0",
              "tags": { "source": "petrel" },
              "data": {
                "Name": "ZGY conversion",
                "Datasets": ["dev:dataset--FileCollection.SEGY:segy-1:", "dev:dataset--FileCollection.SEGY:segy-2:12"],
                "Parameters": [
                  { "Title": "work_product_id", "DataObjectParameter": "dev:work-product--WorkProduct:wp-1:" },
                  { "Title": "lossless", "BooleanParameter": true }
                ],
                "Count": 3
              }
            }
            """)!;
        var values = new WorkflowValues("dev", "osdu-delivery", record) { RunId = "run-7" };
        values.SetInput("h5", ["dev:dataset--File.Generic:conv-1-h5-0", "dev:dataset--File.Generic:conv-1-h5-1"]);
        values.SetOutput(1, "manifestId", JsonValue.Create("dev:dataset--File.Generic:m-1"));
        values.SetSecret("sdToken", "s3cr3t-value");
        return values;
    }

    [Fact]
    public void A_lone_placeholder_keeps_the_shape_of_its_value_and_text_around_one_takes_a_scalar()
    {
        var values = Values();
        Assert.Equal("dev", WorkflowTemplate.RenderText("{partition}", values));
        Assert.Equal("osdu-delivery", WorkflowTemplate.RenderText("{appKey}", values));
        Assert.Equal("run-7", WorkflowTemplate.RenderText("{runId}", values));
        Assert.Equal(AnchorId, WorkflowTemplate.RenderText("{record:id}", values));
        Assert.Equal("id=" + AnchorId + " in dev", WorkflowTemplate.RenderText("id={record:id} in {partition}", values));

        // A list stays a list, a number stays a number, and a whole object is carried as it is.
        var h5 = Assert.IsType<JsonArray>(WorkflowTemplate.RenderString("{input:h5}", values, revealSecrets: false));
        Assert.Equal(2, h5.Count);
        Assert.Equal(3, WorkflowTemplate.RenderString("{record:data.Count}", values, revealSecrets: false)!.GetValue<int>());
        Assert.IsType<JsonObject>(WorkflowTemplate.RenderString("{record:tags}", values, revealSecrets: false));
        Assert.Equal("dev:dataset--File.Generic:conv-1-h5-1", WorkflowTemplate.RenderText("{input:h5[1]}", values));
        Assert.Equal("dev:dataset--File.Generic:m-1", WorkflowTemplate.RenderText("{stage:1.manifestId}", values));
        Assert.Equal("dev:dataset--File.Generic:conv-1-manifest", WorkflowTemplate.RenderText("{dataset:manifest}", values));
        Assert.Equal("{literal} " + AnchorId, WorkflowTemplate.RenderText("{{literal}} {record:id}", values));
    }

    [Fact]
    public void A_record_path_selects_by_index_by_property_and_by_every_item()
    {
        var values = Values();
        Assert.Equal("dev:dataset--FileCollection.SEGY:segy-1:", WorkflowTemplate.RenderText("{record:data.Datasets[0]}", values));
        Assert.Equal(
            "dev:work-product--WorkProduct:wp-1:",
            WorkflowTemplate.RenderText("{record:data.Parameters[Title=work_product_id].DataObjectParameter|first}", values));
        var all = Assert.IsType<JsonArray>(WorkflowTemplate.RenderString("{record:data.Datasets[*]}", values, revealSecrets: false));
        Assert.Equal(2, all.Count);
        var flags = Assert.IsType<JsonArray>(WorkflowTemplate.RenderString("{record:data.Parameters[Title=lossless].BooleanParameter}", values, revealSecrets: false));
        Assert.True(flags[0]!.GetValue<bool>());
    }

    [Fact]
    public void Modifiers_turn_references_into_ids_and_ids_into_references()
    {
        var values = Values();
        Assert.Equal("dev:dataset--FileCollection.SEGY:segy-1", WorkflowTemplate.RenderText("{record:data.Datasets[0]|id}", values));
        Assert.Equal("dev:dataset--FileCollection.SEGY:segy-2", WorkflowTemplate.RenderText("{record:data.Datasets[1]|id}", values));
        Assert.Equal(AnchorId + ":", WorkflowTemplate.RenderText("{record:id|ref}", values));
        var ids = Assert.IsType<JsonArray>(WorkflowTemplate.RenderString("{record:data.Datasets[*]|id}", values, revealSecrets: false));
        Assert.Equal("dev:dataset--FileCollection.SEGY:segy-2", ids[1]!.GetValue<string>());
        Assert.IsType<JsonArray>(WorkflowTemplate.RenderString("{record:id|list}", values, revealSecrets: false));
        Assert.Equal("\"run-7\"", WorkflowTemplate.RenderText("{runId|json}", values));
        Assert.Equal("dev:dataset--File.Generic:conv-1-h5-0", WorkflowTemplate.RenderText("{input:h5|first}", values));

        // A unique segment may hold colons; only a trailing empty or numeric segment is a version.
        Assert.Equal("p:t:a:b", TargetId.WithoutVersion("p:t:a:b"));
        Assert.Equal("p:t:a", TargetId.WithoutVersion("p:t:a:"));
        Assert.Equal("p:t:a", TargetId.WithoutVersion("p:t:a:1614105463059152"));
        Assert.Equal("p:t:a", TargetId.WithoutVersion("p:t:a"));
    }

    [Fact]
    public void A_secret_is_filled_only_for_the_request_that_carries_it()
    {
        var values = Values();
        var template = (JsonObject)JsonNode.Parse("""{ "id_token": "{secret:sdToken}", "file_record_id": "{record:id}" }""")!;
        var shown = WorkflowTemplate.Render(template, values, revealSecrets: false)!;
        Assert.Equal(WorkflowTemplate.Redacted, shown["id_token"]!.GetValue<string>());
        Assert.DoesNotContain("s3cr3t", shown.ToJsonString(), StringComparison.Ordinal);

        var sent = WorkflowTemplate.Render(template, values, revealSecrets: true)!;
        Assert.Equal("s3cr3t-value", sent["id_token"]!.GetValue<string>());

        var missing = (JsonObject)JsonNode.Parse("""{ "k": "{secret:other}" }""")!;
        Assert.Contains("names a secret the route does not declare", Assert.Throws<RecordHeldException>(() => WorkflowTemplate.Render(missing, values, revealSecrets: true)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{record:data.Missing}", "reads a value the record does not have")]
    [InlineData("{input:unknown}", "names an input the route has not registered")]
    [InlineData("{input:h5[5]}", "asks for dataset 5 of the input, which registered 2")]
    [InlineData("{stage:2.manifestId}", "reads an output stage 2 has not produced")]
    [InlineData("ids: {input:h5}", "is a list or an object, and 'ids: {input:h5}' writes it into text")]
    public void A_value_a_record_cannot_give_holds_it(string template, string expected)
    {
        var held = Assert.Throws<RecordHeldException>(() => WorkflowTemplate.RenderString(template, Values(), revealSecrets: false));
        Assert.Contains(expected, held.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{unknown}", "names 'unknown', which is not one of")]
    [InlineData("{record}", "needs an argument")]
    [InlineData("{partition:x}", "partition takes no argument")]
    [InlineData("{record:id|upper}", "uses the modifier 'upper'")]
    [InlineData("{record:data..Name}", "has an empty property name")]
    [InlineData("{record:data.List[x y]}", "which is not [n], [*] or [Property=value]")]
    [InlineData("{stage:x.y}", "names an earlier stage's output as n.output")]
    [InlineData("{input:1bad}", "names an input as name or name[index]")]
    [InlineData("text } more", "closes nothing")]
    [InlineData("text { more", "opens a placeholder that is never closed")]
    public void A_template_that_does_not_read_is_refused_with_where_it_is(string text, string expected)
    {
        var problems = WorkflowTemplate.Problems(new JsonObject { ["key"] = new JsonArray(JsonValue.Create(text)) }, "stages[0].context");
        var problem = Assert.Single(problems);
        Assert.StartsWith("stages[0].context.key[0]: ", problem, StringComparison.Ordinal);
        Assert.Contains(expected, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void The_anchor_tag_and_derived_dataset_ids_are_stable_and_take_the_anchor_partition()
    {
        Assert.Equal(WorkflowValues.Tag(AnchorId), WorkflowValues.Tag(AnchorId));
        Assert.Matches("^osdu-delivery-[0-9a-f]{24}$", WorkflowValues.Tag(AnchorId));
        Assert.NotEqual(WorkflowValues.Tag(AnchorId), WorkflowValues.Tag(AnchorId + "x"));
        Assert.Equal(
            "dev:dataset--FileCollection.Generic:conv-1-files",
            WorkflowValues.DerivedDatasetId(AnchorId, "dataset--FileCollection.Generic", "files"));
        Assert.Throws<DeliveryException>(() => WorkflowValues.DerivedDatasetId("not-an-id", "dataset--File.Generic", "x"));
    }

    [Fact]
    public void Record_ids_are_found_in_whatever_a_run_produced()
    {
        // The text Airflow gives for a Python dict, as the Energistics collection reads it (workflows brief section 3.6).
        var repr = JsonValue.Create("{'energyml_manifest_creation': ['dev:dataset--File.Generic:5f2c:', 'dev:work-product-component--WellLog:w1:3']}");
        Assert.Equal(["dev:dataset--File.Generic:5f2c", "dev:work-product-component--WellLog:w1"], WorkflowIds.Extract(repr, null));
        Assert.Equal(["dev:dataset--File.Generic:5f2c"], WorkflowIds.Extract(repr, "dataset--File.Generic"));

        // A JSON value, as Airflow 3 gives it, and a list of ids.
        var native = JsonNode.Parse("""{"epc": "dev:dataset--File.Generic:epc-1", "h5": "dev:dataset--File.Generic:h5-1", "count": 2}""");
        Assert.Equal(2, WorkflowIds.Extract(native, "dataset--File.Generic").Count);
        Assert.Empty(WorkflowIds.Extract(JsonValue.Create("no ids here: surrogate-key:record-1"), null));
    }
}
