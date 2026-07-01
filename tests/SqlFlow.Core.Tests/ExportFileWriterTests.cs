using System.Data;
using System.Text;
using Parquet;
using SqlFlow.SqlServer.Export;
using Xunit;

namespace SqlFlow.Tests;

public sealed class ExportFileWriterTests
{
    private static DataTable Sample()
    {
        var table = new DataTable();
        table.Columns.Add("Id", typeof(int));
        table.Columns.Add("Name", typeof(string));
        table.Columns.Add("Amount", typeof(decimal));
        table.Rows.Add(1, "Ann", 10.5m);
        table.Rows.Add(2, "B;ig\"quote", 20m);
        table.Rows.Add(3, DBNull.Value, DBNull.Value);
        return table;
    }

    [Fact]
    public async Task Csv_WritesHeader_Delimiter_Quoting_AndNulls()
    {
        using var table = Sample();
        using var reader = table.CreateDataReader();
        var writer = new CsvExportFileWriter(new CsvWriteOptions
        {
            Delimiter = ";",
            Qualifier = '"',
            Encoding = new UTF8Encoding(false),
        });

        using var stream = new MemoryStream();
        var rows = await writer.WriteAsync(reader, stream);

        Assert.Equal(3, rows);
        var lines = Encoding.UTF8.GetString(stream.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Id;Name;Amount", lines[0]);
        Assert.Equal("1;\"Ann\";10.5", lines[1]);                       // string column quoted, numbers bare
        Assert.Equal("2;\"B;ig\"\"quote\";20", lines[2]);               // embedded delimiter + doubled quote
        Assert.Equal("3;;", lines[3]);                                  // nulls -> empty fields
    }

    [Fact]
    public async Task Parquet_RoundTripsThroughParquetNet()
    {
        using var table = Sample();
        using var reader = table.CreateDataReader();
        var writer = new ParquetExportFileWriter("gzip");

        using var stream = new MemoryStream();
        var rows = await writer.WriteAsync(reader, stream);
        Assert.Equal(3, rows);

        stream.Position = 0;
        using var parquet = await ParquetReader.CreateAsync(stream);
        Assert.Equal(3, parquet.Schema.DataFields.Length);
        Assert.Equal(["Id", "Name", "Amount"], parquet.Schema.DataFields.Select(f => f.Name).ToArray());

        using var rowGroup = parquet.OpenRowGroupReader(0);
        var idColumn = await rowGroup.ReadColumnAsync(parquet.Schema.DataFields[0]);
        Assert.Equal(new int?[] { 1, 2, 3 }, idColumn.Data.Cast<int?>().ToArray());

        var nameColumn = await rowGroup.ReadColumnAsync(parquet.Schema.DataFields[1]);
        Assert.Equal(new[] { "Ann", "B;ig\"quote", null }, nameColumn.Data.Cast<string?>().ToArray());
    }
}
