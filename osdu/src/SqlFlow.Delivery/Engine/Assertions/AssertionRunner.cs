using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Engine.Assertions;

/// <summary>One test in a run's outcome: how it came out, and the first assertions that failed it.</summary>
public sealed record AssertionTestSummary(string Test, string Kind, string Outcome, long? Matched, string? Failed);

/// <summary>
/// The <c>result</c> of a test run of an assertion flow: the report's row in the ledger, how the tests came out, and the
/// tests that did not pass first. The whole of every result is in the ledger; the run's own result stays small enough for
/// the run list to show it.
/// </summary>
public sealed record AssertionRunOutcome(
    string Operation,
    long AssertionRunId,
    string Flow,
    string Partition,
    string Status,
    int Tests,
    int Passed,
    int Failed,
    int Warned,
    int Errored,
    int Skipped,
    IReadOnlyList<AssertionTestSummary> Results,
    int ResultsLeftOut)
{
    /// <summary>The most tests a run's result names; the rest are counted, and every one is in the ledger.</summary>
    public const int MaxNamed = 50;

    /// <summary>The tests the run evaluated, which the run list shows beside every run as the work it did.</summary>
    public long RowsLoaded => Tests - Skipped;

    /// <summary>What the run came to, as a run's error states it when its tests fail it.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        void Add(int count, string outcome)
        {
            if (count > 0)
            {
                var names = Results.Where(r => r.Outcome == outcome).Select(r => r.Test).Take(10).ToList();
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{count} {outcome} ({string.Join(", ", names)}{(count > names.Count ? ", ..." : string.Empty)})"));
            }
        }

        Add(Failed, TestOutcomes.Failed);
        Add(Errored, TestOutcomes.Errored);
        Add(Warned, TestOutcomes.Warned);
        var evaluatedPassed = parts.Count == 0;
        Add(Skipped, TestOutcomes.Skipped);
        var tail = parts.Count == 0 ? "every test passed" : evaluatedPassed ? "every test evaluated passed; " + string.Join("; ", parts) : string.Join("; ", parts);
        return string.Create(CultureInfo.InvariantCulture, $"assertion flow '{Flow}' in partition '{Partition}': {Tests - Skipped} of {Tests} test(s) evaluated, {Passed} passed; {tail}.");
    }
}

/// <summary>What a plan found of one test: what it would read, and whether it fits the template of its kind.</summary>
public sealed record AssertionTestPlan(
    string Test, string Kind, string? Query, int Ids, long? Matched, long? WouldRead, string? Template, IReadOnlyList<string> Problems, string? Skipped, string? Error);

/// <summary>The <c>result</c> of a plan run of an assertion flow: every test selected, checked and counted, nothing evaluated.</summary>
public sealed record AssertionPlanOutcome(string Operation, string Flow, string Partition, IReadOnlyList<AssertionTestPlan> Tests, int TestsLeftOut)
{
    /// <summary>The tests the plan checked, which the run list shows beside the run.</summary>
    public long RowsLoaded => Tests.Count + TestsLeftOut;
}

/// <summary>A test run whose tests came out as the flow says fails the run; it carries the outcome, so the run still reports it.</summary>
public sealed class AssertionTestsFailedException : DeliveryException
{
    public AssertionTestsFailedException(AssertionRunOutcome outcome)
        : base((outcome ?? throw new ArgumentNullException(nameof(outcome))).Describe())
    {
        Outcome = outcome;
    }

    public AssertionRunOutcome Outcome { get; }
}

/// <summary>
/// Runs an assertion flow's tests in one partition (docs/assertions-design.md section 6): selects the tests the run names,
/// checks each against the template of its kind, evaluates them as many at once as the flow's concurrency allows, and keeps
/// every result in the flow's ledger the moment it is known, so a long run shows its progress and a stopped one keeps what
/// it found. The plan checks and counts the same tests and records nothing.
/// </summary>
public sealed class AssertionRunner
{
    private readonly EngineContext _context;
    private readonly AssertionFlowDefinition _flow;
    private readonly IReadOnlyDictionary<string, string> _values;
    private readonly ILogger _log;
    private readonly HttpMessageHandler? _transport;
    private readonly bool _allowLoopback;

    /// <param name="context">The run's engine context.</param>
    /// <param name="flow">The flow bound to the partition the run tests (or a flow whose partition is its header's).</param>
    /// <param name="values">The flow's parameter values, resolved.</param>
    /// <param name="log">The run's log.</param>
    /// <param name="transport">Replaces the built transport (null builds the configured one); it exists for tests.</param>
    /// <param name="allowLoopback">Lets the URL guard accept a loopback endpoint; it exists for tests.</param>
    public AssertionRunner(
        EngineContext context, AssertionFlowDefinition flow, IReadOnlyDictionary<string, string> values, ILogger log,
        HttpMessageHandler? transport = null, bool allowLoopback = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(log);
        _context = context;
        _flow = flow;
        _values = values;
        _log = log;
        _transport = transport;
        _allowLoopback = allowLoopback || EngineContext.LoopbackAllowed;
    }

    private DateTime Now => _context.Time.GetUtcNow().UtcDateTime;

    /// <summary>Runs the tests the run selects and records their report.</summary>
    public async Task<AssertionRunOutcome> TestAsync(
        IReadOnlyCollection<string> tests, IReadOnlyCollection<string> tags, Guid runId, string actor, CancellationToken ct)
    {
        var ledger = _context.Ledger ?? throw new DeliveryException(DeliveryServices.NoLedgerMessage);
        var partition = await PartitionAsync(ct).ConfigureAwait(false);
        var flow = _flow;
        var selected = flow.Select(tests, tags);
        var entry = await ledger.RegisterLedgerAsync(
            new LedgerEntry
            {
                FlowId = flow.LedgerId,
                Partition = partition,
                Kind = LedgerKinds.Assertion,
                FlowName = flow.Name,
                LedgerName = flow.LedgerName,
            },
            ct).ConfigureAwait(false);

        var runnable = selected.Where(t => t.RunsIn(partition)).Select(t => AssertionBinding.Bind(t, Values(partition))).ToList();
        var skipped = selected.Where(t => !t.RunsIn(partition)).ToList();
        var fits = await AssertionTemplates.FitAsync(runnable, _context.Templates, ct).ConfigureAwait(false);
        var run = await ledger.StartAssertionRunAsync(
            new AssertionRunState
            {
                FlowId = entry.FlowId,
                FlowName = flow.Name,
                RunId = runId,
                Actor = actor,
                Selection = Selection(tests, tags),
                Status = AssertionRunStatus.Running,
                Counts = AssertionCounts.None with { Tests = selected.Count },
                DefinitionsHash = Hashing.ContentHash.OfParts(selected.Select(t => t.DefinitionHash).ToArray())[..32],
                StartedUtc = Now,
            },
            ct).ConfigureAwait(false);
        _log.LogInformation(
            "assertion flow '{Flow}' in partition '{Partition}': {Tests} test(s) selected{Skipped}; report {Report}",
            flow.Name, partition, selected.Count, skipped.Count > 0 ? string.Create(CultureInfo.InvariantCulture, $", {skipped.Count} of them not run in this partition") : string.Empty, run.AssertionRunId);

        var results = new List<TestResult>(selected.Count);
        var gate = new Lock();
        try
        {
            foreach (var test in skipped)
            {
                var result = Skipped(test, $"The test runs in {PartitionNames.Listed(test.Partitions)}, not in '{partition}'.");
                await RecordAsync(ledger, run, entry.FlowId, result, ct).ConfigureAwait(false);
                results.Add(result);
            }

            // What this module changed in OSDU lately, which the search index may not list yet: read once, before any test is judged.
            var settling = await IndexSettling.ReadAsync(ledger, partition, runnable, Now, ct).ConfigureAwait(false);
            using var http = new HttpRuntime(_flow.Reliability, _context.Secrets, _context.Time, _transport, _allowLoopback, observer: _context.HttpObserver);
            var client = await ProtocolFactory.ClientAsync(http, flow.Source.Endpoint, flow.Source.Auth, flow.Source.Headers, _context.Secrets, ct).ConfigureAwait(false);
            var search = new OsduSearch(client, flow.Source.QueryPath, flow.Source.SearchPath, _log);
            var storage = new StorageRecords(client, flow.Source.RecordQueryPath);
            var legal = new LegalTagValidator(client, flow.Source.LegalPath + "/legaltags:validate", _context.Time);
            var bulk = new WellboreBulk(client, flow.Source.DdmsRoot);
            var concurrency = Math.Max(1, flow.Reliability.Concurrency);
            await Parallel.ForEachAsync(
                runnable,
                new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = ct },
                async (test, token) =>
                {
                    // A test that does not fit its template is reported as such whatever changed; one that fits and reads a
                    // type this module changed within its settle window is skipped, saying why, rather than judged on an index
                    // that may not list the change yet.
                    if (fits[test.Name].Problems.Count == 0 && settling.Why(test) is { } unsettled)
                    {
                        var skippedNow = Skipped(test, unsettled);
                        await RecordAsync(ledger, run, entry.FlowId, skippedNow, token).ConfigureAwait(false);
                        Log(skippedNow);
                        lock (gate)
                        {
                            results.Add(skippedNow);
                        }

                        return;
                    }

                    var scope = new TestScope
                    {
                        Test = test,
                        Template = fits[test.Name].Template,
                        Examples = flow.Defaults.Examples,
                        Query = new OsduSearchQuery
                        {
                            Kind = test.Kind,
                            Query = test.Query,
                            Spatial = test.Spatial,
                            Sort = test.Sort.Select(s => (s.Field, s.Descending)).ToList(),
                        },
                        Search = search,
                        Storage = storage,
                        Legal = legal,
                        Bulk = bulk,
                        Ledger = ledger,
                        Partition = partition,
                        Concurrency = concurrency,
                        Logger = _log,
                    };
                    var result = await TestEvaluator.EvaluateAsync(scope, fits[test.Name], _context.Time, token).ConfigureAwait(false);
                    await RecordAsync(ledger, run, entry.FlowId, result, token).ConfigureAwait(false);
                    Log(result);
                    lock (gate)
                    {
                        results.Add(result);
                    }
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await CloseAsync(ledger, run.AssertionRunId, AssertionRunStatus.Cancelled, Counts(results, selected.Count), "the run was cancelled").ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await CloseAsync(ledger, run.AssertionRunId, AssertionRunStatus.Errored, Counts(results, selected.Count), SecretHygiene.RedactedMessage(ex)).ConfigureAwait(false);
            throw;
        }

        var counts = Counts(results, selected.Count);
        var status = counts.Failed > 0 ? AssertionRunStatus.Failed : counts.Errored > 0 ? AssertionRunStatus.Errored : AssertionRunStatus.Passed;
        await CloseAsync(ledger, run.AssertionRunId, status, counts, null).ConfigureAwait(false);
        var ordered = results
            .OrderBy(r => r.Outcome switch { TestOutcomes.Failed => 0, TestOutcomes.Errored => 1, TestOutcomes.Warned => 2, TestOutcomes.Passed => 3, _ => 4 })
            .ThenBy(r => r.Test, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new AssertionRunOutcome(
            DeliveryOperations.Test, run.AssertionRunId, flow.Name, partition, status,
            counts.Tests, counts.Passed, counts.Failed, counts.Warned, counts.Errored, counts.Skipped,
            ordered.Take(AssertionRunOutcome.MaxNamed).Select(Summary).ToList(),
            Math.Max(0, ordered.Count - AssertionRunOutcome.MaxNamed));
    }

    /// <summary>Checks and counts the tests the run selects, evaluating nothing and recording nothing.</summary>
    public async Task<AssertionPlanOutcome> PlanAsync(IReadOnlyCollection<string> tests, IReadOnlyCollection<string> tags, CancellationToken ct)
    {
        var partition = await PartitionAsync(ct).ConfigureAwait(false);
        var selected = _flow.Select(tests, tags);
        var runnable = selected.Where(t => t.RunsIn(partition)).Select(t => AssertionBinding.Bind(t, Values(partition))).ToList();
        var fits = await AssertionTemplates.FitAsync(runnable, _context.Templates, ct).ConfigureAwait(false);
        using var http = new HttpRuntime(_flow.Reliability, _context.Secrets, _context.Time, _transport, _allowLoopback, observer: _context.HttpObserver);
        var client = await ProtocolFactory.ClientAsync(http, _flow.Source.Endpoint, _flow.Source.Auth, _flow.Source.Headers, _context.Secrets, ct).ConfigureAwait(false);
        var search = new OsduSearch(client, _flow.Source.QueryPath, _flow.Source.SearchPath, _log);
        var plans = new List<AssertionTestPlan>(selected.Count);
        foreach (var test in selected)
        {
            ct.ThrowIfCancellationRequested();
            if (!test.RunsIn(partition))
            {
                plans.Add(new AssertionTestPlan(test.Name, test.Kind, test.Query, test.Ids.Count, null, null, null, [],
                    $"runs in {PartitionNames.Listed(test.Partitions)}, not in '{partition}'", null));
                continue;
            }

            var bound = runnable.First(t => string.Equals(t.Name, test.Name, StringComparison.OrdinalIgnoreCase));
            var fit = fits[bound.Name];
            long? matched = null;
            string? error = null;
            if (!bound.ByIds)
            {
                try
                {
                    matched = await search.CountAsync(
                        new OsduSearchQuery { Kind = bound.Kind, Query = bound.Query, Spatial = bound.Spatial, ReturnedFields = ["id"] }, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (TestEvaluator.Expected(ex, ct))
                {
                    error = SecretHygiene.RedactedMessage(ex);
                }
            }

            var reads = ReadsRecords(bound);
            long? wouldRead = bound.ByIds ? bound.Ids.Count : matched is { } m && reads ? Math.Min(m, bound.MaxRecords) : reads ? null : 0;
            if (matched is { } n && n > bound.MaxRecords && reads && !bound.Sample)
            {
                error ??= string.Create(CultureInfo.InvariantCulture,
                    $"the query matches {n} records, more than the {bound.MaxRecords} the test reads; its assertions over the records would not be evaluated");
            }

            plans.Add(new AssertionTestPlan(bound.Name, bound.Kind, bound.Query, bound.Ids.Count, bound.ByIds ? null : matched, wouldRead, fit.Version, fit.Problems, null, error));
            _log.LogInformation(
                "plan {Test} ({Kind}): {Matched}{Problems}",
                bound.Name, bound.Kind, bound.ByIds ? $"{bound.Ids.Count} id(s)" : matched is { } c ? $"{c} matching record(s)" : "not counted: " + error,
                fit.Problems.Count > 0 ? string.Create(CultureInfo.InvariantCulture, $"; {fit.Problems.Count} problem(s) with its template: {fit.Problems[0]}") : string.Empty);
        }

        return new AssertionPlanOutcome(DeliveryOperations.Plan, _flow.Name, partition, plans.Take(AssertionRunOutcome.MaxNamed).ToList(), Math.Max(0, plans.Count - AssertionRunOutcome.MaxNamed));
    }

    /// <summary>Whether a test reads records at all, rather than asking the search alone.</summary>
    private static bool ReadsRecords(AssertionTest test)
        => test.Bulk is not null || test.Assertions.Any(a => a is not (CountAssertion or GroupAssertion or IndexedAssertion));

    /// <summary>
    /// The partition the run tests, by its data-partition-id: the one the flow is bound to, or for a flow whose partition is
    /// its header's, the header's resolved on this node.
    /// </summary>
    private async Task<string> PartitionAsync(CancellationToken ct)
    {
        if (_flow.Partition is { } bound)
        {
            return bound;
        }

        var declared = _flow.Source.Headers.FirstOrDefault(h => h.Key.Equals(CacheScope.PartitionHeader, StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrWhiteSpace(declared))
        {
            throw new DeliveryException(
                $"Assertion flow '{_flow.Name}' is bound to no partition and names no '{CacheScope.PartitionHeader}' in its source.headers, so the partition it tests is unknown.");
        }

        return CacheScope.Normalize(await _context.Secrets.ResolveAsync(declared, ct).ConfigureAwait(false), $"{_flow.Name}: source.headers");
    }

    /// <summary>The values a test's tokens take: the flow's parameters, and the partition the run tests.</summary>
    private Dictionary<string, string> Values(string partition)
        => new(_values, StringComparer.Ordinal) { [PartitionNames.RunValue] = partition };

    private static async Task RecordAsync(ILedger ledger, AssertionRunState run, Guid flowId, TestResult result, CancellationToken ct)
    {
        await ledger.RecordAssertionResultAsync(
            new AssertionResultState
            {
                AssertionRunId = run.AssertionRunId,
                FlowId = flowId,
                TestName = result.Test,
                Kind = result.Kind,
                Outcome = result.Outcome,
                Severity = result.Severity,
                Matched = result.Matched,
                Evaluated = result.Evaluated,
                Sampled = result.Sampled,
                Assertions = result.Assertions.Count,
                FailedAssertions = result.Assertions.Count(a => a.Outcome is TestOutcomes.Failed or TestOutcomes.Errored),
                DefinitionHash = result.DefinitionHash,
                DurationMs = result.DurationMs,
                Error = result.Error is null ? null : SecretHygiene.RedactedMessage(result.Error),
                Detail = TestResults.Serialize(result),
                StartedUtc = result.StartedUtc,
                CompletedUtc = result.CompletedUtc,
            },
            ct).ConfigureAwait(false);
    }

    private async Task CloseAsync(ILedger ledger, long assertionRunId, string status, AssertionCounts counts, string? error)
    {
        try
        {
            await ledger.CompleteAssertionRunAsync(assertionRunId, status, counts, error, Now, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlFlowException or InvalidOperationException || ex.GetType().Name.Contains("DbUpdate", StringComparison.Ordinal))
        {
            // The run's own outcome is what the caller reports; a report left running says so on its page.
            _log.LogError("Could not close assertion run {AssertionRunId} as {Status}: {Message}", assertionRunId, status, HeaderRedaction.RedactMessage(ex.Message));
        }
    }

    private static AssertionCounts Counts(List<TestResult> results, int selected)
    {
        List<string> outcomes;
        lock (results)
        {
            outcomes = results.Select(r => r.Outcome).ToList();
        }

        return AssertionCounts.Of(outcomes) with { Tests = selected };
    }

    private void Log(TestResult result)
    {
        if (result.Outcome == TestOutcomes.Passed)
        {
            _log.LogInformation("test {Test}: passed in {Ms} ms ({Matched})", result.Test, result.DurationMs, Matched(result));
            return;
        }

        if (result.Outcome == TestOutcomes.Skipped)
        {
            _log.LogInformation("test {Test}: skipped: {Reason}", result.Test, result.Error);
            return;
        }

        var first = result.Assertions.FirstOrDefault(a => a.Outcome is TestOutcomes.Failed or TestOutcomes.Errored);
        _log.LogWarning(
            "test {Test}: {Outcome} in {Ms} ms ({Matched}): {Reason}",
            result.Test, result.Outcome, result.DurationMs, Matched(result), first is null ? result.Error ?? "no assertion failed" : $"'{first.Label}': {first.Message ?? first.Actual}");
    }

    private static string Matched(TestResult result) => result.Matched is { } matched
        ? string.Create(CultureInfo.InvariantCulture, $"{matched} matched, {result.Evaluated ?? 0} read")
        : "not counted";

    private static AssertionTestSummary Summary(TestResult result)
    {
        var failed = result.Assertions.Where(a => a.Outcome is TestOutcomes.Failed or TestOutcomes.Errored).Select(a => a.Label).Take(3).ToList();
        return new AssertionTestSummary(result.Test, result.Kind, result.Outcome, result.Matched,
            failed.Count == 0 ? result.Error : string.Join("; ", failed));
    }

    private static TestResult Skipped(AssertionTest test, string why) => new()
    {
        Test = test.Name,
        Description = test.Description,
        Kind = test.Kind,
        Tags = test.Tags,
        Outcome = TestOutcomes.Skipped,
        Query = test.Query,
        Ids = test.Ids.Count,
        DefinitionHash = test.DefinitionHash,
        Error = why,
        Assertions = test.Assertions.Select((a, i) => AssertionOutcome.Skipped(i, a, why)).ToList(),
    };

    /// <summary>The selection a run was asked for, as the report keeps it; null for every test.</summary>
    private static string? Selection(IReadOnlyCollection<string> tests, IReadOnlyCollection<string> tags)
    {
        if (tests.Count == 0 && tags.Count == 0)
        {
            return null;
        }

        var selection = new JsonObject();
        if (tests.Count > 0)
        {
            selection["tests"] = new JsonArray(tests.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
        }

        if (tags.Count > 0)
        {
            selection["tags"] = new JsonArray(tags.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
        }

        return selection.ToJsonString();
    }
}
