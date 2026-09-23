using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Source;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The SQL Server source over record tables of their own, in the chain fixture's database and ingestion schema: a read
/// paged and cut on the table's identity primary key, and the tables whose declared primary key a read cannot rely on.
/// Runs on the suites' test database (<see cref="OsduTestServer"/>).
/// </summary>
public sealed class SqlServerIngestionSourceTests
{
    private static readonly DateTime Loaded = new(2026, 9, 1, 6, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Changed = new(2026, 9, 2, 6, 0, 0, DateTimeKind.Utc);

    private static readonly IReadOnlyDictionary<string, string> NoValues = new Dictionary<string, string>(StringComparer.Ordinal);

    [Fact]
    public async Task A_read_is_cut_on_the_identity_key_into_ranges_that_hold_every_candidate_once()
    {
        await using var estate = await SqlServerIngestionFixture.StartAsync();
        var table = await CreateTableAsync(
            estate,
            "Item",
            "[RecId] bigint IDENTITY(1, 1) NOT NULL CONSTRAINT [PK_Item] PRIMARY KEY CLUSTERED, [item_key] nvarchar(50) NOT NULL, [UpdatedDate_DW] datetime NULL",
            "CREATE UNIQUE NONCLUSTERED INDEX [NCI_KeyColumn] ON {0} ([item_key]); CREATE NONCLUSTERED INDEX [NCI_UpdatedDate_DW] ON {0} ([UpdatedDate_DW]);");

        // 6,000 rows, then a hole of 2,000 identity values, then 1,000 rows after the identity jumped far ahead: the values
        // the ranges are cut on are nowhere near dense. Every seventh value changed after the load.
        await ExecuteAsync(estate, $"""
            INSERT INTO {table} ([item_key], [UpdatedDate_DW])
            SELECT CONCAT(N'item-', FORMAT(n.rn, 'D6')), @loaded
            FROM (SELECT TOP (6000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS rn FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b) AS n
            ORDER BY n.rn;
            DELETE FROM {table} WHERE [RecId] BETWEEN 2001 AND 4000;
            DBCC CHECKIDENT ('[{estate.IngSchema}].[Item]', RESEED, 100000) WITH NO_INFOMSGS;
            INSERT INTO {table} ([item_key], [UpdatedDate_DW])
            SELECT CONCAT(N'late-', FORMAT(n.rn, 'D6')), @loaded
            FROM (SELECT TOP (1000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS rn FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b) AS n
            ORDER BY n.rn;
            UPDATE {table} SET [UpdatedDate_DW] = @changed WHERE [RecId] % 7 = 0;
            """);
        var ids = Enumerable.Range(1, 2000).Concat(Enumerable.Range(4001, 2000)).Concat(Enumerable.Range(100001, 1000)).Select(i => (long)i).ToList();
        var flow = ItemFlow(estate, "Item", pageSize: 250);

        // The whole table: eight ranges of the identity, each holding its share give or take one counted range of values.
        var source = estate.Engine.Sources.Open(flow, NoValues);
        var full = await source.OpenAsync(SourceSelection.Full(), null);
        Assert.Equal((5000L, "RecId"), (full.EstimatedCandidates, full.PrimaryKey));
        var ranges = await source.SliceBoundsAsync(full, 8);
        Assert.Equal(8, ranges.Count);
        Assert.All(ranges, r => Assert.Equal("RecId", r.On));
        Assert.Equal((null, null), (ranges[0].From, ranges[^1].To));
        var width = ((101_000L - 1) / (8 * KeySlices.BucketsPerSlice)) + 1;
        var read = new List<long>();
        foreach (var range in ranges)
        {
            var slice = await ReadIdsAsync(source, full, range);
            Assert.InRange(slice.Count, 625 - width, 625 + width);
            Assert.Equal(slice.Order(), slice);
            read.AddRange(slice);
        }

        Assert.Equal(ids, read.Order());

        // Read whole, a page of 250 at a time, it meets every row once in identity order.
        Assert.Equal(ids, await ReadIdsAsync(source, full, null));

        // What changed since the load: a seventh of the rows, cut and read the same way.
        var changedSource = estate.Engine.Sources.Open(flow, NoValues);
        var changed = await changedSource.OpenAsync(SourceSelection.Incremental(Loaded), null);
        var expected = ids.Where(i => i % 7 == 0).ToList();
        Assert.Equal(expected.Count, changed.EstimatedCandidates);
        var changedRanges = await changedSource.SliceBoundsAsync(changed, 4);
        Assert.Equal(4, changedRanges.Count);
        var changedRead = new List<long>();
        foreach (var range in changedRanges)
        {
            changedRead.AddRange(await ReadIdsAsync(changedSource, changed, range));
        }

        Assert.Equal(expected, changedRead.Order());

        // Named records are found through the record key and read in identity order.
        var keysSource = estate.Engine.Sources.Open(flow, NoValues);
        var named = await keysSource.OpenAsync(SourceSelection.ForKeys([KeyTuple.Of("late-000001"), KeyTuple.Of("item-000005"), KeyTuple.Of("item-004001")]), null);
        Assert.Equal(3, named.EstimatedCandidates);
        Assert.Equal([5L, 4001L, 100001L], await ReadIdsAsync(keysSource, named, null));

        // A slice cut on the record key belongs to another way of reading, and is refused.
        var foreign = await Assert.ThrowsAsync<DeliveryException>(() => ReadIdsAsync(source, full, new KeyRange(0, null, KeyTuple.Of("item-000100"))));
        Assert.Contains("was cut on the record key", foreign.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_primary_key_a_read_cannot_rely_on_is_refused_with_what_it_lacks()
    {
        await using var estate = await SqlServerIngestionFixture.StartAsync();
        const string Unique = "CREATE UNIQUE NONCLUSTERED INDEX [NCI_KeyColumn] ON {0} ([item_key]);";
        await CreateTableAsync(estate, "PlainId", "[RecId] int NOT NULL, [item_key] nvarchar(50) NOT NULL, [UpdatedDate_DW] datetime NULL", Unique);
        await CreateTableAsync(
            estate, "KeyedByName", "[RecId] int IDENTITY(1, 1) NOT NULL, [item_key] nvarchar(50) NOT NULL CONSTRAINT [PK_KeyedByName] PRIMARY KEY, [UpdatedDate_DW] datetime NULL", string.Empty);
        await CreateTableAsync(
            estate,
            "Composite",
            "[RecId] int IDENTITY(1, 1) NOT NULL, [item_key] nvarchar(50) NOT NULL, [UpdatedDate_DW] datetime NULL, CONSTRAINT [PK_Composite] PRIMARY KEY ([RecId], [item_key])",
            Unique);
        await CreateTableAsync(
            estate, "NoUniqueKey", "[RecId] int IDENTITY(1, 1) NOT NULL CONSTRAINT [PK_NoUniqueKey] PRIMARY KEY, [item_key] nvarchar(50) NOT NULL, [UpdatedDate_DW] datetime NULL", string.Empty);
        await CreateTableAsync(
            estate,
            "History",
            "[RecId] int IDENTITY(1, 1) NOT NULL CONSTRAINT [PK_History] PRIMARY KEY, [item_key] nvarchar(50) NOT NULL, [UpdatedDate_DW] datetime NULL, [IsCurrent_DW] bit NOT NULL",
            "CREATE UNIQUE NONCLUSTERED INDEX [NCI_KeyColumn] ON {0} ([item_key]) WHERE [IsCurrent_DW] = 1;");
        await CreateTableAsync(
            estate, "TextId", "[RecId] nvarchar(20) NOT NULL CONSTRAINT [PK_TextId] PRIMARY KEY, [item_key] nvarchar(50) NOT NULL, [UpdatedDate_DW] datetime NULL", Unique);
        await CreateTableAsync(
            estate, "Good", "[RecId] int IDENTITY(1, 1) NOT NULL CONSTRAINT [PK_Good] PRIMARY KEY, [item_key] nvarchar(50) NOT NULL, [UpdatedDate_DW] datetime NULL", Unique);

        async Task<string> RefusedAsync(string table)
        {
            var source = estate.Engine.Sources.Open(ItemFlow(estate, table), NoValues);
            return (await Assert.ThrowsAsync<FlowValidationException>(() => source.OpenAsync(SourceSelection.Full(), null))).Message;
        }

        var plain = await RefusedAsync("PlainId");
        Assert.Contains("which is neither an identity column nor the table's primary key", plain, StringComparison.Ordinal);
        Assert.Contains($"ALTER TABLE [{estate.DatabaseName}].[{estate.IngSchema}].[PlainId] ADD [RecId] bigint IDENTITY(1, 1) NOT NULL", plain, StringComparison.Ordinal);
        Assert.Contains("which is not the table's single-column primary key", await RefusedAsync("KeyedByName"), StringComparison.Ordinal);
        Assert.Contains("which is not the table's single-column primary key", await RefusedAsync("Composite"), StringComparison.Ordinal);
        Assert.Contains("has no unique index without a filter", await RefusedAsync("NoUniqueKey"), StringComparison.Ordinal);
        Assert.Contains("has no unique index without a filter", await RefusedAsync("History"), StringComparison.Ordinal);
        Assert.Contains("which is nvarchar(20). A read is paged and cut on an integer identity column", await RefusedAsync("TextId"), StringComparison.Ordinal);

        var good = estate.Engine.Sources.Open(ItemFlow(estate, "Good"), NoValues);
        Assert.Equal("RecId", (await good.OpenAsync(SourceSelection.Full(), null)).PrimaryKey);
        Assert.Equal(0, await CountRangesAsync(good));
    }

    /// <summary>The sample flow, reading one of this test's tables by its item key and its identity primary key.</summary>
    private static FlowDefinition ItemFlow(SqlServerIngestionFixture estate, string table, int pageSize = FlowIncremental.DefaultPageSize)
    {
        var sample = estate.DeliveryFlow();
        return sample with
        {
            Source = sample.Source with
            {
                Record = new FlowSourceTable
                {
                    Object = $"[{estate.DatabaseName}].[{estate.IngSchema}].[{table}]",
                    Key = ["item_key"],
                    PrimaryKey = "RecId",
                },
                Datasets = new Dictionary<string, FlowSourceDataset>(StringComparer.Ordinal),
                Incremental = sample.Source.Incremental with { PageSize = pageSize },
            },
        };
    }

    private static async Task<int> CountRangesAsync(IIngestionSource source)
    {
        var header = await source.OpenAsync(SourceSelection.Full(), null);
        return header.EstimatedCandidates == 0 ? 0 : (await source.SliceBoundsAsync(header, 4)).Count;
    }

    private static async Task<List<long>> ReadIdsAsync(IIngestionSource source, SourceHeader header, KeyRange? range)
    {
        var ids = new List<long>();
        await foreach (var record in source.ReadAsync(header, range))
        {
            ids.Add(Convert.ToInt64(record.Row.Get("RecId"), CultureInfo.InvariantCulture));
        }

        return ids;
    }

    /// <summary>Creates a table in the fixture's ingestion schema, with the statements that follow it; <c>{0}</c> names the table.</summary>
    private static async Task<string> CreateTableAsync(SqlServerIngestionFixture estate, string name, string columns, string then)
    {
        var table = $"[{estate.IngSchema}].[{name}]";
        await ExecuteAsync(estate, $"CREATE TABLE {table} ({columns});");
        if (then.Length > 0)
        {
            await ExecuteAsync(estate, string.Format(CultureInfo.InvariantCulture, then, table));
        }

        return table;
    }

    private static async Task ExecuteAsync(SqlServerIngestionFixture estate, string sql)
    {
        await using var connection = new SqlConnection(estate.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        command.Parameters.Add(new SqlParameter("@loaded", System.Data.SqlDbType.DateTime) { Value = Loaded });
        command.Parameters.Add(new SqlParameter("@changed", System.Data.SqlDbType.DateTime) { Value = Changed });
        await command.ExecuteNonQueryAsync();
    }
}
