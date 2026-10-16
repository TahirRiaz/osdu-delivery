using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Releasing and redelivering every record a set error reached, on SQL Server: a run sends what every settled submission
/// still holds, however many submissions a release reached; a redelivery of many records walks them a slice at a time and
/// names each one under its activity, so every record's history shows it; and a record still blocked by a problem keeps the
/// error its problem was read from.
/// </summary>
[Collection(SqlServerSuite.Name)]
public sealed class ReleaseAtScaleTests : IAsyncLifetime, IDisposable
{
    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly Guid _flow = FlowId.Of("scale-flow");

    private OsduLedger Ledger => _db.Ledger(_clock);

    public Task InitializeAsync() => Ledger.RegisterAsync(_flow);

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose() => _db.Dispose();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private RecordState Pending(string sourceKey, Guid submission) => new()
    {
        DeliveryKey = DeliveryKey.Derive("scale", [sourceKey]),
        FlowId = _flow,
        SourceKey = sourceKey,
        MappingName = "Thing",
        TargetId = "dev:master-data--Thing:" + sourceKey,
        LastSubmissionId = submission,
        PendingDocumentRef = "0:0:10",
        PendingRenderContext = "{}",
        PendingSourceFingerprint = "fp",
        PendingMetadataHash = "mh",
        PendingMetadata = true,
    };

    private SubmissionState Submission(Guid id) => new()
    {
        SubmissionId = id,
        FlowId = _flow,
        FlowName = "scale-flow",
        MappingReference = "Thing@1.0.0",
        RenderContext = "{}",
        SourceConnection = "${env:OSDU_DATA_DB}",
        SourceObject = "OsduData.arc.Thing",
        RecordCount = 1,
    };

    [Fact]
    public async Task Every_settled_submission_holding_released_work_is_listed_a_page_at_a_time()
    {
        // Twelve settled submissions, each still holding a record released back to pending with its document.
        var submissions = new List<Guid>();
        for (var i = 0; i < 12; i++)
        {
            var id = Guid.NewGuid();
            var (registered, _) = await Ledger.RegisterSubmissionAsync(Submission(id));
            await Ledger.UpdateSubmissionAsync(registered with { Status = SubmissionStatus.Completed });
            await Ledger.UpsertPendingAsync(_flow, [Pending($"S-{i}", id)]);
            submissions.Add(id);
        }

        var first = await Ledger.ListSettledSubmissionsWithDueWorkAsync(_flow, [], Now, 10);
        Assert.Equal(10, first.Count);
        var rest = await Ledger.ListSettledSubmissionsWithDueWorkAsync(_flow, first.ToList(), Now, 10);
        Assert.Equal(2, rest.Count);
        Assert.Equal(submissions.Order(), first.Concat(rest).Order());
        Assert.Empty(await Ledger.ListSettledSubmissionsWithDueWorkAsync(_flow, submissions, Now, 10));
    }

    [Fact]
    public async Task A_redelivery_of_every_delivered_record_walks_them_and_names_each_under_its_activity()
    {
        var sliced = new OsduLedger(_db.CreateDbContext, _clock) { WriteSlice = 2 };
        var submission = Guid.NewGuid();
        var records = Enumerable.Range(0, 5).Select(i => Pending($"D-{i}", submission)).ToList();
        await sliced.UpsertPendingAsync(_flow, records);
        await sliced.ClaimAsync(_flow, submission, "w", 10, TimeSpan.FromMinutes(5), Now);
        await sliced.CompleteManyAsync(_flow, records.Select(r => new RecordCompletion
        {
            DeliveryKey = r.DeliveryKey,
            Status = RecordStatus.Delivered,
            Promote = true,
            TargetVersion = 1,
            Attempt = new AttemptRecord { DeliveryKey = r.DeliveryKey, Worker = "w", StartedUtc = Now, CompletedUtc = Now, Outcome = AttemptOutcome.Delivered, Phase = "metadata" },
        }).ToList());

        _clock.Advance(TimeSpan.FromSeconds(1));
        var redelivery = await sliced.StartActivityAsync(new ActivityRecord { FlowId = _flow, FlowName = "scale-flow", Kind = "redeliver", Actor = "user:test", StartedUtc = Now });
        Assert.Equal(5, await sliced.ForceRedeliverAsync(_flow, null, new RedeliverSelection(RedeliverScope.All, []), redelivery.ActivityId, Now));

        foreach (var record in records)
        {
            var marked = await sliced.GetRecordAsync(_flow, record.DeliveryKey);
            Assert.Equal((Now, (string?)null), (marked!.PlanRequestedUtc, marked.MetadataHash));
            Assert.Contains(await sliced.ListActivitiesAsync(new ActivityQuery { FlowId = _flow, DeliveryKey = record.DeliveryKey.Value }), a => a.ActivityId == redelivery.ActivityId);
        }

        await using var db = _db.CreateDbContext();
        Assert.Equal(5, await db.DeliveryActivityRecords.CountAsync(l => l.ActivityId == redelivery.ActivityId));
        Assert.Equal(5, (await sliced.ListPlanRequestedAsync(_flow, null, 10)).Count);
    }

    [Fact]
    public async Task A_record_still_blocked_by_a_problem_keeps_its_error_when_it_is_asked_to_be_sent_again()
    {
        const string error = "osdu.tags.Tag4: dataset.tag4 is empty, and the entry is required";
        var held = Pending("H-1", Guid.NewGuid()) with { PendingDocumentRef = null, LastError = error };
        await Ledger.MarkHeldAsync(_flow, [held]);

        Assert.Equal(1, await Ledger.ForceRedeliverAsync(_flow, [held.DeliveryKey], RedeliverScope.All, Now));

        var record = await Ledger.GetRecordAsync(_flow, held.DeliveryKey);
        Assert.Equal((error, ProblemSignature.Of(error), (DateTime?)Now), (record!.LastError, record.ProblemHash, record.PlanRequestedUtc));
        Assert.Equal(1, (await Ledger.ListProblemsAsync(_flow, 10)).TotalRecords);
    }
}
