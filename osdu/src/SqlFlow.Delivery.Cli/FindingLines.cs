using System.Globalization;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.Cli;

/// <summary>
/// What a mapping's assertions found of one record (osdu/docs/reference/flow/mapping-assertions.md), as the terminal says it:
/// one line per failure, the first ones, and how many more. Every verb that shows a record's findings says them this way.
/// </summary>
internal static class FindingLines
{
    /// <summary>The most failures a terminal lists for one record.</summary>
    public const int MaxListed = 10;

    public static IEnumerable<string> Of(AssertionFindings findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        foreach (var failure in findings.Failures.Take(MaxListed))
        {
            yield return $"{failure.Path} fails \"{failure.Assertion}\" ({failure.Stage}, {failure.OnFail}): {failure.Message}";
        }

        if (findings.Failed > Math.Min(findings.Failures.Count, MaxListed))
        {
            yield return string.Create(CultureInfo.InvariantCulture, $"and {findings.Failed - Math.Min(findings.Failures.Count, MaxListed)} more failure(s)");
        }
    }
}
