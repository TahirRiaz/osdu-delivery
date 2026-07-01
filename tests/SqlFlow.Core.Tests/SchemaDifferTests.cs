using SqlFlow.Core;
using SqlFlow.Core.Engine;
using SqlFlow.Core.Model;
using Xunit;

namespace SqlFlow.Tests;

public sealed class SchemaDifferTests
{
    private static TableSchema Desired(params string[] names) => new()
    {
        Schema = "dbo",
        Table = "T",
        Columns = names.Select(n => new ColumnDefinition { Name = n, SqlType = "INT" }).ToList(),
    };

    private static TableSchema Existing(params string[] names) => new()
    {
        Schema = "dbo",
        Table = "T",
        Columns = names.Select(n => new ColumnDefinition { Name = n, SqlType = "INT" }).ToList(),
    };

    [Fact]
    public void Diff_NoTable_CreatesTable()
    {
        var delta = SchemaDiffer.Diff(Desired("A", "B"), actual: null, SchemaEvolution.Widen);

        Assert.True(delta.CreateTable);
        Assert.Equal(2, delta.ColumnsToAdd.Count);
    }

    [Fact]
    public void Diff_Widen_AddsOnlyMissingColumns()
    {
        var delta = SchemaDiffer.Diff(Desired("A", "B"), Existing("A"), SchemaEvolution.Widen);

        Assert.False(delta.CreateTable);
        Assert.Single(delta.ColumnsToAdd);
        Assert.Equal("B", delta.ColumnsToAdd[0].Name);
    }

    [Fact]
    public void Diff_Strict_ThrowsOnDrift()
        => Assert.Throws<SchemaDriftException>(
            () => SchemaDiffer.Diff(Desired("A", "B"), Existing("A"), SchemaEvolution.Strict));

    [Fact]
    public void Diff_Create_DoesNotAlterExistingTable()
    {
        var delta = SchemaDiffer.Diff(Desired("A", "B"), Existing("A"), SchemaEvolution.Create);

        Assert.False(delta.HasChanges);
    }
}
