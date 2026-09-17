using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Records that wait for other records (docs/interfaces-design.md section 7): a claim leaves a record waiting when what
/// its document refers to is a record of the ledger that has not landed, the record goes back to pending when that one
/// lands, and records never wait for each other in a circle.
/// </summary>
public class RecordWaitTests : IDisposable
{
    private readonly SqliteOsdu _db = new();
    private readonly TestClock _clock = new();
    private readonly Guid _logs = FlowId.Of("well-logs");
    private readonly Guid _wellbores = FlowId.Of("wellbores");

    private OsduLedger Ledger => _db.Ledger(_clock);

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>A record with a rendered document waiting to go, referring to <paramref name="references"/>.</summary>
    private static RecordState Pending(Guid flowId, string sourceKey, Guid submission, params string[] references) => new()
    {
        DeliveryKey = DeliveryKey.Derive(flowId.ToString("N"), [sourceKey]),
        FlowId = flowId,
        SourceKey = sourceKey,
        MappingName = "Thing",
        TargetId = Id(flowId, sourceKey),
        LastSubmissionId = submission,
        PendingDocumentRef = "0:0:10",
        PendingRenderContext = "{}",
        PendingSourceFingerprint = "fp",
        PendingMetadataHash = "mh",
        PendingMetadata = true,
        PendingReferences = references.Select((id, index) => new RecordReference(id, $"data.Ref{index}")).ToList(),
    };

    private static string Id(Guid flowId, string sourceKey)
        => flowId == FlowId.Of("wellbores") ? $"opendes:master-data--Wellbore:{sourceKey}" : $"opendes:work-product-component--WellLog:{sourceKey}";

    private SubmissionState Submission(Guid id, Guid flowId) => new()
    {
        SubmissionId = id,
        FlowId = flowId,
        FlowName = flowId == _logs ? "well-logs" : "wellbores",
        MappingReference = "Thing@1.0.0",
        RenderContext = "{}",
        SourceConnection = "${env:OSDU_SAMPLE_DB}",
        SourceObject = "OsduSample.ing.Thing",
        RecordCount = 1,
    };

    /// <summary>Stages one record of a flow, registering the submission it belongs to.</summary>
    private async Task<RecordState> StageAsync(Guid flowId, string sourceKey, Guid submission, params string[] references)
    {
        await Ledger.RegisterSubmissionAsync(Submission(submission, flowId));
        var record = Pending(flowId, sourceKey, submission, references);
        await Ledger.UpsertPendingAsync(flowId, [record]);
        return record;
    }

    /// <summary>Delivers a record as a worker's completion does: its version is what OSDU now holds.</summary>
    private async Task DeliverAsync(Guid flowId, DeliveryKey key)
    {
        var record = await Ledger.GetRecordAsync(flowId, key) ?? throw new InvalidOperationException($"No record {key}.");
        await Ledger.CompleteAsync(flowId, new RecordCompletion
        {
            DeliveryKey = key,
            Status = RecordStatus.Delivered,
            Promote = true,
            TargetVersion = 1,
            TargetId = record.TargetId,
            Claimed = ClaimedWork.Of(record),
            Attempt = Attempt(key),
        });
    }

    private AttemptRecord Attempt(DeliveryKey key) => new()
    {
        DeliveryKey = key,
        Worker = "test",
        StartedUtc = Now,
        CompletedUtc = Now,
        Outcome = AttemptOutcome.Delivered,
        Phase = "metadata",
    };

    [Fact]
    public async Task A_record_referring_to_one_the_ledger_has_not_delivered_is_left_waiting_rather_than_claimed()
    {
        var wellbore = await StageAsync(_wellbores, "W-1", Guid.NewGuid());
        var submission = Guid.NewGuid();
        var log = await StageAsync(_logs, "L-1", submission, wellbore.TargetId!);

        var claim = await Ledger.ClaimAsync(_logs, submission, "w1", 10, TimeSpan.FromMinutes(5), Now);

        Assert.Null(claim.Lease);
        Assert.Empty(claim.Records);
        var waiting = Assert.Single(claim.Waiting);
        Assert.Equal(log.DeliveryKey, waiting.DeliveryKey);
        Assert.Equal(wellbore.TargetId, waiting.WaitingFor);
        Assert.Contains("W-1", waiting.Reason, StringComparison.Ordinal);
        Assert.Contains("wellbores", waiting.Reason, StringComparison.Ordinal);

        // Waiting is not a try: the record keeps its retry budget and its document, and says what it waits for.
        var state = await Ledger.GetRecordAsync(_logs, log.DeliveryKey);
        Assert.Equal(RecordStatus.Waiting, state!.Status);
        Assert.Equal(wellbore.TargetId, state.WaitingFor);
        Assert.Equal(0, state.AttemptCount);
        Assert.NotNull(state.PendingDocumentRef);
        Assert.False(state.Blocked);
        Assert.Empty(await Ledger.ListAttemptsAsync(_logs, log.DeliveryKey, 10));
    }

    [Fact]
    public async Task An_id_the_ledger_does_not_hold_is_not_waited_for()
    {
        var submission = Guid.NewGuid();
        var log = await StageAsync(_logs, "L-1", submission, "opendes:reference-data--UnitOfMeasure:m");

        var claim = await Ledger.ClaimAsync(_logs, submission, "w1", 10, TimeSpan.FromMinutes(5), Now);

        Assert.Empty(claim.Waiting);
        Assert.Equal(log.DeliveryKey, Assert.Single(claim.Records).DeliveryKey);
    }

    [Fact]
    public async Task A_record_the_ledger_has_delivered_or_removed_is_not_waited_for()
    {
        var delivered = await StageAsync(_wellbores, "W-1", Guid.NewGuid());
        await DeliverAsync(_wellbores, delivered.DeliveryKey);
        var removed = await StageAsync(_wellbores, "W-2", Guid.NewGuid());
        await DeliverAsync(_wellbores, removed.DeliveryKey);
        await Ledger.MarkRemovedAsync(_wellbores, [removed.DeliveryKey], RemovalScope.Record, "test", Now);

        var submission = Guid.NewGuid();
        await StageAsync(_logs, "L-1", submission, delivered.TargetId!, removed.TargetId!);

        var claim = await Ledger.ClaimAsync(_logs, submission, "w1", 10, TimeSpan.FromMinutes(5), Now);

        Assert.Empty(claim.Waiting);
        Assert.Single(claim.Records);
    }

    [Fact]
    public async Task A_flow_the_rules_do_not_wait_for_is_not_waited_for()
    {
        var wellbore = await StageAsync(_wellbores, "W-1", Guid.NewGuid());
        var submission = Guid.NewGuid();
        await StageAsync(_logs, "L-1", submission, wellbore.TargetId!);

        var claim = await Ledger.ClaimAsync(
            _logs, submission, "w1", 10, TimeSpan.FromMinutes(5), Now, waits: new WaitRules { NotWaitedFor = new HashSet<Guid> { _wellbores } });

        Assert.Empty(claim.Waiting);
        Assert.Single(claim.Records);
    }

    [Fact]
    public async Task A_waiting_record_goes_back_to_pending_when_the_record_it_waits_for_lands()
    {
        var wellbore = await StageAsync(_wellbores, "W-1", Guid.NewGuid());
        var submission = Guid.NewGuid();
        var log = await StageAsync(_logs, "L-1", submission, wellbore.TargetId!);
        Assert.Single((await Ledger.ClaimAsync(_logs, submission, "w1", 10, TimeSpan.FromMinutes(5), Now)).Waiting);
        Assert.Equal(log.DeliveryKey, Assert.Single(await Ledger.ListWaitingForAsync(wellbore.TargetId!, 10)).DeliveryKey);

        await DeliverAsync(_wellbores, wellbore.DeliveryKey);

        var released = await Ledger.GetRecordAsync(_logs, log.DeliveryKey);
        Assert.Equal(RecordStatus.Pending, released!.Status);
        Assert.Null(released.WaitingFor);
        Assert.Null(released.LastError);
        var claim = await Ledger.ClaimAsync(_logs, submission, "w2", 10, TimeSpan.FromMinutes(5), Now);
        Assert.Equal(log.DeliveryKey, Assert.Single(claim.Records).DeliveryKey);
        Assert.Empty(claim.Waiting);
    }

    [Fact]
    public async Task Two_records_that_refer_to_each_other_do_not_both_wait()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var a = await StageAsync(_wellbores, "W-1", first);
        var b = await StageAsync(_logs, "L-1", second, a.TargetId!);
        await Ledger.UpsertPendingAsync(_wellbores, [Pending(_wellbores, "W-1", first, b.TargetId!)]);

        // The log is decided first and waits for the wellbore; the wellbore's own reference leads back to the log, so it
        // is claimed rather than left waiting for a record that waits for it.
        var logs = await Ledger.ClaimAsync(_logs, second, "w1", 10, TimeSpan.FromMinutes(5), Now);
        var wellbores = await Ledger.ClaimAsync(_wellbores, first, "w2", 10, TimeSpan.FromMinutes(5), Now);

        Assert.Single(logs.Waiting);
        Assert.Empty(wellbores.Waiting);
        Assert.Equal(a.DeliveryKey, Assert.Single(wellbores.Records).DeliveryKey);
    }

    [Fact]
    public async Task Two_records_of_one_claim_that_refer_to_each_other_do_not_both_wait()
    {
        var submission = Guid.NewGuid();
        var one = await StageAsync(_logs, "L-1", submission);
        var two = await StageAsync(_logs, "L-2", submission, one.TargetId!);
        await Ledger.UpsertPendingAsync(_logs, [Pending(_logs, "L-1", submission, two.TargetId!)]);

        var claim = await Ledger.ClaimAsync(_logs, submission, "w1", 10, TimeSpan.FromMinutes(5), Now);

        Assert.Single(claim.Waiting);
        Assert.Single(claim.Records);
    }

    [Fact]
    public async Task Staging_a_new_document_ends_a_wait_and_an_operator_can_send_a_waiting_record_as_it_is()
    {
        var wellbore = await StageAsync(_wellbores, "W-1", Guid.NewGuid());
        var submission = Guid.NewGuid();
        var log = await StageAsync(_logs, "L-1", submission, wellbore.TargetId!);
        Assert.Single((await Ledger.ClaimAsync(_logs, submission, "w1", 10, TimeSpan.FromMinutes(5), Now)).Waiting);

        // A release of the whole flow leaves a wait alone: nothing about it needs an operator.
        await Ledger.ReleaseAsync(_logs, null, Now);
        Assert.Equal(RecordStatus.Waiting, (await Ledger.GetRecordAsync(_logs, log.DeliveryKey))!.Status);

        // Released by name, the record goes out as it is: without its references it waits for nothing.
        Assert.Equal(1, await Ledger.ReleaseAsync(_logs, [log.DeliveryKey], Now));
        var released = await Ledger.GetRecordAsync(_logs, log.DeliveryKey);
        Assert.Equal(RecordStatus.Pending, released!.Status);
        Assert.Null(released.WaitingFor);
        Assert.Empty(released.PendingReferences);
        Assert.Single((await Ledger.ClaimAsync(_logs, submission, "w2", 10, TimeSpan.FromMinutes(5), Now)).Records);
    }

    [Fact]
    public async Task A_new_document_for_a_waiting_record_makes_it_pending_with_what_that_document_refers_to()
    {
        var wellbore = await StageAsync(_wellbores, "W-1", Guid.NewGuid());
        var submission = Guid.NewGuid();
        var log = await StageAsync(_logs, "L-1", submission, wellbore.TargetId!);
        Assert.Single((await Ledger.ClaimAsync(_logs, submission, "w1", 10, TimeSpan.FromMinutes(5), Now)).Waiting);

        await Ledger.UpsertPendingAsync(_logs, [Pending(_logs, "L-1", submission, "opendes:reference-data--UnitOfMeasure:m")]);

        var restaged = await Ledger.GetRecordAsync(_logs, log.DeliveryKey);
        Assert.Equal(RecordStatus.Pending, restaged!.Status);
        Assert.Null(restaged.WaitingFor);
        Assert.Equal("opendes:reference-data--UnitOfMeasure:m", Assert.Single(restaged.PendingReferences).Id);
        Assert.Single((await Ledger.ClaimAsync(_logs, submission, "w2", 10, TimeSpan.FromMinutes(5), Now)).Records);
    }

    [Fact]
    public async Task A_wait_no_record_of_the_ledger_holds_any_more_is_ended_by_the_sweep_a_run_starts_with()
    {
        var wellbore = await StageAsync(_wellbores, "W-1", Guid.NewGuid());
        var submission = Guid.NewGuid();
        var log = await StageAsync(_logs, "L-1", submission, wellbore.TargetId!);
        Assert.Single((await Ledger.ClaimAsync(_logs, submission, "w1", 10, TimeSpan.FromMinutes(5), Now)).Waiting);

        // The wellbore's record leaves the ledger without a delivery of its own to release what waited for it: a ledger
        // minted again, or records taken out beside the ledger's own paths. Nothing holds the id, so nothing is waited for.
        await using (var db = _db.CreateDbContext())
        {
            await db.DeliveryRecords.Where(r => r.FlowId == _wellbores).ExecuteDeleteAsync();
        }

        Assert.Equal(1, await Ledger.ReleaseResolvedWaitsAsync(_logs, null, Now));
        var released = await Ledger.GetRecordAsync(_logs, log.DeliveryKey);
        Assert.Equal(RecordStatus.Pending, released!.Status);
        Assert.Null(released.WaitingFor);
        Assert.Single((await Ledger.ClaimAsync(_logs, submission, "w2", 10, TimeSpan.FromMinutes(5), Now)).Records);
    }

    [Fact]
    public async Task Waiting_records_are_counted_for_the_flow_and_the_ids_they_hold_are_answered()
    {
        var wellbore = await StageAsync(_wellbores, "W-1", Guid.NewGuid());
        var submission = Guid.NewGuid();
        await StageAsync(_logs, "L-1", submission, wellbore.TargetId!);
        await StageAsync(_logs, "L-2", submission, wellbore.TargetId!);
        Assert.Equal(2, (await Ledger.ClaimAsync(_logs, submission, "w1", 10, TimeSpan.FromMinutes(5), Now)).Waiting.Count);

        var stats = await Ledger.StatsAsync(_logs, Now);
        Assert.Equal(2, stats.Waiting);
        Assert.Equal(0, stats.Pending);
        Assert.Equal(2, await Ledger.CountAsync(_logs, submission, RecordStatus.Waiting));
        Assert.False(await Ledger.HasPendingAsync(_logs, submission, Now));

        Assert.Equal(2, (await Ledger.ListWaitingForAsync(wellbore.TargetId!, 10)).Count);
        Assert.Equal(wellbore.DeliveryKey, Assert.Single(await Ledger.ListHoldersAsync(wellbore.TargetId!, 10)).DeliveryKey);
        Assert.Equal([wellbore.TargetId], await Ledger.HeldIdsAsync([wellbore.TargetId!, "opendes:master-data--Well:nobody"]));
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }
}
