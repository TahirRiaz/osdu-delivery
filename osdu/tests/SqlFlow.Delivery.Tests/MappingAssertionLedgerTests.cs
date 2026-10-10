using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A mapping's assertions in a real delivery (osdu/docs/reference/flow/mapping-assertions.md), over the sample estate with
/// its WellLog mapping given one assertion, the module's ledger on SQL Server and a fake protocol: a failure that holds
/// keeps the record from being sent with its document kept, the records it holds are one issue, and a release sends the
/// document it accepted; a reported failure is sent and recorded; an omitted value never reaches the target; the record
/// keeps how many judgements failed, and the run's trace counts them.
/// </summary>
[Collection(SqlServerSuite.Name)]
public sealed class MappingAssertionLedgerTests : IDisposable
{
    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private static int LogCount => SampleEstate.Logs().Count;

    /// <summary>The sample flow rendering with a copy of its mapping whose <c>data.Name</c> node states <paramref name="assertion"/>.</summary>
    private async Task<(FlowRuntime Runtime, FakeProtocol Protocol, OsduLedger Ledger)> RuntimeAsync(string assertion, bool optional = false)
    {
        var mappings = Path.Combine(_root, "asserting-mappings");
        Directory.CreateDirectory(mappings);
        var sample = await File.ReadAllTextAsync(Path.Combine(Samples.Mappings, "WellLog@1.4.0.yaml"));
        const string Name = "    Name:\n      $from: log_source\n      $modifiers: [trim]\n";
        var normalized = sample.ReplaceLineEndings("\n");
        Assert.Contains(Name, normalized, StringComparison.Ordinal);
        var asserted = Name
            + (optional ? "      $required: false\n" : string.Empty)
            + "      $assert:\n"
            + string.Concat(assertion.ReplaceLineEndings("\n").Split('\n').Where(l => l.Length > 0).Select(l => "        " + l + "\n"));
        await File.WriteAllTextAsync(Path.Combine(mappings, "WellLog@1.4.0.yaml"), normalized.Replace(Name, asserted, StringComparison.Ordinal));

        var ledger = _db.Ledger(_clock);
        var protocol = new FakeProtocol();
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        var engine = Samples.Engine(ledger, _clock, sources: tables) with { Protocols = new FakeProtocolFactory(protocol) };
        var flow = Samples.LocalFlow(_root);
        flow = flow with { Render = flow.Render with { MappingsDirectory = mappings } };
        var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);
        return (runtime, protocol, ledger);
    }

    private DeliveryWorker Worker(FlowRuntime runtime, FakeProtocol protocol, OsduLedger ledger, ValidationGate gate, RunTrace? trace = null)
        => new(ledger, runtime.Context.Payloads, runtime.Context.Stores, protocol, runtime.Flow, _clock, CompositeDeliveryListener.Empty, Samples.Logger<DeliveryWorker>(), "test-worker")
        {
            MaxWait = null,
            Gate = gate,
            Trace = trace,
        };

    private static async Task<Guid> IntakeAsync(FlowRuntime runtime)
    {
        var intake = await runtime.Intake.IntakeAsync(runtime.Flow, runtime.Mapping, runtime.Parameters, runtime.Request, force: false);
        return intake.Submission.SubmissionId;
    }

    private static async Task<IReadOnlyList<(RecordState Record, ValidationVerdict? Verdict)>> LatestAsync(OsduLedger ledger, Guid flowId)
    {
        var records = await ledger.ListAsync(flowId, new RecordQuery());
        var attempts = await ledger.LatestAttemptsAsync(flowId, records.Select(r => r.DeliveryKey).ToList());
        return records.Select(r => (r, AttemptResult.Validation(attempts[r.DeliveryKey].ResultJson))).ToList();
    }

    [Fact]
    public async Task A_failure_that_holds_keeps_the_record_and_its_document_as_one_issue_and_a_release_sends_it()
    {
        var (runtime, protocol, ledger) = await RuntimeAsync("""
            - equals: no log is named this
              name: log-name-known
            """);
        using (runtime)
        {
            var submission = await IntakeAsync(runtime);
            var held = await Worker(runtime, protocol, ledger, await runtime.GateAsync()).DrainAsync(submission);

            Assert.Equal(LogCount, held.Held);
            Assert.Empty(protocol.Deliveries);
            var records = await ledger.ListAsync(runtime.Flow.Id, new RecordQuery { Status = RecordStatus.Held });
            Assert.Equal(LogCount, records.Count);
            Assert.All(records, r =>
            {
                Assert.True(r.Blocked);
                Assert.NotNull(r.PendingDocumentRef);
                Assert.StartsWith("assertion: the record fails what its mapping WellLog@1.4.0 asserts: data.Name fails \"log-name-known\" with '", r.LastError, StringComparison.Ordinal);
                Assert.Equal(ValidationOutcomes.Valid, r.ValidationOutcome);
                Assert.Equal(1, r.AssertionFailures);
            });

            // The records one assertion holds are one issue, whatever values they hold.
            Assert.Single(records.Select(r => r.ProblemHash).Distinct());
            Assert.All(await LatestAsync(ledger, runtime.Flow.Id), pair =>
            {
                var findings = pair.Verdict!.Assertions!;
                Assert.Equal("WellLog@1.4.0", findings.Mapping);
                Assert.Equal(1, findings.Held);
                Assert.Equal("data.Name", Assert.Single(findings.Failures).At);
            });

            // A release accepts each record's document as it is, and the check before sending sends it.
            Assert.Equal(LogCount, await ledger.ReleaseAsync(runtime.Flow.Id, records.Select(r => r.DeliveryKey).ToList(), Now));
            var sent = await Worker(runtime, protocol, ledger, await runtime.GateAsync()).DrainAsync(submission);
            Assert.Equal(LogCount, sent.Delivered);
            Assert.Equal(LogCount, protocol.Deliveries.Count);
            foreach (var (record, verdict) in await LatestAsync(ledger, runtime.Flow.Id))
            {
                Assert.Equal(RecordStatus.Delivered, record.Status);
                Assert.True(verdict!.Accepted);
                Assert.Equal(1, verdict.Assertions!.Held);
                Assert.Equal(1, record.AssertionFailures);
            }
        }
    }

    [Fact]
    public async Task A_reported_failure_is_sent_recorded_on_the_record_and_counted_on_the_run_s_trace()
    {
        var (runtime, protocol, ledger) = await RuntimeAsync("""
            - equals: no log is named this
              onFail: report
            """);
        using (runtime)
        {
            var trace = new RunTrace(_clock);
            var sent = await Worker(runtime, protocol, ledger, await runtime.GateAsync(), trace).DrainAsync(await IntakeAsync(runtime));

            Assert.Equal(LogCount, sent.Delivered);
            Assert.All(await LatestAsync(ledger, runtime.Flow.Id), pair =>
            {
                Assert.Equal(RecordStatus.Delivered, pair.Record.Status);
                Assert.Equal(1, pair.Record.AssertionFailures);
                Assert.Equal(1, pair.Verdict!.Assertions!.Reported);
                Assert.False(pair.Verdict.Accepted);
            });

            var line = Assert.Single(trace.Validation.Lines());
            Assert.Contains($"Assertions: {LogCount} of {LogCount} document(s) failed them (0 failure(s) holding, {LogCount} reported, 0 left out)", line, StringComparison.Ordinal);
            Assert.Contains("Most failed: data.Name \"equals \"no log is named this\"\"", line, StringComparison.Ordinal);
            Assert.Contains($"{LogCount} failing an assertion", trace.Validation.Brief(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_record_that_meets_its_assertions_keeps_no_failure()
    {
        var (runtime, protocol, ledger) = await RuntimeAsync("""
            - exists: true
            """);
        using (runtime)
        {
            var sent = await Worker(runtime, protocol, ledger, await runtime.GateAsync()).DrainAsync(await IntakeAsync(runtime));

            Assert.Equal(LogCount, sent.Delivered);
            Assert.All(await LatestAsync(ledger, runtime.Flow.Id), pair =>
            {
                Assert.Equal(0, pair.Record.AssertionFailures);
                Assert.Equal(1, pair.Verdict!.Assertions!.Checked);
            });
        }
    }

    [Fact]
    public async Task An_omitted_value_never_reaches_the_target()
    {
        var (runtime, protocol, ledger) = await RuntimeAsync(
            """
            - stage: incoming
              equals: no log is named this
              onFail: omit
            """,
            optional: true);
        using (runtime)
        {
            var sent = await Worker(runtime, protocol, ledger, await runtime.GateAsync()).DrainAsync(await IntakeAsync(runtime));

            Assert.Equal(LogCount, sent.Delivered);
            Assert.All(protocol.Deliveries, work => Assert.Null(work.Document["data"]!["Name"]));
            Assert.All(protocol.Deliveries, work => Assert.Equal(1, work.Assertions!.Omitted));
            Assert.All(await LatestAsync(ledger, runtime.Flow.Id), pair => Assert.Equal(1, pair.Record.AssertionFailures));
        }
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temporary directory a reader still holds open is left for the operating system to reclaim.
        }

        GC.SuppressFinalize(this);
    }
}
