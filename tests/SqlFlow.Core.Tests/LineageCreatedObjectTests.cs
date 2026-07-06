using SqlFlow.Core.Lineage;
using SqlFlow.Lineage.Extraction;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The created-object capture specification: for every CREATE TABLE/VIEW (and CTAS / SELECT INTO) the
/// extractor records the verbatim generating DDL and, for a plain table, its column definitions, so the
/// catalog can attach an object's script and an offline column dictionary without a live connection. Temp
/// objects are script-local and never captured.
/// </summary>
public sealed class LineageCreatedObjectTests
{
    private static ScriptDependencies Extract(string sql, string? defaultDatabase = null)
        => TSqlLineageExtractor.Extract(sql, "test", defaultDatabase);

    [Fact]
    public void CreateTable_CapturesDdlAndColumns()
    {
        var deps = Extract("""
            CREATE TABLE DW.dbo.Customer (
                Id INT NOT NULL,
                Name NVARCHAR(100) NULL,
                Balance DECIMAL(18,2) NOT NULL
            );
            """);

        var created = Assert.Single(deps.CreatedObjects.Values);
        Assert.Equal("dw.dbo.customer", created.Table.Key);
        Assert.Equal(LineageNodeKind.Table, created.Kind);
        Assert.Contains("CREATE TABLE", created.Ddl, StringComparison.Ordinal);

        Assert.Collection(created.Columns,
            c => { Assert.Equal("Id", c.Name); Assert.Equal("INT", c.DataType); Assert.False(c.Nullable); },
            c => { Assert.Equal("Name", c.Name); Assert.Equal("NVARCHAR(100)", c.DataType); Assert.True(c.Nullable); },
            c => { Assert.Equal("Balance", c.Name); Assert.Equal("DECIMAL(18,2)", c.DataType); Assert.False(c.Nullable); });
    }

    [Fact]
    public void CreateView_CapturesDdl_WithoutTypedColumns()
    {
        var deps = Extract("CREATE VIEW dbo.vX AS SELECT c.Id FROM DW.dbo.Customers c;");

        var created = Assert.Single(deps.CreatedObjects.Values);
        Assert.Equal(LineageNodeKind.View, created.Kind);
        Assert.Contains("CREATE VIEW", created.Ddl, StringComparison.Ordinal);
        Assert.Empty(created.Columns);
    }

    [Fact]
    public void SelectInto_CapturesTheCreatedTable()
    {
        var deps = Extract("SELECT c.Id INTO DW.dbo.Snapshot FROM DW.dbo.Customers c;");

        var created = Assert.Single(deps.CreatedObjects.Values);
        Assert.Equal("dw.dbo.snapshot", created.Table.Key);
        Assert.Equal(LineageNodeKind.Table, created.Kind);
    }

    [Fact]
    public void TempTable_IsNotCaptured()
    {
        var deps = Extract("CREATE TABLE #Staging (Id INT NOT NULL);");

        Assert.Empty(deps.CreatedObjects);
    }
}
