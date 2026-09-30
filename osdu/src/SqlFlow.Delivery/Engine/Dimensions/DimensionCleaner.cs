using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Engine.Dimensions;

/// <summary>What cleaning one original came to.</summary>
public enum CleanOutcome
{
    /// <summary>A clean value: the member the original belongs to.</summary>
    Member,

    /// <summary>Cleaning left nothing.</summary>
    Empty,

    /// <summary>The clean value is longer than a member's may be.</summary>
    TooLong,

    /// <summary>A <c>map</c> step left it out: the dictionary does not list it and says to drop it, or lists it with no value.</summary>
    Dropped,

    /// <summary>A step could not run on it (a regular expression that ran out of time).</summary>
    Failed,
}

/// <summary>One original cleaned: its member's clean value, or why it has none, and what a step had to say about it.</summary>
public readonly record struct CleanResult(CleanOutcome Outcome, string? Value, string? Note)
{
    public static CleanResult Member(string value, string? note = null) => new(CleanOutcome.Member, value, note);

    public static CleanResult None(CleanOutcome outcome, string note) => new(outcome, null, note);
}

/// <summary>
/// Cleans a dimension's originals into the members they belong to, by the dimension's steps in order. Built once per
/// build with every regular expression compiled and every dictionary a <c>map</c> step reads loaded, so cleaning a value is
/// pure work on it. A clean value is trimmed at the end, whatever the steps did: SQL Server ignores trailing spaces when it
/// compares text, so <c>a</c> and <c>a </c> could never be two members of one dimension.
/// </summary>
public sealed partial class DimensionCleaner
{
    /// <summary>How long one regular expression may take on one value; the non-backtracking engine runs in linear time, so this only guards a huge value.</summary>
    public static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);

    private readonly IReadOnlyList<Func<string, CleanResult>> _steps;

    private DimensionCleaner(IReadOnlyList<Func<string, CleanResult>> steps) => _steps = steps;

    /// <summary>A cleaner that keeps every original as its own clean value, trimmed.</summary>
    public static DimensionCleaner Identity { get; } = new([]);

    /// <summary>
    /// The cleaner for <paramref name="steps"/>: every pattern compiled, every dictionary a <c>map</c> step names loaded through
    /// <paramref name="dictionaries"/>, and the field each map reads settled against its dictionary.
    /// </summary>
    /// <exception cref="DeliveryException">A pattern will not compile, or a dictionary or its field cannot be read.</exception>
    public static DimensionCleaner Build(IReadOnlyList<CleanStep> steps, Func<string, DictionaryDefinition> dictionaries)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(dictionaries);
        var built = new List<Func<string, CleanResult>>(steps.Count);
        foreach (var step in steps)
        {
            built.Add(step.Kind switch
            {
                CleanStepKind.Trim => value => CleanResult.Member(value.Trim()),
                CleanStepKind.CollapseSpaces => value => CleanResult.Member(Spaces().Replace(value, " ")),
                CleanStepKind.Upper => value => CleanResult.Member(value.ToUpperInvariant()),
                CleanStepKind.Lower => value => CleanResult.Member(value.ToLowerInvariant()),
                CleanStepKind.Nfc => value => Normalize(value, NormalizationForm.FormC, "nfc"),
                CleanStepKind.Nfkc => value => Normalize(value, NormalizationForm.FormKC, "nfkc"),
                CleanStepKind.FoldSeparators => value => CleanResult.Member(ReferenceKeyFold.Separators(value)),
                CleanStepKind.Replace => Replace(step),
                _ => Map(step, dictionaries),
            });
        }

        return new DimensionCleaner(built);
    }

    /// <summary>
    /// The regular expression a <c>replace</c> step runs: compiled for .NET's non-backtracking engine, which runs in time
    /// linear in the value, so no pattern can hang a build on a value crafted or accidental.
    /// </summary>
    /// <exception cref="ArgumentException">The pattern is not a regular expression, or uses what the engine cannot run without backtracking; the message says which.</exception>
    public static Regex CompilePattern(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        try
        {
            return new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, PatternTimeout);
        }
        catch (NotSupportedException ex)
        {
            // Backreferences, lookarounds and atomic groups need backtracking.
            throw new ArgumentException(
                $"it uses something the non-backtracking engine cannot run (a backreference, a lookaround, an atomic group or a balancing group): {ex.Message}", nameof(pattern), ex);
        }
    }

    /// <summary>Cleans <paramref name="original"/> by every step in order, stopping at the first that leaves it out of every member.</summary>
    public CleanResult Clean(string original)
    {
        ArgumentNullException.ThrowIfNull(original);
        var value = original;
        string? note = null;
        foreach (var step in _steps)
        {
            var result = step(value);
            if (result.Outcome != CleanOutcome.Member)
            {
                return result;
            }

            value = result.Value!;
            note = result.Note ?? note;
        }

        value = value.Trim();
        if (value.Length == 0)
        {
            return CleanResult.None(CleanOutcome.Empty, "cleaning left nothing");
        }

        return value.Length > DimensionSpec.MaxCleanLength
            ? CleanResult.None(CleanOutcome.TooLong, string.Create(CultureInfo.InvariantCulture, $"the clean value is {value.Length} characters, and a member's is at most {DimensionSpec.MaxCleanLength}"))
            : CleanResult.Member(value, note);
    }

    private static CleanResult Normalize(string value, NormalizationForm form, string step)
    {
        try
        {
            return CleanResult.Member(value.Normalize(form));
        }
        catch (ArgumentException)
        {
            // Text holding an unpaired surrogate has no normal form; it goes on as it is, and says so.
            return CleanResult.Member(value, $"{step} left it as it is: it holds a character with no normal form");
        }
    }

    private static Func<string, CleanResult> Replace(CleanStep step)
    {
        var regex = CompilePattern(step.Pattern ?? throw new DeliveryException("A replace step names no pattern."));
        var with = step.With ?? string.Empty;
        return value =>
        {
            try
            {
                return CleanResult.Member(regex.Replace(value, with));
            }
            catch (RegexMatchTimeoutException)
            {
                return CleanResult.None(CleanOutcome.Failed, string.Create(CultureInfo.InvariantCulture, $"replace /{step.Pattern}/ ran longer than {PatternTimeout.TotalSeconds} second(s) on it"));
            }
        };
    }

    private static Func<string, CleanResult> Map(CleanStep step, Func<string, DictionaryDefinition> dictionaries)
    {
        var name = step.Dictionary ?? throw new DeliveryException("A map step names no dictionary.");
        var dictionary = dictionaries(name);
        var field = step.Field ?? (dictionary.Fields.Count == 1
            ? dictionary.Fields[0]
            : throw new DeliveryException(
                $"The map step through dictionary {name} names no field, and the dictionary's entries hold {dictionary.Fields.Count} ({string.Join(", ", dictionary.Fields)}); name the one that gives the clean value with field."));
        if (!dictionary.Fields.Contains(field, StringComparer.Ordinal))
        {
            throw new DeliveryException(
                $"The map step through dictionary {name} reads field '{field}', which its entries do not hold; they hold {string.Join(", ", dictionary.Fields)}.");
        }

        // The dictionary as the lookup table a mapping's replace reads it as, matched by the same rules: exactly, or ignoring
        // case when that finds one entry.
        var table = dictionary.ToLookup(name);
        return value =>
        {
            var match = table.Find(dictionary.Key, value);
            if (match.Item is { } entry)
            {
                return table.Value(entry, field) is { } mapped && mapped.Text.Length > 0
                    ? CleanResult.Member(mapped.Text)
                    : CleanResult.None(CleanOutcome.Dropped, $"dictionary {name} maps it to no value");
            }

            var why = match.IsCaseAmbiguous
                ? $"dictionary {name} lists it {match.Loosening} under {string.Join(" and ", match.CaseVariants.Select(v => $"'{v.Id}'"))}, and picking one would be a guess"
                : $"dictionary {name} does not list it";
            return step.Otherwise switch
            {
                MapOtherwise.LeaveOut => CleanResult.None(CleanOutcome.Dropped, why),
                MapOtherwise.Text => CleanResult.Member(step.OtherwiseText ?? string.Empty, why),
                _ => match.IsCaseAmbiguous ? CleanResult.Member(value, why) : CleanResult.Member(value),
            };
        };
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();
}
