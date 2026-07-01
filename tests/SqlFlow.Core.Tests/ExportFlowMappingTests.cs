using SqlFlow.Core;
using SqlFlow.Core.Export.Legacy;
using Xunit;

namespace SqlFlow.Tests;

public sealed class ExportFlowMappingTests
{
    private static LegacyExportRow Row() => new()
    {
        FlowID = 4,
        SysAlias = "sys",
        srcServer = "src",
        srcDBSchTbl = "[DB].[dbo].[Orders]",
    };

    [Fact]
    public void Maps_RequiredFields_AndDefaults()
    {
        var flow = ExportFlowMapper.FromLegacy(Row());

        Assert.Equal(4, flow.FlowId);
        Assert.Equal("src", flow.SrcServer);
        Assert.Equal("@src", flow.ConnectionReference);
        Assert.Equal("Orders", flow.Source.Name);
        Assert.Equal("D", flow.ExportBy);
        Assert.Equal(1, flow.ExportSize);
        Assert.Equal("csv", flow.TrgFiletype);
        Assert.Equal(";", flow.ColumnDelimiter);
        Assert.Equal("\"", flow.TextQualifier);
        Assert.True(flow.AddTimeStampToFileName);
        Assert.Null(flow.TargetReference);
    }

    [Fact]
    public void ServicePrincipalAlias_CollapsesToTargetReference()
    {
        var row = Row();
        row.ServicePrincipalAlias = "adls1";
        Assert.Equal("@adls1", ExportFlowMapper.FromLegacy(row).TargetReference);
    }

    [Fact]
    public void MissingSource_Throws()
    {
        var row = Row();
        row.srcDBSchTbl = null;
        Assert.Throws<SqlFlowException>(() => ExportFlowMapper.FromLegacy(row));
    }

    [Fact]
    public void HonorsConfiguredCsvOptions()
    {
        var row = Row();
        row.ColumnDelimiter = "|";
        row.TextQualifier = "'";
        row.trgEncoding = "Unicode";
        row.trgFiletype = "parquet";
        var flow = ExportFlowMapper.FromLegacy(row);
        Assert.Equal("|", flow.ColumnDelimiter);
        Assert.Equal("'", flow.TextQualifier);
        Assert.Equal("Unicode", flow.TrgEncoding);
        Assert.Equal("parquet", flow.TrgFiletype);
    }
}
