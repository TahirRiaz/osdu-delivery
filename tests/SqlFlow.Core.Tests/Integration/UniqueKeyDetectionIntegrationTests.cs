using SqlFlow.Core.Profiling;
using SqlFlow.SqlServer.Profiling;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Exercises the SQL Server uniqueness probe and the detector end to end against a real table with a planted
/// composite key, proving the T-SQL measurements (distinct-tuple counts, null handling, the sample temp table) and
/// the algorithm agree on real data. Skips when no sink database is configured. Each test drops its table up front.
/// </summary>
[Trait("Category", "Integration")]
public sealed class UniqueKeyDetectionIntegrationTests
{
    private const string Schema = IntegrationDb.Schema;

    [SkippableFact]
    public async Task PlantedCompositeKey_IsDetectedAndMinimal()
    {
        var cs = IntegrationDb.Require();
        const string table = "UkDetect_Composite";
        await IntegrationDb.DropTableAsync(cs, table);
        // (Region, OrderNo) is the only minimal key: Amount and Note are constants, so they cannot substitute for
        // either key column, and neither Region nor OrderNo is unique on its own.
        await IntegrationDb.ExecuteAsync(cs, $"""
            CREATE TABLE [{Schema}].[{table}] (Region nvarchar(10) NOT NULL, OrderNo int NOT NULL, Amount decimal(10,2) NULL, Note nvarchar(50) NULL);
            INSERT INTO [{Schema}].[{table}] (Region, OrderNo, Amount, Note) VALUES
                (N'east', 1, 10.00, N'x'), (N'east', 2, 10.00, N'x'), (N'west', 1, 10.00, N'x'),
                (N'west', 2, 10.00, N'x'), (N'north', 1, 10.00, N'x'), (N'north', 2, 10.00, N'x');
            """);

        var columns = new[] { "Region", "OrderNo", "Amount", "Note" };
        await using var probe = await SqlServerUniquenessProbe.CreateAsync(cs, $"[{Schema}].[{table}]", columns, sampleSize: 0);
        var report = await UniqueKeyDetector.DetectAsync(probe, columns, new UniqueKeyOptions());

        var key = Assert.Single(report.Candidates, c => c.IsUnique);
        Assert.True(key.Verified);
        Assert.Equal(new[] { "OrderNo", "Region" }, key.Columns.OrderBy(c => c, StringComparer.Ordinal).ToArray());
        Assert.Equal(6, report.TotalRows);
    }

    [SkippableFact]
    public async Task SingleColumnKey_IsDetected_AndNullableColumnIsRejected()
    {
        var cs = IntegrationDb.Require();
        const string table = "UkDetect_Single";
        await IntegrationDb.DropTableAsync(cs, table);
        await IntegrationDb.ExecuteAsync(cs, $"""
            CREATE TABLE [{Schema}].[{table}] (Id int NOT NULL, Alt int NULL, Grp nvarchar(5) NULL);
            INSERT INTO [{Schema}].[{table}] (Id, Alt, Grp) VALUES
                (1, 100, N'x'), (2, 200, N'x'), (3, NULL, N'y'), (4, 400, N'y');
            """);

        var columns = new[] { "Id", "Alt", "Grp" };
        await using var probe = await SqlServerUniquenessProbe.CreateAsync(cs, $"[{Schema}].[{table}]", columns, sampleSize: 0);
        var report = await UniqueKeyDetector.DetectAsync(probe, columns, new UniqueKeyOptions());

        Assert.Equal(new[] { "Id" }, report.Candidates.First(c => c.IsUnique).Columns);
        // Alt has all-distinct non-null values but a NULL row, so it can never be a key.
        Assert.DoesNotContain(report.Candidates, c => c.IsUnique && c.Columns.Contains("Alt"));
    }

    [SkippableFact]
    public async Task SampledRun_VerifiesCandidatesAgainstTheWholeTable()
    {
        var cs = IntegrationDb.Require();
        const string table = "UkDetect_Sampled";
        await IntegrationDb.DropTableAsync(cs, table);
        // 200 rows where Id is a true key; a sample of 50 still resolves it and the report marks it verified.
        await IntegrationDb.ExecuteAsync(cs, $"""
            CREATE TABLE [{Schema}].[{table}] (Id int NOT NULL, Bucket int NOT NULL);
            INSERT INTO [{Schema}].[{table}] (Id, Bucket)
            SELECT TOP (200) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 4
            FROM sys.all_objects;
            """);

        var columns = new[] { "Id", "Bucket" };
        await using var probe = await SqlServerUniquenessProbe.CreateAsync(cs, $"[{Schema}].[{table}]", columns, sampleSize: 50);
        var report = await UniqueKeyDetector.DetectAsync(probe, columns, new UniqueKeyOptions());

        Assert.True(report.Sampled);
        Assert.Equal(200, report.TotalRows);
        Assert.Equal(50, report.ScannedRows);
        var key = Assert.Single(report.Candidates, c => c.IsUnique);
        Assert.Equal(new[] { "Id" }, key.Columns);
        Assert.True(key.Verified);
    }
}
