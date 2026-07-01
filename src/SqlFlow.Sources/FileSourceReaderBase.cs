using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Data;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Model;

namespace SqlFlow.Sources;

/// <summary>
/// The shared, format-agnostic file-ingestion pipeline: file selection (glob, path-mask regex, date
/// window, incremental watermark), dynamic schema evolution (union columns across files, NULL-fill),
/// provenance column injection with correct types, synthetic hash/concatenation keys, streaming into
/// the bulk loader at bounded memory, and the post-load file lifecycle (copy/zip/delete). A concrete
/// reader supplies only the format-specific parts: how to read a file's column names and how to stream
/// its rows. This keeps every format (CSV, XLS, ...) on one code path.
/// </summary>
public abstract class FileSourceReaderBase : ISourceReader
{
    protected const string FileLineNumberColumn = "FileLineNumber";
    protected const string FileNameColumn = "FileName_DW";
    protected const string FileDateColumn = "FileDate_DW";
    protected const string FileRowDateColumn = "FileRowDate_DW";
    protected const string FileSizeColumn = "FileSize_DW";
    protected const string DataSetColumn = "DataSet_DW";
    protected const string RowNumberColumn = "RowNumber_DW";
    protected const string HashKeyColumn = "HashKey_DW";
    protected const string ConcatKeyColumn = "ConcatKey_DW";

    private readonly IFileLifecycle _lifecycle;
    private readonly IReadOnlyList<IFileStore> _fileStores;

    protected FileSourceReaderBase(IFileLifecycle lifecycle, IEnumerable<IFileStore> fileStores)
    {
        ArgumentNullException.ThrowIfNull(fileStores);
        _lifecycle = lifecycle;
        _fileStores = fileStores.ToList();
    }

    /// <summary>True if this reader handles the given source type (e.g. "csv", "xls").</summary>
    public abstract bool CanHandle(string sourceType);

    /// <summary>Default file glob when none is specified (e.g. "*.csv", "*.xlsx").</summary>
    protected abstract string DefaultFilePattern { get; }

    /// <summary>Maps the format's rich metadata onto the common pipeline options.</summary>
    protected abstract FileSourceOptions ReadOptions(SourceSpec source);

    /// <summary>Reads a single file's column names (header, generated, or recovered).</summary>
    protected abstract Task<IReadOnlyList<string>> ReadColumnNamesAsync(IFileStore store, FileRef file, SourceSpec source, CancellationToken ct);

    /// <summary>Streams a single file's rows; cells are in the same order as <see cref="ReadColumnNamesAsync"/>.</summary>
    protected abstract IAsyncEnumerable<FileLine> ReadLinesAsync(IFileStore store, FileRef file, SourceSpec source, CancellationToken ct);

    /// <summary>
    /// Reads a single file's column schema in file order. The default treats every column as a string - the
    /// format leaves typing to the downstream inference step. A self-describing format (Parquet) overrides this
    /// to declare the real CLR and SQL types; its <see cref="ReadLinesAsync"/> cells then carry the matching
    /// typed values, which the pipeline streams straight to the typed target columns.
    /// </summary>
    protected virtual async Task<IReadOnlyList<SourceColumn>> ReadColumnSchemaAsync(IFileStore store, FileRef file, SourceSpec source, CancellationToken ct)
    {
        var names = CleanColumnNames(await ReadColumnNamesAsync(store, file, source, ct).ConfigureAwait(false));
        var columns = new List<SourceColumn>(names.Count);
        foreach (var name in names)
        {
            columns.Add(new SourceColumn { Name = name, Type = typeof(string), IsNullable = true });
        }

        return columns;
    }

    /// <summary>
    /// The legacy file-flow column-name cleanup, applied to every file source so a V3 run produces the same
    /// target column names a legacy install did (legacy did this in Shared.cs for every header-derived name,
    /// always on). Cleaning is index-preserving, so the positional row reader stays aligned. Self-describing
    /// formats apply it to their own field names by routing through here too.
    /// </summary>
    protected static IReadOnlyList<string> CleanColumnNames(IReadOnlyList<string> names)
        => LegacyColumnCleanup.CleanFileColumnNames(names);

    public async Task<IReadOnlyList<SourceColumn>> GetColumnsAsync(SourceSpec source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var options = ReadOptions(source);
        var (store, files) = await ResolveAsync(options, ct).ConfigureAwait(false);

        var union = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceColumns = new Dictionary<string, SourceColumn>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            foreach (var column in await ReadColumnSchemaAsync(store, file, source, ct).ConfigureAwait(false))
            {
                if (string.IsNullOrEmpty(column.Name))
                {
                    continue;
                }

                if (seen.Add(column.Name))
                {
                    union.Add(column.Name);
                    sourceColumns[column.Name] = column;
                }
                else
                {
                    // The same column in another file: keep the type if it agrees, else widen to a string column
                    // (the safe, lossless union across files that disagree on the type).
                    sourceColumns[column.Name] = MergeColumns(sourceColumns[column.Name], column);
                }
            }
        }

        // Provenance and key columns are values we generate and write last, so a source column that shares one
        // of their names would be silently overwritten and re-typed. Reject the ambiguity instead of losing the
        // source data; the author can rename the source field (columnMappings) or disable the system column.
        void AddSystemColumn(string name)
        {
            if (!seen.Add(name))
            {
                throw new SqlFlowException(
                    $"A source column named '{name}' collides with the enabled '{name}' provenance/key column, "
                    + "whose generated value would overwrite the source data. Rename the source column "
                    + "(columnMappings) or disable that provenance/key column.");
            }

            union.Add(name);
        }

        foreach (var systemColumn in EnabledSystemColumns(options))
        {
            AddSystemColumn(systemColumn);
        }

        if (options.IncludeFileLineNumber)
        {
            AddSystemColumn(FileLineNumberColumn);
        }

        if (options.IncludeHashKey)
        {
            AddSystemColumn(HashKeyColumn);
        }

        if (options.IncludeConcatKey)
        {
            AddSystemColumn(ConcatKeyColumn);
        }

        var hashDigestBytes = DigestByteLength(options.HashKeyType);

        // Only the system/key columns we actually generate get their real type; a disabled provenance column
        // whose name a source column happens to share stays an ordinary string source column.
        var enabledSystem = new HashSet<string>(EnabledSystemColumns(options), StringComparer.OrdinalIgnoreCase);
        if (options.IncludeFileLineNumber)
        {
            enabledSystem.Add(FileLineNumberColumn);
        }

        return union
            .Select(name =>
            {
                // Provenance and key columns are values we generate, so they get their real types. A real source
                // column keeps the type the reader declared (string for CSV/XLS/JSON/XML, the mapped SQL type for
                // a self-describing format like Parquet), merged across files above.
                if (options.IncludeHashKey && name == HashKeyColumn)
                {
                    return new SourceColumn { Name = name, Type = typeof(byte[]), SqlType = $"varbinary({hashDigestBytes})", IsNullable = false };
                }

                if (options.IncludeConcatKey && name == ConcatKeyColumn)
                {
                    return new SourceColumn { Name = name, Type = typeof(string), MaxLength = 4000, IsNullable = false };
                }

                if (enabledSystem.Contains(name))
                {
                    var system = SystemColumnType(name);
                    return new SourceColumn { Name = name, Type = system?.Type ?? typeof(string), MaxLength = system?.MaxLength, IsNullable = true };
                }

                return sourceColumns[name];
            })
            .ToList();
    }

    /// <summary>
    /// Reconciles the same column seen in two files: identical resolved type wins (nullability widened); any
    /// disagreement widens to an untyped string column (the flow default types it), the safe lossless union.
    /// </summary>
    private static SourceColumn MergeColumns(SourceColumn existing, SourceColumn incoming)
    {
        // The explicit SqlType is the authoritative SQL type (it already encodes precision/scale), so columns
        // that resolve to the same Type and SqlType agree even if one producer left the redundant Precision/Scale
        // metadata null (e.g. a Parquet ulong and a decimal(20,0) both map to SQL decimal(20,0)).
        if (existing.Type == incoming.Type
            && string.Equals(existing.SqlType, incoming.SqlType, StringComparison.OrdinalIgnoreCase)
            && existing.MaxLength == incoming.MaxLength)
        {
            return existing with { IsNullable = existing.IsNullable || incoming.IsNullable };
        }

        // A real type disagreement. Only a typed format (Parquet) can reach here - the string formats all declare
        // the same untyped string column, which agrees with itself above. Carry an explicit nvarchar(max) so the
        // widened column is self-typed and does not depend on the flow's default column type.
        return new SourceColumn { Name = existing.Name, Type = typeof(string), SqlType = "nvarchar(max)", IsNullable = true };
    }

    public async Task<SourceReadResult> OpenAsync(SourceSpec source, IReadOnlyList<SourceColumn> columns, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(columns);
        var options = ReadOptions(source);
        var (store, files) = await ResolveAsync(options, ct).ConfigureAwait(false);

        var manifest = new List<ProcessedFile>();
        var names = new string[columns.Count];
        var types = new Type[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            names[i] = columns[i].Name;
            types[i] = columns[i].Type;
        }

        var rows = StreamRowsAsync(store, files, columns, source, options, manifest, CancellationToken.None);
        var reader = new StreamingDataReader(names, types, rows.GetAsyncEnumerator(ct));
        return new SourceReadResult { Reader = reader, ProcessedFiles = manifest };
    }

    private async IAsyncEnumerable<object?[]> StreamRowsAsync(
        IFileStore store,
        IReadOnlyList<FileRef> files,
        IReadOnlyList<SourceColumn> columns,
        SourceSpec source,
        FileSourceOptions options,
        List<ProcessedFile> manifest,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var indexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < columns.Count; i++)
        {
            indexByName[columns[i].Name] = i;
        }

        int IndexOf(string name) => indexByName.TryGetValue(name, out var idx) ? idx : -1;

        // Each provenance index is gated on its own Include flag: when the column is disabled, a source column
        // that merely shares the name is a normal data column and must not be overwritten by a generated value.
        var columnCount = columns.Count;
        var fileNameIndex = options.IncludeFileName ? IndexOf(FileNameColumn) : -1;
        var fileDateIndex = options.IncludeFileDate ? IndexOf(FileDateColumn) : -1;
        var fileRowDateIndex = options.IncludeFileRowDate ? IndexOf(FileRowDateColumn) : -1;
        var fileSizeIndex = options.IncludeFileSize ? IndexOf(FileSizeColumn) : -1;
        var dataSetIndex = options.IncludeDataSet ? IndexOf(DataSetColumn) : -1;
        var rowNumberIndex = options.IncludeRowNumber ? IndexOf(RowNumberColumn) : -1;
        var lineNumberIndex = options.IncludeFileLineNumber ? IndexOf(FileLineNumberColumn) : -1;

        var hashKeyIndex = options.IncludeHashKey ? IndexOf(HashKeyColumn) : -1;
        var concatKeyIndex = options.IncludeConcatKey ? IndexOf(ConcatKeyColumn) : -1;
        var hashInputIndices = hashKeyIndex >= 0 ? ResolveKeyInputIndices(options.HashKeyColumns, columns, IndexOf) : [];
        var concatInputIndices = concatKeyIndex >= 0 ? ResolveKeyInputIndices(options.ConcatKeyColumns, columns, IndexOf) : [];
        using var rowHasher = hashKeyIndex >= 0 ? CreateRowHasher(options.HashKeyType) : null;

        var skipEnding = Math.Max(0, options.SkipEndingDataRows);
        long total = 0;
        var reachedMax = false;

        foreach (var file in files)
        {
            if (reachedMax)
            {
                break;
            }

            var fileModifiedUtc = (file.Modified ?? DateTimeOffset.UtcNow).UtcDateTime;
            var ingestedUtc = DateTime.UtcNow;
            var nameValue = options.ShowPathWithFileName ? file.Path : file.Name;

            var fileColumns = (await ReadColumnSchemaAsync(store, file, source, ct).ConfigureAwait(false)).Select(c => c.Name).ToList();
            var map = BuildColumnMap(fileColumns, indexByName);
            var window = skipEnding > 0 ? new Queue<object?[]>(skipEnding + 1) : null;
            long fileRows = 0;

            await foreach (var line in ReadLinesAsync(store, file, source, ct).ConfigureAwait(false))
            {
                if (options.MaxRows > 0 && total >= options.MaxRows)
                {
                    reachedMax = true;
                    break;
                }

                var cells = line.Cells;
                var row = new object?[columnCount];
                for (var i = 0; i < cells.Length && i < map.Length; i++)
                {
                    if (map[i] >= 0)
                    {
                        row[map[i]] = CoerceCell(cells[i], columns[map[i]].Type);
                    }
                }

                if (lineNumberIndex >= 0) row[lineNumberIndex] = line.LineNumber;
                if (fileNameIndex >= 0) row[fileNameIndex] = nameValue;
                if (fileDateIndex >= 0) row[fileDateIndex] = fileModifiedUtc;
                if (fileRowDateIndex >= 0) row[fileRowDateIndex] = ingestedUtc;
                if (fileSizeIndex >= 0) row[fileSizeIndex] = file.Size;
                if (dataSetIndex >= 0) row[dataSetIndex] = fileModifiedUtc;
                if (rowNumberIndex >= 0) row[rowNumberIndex] = line.DataRowNumber;

                if (concatKeyIndex >= 0) row[concatKeyIndex] = BuildConcatKey(row, concatInputIndices, options.ConcatKeySeparator);
                if (rowHasher is not null) row[hashKeyIndex] = ComputeRowHash(rowHasher, row, hashInputIndices);

                if (window is not null)
                {
                    window.Enqueue(row);
                    if (window.Count <= skipEnding)
                    {
                        continue;
                    }

                    row = window.Dequeue();
                }

                total++;
                fileRows++;
                yield return row;
            }

            manifest.Add(new ProcessedFile
            {
                Name = file.Name,
                Path = file.Path,
                SizeBytes = file.Size,
                Modified = file.Modified,
                Rows = fileRows,
                Columns = fileColumns.Count,
            });
        }
    }

    public async Task CompleteAsync(SourceSpec source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var options = ReadOptions(source);

        if (string.IsNullOrWhiteSpace(options.CopyToPath)
            && string.IsNullOrWhiteSpace(options.ZipToPath)
            && !options.SrcDeleteIngested
            && !options.SrcDeleteAtPath)
        {
            return;
        }

        var (_, files) = await ResolveAsync(options, ct).ConfigureAwait(false);
        foreach (var file in files)
        {
            if (!string.IsNullOrWhiteSpace(options.CopyToPath))
            {
                _lifecycle.Copy(file.Path, options.CopyToPath);
            }

            if (!string.IsNullOrWhiteSpace(options.ZipToPath))
            {
                _lifecycle.Zip(file.Path, options.ZipToPath);
            }

            if (options.SrcDeleteIngested || options.SrcDeleteAtPath)
            {
                _lifecycle.Delete(file.Path);
            }
        }
    }

    /// <summary>
    /// Resolves the source files a run would read, applying every selection filter (glob, path mask, date
    /// window, incremental watermark) exactly as the load does. Exposed so format-specific tooling (e.g.
    /// JSON structure discovery) selects the same files on the one code path instead of re-listing.
    /// </summary>
    protected Task<(IFileStore Store, IReadOnlyList<FileRef> Files)> ResolveFilesAsync(SourceSpec source, CancellationToken ct)
        => ResolveAsync(ReadOptions(source), ct);

    private async Task<(IFileStore Store, IReadOnlyList<FileRef> Files)> ResolveAsync(FileSourceOptions options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.SrcPath))
        {
            throw new SqlFlowException("Source requires a 'location' (file path, folder, or URI).");
        }

        var store = _fileStores.FirstOrDefault(s => s.CanHandle(options.SrcPath))
            ?? throw new SqlFlowException($"No file store handles location '{options.SrcPath}'.");

        var pattern = string.IsNullOrWhiteSpace(options.SrcFile) ? DefaultFilePattern : options.SrcFile;

        var pathMask = CompilePathMask(options.SrcPathMask);
        var from = ParseFileDateBound(options.InitFromFileDate, "initFromFileDate");
        var to = ParseFileDateBound(options.InitToFileDate, "initToFileDate");
        var after = ParseFileDateBound(options.IncrementalAfterDate, "incrementalAfterDate");

        // The selection (path mask + date window, read where fileDate says) is pushed into the store so it can
        // prune whole out-of-window partition folders during the walk instead of listing the lake and discarding
        // most of it. When nothing is being filtered the discovery carries no filter and the store lists plainly.
        var filter = new FileDateFilter(options.FileDate, from, to, after, pathMask);
        var discovery = new FileDiscovery
        {
            Pattern = pattern,
            Recursive = options.SearchSubDirectories,
            Filter = filter.IsActive ? filter : null,
        };

        var listed = await store.ListAsync(options.SrcPath, discovery, ct).ConfigureAwait(false);

        var files = listed
            .OrderBy(f => f.Modified ?? DateTimeOffset.MinValue)
            .ThenBy(f => f.Name, StringComparer.Ordinal)
            .ToList();

        if (files.Count == 0)
        {
            var filters = new List<string> { $"pattern '{pattern}'" };
            if (pathMask is not null) filters.Add($"path mask '{options.SrcPathMask}'");
            if (options.FileDate is not null) filters.Add($"file date from {options.FileDate.Source.ToString().ToLowerInvariant()}");
            if (from is not null || to is not null) filters.Add($"date window [{options.InitFromFileDate ?? "min"} .. {options.InitToFileDate ?? "max"}]");
            if (after is not null) filters.Add($"incremental watermark (> {options.IncrementalAfterDate})");
            throw new NoSourceFilesException($"No files under '{options.SrcPath}' matched the filters ({string.Join(", ", filters)}).");
        }

        return (store, files);
    }

    /// <summary>Generated name for a column with no header: Column1, Column2, ... (1-based).</summary>
    protected static string GeneratedColumnName(int index) => "Column" + (index + 1).ToString(CultureInfo.InvariantCulture);

    private static int[] BuildColumnMap(IReadOnlyList<string> fileColumns, IReadOnlyDictionary<string, int> indexByName)
    {
        var map = new int[fileColumns.Count];
        for (var i = 0; i < fileColumns.Count; i++)
        {
            map[i] = indexByName.TryGetValue(fileColumns[i], out var target) ? target : -1;
        }

        return map;
    }

    private static bool IsGeneratedColumn(string name)
        => name is HashKeyColumn or ConcatKeyColumn || SystemColumnType(name) is not null;

    private static int[] ResolveKeyInputIndices(string? explicitColumns, IReadOnlyList<SourceColumn> columns, Func<string, int> indexOf)
    {
        if (!string.IsNullOrWhiteSpace(explicitColumns))
        {
            var resolved = explicitColumns
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(indexOf)
                .Where(i => i >= 0)
                .ToArray();

            if (resolved.Length == 0)
            {
                throw new SqlFlowException($"Key columns '{explicitColumns}' did not match any column in the source.");
            }

            return resolved;
        }

        var indices = new List<int>(columns.Count);
        for (var i = 0; i < columns.Count; i++)
        {
            if (!IsGeneratedColumn(columns[i].Name))
            {
                indices.Add(i);
            }
        }

        if (indices.Count == 0)
        {
            throw new SqlFlowException("A row key was requested but the source has no columns to derive it from.");
        }

        return indices.ToArray();
    }

    private static string BuildConcatKey(object?[] row, int[] inputs, string separator)
    {
        var parts = new string[inputs.Length];
        for (var i = 0; i < inputs.Length; i++)
        {
            parts[i] = AsString(row[inputs[i]]) ?? string.Empty;
        }

        return string.Join(separator, parts);
    }

    private static byte[] ComputeRowHash(IncrementalHash hasher, object?[] row, int[] inputs)
    {
        Span<byte> lengthPrefix = stackalloc byte[4];
        foreach (var index in inputs)
        {
            var value = AsString(row[index]);
            BinaryPrimitives.WriteInt32LittleEndian(lengthPrefix, value?.Length ?? -1);
            hasher.AppendData(lengthPrefix);
            if (!string.IsNullOrEmpty(value))
            {
                hasher.AppendData(Encoding.UTF8.GetBytes(value));
            }
        }

        return hasher.GetHashAndReset();
    }

    /// <summary>
    /// Maps a source cell onto its resolved target column type: an empty string is a null; a string-format cell
    /// passes through; a typed cell (Parquet) streams straight into its typed column, or is rendered as a
    /// faithful string when the target column was widened to string (a cross-file type disagreement).
    /// </summary>
    private static object? CoerceCell(object? value, Type targetType)
    {
        if (value is null)
        {
            return null;
        }

        if (value is string s)
        {
            return s.Length == 0 ? null : s;
        }

        return targetType == typeof(string) ? AsString(value) : value;
    }

    /// <summary>A faithful, round-trippable invariant rendering of a typed cell, for string columns and row keys.</summary>
    private static string? AsString(object? value) => value switch
    {
        null => null,
        string s => s,
        bool b => b ? "True" : "False",
        float f => f.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString(CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        DateTime dt => FormatDateTime(dt),
        DateTimeOffset dto => FormatDateTimeOffset(dto),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => FormatTime(t),
        TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
        Guid g => g.ToString("D", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToBase64String(bytes),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static string FormatDateTime(DateTime dt)
        => dt.Ticks % TimeSpan.TicksPerSecond == 0
            ? dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : dt.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture).TrimEnd('0');

    private static string FormatDateTimeOffset(DateTimeOffset dto)
        => dto.Ticks % TimeSpan.TicksPerSecond == 0
            ? dto.ToString("yyyy-MM-dd HH:mm:sszzz", CultureInfo.InvariantCulture)
            : dto.ToString("yyyy-MM-dd HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture);

    private static string FormatTime(TimeOnly t)
    {
        var s = t.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture).TrimEnd('0');
        return s.EndsWith('.') ? s[..^1] : s;
    }

    [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "Row key is an author-selected identity/dedup hash, not a security primitive.")]
    [SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "Row key is an author-selected identity/dedup hash, not a security primitive.")]
    private static IncrementalHash CreateRowHasher(string hashType) => IncrementalHash.CreateHash(NormalizeHashType(hashType) switch
    {
        "SHA256" or "SHA2256" => HashAlgorithmName.SHA256,
        "SHA1" => HashAlgorithmName.SHA1,
        "MD5" => HashAlgorithmName.MD5,
        _ => HashAlgorithmName.SHA512,
    });

    private static int DigestByteLength(string hashType) => NormalizeHashType(hashType) switch
    {
        "SHA256" or "SHA2256" => 32,
        "SHA1" => 20,
        "MD5" => 16,
        _ => 64,
    };

    private static string NormalizeHashType(string? hashType)
        => (hashType ?? string.Empty).Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();

    private static (Type Type, int? MaxLength)? SystemColumnType(string name) => name switch
    {
        FileNameColumn => (typeof(string), 4000),
        FileDateColumn => (typeof(DateTime), null),
        FileRowDateColumn => (typeof(DateTime), null),
        FileSizeColumn => (typeof(long), null),
        DataSetColumn => (typeof(DateTime), null),
        RowNumberColumn => (typeof(long), null),
        FileLineNumberColumn => (typeof(long), null),
        _ => null,
    };

    private static IEnumerable<string> EnabledSystemColumns(FileSourceOptions options)
    {
        if (options.IncludeFileName) yield return FileNameColumn;
        if (options.IncludeFileDate) yield return FileDateColumn;
        if (options.IncludeFileRowDate) yield return FileRowDateColumn;
        if (options.IncludeFileSize) yield return FileSizeColumn;
        if (options.IncludeDataSet) yield return DataSetColumn;
        if (options.IncludeRowNumber) yield return RowNumberColumn;
    }

    private static Regex? CompilePathMask(string? mask)
    {
        if (string.IsNullOrWhiteSpace(mask))
        {
            return null;
        }

        try
        {
            return new Regex(mask, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch (ArgumentException ex)
        {
            throw new SqlFlowException($"Invalid 'srcPathMask' regular expression '{mask}': {ex.Message}", ex);
        }
    }

    private static DateTime? ParseFileDateBound(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string[] formats = ["yyyy-MM-dd", "yyyyMMdd", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyyMMddHHmmss"];
        const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

        if (DateTime.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture, styles, out var exact))
        {
            return exact;
        }

        if (DateTime.TryParse(value.Trim(), CultureInfo.InvariantCulture, styles, out var parsed))
        {
            return parsed;
        }

        throw new SqlFlowException($"Invalid '{field}' value '{value}'. Use yyyy-MM-dd or a full timestamp.");
    }
}
