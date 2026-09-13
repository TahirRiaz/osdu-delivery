using SqlFlow.Core;
using SqlFlow.Core.Model;
using SqlFlow.Core.Storage;
using SqlFlow.Delivery.Drops;
using SqlFlow.Delivery.SampleDrop;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Storage;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.Delivery.Tests;

public class AzureBlobLocationTests
{
    [Theory]
    [InlineData("abfss://lake@acct.dfs.core.windows.net/osdu-prepare/x", "acct", "lake", "osdu-prepare/x", "dfs", true)]
    [InlineData("https://acct.blob.core.windows.net/lake/osdu-prepare/x/", "acct", "lake", "osdu-prepare/x", "blob", false)]
    [InlineData("wasbs://lake@acct.blob.core.windows.net", "acct", "lake", "", "blob", true)]
    public void Parses_the_supported_forms(string uri, string account, string container, string path, string kind, bool inAuthority)
    {
        var loc = AzureBlobLocation.Parse(uri);
        Assert.Equal(account, loc.Account);
        Assert.Equal(container, loc.Container);
        Assert.Equal(path, loc.BlobPath);
        Assert.Equal(kind, loc.EndpointKind);
        Assert.Equal(inAuthority, loc.ContainerInAuthority);
        Assert.True(AzureBlobLocation.IsAzureStorageUri(uri));
        Assert.EndsWith("/osdu-prepare/x/a.parquet", loc.UriFor("osdu-prepare/x/a.parquet"), StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_non_azure_uris_and_local_paths()
    {
        Assert.False(AzureBlobLocation.IsAzureStorageUri("https://example.org/x"));
        Assert.False(AzureBlobLocation.IsAzureStorageUri(@"D:\drops\x"));
        Assert.Throws<SqlFlowException>(() => AzureBlobLocation.Parse("https://acct.blob.core.windows.net"));
        Assert.Throws<SqlFlowException>(() => AzureBlobLocation.Parse("s3://bucket/x"));
    }
}

/// <summary>
/// The row labels a dataframe reader gives a parquet file, read from its footer. The wellbore DDMS aggregates a
/// session's chunks by these labels, so they decide whether two chunks add rows or replace each other's.
/// </summary>
public class ParquetRowLabelTests
{
    private static readonly (string, Type)[] Curves = [("MD", typeof(double)), ("GR", typeof(double))];

    private static async Task<ParquetShape> ShapeAsync(IReadOnlyList<(string, Type)> columns, int rows, string? pandas, long firstLabel = 0)
    {
        var data = Enumerable.Range(0, rows)
            .Select(i => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["MD"] = 1000.0 + i,
                ["GR"] = 40.0 + i,
                ["__index_level_0__"] = firstLabel + i,
            })
            .ToList();
        using var buffer = new MemoryStream();
        await ParquetScopeReader.WriteAsync(buffer, columns, data, pandas is null ? null : new Dictionary<string, string> { [ParquetScopeReader.PandasMetadataKey] = pandas });
        buffer.Position = 0;
        return await ParquetScopeReader.ReadShapeAsync(buffer);
    }

    [Fact]
    public async Task A_file_without_pandas_metadata_numbers_its_rows_from_zero()
    {
        var shape = await ShapeAsync(Curves, 4, pandas: null);

        Assert.Equal(new ParquetRowIndex(0, 3, ParquetRowIndexSource.Implicit), shape.RowIndex);
        Assert.Equal(["MD", "GR"], shape.ColumnNames);
    }

    [Fact]
    public async Task A_range_index_starts_where_its_metadata_says()
    {
        // What pyarrow writes for a frame whose RangeIndex starts at 5 (to_parquet with index=None).
        var shape = await ShapeAsync(Curves, 4, """{"index_columns": [{"kind": "range", "name": null, "start": 5, "stop": 9, "step": 1}]}""");

        Assert.Equal(new ParquetRowIndex(5, 8, ParquetRowIndexSource.Range), shape.RowIndex);
    }

    [Fact]
    public async Task A_stored_index_column_gives_its_lowest_and_highest_label_and_is_not_a_curve()
    {
        // What pyarrow writes for a frame with an explicit integer index (to_parquet with index=True).
        var columns = new (string, Type)[] { ("MD", typeof(double)), ("GR", typeof(double)), ("__index_level_0__", typeof(long)) };
        var shape = await ShapeAsync(columns, 4, """{"index_columns": ["__index_level_0__"]}""", firstLabel: 5);

        Assert.Equal(new ParquetRowIndex(5, 8, ParquetRowIndexSource.Column), shape.RowIndex);
        Assert.Equal(["MD", "GR"], shape.ColumnNames);
        Assert.Equal(3, shape.Columns);
    }

    [Fact]
    public async Task Labels_the_footer_cannot_tell_are_left_unknown()
    {
        Assert.Null((await ShapeAsync(Curves, 4, """{"index_columns": ["level_0", "level_1"]}""")).RowIndex);
        Assert.Null((await ShapeAsync(Curves, 4, """{"index_columns": ["__index_level_0__"]}""")).RowIndex);
        Assert.Null((await ShapeAsync(Curves, 4, "{ this is not json")).RowIndex);
    }
}

public class ParquetAndLocalStoreTests
{
    [Fact]
    public async Task Parquet_round_trips_scalar_types_and_prunes_columns()
    {
        var dir = Samples.NewTempDirectory();
        var file = Path.Combine(dir, "rows.parquet");
        var columns = new (string, Type)[] { ("s", typeof(string)), ("d", typeof(double)), ("l", typeof(long)), ("b", typeof(bool)) };
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["s"] = "a", ["d"] = 1.5, ["l"] = 7L, ["b"] = true },
            new Dictionary<string, object?> { ["s"] = null, ["d"] = null, ["l"] = -1L, ["b"] = false },
        };
        await using (var stream = File.Create(file))
        {
            await ParquetScopeReader.WriteAsync(stream, columns, rows);
        }

        await using var read = File.OpenRead(file);
        Assert.Equal(["s", "d", "l", "b"], await ParquetScopeReader.ReadColumnsAsync(read));
        read.Position = 0;
        var back = new List<Rendering.SourceRow>();
        await foreach (var row in ParquetScopeReader.ReadRowsAsync(read, new HashSet<string> { "s", "d", "l" }))
        {
            back.Add(row);
        }

        Assert.Equal(2, back.Count);
        Assert.Equal("a", back[0].GetString("s"));
        Assert.Equal(1.5, back[0].Get("d"));
        Assert.Equal(7L, back[0].Get("l"));
        Assert.False(back[0].Has("b"));
        Assert.Null(back[1].Get("s"));
        Assert.Equal("-1", back[1].GetString("l"));
    }

    [Fact]
    public async Task Local_store_lists_by_glob_and_writes_atomically()
    {
        var dir = Samples.NewTempDirectory();
        var store = new LocalFileStore();
        var writer = new LocalFileWriter();
        using (var content = new MemoryStream("hello"u8.ToArray()))
        {
            await writer.WriteAsync(Path.Combine(dir, "sub", "a.txt"), content);
        }

        File.WriteAllText(Path.Combine(dir, "sub", "b.parquet"), "x");
        var parquet = await store.ListAsync(Path.Combine(dir, "sub"), new FileDiscovery { Pattern = "*.parquet" });
        Assert.Single(parquet);
        Assert.Equal("b.parquet", parquet[0].Name);
        var all = await store.ListAsync(dir, new FileDiscovery { Pattern = "*", Recursive = true });
        Assert.Equal(2, all.Count);
        Assert.False(File.Exists(Path.Combine(dir, "sub", "a.txt.tmp")));
        Assert.True(store.CanHandle(dir));
        Assert.False(store.CanHandle("abfss://c@a.dfs.core.windows.net/x"));
    }
}

public class DropReaderTests
{
    [Fact]
    public async Task Reads_records_with_child_scopes_and_payload_chunks()
    {
        var dir = Samples.NewTempDirectory();
        var records = SampleDropBuilder.DefaultRecords("STAT_COMP");
        await SampleDropBuilder.WriteAsync(dir, "STAT_COMP", records, Guid.NewGuid(), 1);

        var reader = new DropReader(Samples.Stores());
        var drop = await reader.OpenAsync(dir, "manifest.json");
        Assert.Equal(3, drop.Manifest.RecordCount);
        Assert.Equal("curves", drop.Manifest.Scopes.Keys.Single(k => k != "record"));

        var read = new List<Rendering.SourceRecord>();
        await foreach (var record in reader.ReadRecordsAsync(drop))
        {
            read.Add(record);
        }

        Assert.Equal(3, read.Count);
        var first = read.Single(r => r.Row.GetString("log_id") == "L-1001");
        Assert.Equal(records[0].Key.Value, first.DeclaredDeliveryKey);
        Assert.Equal([SampleDropBuilder.IndexCurveId, "GR", "RHOB"], first.ScopeRows("curves").Select(c => c.GetString("curve_id")));
        Assert.Equal(1000d, first.Row.Get("index_min"));

        var chunks = await reader.ListPayloadChunksAsync(drop, "curves", records[0].Key.Value);
        Assert.Single(chunks);
        Assert.EndsWith("chunk_00000.parquet", chunks[0].Path, StringComparison.Ordinal);
        Assert.Equal(File.GetLastWriteTimeUtc(chunks[0].Path), chunks[0].Modified?.UtcDateTime);
        await using var stream = await reader.OpenChunkAsync(chunks[0]);
        Assert.True(stream.Length > 0);

        var none = await reader.ListPayloadChunksAsync(drop, "curves", Guid.NewGuid());
        Assert.Empty(none);
    }

    [Fact]
    public async Task Missing_manifest_is_a_validation_error()
    {
        var reader = new DropReader(Samples.Stores());
        await Assert.ThrowsAsync<FlowValidationException>(() => reader.OpenAsync(Samples.NewTempDirectory(), "manifest.json"));
    }

    [Fact]
    public void Payload_hash_is_over_the_logical_grid()
    {
        var columns = new[] { "MD", "GR" };
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["MD"] = 1000.0, ["GR"] = 45.2 },
            new Dictionary<string, object?> { ["MD"] = 1000.5, ["GR"] = null },
        };
        var a = SampleDropBuilder.HashGrid(columns, rows);
        var same = SampleDropBuilder.HashGrid(columns, rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(r)).ToList());
        Assert.Equal(a, same);
        rows[1] = new Dictionary<string, object?> { ["MD"] = 1000.5, ["GR"] = 0.0 };
        Assert.NotEqual(a, SampleDropBuilder.HashGrid(columns, rows));
    }
}

public class FileSnapshotStoreTests
{
    [Fact]
    public async Task Reference_snapshots_round_trip_and_are_immutable()
    {
        var root = Samples.NewTempDirectory();
        var store = new FileSnapshotStore(root, Samples.Stores());

        Assert.Null(await store.CurrentReferenceVersionAsync());
        var references = TestSchema.References();
        await store.SaveReferencesAsync(references, makeCurrent: true);
        Assert.Equal("refs-1", await store.CurrentReferenceVersionAsync());
        var back = await store.LoadReferencesAsync("refs-1");
        Assert.NotNull(back);
        Assert.Equal(references.ContentHash(), back!.ContentHash());
        Assert.Equal("dev:reference-data--UnitOfMeasure:ft", back.Type("UnitOfMeasure")!.Match("Code", "ft")!.Id);
        Assert.Equal(["refs-1"], await store.ListReferenceVersionsAsync());
        await Assert.ThrowsAsync<DeliveryException>(() => store.SaveReferencesAsync(references, makeCurrent: false));
    }

    [Fact]
    public async Task Edited_reference_snapshots_are_detected()
    {
        var root = Samples.NewTempDirectory();
        var store = new FileSnapshotStore(root, Samples.Stores());
        await store.SaveReferencesAsync(TestSchema.References(), makeCurrent: true);
        var file = Path.Combine(root, "references", "refs-1", "UnitOfMeasure.json");
        File.WriteAllText(file, File.ReadAllText(file).Replace("metre", "meter", StringComparison.Ordinal));
        await Assert.ThrowsAsync<DeliveryException>(() => store.LoadReferencesAsync("refs-1"));
    }

    [Fact]
    public async Task The_sample_snapshot_store_loads()
    {
        var store = new FileSnapshotStore(Samples.Snapshots, Samples.Stores());
        var current = await store.CurrentReferenceVersionAsync();
        Assert.NotNull(current);
        var references = await store.LoadReferencesAsync(current!);
        Assert.True(references!.HasType("UnitOfMeasure"));
    }
}
