using SqlFlow.Core.Model;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// End-to-end incremental load against the physical sink: the engine probes the target for the
/// MAX(FileDate_DW) watermark and only re-reads files newer than it. Proves that a second run with an
/// older file loads nothing, a newer file loads only itself, and a run with no new files is a clean
/// no-op (not a failure).
/// </summary>
[Trait("Category", "Integration")]
public sealed class IncrementalIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_inc_" + Guid.NewGuid().ToString("N"));

    public IncrementalIntegrationTests() => Directory.CreateDirectory(_dir);

    private SourceSpec DatedCsv(string name, string content, DateTime modifiedUtc)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, modifiedUtc);
        return new SourceSpec { Type = "csv", Location = path, Options = new Dictionary<string, string?>() };
    }

    private static FlowDefinition Flow(string cs, SourceSpec source, string table)
        => new()
        {
            Name = table,
            Source = source,
            Target = new TargetSpec { Connection = cs, Schema = IntegrationDb.Schema, Table = table },
            Schema = new SchemaPolicy { Evolve = SchemaEvolution.Widen },
            Load = new LoadPolicy(),
            Incremental = new IncrementalSpec { DateColumn = "FileDate_DW" },
        };

    [SkippableFact]
    public async Task IncrementalLoad_OnlyIngestsFilesNewerThanWatermark()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_Incr_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        try
        {
            // First run: an "old" file establishes the watermark.
            var first = await IntegrationDb.RealRunner().RunAsync(
                Flow(cs, DatedCsv("day1.csv", "OrderId\n1\n2\n", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)), table));
            Assert.Equal(FlowStatus.Success, first.Status);
            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, table));

            // Second run sees only an older file -> nothing new -> clean no-op, row count unchanged.
            var noop = await IntegrationDb.RealRunner().RunAsync(
                Flow(cs, DatedCsv("day0.csv", "OrderId\n9\n", new DateTime(2023, 12, 1, 0, 0, 0, DateTimeKind.Utc)), table));
            Assert.Equal(FlowStatus.Success, noop.Status);
            Assert.Equal(0, noop.RowsLoaded);
            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, table));

            // Third run with a newer file ingests only that file.
            var third = await IntegrationDb.RealRunner().RunAsync(
                Flow(cs, DatedCsv("day2.csv", "OrderId\n3\n4\n5\n", new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc)), table));
            Assert.Equal(FlowStatus.Success, third.Status);
            Assert.Equal(3, third.RowsLoaded);
            Assert.Equal(5, await IntegrationDb.RowCountAsync(cs, table));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task IncrementalLoad_EmptyTarget_LoadsEverything()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_IncrFull_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        try
        {
            // No target yet -> no watermark -> full load of the available file.
            var result = await IntegrationDb.RealRunner().RunAsync(
                Flow(cs, DatedCsv("seed.csv", "OrderId\n1\n2\n3\n", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)), table));

            Assert.Equal(FlowStatus.Success, result.Status);
            Assert.Equal(3, result.RowsLoaded);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
