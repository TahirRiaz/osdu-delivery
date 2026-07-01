using SqlFlow.Core.Ingestion;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// The engine's universal backstop for flags that are accepted by the loaders (YAML, control-DB, legacy) but not
/// yet implemented: a flow that sets versioning.temporalHistory or versioning.insertUnknownDimensionRow fails
/// with a clear message instead of silently doing nothing. The YAML loader rejects these earlier (unit-tested);
/// this proves the runner guard that also covers the non-YAML load paths. Skips when the sink is unreachable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class UnsupportedFeatureGuardIntegrationTests
{
    private static IngestionFlow BuildFlow(string src, string trg, VersioningPolicy versioning) => new()
    {
        FlowId = 94,
        Source = new IngestionSource { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = src } },
        Target = new IngestionTarget { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = trg } },
        Load = new IngestionLoadPolicy { KeyColumns = ["Id"] },
        Versioning = versioning,
    };

    [SkippableTheory]
    [InlineData(true, false, "temporalHistory")]
    [InlineData(false, true, "insertUnknownDimensionRow")]
    public async Task UnsupportedVersioningFlag_FailsRunWithClearMessage(bool temporal, bool unknownRow, string expected)
    {
        var cs = IntegrationDb.Require();
        const string src = "_SfGuard_Src";
        const string trg = "_SfGuard_Trg";
        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);

        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{src}] ([Id] int NOT NULL, [Name] nvarchar(50) NULL);");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a');");

        try
        {
            var flow = BuildFlow(src, trg, new VersioningPolicy { TemporalHistory = temporal, InsertUnknownDimensionRow = unknownRow });
            var result = await RelationalIngestionHarness.BuildRunner().RunAsync(flow);

            Assert.False(result.Success);
            Assert.Contains(expected, result.Error, StringComparison.Ordinal);
            Assert.Contains("not yet implemented", result.Error!, StringComparison.OrdinalIgnoreCase);

            // The guard fires before any target work: nothing was created.
            Assert.False(await IntegrationDb.TableExistsAsync(cs, trg));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, src);
            await IntegrationDb.DropTableAsync(cs, trg);
        }
    }
}
