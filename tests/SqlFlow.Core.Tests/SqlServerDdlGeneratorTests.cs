using SqlFlow.Core.Model;
using SqlFlow.SqlServer;
using Xunit;

namespace SqlFlow.Tests;

public sealed class SqlServerDdlGeneratorTests
{
    private static readonly SqlServerDdlGenerator Generator = new();
    private static readonly TargetSpec Target = new() { Connection = "x", Schema = "dbo", Table = "Orders" };

    [Fact]
    public void Generate_CreateTable_EmitsCreateWithNullability()
    {
        var delta = new SchemaDelta
        {
            CreateTable = true,
            ColumnsToAdd =
            [
                new ColumnDefinition { Name = "OrderId", SqlType = "BIGINT", IsNullable = false },
                new ColumnDefinition { Name = "Amount", SqlType = "DECIMAL(38, 6)", IsNullable = true },
            ],
        };

        var sql = Generator.Generate(Target, delta);

        Assert.Single(sql);
        Assert.Contains("CREATE TABLE [dbo].[Orders]", sql[0], StringComparison.Ordinal);
        Assert.Contains("[OrderId] BIGINT NOT NULL", sql[0], StringComparison.Ordinal);
        Assert.Contains("[Amount] DECIMAL(38, 6) NULL", sql[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_AddColumns_ForcesNullable()
    {
        var delta = new SchemaDelta
        {
            ColumnsToAdd = [new ColumnDefinition { Name = "NewCol", SqlType = "INT", IsNullable = false }],
        };

        var sql = Generator.Generate(Target, delta);

        Assert.Single(sql);
        Assert.Equal("ALTER TABLE [dbo].[Orders] ADD [NewCol] INT NULL;", sql[0]);
    }

    [Fact]
    public void Generate_NoChanges_EmitsNothing()
        => Assert.Empty(Generator.Generate(Target, new SchemaDelta()));
}
