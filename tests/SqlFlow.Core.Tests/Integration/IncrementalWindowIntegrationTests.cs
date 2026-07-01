using SqlFlow.Core.Ingestion;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Exercises the incremental read window against the real sink: the first run into an empty target is a full
/// load; subsequent runs bound the source read to the changed slice using the target watermark. Asserts the
/// generated WHERE string (surfaced on the result), not just row counts, so the legacy precedence is verified
/// exactly.
/// </summary>
[Trait("Category", "Integration")]
public sealed class IncrementalWindowIntegrationTests
{
    private static IngestionFlow Flow(int flowId, string src, string trg, IncrementalPolicy incremental, IngestionSource? sourceOverride = null)
        => new()
        {
            FlowId = flowId,
            Source = sourceOverride ?? new IngestionSource { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = src } },
            Target = new IngestionTarget { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = trg } },
            Load = new IngestionLoadPolicy { KeyColumns = ["Id"] },
            Incremental = incremental,
        };

    [SkippableFact]
    public async Task EmptyTarget_FullLoad_ThenWatermark_StagesOnlyNewRows()
    {
        const int flowId = 11;
        var cs = IntegrationDb.Require();
        const string src = "_SfInc1_Src";
        const string trg = "_SfInc1_Trg";
        await Reset(cs, src, trg, flowId, "[Id] int NOT NULL, [Val] nvarchar(20) NULL");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a'),(2,'b'),(3,'c');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = Flow(flowId, src, trg, new IncrementalPolicy { Columns = ["Id"] });

            var first = await runner.RunAsync(flow);
            Assert.True(first.Success, first.Error);
            Assert.True(first.RunFullLoad);
            Assert.Equal(string.Empty, first.SourceWhere);
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, trg));

            await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (4,'d'),(5,'e');");

            var second = await runner.RunAsync(flow);
            Assert.True(second.Success, second.Error);
            Assert.False(second.RunFullLoad);
            Assert.Equal(" AND [Id] > 3", second.SourceWhere);
            Assert.Equal(2, second.RowsStaged);
            Assert.Equal(5, await IntegrationDb.RowCountAsync(cs, trg));
        }
        finally
        {
            await Cleanup(cs, src, trg, flowId);
        }
    }

    [SkippableFact]
    public async Task DateColumn_AppliesOverlapDays()
    {
        const int flowId = 12;
        var cs = IntegrationDb.Require();
        const string src = "_SfInc2_Src";
        const string trg = "_SfInc2_Trg";
        await Reset(cs, src, trg, flowId, "[Id] int NOT NULL, [OrderDate] datetime2(3) NULL");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'2026-01-10'),(2,'2026-01-20');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = Flow(flowId, src, trg, new IncrementalPolicy { DateColumn = "OrderDate", OverlapDays = 7 });

            var first = await runner.RunAsync(flow);
            Assert.True(first.Success, first.Error);
            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, trg));

            // MAX(OrderDate)=2026-01-20, minus 7 overlap days => 2026-01-13. Only Id=2 (01-20) is re-read.
            var second = await runner.RunAsync(flow);
            Assert.True(second.Success, second.Error);
            Assert.Equal(" AND [OrderDate] > '2026-01-13 00:00:00.000'", second.SourceWhere);
            Assert.Equal(1, second.RowsStaged);
        }
        finally
        {
            await Cleanup(cs, src, trg, flowId);
        }
    }

    [SkippableFact]
    public async Task IncrementalColumn_WinsOverDateColumn()
    {
        const int flowId = 13;
        var cs = IntegrationDb.Require();
        const string src = "_SfInc3_Src";
        const string trg = "_SfInc3_Trg";
        await Reset(cs, src, trg, flowId, "[Id] int NOT NULL, [OrderDate] datetime2(3) NULL");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'2026-01-10'),(2,'2026-01-20');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = Flow(flowId, src, trg, new IncrementalPolicy { Columns = ["Id"], DateColumn = "OrderDate", OverlapDays = 7 });

            Assert.True((await runner.RunAsync(flow)).Success);

            var second = await runner.RunAsync(flow);
            Assert.True(second.Success, second.Error);
            // The incremental predicate wins; the date predicate is suppressed entirely.
            Assert.Equal(" AND [Id] > 2", second.SourceWhere);
            Assert.DoesNotContain("OrderDate", second.SourceWhere, StringComparison.Ordinal);
        }
        finally
        {
            await Cleanup(cs, src, trg, flowId);
        }
    }

    [SkippableFact]
    public async Task FullLoadFlag_ReadsWholeSource()
    {
        const int flowId = 14;
        var cs = IntegrationDb.Require();
        const string src = "_SfInc4_Src";
        const string trg = "_SfInc4_Trg";
        await Reset(cs, src, trg, flowId, "[Id] int NOT NULL, [Val] nvarchar(20) NULL");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a'),(2,'b'),(3,'c');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = Flow(flowId, src, trg, new IncrementalPolicy { Columns = ["Id"], FullLoad = true });

            Assert.True((await runner.RunAsync(flow)).Success);

            // FullLoad flag re-reads the whole source (empty WHERE) and reconciles via the keyed upsert.
            var second = await runner.RunAsync(flow);
            Assert.True(second.Success, second.Error);
            Assert.Equal(string.Empty, second.SourceWhere);
            Assert.Equal(3, second.RowsStaged);
        }
        finally
        {
            await Cleanup(cs, src, trg, flowId);
        }
    }

    [SkippableFact]
    public async Task ReplaceFilter_And_AppendFilter()
    {
        const int flowId = 15;
        var cs = IntegrationDb.Require();
        const string src = "_SfInc5_Src";
        const string trg = "_SfInc5_Trg";
        await Reset(cs, src, trg, flowId, "[Id] int NOT NULL, [Region] nvarchar(10) NULL");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'EU'),(2,'US'),(3,'EU');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var baseFlow = Flow(flowId, src, trg, new IncrementalPolicy { Columns = ["Id"] });

            // Establish the watermark (MAX(Id)=3) with a plain full first load.
            Assert.True((await runner.RunAsync(baseFlow)).Success);

            // Replace mode: the user filter replaces the computed incremental predicate entirely.
            var replace = baseFlow with
            {
                Source = baseFlow.Source with { Filter = "AND [Region] = 'EU'", FilterIsAppend = false },
            };
            var replaceResult = await runner.RunAsync(replace);
            Assert.True(replaceResult.Success, replaceResult.Error);
            Assert.Equal(" AND [Region] = 'EU'", replaceResult.SourceWhere);

            // Append mode: the incremental predicate then the user filter.
            var append = baseFlow with
            {
                Source = baseFlow.Source with { Filter = "AND [Region] = 'EU'", FilterIsAppend = true },
            };
            var appendResult = await runner.RunAsync(append);
            Assert.True(appendResult.Success, appendResult.Error);
            Assert.Equal(" AND [Id] > 3 AND [Region] = 'EU'", appendResult.SourceWhere);
        }
        finally
        {
            await Cleanup(cs, src, trg, flowId);
        }
    }

    [SkippableFact]
    public async Task FullLoad_NonEmptyTarget_NullWatermark_DoesNotDuplicate()
    {
        // Regression: a NULL watermark on a NON-empty target forces a full reload; the keyed apply must
        // anti-join (insert only missing keys), never blindly re-insert existing rows.
        const int flowId = 19;
        var cs = IntegrationDb.Require();
        const string src = "_SfInc6_Src";
        const string trg = "_SfInc6_Trg";
        await Reset(cs, src, trg, flowId, "[Id] int NOT NULL, [Val] nvarchar(20) NULL, [ModifiedDate] datetime2(3) NULL");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a',NULL),(2,'b',NULL),(3,'c',NULL);");

        // Pre-populate the target (matching the evolved schema) with rows whose watermark column is NULL.
        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{trg}] ([Id] int NOT NULL, [Val] nvarchar(20) NULL, [ModifiedDate] datetime2(3) NULL, [InsertedDate_DW] datetime2(3) NULL, [UpdatedDate_DW] datetime2(3) NULL);");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{trg}] ([Id],[Val]) VALUES (1,'a'),(2,'b');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = Flow(flowId, src, trg, new IncrementalPolicy { DateColumn = "ModifiedDate" });

            var result = await runner.RunAsync(flow);
            Assert.True(result.Success, result.Error);
            Assert.True(result.RunFullLoad);
            Assert.Equal(string.Empty, result.SourceWhere);
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, trg));
            Assert.Equal(1, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [Id] = 1"));
        }
        finally
        {
            await Cleanup(cs, src, trg, flowId);
        }
    }

    private static async Task Reset(string cs, string src, string trg, int flowId, string sourceColumns)
    {
        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await RelationalIngestionHarness.DropStagingAsync(cs, flowId);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{src}] ({sourceColumns});");
    }

    private static async Task Cleanup(string cs, string src, string trg, int flowId)
    {
        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await RelationalIngestionHarness.DropStagingAsync(cs, flowId);
    }
}
