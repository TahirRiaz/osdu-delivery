using SqlFlow.Core.Ingestion;

namespace SqlFlow.Core.Export;

/// <summary>
/// An export flow (legacy flw.Export, FlowType 'exp'): reads a SQL Server source object and writes it to one or
/// more delimited (CSV) or columnar (Parquet) files, on local disk or a cloud store, optionally chunked by
/// day/month/integer-key and optionally bounded by a date/key window. The source server and the destination are
/// connection-registry aliases; the legacy Azure service-principal / Key Vault columns collapse into the
/// <see cref="TargetReference"/> alias (the secretless decision, as with surrogate keys). Immutable.
/// </summary>
public sealed record ExportFlow
{
    public int FlowId { get; init; }                          // FlowID

    public string? Batch { get; init; }                       // Batch

    public required string SysAlias { get; init; }            // SysAlias (mandatory)

    /// <summary>The flw.SysDataSource alias of the source server (legacy srcServer).</summary>
    public required string SrcServer { get; init; }

    /// <summary>The three-part source object (legacy srcDBSchTbl).</summary>
    public required RelationalObject Source { get; init; }

    public string? SrcWithHint { get; init; }                 // srcWithHint

    /// <summary>A static source filter appended to the read (legacy srcFilter; legacy dropped it in the batched
    /// path, V3 applies it).</summary>
    public string? SrcFilter { get; init; }

    public string? IncrementalColumn { get; init; }           // IncrementalColumn (integer key for 'K')

    public string? DateColumn { get; init; }                  // DateColumn (for 'D'/'M')

    public int NoOfOverlapDays { get; init; } = 1;            // NoOfOverlapDays

    public DateOnly? FromDate { get; init; }                  // FromDate

    public DateOnly? ToDate { get; init; }                    // ToDate

    /// <summary>Chunk unit: 'D' day, 'M' month, 'K' integer key, 'F' full single file (legacy ExportBy).</summary>
    public string ExportBy { get; init; } = "D";

    public int ExportSize { get; init; } = 1;                 // ExportSize (days/months/rows per chunk)

    /// <summary>The destination connection alias (from legacy ServicePrincipalAlias). Null means a local
    /// filesystem path in <see cref="TrgPath"/>.</summary>
    public string? TargetReference { get; init; }

    public string? TrgPath { get; init; }                     // trgPath

    public string? TrgFileName { get; init; }                 // trgFileName

    public string TrgFiletype { get; init; } = "csv";         // trgFiletype (csv | parquet)

    public string? TrgEncoding { get; init; }                 // trgEncoding (V3 honors; default UTF-8 no BOM)

    public string CompressionType { get; init; } = "gzip";    // CompressionType (Parquet codec)

    public string ColumnDelimiter { get; init; } = ";";       // ColumnDelimiter (CSV)

    public string TextQualifier { get; init; } = "\"";        // TextQualifier (CSV)

    public bool AddTimeStampToFileName { get; init; } = true; // AddTimeStampToFileName

    public string? Subfolderpattern { get; init; }            // Subfolderpattern (YYYYMM...)

    public int NoOfThreads { get; init; }                     // NoOfThreads (0 => default)

    /// <summary>Compress each written file into a single-entry <c>.zip</c> in place (legacy ZipTrg). Supported by
    /// the local destination; a destination that cannot compress raises a clear error rather than skipping.</summary>
    public bool ZipTrg { get; init; }                         // ZipTrg

    public bool OnErrorResume { get; init; } = true;          // OnErrorResume

    public string? PostInvokeAlias { get; init; }             // PostInvokeAlias (run after the files; an error when no invoke runner is wired)

    public bool DeactivateFromBatch { get; init; }            // DeactivateFromBatch

    public string FlowType { get; init; } = "exp";            // FlowType

    public int? FromObjectMK { get; init; }                   // FromObjectMK

    public int? ToObjectMK { get; init; }                     // ToObjectMK

    public string? CreatedBy { get; init; }                   // CreatedBy

    public DateTime? CreatedDate { get; init; }               // CreatedDate

    /// <summary>The source connection reference understood by the resolver.</summary>
    public string ConnectionReference => "@" + SrcServer;
}

/// <summary>One file produced by an export run.</summary>
public sealed record ExportedFile(string Path, long Rows, long Bytes);

/// <summary>The outcome of one export run.</summary>
public sealed record ExportRunResult
{
    public required Guid RunId { get; init; }

    public required int FlowId { get; init; }

    public required bool Success { get; init; }

    public DateTime StartTimeUtc { get; init; }

    public DateTime EndTimeUtc { get; init; }

    public int DurationSeconds { get; init; }

    public long TotalRows { get; init; }

    public IReadOnlyList<ExportedFile> Files { get; init; } = [];

    /// <summary>Every SQL statement the run generated against the source (probes and per-segment SELECTs), in
    /// execution order. Always captured, success or failure: the generated SQL is the debugging record.</summary>
    public IReadOnlyList<SqlTraceEntry> SqlTrace { get; init; } = [];

    public string? Error { get; init; }
}
