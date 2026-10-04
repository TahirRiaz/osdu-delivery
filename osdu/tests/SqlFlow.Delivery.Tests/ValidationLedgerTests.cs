using System.Text.Json.Nodes;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The gate in a real delivery, over the sample estate, the real WellLog 1.4.0 schema, the module's ledger on SQL Server
/// and a fake protocol: every attempt carries the verdict of the document it sent or held, and the record keeps the last
/// outcome; under <c>enforce</c> a record breaking its schema is held with its document kept, records broken the same way
/// are one issue, and a release sends the document it accepted as it is; under <c>report</c> it is sent and recorded; a
/// try that sends the payload alone checks nothing and leaves the record's outcome as it was; and the run's trace counts it.
/// </summary>
[Collection(SqlServerSuite.Name)]
public sealed class ValidationLedgerTests : IDisposable
{
    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private static int LogCount => SampleEstate.Logs().Count;

    private async Task<(FlowRuntime Runtime, FakeProtocol Protocol, OsduLedger Ledger)> RuntimeAsync()
    {
        var ledger = _db.Ledger(_clock);
        var protocol = new FakeProtocol();
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        var engine = Samples.Engine(ledger, _clock, sources: tables) with { Protocols = new FakeProtocolFactory(protocol) };
        var runtime = await FlowRuntime.CreateAsync(engine, Samples.LocalFlow(_root), SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);
        return (runtime, protocol, ledger);
    }

    /// <summary>The template the mapping pins with one more required data property, which no rendered document carries.</summary>
    private static SchemaSnapshot Stricter(SchemaSnapshot schema)
    {
        var root = schema.Root.DeepClone().AsObject();
        root["properties"]!["data"]!["allOf"]!.AsArray().Add(new JsonObject { ["required"] = new JsonArray("ValidationTestMarker") });
        return new SchemaSnapshot(schema.Kind, root, schema.CapturedUtc);
    }

    private ValidationGate Gate(FlowRuntime runtime, OsduLedger ledger, ValidationMode mode, UnverifiedAction unverified = UnverifiedAction.Send, SchemaSnapshot? schema = null, bool saved = true)
    {
        var flow = runtime.Flow with { Target = runtime.Flow.Target with { Validation = new ValidationPolicy { Mode = mode, Unverified = unverified } } };
        return new ValidationGate(
            flow,
            (_, _, _) => Task.FromResult(saved ? schema ?? runtime.Mapping.Schema : null),
            new ReferenceResolver(runtime.Mapping.References, null, (ids, ct) => ledger.HeldIdsAsync(runtime.Flow.Id, ids, ct), null),
            _clock);
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
    public async Task Every_attempt_carries_the_verdict_of_its_document_and_the_record_keeps_the_outcome()
    {
        var (runtime, protocol, ledger) = await RuntimeAsync();
        using (runtime)
        {
            var submission = await IntakeAsync(runtime);
            var worker = Worker(runtime, protocol, ledger, await runtime.GateAsync());
            var sent = await worker.DrainAsync(submission);

            Assert.Equal(LogCount, sent.Delivered);
            foreach (var (record, verdict) in await LatestAsync(ledger, runtime.Flow.Id))
            {
                Assert.NotNull(verdict);
                Assert.Equal(ValidationOutcome.Valid, verdict.Outcome);
                Assert.Equal(new VerdictSchema(runtime.Mapping.Schema.Kind, runtime.Mapping.Schema.Version, VerdictSchema.Template), verdict.Schema);
                Assert.True(verdict.Rules > 0);
                Assert.True(verdict.References.Total > 0);
                Assert.Equal(verdict.References.Total, verdict.References.InCache + verdict.References.InLedger + verdict.References.NotChecked);
                Assert.Equal(ValidationOutcomes.Valid, record.ValidationOutcome);
                Assert.Equal(0, record.ValidationProblems);
                Assert.Equal(verdict.CheckedUtc, record.ValidatedUtc);
            }
        }
    }

    [Fact]
    public async Task Under_enforce_records_breaking_their_schema_are_held_with_documents_kept_as_one_issue_and_a_release_sends_them()
    {
        var (runtime, protocol, ledger) = await RuntimeAsync();
        using (runtime)
        {
            // Queued under the flow's own setting, report; the gate applies enforce when the documents come to be sent.
            var submission = await IntakeAsync(runtime);
            var gate = Gate(runtime, ledger, ValidationMode.Enforce, schema: Stricter(runtime.Mapping.Schema));
            var held = await Worker(runtime, protocol, ledger, gate).DrainAsync(submission);

            Assert.Equal(LogCount, held.Held);
            Assert.Equal(0, held.Delivered);
            Assert.Empty(protocol.Deliveries);
            var records = await ledger.ListAsync(runtime.Flow.Id, new RecordQuery { Status = RecordStatus.Held });
            Assert.Equal(LogCount, records.Count);
            Assert.All(records, r =>
            {
                Assert.True(r.Blocked);
                Assert.NotNull(r.PendingDocumentRef);
                Assert.StartsWith("validation: the record breaks the schema of osdu:wks:work-product-component--WellLog:1.4.0: data.ValidationTestMarker required: ", r.LastError, StringComparison.Ordinal);
                Assert.Equal(ValidationOutcomes.Invalid, r.ValidationOutcome);
                Assert.Equal(1, r.ValidationProblems);
            });
            Assert.Single(records.Select(r => r.ProblemHash).Distinct());
            Assert.NotNull(records[0].ProblemHash);
            Assert.All(await LatestAsync(ledger, runtime.Flow.Id), pair => Assert.Equal(ValidationOutcome.Invalid, pair.Verdict!.Outcome));

            // A release accepts each record's document as it is; the gate sends it, and its verdict says it was accepted.
            Assert.Equal(LogCount, await ledger.ReleaseAsync(runtime.Flow.Id, records.Select(r => r.DeliveryKey).ToList(), Now));
            var released = await ledger.ListAsync(runtime.Flow.Id, new RecordQuery());
            Assert.All(released, r => Assert.Equal(r.PendingMetadataHash, r.AcceptedMetadataHash));

            var sent = await Worker(runtime, protocol, ledger, gate).DrainAsync(submission);
            Assert.Equal(LogCount, sent.Delivered);
            Assert.Equal(LogCount, protocol.Deliveries.Count);
            foreach (var (record, verdict) in await LatestAsync(ledger, runtime.Flow.Id))
            {
                Assert.Equal(RecordStatus.Delivered, record.Status);
                Assert.True(verdict!.Accepted);
                Assert.Equal(ValidationOutcome.Invalid, verdict.Outcome);
                Assert.Equal(ValidationOutcomes.Invalid, record.ValidationOutcome);
            }
        }
    }

    [Fact]
    public async Task Under_report_records_breaking_their_schema_are_sent_recorded_as_invalid_and_counted_on_the_run_s_trace()
    {
        var (runtime, protocol, ledger) = await RuntimeAsync();
        using (runtime)
        {
            var submission = await IntakeAsync(runtime);
            var trace = new RunTrace(_clock);
            var sent = await Worker(runtime, protocol, ledger, Gate(runtime, ledger, ValidationMode.Report, schema: Stricter(runtime.Mapping.Schema)), trace).DrainAsync(submission);

            Assert.Equal(LogCount, sent.Delivered);
            Assert.All(await LatestAsync(ledger, runtime.Flow.Id), pair =>
            {
                Assert.Equal(RecordStatus.Delivered, pair.Record.Status);
                Assert.Equal(ValidationOutcomes.Invalid, pair.Record.ValidationOutcome);
                Assert.False(pair.Verdict!.Accepted);
                Assert.Equal("data.ValidationTestMarker", Assert.Single(pair.Verdict.Problems).At);
            });

            Assert.Equal(LogCount, trace.Validation.Validated);
            var line = Assert.Single(trace.Validation.Lines());
            Assert.Contains($"{LogCount} invalid", line, StringComparison.Ordinal);
            Assert.Contains("0 held for it", line, StringComparison.Ordinal);
            Assert.Contains("Most broken: data.ValidationTestMarker required", line, StringComparison.Ordinal);
            Assert.Contains("validated", trace.DeliveryProgress(_clock.GetUtcNow()), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_document_whose_template_is_not_saved_is_held_only_when_the_flow_holds_what_it_cannot_check()
    {
        var (runtime, protocol, ledger) = await RuntimeAsync();
        using (runtime)
        {
            var submission = await IntakeAsync(runtime);
            var held = await Worker(runtime, protocol, ledger, Gate(runtime, ledger, ValidationMode.Enforce, UnverifiedAction.Hold, saved: false)).DrainAsync(submission);

            Assert.Equal(LogCount, held.Held);
            var records = await ledger.ListAsync(runtime.Flow.Id, new RecordQuery());
            Assert.All(records, r =>
            {
                Assert.StartsWith("validation: the record could not be fully checked against the schema of its kind", r.LastError, StringComparison.Ordinal);
                Assert.Equal(ValidationOutcomes.Unverified, r.ValidationOutcome);
            });

            // Released, they go out under the same setting, accepted as they are.
            await ledger.ReleaseAsync(runtime.Flow.Id, records.Select(r => r.DeliveryKey).ToList(), Now);
            var sent = await Worker(runtime, protocol, ledger, Gate(runtime, ledger, ValidationMode.Enforce, UnverifiedAction.Hold, saved: false)).DrainAsync(submission);
            Assert.Equal(LogCount, sent.Delivered);
        }
    }

    [Fact]
    public async Task A_try_that_sends_the_payload_alone_checks_nothing_and_the_record_keeps_its_last_outcome()
    {
        var (runtime, protocol, ledger) = await RuntimeAsync();
        using (runtime)
        {
            var gate = await runtime.GateAsync();
            await Worker(runtime, protocol, ledger, gate).DrainAsync(await IntakeAsync(runtime));
            var before = (await ledger.ListAsync(runtime.Flow.Id, new RecordQuery())).ToDictionary(r => r.DeliveryKey);
            Assert.All(before.Values, r => Assert.Equal(ValidationOutcomes.Valid, r.ValidationOutcome));

            _clock.Advance(TimeSpan.FromMinutes(5));
            await ledger.ForceRedeliverAsync(runtime.Flow.Id, null, RedeliverScope.Payload, Now);
            protocol.Deliveries.Clear();
            var again = await Worker(runtime, protocol, ledger, gate).DrainAsync(await IntakeAsync(runtime));

            Assert.Equal(LogCount, again.Delivered);
            Assert.All(protocol.Deliveries, w => Assert.True(w.DeliverPayload && !w.DeliverMetadata));
            foreach (var (record, verdict) in await LatestAsync(ledger, runtime.Flow.Id))
            {
                Assert.Equal(ValidationOutcome.NotValidated, verdict!.Outcome);
                Assert.Equal(ValidationOutcomes.Valid, record.ValidationOutcome);
                Assert.Equal(before[record.DeliveryKey].ValidatedUtc, record.ValidatedUtc);
            }
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
