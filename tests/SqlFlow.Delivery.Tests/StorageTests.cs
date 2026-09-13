using SqlFlow.Core;
using SqlFlow.Core.Model;
using SqlFlow.Core.Storage;
using SqlFlow.Delivery.Catalog;
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

/// <summary>
/// The cache store over the catalog: a version round-trips whole and is never rewritten, a version writes only the records
/// that moved against the newest one, every earlier version still reads exactly as it was written, and a version whose
/// records were altered afterwards is refused.
/// </summary>
public sealed class CatalogCacheStoreTests : IDisposable
{
    private const string Cache = "units";

    private static readonly CacheCapture Capture = new(Guid.Parse("0195c9a2-7f30-7c44-9c1e-0aa1b2c3d4e5"), "manual:tester", "${env:PETRODB_URL}");

    private readonly SqliteCatalog _catalog = new();

    public void Dispose() => _catalog.Dispose();

    private static ReferenceSnapshot Units(string version, params (string Code, string Name)[] units) => new(
        version,
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        [
            new ReferenceType(
                "UnitOfMeasure", "reference-data--UnitOfMeasure",
                units.Select(u => ReferenceItem.FromText("dev:reference-data--UnitOfMeasure:" + u.Code, new Dictionary<string, string> { ["Code"] = u.Code, ["Name"] = u.Name }))),
            new ReferenceType(
                "Wellbore", "master-data--Wellbore",
                [ReferenceItem.FromText("dev:master-data--Wellbore:abc", new Dictionary<string, string> { ["FacilityName"] = "NO 1/1-A" })]),
        ]);

    [Fact]
    public async Task A_version_round_trips_is_made_current_and_is_never_rewritten()
    {
        var store = _catalog.Caches();
        Assert.Null(await store.CurrentVersionAsync(Cache));

        var references = TestSchema.References();
        var saved = await store.SaveAsync(Cache, references, Capture, makeCurrent: true);
        Assert.True(saved.Current);
        Assert.Equal(1, saved.Sequence);
        Assert.Null(saved.PreviousVersion);
        Assert.Equal(Capture.RunId, saved.RunId);
        Assert.Equal("manual:tester", saved.CapturedBy);
        Assert.Equal(3, saved.Items);
        Assert.Equal(["UnitOfMeasure", "Wellbore"], saved.Types.Select(t => t.Name));
        Assert.Equal("refs-1", await store.CurrentVersionAsync(Cache));

        // Read through a store that has never seen it, so the records come from the catalog and not from memory.
        var back = await _catalog.Caches().LoadAsync(Cache, "refs-1");
        Assert.NotNull(back);
        Assert.Equal(references.Normalized().ContentHash(), back.ContentHash());
        Assert.Equal("dev:reference-data--UnitOfMeasure:ft", back.Type("UnitOfMeasure")!.Match("Code", "ft")!.Id);
        Assert.Single(await store.ListVersionsAsync(Cache));

        await Assert.ThrowsAsync<DeliveryException>(() => store.SaveAsync(Cache, references, Capture, makeCurrent: false));

        // A cache is its name: another cache holds nothing of this one.
        Assert.Null(await store.CurrentVersionAsync("other"));
        Assert.Null(await store.LoadAsync("other", "refs-1"));
    }

    [Fact]
    public async Task A_version_writes_only_the_records_that_moved_and_every_earlier_version_reads_as_it_was()
    {
        var store = _catalog.Caches();
        var first = Units("v1", ("m", "metre"), ("ft", "foot"));
        var second = Units("v2", ("m", "Metre"), ("km", "kilometre"));
        await store.SaveAsync(Cache, first, Capture, makeCurrent: true);
        var saved = await store.SaveAsync(Cache, second, Capture, makeCurrent: true);
        Assert.Equal("v1", saved.PreviousVersion);

        // The first version wrote three rows; the second only the renamed metre and the new kilometre. The wellbore did not
        // move, so its one row covers both versions.
        await using (var db = _catalog.CreateDbContext())
        {
            var rows = db.DeliveryCacheItems.Where(i => i.CacheName == Cache).ToList();
            Assert.Equal(5, rows.Count);
            Assert.Equal(2, rows.Count(r => r.ToSequence == 2));
            Assert.Single(rows, r => r.TypeName == "Wellbore" && r.FromSequence == 1 && r.ToSequence == null);
        }

        var reader = _catalog.Caches();
        Assert.Equal(first.Normalized().ContentHash(), (await reader.LoadAsync(Cache, "v1"))!.ContentHash());
        Assert.Equal(second.Normalized().ContentHash(), (await reader.LoadAsync(Cache, "v2"))!.ContentHash());
        Assert.Equal("v2", await reader.CurrentVersionAsync(Cache));
        Assert.Equal(["v2", "v1"], (await reader.ListVersionsAsync(Cache)).Select(v => v.Version));
    }

    [Fact]
    public async Task A_version_not_made_current_leaves_the_current_one_where_it_is()
    {
        var store = _catalog.Caches();
        await store.SaveAsync(Cache, Units("v1", ("m", "metre")), Capture, makeCurrent: true);
        var candidate = await store.SaveAsync(Cache, Units("v2", ("m", "meter")), Capture, makeCurrent: false);
        Assert.False(candidate.Current);
        Assert.Equal("v1", await store.CurrentVersionAsync(Cache));

        await store.SaveAsync(Cache, Units("v3", ("m", "Metre")), Capture, makeCurrent: true);
        Assert.Equal("v3", await store.CurrentVersionAsync(Cache));
        Assert.Equal(["v3"], (await store.ListVersionsAsync(Cache)).Where(v => v.Current).Select(v => v.Version));
        Assert.Equal("meter", (await _catalog.Caches().LoadAsync(Cache, "v2"))!.Type("UnitOfMeasure")!.Items.Single().Fields["Name"].Text);
    }

    [Fact]
    public async Task A_type_that_matched_no_record_is_still_held_by_its_version()
    {
        var store = _catalog.Caches();
        var snapshot = new ReferenceSnapshot("v1", DateTimeOffset.UnixEpoch, [new ReferenceType("VerticalMeasurementType", "reference-data--VerticalMeasurementType", [])]);
        await store.SaveAsync(Cache, snapshot, Capture, makeCurrent: true);

        var back = await _catalog.Caches().LoadAsync(Cache, "v1");
        Assert.True(back!.HasType("VerticalMeasurementType"));
        Assert.Equal(snapshot.ContentHash(), back.ContentHash());
    }

    [Fact]
    public async Task A_version_whose_records_were_altered_after_it_was_written_is_refused()
    {
        await _catalog.Caches().SaveAsync(Cache, TestSchema.References(), Capture, makeCurrent: true);
        await using (var db = _catalog.CreateDbContext())
        {
            foreach (var row in db.DeliveryCacheItems.Where(i => i.CacheName == Cache && i.TypeName == "UnitOfMeasure").ToList())
            {
                row.FieldsJson = row.FieldsJson.Replace("metre", "meter", StringComparison.Ordinal);
            }

            await db.SaveChangesAsync();
        }

        var ex = await Assert.ThrowsAsync<DeliveryException>(() => _catalog.Caches().LoadAsync(Cache, "refs-1"));
        Assert.Contains("altered after the version was written", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_type_holding_one_record_twice_is_refused_before_anything_is_written()
    {
        var store = _catalog.Caches();
        var twice = ReferenceItem.FromText("dev:reference-data--UnitOfMeasure:m", new Dictionary<string, string> { ["Code"] = "m" });
        var snapshot = new ReferenceSnapshot("v1", DateTimeOffset.UnixEpoch, [new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure", [twice, twice])]);

        var ex = await Assert.ThrowsAsync<DeliveryException>(() => store.SaveAsync(Cache, snapshot, Capture, makeCurrent: true));
        Assert.Contains("more than once", ex.Message, StringComparison.Ordinal);
        Assert.Empty(await store.ListVersionsAsync(Cache));
    }

    [Fact]
    public async Task The_sample_cache_imports_as_its_cache_flow_declares_it()
    {
        var store = _catalog.Caches();
        var version = await Samples.ImportSampleCacheAsync(store);
        Assert.Equal("20260908T212727Z", version.Version);
        Assert.Equal(version.Version, await store.CurrentVersionAsync(Samples.SampleCacheName));
        Assert.True(version.HasType("UnitOfMeasure"));
        Assert.True(version.HasType("Wellbore"));
    }
}
