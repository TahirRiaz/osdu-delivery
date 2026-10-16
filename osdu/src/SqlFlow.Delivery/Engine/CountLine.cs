using System.Globalization;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// The short account of what a run did, as the audit trail shows it: only the counts that are not zero, in the order
/// given, so a run that sent three records reads "3 delivered" rather than a row of zeros. The full counts stay on the
/// submission, on the platform run's result and in the run's log.
/// </summary>
internal static class CountLine
{
    /// <summary>The counts that are not zero as "3 delivered, 1 failed", or <paramref name="none"/> when every one is.</summary>
    public static string Of(string none, params (long Count, string What)[] counts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(none);
        ArgumentNullException.ThrowIfNull(counts);
        var parts = counts
            .Where(c => c.Count != 0)
            .Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.Count} {c.What}"))
            .ToList();
        return parts.Count == 0 ? none : string.Join(", ", parts);
    }
}
