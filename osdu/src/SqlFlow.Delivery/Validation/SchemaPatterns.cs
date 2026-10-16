using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace SqlFlow.Delivery.Validation;

/// <summary>
/// The regular expressions OSDU's schemas give in <c>pattern</c>, compiled once each and shared by every check that applies
/// one: the render's check of the ids it builds, the preflight, and the validation of values and whole records. A pattern
/// is read the way JSON Schema reads it (ECMAScript, where <c>\w</c> is ASCII) and, when it uses what that dialect lacks,
/// as .NET reads it. Every match runs under <see cref="Timeout"/>, so a pattern that backtracks costs a bounded time.
/// </summary>
public static class SchemaPatterns
{
    /// <summary>The longest one match of a schema pattern may take.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The most patterns kept compiled. Patterns come from the templates and schemas a deployment reads, a set that stays
    /// small; past this a pattern is compiled for each use rather than kept, so a stream of distinct patterns cannot grow
    /// the process without bound.
    /// </summary>
    internal const int MaxKept = 10_000;

    private static readonly ConcurrentDictionary<string, Regex?> Compiled = new(StringComparer.Ordinal);

    /// <summary>The pattern as a regular expression, or null when neither dialect reads it.</summary>
    public static Regex? Compile(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (Compiled.TryGetValue(pattern, out var known))
        {
            return known;
        }

        var regex = Read(pattern);
        if (Compiled.Count < MaxKept)
        {
            Compiled.TryAdd(pattern, regex);
        }

        return regex;
    }

    private static Regex? Read(string pattern)
    {
        try
        {
            return new Regex(pattern, RegexOptions.ECMAScript, Timeout);
        }
        catch (ArgumentException)
        {
            try
            {
                return new Regex(pattern, RegexOptions.CultureInvariant, Timeout);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }
}
