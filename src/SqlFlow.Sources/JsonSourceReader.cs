using System.Runtime.CompilerServices;
using System.Text.Json;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Model;
using SqlFlow.Sources.Json;

namespace SqlFlow.Sources;

/// <summary>
/// Reads JSON / NDJSON files by flattening each record against a declarative, path-based configuration
/// (the modern replacement for the original <c>JsonToDataTableCode</c>). The flattened columns and the
/// streamed rows both come from one deterministic flatten, so a file's column order is stable between the
/// schema pass and the data pass. Every value is a raw string; the existing inference step types them
/// afterward. Arrays follow <see cref="JsonFlattenConfig.ArrayHandling"/> (explosion is a later addition).
/// File selection, schema evolution across files, provenance / key columns, streaming, and the post-load
/// lifecycle all come from <see cref="FileSourceReaderBase"/>, keeping JSON on the same code path as CSV
/// and XLS.
/// </summary>
public sealed class JsonSourceReader : FileSourceReaderBase, IFlattenIntrospector
{
    public JsonSourceReader(IFileLifecycle lifecycle, IEnumerable<IFileStore> fileStores)
        : base(lifecycle, fileStores)
    {
    }

    public override bool CanHandle(string sourceType)
        => sourceType is not null
            && (sourceType.Equals("json", StringComparison.OrdinalIgnoreCase)
                || sourceType.Equals("jsonl", StringComparison.OrdinalIgnoreCase)
                || sourceType.Equals("ndjson", StringComparison.OrdinalIgnoreCase));

    protected override string DefaultFilePattern => "*.json";

    protected override FileSourceOptions ReadOptions(SourceSpec source)
    {
        var meta = PreIngestionJsn.FromSource(source);
        return new FileSourceOptions
        {
            SrcPath = meta.SrcPath,
            SrcFile = meta.SrcFile,
            SrcPathMask = meta.SrcPathMask,
            SearchSubDirectories = meta.SearchSubDirectories,
            InitFromFileDate = meta.InitFromFileDate,
            InitToFileDate = meta.InitToFileDate,
            IncrementalAfterDate = meta.IncrementalAfterDate,
            FileDate = FileDateSpec.FromOptions(source.Options),
            CopyToPath = meta.CopyToPath,
            ZipToPath = meta.ZipToPath,
            SrcDeleteIngested = meta.SrcDeleteIngested,
            SrcDeleteAtPath = meta.SrcDeleteAtPath,
            ShowPathWithFileName = meta.ShowPathWithFileName,
            IncludeFileName = meta.IncludeFileName,
            IncludeFileDate = meta.IncludeFileDate,
            IncludeFileRowDate = meta.IncludeFileRowDate,
            IncludeFileSize = meta.IncludeFileSize,
            IncludeDataSet = meta.IncludeDataSet,
            IncludeRowNumber = meta.IncludeRowNumber,
            IncludeFileLineNumber = meta.IncludeFileLineNumber,
            IncludeHashKey = meta.IncludeHashKey,
            HashKeyColumns = meta.HashKeyColumns,
            HashKeyType = meta.HashKeyType,
            IncludeConcatKey = meta.IncludeConcatKey,
            ConcatKeyColumns = meta.ConcatKeyColumns,
            ConcatKeySeparator = meta.ConcatKeySeparator,
        };
    }

    protected override async Task<IReadOnlyList<string>> ReadColumnNamesAsync(IFileStore store, FileRef file, SourceSpec source, CancellationToken ct)
    {
        var meta = PreIngestionJsn.FromSource(source);
        var flattener = new JsonPathFlattener(BuildFlattenConfig(meta));
        var data = await ReadAllBytesAsync(store, file, ct).ConfigureAwait(false);
        return DiscoverColumns(JsonRecordReader.ReadRecords(data, source.Type, meta.RootPath, file.Name), flattener);
    }

    protected override async IAsyncEnumerable<FileLine> ReadLinesAsync(
        IFileStore store, FileRef file, SourceSpec source, [EnumeratorCancellation] CancellationToken ct)
    {
        var meta = PreIngestionJsn.FromSource(source);
        var flattener = new JsonPathFlattener(BuildFlattenConfig(meta));
        var data = await ReadAllBytesAsync(store, file, ct).ConfigureAwait(false);

        // A file's column order is fixed by the same discovery the schema pass uses, so the cells below
        // line up positionally with ReadColumnNamesAsync. Records are re-read (cheaply, from the in-memory
        // bytes) for the data pass.
        var columns = DiscoverColumns(JsonRecordReader.ReadRecords(data, source.Type, meta.RootPath, file.Name), flattener);
        // Case-insensitive, matching SQL Server collation and the base pipeline's column union, so the
        // positional projection cannot desync on keys that differ only in case.
        var index = new Dictionary<string, int>(columns.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < columns.Count; i++)
        {
            index[columns[i]] = i;
        }

        // One source record can yield several output rows when explode paths multiply it; RowNumber_DW is
        // per output row, while the (optional) FileLineNumber stays the source record's ordinal.
        long recordOrdinal = 0;
        long outputRow = 0;
        foreach (var record in JsonRecordReader.ReadRecords(data, source.Type, meta.RootPath, file.Name))
        {
            ct.ThrowIfCancellationRequested();
            recordOrdinal++;

            foreach (var rowPairs in flattener.FlattenRows(record))
            {
                var cells = new string?[columns.Count];
                foreach (var (name, value) in rowPairs)
                {
                    if (index.TryGetValue(name, out var target))
                    {
                        cells[target] = value;
                    }
                }

                outputRow++;
                yield return new FileLine(cells, recordOrdinal, outputRow);
            }
        }
    }

    /// <summary>
    /// Scans the source files (selected by the same filters a load would apply) and reports their JSONPath
    /// structure: the union of leaf paths, the distinct record shapes, and how many files and records were
    /// seen. Powers the <c>discover</c> command, which authors a flatten configuration without a load.
    /// </summary>
    public async Task<JsonDiscoveryResult> DiscoverAsync(SourceSpec source, int maxFiles, int maxRecords, int maxDepth, CancellationToken ct = default)
    {
        var depth = maxDepth < 1 ? 10 : maxDepth;
        var (filesScanned, perRecord) = await ScanRecordsAsync(
            source, maxFiles, maxRecords, record => JsonStructureDiscovery.ExtractPaths(record, depth), ct).ConfigureAwait(false);
        return JsonStructureDiscovery.Aggregate(perRecord) with { FilesScanned = filesScanned };
    }

    /// <summary>
    /// Scans the source files and reports every addressable JSONPath, including the object and array
    /// container paths (the valid targets for rootPath / jsonPaths / excludePaths), with each path's kind
    /// and how many records contained it. Powers the <c>paths</c> command.
    /// </summary>
    public async Task<JsonPathInventory> InventoryAsync(SourceSpec source, int maxFiles, int maxRecords, int maxDepth, CancellationToken ct = default)
    {
        var depth = maxDepth < 1 ? 10 : maxDepth;
        var (filesScanned, perRecord) = await ScanRecordsAsync(
            source, maxFiles, maxRecords, record => JsonPathInventoryBuilder.ExtractTypedPaths(record, depth), ct).ConfigureAwait(false);
        return JsonPathInventoryBuilder.Build(perRecord) with { FilesScanned = filesScanned };
    }

    /// <summary>
    /// Reads records from the selected files (bounded by <paramref name="maxFiles"/> and
    /// <paramref name="maxRecords"/>), projecting each record eagerly while it is still valid. The shared
    /// scan behind <see cref="DiscoverAsync"/> and <see cref="InventoryAsync"/>.
    /// </summary>
    private async Task<(int FilesScanned, List<T> Items)> ScanRecordsAsync<T>(
        SourceSpec source, int maxFiles, int maxRecords, Func<JsonElement, T> project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        var meta = PreIngestionJsn.FromSource(source);
        var (store, files) = await ResolveFilesAsync(source, ct).ConfigureAwait(false);

        var items = new List<T>();
        var fileLimit = maxFiles > 0 ? maxFiles : int.MaxValue;
        var filesScanned = 0;
        var recordsScanned = 0;

        foreach (var file in files)
        {
            if (filesScanned >= fileLimit || (maxRecords > 0 && recordsScanned >= maxRecords))
            {
                break;
            }

            filesScanned++;
            var data = await ReadAllBytesAsync(store, file, ct).ConfigureAwait(false);

            foreach (var record in JsonRecordReader.ReadRecords(data, source.Type, meta.RootPath, file.Name))
            {
                if (maxRecords > 0 && recordsScanned >= maxRecords)
                {
                    break;
                }

                recordsScanned++;
                items.Add(project(record));
            }
        }

        return (filesScanned, items);
    }

    /// <summary>Scans the source and reports its JSONPath structure, the derived flatten formula, and the options to run it.</summary>
    public async Task<FlattenIntrospection> IntrospectAsync(SourceSpec source, int maxFiles, int maxRecords, int maxDepth, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var meta = PreIngestionJsn.FromSource(source);
        var config = BuildFlattenConfig(meta);

        var inventory = await InventoryAsync(source, maxFiles, maxRecords, maxDepth, ct).ConfigureAwait(false);
        var formula = JsonFlattenFormulaBuilder.Build(inventory, config);

        var paths = inventory.Paths
            .Select(p => new SchemaPath(p.Path, MapKind(p.Kind), p.RecordCount, p.Kind == JsonNodeKind.Object ? string.Empty : config.ColumnName(p.Path)))
            .ToList();
        var columns = formula.Columns.Select(c => new SchemaColumn(c.Name, c.SourcePath, c.IsJsonText)).ToList();

        return new FlattenIntrospection(
            "json",
            new SchemaInventory(inventory.FilesScanned, inventory.RecordsScanned, paths),
            new SchemaFormula(columns, formula.CollisionMappings),
            BuildOptions(meta));
    }

    private static SchemaPathKind MapKind(JsonNodeKind kind) => kind switch
    {
        JsonNodeKind.Object => SchemaPathKind.Container,
        JsonNodeKind.Array => SchemaPathKind.Repeating,
        _ => SchemaPathKind.Value,
    };

    private static IReadOnlyList<KeyValuePair<string, string>> BuildOptions(PreIngestionJsn meta)
    {
        var options = new List<KeyValuePair<string, string>>
        {
            new("rootPath", string.IsNullOrWhiteSpace(meta.RootPath) ? "$" : meta.RootPath),
            new("separator", meta.Separator),
            new("arrayHandling", meta.ArrayHandling),
        };

        AddIfSet(options, "includePaths", meta.IncludePaths);
        AddIfSet(options, "excludePaths", meta.ExcludePaths);
        AddIfSet(options, "jsonPaths", meta.JsonPaths);
        AddIfSet(options, "explodePaths", meta.ExplodePaths);
        AddIfSet(options, "pathAliases", meta.PathAliases);
        AddIfSet(options, "columnMappings", meta.ColumnMappings);
        return options;
    }

    private static void AddIfSet(List<KeyValuePair<string, string>> options, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            options.Add(new KeyValuePair<string, string>(key, value));
        }
    }

    /// <summary>Builds the flatten configuration from the bound metadata, validating its option values.</summary>
    public static JsonFlattenConfig BuildFlattenConfig(PreIngestionJsn meta)
    {
        if (meta.MaxDepth < 1)
        {
            throw new SqlFlow.Core.SqlFlowException($"Invalid maxDepth '{meta.MaxDepth}'. Use a positive integer.");
        }

        return new JsonFlattenConfig
        {
            RootPath = string.IsNullOrWhiteSpace(meta.RootPath) ? "$" : meta.RootPath,
            IncludePaths = JsonFlattenConfig.SplitPaths(meta.IncludePaths),
            ExcludePaths = JsonFlattenConfig.SplitPaths(meta.ExcludePaths),
            JsonPaths = JsonFlattenConfig.SplitPaths(meta.JsonPaths),
            ExplodePaths = JsonFlattenConfig.SplitPaths(meta.ExplodePaths),
            PathAliasColumns = JsonFlattenConfig.ParsePathAliases(meta.PathAliases),
            ColumnMappings = JsonFlattenConfig.ParseColumnMappings(meta.ColumnMappings),
            MaxDepth = meta.MaxDepth,
            Separator = string.IsNullOrEmpty(meta.Separator) ? "_" : meta.Separator,
            ArrayHandling = JsonFlattenConfig.ParseArrayHandling(meta.ArrayHandling),
            JoinSeparator = meta.JoinSeparator,
        };
    }

    private static List<string> DiscoverColumns(IEnumerable<JsonElement> records, JsonPathFlattener flattener)
    {
        var order = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            foreach (var (name, _) in flattener.Flatten(record))
            {
                if (seen.Add(name))
                {
                    order.Add(name);
                }
            }
        }

        return order;
    }

    private static async Task<byte[]> ReadAllBytesAsync(IFileStore store, FileRef file, CancellationToken ct)
    {
        await using var stream = await store.OpenReadAsync(file, ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
        return buffer.ToArray();
    }
}
