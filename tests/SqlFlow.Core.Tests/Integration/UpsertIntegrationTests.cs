using SqlFlow.Core.Ingestion;
using SqlFlow.SqlServer.Schema;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Exercises the two-step staging-to-target upsert against the real sink: it updates a changed row, inserts
/// a new row, stamps the system date columns on the right branch, and (on a re-run with unchanged staging)
/// detects no change and rewrites nothing.
/// </summary>
[Trait("Category", "Integration")]
public sealed class UpsertIntegrationTests
{
    [SkippableFact]
    public async Task Upsert_UpdatesChanged_InsertsNew_AndReRunIsNoOp()
    {
        var cs = IntegrationDb.Require();
        const string trg = "_SfUpsert_Trg";
        const string stg = "_SfUpsert_Stg";
        await IntegrationDb.DropTableAsync(cs, trg);
        await IntegrationDb.DropTableAsync(cs, stg);

        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{trg}] ([Id] int NOT NULL PRIMARY KEY, [Name] nvarchar(50) NULL, [Amount] int NULL, [InsertedDate_DW] datetime2(3) NULL, [UpdatedDate_DW] datetime2(3) NULL);");
        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{stg}] ([Id] int NOT NULL, [Name] nvarchar(50) NULL, [Amount] int NULL);");

        try
        {
            await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{trg}] ([Id],[Name],[Amount]) VALUES (1, 'Ann', 100);");
            await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{stg}] ([Id],[Name],[Amount]) VALUES (1, 'Ann', 200), (2, 'Bob', 300);");

            var options = new UpsertOptions
            {
                DataColumns = ["Id", "Name", "Amount"],
                KeyColumns = ["Id"],
                InsertedDateColumn = "InsertedDate_DW",
                UpdatedDateColumn = "UpdatedDate_DW",
            };
            var target = new RelationalObject { Database = "db", Schema = "dbo", Name = trg };
            var staging = new RelationalObject { Database = "db", Schema = "dbo", Name = stg };
            var statements = UpsertGenerator.Generate(target, staging, options);

            foreach (var statement in statements)
            {
                await IntegrationDb.ExecuteAsync(cs, statement);
            }

            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, trg));
            Assert.Equal(200, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT [Amount] FROM [dbo].[{trg}] WHERE [Id] = 1"));
            Assert.Equal("Bob", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [Name] FROM [dbo].[{trg}] WHERE [Id] = 2"));
            Assert.Equal(1, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [Id] = 1 AND [UpdatedDate_DW] IS NOT NULL"));
            Assert.Equal(1, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [Id] = 2 AND [InsertedDate_DW] IS NOT NULL"));

            // Re-run with unchanged staging: the checksum matches (no UPDATE) and the rows exist (no INSERT).
            var updatedBefore = await IntegrationDb.ScalarAsync<DateTime?>(cs, $"SELECT [UpdatedDate_DW] FROM [dbo].[{trg}] WHERE [Id] = 1");
            foreach (var statement in statements)
            {
                await IntegrationDb.ExecuteAsync(cs, statement);
            }

            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, trg));
            var updatedAfter = await IntegrationDb.ScalarAsync<DateTime?>(cs, $"SELECT [UpdatedDate_DW] FROM [dbo].[{trg}] WHERE [Id] = 1");
            Assert.Equal(updatedBefore, updatedAfter);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, trg);
            await IntegrationDb.DropTableAsync(cs, stg);
        }
    }
}
