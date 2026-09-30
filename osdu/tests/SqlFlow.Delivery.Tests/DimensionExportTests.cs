using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Ledger;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// How a dimension export writes its cells and names its file (docs/dimension-plan.md, Stage 5): CSV quoted as RFC 4180 asks,
/// a formula a spreadsheet would run made text while a plain number stays a number, and every query parameter it takes read
/// strictly.
/// </summary>
public class DimensionExportTests
{
    [Theory]
    [InlineData("GR", "GR")]
    [InlineData("Gamma, Ray", "\"Gamma, Ray\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    [InlineData(" padded ", "\" padded \"")]
    [InlineData("", "")]
    public void A_cell_is_quoted_only_when_its_text_needs_it(string cell, string written)
        => Assert.Equal(written, DimensionExport.Csv(cell));

    [Theory]
    [InlineData("=SUM(A1:A9)", "'=SUM(A1:A9)")]
    [InlineData("+cmd", "'+cmd")]
    [InlineData("@user", "'@user")]
    [InlineData("-lookup()", "'-lookup()")]
    [InlineData("-999.25", "-999.25")]
    [InlineData("+1e5", "+1e5")]
    [InlineData("GR", "GR")]
    public void A_cell_a_spreadsheet_would_run_as_a_formula_is_made_text_and_a_number_is_left_as_it_is(string cell, string written)
        => Assert.Equal(written, DimensionExport.Defused(cell));

    [Fact]
    public void A_null_cell_is_empty_and_cells_are_joined_by_commas()
        => Assert.Equal("1,,x", DimensionExport.Csv("1", null, "x"));

    [Theory]
    [InlineData(null, DimensionExportSet.Members)]
    [InlineData("members", DimensionExportSet.Members)]
    [InlineData("Originals", DimensionExportSet.Originals)]
    [InlineData("values", DimensionExportSet.Originals)]
    public void The_set_is_read_from_its_name(string? name, DimensionExportSet set) => Assert.Equal(set, DimensionExport.SetOf(name));

    [Theory]
    [InlineData(null, DimensionExportFormat.Csv)]
    [InlineData("CSV", DimensionExportFormat.Csv)]
    [InlineData("jsonl", DimensionExportFormat.JsonLines)]
    [InlineData("ndjson", DimensionExportFormat.JsonLines)]
    public void The_format_is_read_from_its_name(string? name, DimensionExportFormat format) => Assert.Equal(format, DimensionExport.FormatOf(name));

    [Fact]
    public void An_unknown_set_or_format_is_refused_rather_than_guessed()
    {
        Assert.Null(DimensionExport.SetOf("everything"));
        Assert.Null(DimensionExport.FormatOf("xlsx"));
    }

    [Fact]
    public void The_file_is_named_for_the_flow_partition_and_dimension_with_nothing_a_file_system_refuses()
    {
        var dimension = new DimensionState
        {
            DimensionId = 1, FlowId = Guid.NewGuid(), FlowName = "wells/dims", Partition = "dev", Name = "Curve Mnemonic", Kind = "k", Path = "data.x",
            CleanJson = "[]", DefinitionHash = "0123456789abcdef",
        };

        Assert.Equal("wells-dims-dev-Curve-Mnemonic-originals.jsonl", DimensionExport.FileName(dimension, DimensionExportSet.Originals, DimensionExportFormat.JsonLines));
        Assert.Equal("wells-dims-dev-Curve-Mnemonic-members.csv", DimensionExport.FileName(dimension, DimensionExportSet.Members, DimensionExportFormat.Csv));
    }
}
