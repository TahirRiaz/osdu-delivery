using System.Globalization;
using System.Text.Json.Nodes;

namespace SqlFlow.Delivery.Validation;

/// <summary>
/// The bounds a check of a whole record keeps to, so a record of any size is checked in bounded time and answered in a
/// bounded verdict. What a check does not reach within them is counted as not checked, never as met.
/// </summary>
public sealed record ValidationLimits
{
    public static ValidationLimits Default { get; } = new();

    /// <summary>The most problems a verdict lists; every problem is counted.</summary>
    public int MaxProblemsListed { get; init; } = 50;

    /// <summary>The most parts not checked a verdict lists; every one is counted.</summary>
    public int MaxUnverifiedListed { get; init; } = 20;

    /// <summary>The most distinct ids a check collects from the relationships a record holds.</summary>
    public int MaxReferences { get; init; } = 10_000;

    /// <summary>The most values one check walks.</summary>
    public long MaxValues { get; init; } = 1_000_000;

    /// <summary>The deepest level of a record checked.</summary>
    public int MaxDepth { get; init; } = 64;

    /// <summary>The most items of one list checked.</summary>
    public int MaxItems { get; init; } = 100_000;

    /// <summary>The longest one check runs.</summary>
    public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>What checking a whole record against its schema found (<see cref="RecordValidator"/>).</summary>
public sealed record RecordFindings
{
    /// <summary>The problems, at most <see cref="ValidationLimits.MaxProblemsListed"/> of them.</summary>
    public required IReadOnlyList<SchemaFinding> Problems { get; init; }

    /// <summary>Every problem found, listed or not.</summary>
    public required long ProblemCount { get; init; }

    /// <summary>The parts not checked, at most <see cref="ValidationLimits.MaxUnverifiedListed"/> of them.</summary>
    public required IReadOnlyList<SchemaFinding> Unverified { get; init; }

    /// <summary>Every part not checked, listed or not.</summary>
    public required long UnverifiedCount { get; init; }

    /// <summary>The distinct OSDU ids the record names at its relationships, first place first.</summary>
    public required IReadOnlyList<FoundReference> References { get; init; }

    /// <summary>Whether the record names more distinct ids than a check collects.</summary>
    public bool ReferencesCut { get; init; }

    /// <summary>How many rules were applied.</summary>
    public long Rules { get; init; }

    /// <summary>How many values were walked.</summary>
    public long Values { get; init; }
}

/// <summary>
/// Checks a whole OSDU record against the rules of its template (<see cref="SchemaRules"/>), with the walk every validation
/// shares (<see cref="SchemaWalk"/>): the record's own properties, its <c>data</c> and everything inside it. The gate before a
/// record is sent, the record preview, the <c>conforms</c> assertion and the explorer all check a record here.
/// </summary>
/// <remarks>
/// A part of the record a route fills when it sends it (a dataset list the File service's ids replace) is named in
/// <c>routeFilled</c> by its property path and is not judged. A defect of the walk itself does not fail its caller: the
/// record comes back with nothing checked, the error said, so it can never read as one that met every rule.
/// </remarks>
public static class RecordValidator
{
    /// <summary>Checks <paramref name="record"/> against <paramref name="rules"/>.</summary>
    /// <param name="record">The record as OSDU holds it or as it will be sent.</param>
    /// <param name="rules">The rules of the record's template.</param>
    /// <param name="routeFilled">The property paths (<c>data.Datasets</c>) the route fills when it sends the record.</param>
    /// <param name="limits">The bounds of the check; <see cref="ValidationLimits.Default"/> when null.</param>
    public static RecordFindings Check(JsonNode? record, SchemaRules rules, IReadOnlySet<string>? routeFilled = null, ValidationLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        limits ??= ValidationLimits.Default;
        var walk = new WalkLimits
        {
            MaxListed = limits.MaxProblemsListed,
            MaxUnverifiedListed = Math.Max(1, limits.MaxUnverifiedListed),
            MaxItems = limits.MaxItems,
            MaxDepth = limits.MaxDepth,
            MaxValues = limits.MaxValues,
            Budget = limits.Budget,
            MaxReferences = limits.MaxReferences,
            RouteFilled = routeFilled ?? new HashSet<string>(StringComparer.Ordinal),
        };

        try
        {
            var found = SchemaWalk.Run(record, rules.Root, new SchemaWalk.Place(string.Empty, string.Empty), walk);
            return new RecordFindings
            {
                Problems = found.Problems,
                ProblemCount = found.ProblemCount,
                Unverified = found.Unverified,
                UnverifiedCount = found.UnverifiedCount,
                References = found.References,
                ReferencesCut = found.ReferencesCut,
                Rules = found.Rules,
                Values = found.Values,
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A defect of the check must not be read as a record that met every rule, nor stop the caller's work.
            var why = string.Create(CultureInfo.InvariantCulture, $"the check could not run: {ex.GetType().Name}: {ex.Message}");
            return new RecordFindings
            {
                Problems = [],
                ProblemCount = 0,
                Unverified = [new SchemaFinding(string.Empty, string.Empty, "error", why, string.Empty)],
                UnverifiedCount = 1,
                References = [],
            };
        }
    }
}
