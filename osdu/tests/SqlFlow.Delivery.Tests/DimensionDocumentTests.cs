using SqlFlow.Core;
using SqlFlow.Core.Lineage;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The dimension flow document (<c>flowType: dimension</c>): what it declares, and every rule the loader holds it to, each
/// refusal naming the place in the document it is about.
/// </summary>
public class DimensionDocumentTests
{
    private const string Head = """
        flowType: dimension
        name: wells-dimensions
        batch: reference
        partitions: [dev, test]
        source:
          endpoint: ${env:OSDU_URL}
          auth:
            type: bearer
            secretRef: ${env:OSDU_TOKEN}

        """;

    private const string Curves = """
        dimensions:
          - name: CurveMnemonic
            description: Every curve mnemonic the well logs hold.
            kind: "osdu:wks:work-product-component--WellLog:*"
            path: data.Curves.Mnemonic
        """;

    private static DimensionFlowDefinition Parse(string yaml) => new DeliveryDocumentLoader().ParseDimension(yaml, "flows/dims.yaml");

    private static FlowValidationException Refused(string yaml) => Assert.Throws<FlowValidationException>(() => Parse(yaml));

    [Fact]
    public void A_document_declares_its_dimensions_with_their_paths_and_clean_steps()
    {
        var flow = Parse(Head + """
            parameters:
              country: { default: NO }
            dimensions:
              - name: CurveMnemonic
                kind: "osdu:wks:work-product-component--WellLog:*"
                query: 'data.Country:"{country}" AND data.Partition:"{partition}"'
                path: data.Curves.Mnemonic
                countRecords: true
                maxValues: 200000
                clean:
                  - trim
                  - collapseSpaces
                  - upper
                  - nfkc
                  - replace: { pattern: '^(\w+)_\d+$', with: '$1' }
                  - map: CurveAliases
                  - map: { dictionary: CurveFamilies, field: family, otherwise: ~ }
                  - map: { dictionary: CurveFamilies, otherwise: Other }
              - name: LegalTag
                kind: "*:*:*:*"
                path: legal.legaltags
                partitions: [dev]
            """);

        Assert.Equal("wells-dimensions", flow.Name);
        Assert.Equal(["dev", "test"], flow.Partitions);
        Assert.Equal(DimensionSource.DefaultAggregationSize, flow.Source.AggregationSize);
        Assert.Equal(DimensionSource.DefaultQueryPath, flow.Source.QueryPath);
        Assert.True(flow.ReadsDictionaries);

        var curves = flow.Dimensions[0];
        Assert.Equal("data.Curves.Mnemonic", curves.Path);
        Assert.True(curves.CountRecords);
        Assert.Equal(200_000, curves.MaxValues);
        Assert.Equal(
            [CleanStepKind.Trim, CleanStepKind.CollapseSpaces, CleanStepKind.Upper, CleanStepKind.Nfkc, CleanStepKind.Replace, CleanStepKind.Map, CleanStepKind.Map, CleanStepKind.Map],
            curves.Clean.Select(s => s.Kind));
        Assert.Equal((@"^(\w+)_\d+$", "$1"), (curves.Clean[4].Pattern, curves.Clean[4].With));
        Assert.Equal(("CurveAliases", null, MapOtherwise.Keep), (curves.Clean[5].Dictionary, curves.Clean[5].Field, curves.Clean[5].Otherwise));
        Assert.Equal(("CurveFamilies", "family", MapOtherwise.LeaveOut), (curves.Clean[6].Dictionary, curves.Clean[6].Field, curves.Clean[6].Otherwise));
        Assert.Equal((MapOtherwise.Text, "Other"), (curves.Clean[7].Otherwise, curves.Clean[7].OtherwiseText));
        Assert.Equal(16, curves.DefinitionHash.Length);

        var legal = flow.Dimensions[1];
        Assert.Equal(DimensionSpec.DefaultMaxValues, legal.MaxValues);
        Assert.Empty(legal.Clean);
        Assert.Equal(["dev"], legal.Partitions);
        Assert.True(legal.BuildsIn("dev"));
        Assert.False(legal.BuildsIn("test"));
    }

    [Fact]
    public void A_dimensions_hash_says_whether_what_it_declares_changed_and_nothing_else()
    {
        var first = Parse(Head + Curves).Dimensions[0].DefinitionHash;
        var relaidOut = Parse(Head + """
            dimensions:
              - path: data.Curves.Mnemonic
                kind: "osdu:wks:work-product-component--WellLog:*"
                name: CurveMnemonic
                description: Every curve mnemonic the well logs hold.
            """).Dimensions[0].DefinitionHash;
        var cleaned = Parse(Head + Curves + "\n    clean: [upper]").Dimensions[0].DefinitionHash;

        Assert.Equal(first, relaidOut);
        Assert.NotEqual(first, cleaned);
    }

    [Fact]
    public void A_label_is_read_through_one_path_or_a_list_of_paths_and_changes_what_the_dimension_declares()
    {
        var flow = Parse(Head + """
            dimensions:
              - name: Wellbore
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                label: data.FacilityName
              - name: Country
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                label: [data.GeoContexts.GeoPoliticalEntityID, ' data.GeoPoliticalEntityName ']
            """);

        Assert.Equal(["data.FacilityName"], flow.Dimensions[0].Label);
        Assert.Equal(["data.GeoContexts.GeoPoliticalEntityID", "data.GeoPoliticalEntityName"], flow.Dimensions[1].Label);
        Assert.Empty(Parse(Head + Curves).Dimensions[0].Label);

        // Keys labelled another way are another dimension's values, so the label is part of what the hash says changed.
        Assert.NotEqual(Parse(Head + Curves).Dimensions[0].DefinitionHash, Parse(Head + Curves + "\n    label: data.Name").Dimensions[0].DefinitionHash);
    }

    [Theory]
    [InlineData("label: 'data.Facility Name'", "label 'data.Facility Name' is not a property path")]
    [InlineData("label: [data.A, 'x y']", "label[1] 'x y' is not a property path")]
    [InlineData("label: [data.A, [data.B]]", "label[1] is not a path")]
    [InlineData("label: [data.A, data.B, data.C, data.D]", "at most 3")]
    [InlineData("label: { path: data.A }", "it is neither")]
    public void A_label_that_breaks_a_rule_is_refused_naming_the_rule(string label, string reason)
    {
        var refused = Refused(Head + Curves + "\n    " + label);
        Assert.Contains("dimensions[0] 'CurveMnemonic'", refused.Message, StringComparison.Ordinal);
        Assert.Contains(reason, refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("kind", "osdu:wks:master-data--Wellbore:*")]
    [InlineData("id", "osdu:wks:*:*")]
    [InlineData("acl.viewers", "*:*:*:*")]
    [InlineData("legal.status", "*:*:*:*")]
    [InlineData("tags.Source", "*:*:*:*")]
    [InlineData("createTime", "*:*:*:*")]
    [InlineData("data.Anything.Here", "*:*:*:*")]
    public void Any_property_search_can_match_exactly_can_be_a_dimension(string path, string kind)
        => Assert.Equal(path, Parse(Head + $"""
            dimensions:
              - name: D
                kind: "{kind}"
                path: {path}
            """).Dimensions[0].Path);

    [Theory]
    [InlineData("meta", "not a property the index holds")]
    [InlineData("acl", "under acl it holds")]
    [InlineData("tags", "name one tag")]
    [InlineData("data.Curves[*].Mnemonic", "not a property path")]
    [InlineData("x-acl", "not a property path")]
    public void A_path_search_holds_no_exact_value_at_is_refused_where_it_is_written(string path, string reason)
    {
        var refused = Refused(Head + $"""
            dimensions:
              - name: D
                kind: "*:*:*:*"
                path: {path}
            """);
        Assert.Contains("dimensions[0] 'D'", refused.Message, StringComparison.Ordinal);
        Assert.Contains(reason, refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dimensions: []", "at least one dimension")]
    [InlineData("dimensions:\n  - name: 'not a name'\n    kind: 'a:b:c:1.0.0'\n    path: kind", "is not a dimension name")]
    [InlineData("dimensions:\n  - name: D\n    kind: 'not-a-kind'\n    path: kind", "is not authority:source:entityType:version")]
    [InlineData("dimensions:\n  - name: D\n    kind: 'a:b:c:1.0.0'", "path")]
    [InlineData("dimensions:\n  - name: D\n    kind: 'a:b:c:1.0.0'\n    path: kind\n    query: 'data.X:\"{undeclared}\"'", "'{undeclared}'")]
    [InlineData("dimensions:\n  - name: D\n    kind: 'a:b:c:1.0.0'\n    path: kind\n  - name: d\n    kind: 'a:b:c:1.0.0'\n    path: id", "as an earlier dimension is")]
    [InlineData("dimensions:\n  - name: D\n    kind: 'a:b:c:1.0.0'\n    path: kind\n    maxValues: 0", "maxValues is 0")]
    [InlineData("dimensions:\n  - name: D\n    kind: 'a:b:c:1.0.0'\n    path: kind\n    maxValues: 5000001", "maxValues is 5000001")]
    [InlineData("dimensions:\n  - name: D\n    kind: 'a:b:c:1.0.0'\n    path: kind\n    partitions: [prod]", "'prod' is not a partition of the flow")]
    [InlineData("dimensions:\n  - name: D\n    kind: 'a:b:c:1.0.0'\n    path: kind\n    colour: red", "colour")]
    public void A_dimension_that_breaks_a_rule_is_refused_naming_the_rule(string dimensions, string reason)
        => Assert.Contains(reason, Refused(Head + dimensions).Message, StringComparison.Ordinal);

    [Fact]
    public void More_dimensions_than_one_flow_holds_are_refused()
    {
        var many = string.Join("\n", Enumerable.Range(0, DimensionFlowDefinition.MaxDimensions + 1).Select(i => $"  - {{ name: D{i}, kind: 'a:b:c:1.0.0', path: kind }}"));
        Assert.Contains("at most 100", Refused(Head + "dimensions:\n" + many).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("- sparkle", "'sparkle' is not a clean step")]
    [InlineData("- replace", "replace takes settings")]
    [InlineData("- map", "map takes settings")]
    [InlineData("- { trim: ~ }", "trim takes no settings")]
    [InlineData("- { upper: ~, lower: ~ }", "holds 2 keys")]
    [InlineData("- replace: { with: x }", "replace needs a pattern")]
    [InlineData("- replace: { pattern: a }", "replace needs with")]
    [InlineData("- replace: { pattern: a, with: b, flags: i }", "no 'flags' setting")]
    [InlineData("- replace: { pattern: '(a)\\1', with: b }", "non-backtracking")]
    [InlineData("- replace: { pattern: '(unclosed', with: b }", "cannot be used")]
    [InlineData("- map: { field: family }", "map needs a dictionary")]
    [InlineData("- map: '../Other'", "map needs a dictionary")]
    [InlineData("- map: { dictionary: Aliases, field: 'not a field' }", "is not a dictionary field name")]
    [InlineData("- map: { dictionary: Aliases, otherwise: '' }", "otherwise is empty text")]
    [InlineData("- map: { dictionary: Aliases, colour: red }", "no 'colour' setting")]
    [InlineData("- map: [Aliases]", "map takes a dictionary's name")]
    [InlineData("- ~", "is empty")]
    public void A_clean_step_that_breaks_a_rule_is_refused_naming_the_step(string step, string reason)
    {
        var refused = Refused(Head + Curves + "\n    clean:\n      " + step);
        Assert.Contains("clean[0]", refused.Message, StringComparison.Ordinal);
        Assert.Contains(reason, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void More_clean_steps_than_a_dimension_applies_are_refused()
        => Assert.Contains(
            "at most 20",
            Refused(Head + Curves + "\n    clean: [" + string.Join(", ", Enumerable.Repeat("trim", DimensionSpec.MaxCleanSteps + 1)) + "]").Message,
            StringComparison.Ordinal);

    [Theory]
    [InlineData(9)]
    [InlineData(10_001)]
    public void An_aggregation_size_no_platform_uses_is_refused(int size)
        => Assert.Contains("AGGREGATION_SIZE", Refused(Head.Replace("source:", $"source:\n  aggregationSize: {size}", StringComparison.Ordinal) + Curves).Message, StringComparison.Ordinal);

    [Fact]
    public void An_aggregation_size_the_platform_was_set_to_is_taken()
        => Assert.Equal(500, Parse(Head.Replace("source:", "source:\n  aggregationSize: 500", StringComparison.Ordinal) + Curves).Source.AggregationSize);

    [Fact]
    public void A_flow_naming_its_partitions_and_a_partition_header_is_refused()
        => Assert.Contains(
            "Remove the header",
            Refused(Head.Replace("secretRef: ${env:OSDU_TOKEN}", "secretRef: ${env:OSDU_TOKEN}\n  headers:\n    data-partition-id: dev", StringComparison.Ordinal) + Curves).Message,
            StringComparison.Ordinal);

    [Fact]
    public void A_parameter_named_partition_is_refused_because_the_run_names_the_partition()
        => Assert.Contains("parameters declares 'partition'", Refused(Head + "parameters:\n  partition: { default: x }\n" + Curves).Message, StringComparison.Ordinal);

    [Fact]
    public void An_unknown_key_is_refused_rather_than_ignored()
        => Assert.Contains("sourcee", Refused(Head + Curves + "\nsourcee: {}").Message, StringComparison.Ordinal);

    [Fact]
    public void A_run_builds_in_the_partition_it_names_and_one_partition_only()
    {
        var flow = Parse(Head + Curves);
        var registry = RegisteredPartitions.None;

        Assert.Equal("test", flow.ForRun("test", registry).Partition);
        Assert.Equal("dev", flow.ForRun("test", registry).ForPartition("dev").Partition);
        Assert.Contains("one partition per run", Assert.Throws<DeliveryException>(() => flow.ForRun(PartitionNames.Every, registry)).Message, StringComparison.Ordinal);
        Assert.Contains("does not build in partition 'prod'", Assert.Throws<DeliveryException>(() => flow.ForRun("prod", registry)).Message, StringComparison.Ordinal);
        Assert.NotEqual(flow.ForPartition("dev").LedgerId, flow.ForPartition("test").LedgerId);
        Assert.Equal("wells-dimensions@dev", flow.ForPartition("dev").LedgerName);
        Assert.Equal("dev", flow.ForPartition("dev").Source.Headers["data-partition-id"]);
    }

    [Fact]
    public void A_run_selects_dimensions_by_name_and_refuses_one_the_flow_does_not_have()
    {
        var flow = Parse(Head + Curves);
        Assert.Equal(["CurveMnemonic"], flow.Select(["curvemnemonic"]).Select(d => d.Name));
        Assert.Single(flow.Select([]));
        Assert.Contains("'Nope'", Assert.Throws<DeliveryException>(() => flow.Select(["Nope"])).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_payload_carries_the_dimensions_a_run_builds_and_no_other_kind_takes_them()
    {
        var payload = DeliveryRunPayload.Parse("""{ "dimensions": ["CurveMnemonic", "LegalTag"] }""");
        Assert.Equal(["CurveMnemonic", "LegalTag"], payload.Dimensions);
        Assert.Equal("""{"dimensions":["CurveMnemonic","LegalTag"]}""", payload.ToJson());
        Assert.False(payload.CarriesOnlyConfiguration);

        Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse("""{ "dimensions": ["not a name"] }"""));
        Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse("""{ "dimensions": ["A", "a"] }"""));
        Assert.Contains("only a dimension flow", Assert.Throws<SqlFlowException>(() => payload.Validate(DeliveryOperations.Deliver)).Message, StringComparison.Ordinal);

        var kind = new DimensionFlowKind(new DeliveryDocumentLoader());
        kind.ValidateParameters(new RunParameters { Payload = payload.ToJson() });
        Assert.Throws<SqlFlowException>(() => kind.ValidateParameters(new RunParameters { Payload = """{ "tests": ["t"] }""" }));
        Assert.Throws<SqlFlowException>(() => new AssertionFlowKind(new DeliveryDocumentLoader()).ValidateParameters(new RunParameters { Payload = payload.ToJson() }));
    }

    [Theory]
    [InlineData(null, "build")]
    [InlineData("build", "build")]
    [InlineData("plan", "plan")]
    public void A_dimension_flow_builds_by_default_and_plans_when_asked(string? operation, string expected)
        => Assert.Equal(expected, DimensionFlowKind.Operation(new RunParameters { Operation = operation }));

    [Fact]
    public void A_dimension_flow_runs_no_other_operation()
        => Assert.Throws<SqlFlowException>(() => DimensionFlowKind.Operation(new RunParameters { Operation = "deliver" }));

    [Fact]
    public void Lineage_reads_each_dimensions_kind_and_writes_each_dimension_in_each_partition()
    {
        var flow = Parse(Head + Curves);

        var lineage = DimensionLineage.Describe(flow);

        var reads = lineage.Datasets.Where(d => d.Relation == LineageRelation.Reads).Select(d => $"{d.System}/{d.Namespace}/{d.Name}").ToList();
        var writes = lineage.Datasets.Where(d => d.Relation == LineageRelation.Writes).Select(d => $"{d.System}/{d.Namespace}/{d.Group}/{d.Name}").ToList();
        Assert.Equal(
            ["osdu-type/dev/osdu:wks:work-product-component--WellLog:*", "osdu-type/test/osdu:wks:work-product-component--WellLog:*"],
            reads);
        Assert.Equal(["osdu-dimension/dev/wells-dimensions/CurveMnemonic", "osdu-dimension/test/wells-dimensions/CurveMnemonic"], writes);
        Assert.Empty(lineage.Warnings);
    }

    [Fact]
    public void The_flow_needs_the_repositorys_tree_only_when_it_cleans_through_a_dictionary()
    {
        Assert.False(new DimensionFlowDocument { Flow = Parse(Head + Curves) }.RequiresRepoTree);
        Assert.True(new DimensionFlowDocument { Flow = Parse(Head + Curves + "\n    clean: [{ map: Aliases }]") }.RequiresRepoTree);
    }

    [Fact]
    public void Its_secrets_are_references_the_hygiene_check_inspects()
    {
        var references = new DimensionFlowDocument { Flow = Parse(Head + Curves) }.CredentialReferences.ToList();
        Assert.Contains(new KeyValuePair<string, string>("source.endpoint", "${env:OSDU_URL}"), references);
        Assert.Contains(new KeyValuePair<string, string>("source.auth.secretRef", "${env:OSDU_TOKEN}"), references);
    }
}
