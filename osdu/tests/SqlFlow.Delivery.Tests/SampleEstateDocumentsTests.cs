using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The estate in <c>osdu/samples/wells</c> as an operator runs it. Every delivery document there is read the
/// way a run reads it, and each flow is checked against the mapping it names: a mapping repeats the rows of a child
/// dataset, and a flow that does not declare that dataset cannot plan a single record. The suites that exercise the
/// estate build their own tables, so only a check of the committed documents catches a flow and a mapping that have
/// drifted apart.
/// </summary>
public sealed class SampleEstateDocumentsTests
{
    private readonly DeliveryDocumentLoader _loader = new();

    /// <summary>Every delivery document of the sample estate, by file name.</summary>
    public static TheoryData<string> Documents()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Samples.Source, "flows"), "*.yaml").OrderBy(f => f, StringComparer.Ordinal))
        {
            if (File.ReadAllText(file).Contains("flowType: delivery", StringComparison.Ordinal))
            {
                data.Add(Path.GetFileName(file));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void Every_interface_declares_the_child_datasets_its_mapping_repeats(string document)
    {
        var path = Path.Combine(Samples.Source, "flows", document);
        var source = _loader.ParseSource(File.ReadAllText(path), document);

        foreach (var flow in source.Interfaces)
        {
            var mapping = Mapping(flow);
            var declared = flow.Source.Datasets.Keys.ToList();
            var missing = mapping.ChildDatasets.Where(child => !declared.Contains(child, StringComparer.OrdinalIgnoreCase)).ToList();

            Assert.True(
                missing.Count == 0,
                $"{document} ({flow.Label}) names mapping {flow.Render.Mapping}, which repeats the child dataset(s) "
                + $"{string.Join(", ", missing)}; the flow declares {(declared.Count == 0 ? "none" : string.Join(", ", declared))}. "
                + "A run of it holds every record with a preflight error.");
        }
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void Every_interface_reads_only_columns_of_a_dataset_it_declares(string document)
    {
        var path = Path.Combine(Samples.Source, "flows", document);
        var source = _loader.ParseSource(File.ReadAllText(path), document);

        foreach (var flow in source.Interfaces)
        {
            var mapping = Mapping(flow);
            var declared = flow.Source.Datasets.Keys.ToList();
            var read = mapping.Entries
                .SelectMany(entry => entry.Columns)
                .Select(column => column.Child)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(child => !declared.Contains(child, StringComparer.OrdinalIgnoreCase))
                .ToList();

            Assert.True(
                read.Count == 0,
                $"{document} ({flow.Label}) names mapping {flow.Render.Mapping}, which reads column(s) of the child dataset(s) "
                + $"{string.Join(", ", read)}; the flow declares {(declared.Count == 0 ? "none" : string.Join(", ", declared))}.");
        }
    }

    private MappingDefinition Mapping(FlowDefinition flow)
    {
        var file = Path.Combine(Samples.Mappings, flow.Render.Mapping + ".yaml");
        Assert.True(File.Exists(file), $"{flow.Label} names mapping {flow.Render.Mapping}, and {file} does not exist.");
        return _loader.ParseMapping(File.ReadAllText(file), Path.GetFileName(file));
    }
}
