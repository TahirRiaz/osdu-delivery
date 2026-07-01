using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using GenericParsing;
using SqlFlow.Core;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Model;

namespace SqlFlow.Sources;

/// <summary>
/// Reads delimited / fixed-width text files using <see cref="GenericParser"/> (the same library the
/// original SQLFlow used). The only format-specific concern is how a CSV file's columns and rows are
/// parsed; everything else (file selection, schema evolution, provenance/key columns, streaming, file
/// lifecycle) comes from <see cref="FileSourceReaderBase"/>, so every format shares one code path.
/// </summary>
public sealed class CsvSourceReader : FileSourceReaderBase
{
    public CsvSourceReader(IFileLifecycle lifecycle, IEnumerable<IFileStore> fileStores)
        : base(lifecycle, fileStores)
    {
    }

    public override bool CanHandle(string sourceType)
        => string.Equals(sourceType, "csv", StringComparison.OrdinalIgnoreCase);

    protected override string DefaultFilePattern => "*.csv";

    protected override FileSourceOptions ReadOptions(SourceSpec source)
    {
        var meta = PreIngestionCsv.FromSource(source);
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
            MaxRows = meta.MaxRows,
            SkipEndingDataRows = meta.SkipEndingDataRows,
        };
    }

    /// <summary>
    /// Reads one file's column names. The fast path reads the first row and resolves names from it. A
    /// header-only file (a header line with no data rows) is a valid empty dataset with a known schema,
    /// but <see cref="GenericParser"/> exposes no columns when no data row follows; in that case the
    /// first line is re-read as data and its values become the column names.
    /// </summary>
    protected override async Task<IReadOnlyList<string>> ReadColumnNamesAsync(IFileStore store, FileRef file, SourceSpec source, CancellationToken ct)
    {
        var meta = PreIngestionCsv.FromSource(source);

        await using (var stream = await store.OpenReadAsync(file, ct).ConfigureAwait(false))
        using (var reader = new StreamReader(stream, ResolveEncoding(meta.SrcEncoding)))
        using (var parser = Configure(new GenericParser(), reader, meta))
        {
            TryReadRow(parser, file);
            if (parser.ColumnCount > 0)
            {
                return ResolveColumnNames(parser, meta);
            }
        }

        if (!meta.FirstRowHasHeader)
        {
            return [];
        }

        await using var headerStream = await store.OpenReadAsync(file, ct).ConfigureAwait(false);
        using var headerReader = new StreamReader(headerStream, ResolveEncoding(meta.SrcEncoding));
        using var headerParser = Configure(new GenericParser(), headerReader, meta);
        headerParser.FirstRowHasHeader = false;
        if (!TryReadRow(headerParser, file))
        {
            return [];
        }

        var headerNames = new string[headerParser.ColumnCount];
        for (var i = 0; i < headerParser.ColumnCount; i++)
        {
            headerNames[i] = headerParser[i] ?? string.Empty;
        }

        return headerNames;
    }

    protected override async IAsyncEnumerable<FileLine> ReadLinesAsync(
        IFileStore store, FileRef file, SourceSpec source, [EnumeratorCancellation] CancellationToken ct)
    {
        var meta = PreIngestionCsv.FromSource(source);

        await using var stream = await store.OpenReadAsync(file, ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, ResolveEncoding(meta.SrcEncoding));
        using var parser = Configure(new GenericParser(), reader, meta);

        while (true)
        {
            FileLine line;

            // The per-row try only guards parse + value extraction (no yield inside it, which an
            // iterator forbids). On failure parser.DataRowNumber pins down the offending row.
            try
            {
                if (!parser.Read())
                {
                    break;
                }

                // GenericParser, on a file whose final line carries no trailing terminator, can surface one
                // last record that holds no fields at all (ColumnCount == 0) once the content is exhausted.
                // For a header-only file with no trailing newline this is the only "row" it emits, and the
                // pipeline would otherwise stamp it as a spurious all-NULL data row. The same file WITH a
                // trailing newline never produces it. A real final row (even one of only whitespace, only
                // empty fields, or a lone trailing delimiter) always parses to at least one field, so this
                // is the synthetic end-of-file artifact, not data: skip it and keep reading.
                if (parser.ColumnCount == 0)
                {
                    continue;
                }

                var cells = new string?[parser.ColumnCount];
                for (var i = 0; i < parser.ColumnCount; i++)
                {
                    cells[i] = parser[i];
                }

                line = new FileLine(cells, parser.FileRowNumber, parser.DataRowNumber);
            }
            catch (Exception ex) when (ex is not SqlFlowException and not OperationCanceledException)
            {
                throw ParseError(parser, file, ex);
            }

            yield return line;
        }
    }

    private static string[] ResolveColumnNames(GenericParser parser, PreIngestionCsv meta)
    {
        var names = new string[parser.ColumnCount];
        for (var i = 0; i < parser.ColumnCount; i++)
        {
            var header = meta.FirstRowHasHeader ? parser.GetColumnName(i) : null;
            names[i] = string.IsNullOrEmpty(header) ? GeneratedColumnName(i) : header;
        }

        return names;
    }

    private static bool TryReadRow(GenericParser parser, FileRef file)
    {
        try
        {
            return parser.Read();
        }
        catch (Exception ex) when (ex is not SqlFlowException and not OperationCanceledException)
        {
            throw ParseError(parser, file, ex);
        }
    }

    private static SqlFlowException ParseError(GenericParser parser, FileRef file, Exception inner)
        => new($"Parse error in '{file.Name}' near file line {parser.FileRowNumber + 1}: {inner.Message}", inner);

    private static GenericParser Configure(GenericParser parser, TextReader reader, PreIngestionCsv meta)
    {
        parser.SetDataSource(reader);
        parser.FirstRowHasHeader = meta.FirstRowHasHeader;
        parser.TrimResults = meta.TrimResults;
        parser.StripControlChars = meta.StripControlChars;
        parser.SkipStartingDataRows = meta.SkipStartingDataRows;
        parser.MaxBufferSize = Math.Max(meta.MaxBufferSize, 1024);
        parser.FirstRowSetsExpectedColumnCount = meta.FirstRowSetsExpectedColumnCount;

        if (meta.ExpectedColumnCount > 0)
        {
            parser.ExpectedColumnCount = meta.ExpectedColumnCount;
        }

        if (!string.IsNullOrWhiteSpace(meta.ColumnWidths))
        {
            parser.ColumnWidths = ParseWidths(meta.ColumnWidths);
        }
        else
        {
            parser.ColumnDelimiter = DelimiterChar(meta.ColumnDelimiter);
        }

        if (!string.IsNullOrEmpty(meta.TextQualifier))
        {
            parser.TextQualifier = meta.TextQualifier[0];
        }

        if (!string.IsNullOrEmpty(meta.EscapeCharacter) && meta.EscapeCharacter != "0")
        {
            parser.EscapeCharacter = meta.EscapeCharacter[0];
        }

        if (!string.IsNullOrEmpty(meta.CommentCharacter))
        {
            parser.CommentCharacter = meta.CommentCharacter[0];
        }

        return parser;
    }

    private static char DelimiterChar(string? delimiter) => delimiter switch
    {
        null or "" => ',',
        "\\t" or "\t" => '\t',
        _ => delimiter[0],
    };

    private static int[] ParseWidths(string widths) => widths
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(w => int.Parse(w, CultureInfo.InvariantCulture))
        .ToArray();

    private static Encoding ResolveEncoding(string? name) => name?.Trim().ToUpperInvariant() switch
    {
        null or "" => Encoding.UTF8,
        "UTF8" or "UTF-8" => Encoding.UTF8,
        "ASCII" => Encoding.ASCII,
        "UNICODE" or "UTF16" or "UTF-16" => Encoding.Unicode,
        "UTF32" or "UTF-32" => Encoding.UTF32,
        _ => Encoding.UTF8,
    };
}
