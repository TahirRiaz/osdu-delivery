using System.Globalization;

namespace SqlFlow.Delivery.Validation;

/// <summary>
/// What the gate found over many records, counted by template and outcome, with the rules broken most often and what the
/// mappings' assertions found (osdu/docs/reference/flow/mapping-assertions.md): what a run's trace says once instead of a
/// line per record. Thread-safe, since a run's workers report from concurrent deliveries, and
/// bounded however many records it counts: a template keeps its <see cref="MaxRulesKept"/> first distinct rules, and every
/// rule past them is counted as one more.
/// </summary>
public sealed class ValidationTally
{
    /// <summary>The distinct rules (a property path and a keyword) a template's count keeps by name.</summary>
    public const int MaxRulesKept = 500;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Counts> _byKind = new(StringComparer.Ordinal);
    private long _validated;

    /// <summary>How many documents were checked, of every outcome but not validated.</summary>
    public long Validated => Interlocked.Read(ref _validated);

    /// <summary>Counts one verdict, and whether the gate held its record for it.</summary>
    public void Add(ValidationVerdict verdict, bool held)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        if (verdict.Outcome == ValidationOutcome.NotValidated)
        {
            return;
        }

        Interlocked.Increment(ref _validated);
        var kind = verdict.Schema?.Kind ?? "(no template)";
        lock (_gate)
        {
            if (!_byKind.TryGetValue(kind, out var counts))
            {
                counts = new Counts();
                _byKind[kind] = counts;
            }

            counts.Add(verdict, held);
        }
    }

    /// <summary>
    /// What was found, one line per template (at most <paramref name="maxKinds"/>, the most checked first), each naming the
    /// <paramref name="topRules"/> rules broken most often. Empty when nothing was checked.
    /// </summary>
    public IReadOnlyList<string> Lines(int maxKinds = 5, int topRules = 5)
    {
        lock (_gate)
        {
            var kinds = _byKind.OrderByDescending(k => k.Value.Total).ThenBy(k => k.Key, StringComparer.Ordinal).ToList();
            var lines = kinds.Take(maxKinds).Select(k => k.Value.Line(k.Key, topRules)).ToList();
            if (kinds.Count > maxKinds)
            {
                lines.Add(string.Create(CultureInfo.InvariantCulture, $"and {kinds.Count - maxKinds} more template(s), {kinds.Skip(maxKinds).Sum(k => k.Value.Total):N0} document(s) between them"));
            }

            return lines;
        }
    }

    /// <summary>The counts for the progress line: how many were checked, and how many of them are invalid or unverified.</summary>
    public string Brief()
    {
        lock (_gate)
        {
            var invalid = _byKind.Values.Sum(c => c.Invalid);
            var unverified = _byKind.Values.Sum(c => c.Unverified);
            var held = _byKind.Values.Sum(c => c.Held);
            var asserted = _byKind.Values.Sum(c => c.Asserted);
            var failing = _byKind.Values.Sum(c => c.FailingAssertions);
            return string.Create(CultureInfo.InvariantCulture, $"{Validated:N0} validated ({invalid:N0} invalid, {unverified:N0} unverified")
                + (asserted > 0 ? string.Create(CultureInfo.InvariantCulture, $", {failing:N0} failing an assertion") : string.Empty)
                + string.Create(CultureInfo.InvariantCulture, $", {held:N0} held for it)");
        }
    }

    private sealed class Counts
    {
        private readonly Dictionary<string, long> _rules = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _assertions = new(StringComparer.Ordinal);
        private long _otherRules;
        private long _otherAssertions;

        /// <summary>The documents whose mapping states assertions.</summary>
        public long Asserted { get; private set; }

        /// <summary>The documents that failed one or more of them.</summary>
        public long FailingAssertions { get; private set; }

        /// <summary>The failures that held, were reported and left their value out.</summary>
        public long AssertionsHeld { get; private set; }

        public long AssertionsReported { get; private set; }

        public long AssertionsOmitted { get; private set; }

        public long Valid { get; private set; }

        public long Invalid { get; private set; }

        public long Unverified { get; private set; }

        public long Held { get; private set; }

        public long Accepted { get; private set; }

        public long Total => Valid + Invalid + Unverified;

        public void Add(ValidationVerdict verdict, bool held)
        {
            switch (verdict.Outcome)
            {
                case ValidationOutcome.Valid:
                    Valid++;
                    break;
                case ValidationOutcome.Invalid:
                    Invalid++;
                    break;
                case ValidationOutcome.Unverified:
                    Unverified++;
                    break;
            }

            Held += held ? 1 : 0;
            Accepted += verdict.Accepted ? 1 : 0;
            foreach (var rule in verdict.Problems.Select(p => $"{ValidationVerdict.Where(p)} {p.Rule}").Distinct(StringComparer.Ordinal))
            {
                _otherRules += Count(_rules, rule);
            }

            if (verdict.Assertions is not { } assertions)
            {
                return;
            }

            Asserted++;
            FailingAssertions += assertions.Failed > 0 ? 1 : 0;
            AssertionsHeld += assertions.Held;
            AssertionsReported += assertions.Reported;
            AssertionsOmitted += assertions.Omitted;
            foreach (var failed in assertions.Failures.Select(f => $"{f.At} \"{f.Assertion}\"").Distinct(StringComparer.Ordinal))
            {
                _otherAssertions += Count(_assertions, failed);
            }
        }

        /// <summary>Counts one more of <paramref name="name"/>, kept by name while fewer than <see cref="MaxRulesKept"/> are; 1 when it was not kept.</summary>
        private static long Count(Dictionary<string, long> counts, string name)
        {
            if (counts.TryGetValue(name, out var seen))
            {
                counts[name] = seen + 1;
                return 0;
            }

            if (counts.Count < MaxRulesKept)
            {
                counts[name] = 1;
                return 0;
            }

            return 1;
        }

        public string Line(string kind, int topRules)
        {
            var line = string.Create(
                CultureInfo.InvariantCulture,
                $"Validated {Total:N0} document(s) against {kind}: {Valid:N0} valid, {Invalid:N0} invalid, {Unverified:N0} unverified; {Held:N0} held for it");
            if (Accepted > 0)
            {
                line += string.Create(CultureInfo.InvariantCulture, $", {Accepted:N0} sent as a release accepted them");
            }

            var top = _rules.OrderByDescending(r => r.Value).ThenBy(r => r.Key, StringComparer.Ordinal).Take(topRules).ToList();
            if (top.Count > 0)
            {
                line += ". Most broken: " + string.Join(", ", top.Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.Key} ({r.Value:N0})")));
                if (_otherRules > 0)
                {
                    line += string.Create(CultureInfo.InvariantCulture, $"; {_otherRules:N0} more break(s) of rules past the first {MaxRulesKept} kept");
                }
            }

            if (Asserted > 0)
            {
                line += string.Create(
                    CultureInfo.InvariantCulture,
                    $". Assertions: {FailingAssertions:N0} of {Asserted:N0} document(s) failed them ({AssertionsHeld:N0} failure(s) holding, {AssertionsReported:N0} reported, {AssertionsOmitted:N0} left out)");
                var failedMost = _assertions.OrderByDescending(r => r.Value).ThenBy(r => r.Key, StringComparer.Ordinal).Take(topRules).ToList();
                if (failedMost.Count > 0)
                {
                    line += ". Most failed: " + string.Join(", ", failedMost.Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.Key} ({r.Value:N0})")));
                    if (_otherAssertions > 0)
                    {
                        line += string.Create(CultureInfo.InvariantCulture, $"; {_otherAssertions:N0} more failure(s) of assertions past the first {MaxRulesKept} kept");
                    }
                }
            }

            return line + ".";
        }
    }
}
