using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// <c>sqlflow fixtures update</c>: each fixture's expected record written from what it renders, only those lines changed,
/// and a fixture whose render would not be delivered, or whose expected value cannot be edited in place, left and named.
/// </summary>
public sealed class FixtureUpdateTests
{
    private static string Record(string key, string depth) =>
        $$$"""{"id":"dev:work-product-component--Thing:{{{key}}}","kind":"test:wks:work-product-component--Thing:1.0.0","acl":{"owners":["owners@x"],"viewers":["viewers@x"]},"legal":{"legaltags":["tag"],"otherRelevantDataCountries":["NO"]},"data":{"Name":"w","Depth":{{{depth}}}}}""";

    [Fact]
    public void Only_the_expected_blocks_that_differ_are_written_and_the_rest_of_the_document_stays_as_it_was()
    {
        var key = DeliveryKey.Derive("test", ["w"]).Value.ToString("N");
        var fixtures = $$$"""
            fixtures:
              # The first expects an old depth, so it is written again.
              - name: stale
                row: { name: w, depth: "1" }
                expected: |
                  {{{Record(key, "2")}}}
              - name: current
                row: { name: w, depth: "1" }
                expected: |
                  {{{Record(key, "1")}}}

              - { name: flow, row: { name: w, depth: "1" }, expected: "{}" }
              - name: held
                row: { name: w }
                expected: "{}"
              - name: inline
                row: { name: w, depth: "1" }
                expected: "{}"   # written again as a block
            """;
        var yaml = TestSchema.MappingDocument(fixtures: fixtures).ReplaceLineEndings("\n");
        var mapping = new DeliveryDocumentLoader().ParseMapping(yaml, "thing.yaml");
        var renderer = new MappingRenderer(mapping, TestSchema.Build(), TestSchema.References(), TestSchema.Context());

        var rewrite = FixtureRewriter.Rewrite(yaml, mapping, Preflight.RenderFixtures(mapping, renderer), "thing.yaml");
        Assert.Equal(
            ["stale:Updated", "current:Unchanged", "flow:Skipped", "held:Skipped", "inline:Updated"],
            rewrite.Outcomes.Select(o => $"{o.Name}:{o.Kind}"));
        Assert.Contains("written as a flow mapping", rewrite.Outcomes[2].Reason, StringComparison.Ordinal);
        Assert.Contains("renders a record that is held (osdu.data.Depth: dataset.depth is empty, and the entry is required", rewrite.Outcomes[3].Reason, StringComparison.Ordinal);

        var expected = """
            expected: |
              {
                "id": "dev:work-product-component--Thing:KEY",
                "kind": "test:wks:work-product-component--Thing:1.0.0",
                "acl": { "owners": ["owners@x"], "viewers": ["viewers@x"] },
                "legal": { "legaltags": ["tag"], "otherRelevantDataCountries": ["NO"] },
                "data": { "Name": "w", "Depth": 1 }
              }
            """.Replace("KEY", key, StringComparison.Ordinal);
        var written = rewrite.Text;
        Assert.Contains(Indented(expected, 4) + "  - name: current", written, StringComparison.Ordinal);
        Assert.Contains("    expected: |\n      " + Record(key, "1") + "\n\n  - { name: flow", written, StringComparison.Ordinal);
        Assert.Contains("  # The first expects an old depth, so it is written again.\n", written, StringComparison.Ordinal);
        Assert.EndsWith(Indented(expected, 4), written.TrimEnd('\n') + "\n", StringComparison.Ordinal);

        // Everything outside the fixtures is the document as it was.
        var head = yaml[..yaml.IndexOf("fixtures:", StringComparison.Ordinal)];
        Assert.StartsWith(head, written, StringComparison.Ordinal);

        // What was written reads back, and the fixtures it wrote now pass the gate's comparison.
        var reread = new DeliveryDocumentLoader().ParseMapping(written, "thing.yaml");
        var again = FixtureRewriter.Rewrite(written, reread, Preflight.RenderFixtures(reread, new MappingRenderer(reread, TestSchema.Build(), TestSchema.References(), TestSchema.Context())), "thing.yaml");
        Assert.Equal(
            ["stale:Unchanged", "current:Unchanged", "flow:Skipped", "held:Skipped", "inline:Unchanged"],
            again.Outcomes.Select(o => $"{o.Name}:{o.Kind}"));
        Assert.Equal(written, again.Text);
    }

    [Fact]
    public async Task Updating_the_sample_mapping_writes_the_fixture_that_changed_and_the_gate_then_passes()
    {
        const string LogRun = "\"LogRun\": \"C\",";
        var root = Samples.NewTempDirectory();
        try
        {
            var mappings = Path.Combine(root, "mappings");
            Directory.CreateDirectory(mappings);
            var file = Path.Combine(mappings, "WellLog@1.4.0.yaml");
            var original = await File.ReadAllTextAsync(Path.Combine(Samples.Mappings, "WellLog@1.4.0.yaml"));

            // Only the first fixture's record is made stale; the second expects the same run and stays as written.
            var at = original.IndexOf(LogRun, StringComparison.Ordinal);
            await File.WriteAllTextAsync(file, original[..at] + "\"LogRun\": \"stale\"," + original[(at + LogRun.Length)..]);

            var flow = Samples.LocalFlow(root);
            flow = flow with { Render = flow.Render with { MappingsDirectory = mappings } };
            var engine = Samples.Engine(ledger: null);

            var dry = await FixtureUpdates.UpdateAsync(engine, flow, write: false);
            Assert.False(dry.Written);
            Assert.Equal([FixtureOutcomeKind.Updated, FixtureOutcomeKind.Unchanged], dry.Rewrite.Outcomes.Select(o => o.Kind));
            Assert.Contains("\"LogRun\": \"stale\"", await File.ReadAllTextAsync(file), StringComparison.Ordinal);

            var update = await FixtureUpdates.UpdateAsync(engine, flow, write: true);
            Assert.True(update.Written);
            Assert.Equal(file, update.Path);
            var text = await File.ReadAllTextAsync(file);
            Assert.DoesNotContain("\"LogRun\": \"stale\"", text, StringComparison.Ordinal);
            Assert.Equal(2, text.Split(LogRun).Length - 1);

            // The second fixture and everything before the fixtures are exactly as they were.
            Assert.StartsWith(original[..original.IndexOf("fixtures:", StringComparison.Ordinal)], text, StringComparison.Ordinal);
            Assert.EndsWith(original[original.IndexOf("  - name: 22494/1", StringComparison.Ordinal)..], text, StringComparison.Ordinal);
            Assert.Equal([file], Directory.GetFiles(mappings));

            // The gate, fixtures included, now passes, and a second update finds nothing to write.
            var resolved = await new RenderResolver(new MappingCatalog(mappings, engine.Documents), engine.Cache, engine.Templates, engine.Secrets).ResolveAsync(flow);
            Assert.Equal("WellLog@1.4.0", resolved.Mapping.Reference);
            var second = await FixtureUpdates.UpdateAsync(engine, flow, write: true);
            Assert.False(second.Written);
            Assert.All(second.Rewrite.Outcomes, o => Assert.Equal(FixtureOutcomeKind.Unchanged, o.Kind));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Indented(string text, int by)
        => string.Concat(text.ReplaceLineEndings("\n").Split('\n').Select(line => (line.Length == 0 ? string.Empty : new string(' ', by) + line) + "\n"));
}
