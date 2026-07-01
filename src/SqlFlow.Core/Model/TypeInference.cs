namespace SqlFlow.Core.Model;

/// <summary>How to handle values that don't convert to the inferred type.</summary>
public enum ConvertErrorMode
{
    /// <summary>Bad values become NULL (uses <c>TRY_CONVERT</c>). Lenient.</summary>
    SilentNull,

    /// <summary>Bad values raise an error (uses <c>CONVERT</c>). Strict / fail loudly. The default.</summary>
    Fail,

    /// <summary>Only convert columns that are 100% convertible; otherwise keep the raw string.</summary>
    KeepString,
}

/// <summary>Day/month/year ordering of a locale's short date format, used to order date-style candidates.</summary>
public enum DateOrder
{
    /// <summary>year, month, day (ISO-leaning; e.g. en-CA, sv-SE).</summary>
    Ymd,

    /// <summary>day, month, year (most of Europe; e.g. nb-NO, en-GB).</summary>
    Dmy,

    /// <summary>month, day, year (e.g. en-US).</summary>
    Mdy,
}

/// <summary>Which numeric convention a candidate normalizes from before converting.</summary>
public enum NumericFormat
{
    /// <summary>The server (or override) locale's decimal/grouping convention.</summary>
    Locale,

    /// <summary>Invariant: '.' decimal separator, ',' grouping. The universal fallback.</summary>
    Invariant,
}

/// <summary>
/// The single locale that an inference run binds to: the server's configured locale (or an explicit
/// override). It is resolved once per run and applied to every column, so ambiguous dates and numbers
/// are interpreted consistently across the whole file instead of flipping per column.
/// </summary>
public sealed record ServerLocale
{
    /// <summary>BCP-47 culture name (e.g. "nb-NO"), or "invariant" for the fallback.</summary>
    public required string Culture { get; init; }

    /// <summary>The locale's date-component ordering, used to prioritise date-style candidates.</summary>
    public required DateOrder DateOrder { get; init; }

    /// <summary>The locale's decimal separator ('.' or ',').</summary>
    public required char DecimalSeparator { get; init; }

    /// <summary>The conventional thousands separator paired with the decimal separator.</summary>
    public required char GroupSeparator { get; init; }

    /// <summary>The neutral fallback: ISO date order and invariant ('.'-decimal) numbers.</summary>
    public static ServerLocale Invariant { get; } = new()
    {
        Culture = "invariant",
        DateOrder = DateOrder.Ymd,
        DecimalSeparator = '.',
        GroupSeparator = ',',
    };
}

/// <summary>User-controlled policy for the datatype-inference / transform layer.</summary>
public sealed record TypeInferencePolicy
{
    /// <summary>Enable datatype inference (the SQLFlow <c>FetchDataTypes</c> behavior).</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// What to do with values that don't fit the inferred type. Defaults to <see cref="ConvertErrorMode.Fail"/>
    /// so a value that diverges from the profiled sample halts the load instead of being silently nulled.
    /// </summary>
    public ConvertErrorMode OnConvertError { get; init; } = ConvertErrorMode.Fail;

    /// <summary>Fraction of non-null values that must convert before a type is chosen (1.0 = all).</summary>
    public double Threshold { get; init; } = 1.0;

    /// <summary>Rows to sample when profiling; 0 = full scan.</summary>
    public int SampleSize { get; init; }

    /// <summary>Keep integer-looking values with significant leading zeros (e.g. zip codes, IDs) as string.</summary>
    public bool PreserveLeadingZeros { get; init; } = true;

    /// <summary>
    /// Explicit BCP-47 culture (e.g. "nb-NO") that overrides the server-resolved locale for date and
    /// numeric interpretation; null means use the locale configured on the server.
    /// </summary>
    public string? Culture { get; init; }
}

/// <summary>One date-style candidate the profiler counted, in locale-preferred priority order.</summary>
public sealed record DateConversionCandidate
{
    /// <summary>SQL Server <c>CONVERT</c> style code (e.g. 104 dd.mm.yyyy, 103 dd/mm/yyyy, 23 ISO).</summary>
    public required int Style { get; init; }

    /// <summary>How many non-null sampled values convert under this style.</summary>
    public required long Count { get; init; }
}

/// <summary>
/// One numeric convention the profiler counted (locale or invariant), in priority order. Carries the
/// counts plus the precision/scale measured against the normalized ('.'-decimal) form.
/// </summary>
public sealed record NumericConversionCandidate
{
    public required NumericFormat Format { get; init; }

    /// <summary>How many non-null sampled values convert to <c>decimal</c> after normalization.</summary>
    public required long AsDecimal { get; init; }

    /// <summary>How many convert to <c>float</c> after normalization (catches scientific notation).</summary>
    public required long AsFloat { get; init; }

    /// <summary>Largest number of fractional digits seen across the normalized values.</summary>
    public required int MaxScale { get; init; }

    /// <summary>Largest number of integer (pre-decimal) digits seen across the normalized values.</summary>
    public required int MaxIntegerDigits { get; init; }
}

/// <summary>
/// The result of profiling one raw (string) column against SQL Server's own conversions. Counts are
/// how many non-null sampled values <c>TRY_CONVERT</c> accepts for each candidate - so any type
/// chosen from these counts is, by construction, convertible by SQL Server. Date and numeric
/// candidates are ordered locale-preferred-first so the inferencer can pick the first that fits.
/// </summary>
public sealed record ColumnProfile
{
    public required string ColumnName { get; init; }
    public long Total { get; init; }
    public long NonNull { get; init; }
    public long AsBigInt { get; init; }
    public long AsGuid { get; init; }
    public long AsBitTokens { get; init; }
    public long LeadingZeroInts { get; init; }
    public int MaxLen { get; init; }
    public long? MinValue { get; init; }
    public long? MaxValue { get; init; }

    /// <summary><c>datetime2</c> style candidates, locale-preferred first.</summary>
    public IReadOnlyList<DateConversionCandidate> DateTimeCandidates { get; init; } = [];

    /// <summary><c>date</c> style candidates, locale-preferred first.</summary>
    public IReadOnlyList<DateConversionCandidate> DateCandidates { get; init; } = [];

    /// <summary>Numeric convention candidates (locale, then invariant), preferred first.</summary>
    public IReadOnlyList<NumericConversionCandidate> NumericCandidates { get; init; } = [];
}

/// <summary>An inferred column: the chosen SQL type and the SELECT expression that produces it.</summary>
public sealed record InferredColumn
{
    public required string ColumnName { get; init; }
    public required string DataType { get; init; }
    public required string SelectExpression { get; init; }
    public bool Converted { get; init; }

    /// <summary>CONVERT/TRY_CONVERT style code (date formats); null when not applicable.</summary>
    public int? Style { get; init; }

    /// <summary>
    /// The numeric convention used to normalize the column before conversion; null for non-numeric
    /// columns. Lets validation rebuild the exact same normalization the transform applies.
    /// </summary>
    public NumericFormat? NumericFormat { get; init; }
}
