using SqlFlow.Core.Model;

namespace SqlFlow.Sources;

/// <summary>
/// The format-agnostic settings the shared file-ingestion pipeline needs: which files to read, which
/// provenance/key columns to inject, the streaming caps, and the post-load file lifecycle. Each format
/// reader maps its own rich metadata (CSV, XLS, ...) onto this common shape.
/// </summary>
public sealed record FileSourceOptions
{
    // --- File selection ---
    public required string SrcPath { get; init; }
    public string? SrcFile { get; init; }
    public string? SrcPathMask { get; init; }
    public bool SearchSubDirectories { get; init; }
    public string? InitFromFileDate { get; init; }
    public string? InitToFileDate { get; init; }
    public string? IncrementalAfterDate { get; init; }

    /// <summary>
    /// Where the date window reads a file's business date: null keeps the legacy modified-timestamp behavior; a
    /// present spec reads the date from the path (Hive partitions) or the file name, which lets discovery prune
    /// whole out-of-window partition folders instead of enumerating the whole lake.
    /// </summary>
    public FileDateSpec? FileDate { get; init; }

    /// <summary>
    /// How <c>DataSet_DW</c> is derived: from a date detected in the file name (with last-modified as the
    /// fallback), or the last-modified timestamp only. Defaults to <see cref="DataSetDateSpec.ModifiedOnly"/>;
    /// each reader replaces it with <see cref="DataSetDateSpec.FromOptions"/> so a flow's <c>dataSetFromFileName</c>
    /// / <c>dataSetFormats</c> options take effect.
    /// </summary>
    public DataSetDateSpec DataSetDate { get; init; } = DataSetDateSpec.ModifiedOnly;

    // --- Post-load lifecycle ---
    public string? CopyToPath { get; init; }
    public string? ZipToPath { get; init; }
    public bool SrcDeleteIngested { get; init; }
    public bool SrcDeleteAtPath { get; init; }

    // --- Provenance columns ---
    public bool ShowPathWithFileName { get; init; }
    public bool IncludeFileName { get; init; } = true;
    public bool IncludeFileDate { get; init; } = true;
    public bool IncludeFileRowDate { get; init; } = true;
    public bool IncludeFileSize { get; init; } = true;
    public bool IncludeDataSet { get; init; } = true;
    public bool IncludeRowNumber { get; init; } = true;
    public bool IncludeFileLineNumber { get; init; }

    // --- Synthetic keys ---
    public bool IncludeHashKey { get; init; }
    public string? HashKeyColumns { get; init; }
    public string HashKeyType { get; init; } = "SHA2_512";
    public bool IncludeConcatKey { get; init; }
    public string? ConcatKeyColumns { get; init; }
    public string ConcatKeySeparator { get; init; } = "|";

    // --- Streaming caps (0 = unbounded; not all formats use these) ---
    public int MaxRows { get; init; }
    public int SkipEndingDataRows { get; init; }
}

/// <summary>One parsed source row handed from a format reader to the shared pipeline.</summary>
/// <param name="Cells">
/// Values in the file's own column order (null for a missing cell). A string-typed format (CSV/XLS/JSON/XML)
/// fills these with strings; a self-describing format (Parquet) fills them with the real typed values that
/// match the column types it declared. The pipeline coerces each cell to its resolved target column type.
/// </param>
/// <param name="LineNumber">Physical line/row number in the file (for the FileLineNumber column).</param>
/// <param name="DataRowNumber">Data row index within the file (for the RowNumber_DW column).</param>
public readonly record struct FileLine(object?[] Cells, long LineNumber, long DataRowNumber);
