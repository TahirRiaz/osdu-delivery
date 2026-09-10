using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.Delivery.Tests;

public class SqlLedgerTests : IDisposable
{
    private readonly SqliteCatalog _db = new();
    private readonly TestClock _clock = new();
    private readonly Guid _flow = FlowId.Of("test-flow");

    private CatalogLedger Ledger => _db.Ledger(_clock);

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private RecordState Pending(string sourceKey, Guid submission, bool payload = true) => new()
    {
        DeliveryKey = DeliveryKey.Derive("test", [sourceKey]),
        FlowId = _flow,
        SourceKey = sourceKey,
        MappingName = "Thing",
        TargetId = "dev:x:" + sourceKey,
        LastSubmissionId = submission,
        PendingDocumentRef = "0:0:10",
        PendingRenderContext = "{}",
        PendingSourceFingerprint = "fp",
        PendingMetadataHash = "mh",
        PendingPayloadHash = payload ? "ph" : null,
        PendingPayloadLocation = payload ? "loc" : null,
        PendingMetadata = true,
        PendingPayload = payload,
    };

    private SubmissionState Submission(Guid id) => new()
    {
        SubmissionId = id,
        FlowId = _flow,
        FlowName = "test-flow",
        MappingReference = "Thing@1.0.0",
        RenderContext = "{}",
        DropLocation = "drop",
        RecordCount = 1,
    };

    [Fact]
    public async Task Submissions_are_idempotent_by_id()
    {
        var id = Guid.NewGuid();
        var (first, created) = await Ledger.RegisterSubmissionAsync(Submission(id));
        var (second, createdAgain) = await Ledger.RegisterSubmissionAsync(Submission(id) with { RecordCount = 99 });
        Assert.True(created);
        Assert.False(createdAgain);
        Assert.Equal(1, second.RecordCount);
        Assert.Equal(first.SubmissionId, second.SubmissionId);
        await Ledger.UpdateSubmissionAsync(second with { Status = SubmissionStatus.Completed, Delivered = 3 });
        var loaded = await Ledger.GetSubmissionAsync(id);
        Assert.Equal(SubmissionStatus.Completed, loaded!.Status);
        Assert.Equal(3, loaded.Delivered);
        Assert.Single(await Ledger.ListSubmissionsAsync(_flow, 10));
    }

    [Fact]
    public async Task Lookup_answers_by_delivery_key_or_by_prefix_across_flows()
    {
        var submission = Guid.NewGuid();
        var otherFlow = FlowId.Of("other-flow");
        await Ledger.UpsertPendingAsync([
            Pending("WELL-1", submission),
            Pending("WELL-2", submission),
            Pending("OTHER-1", submission) with { FlowId = otherFlow, Label = "Other one" },
        ]);

        var byKey = await Ledger.LookupAsync(DeliveryKey.Derive("test", ["WELL-1"]).Value.ToString(), 10);
        Assert.Equal("WELL-1", Assert.Single(byKey).SourceKey);

        var byTargetId = await Ledger.LookupAsync("dev:x:WELL", 10);
        Assert.Equal(2, byTargetId.Count);
        Assert.Equal(2, await Ledger.CountLookupAsync("dev:x:WELL"));
        Assert.Single(await Ledger.LookupAsync("dev:x:WELL", 1));

        var byLabel = await Ledger.LookupAsync("Other", 10);
        Assert.Equal(otherFlow, Assert.Single(byLabel).FlowId);

        Assert.Empty(await Ledger.LookupAsync("nothing-like-this", 10));
        Assert.Empty(await Ledger.LookupAsync(Guid.NewGuid().ToString(), 10));
        await Assert.ThrowsAsync<ArgumentException>(() => Ledger.LookupAsync(" ", 10));
    }

    [Fact]
    public async Task Claim_leases_pending_records_once_and_complete_promotes_pending_state()
    {
        var submission = Guid.NewGuid();
        await Ledger.UpsertPendingAsync([Pending("a", submission), Pending("b", submission)]);

        var claimed = await Ledger.ClaimAsync(_flow, submission, "w1", 10, TimeSpan.FromMinutes(5), Now);
        Assert.Equal(2, claimed.Count);
        Assert.All(claimed, r => Assert.Equal(RecordStatus.Delivering, r.Status));
        Assert.All(claimed, r => Assert.Equal(1, r.AttemptCount));
        Assert.All(claimed, r => Assert.StartsWith("w1/", r.LeaseOwner!, StringComparison.Ordinal));
        Assert.Empty(await Ledger.ClaimAsync(_flow, submission, "w2", 10, TimeSpan.FromMinutes(5), Now));
        Assert.True(await Ledger.RenewLeaseAsync(claimed[0].DeliveryKey, claimed[0].LeaseOwner!, TimeSpan.FromMinutes(5), Now));
        Assert.False(await Ledger.RenewLeaseAsync(claimed[0].DeliveryKey, "someone-else", TimeSpan.FromMinutes(5), Now));

        var record = claimed[0];
        await Ledger.CompleteAsync(new RecordCompletion
        {
            DeliveryKey = record.DeliveryKey,
            Status = RecordStatus.Delivered,
            Promote = true,
            TargetVersion = 42,
            TargetId = record.TargetId,
            Attempt = new AttemptRecord { DeliveryKey = record.DeliveryKey, SubmissionId = submission, Worker = "w1", StartedUtc = Now, CompletedUtc = Now, Outcome = AttemptOutcome.Delivered, Phase = "metadata+payload", TargetVersion = 42 },
        });

        var delivered = await Ledger.GetRecordAsync(_flow, record.DeliveryKey);
        Assert.Equal(RecordStatus.Delivered, delivered!.Status);
        Assert.Equal("mh", delivered.MetadataHash);
        Assert.Equal("ph", delivered.PayloadHash);
        Assert.Equal("fp", delivered.SourceFingerprint);
        Assert.Equal(42, delivered.TargetVersion);
        Assert.Null(delivered.PendingDocumentRef);
        Assert.Null(delivered.LeaseOwner);
        Assert.Equal(0, delivered.AttemptCount);
        Assert.Single(await Ledger.ListAttemptsAsync(record.DeliveryKey, 10));
        Assert.Equal(1, await Ledger.CountAsync(_flow, submission, RecordStatus.Delivered));
        Assert.True(await Ledger.HasPendingAsync(_flow, submission, Now));
    }

    [Fact]
    public async Task Expired_leases_are_reclaimed_and_backoff_is_honoured()
    {
        var submission = Guid.NewGuid();
        await Ledger.UpsertPendingAsync([Pending("a", submission)]);
        var first = await Ledger.ClaimAsync(_flow, null, "w1", 10, TimeSpan.FromMinutes(5), Now);
        Assert.Single(first);

        _clock.Advance(TimeSpan.FromMinutes(6));
        var reclaimed = await Ledger.ReclaimExpiredLeasesAsync(_flow, Now);
        Assert.Equal(1, reclaimed);
        var state = await Ledger.GetRecordAsync(_flow, first[0].DeliveryKey);
        Assert.Equal(RecordStatus.Pending, state!.Status);
        Assert.Contains("lease expired", state.LastError, StringComparison.Ordinal);

        var second = await Ledger.ClaimAsync(_flow, null, "w2", 10, TimeSpan.FromMinutes(5), Now);
        Assert.Single(second);
        Assert.Equal(2, second[0].AttemptCount);

        // Retry later: the record goes back to pending with a next-attempt time and is not claimable until then.
        await Ledger.CompleteAsync(new RecordCompletion
        {
            DeliveryKey = second[0].DeliveryKey,
            Status = RecordStatus.Pending,
            NextAttemptUtc = Now + TimeSpan.FromMinutes(10),
            Error = "503",
            Attempt = new AttemptRecord { DeliveryKey = second[0].DeliveryKey, Worker = "w2", StartedUtc = Now, CompletedUtc = Now, Outcome = AttemptOutcome.Failed, Phase = "none", Error = "503" },
        });
        Assert.Empty(await Ledger.ClaimAsync(_flow, null, "w3", 10, TimeSpan.FromMinutes(5), Now));
        _clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Single(await Ledger.ClaimAsync(_flow, null, "w3", 10, TimeSpan.FromMinutes(5), Now));

        // A claim that expired while still 'delivering' is picked up directly by the next claim as well.
        _clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Single(await Ledger.ClaimAsync(_flow, null, "w4", 10, TimeSpan.FromMinutes(5), Now));
    }

    [Fact]
    public async Task Releasing_a_lease_on_shutdown_does_not_charge_the_attempt()
    {
        var submission = Guid.NewGuid();
        await Ledger.UpsertPendingAsync([Pending("a", submission)]);
        var claimed = await Ledger.ClaimAsync(_flow, submission, "w1", 10, TimeSpan.FromMinutes(5), Now);
        var record = claimed.Single();
        Assert.Equal(1, record.AttemptCount);

        Assert.False(await Ledger.ReleaseLeaseAsync(record.DeliveryKey, "someone-else", countAttempt: false, Now));
        Assert.True(await Ledger.ReleaseLeaseAsync(record.DeliveryKey, record.LeaseOwner!, countAttempt: false, Now));
        Assert.False(await Ledger.ReleaseLeaseAsync(record.DeliveryKey, record.LeaseOwner!, countAttempt: false, Now));

        var released = await Ledger.GetRecordAsync(_flow, record.DeliveryKey);
        Assert.Equal(RecordStatus.Pending, released!.Status);
        Assert.Equal(0, released.AttemptCount);
        Assert.Null(released.LeaseOwner);
        Assert.Null(released.LeaseExpiresUtc);
        Assert.Contains("stopped mid-attempt", released.LastError, StringComparison.Ordinal);
        Assert.Empty(await Ledger.ListAttemptsAsync(record.DeliveryKey, 10));

        // Immediately claimable again, and the retry budget starts from the first attempt.
        var again = await Ledger.ClaimAsync(_flow, submission, "w2", 10, TimeSpan.FromMinutes(5), Now);
        Assert.Equal(1, again.Single().AttemptCount);

        Assert.True(await Ledger.ReleaseLeaseAsync(record.DeliveryKey, again[0].LeaseOwner!, countAttempt: true, Now));
        Assert.Equal(1, (await Ledger.GetRecordAsync(_flow, record.DeliveryKey))!.AttemptCount);
    }

    [Fact]
    public async Task Upsert_pending_preserves_current_state_and_held_records_can_be_released()
    {
        var s1 = Guid.NewGuid();
        await Ledger.UpsertPendingAsync([Pending("a", s1)]);
        var claimed = await Ledger.ClaimAsync(_flow, s1, "w", 10, TimeSpan.FromMinutes(1), Now);
        await Ledger.CompleteAsync(new RecordCompletion
        {
            DeliveryKey = claimed[0].DeliveryKey,
            Status = RecordStatus.Delivered,
            Promote = true,
            TargetVersion = 1,
            Attempt = new AttemptRecord { DeliveryKey = claimed[0].DeliveryKey, Worker = "w", StartedUtc = Now, CompletedUtc = Now, Outcome = AttemptOutcome.Delivered, Phase = "metadata" },
        });

        var s2 = Guid.NewGuid();
        await Ledger.UpsertPendingAsync([Pending("a", s2) with { PendingMetadataHash = "mh2", PendingPayload = false, PendingPayloadHash = null }]);
        var state = await Ledger.GetRecordAsync(_flow, claimed[0].DeliveryKey);
        Assert.Equal(RecordStatus.Pending, state!.Status);
        Assert.Equal("mh", state.MetadataHash);
        Assert.Equal("ph", state.PayloadHash);
        Assert.Equal(1, state.TargetVersion);
        Assert.Equal("mh2", state.PendingMetadataHash);
        Assert.Equal(s2, state.LastSubmissionId);

        await Ledger.MarkSkippedAsync(_flow, [new SkippedRecord { DeliveryKey = claimed[0].DeliveryKey, Kind = SkipKind.Unchanged, Reason = "unchanged" }], s2);
        var held = Pending("b", s2) with { LastError = "no wellbore" };
        await Ledger.MarkHeldAsync([held]);
        var heldState = await Ledger.GetRecordAsync(_flow, held.DeliveryKey);
        Assert.Equal(RecordStatus.Held, heldState!.Status);
        Assert.Equal("no wellbore", heldState.LastError);
        Assert.Equal(1, await Ledger.CountAsync(_flow, s2, RecordStatus.Held));

        // A held record without a pending document cannot go to the worker; releasing it unblocks the next plan.
        Assert.Equal(1, await Ledger.ReleaseAsync(_flow, [held.DeliveryKey], Now));
        var unblocked = await Ledger.GetRecordAsync(_flow, held.DeliveryKey);
        Assert.Equal(RecordStatus.Held, unblocked!.Status);
        Assert.True(heldState.Blocked);
        Assert.False(unblocked.Blocked);
        Assert.Contains("released", unblocked.LastError, StringComparison.Ordinal);

        await Ledger.UpsertPendingAsync([Pending("c", s2)]);
        var c = await Ledger.ClaimAsync(_flow, s2, "w", 10, TimeSpan.FromMinutes(1), Now);
        var cRecord = c.Single(r => r.SourceKey == "c");
        await Ledger.CompleteAsync(new RecordCompletion
        {
            DeliveryKey = cRecord.DeliveryKey,
            Status = RecordStatus.Held,
            Error = "409",
            Attempt = new AttemptRecord { DeliveryKey = cRecord.DeliveryKey, Worker = "w", StartedUtc = Now, CompletedUtc = Now, Outcome = AttemptOutcome.Held, Phase = "none", Error = "409" },
        });
        Assert.Equal(1, await Ledger.ReleaseAsync(_flow, null, Now));
        Assert.Equal(RecordStatus.Pending, (await Ledger.GetRecordAsync(_flow, cRecord.DeliveryKey))!.Status);
    }

    [Fact]
    public async Task Work_batches_lease_their_due_records_and_close_with_counts()
    {
        var submission = Guid.NewGuid();
        await Ledger.RegisterSubmissionAsync(Submission(submission));
        await Ledger.UpsertPendingAsync([
            Pending("a", submission) with { WorkBatch = 3, PendingDocumentRef = "3:0:10" },
            Pending("b", submission) with { WorkBatch = 3, PendingDocumentRef = "3:11:10" },
            Pending("c", submission) with { WorkBatch = 4, PendingDocumentRef = "4:0:10" },
        ]);
        await Ledger.AddWorkBatchAsync(new WorkBatchState { SubmissionId = submission, FlowId = _flow, Index = 3, Location = "batch-3", RecordCount = 2, CreatedUtc = Now });
        await Ledger.AddWorkBatchAsync(new WorkBatchState { SubmissionId = submission, FlowId = _flow, Index = 4, Location = "batch-4", RecordCount = 1, CreatedUtc = Now.AddSeconds(1) });
        Assert.Equal(2, await Ledger.CountWorkBatchesAsync(submission, WorkBatchStatus.Queued));

        var claimed = await Ledger.ClaimWorkBatchAsync(_flow, submission, "w1", TimeSpan.FromMinutes(5), Now, runId: Guid.NewGuid());
        Assert.NotNull(claimed);
        Assert.Equal(3, claimed!.Batch.Index);
        Assert.Equal(WorkBatchStatus.Running, claimed.Batch.Status);
        Assert.Equal(2, claimed.Records.Count);
        Assert.All(claimed.Records, r => Assert.Equal(RecordStatus.Delivering, r.Status));
        Assert.All(claimed.Records, r => Assert.Equal(claimed.Batch.LeaseOwner, r.LeaseOwner));

        // Records leased under the batch are invisible to the individual claim; a record of a queued batch is not.
        var loose = await Ledger.ClaimAsync(_flow, submission, "w2", 10, TimeSpan.FromMinutes(5), Now);
        Assert.Equal("c", Assert.Single(loose).SourceKey);
        Assert.True(await Ledger.RenewWorkBatchLeaseAsync(submission, 3, claimed.Batch.LeaseOwner!, TimeSpan.FromMinutes(5), Now));
        Assert.False(await Ledger.RenewWorkBatchLeaseAsync(submission, 3, "someone-else", TimeSpan.FromMinutes(5), Now));

        await Ledger.CompleteManyAsync(claimed.Records.Select(r => new RecordCompletion
        {
            DeliveryKey = r.DeliveryKey,
            Status = RecordStatus.Delivered,
            Promote = true,
            TargetVersion = 5,
            TargetStateJson = "{\"recordId\":\"x\"}",
            Attempt = new AttemptRecord { DeliveryKey = r.DeliveryKey, SubmissionId = submission, Worker = "w1", StartedUtc = Now, CompletedUtc = Now, Outcome = AttemptOutcome.Delivered, Phase = "metadata", ResultJson = "{\"steps\":[]}", WorkBatch = 3 },
        }).ToList());
        await Ledger.CompleteWorkBatchAsync(submission, 3, claimed.Batch.LeaseOwner!, WorkBatchStatus.Done, 2, 0, 0, 0, null, Now);

        var batches = await Ledger.ListWorkBatchesAsync(submission, 10, 0);
        var done = batches.Single(b => b.Index == 3);
        Assert.Equal(WorkBatchStatus.Done, done.Status);
        Assert.Equal(2, done.Delivered);
        Assert.Null(done.LeaseOwner);
        Assert.NotNull(done.CompletedUtc);
        var a = await Ledger.GetRecordAsync(_flow, claimed.Records[0].DeliveryKey);
        Assert.Equal(RecordStatus.Delivered, a!.Status);
        Assert.Null(a.PendingDocumentRef);
        Assert.Null(a.WorkBatch);
        Assert.Equal("{\"recordId\":\"x\"}", a.TargetStateJson);
        var attempts = await Ledger.ListAttemptsAsync(a.DeliveryKey, 5);
        Assert.Equal(3, attempts[0].WorkBatch);
        Assert.Equal("{\"steps\":[]}", attempts[0].ResultJson);

        // Batch 4: its only record is leased by w2, so the claim finds nothing due; a release hands the batch back.
        var second = await Ledger.ClaimWorkBatchAsync(_flow, submission, "w1", TimeSpan.FromMinutes(5), Now);
        Assert.NotNull(second);
        Assert.Equal(4, second!.Batch.Index);
        Assert.Empty(second.Records);
        Assert.True(await Ledger.ReleaseWorkBatchAsync(submission, 4, second.Batch.LeaseOwner!, Now));
        Assert.Equal(1, await Ledger.CountWorkBatchesAsync(submission, WorkBatchStatus.Queued));

        // An expired batch lease is reclaimed by the sweep.
        var third = await Ledger.ClaimWorkBatchAsync(_flow, submission, "w3", TimeSpan.FromMinutes(1), Now);
        Assert.NotNull(third);
        Assert.Equal(0, await Ledger.CountWorkBatchesAsync(submission, WorkBatchStatus.Queued));
        _clock.Advance(TimeSpan.FromMinutes(2));
        await Ledger.ReclaimExpiredLeasesAsync(_flow, Now);
        Assert.Equal(1, await Ledger.CountWorkBatchesAsync(submission, WorkBatchStatus.Queued));
        Assert.Null(await Ledger.ClaimWorkBatchAsync(Guid.NewGuid(), null, "w4", TimeSpan.FromMinutes(1), Now));
    }

    [Fact]
    public async Task Step_progress_and_next_due_are_tracked_on_the_record()
    {
        var submission = Guid.NewGuid();
        await Ledger.UpsertPendingAsync([Pending("s", submission)]);
        var claimed = await Ledger.ClaimAsync(_flow, submission, "w", 10, TimeSpan.FromMinutes(5), Now);
        var key = claimed[0].DeliveryKey;
        await Ledger.SaveStepAsync(key, submission, "0:0:10", "{\"metadata\":{\"version\":\"3\"}}");
        Assert.Equal("{\"metadata\":{\"version\":\"3\"}}", (await Ledger.GetRecordAsync(_flow, key))!.PendingStepJson);

        var next = Now + TimeSpan.FromMinutes(10);
        await Ledger.CompleteAsync(new RecordCompletion
        {
            DeliveryKey = key,
            Status = RecordStatus.Pending,
            NextAttemptUtc = next,
            Error = "503",
            PendingStepJson = "{\"metadata\":{\"version\":\"3\"}}",
            Attempt = new AttemptRecord { DeliveryKey = key, SubmissionId = submission, Worker = "w", StartedUtc = Now, CompletedUtc = Now, Outcome = AttemptOutcome.Failed, Phase = "none", Error = "503" },
        });
        Assert.Equal(next, await Ledger.NextDueAsync(_flow, submission, Now));
        Assert.Null(await Ledger.NextDueAsync(_flow, submission, next));
        Assert.True(await Ledger.HasPendingAsync(_flow, submission, Now));
        Assert.Equal("{\"metadata\":{\"version\":\"3\"}}", (await Ledger.GetRecordAsync(_flow, key))!.PendingStepJson);

        _clock.Advance(TimeSpan.FromMinutes(11));
        var again = await Ledger.ClaimAsync(_flow, submission, "w", 10, TimeSpan.FromMinutes(5), Now);
        Assert.Single(again);
        await Ledger.CompleteAsync(new RecordCompletion
        {
            DeliveryKey = key,
            Status = RecordStatus.Delivered,
            Promote = true,
            TargetVersion = 4,
            TargetStateJson = JsonMerge.Merge(again[0].TargetStateJson, "{\"version\":\"4\"}"),
            Attempt = new AttemptRecord { DeliveryKey = key, SubmissionId = submission, Worker = "w", StartedUtc = Now, CompletedUtc = Now, Outcome = AttemptOutcome.Delivered, Phase = "metadata" },
        });
        var delivered = await Ledger.GetRecordAsync(_flow, key);
        Assert.Null(delivered!.PendingStepJson);
        Assert.Equal("{\"version\":\"4\"}", delivered.TargetStateJson);
        Assert.Null(await Ledger.NextDueAsync(_flow, submission, Now));
        Assert.False(await Ledger.HasPendingAsync(_flow, submission, Now));
    }

    [Fact]
    public async Task Verify_reconcile_watermarks_known_state_and_prune()
    {
        var s = Guid.NewGuid();
        await Ledger.UpsertPendingAsync([Pending("a", s)]);
        var claimed = await Ledger.ClaimAsync(_flow, s, "w", 10, TimeSpan.FromMinutes(1), Now);
        var key = claimed[0].DeliveryKey;
        await Ledger.CompleteAsync(new RecordCompletion
        {
            DeliveryKey = key,
            Status = RecordStatus.Delivered,
            Promote = true,
            TargetVersion = 7,
            Attempt = new AttemptRecord { DeliveryKey = key, Worker = "w", StartedUtc = Now, CompletedUtc = Now, Outcome = AttemptOutcome.Delivered, Phase = "metadata" },
        });

        var due = await Ledger.ListForVerifyAsync(_flow, null, 10);
        Assert.Single(due);
        await Ledger.RecordVerifyAsync(key, VerifyOutcome.Drifted, 9, Now, requeue: true);
        var state = await Ledger.GetRecordAsync(_flow, key);
        Assert.Equal(VerifyOutcome.Drifted, state!.LastVerifyOutcome);
        Assert.Null(state.MetadataHash);
        Assert.Equal(RecordStatus.Delivered, state.Status);
        Assert.Empty(await Ledger.ListForVerifyAsync(_flow, Now - TimeSpan.FromHours(1), 10));

        await Ledger.SetWatermarksAsync([new SourceWatermark(_flow, "logSource=X", "t1", 5, Now)]);
        await Ledger.SetWatermarksAsync([new SourceWatermark(_flow, "logSource=X", "t1", 6, Now)]);
        var marks = await Ledger.GetWatermarksAsync(_flow, "logSource=X");
        Assert.Equal(6, marks.Single().Version);

        var known = await Ledger.KnownStateAsync(_flow);
        Assert.Single(known);
        Assert.Equal(7, known[0].TargetVersion);

        for (var i = 0; i < 3; i++)
        {
            await Ledger.CompleteAsync(new RecordCompletion
            {
                DeliveryKey = key,
                Status = RecordStatus.Delivered,
                Attempt = new AttemptRecord { DeliveryKey = key, Worker = "w", StartedUtc = Now - TimeSpan.FromDays(100), CompletedUtc = Now, Outcome = AttemptOutcome.Skipped, Phase = "none" },
            });
        }

        var pruned = await Ledger.PruneAttemptsAsync(Now - TimeSpan.FromDays(30));
        Assert.Equal(2, pruned);
        Assert.Equal(2, (await Ledger.ListAttemptsAsync(key, 10)).Count);
    }

    [Fact]
    public async Task Work_for_a_record_in_flight_queues_behind_the_delivery_and_its_completion_leaves_it_pending()
    {
        var s1 = Guid.NewGuid();
        await Ledger.UpsertPendingAsync([Pending("a", s1) with { PendingSourceModifiedUtc = Now.AddDays(-2) }]);
        var claimed = (await Ledger.ClaimAsync(_flow, s1, "w1", 10, TimeSpan.FromMinutes(5), Now)).Single();

        var s2 = Guid.NewGuid();
        var staging = await Ledger.UpsertPendingAsync([Pending("a", s2) with { PendingDocumentRef = "7:0:10", WorkBatch = 7, PendingMetadataHash = "mh2", PendingSourceModifiedUtc = Now.AddDays(-1) }]);
        Assert.Equal(1, staging.Staged);
        Assert.Empty(staging.Refused);
        var queued = await Ledger.GetRecordAsync(_flow, claimed.DeliveryKey);
        Assert.Equal(RecordStatus.Delivering, queued!.Status);
        Assert.Equal(claimed.LeaseOwner, queued.LeaseOwner);
        Assert.Equal(s2, queued.LastSubmissionId);
        Assert.Equal("7:0:10", queued.PendingDocumentRef);

        // The in-flight try's step progress belongs to its own document and never reaches the newer work.
        await Ledger.SaveStepAsync(claimed.DeliveryKey, s1, "0:0:10", "{\"metadata\":{\"version\":\"1\"}}");
        Assert.Null((await Ledger.GetRecordAsync(_flow, claimed.DeliveryKey))!.PendingStepJson);

        await Ledger.CompleteAsync(new RecordCompletion
        {
            DeliveryKey = claimed.DeliveryKey,
            Status = RecordStatus.Delivered,
            Promote = true,
            TargetVersion = 1,
            Claimed = ClaimedWork.Of(claimed),
            Attempt = new AttemptRecord { DeliveryKey = claimed.DeliveryKey, SubmissionId = s1, Worker = "w1", StartedUtc = Now, CompletedUtc = Now, Outcome = AttemptOutcome.Delivered, Phase = "metadata+payload" },
        });

        var settled = await Ledger.GetRecordAsync(_flow, claimed.DeliveryKey);
        Assert.Equal(RecordStatus.Pending, settled!.Status);
        Assert.Null(settled.LeaseOwner);
        Assert.Equal("mh", settled.MetadataHash);
        Assert.Equal(Now.AddDays(-2), settled.SourceModifiedUtc);
        Assert.NotNull(settled.LastDeliveredUtc);
        Assert.Equal("mh2", settled.PendingMetadataHash);
        Assert.Equal("7:0:10", settled.PendingDocumentRef);
        Assert.Equal(0, settled.AttemptCount);
        Assert.Equal(1, await Ledger.CountAttemptsAsync(s1, AttemptOutcome.Delivered));
        Assert.Equal(0, await Ledger.CountAttemptsAsync(s2, AttemptOutcome.Delivered));
        Assert.Single(await Ledger.ClaimAsync(_flow, s2, "w2", 10, TimeSpan.FromMinutes(5), Now));
    }

    [Fact]
    public async Task Work_older_than_what_the_record_holds_is_refused_and_a_stale_skip_is_recorded_without_touching_it()
    {
        var s1 = Guid.NewGuid();
        var delivered = Now.AddDays(-1);
        await Ledger.UpsertPendingAsync([Pending("a", s1) with { PendingSourceModifiedUtc = delivered, PendingPayloadModifiedUtc = delivered }]);
        var claimed = (await Ledger.ClaimAsync(_flow, s1, "w", 10, TimeSpan.FromMinutes(5), Now)).Single();
        await Ledger.CompleteAsync(new RecordCompletion
        {
            DeliveryKey = claimed.DeliveryKey,
            Status = RecordStatus.Delivered,
            Promote = true,
            TargetVersion = 1,
            Claimed = ClaimedWork.Of(claimed),
            Attempt = new AttemptRecord { DeliveryKey = claimed.DeliveryKey, SubmissionId = s1, Worker = "w", StartedUtc = Now, CompletedUtc = Now, Outcome = AttemptOutcome.Delivered, Phase = "metadata+payload" },
        });
        var key = claimed.DeliveryKey;
        Assert.Equal(delivered, (await Ledger.GetRecordAsync(_flow, key))!.PayloadModifiedUtc);

        var s2 = Guid.NewGuid();
        var olderSource = await Ledger.UpsertPendingAsync([Pending("a", s2) with { PendingSourceModifiedUtc = delivered.AddHours(-1) }]);
        Assert.Equal(0, olderSource.Staged);
        Assert.Equal(key, Assert.Single(olderSource.Refused));
        var olderPayload = await Ledger.UpsertPendingAsync([Pending("a", s2) with { PendingSourceModifiedUtc = delivered.AddHours(1), PendingPayloadModifiedUtc = delivered.AddHours(-1) }]);
        Assert.Equal(key, Assert.Single(olderPayload.Refused));
        var untouched = await Ledger.GetRecordAsync(_flow, key);
        Assert.Equal(RecordStatus.Delivered, untouched!.Status);
        Assert.Equal(s1, untouched.LastSubmissionId);

        // Newer work is staged, and then stands against anything older than itself; work carrying no moment is never refused.
        Assert.Equal(1, (await Ledger.UpsertPendingAsync([Pending("a", s2) with { PendingSourceModifiedUtc = delivered.AddHours(2) }])).Staged);
        Assert.Single((await Ledger.UpsertPendingAsync([Pending("a", s2) with { PendingSourceModifiedUtc = delivered.AddHours(1) }])).Refused);
        Assert.Equal(1, (await Ledger.UpsertPendingAsync([Pending("a", s2)])).Staged);

        await Ledger.MarkSkippedAsync(_flow, [new SkippedRecord { DeliveryKey = key, Kind = SkipKind.Stale, Reason = "the drop carries an older version", SourceModifiedUtc = delivered.AddHours(-1) }], s2);
        var stale = (await Ledger.ListAttemptsAsync(key, 10)).Single(a => a.Phase == AttemptPhases.Stale);
        Assert.Equal(AttemptOutcome.Skipped, stale.Outcome);
        Assert.Equal(s2, stale.SubmissionId);
        Assert.Null(stale.Error);
        Assert.Contains("older version", stale.ResultJson, StringComparison.Ordinal);
        Assert.Equal(1, await Ledger.CountAttemptsAsync(s2, AttemptOutcome.Skipped, AttemptPhases.Stale));
    }

    [Fact]
    public async Task A_rendered_skip_advances_a_delivered_record_and_leaves_queued_work_with_its_submission()
    {
        var s1 = Guid.NewGuid();
        await Ledger.UpsertPendingAsync([Pending("a", s1) with { PendingSourceModifiedUtc = Now.AddDays(-2) }]);
        var claimed = (await Ledger.ClaimAsync(_flow, s1, "w", 10, TimeSpan.FromMinutes(5), Now)).Single();
        await Ledger.CompleteAsync(new RecordCompletion
        {
            DeliveryKey = claimed.DeliveryKey,
            Status = RecordStatus.Delivered,
            Promote = true,
            TargetVersion = 1,
            Claimed = ClaimedWork.Of(claimed),
            Attempt = new AttemptRecord { DeliveryKey = claimed.DeliveryKey, SubmissionId = s1, Worker = "w", StartedUtc = Now, CompletedUtc = Now, Outcome = AttemptOutcome.Delivered, Phase = "metadata+payload" },
        });
        await Ledger.UpsertPendingAsync([Pending("b", s1) with { PendingSourceModifiedUtc = Now.AddDays(-2) }]);
        var a = claimed.DeliveryKey;
        var b = DeliveryKey.Derive("test", ["b"]);

        var s2 = Guid.NewGuid();
        await Ledger.MarkSkippedAsync(_flow, [
            new SkippedRecord { DeliveryKey = a, Kind = SkipKind.Rendered, Reason = "hashes unchanged", SourceModifiedUtc = Now.AddDays(-1), RenderContext = "{\"cache\":\"2\"}" },
            new SkippedRecord { DeliveryKey = b, Kind = SkipKind.Unchanged, Reason = "unchanged" },
        ], s2);

        var advanced = await Ledger.GetRecordAsync(_flow, a);
        Assert.Equal(s2, advanced!.LastSubmissionId);
        Assert.Equal(Now.AddDays(-1), advanced.SourceModifiedUtc);
        Assert.Equal("{\"cache\":\"2\"}", advanced.RenderContext);
        Assert.Equal(s1, (await Ledger.GetRecordAsync(_flow, b))!.LastSubmissionId);

        // A render identical to the queued work lends that work the newer moment it was found at.
        await Ledger.MarkSkippedAsync(_flow, [new SkippedRecord { DeliveryKey = b, Kind = SkipKind.Rendered, Reason = "equal to the version already queued", SourceModifiedUtc = Now }], s2);
        var queued = await Ledger.GetRecordAsync(_flow, b);
        Assert.Equal(s1, queued!.LastSubmissionId);
        Assert.Equal(Now, queued.PendingSourceModifiedUtc);
        Assert.Equal(RecordStatus.Pending, queued.Status);
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }
}

