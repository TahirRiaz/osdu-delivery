using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Source;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The provenance a read gives each record, its origin file and row, which is what the ledger traces a record back to: a
/// column the flow names itself has to be on the record table, as the delete and insert stamps have to, while one left at
/// its default is read when the table carries it; and the row column has to hold a whole number. Runs on the suites' test
/// database (<see cref="OsduTestServer"/>), with tables of its own in the fixture's ingestion schema.
/// </summary>
[Collection(SqlServerSuite.Name)]
public sealed class SqlServerIngestionProvenanceTests
{
    private static readonly DateTime Loaded = new(2026, 9, 1, 6, 0, 0, DateTimeKind.Utc);

    private static readonly IReadOnlyDictionary<string, string> NoValues = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>A delivery flow in the single form whose system columns are <paramref name="systemColumns"/>, or the defaults when null.</summary>
    private static string FlowYaml(string? systemColumns) => $$"""
        flowType: delivery
        name: provenance-demo
        source:
          connection: ${env:OSDU_DATA_DB}
          record:
            object: OsduData.silver.Item
            key: [item_key]
        {{(systemColumns is null ? string.Empty : "  systemColumns: " + systemColumns)}}
          work: work
        render:
          mapping: Item@1.0.0
          parameters: { dataPartition: dev }
        target:
          endpoint: https://osdu.example.com
          headers: { data-partition-id: dev }
          protocol: storage
        """;

    [Fact]
    public void A_provenance_column_the_flow_names_is_marked_as_named_and_one_left_out_or_opted_out_is_not()
    {
        var loader = new DeliveryDocumentLoader();

        var defaults = loader.ParseFlow(FlowYaml(null), "defaults.yaml").Source.SystemColumns;
        Assert.Equal((FlowSystemColumns.DefaultFileName, FlowSystemColumns.DefaultRowNumber), (defaults.FileName, defaults.RowNumber));
        Assert.False(defaults.FileNameDeclared);
        Assert.False(defaults.RowNumberDeclared);

        var named = loader.ParseFlow(FlowYaml("{ fileName: Origin_File, rowNumber: Origin_Row }"), "named.yaml").Source.SystemColumns;
        Assert.Equal(("Origin_File", "Origin_Row"), (named.FileName, named.RowNumber));
        Assert.True(named.FileNameDeclared);
        Assert.True(named.RowNumberDeclared);

        // Naming the default's own name is naming it: the table then has to hold it.
        var spelledOut = loader.ParseFlow(FlowYaml("{ fileName: FileName_DW }"), "spelled.yaml").Source.SystemColumns;
        Assert.True(spelledOut.FileNameDeclared);
        Assert.False(spelledOut.RowNumberDeclared);

        var optedOut = loader.ParseFlow(FlowYaml("{ fileName: ~, rowNumber: ~ }"), "opted-out.yaml").Source.SystemColumns;
        Assert.Equal((null, null), (optedOut.FileName, optedOut.RowNumber));
        Assert.False(optedOut.FileNameDeclared);
        Assert.False(optedOut.RowNumberDeclared);
    }

    [Fact]
    public async Task A_provenance_column_the_flow_names_that_the_table_lacks_is_refused_naming_the_column_and_the_table()
    {
        await using var estate = await SqlServerIngestionFixture.StartAsync();
        await CreateTableAsync(
            estate, "Traced",
            "[RecId] bigint IDENTITY(1, 1) NOT NULL CONSTRAINT [PK_Traced] PRIMARY KEY, [item_key] nvarchar(50) NOT NULL, [UpdatedDate_DW] datetime NULL, [FileName_DW] nvarchar(400) NULL, [RowNumber_DW] bigint NULL");
        await CreateTableAsync(
            estate, "Untraced",
            "[RecId] bigint IDENTITY(1, 1) NOT NULL CONSTRAINT [PK_Untraced] PRIMARY KEY, [item_key] nvarchar(50) NOT NULL, [UpdatedDate_DW] datetime NULL");
        await ExecuteAsync(estate, $"INSERT INTO [{estate.SilverSchema}].[Traced] ([item_key], [UpdatedDate_DW], [FileName_DW], [RowNumber_DW]) VALUES (N'item', @loaded, N'landing/items_001.csv', 7);");
        await ExecuteAsync(estate, $"INSERT INTO [{estate.SilverSchema}].[Untraced] ([item_key], [UpdatedDate_DW]) VALUES (N'item', @loaded);");

        // The defaults read what the table holds, and a table without them is read without an origin, as before.
        var traced = await OriginAsync(estate, ItemFlow(estate, "Traced", c => c));
        Assert.Equal(("landing/items_001.csv", 7L), (traced.FileName, traced.RowNumber));
        var untraced = await OriginAsync(estate, ItemFlow(estate, "Untraced", c => c));
        Assert.Equal((null, null), (untraced.FileName, untraced.RowNumber));

        // A file name column the flow names, misspelt: refused, naming the key, the column, the table and what it holds.
        var misspeltFile = await Assert.ThrowsAsync<FlowValidationException>(() => OriginAsync(
            estate, ItemFlow(estate, "Traced", c => c with { FileName = "FileNam_DW", FileNameDeclared = true })));
        Assert.Contains("source.systemColumns.fileName names column 'FileNam_DW', which the record table", misspeltFile.Message, StringComparison.Ordinal);
        Assert.Contains($"{estate.SilverSchema}.Traced", misspeltFile.Message, StringComparison.Ordinal);
        Assert.Contains("without its origin file", misspeltFile.Message, StringComparison.Ordinal);
        Assert.Contains("FileName_DW", misspeltFile.Message, StringComparison.Ordinal);
        Assert.Contains("source.systemColumns.fileName: ~", misspeltFile.Message, StringComparison.Ordinal);

        // The same for the row column, and for the default names spelled out on a table that does not hold them.
        var misspeltRow = await Assert.ThrowsAsync<FlowValidationException>(() => OriginAsync(
            estate, ItemFlow(estate, "Traced", c => c with { RowNumber = "RowNum_DW", RowNumberDeclared = true })));
        Assert.Contains("source.systemColumns.rowNumber names column 'RowNum_DW'", misspeltRow.Message, StringComparison.Ordinal);
        Assert.Contains("without its origin row", misspeltRow.Message, StringComparison.Ordinal);
        var spelledOut = await Assert.ThrowsAsync<FlowValidationException>(() => OriginAsync(
            estate, ItemFlow(estate, "Untraced", c => c with { FileNameDeclared = true })));
        Assert.Contains("names column 'FileName_DW', which the record table", spelledOut.Message, StringComparison.Ordinal);

        // Opted out, the table that lacks them is read without an origin.
        var optedOut = await OriginAsync(estate, ItemFlow(estate, "Untraced", c => c with { FileName = null, RowNumber = null }));
        Assert.Equal((null, null), (optedOut.FileName, optedOut.RowNumber));
    }

    [Fact]
    public async Task A_row_column_that_cannot_hold_a_whole_number_is_refused_and_a_decimal_without_a_scale_is_read()
    {
        await using var estate = await SqlServerIngestionFixture.StartAsync();
        await CreateTableAsync(
            estate, "TextRow",
            "[RecId] bigint IDENTITY(1, 1) NOT NULL CONSTRAINT [PK_TextRow] PRIMARY KEY, [item_key] nvarchar(50) NOT NULL, [UpdatedDate_DW] datetime NULL, [RowNumber_DW] nvarchar(20) NULL");
        await CreateTableAsync(
            estate, "NumericRow",
            "[RecId] bigint IDENTITY(1, 1) NOT NULL CONSTRAINT [PK_NumericRow] PRIMARY KEY, [item_key] nvarchar(50) NOT NULL, [UpdatedDate_DW] datetime NULL, [RowNumber_DW] numeric(18, 0) NULL");
        await ExecuteAsync(estate, $"INSERT INTO [{estate.SilverSchema}].[NumericRow] ([item_key], [UpdatedDate_DW], [RowNumber_DW]) VALUES (N'item', @loaded, 42);");

        // A row number held as text would reach the ledger as no row at all, so the table is refused with the way out.
        var text = await Assert.ThrowsAsync<FlowValidationException>(() => OriginAsync(estate, ItemFlow(estate, "TextRow", c => c)));
        Assert.Contains("names column 'RowNumber_DW'", text.Message, StringComparison.Ordinal);
        Assert.Contains("which is nvarchar(20)", text.Message, StringComparison.Ordinal);
        Assert.Contains("source.systemColumns.rowNumber: ~", text.Message, StringComparison.Ordinal);

        // A whole number in a decimal column is the record's origin row like any other.
        Assert.Equal(42L, (await OriginAsync(estate, ItemFlow(estate, "NumericRow", c => c))).RowNumber);
    }

    /// <summary>The sample flow reading one of this class's tables by its item key, its system columns changed by <paramref name="columns"/>.</summary>
    private static FlowDefinition ItemFlow(SqlServerIngestionFixture estate, string table, Func<FlowSystemColumns, FlowSystemColumns> columns)
    {
        var sample = estate.DeliveryFlow();
        return sample with
        {
            Source = sample.Source with
            {
                Record = new FlowSourceTable
                {
                    Object = $"[{estate.DatabaseName}].[{estate.SilverSchema}].[{table}]",
                    Key = ["item_key"],
                    PrimaryKey = "RecId",
                },
                Datasets = new Dictionary<string, FlowSourceDataset>(StringComparer.Ordinal),
                SystemColumns = columns(new FlowSystemColumns()),
            },
        };
    }

    /// <summary>The origin of the one record the table holds, as a full read gives it.</summary>
    private static async Task<SourceOrigin> OriginAsync(SqlServerIngestionFixture estate, FlowDefinition flow)
    {
        var source = estate.Engine.Sources.Open(flow, NoValues, NullLoggerFactory.Instance);
        var header = await source.OpenAsync(SourceSelection.Full(), null);
        await foreach (var record in source.ReadAsync(header, null))
        {
            return record.Origin;
        }

        throw new InvalidOperationException($"The table read for flow '{flow.Label}' holds one row, and the read found none.");
    }

    /// <summary>Creates a table in the fixture's ingestion schema, with the unique index on its key a read by ranges needs.</summary>
    private static async Task CreateTableAsync(SqlServerIngestionFixture estate, string name, string columns)
    {
        var table = $"[{estate.SilverSchema}].[{name}]";
        await ExecuteAsync(estate, $"CREATE TABLE {table} ({columns});");
        await ExecuteAsync(estate, $"CREATE UNIQUE NONCLUSTERED INDEX [NCI_KeyColumn] ON {table} ([item_key]);");
    }

    private static async Task ExecuteAsync(SqlServerIngestionFixture estate, string sql)
    {
        await using var connection = new SqlConnection(estate.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        command.Parameters.Add(new SqlParameter("@loaded", System.Data.SqlDbType.DateTime) { Value = Loaded });
        await command.ExecuteNonQueryAsync();
    }
}
