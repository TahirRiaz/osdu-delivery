using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Core;
using SqlFlow.Core.Model;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Planning;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Storage;
using SqlFlow.Delivery.Tests;
using SqlFlow.Sources;
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
        await ParquetFiles.WriteAsync(buffer, columns, data, pandas is null ? null : new Dictionary<string, string> { [ParquetFiles.PandasMetadataKey] = pandas });
        buffer.Position = 0;
        return await ParquetFiles.ReadShapeAsync(buffer);
    }

    [Fact]
    public async Task A_file_without_pandas_metadata_numbers_its_rows_from_zero()
    {
        var shape = await ShapeAsync(Curves, 4, pandas: null);

        Assert.Equal(new ParquetRowIndex(0, 3, ParquetRowIndexSource.Implicit), shape.RowIndex);
        Assert.Equal(["MD", "GR"], shape.ColumnNames);
        Assert.Equal(4, shape.Rows);
    }

    [Fact]
    public async Task A_range_index_starts_where_its_metadata_says()
    {
        // The index part of what pyarrow writes for a frame whose RangeIndex starts at 5 (to_parquet with index=None).
        var shape = await ShapeAsync(Curves, 4, """{"index_columns": [{"kind": "range", "name": null, "start": 5, "stop": 9, "step": 1}]}""");

        Assert.Equal(new ParquetRowIndex(5, 8, ParquetRowIndexSource.Range), shape.RowIndex);
    }

    [Fact]
    public async Task A_stored_index_column_gives_its_lowest_and_highest_label_and_is_not_a_curve()
    {
        // The index part of what pyarrow writes for a frame with an explicit integer index (to_parquet with index=True).
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

/// <summary>
/// What a dataframe reader needs of a file's pandas entry. A bulk service reads a chunk as a dataframe, so a file the
/// reader raises on is refused as malformed whatever its rows hold, and the delivery says so before it sends one.
/// </summary>
public class ParquetPandasEntryTests
{
    private static readonly (string Name, Type ClrType)[] Curves = [("MD", typeof(double)), ("GR", typeof(double))];

    private static Dictionary<string, string> Entry(string json)
        => new(StringComparer.Ordinal) { [ParquetFiles.PandasMetadataKey] = json };

    [Fact]
    public void A_footer_with_no_pandas_entry_has_nothing_to_refuse()
    {
        Assert.Null(ParquetFiles.PandasDefect(new Dictionary<string, string>(StringComparer.Ordinal)));
        Assert.Null(ParquetFiles.PandasDefect(Entry("  ")));
    }

    [Fact]
    public void A_whole_entry_reads_whether_the_index_is_a_range_or_a_column()
    {
        Assert.Null(ParquetFiles.PandasDefect(PandasMetadata.Range(Curves, 0, 4)));
        Assert.Null(ParquetFiles.PandasDefect(PandasMetadata.Stored(Curves, "MD")));
    }

    [Fact]
    public void An_entry_without_the_column_descriptors_is_refused()
    {
        // pyarrow reads the descriptors before anything else: without them it raises KeyError('columns'), whatever the
        // index says, and the service answers that the data is malformed.
        Assert.Equal(
            "its pandas metadata carries no 'columns' descriptors",
            ParquetFiles.PandasDefect(Entry("""{"index_columns": ["MD"]}""")));
        Assert.Equal(
            "its pandas metadata carries no 'columns' descriptors",
            ParquetFiles.PandasDefect(Entry("""{"index_columns": [{"kind": "range", "name": null, "start": 0, "stop": 4, "step": 1}]}""")));
        Assert.Equal(
            "its pandas metadata carries no 'columns' descriptors",
            ParquetFiles.PandasDefect(Entry("""{"columns": {"MD": "float64"}}""")));
    }

    [Fact]
    public void An_index_column_no_descriptor_names_is_refused()
    {
        // The reader lifts the index out of the frame by its descriptor: without one it raises KeyError on that name.
        var json = PandasMetadata.Json([("GR", typeof(double))], new JsonArray("MD"));

        Assert.Equal("its pandas metadata names 'MD' as the index without describing it in 'columns'", ParquetFiles.PandasDefect(Entry(json)));
    }

    [Fact]
    public void An_entry_that_is_not_an_object_or_not_json_is_refused()
    {
        Assert.Equal("its pandas metadata is not valid JSON", ParquetFiles.PandasDefect(Entry("{ this is not json")));
        Assert.Equal("its pandas metadata is not an object", ParquetFiles.PandasDefect(Entry("[]")));
    }

    [Fact]
    public async Task The_shape_of_a_file_carries_what_its_pandas_entry_lacks()
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["MD"] = 1000.0, ["GR"] = 40.0 },
        };

        using var whole = new MemoryStream();
        await ParquetFiles.WriteAsync(whole, Curves, rows, PandasMetadata.Stored(Curves, "MD"));
        whole.Position = 0;
        Assert.Null((await ParquetFiles.ReadShapeAsync(whole)).PandasDefect);

        using var partial = new MemoryStream();
        await ParquetFiles.WriteAsync(partial, Curves, rows, Entry("""{"index_columns": ["MD"]}"""));
        partial.Position = 0;
        var shape = await ParquetFiles.ReadShapeAsync(partial);

        // The labels still read: what the file cannot do is become a frame.
        Assert.Equal(new ParquetRowIndex(1000, 1000, ParquetRowIndexSource.Column), shape.RowIndex);
        Assert.Equal("its pandas metadata carries no 'columns' descriptors", shape.PandasDefect);
    }
}

public class ParquetAndLocalStoreTests
{
    [Fact]
    public async Task A_written_file_reads_back_with_its_columns_and_rows()
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
            await ParquetFiles.WriteAsync(stream, columns, rows);
        }

        await using var read = File.OpenRead(file);
        var shape = await ParquetFiles.ReadShapeAsync(read);
        Assert.Equal(["s", "d", "l", "b"], shape.ColumnNames);
        Assert.Equal(2, shape.Rows);
        Assert.Equal(8, shape.Values);
    }

    [Fact]
    public void Numbers_are_read_as_the_values_they_were_written_as()
    {
        Assert.Equal(12.3, ParquetFiles.Normalize(12.3f));
        Assert.Equal("12.3", Rendering.SourceRow.Stringify(ParquetFiles.Normalize(12.3f)));
        Assert.Null(ParquetFiles.Normalize(float.NaN));
        Assert.Null(ParquetFiles.Normalize(double.NaN));
        Assert.Equal(double.PositiveInfinity, ParquetFiles.Normalize(double.PositiveInfinity));
        Assert.Equal(1234567890.123456789m, ParquetFiles.Normalize(1234567890.123456789m));
        Assert.Equal((decimal)ulong.MaxValue, ParquetFiles.Normalize(ulong.MaxValue));

        // A decimal's text drops trailing zeros and never a digit, so a whole or short decimal keys exactly as its double did.
        Assert.Equal("12.5", Rendering.SourceRow.Stringify(12.500m));
        Assert.Equal("0", Rendering.SourceRow.Stringify(-0.00m));
        Assert.Equal("0.0000001", Rendering.SourceRow.Stringify(0.0000001m));
    }

    [Fact]
    public async Task A_parquet_decimal_and_float_are_read_exactly_and_a_NaN_as_missing()
    {
        // The values the source normalizer produces are what the renderer sees, whatever the file stored them as.
        Assert.Equal(1234567890.123456789m, SourceValues.Normalize(1234567890.123456789m));
        Assert.Equal(12.3, SourceValues.Normalize(12.3f));
        Assert.Null(SourceValues.Normalize(double.NaN));

        var dir = Samples.NewTempDirectory();
        var file = Path.Combine(dir, "numbers.parquet");
        var columns = new (string, Type)[] { ("m", typeof(decimal)), ("f", typeof(float)), ("d", typeof(double)) };
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["m"] = 1234567890.123456789m, ["f"] = 12.3f, ["d"] = 1.0 },
        };
        await using (var stream = File.Create(file))
        {
            await ParquetFiles.WriteAsync(stream, columns, rows);
        }

        await using var read = File.OpenRead(file);
        var shape = await ParquetFiles.ReadShapeAsync(read);
        Assert.Equal(1, shape.Rows);
        Assert.Equal(["m", "f", "d"], shape.ColumnNames);
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

/// <summary>
/// A record's payload files, as the plan measures them and the protocol streams them: listed from the folder the record's
/// row names, in file-name order, each opened as a fresh stream.
/// </summary>
public class PayloadFileTests
{
    [Fact]
    public async Task The_files_of_a_record_are_listed_in_name_order_and_opened_one_by_one()
    {
        var root = Samples.NewTempDirectory();
        var log = SampleWellLogs.Logs()[0];
        var folder = Path.Combine(root, log.SourceProject, log.LogId);
        await SampleWellLogs.WriteChunkAsync(folder, log);
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "not a payload file");

        var payloads = Samples.Payloads();
        var files = await payloads.ListAsync(folder, "chunk_*.parquet");
        var only = Assert.Single(files);
        Assert.Equal(0, only.Index);
        Assert.EndsWith(SampleWellLogs.ChunkFileName, only.Path, StringComparison.Ordinal);
        Assert.True(only.Size > 0);

        await using var stream = await payloads.OpenAsync(only);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        Assert.Equal(only.Size, buffer.Length);

        // A folder with nothing matching lists as empty rather than failing: the plan turns that into its own reason.
        Assert.Empty(await payloads.ListAsync(folder, "nothing_*.parquet"));
    }

    [Fact]
    public async Task A_payload_source_lists_once_and_re_opens_each_file_for_every_read()
    {
        var root = Samples.NewTempDirectory();
        var log = SampleWellLogs.Logs()[0];
        var folder = Path.Combine(root, log.SourceProject, log.LogId);
        await SampleWellLogs.WriteChunkAsync(folder, log);

        var source = new StoragePayloadSource(Samples.Payloads(), new PayloadLocation(folder, "chunk_*.parquet"));
        var first = await source.ListChunksAsync();
        var again = await source.ListChunksAsync();
        Assert.Same(first, again);

        var file = Assert.Single(first);
        await using (var one = await source.OpenAsync(file))
        {
            Assert.True(one.Length > 0);
        }

        await using var two = await source.OpenAsync(file);
        Assert.True(two.Length > 0);
    }

    [Fact]
    public void A_stored_payload_location_round_trips_through_its_text()
    {
        var location = new PayloadLocation(@"D:\lake\curves\NO_15_9\L-1001", "chunk_*.parquet");
        var parsed = PayloadLocation.Parse(location.ToString());
        Assert.Equal(location.Folder, parsed.Folder);
        Assert.Equal(location.Pattern, parsed.Pattern);

        var blob = new PayloadLocation("abfss://lake@acct.dfs.core.windows.net/curves/L-1001", "*.parquet");
        var back = PayloadLocation.Parse(blob.ToString());
        Assert.Equal(blob.Folder, back.Folder);
        Assert.Equal(blob.Pattern, back.Pattern);

        Assert.Throws<DeliveryException>(() => PayloadLocation.Parse("chunk_00000.parquet"));
    }

    [Fact]
    public void The_payload_signature_is_over_the_files_names_sizes_and_times()
    {
        var modified = new DateTimeOffset(2026, 9, 1, 6, 0, 0, TimeSpan.Zero);
        IReadOnlyList<PayloadFile> files =
        [
            new PayloadFile(0, "/lake/a/chunk_00000.parquet", 120, modified),
            new PayloadFile(1, "/lake/a/chunk_00001.parquet", 90, modified.AddMinutes(5)),
        ];

        var signature = PayloadFiles.Of(files);
        Assert.Equal(2, signature.Count);
        Assert.Equal(modified.AddMinutes(5).UtcDateTime, signature.ModifiedUtc);

        // The same files read from another folder are the same payload: the names, sizes and times are the identity.
        var elsewhere = PayloadFiles.Of(files.Select(f => f with { Path = "/other" + f.Path }).ToList());
        Assert.Equal(signature.Signature, elsewhere.Signature);

        // A rewritten file moves the signature.
        var rewritten = PayloadFiles.Of([files[0] with { Size = 121 }, files[1]]);
        Assert.NotEqual(signature.Signature, rewritten.Signature);
    }
}

/// <summary>
/// The cache store over the module's database, one cache per partition: a merge writes a version only when the cached
/// content moved, every version written becomes current, a version stores only the records that moved against the newest
/// one, every earlier version still reads exactly as it was written, and a version whose records were altered afterwards
/// is refused. Several cache flows of one partition fill the same cache: a record is stored once however many of them
/// capture it, and it leaves only when none of them still finds it.
/// </summary>
[Collection(SqlServerSuite.Name)]
public sealed class OsduCacheStoreTests : IDisposable
{
    private const string Scope = "dev";

    private const string ProjectA = "project-a-cache";

    private const string ProjectB = "project-b-cache";

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly CacheCapture Capture = new(Guid.Parse("0195c9a2-7f30-7c44-9c1e-0aa1b2c3d4e5"), "manual:tester", "${env:OSDU_URL}");

    private static readonly ReferenceTypeSpec WellboreSpec = new()
    {
        Name = "Wellbore",
        EntityType = "master-data--Wellbore",
        Kind = "osdu:wks:master-data--Wellbore:*",
        Fields = [new ReferenceFieldSpec("data.FacilityName")],
    };

    private static readonly ReferenceTypeSpec UnitSpec = new()
    {
        Name = "UnitOfMeasure",
        EntityType = "reference-data--UnitOfMeasure",
        Kind = "osdu:wks:reference-data--UnitOfMeasure:*",
        Fields = [new ReferenceFieldSpec("data.Code"), new ReferenceFieldSpec("data.Name")],
    };

    private readonly OsduTestDatabase _catalog = new();

    public void Dispose() => _catalog.Dispose();

    private static ReferenceType Units(params (string Code, string Name)[] units) => new(
        "UnitOfMeasure", "reference-data--UnitOfMeasure",
        units.Select(u => ReferenceItem.FromText("dev:reference-data--UnitOfMeasure:" + u.Code, new Dictionary<string, string> { ["Code"] = u.Code, ["Name"] = u.Name })));

    private static ReferenceType Wellbores(params string[] keys) => new(
        "Wellbore", "master-data--Wellbore",
        keys.Select(key => ReferenceItem.FromText(WellboreId(key), new Dictionary<string, string> { ["FacilityName"] = "NO " + key })));

    private static string WellboreId(string key) => "dev:master-data--Wellbore:" + key;

    /// <summary>The cache flows whose last capture of the type held the record, in name order.</summary>
    private async Task<List<string>> HoldersAsync(string type, string recordId, string scope = Scope)
    {
        await using var db = _catalog.CreateDbContext();
        return (await db.DeliveryCacheMembers
                .Where(m => m.Scope == scope && m.TypeName == type && m.RecordId == recordId)
                .Select(m => m.FlowName)
                .ToListAsync())
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private async Task<List<string>> CurrentIdsAsync(OsduCacheStore store, string type, string scope = Scope)
    {
        var current = await _catalog.Caches().LoadAsync(scope, (await store.CurrentVersionAsync(scope))!);
        return current!.Type(type)!.Items.Select(i => i.Id).ToList();
    }

    [Fact]
    public async Task A_version_round_trips_becomes_current_and_the_same_content_writes_no_second_version()
    {
        var store = _catalog.Caches();
        Assert.Null(await store.CurrentVersionAsync(Scope));

        var references = TestSchema.References();
        var write = await store.MergeAsync(Scope, ProjectA, references.Types.ToList(), Capture, T0);
        Assert.True(write.Written);
        Assert.Null(write.Previous);
        Assert.Equal("20260101T000000Z", write.Snapshot.Version);

        var saved = Assert.Single(await store.ListVersionsAsync(Scope));
        Assert.Equal(Scope, saved.Scope);
        Assert.True(saved.Current);
        Assert.Equal(1, saved.Sequence);
        Assert.Null(saved.PreviousVersion);
        Assert.Equal(Capture.RunId, saved.RunId);
        Assert.Equal("manual:tester", saved.CapturedBy);
        Assert.Equal("${env:OSDU_URL}", saved.Origin);
        Assert.Equal(ProjectA, saved.FlowName);
        Assert.Equal(3, saved.Items);
        Assert.Equal(["UnitOfMeasure", "Wellbore"], saved.Types.Select(t => t.Name));
        Assert.Equal(write.Snapshot.Version, await store.CurrentVersionAsync(Scope));

        // Read through a store that has never seen it, so the records come from the database and not from memory.
        var back = await _catalog.Caches().LoadAsync(Scope, write.Snapshot.Version);
        Assert.NotNull(back);
        Assert.Equal(references.Normalized().ContentHash(), back.ContentHash());
        Assert.Equal("dev:reference-data--UnitOfMeasure:ft", back.Type("UnitOfMeasure")!.Match("Code", "ft")!.Id);

        // The same content, however much later, writes no version: the label would move every render context for nothing.
        var again = await store.MergeAsync(Scope, ProjectA, references.Types.ToList(), Capture, T0.AddHours(6));
        Assert.False(again.Written);
        Assert.Equal(write.Snapshot.Version, again.Snapshot.Version);
        Assert.Single(await store.ListVersionsAsync(Scope));

        // A partition is its own cache: another partition holds nothing of this one.
        Assert.Null(await store.CurrentVersionAsync("other"));
        Assert.Null(await store.LoadAsync("other", write.Snapshot.Version));
    }

    [Fact]
    public async Task A_version_writes_only_the_records_that_moved_and_every_earlier_version_reads_as_it_was()
    {
        var store = _catalog.Caches();
        var first = await store.MergeAsync(Scope, ProjectA, [Units(("m", "metre"), ("ft", "foot")), Wellbores("A")], Capture, T0);
        var second = await store.MergeAsync(Scope, ProjectA, [Units(("m", "Metre"), ("km", "kilometre")), Wellbores("A")], Capture, T0.AddHours(1));
        Assert.Equal(first.Snapshot.Version, second.Previous!.Version);

        // The first version wrote three rows; the second only the renamed metre and the new kilometre. The wellbore did not
        // move, so its one row covers both versions.
        await using (var db = _catalog.CreateDbContext())
        {
            var rows = db.DeliveryCacheItems.Where(i => i.Scope == Scope).ToList();
            Assert.Equal(5, rows.Count);
            Assert.Equal(2, rows.Count(r => r.ToSequence == 2));
            Assert.Single(rows, r => r.TypeName == "Wellbore" && r.FromSequence == 1 && r.ToSequence == null);
        }

        var reader = _catalog.Caches();
        Assert.Equal(first.Snapshot.ContentHash(), (await reader.LoadAsync(Scope, first.Snapshot.Version))!.ContentHash());
        Assert.Equal(second.Snapshot.ContentHash(), (await reader.LoadAsync(Scope, second.Snapshot.Version))!.ContentHash());
        Assert.DoesNotContain((await reader.LoadAsync(Scope, second.Snapshot.Version))!.Type("UnitOfMeasure")!.Items, i => i.Id.EndsWith(":ft", StringComparison.Ordinal));
        Assert.Equal(second.Snapshot.Version, await reader.CurrentVersionAsync(Scope));
        var versions = await reader.ListVersionsAsync(Scope);
        Assert.Equal([second.Snapshot.Version, first.Snapshot.Version], versions.Select(v => v.Version));
        Assert.Equal([second.Snapshot.Version], versions.Where(v => v.Current).Select(v => v.Version));
        Assert.Equal(first.Snapshot.Version, versions[0].PreviousVersion);
    }

    [Fact]
    public async Task A_second_version_within_the_same_second_takes_its_sequence_into_its_label()
    {
        var store = _catalog.Caches();
        var first = await store.MergeAsync(Scope, ProjectA, [Units(("m", "metre"))], Capture, T0);
        var second = await store.MergeAsync(Scope, ProjectB, [Units(("m", "Metre"))], Capture, T0);

        Assert.Equal("20260101T000000Z", first.Snapshot.Version);
        Assert.Equal("20260101T000000Z-2", second.Snapshot.Version);
        Assert.Equal(second.Snapshot.Version, await store.CurrentVersionAsync(Scope));
        Assert.Equal("metre", (await _catalog.Caches().LoadAsync(Scope, first.Snapshot.Version))!.Type("UnitOfMeasure")!.Items.Single().Fields["Name"].Text);
        Assert.Equal("Metre", (await _catalog.Caches().LoadAsync(Scope, second.Snapshot.Version))!.Type("UnitOfMeasure")!.Items.Single().Fields["Name"].Text);
        Assert.Equal([ProjectB, ProjectA], (await store.ListVersionsAsync(Scope)).Select(v => v.FlowName));
    }

    [Fact]
    public async Task Two_cache_flows_of_one_partition_fill_one_cache_and_a_record_leaves_only_when_no_flow_still_finds_it()
    {
        var store = _catalog.Caches();

        // Project A captures wellbores 1 and 2.
        var first = await store.MergeAsync(Scope, ProjectA, [Wellbores("1", "2")], Capture, T0);
        Assert.True(first.Written);

        // Project B captures wellbores 2 and 3, and a unit. Wellbore 2 is one record of the partition's cache, stored once
        // and held by both flows; wellbore 1 stays although B did not find it, because A holds it.
        var second = await store.MergeAsync(Scope, ProjectB, [Wellbores("2", "3"), Units(("m", "metre"))], Capture, T0.AddHours(1));
        Assert.True(second.Written);
        Assert.Equal([WellboreId("1"), WellboreId("2"), WellboreId("3")], await CurrentIdsAsync(store, "Wellbore"));
        Assert.True(second.Snapshot.HasType("UnitOfMeasure"));
        await using (var db = _catalog.CreateDbContext())
        {
            Assert.Equal(1, await db.DeliveryCacheItems.CountAsync(i => i.Scope == Scope && i.RecordId == WellboreId("2")));
        }

        Assert.Equal([ProjectA, ProjectB], await HoldersAsync("Wellbore", WellboreId("2")));
        Assert.Equal([ProjectB, ProjectA], (await store.ListVersionsAsync(Scope)).Select(v => v.FlowName));

        // A no longer finds wellbore 1, which only A held: it leaves. Wellbore 3 stays, B holds it; the unit A never
        // captures is untouched.
        var third = await store.MergeAsync(Scope, ProjectA, [Wellbores("2")], Capture, T0.AddHours(2));
        Assert.True(third.Written);
        Assert.Equal([WellboreId("2"), WellboreId("3")], await CurrentIdsAsync(store, "Wellbore"));
        Assert.True(third.Snapshot.HasType("UnitOfMeasure"));

        // B no longer finds wellbore 2, which A still holds: the cached content does not move, so no version is written, but
        // B's hold on wellbore 2 is gone.
        var fourth = await store.MergeAsync(Scope, ProjectB, [Wellbores("3"), Units(("m", "metre"))], Capture, T0.AddHours(3));
        Assert.False(fourth.Written);
        Assert.Equal(third.Snapshot.Version, fourth.Snapshot.Version);
        Assert.Equal(3, (await store.ListVersionsAsync(Scope)).Count);
        Assert.Equal([ProjectA], await HoldersAsync("Wellbore", WellboreId("2")));
        Assert.Equal([ProjectB], await HoldersAsync("Wellbore", WellboreId("3")));

        // Now A lets go of wellbore 2 as well, and nothing holds it any more.
        var fifth = await store.MergeAsync(Scope, ProjectA, [Wellbores("3")], Capture, T0.AddHours(4));
        Assert.True(fifth.Written);
        Assert.Equal([WellboreId("3")], await CurrentIdsAsync(store, "Wellbore"));
        Assert.Empty(await HoldersAsync("Wellbore", WellboreId("2")));
        Assert.Equal([ProjectA, ProjectB], await HoldersAsync("Wellbore", WellboreId("3")));
        Assert.Equal(ProjectA, (await store.ListVersionsAsync(Scope))[0].FlowName);
    }

    [Fact]
    public async Task A_type_no_synced_flow_declares_leaves_with_the_next_merge_and_a_type_still_declared_stays()
    {
        var store = _catalog.Caches();
        await _catalog.DeclareCacheAsync(Scope, ProjectA, WellboreSpec, UnitSpec);
        await store.MergeAsync(Scope, ProjectA, [Wellbores("1"), Units(("m", "metre"))], Capture, T0);

        // A capture that does not cover a declared type leaves it where it is.
        var wellboresOnly = await store.MergeAsync(Scope, ProjectA, [Wellbores("1", "2")], Capture, T0.AddHours(1));
        Assert.True(wellboresOnly.Snapshot.HasType("UnitOfMeasure"));

        // Once no synced flow declares the unit type, the next merge takes it out, with every flow's hold on its records.
        await _catalog.DeclareCacheAsync(Scope, ProjectA, WellboreSpec);
        var undeclared = await store.MergeAsync(Scope, ProjectA, [Wellbores("1", "2")], Capture, T0.AddHours(2));
        Assert.True(undeclared.Written);
        Assert.False(undeclared.Snapshot.HasType("UnitOfMeasure"));
        Assert.False((await _catalog.Caches().LoadAsync(Scope, undeclared.Snapshot.Version))!.HasType("UnitOfMeasure"));
        Assert.Empty(await HoldersAsync("UnitOfMeasure", "dev:reference-data--UnitOfMeasure:m"));
    }

    [Fact]
    public async Task Different_partitions_keep_separate_caches()
    {
        var store = _catalog.Caches();
        var here = await store.MergeAsync(Scope, ProjectA, [Units(("m", "metre"))], Capture, T0);
        var there = await store.MergeAsync("other-partition", ProjectA, [Units(("m", "meter"))], Capture, T0);

        // The same instant mints the same label in both, and each is its partition's first version.
        Assert.Equal(here.Snapshot.Version, there.Snapshot.Version);
        Assert.Equal(1, Assert.Single(await store.ListVersionsAsync(Scope)).Sequence);
        Assert.Equal(1, Assert.Single(await store.ListVersionsAsync("other-partition")).Sequence);
        Assert.Equal("metre", (await _catalog.Caches().LoadAsync(Scope, here.Snapshot.Version))!.Type("UnitOfMeasure")!.Items.Single().Fields["Name"].Text);
        Assert.Equal("meter", (await _catalog.Caches().LoadAsync("other-partition", there.Snapshot.Version))!.Type("UnitOfMeasure")!.Items.Single().Fields["Name"].Text);

        // A flow's hold on a record is per partition: letting go of it in one partition leaves the other untouched.
        var emptied = await store.MergeAsync("other-partition", ProjectA, [Units()], Capture, T0.AddHours(1));
        Assert.True(emptied.Written);
        Assert.Empty(emptied.Snapshot.Type("UnitOfMeasure")!.Items);
        Assert.Single((await _catalog.Caches().LoadAsync(Scope, (await store.CurrentVersionAsync(Scope))!))!.Type("UnitOfMeasure")!.Items);
        Assert.Equal([ProjectA], await HoldersAsync("UnitOfMeasure", "dev:reference-data--UnitOfMeasure:m"));
        Assert.Empty(await HoldersAsync("UnitOfMeasure", "dev:reference-data--UnitOfMeasure:m", "other-partition"));
    }

    [Fact]
    public async Task A_type_captured_as_another_entity_type_than_the_cache_holds_writes_nothing()
    {
        var store = _catalog.Caches();
        await store.MergeAsync(Scope, ProjectA, [Units(("m", "metre"))], Capture, T0);

        var other = new ReferenceType("UnitOfMeasure", "reference-data--UnitQuantity", [ReferenceItem.FromText("dev:reference-data--UnitQuantity:L", new Dictionary<string, string> { ["Code"] = "L" })]);
        var ex = await Assert.ThrowsAsync<DeliveryException>(() => store.MergeAsync(Scope, ProjectB, [other], Capture, T0.AddHours(1)));
        Assert.Contains("One name cannot hold both", ex.Message, StringComparison.Ordinal);
        Assert.Single(await store.ListVersionsAsync(Scope));
        Assert.Empty(await HoldersAsync("UnitOfMeasure", "dev:reference-data--UnitQuantity:L"));
    }

    [Fact]
    public async Task A_type_that_matched_no_record_is_still_held_by_its_version()
    {
        var store = _catalog.Caches();
        var write = await store.MergeAsync(Scope, ProjectA, [new ReferenceType("VerticalMeasurementType", "reference-data--VerticalMeasurementType", [])], Capture, T0);

        var back = await _catalog.Caches().LoadAsync(Scope, write.Snapshot.Version);
        Assert.True(back!.HasType("VerticalMeasurementType"));
        Assert.Equal(write.Snapshot.ContentHash(), back.ContentHash());
    }

    [Fact]
    public async Task A_version_whose_records_were_altered_after_it_was_written_is_refused()
    {
        var write = await _catalog.Caches().MergeAsync(Scope, ProjectA, TestSchema.References().Types.ToList(), Capture, T0);
        await using (var db = _catalog.CreateDbContext())
        {
            foreach (var row in db.DeliveryCacheItems.Where(i => i.Scope == Scope && i.TypeName == "UnitOfMeasure").ToList())
            {
                row.FieldsJson = row.FieldsJson.Replace("metre", "meter", StringComparison.Ordinal);
            }

            await db.SaveChangesAsync();
        }

        var ex = await Assert.ThrowsAsync<DeliveryException>(() => _catalog.Caches().LoadAsync(Scope, write.Snapshot.Version));
        Assert.Contains("altered after the version was written", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_type_holding_one_record_twice_is_refused_before_anything_is_written()
    {
        var store = _catalog.Caches();
        var twice = ReferenceItem.FromText("dev:reference-data--UnitOfMeasure:m", new Dictionary<string, string> { ["Code"] = "m" });

        var ex = await Assert.ThrowsAsync<DeliveryException>(() => store.MergeAsync(
            Scope, ProjectA, [new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure", [twice, twice])], Capture, T0));
        Assert.Contains("more than once", ex.Message, StringComparison.Ordinal);
        Assert.Empty(await store.ListVersionsAsync(Scope));
        Assert.Empty(await HoldersAsync("UnitOfMeasure", "dev:reference-data--UnitOfMeasure:m"));
    }

    [Fact]
    public async Task The_sample_cache_imports_into_its_partition_as_its_cache_flow_declares_it()
    {
        var store = _catalog.Caches();
        var version = await Samples.ImportSampleCacheAsync(store);
        Assert.Equal("20260908T212727Z", version.Version);
        Assert.Equal(version.Version, await store.CurrentVersionAsync(Samples.SampleCacheScope));
        // The lookups flow wrote its tables first, and the reference data import added to them: one partition, one cache.
        Assert.Equal(
            [Samples.SampleCacheFlowName, Samples.SampleLookupsFlowName],
            (await store.ListVersionsAsync(Samples.SampleCacheScope)).Select(v => v.FlowName));
        Assert.True(version.HasType("UnitOfMeasure"));
        Assert.True(version.HasType("VerticalMeasurementType"));
        // petrodb-api's translations, loaded from the files in cache/data: the curve unit map, the depth unit map, and the
        // curve dictionary giving each mnemonic the codes of the records it is filed under.
        Assert.Equal("source_unit", version.Type("RecallUnits")!.Key);
        Assert.Equal("v/v", version.Type("RecallUnits")!.Value(version.Type("RecallUnits")!.Match("source_unit", "V/V")!, "osdu_unit")!.Text);
        Assert.Equal("ft", version.Type("RecallDepthUnits")!.Value(version.Type("RecallDepthUnits")!.Match("source_unit", "FEET")!, "osdu_unit")!.Text);
        Assert.Equal("Gamma%20Ray", version.Type("CurveDictionary")!.Value(version.Type("CurveDictionary")!.Match("mnemonic", "GR")!, "log_curve_family_id")!.Text);

        // Wellbores are searched for on the platform rather than captured, so the sample cache holds none.
        Assert.False(version.HasType("Wellbore"));
    }
}
