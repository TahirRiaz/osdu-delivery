namespace SqlFlow.Core.Runs;

/// <summary>
/// Per-run substitution parameters: operational overrides supplied at trigger time (API, CLI flags, or the GUI),
/// carried on the queued run, and applied by the engine for THAT run only. They never touch the flow's definition
/// in git, which is what makes a backfill an audited operational act instead of a temporary YAML edit. The set is
/// deliberately typed and closed (never free-form SQL or YAML patches), so a parameter can be validated at the
/// trust boundary and can never smuggle an injection into generated statements.
/// <para>Semantics per flow kind:</para>
/// <list type="bullet">
/// <item><see cref="FullLoad"/>: ignore the watermark entirely. File flows read every file their definition
/// selects; ingestion flows read the whole (optionally filtered) source. Keyed targets still upsert, so a keyed
/// full reload is idempotent; a keyless append will duplicate (the callers warn).</item>
/// <item><see cref="BackfillFrom"/>/<see cref="BackfillTo"/>: an externally-bounded window replacing the probed
/// watermark. File flows bound file dates (the init window, inclusive from / inclusive to); ingestion flows bound
/// the incremental date column (<c>&gt;= from</c>, <c>&lt; to</c>), and an InitLoad backfill re-windows its chunk
/// plan. This is the V3 equivalent of the legacy SetFileDate: rewind by parameter, not by mutating state.</item>
/// <item><see cref="FilePattern"/>: narrows file selection to a glob for this run (reprocess one file or one
/// prefix). File flows only; ignored by relational flows.</item>
/// </list>
/// </summary>
public sealed record RunParameters
{
    public static readonly RunParameters None = new();

    /// <summary>The longest accepted <see cref="FilePattern"/>; a bound so the value is always indexable and
    /// displayable.</summary>
    public const int MaxFilePatternLength = 200;

    /// <summary>Ignore the watermark and read everything the definition selects (a forced full reload).</summary>
    public bool FullLoad { get; init; }

    /// <summary>Low bound of the externally-bounded window (inclusive), UTC.</summary>
    public DateTime? BackfillFrom { get; init; }

    /// <summary>High bound of the externally-bounded window (exclusive for date columns, inclusive for file
    /// dates, matching each mechanism's native window semantics), UTC.</summary>
    public DateTime? BackfillTo { get; init; }

    /// <summary>A glob narrowing which files a file flow reads this run (for example <c>orders_2023-01*.csv</c>).</summary>
    public string? FilePattern { get; init; }

    /// <summary>True when nothing is overridden: the run behaves exactly as its definition says.</summary>
    public bool IsDefault
        => !FullLoad && BackfillFrom is null && BackfillTo is null && string.IsNullOrWhiteSpace(FilePattern);

    /// <summary>Validates the combination, throwing <see cref="SqlFlowException"/> with a caller-safe message.
    /// Called at every trust boundary (API trigger, CLI flags) so a run can never be queued with parameters no
    /// engine path could honor.</summary>
    public void Validate()
    {
        if (FullLoad && (BackfillFrom is not null || BackfillTo is not null))
        {
            throw new SqlFlowException(
                "fullLoad and a backfill window are mutually exclusive: full load ignores every bound; a window IS the bound.");
        }

        if (BackfillFrom is { } from && BackfillTo is { } to && to <= from)
        {
            throw new SqlFlowException("backfillTo must be after backfillFrom.");
        }

        if (BackfillTo is not null && BackfillFrom is null)
        {
            throw new SqlFlowException("backfillTo requires backfillFrom (an upper bound alone is not a window).");
        }

        if (FilePattern is { } pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > MaxFilePatternLength)
            {
                throw new SqlFlowException($"filePattern must be 1 to {MaxFilePatternLength} characters.");
            }

            if (pattern.Any(char.IsControl))
            {
                throw new SqlFlowException("filePattern must not contain control characters.");
            }
        }
    }

    /// <summary>A one-line human description for run logs ("full load", "window 2023-01-01 .. 2023-02-01").</summary>
    public string Describe()
    {
        if (IsDefault)
        {
            return "none";
        }

        var parts = new List<string>(3);
        if (FullLoad)
        {
            parts.Add("full load");
        }

        if (BackfillFrom is { } from)
        {
            parts.Add(BackfillTo is { } to
                ? $"window {from:yyyy-MM-dd HH:mm:ss} .. {to:yyyy-MM-dd HH:mm:ss}"
                : $"from {from:yyyy-MM-dd HH:mm:ss}");
        }

        if (!string.IsNullOrWhiteSpace(FilePattern))
        {
            parts.Add($"files '{FilePattern}'");
        }

        return string.Join(", ", parts);
    }
}
