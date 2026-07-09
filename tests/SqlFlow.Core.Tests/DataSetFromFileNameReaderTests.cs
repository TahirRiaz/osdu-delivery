using SqlFlow.Core.Model;
using SqlFlow.Sources;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// End-to-end (no database) verification that the file readers populate DataSet_DW from a date detected in the
/// file NAME while FileDate_DW stays the file's last-modified timestamp, that a name with no date falls back to
/// last-modified, and that <c>dataSetFromFileName: false</c> restores the legacy-identical behavior where
/// DataSet_DW equals FileDate_DW. Exercises the shared pipeline through the CSV reader.
/// </summary>
public sealed class DataSetFromFileNameReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_dataset_" + Guid.NewGuid().ToString("N"));
    private readonly CsvSourceReader _reader = new(new LocalFileLifecycle(), [new LocalFileStore()]);

    public DataSetFromFileNameReaderTests() => Directory.CreateDirectory(_dir);

    // Writes a CSV whose last-modified time is pinned distinct from any date in its name, so the two provenance
    // columns are distinguishable.
    private SourceSpec Csv(string fileName, DateTime modifiedUtc, Dictionary<string, string?>? options = null)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, "OrderId,Amount\n1,9.5\n2,3.0\n");
        File.SetLastWriteTimeUtc(path, modifiedUtc);
        return new SourceSpec { Type = "csv", Location = path, Options = options ?? new Dictionary<string, string?>() };
    }

    private async Task<(int FileDate, int DataSet, List<object?[]> Rows)> ReadAsync(SourceSpec source)
    {
        var columns = (await _reader.GetColumnsAsync(source)).Select(c => c.Name).ToList();
        var read = await _reader.OpenAsync(source, await _reader.GetColumnsAsync(source));
        await using var data = read.Reader;

        var rows = new List<object?[]>();
        while (await data.ReadAsync())
        {
            var row = new object?[data.FieldCount];
            for (var i = 0; i < data.FieldCount; i++)
            {
                row[i] = data.IsDBNull(i) ? null : data.GetValue(i);
            }

            rows.Add(row);
        }

        return (columns.IndexOf("FileDate_DW"), columns.IndexOf("DataSet_DW"), rows);
    }

    [Fact]
    public async Task DataSet_TakesFilenameDate_WhileFileDate_StaysLastModified()
    {
        var modified = new DateTime(2024, 6, 15, 9, 0, 0, DateTimeKind.Utc);
        var (fileDate, dataSet, rows) = await ReadAsync(Csv("sess_20240101.csv", modified));

        Assert.NotEmpty(rows);
        Assert.All(rows, r =>
        {
            Assert.Equal("20240615090000", r[fileDate]);   // last-modified
            Assert.Equal("20240101000000", r[dataSet]);    // date parsed from the name
        });
    }

    [Fact]
    public async Task DataSet_FallsBackToLastModified_WhenNameHasNoDate()
    {
        var modified = new DateTime(2024, 6, 15, 9, 0, 0, DateTimeKind.Utc);
        var (fileDate, dataSet, rows) = await ReadAsync(Csv("orders_batch.csv", modified));

        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Equal(r[fileDate], r[dataSet]));   // both = last-modified
        Assert.All(rows, r => Assert.Equal("20240615090000", r[dataSet]));
    }

    [Fact]
    public async Task DataSet_EqualsLastModified_WhenOptedOut()
    {
        var modified = new DateTime(2024, 6, 15, 9, 0, 0, DateTimeKind.Utc);
        var options = new Dictionary<string, string?> { ["dataSetFromFileName"] = "false" };
        var (fileDate, dataSet, rows) = await ReadAsync(Csv("sess_20240101.csv", modified, options));

        Assert.NotEmpty(rows);
        // Opted out: the name date is ignored, DataSet_DW == FileDate_DW (the pre-port behavior).
        Assert.All(rows, r => Assert.Equal("20240615090000", r[dataSet]));
        Assert.All(rows, r => Assert.Equal(r[fileDate], r[dataSet]));
    }

    [Fact]
    public async Task DataSet_UsesCustomFormat_FromOptions()
    {
        var modified = new DateTime(2024, 6, 15, 9, 0, 0, DateTimeKind.Utc);
        var options = new Dictionary<string, string?> { ["dataSetFormats"] = "yyMMdd" };
        var (_, dataSet, rows) = await ReadAsync(Csv("bill_240301.csv", modified, options));

        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Equal("20240301000000", r[dataSet]));   // yyMMdd -> 2024-03-01
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
