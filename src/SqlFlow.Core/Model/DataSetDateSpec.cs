using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SqlFlow.Core.Model;

/// <summary>
/// How <c>DataSet_DW</c> is derived for a file: from a date embedded in the file NAME when one can be detected,
/// otherwise the file's last-modified timestamp (the fallback). This ports the legacy pre-ingestion behavior
/// (the <c>flw.SysDateTimeFormat</c> lookup + <c>Functions.ExtractDateTimeFromString</c>), with the format
/// vocabulary baked into the engine and extendable per flow. <c>FileDate_DW</c> always stays the last-modified
/// timestamp (the incremental watermark); only <c>DataSet_DW</c> takes the filename date, so files load in the
/// order their data was actually produced, which is the dataset ordering a dataset-partitioned load relies on.
///
/// Detection is deterministic, not fuzzy: for each format (most specific first) a precise regex locates the
/// matching segment anywhere in the name, and <see cref="DateTime.TryParseExact(string, string, IFormatProvider, DateTimeStyles, out DateTime)"/>
/// validates it against that same format. A segment is bounded by digit look-arounds, so a date is never matched
/// inside a longer run of digits (an id or version number).
/// </summary>
public sealed class DataSetDateSpec
{
    // The minimum year a detected date must have to be accepted; guards against a bare number parsing to year 1
    // (legacy used a 1900-01-01 sentinel for the same purpose).
    private const int MinYear = 1900;

    /// <summary>The baked-in format vocabulary. Each is a standard .NET custom date/time format. Order here is
    /// only for readability; extraction sorts by length so the most specific (longest) format wins, and for equal
    /// length the list order breaks the tie (ISO year-first forms precede day-first, and day-first precedes
    /// month-first for the European default). A flow overrides ties by supplying its own formats via
    /// <c>dataSetFormats</c>, which are tried ahead of these.</summary>
    private static readonly string[] BuiltInFormats =
    [
        // date + time
        "yyyy-MM-dd_HH-mm-ss", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy_MM_dd_HH_mm_ss",
        "yyyyMMdd_HHmmss", "yyyyMMdd-HHmmss", "yyyyMMddHHmmss",
        // date only, delimited
        "yyyy-MM-dd", "yyyy_MM_dd", "yyyy.MM.dd", "yyyy/MM/dd",
        "dd-MM-yyyy", "dd_MM_yyyy", "dd.MM.yyyy", "MM-dd-yyyy", "MM/dd/yyyy",
        "yyyy-M-d", "yyyy_M_d",
        // date only, compact
        "yyyyMMdd", "ddMMyyyy", "MMddyyyy",
        // year-month
        "yyyy-MM", "yyyy_MM", "yyyyMM",
    ];

    // Format tokens, longest first, so MM is never read as two M and fff before ff before f. Case-sensitive
    // (M is month, m is minute). Anything not a token is treated as a literal separator.
    private static readonly (string Token, string Pattern)[] Tokens =
    [
        ("fffffff", "\\d{7}"), ("ffffff", "\\d{6}"), ("fffff", "\\d{5}"),
        ("yyyy", "\\d{4}"), ("ffff", "\\d{4}"),
        ("yyy", "\\d{3}"), ("fff", "\\d{3}"),
        ("yy", "\\d{2}"), ("MM", "\\d{2}"), ("dd", "\\d{2}"), ("HH", "\\d{2}"), ("hh", "\\d{2}"),
        ("mm", "\\d{2}"), ("ss", "\\d{2}"), ("ff", "\\d{2}"), ("tt", "(?:AM|PM|am|pm)"),
        ("M", "\\d{1,2}"), ("d", "\\d{1,2}"), ("H", "\\d{1,2}"), ("h", "\\d{1,2}"),
        ("m", "\\d{1,2}"), ("s", "\\d{1,2}"), ("f", "\\d"),
    ];

    private static readonly IReadOnlyList<(string Format, Regex Regex)> BuiltInCompiled = OrderBySpecificity(Compile(BuiltInFormats));

    /// <summary>The spec that never reads the file name: <c>DataSet_DW</c> is always the last-modified timestamp
    /// (the behavior for a flow that opts out with <c>dataSetFromFileName: false</c>).</summary>
    public static readonly DataSetDateSpec ModifiedOnly = new(fromFileName: false, []);

    private readonly IReadOnlyList<(string Format, Regex Regex)> _formats;

    private DataSetDateSpec(bool fromFileName, IReadOnlyList<(string Format, Regex Regex)> formats)
    {
        FromFileName = fromFileName;
        _formats = formats;
    }

    /// <summary>True when <c>DataSet_DW</c> is derived from the file name (with last-modified as the fallback).</summary>
    public bool FromFileName { get; }

    /// <summary>The effective format vocabulary, most-specific (longest) first: custom formats then built-ins.</summary>
    public IReadOnlyList<string> Formats => _formats.Select(f => f.Format).ToList();

    /// <summary>
    /// Parses the <c>dataSet.*</c> flat options. <c>dataSetFromFileName</c> (default true) toggles filename-date
    /// derivation; <c>dataSetFormats</c> is an optional comma- or pipe-separated list of extra .NET date formats,
    /// tried ahead of the built-ins. Returns <see cref="ModifiedOnly"/> when derivation is off.
    /// </summary>
    public static DataSetDateSpec FromOptions(IReadOnlyDictionary<string, string?> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.GetBool("dataSetFromFileName", true))
        {
            return ModifiedOnly;
        }

        var extra = ParseFormats(options.GetString("dataSetFormats", string.Empty));
        var compiled = extra.Count == 0
            ? BuiltInCompiled
            : OrderBySpecificity([.. Compile(extra), .. BuiltInCompiled]);
        return new DataSetDateSpec(fromFileName: true, compiled);
    }

    /// <summary>The DataSet date for a file: the filename date when one is detected, else the last-modified
    /// timestamp. Equal to the last-modified timestamp when this spec does not read the file name.</summary>
    public DateTime Resolve(string fileName, DateTime fileModifiedUtc)
        => TryExtract(fileName) ?? fileModifiedUtc;

    /// <summary>The date embedded in the file name, or null when none is detected.</summary>
    public DateTime? TryExtract(string fileName)
    {
        if (!FromFileName || string.IsNullOrEmpty(fileName))
        {
            return null;
        }

        foreach (var (format, regex) in _formats)
        {
            var match = regex.Match(fileName);
            if (match.Success
                && DateTime.TryParseExact(match.Value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
                && value.Year >= MinYear)
            {
                return value;
            }
        }

        return null;
    }

    private static List<string> ParseFormats(string raw)
        => [.. raw.Split(['|', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static IReadOnlyList<(string Format, Regex Regex)> Compile(IReadOnlyList<string> formats)
        => [.. formats.Select(f => (f, new Regex(BuildPattern(f), RegexOptions.CultureInvariant)))];

    // Stable sort longest-format-first so a full timestamp is preferred over a date-only prefix, and custom
    // formats win ties over built-ins (they precede the built-ins in the input, and OrderByDescending is stable).
    private static IReadOnlyList<(string Format, Regex Regex)> OrderBySpecificity(IReadOnlyList<(string Format, Regex Regex)> formats)
        => [.. formats.OrderByDescending(f => f.Format.Length)];

    // Convert a .NET custom date/time format into a precise regex that locates just that segment in a longer
    // string. Numeric tokens map to fixed/variable digit runs, tt to AM/PM, everything else is a literal. The
    // match is bounded by digit look-arounds so a date is never matched inside a longer run of digits.
    private static string BuildPattern(string format)
    {
        var sb = new StringBuilder("(?<!\\d)");
        var i = 0;
        while (i < format.Length)
        {
            var length = MatchTokenLength(format, i, out var pattern);
            if (length > 0)
            {
                sb.Append(pattern);
                i += length;
            }
            else
            {
                sb.Append(Regex.Escape(format[i].ToString()));
                i++;
            }
        }

        sb.Append("(?!\\d)");
        return sb.ToString();
    }

    private static int MatchTokenLength(string format, int i, out string pattern)
    {
        foreach (var (token, tokenPattern) in Tokens)
        {
            if (i + token.Length <= format.Length && format.AsSpan(i, token.Length).SequenceEqual(token))
            {
                pattern = tokenPattern;
                return token.Length;
            }
        }

        pattern = string.Empty;
        return 0;
    }
}
