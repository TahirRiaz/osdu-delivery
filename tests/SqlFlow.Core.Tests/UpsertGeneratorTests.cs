using SqlFlow.Core;
using SqlFlow.Core.Ingestion;
using SqlFlow.SqlServer.Schema;
using Xunit;

namespace SqlFlow.Tests;

public sealed class UpsertGeneratorTests
{
    private static RelationalObject Obj(string name) => new() { Database = "db", Schema = "dbo", Name = name };

    private static UpsertOptions Options(IReadOnlyList<string> data, IReadOnlyList<string> keys, bool skipUpdate = false, bool skipInsert = false)
        => new() { DataColumns = data, KeyColumns = keys, SkipUpdate = skipUpdate, SkipInsert = skipInsert };

    [Fact]
    public void Generate_ProducesUpdateThenInsert()
    {
        var statements = UpsertGenerator.Generate(Obj("Trg"), Obj("Stg"), Options(["Id", "Name"], ["Id"]));
        Assert.Equal(2, statements.Count);
        Assert.StartsWith("UPDATE", statements[0], StringComparison.Ordinal);
        Assert.Contains("HASHBYTES", statements[0], StringComparison.Ordinal);
        Assert.StartsWith("INSERT INTO", statements[1], StringComparison.Ordinal);
        Assert.Contains("NOT EXISTS", statements[1], StringComparison.Ordinal);
    }

    [Fact]
    public void SkipUpdate_OmitsUpdate()
    {
        var statements = UpsertGenerator.Generate(Obj("Trg"), Obj("Stg"), Options(["Id", "Name"], ["Id"], skipUpdate: true));
        Assert.StartsWith("INSERT INTO", Assert.Single(statements), StringComparison.Ordinal);
    }

    [Fact]
    public void SkipInsert_OmitsInsert()
    {
        var statements = UpsertGenerator.Generate(Obj("Trg"), Obj("Stg"), Options(["Id", "Name"], ["Id"], skipInsert: true));
        Assert.StartsWith("UPDATE", Assert.Single(statements), StringComparison.Ordinal);
    }

    [Fact]
    public void AllKeyColumns_ProduceOnlyInsert()
    {
        // No non-key columns means there is nothing to update; only the INSERT is emitted.
        var statements = UpsertGenerator.Generate(Obj("Trg"), Obj("Stg"), Options(["Id"], ["Id"]));
        Assert.StartsWith("INSERT INTO", Assert.Single(statements), StringComparison.Ordinal);
    }

    [Fact]
    public void NoKeys_Throws()
        => Assert.Throws<SqlFlowException>(() => UpsertGenerator.Generate(Obj("Trg"), Obj("Stg"), Options(["Id"], [])));

    [Fact]
    public void KeyNotInDataColumns_Throws()
        => Assert.Throws<SqlFlowException>(() => UpsertGenerator.Generate(Obj("Trg"), Obj("Stg"), Options(["Name"], ["Id"])));

    [Fact]
    public void UnknownHashAlgorithm_Throws()
        => Assert.Throws<SqlFlowException>(() => UpsertGenerator.Generate(Obj("Trg"), Obj("Stg"),
            new UpsertOptions { DataColumns = ["Id", "Name"], KeyColumns = ["Id"], HashAlgorithm = "CRC32" }));

    [Fact]
    public void SystemDateColumns_AreStamped()
    {
        var options = new UpsertOptions
        {
            DataColumns = ["Id", "Name"],
            KeyColumns = ["Id"],
            InsertedDateColumn = "InsertedDate_DW",
            UpdatedDateColumn = "UpdatedDate_DW",
        };
        var statements = UpsertGenerator.Generate(Obj("Trg"), Obj("Stg"), options);
        Assert.Contains("[UpdatedDate_DW] = SYSUTCDATETIME()", statements[0], StringComparison.Ordinal);
        Assert.Contains("[InsertedDate_DW]", statements[1], StringComparison.Ordinal);
    }
}
