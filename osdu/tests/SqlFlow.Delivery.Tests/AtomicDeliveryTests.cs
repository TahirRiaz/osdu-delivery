using System.Globalization;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A route that delivers a record in steps, as the multi-call routes do (docs/atomic-delivery-plan.md): a dataset registered
/// for its files (an intent first, then the id), the record written, a bulk session opened and committed, and a last read.
/// It keeps what OSDU would hold, can fail at any step (or lose a call's answer after the call landed), and undoes what it is
/// asked to as a route does: removing what a unit created, writing back the version a write replaced, leaving the record to
/// newer work.
/// </summary>
internal sealed class StagedRoute : IDeliveryProtocol
{
    private readonly object _gate = new();
    private long _version = 5000;
    private int _datasets;
    private int _uploads;

    /// <summary>What OSDU holds: every live record and dataset, with its version.</summary>
    public Dictionary<string, long> Live { get; } = new(StringComparer.Ordinal);

    /// <summary>Where each dataset's file landed, as the index would find it by its landing-zone path.</summary>
    public Dictionary<string, string> ByLocator { get; } = new(StringComparer.Ordinal);

    /// <summary>The failure to raise at the start of a step of a record's delivery, or none.</summary>
    public Func<DeliveryWork, string, Exception?>? FailAt { get; set; }

    /// <summary>A step whose call lands and whose answer is lost, raising this failure after the call; or none.</summary>
    public Func<DeliveryWork, string, Exception?>? LoseAnswerAt { get; set; }

    /// <summary>
    /// Whether the index lists a registration whose answer was lost, so a resumed try takes it over; false stands for an index
    /// that has not caught up, where the route uploads and registers the file again, as the file route does.
    /// </summary>
    public bool IndexLists { get; set; } = true;

    /// <summary>How an undo answers one artifact, or null for what the route does by default.</summary>
    public Func<UndoWork, UndoItem, UndoResult?>? UndoWith { get; set; }

    public List<DeliveryWork> Deliveries { get; } = [];

    public List<UndoWork> Undos { get; } = [];

    public List<(string TargetId, long Version)> Restores { get; } = [];

    /// <summary>Datasets registered, per record key, across every try.</summary>
    public Dictionary<DeliveryKey, int> Registrations { get; } = [];

    public DeliveryProtocol Kind => DeliveryProtocol.Ddms;

    public bool Undoes => true;

    public async Task<DeliveryOutcome> DeliverAsync(DeliveryWork work, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Deliveries.Add(work);
        }

        var returned = new Dictionary<string, string>(StringComparer.Ordinal) { ["recordId"] = work.TargetId };
        var superseded = new List<string>();
        string? datasetId = null;
        if (work.DeliverPayload)
        {
            var mark = work.Completed("register");
            if (mark?.TryGetValue("datasetId", out var registered) == true)
            {
                datasetId = registered;
            }
            else if (IndexLists && mark?.TryGetValue("fileSource", out var landed) == true && ByLocator.TryGetValue(landed, out var adopted))
            {
                // A registration an earlier try sent and never heard back from, found by where its file landed and taken over.
                datasetId = adopted;
                await work.ReportStepAsync("register", new Dictionary<string, string> { ["datasetId"] = adopted, ["fileSource"] = landed, ["adopted"] = "true" }, [TargetArtifact.Created($"dataset:{landed}", ArtifactRoles.Dataset, adopted, locator: landed)], ct);
            }
            else
            {
                // Each upload lands at a path of its own, so each registration is an artifact of its own.
                string locator;
                lock (_gate)
                {
                    locator = $"landing/{work.Key}/{++_uploads}";
                }

                await work.ReportStepAsync("register", new Dictionary<string, string> { ["state"] = "registering", ["fileSource"] = locator }, [TargetArtifact.Intent($"dataset:{locator}", ArtifactRoles.Dataset, locator)], ct);
                Fail(work, "register");
                lock (_gate)
                {
                    datasetId = $"dev:dataset--File.Generic:{++_datasets}";
                    Live[datasetId] = 1;
                    ByLocator[locator] = datasetId;
                    Registrations[work.Key] = Registrations.GetValueOrDefault(work.Key) + 1;
                }

                Lose(work, "register");
                await work.ReportStepAsync("register", new Dictionary<string, string> { ["datasetId"] = datasetId, ["fileSource"] = locator }, [TargetArtifact.Created($"dataset:{locator}", ArtifactRoles.Dataset, datasetId, locator: locator)], ct);
            }

            returned["datasetIds"] = datasetId;
            if (work.TargetState.TryGetValue("datasetIds", out var earlier) && earlier != datasetId)
            {
                superseded.Add(earlier);
            }
        }

        long? version = work.ExistingVersion;
        if (work.DeliverMetadata && work.Completed("metadata") is null)
        {
            Fail(work, "metadata");
            version = Write(work.TargetId);
            Lose(work, "metadata");
            await work.ReportStepAsync("metadata", Values(version.Value), [TargetArtifact.RecordWritten(work.TargetId, version, work.ExistingVersion)], ct);
        }
        else if (work.Completed("metadata") is { } written)
        {
            version = long.Parse(written["version"], CultureInfo.InvariantCulture);
        }

        if (work.DeliverPayload && work.Completed("commit") is null)
        {
            var session = $"session-{work.Key}-{work.Unit?.Id:N}";
            await work.ReportStepAsync("session", new Dictionary<string, string> { ["sessionId"] = session }, [TargetArtifact.Created("session", ArtifactRoles.Session, session, locator: work.TargetId)], ct);
            Fail(work, "commit");
            version = Write(work.TargetId);
            await work.ReportStepAsync("commit", Values(version.Value), ct);
        }

        Fail(work, "finish");
        if (version is { } v)
        {
            returned["version"] = v.ToString(CultureInfo.InvariantCulture);
        }

        return new DeliveryOutcome
        {
            MetadataDelivered = work.DeliverMetadata,
            PayloadDelivered = work.DeliverPayload,
            TargetVersion = version,
            Returned = returned,
            Superseded = superseded,
        };
    }

    public Task<IReadOnlyList<UndoResult>> UndoAsync(IReadOnlyList<UndoWork> works, CancellationToken ct = default)
    {
        var results = new List<UndoResult>();
        lock (_gate)
        {
            foreach (var work in works)
            {
                Undos.Add(work);
                foreach (var item in work.Items)
                {
                    results.Add(UndoWith?.Invoke(work, item) ?? Default(work, item));
                }
            }
        }

        return Task.FromResult<IReadOnlyList<UndoResult>>(results);
    }

    private UndoResult Default(UndoWork work, UndoItem item)
    {
        var artifact = item.Artifact;
        if (work.KeepRecord && ArtifactRoles.IsTheRecord(artifact.Role))
        {
            return UndoResult.Superseded(item, "the newer work writes the record again");
        }

        switch (artifact.Role)
        {
            case ArtifactRoles.Version:
                Restores.Add((artifact.TargetId!, artifact.PriorVersion!.Value));
                Live[artifact.TargetId!] = ++_version;
                return UndoResult.Restored(item, $"version {artifact.PriorVersion} written back as version {_version}") with { Rewrite = (artifact.PriorVersion.Value, _version) };
            case ArtifactRoles.Session:
                return UndoResult.Removed(item, "abandoned");
            default:
                var id = artifact.TargetId ?? (artifact.Locator is { } locator && ByLocator.TryGetValue(locator, out var found) ? found : null);
                return id is not null && Live.Remove(id) ? UndoResult.Removed(item) : UndoResult.Gone(item);
        }
    }

    private long Write(string targetId)
    {
        lock (_gate)
        {
            var version = ++_version;
            Live[targetId] = version;
            return version;
        }
    }

    private void Fail(DeliveryWork work, string step)
    {
        if (FailAt?.Invoke(work, step) is { } failure)
        {
            throw failure;
        }
    }

    private void Lose(DeliveryWork work, string step)
    {
        if (LoseAnswerAt?.Invoke(work, step) is { } failure)
        {
            throw failure;
        }
    }

    private static Dictionary<string, string> Values(long version) => new(StringComparer.Ordinal) { ["version"] = version.ToString(CultureInfo.InvariantCulture) };

    public Task<VerifyResult> VerifyAsync(string targetId, long? expectedVersion, CancellationToken ct = default)
        => Task.FromResult(new VerifyResult(VerifyOutcome.Match, expectedVersion, null));

    public Task<DeleteOutcome> DeleteAsync(string targetId, RemovalScope scope, IReadOnlyDictionary<string, string>? targetState = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(Live.Remove(targetId) ? new DeleteOutcome(true, false, "removed") : new DeleteOutcome(false, true, "record not found in OSDU"));
        }
    }

    public Task<JsonObject?> ReadAsync(string targetId, CancellationToken ct = default) => Task.FromResult<JsonObject?>(null);

    public Task<ProbeOutcome> ProbeAsync(CancellationToken ct = default) => Task.FromResult(new ProbeOutcome(true, 200, "the service answered", "/about"));
}

/// <summary>
/// Deliveries that complete or undo themselves (docs/atomic-delivery-plan.md), end to end over the sample estate, the worker,
/// the module's database on SQL Server and a route that delivers in steps: what each unit created is recorded as it is created,
/// made live when the unit commits, and undone when it aborts, is abandoned, or fails its budget; an undo that cannot reach OSDU
/// is tried again by the sweep and never lost.
/// </summary>
[Collection(SqlServerSuite.Name)]
public sealed class AtomicDeliveryTests : IDisposable
{
    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private static int LogCount => SampleEstate.Logs().Count;

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A file still open on a slow agent; the temp directory is the agent's to clear.
        }
    }

    [Fact]
    public async Task A_unit_that_completes_makes_the_ids_it_minted_live_and_keeps_nothing_of_its_progress()
    {
        var (tables, runtime, route, ledger) = await EstateAsync();
        using (runtime)
        {
            Assert.Equal(LogCount, (await RunAsync(runtime, route, ledger)).Delivered);

            foreach (var key in Keys())
            {
                var artifacts = await ledger.RecordArtifactsAsync(runtime.Flow.Id, key, 50);
                var dataset = Assert.Single(artifacts);
                Assert.Equal((ArtifactRoles.Dataset, ArtifactStatus.Live), (dataset.Role, dataset.Status));
                Assert.True(route.Live.ContainsKey(dataset.TargetId!));
                Assert.StartsWith("landing/", dataset.Locator, StringComparison.Ordinal);

                // The attempt names the unit its artifacts carry, and the unit is not one of its steps.
                var delivered = (await ledger.ListAttemptsAsync(runtime.Flow.Id, key, 10)).Single(a => a.Outcome == AttemptOutcome.Delivered);
                var result = JsonNode.Parse(delivered.ResultJson!)!;
                Assert.Equal(dataset.UnitId.ToString("D"), result["unit"]!.GetValue<string>());
                Assert.DoesNotContain(result["steps"]!.AsArray(), s => s!["name"]!.GetValue<string>() == DeliveryUnit.StepName);
            }

            Assert.Equal(ArtifactCounts.None, await ledger.ArtifactCountsAsync(runtime.Flow.Id));
            Assert.Empty(route.Undos);
            Assert.Equal(0, tables.Records.Count(r => r.DeletedUtc is not null));
        }
    }

    [Fact]
    public async Task A_try_held_after_the_record_was_written_removes_the_record_and_what_its_unit_registered()
    {
        var (_, runtime, route, ledger) = await EstateAsync();
        using (runtime)
        {
            var held = Keys()[0];
            route.FailAt = (work, step) => work.Key == held && step == "commit" ? new RecordHeldException("the DDMS refused the bulk data: 422 reference curve does not cover the bulk") : null;

            var summary = await RunAsync(runtime, route, ledger);

            Assert.Equal((LogCount - 1L, 1L), (summary.Delivered, summary.Held));
            var record = (await ledger.GetRecordAsync(runtime.Flow.Id, held))!;
            Assert.Equal(RecordStatus.Held, record.Status);
            Assert.False(route.Live.ContainsKey(record.TargetId!));
            var undo = Assert.Single(route.Undos);
            Assert.Equal((UndoReason.Held, false), (undo.Reason, undo.KeepRecord));
            Assert.Equal([ArtifactRoles.Dataset, ArtifactRoles.Record, ArtifactRoles.Session], undo.Items.Select(i => i.Artifact.Role));

            var artifacts = (await ledger.RecordArtifactsAsync(runtime.Flow.Id, held, 50)).OrderBy(a => a.ArtifactId).ToList();
            Assert.All(artifacts, a => Assert.Equal(ArtifactStatus.Removed, a.Status));
            Assert.All(artifacts, a => Assert.NotNull(a.SettledUtc));
            Assert.DoesNotContain(route.Live.Keys, k => artifacts.Any(a => a.TargetId == k));

            // The undo is on the record's history, after the held try, naming every artifact and what became of it.
            var attempts = await ledger.ListAttemptsAsync(runtime.Flow.Id, held, 10);
            var undone = Assert.Single(attempts, a => a.Outcome == AttemptOutcome.Undone);
            Assert.Equal(AttemptPhases.Undo, undone.Phase);
            Assert.Null(undone.Error);
            var result = JsonNode.Parse(undone.ResultJson!)!["undo"]!;
            Assert.Equal("held", result["reason"]!.GetValue<string>());
            Assert.Equal(3, result["artifacts"]!.AsArray().Count);
            Assert.Equal("3 removed", result["summary"]!.GetValue<string>());
            Assert.Contains(attempts, a => a.Outcome == AttemptOutcome.Held);
            Assert.Equal(ArtifactCounts.None, await ledger.ArtifactCountsAsync(runtime.Flow.Id));
        }
    }

    [Fact]
    public async Task A_try_that_fails_for_now_resumes_its_unit_and_creates_nothing_twice()
    {
        var (_, runtime, route, ledger) = await EstateAsync();
        using (runtime)
        {
            var key = Keys()[0];
            var failures = 1;
            route.FailAt = (work, step) => work.Key == key && step == "commit" && failures-- > 0 ? new DeliveryException("HTTP 503 Service Unavailable from POST /sessions") : null;

            var first = await RunAsync(runtime, route, ledger);
            Assert.Equal(1, first.Retried);
            var pending = (await ledger.GetRecordAsync(runtime.Flow.Id, key))!;
            Assert.Equal(RecordStatus.Pending, pending.Status);
            Assert.Contains(DeliveryUnit.StepName, pending.PendingStepJson, StringComparison.Ordinal);
            var open = await ledger.OpenArtifactsAsync(runtime.Flow.Id, [key]);
            Assert.Equal([ArtifactStatus.Pending, ArtifactStatus.Pending, ArtifactStatus.Pending], open.Select(a => a.Status));
            var unit = Assert.Single(open.Select(a => a.UnitId).Distinct());

            // The sweep leaves a unit its record still carries.
            Assert.True((await runtime.UndoUnfinishedAsync(exhausted: false)).Idle);

            _clock.Advance(TimeSpan.FromMinutes(5));
            var second = await DrainAsync(runtime, route, ledger);

            Assert.Equal(1, second.Delivered);
            Assert.Equal(1, route.Registrations[key]);
            var resumed = route.Deliveries.Last(d => d.Key == key);
            Assert.Equal(unit, resumed.Unit!.Id);
            Assert.DoesNotContain(DeliveryUnit.StepName, resumed.CompletedSteps.Keys);
            var dataset = Assert.Single(await ledger.RecordArtifactsAsync(runtime.Flow.Id, key, 50));
            Assert.Equal((ArtifactStatus.Live, unit), (dataset.Status, dataset.UnitId));
            Assert.Empty(route.Undos);
        }
    }

    [Fact]
    public async Task A_unit_that_spends_its_retry_budget_is_undone_as_failed()
    {
        var (_, runtime, route, ledger) = await EstateAsync();
        using (runtime)
        {
            var key = Keys()[1];
            route.FailAt = (work, step) => work.Key == key && step == "finish" ? new DeliveryException("HTTP 502 Bad Gateway from GET /welllogs") : null;

            await RunAsync(runtime, route, ledger);
            for (var i = 0; i < 4 && (await ledger.GetRecordAsync(runtime.Flow.Id, key))!.Status == RecordStatus.Pending; i++)
            {
                _clock.Advance(TimeSpan.FromHours(1));
                await DrainAsync(runtime, route, ledger);
            }

            var record = (await ledger.GetRecordAsync(runtime.Flow.Id, key))!;
            Assert.Equal(RecordStatus.Failed, record.Status);
            Assert.Equal(1, route.Registrations[key]);
            var undo = Assert.Single(route.Undos);
            Assert.Equal(UndoReason.Failed, undo.Reason);
            Assert.False(route.Live.ContainsKey(record.TargetId!));
            Assert.All(await ledger.RecordArtifactsAsync(runtime.Flow.Id, key, 50), a => Assert.Equal(ArtifactStatus.Removed, a.Status));
        }
    }

    [Fact]
    public async Task An_update_held_after_its_metadata_write_puts_back_the_version_it_replaced()
    {
        var (tables, runtime, route, ledger) = await EstateAsync();
        using (runtime)
        {
            await RunAsync(runtime, route, ledger);
            var key = Keys()[0];
            var delivered = (await ledger.GetRecordAsync(runtime.Flow.Id, key))!;
            var committed = delivered.TargetVersion!.Value;

            _clock.Advance(TimeSpan.FromMinutes(10));
            SampleEstate.Change(tables.Records[0], "creator", "HAL", Now, SampleWellLogs.UpdatedUtc(0).AddHours(2));
            route.FailAt = (work, step) => work.Key == key && step == "finish" ? new RecordHeldException("the DDMS answered 422: the record does not match its schema") : null;

            var summary = await RunAsync(runtime, route, ledger, force: true);

            Assert.Equal(1, summary.Held);
            var undo = Assert.Single(route.Undos);
            var version = Assert.Single(undo.Items);
            Assert.Equal((ArtifactRoles.Version, committed), (version.Artifact.Role, version.Artifact.PriorVersion));
            Assert.Equal(committed, undo.CommittedVersion);
            Assert.Equal((delivered.TargetId!, committed), Assert.Single(route.Restores));
            var artifact = (await ledger.RecordArtifactsAsync(runtime.Flow.Id, key, 50)).Single(a => a.Role == ArtifactRoles.Version);
            Assert.Equal(ArtifactStatus.Restored, artifact.Status);

            // The record the ledger holds is still the delivery before, now under the version its write-back got, so a verify
            // finds OSDU holding what the ledger says.
            var record = (await ledger.GetRecordAsync(runtime.Flow.Id, key))!;
            var written = route.Live[delivered.TargetId!];
            Assert.NotEqual(committed, written);
            Assert.Equal((RecordStatus.Held, written), (record.Status, record.TargetVersion!.Value));
            Assert.Equal(written.ToString(CultureInfo.InvariantCulture), JsonMerge.ToValues(record.TargetStateJson)["version"]);
            Assert.Equal(delivered.MetadataHash, record.MetadataHash);
        }
    }

    [Fact]
    public async Task A_unit_abandoned_by_newer_work_is_undone_before_the_newer_work_goes_and_leaves_the_record_to_it()
    {
        var (tables, runtime, route, ledger) = await EstateAsync();
        using (runtime)
        {
            var key = Keys()[0];
            var failing = true;
            route.FailAt = (work, step) => work.Key == key && step == "commit" && failing ? new DeliveryException("HTTP 504 Gateway Timeout from PATCH /sessions") : null;
            await RunAsync(runtime, route, ledger);
            var abandoned = await ledger.OpenArtifactsAsync(runtime.Flow.Id, [key]);
            Assert.Equal(3, abandoned.Count);
            var abandonedDataset = abandoned.Single(a => a.Role == ArtifactRoles.Dataset).TargetId!;

            // The source changes before the retry: the new work drops the unit's steps.
            failing = false;
            _clock.Advance(TimeSpan.FromMinutes(10));
            SampleEstate.Change(tables.Records[0], "creator", "HAL", Now, SampleWellLogs.UpdatedUtc(0).AddHours(2));
            var summary = await RunAsync(runtime, route, ledger, force: true);

            Assert.Equal(1, summary.Delivered);
            var undo = Assert.Single(route.Undos);
            Assert.Equal((UndoReason.Abandoned, true), (undo.Reason, undo.KeepRecord));
            Assert.False(route.Live.ContainsKey(abandonedDataset));
            var record = (await ledger.GetRecordAsync(runtime.Flow.Id, key))!;
            Assert.Equal(RecordStatus.Delivered, record.Status);
            Assert.True(route.Live.ContainsKey(record.TargetId!));

            var artifacts = (await ledger.RecordArtifactsAsync(runtime.Flow.Id, key, 50)).OrderBy(a => a.ArtifactId).ToList();
            Assert.Equal(ArtifactStatus.Removed, artifacts.Single(a => a.TargetId == abandonedDataset).Status);
            Assert.Equal(ArtifactStatus.Superseded, artifacts.Single(a => a.Role == ArtifactRoles.Record).Status);
            var live = artifacts.Single(a => a.Status == ArtifactStatus.Live);
            Assert.NotEqual(abandoned[0].UnitId, live.UnitId);
            Assert.True(route.Live.ContainsKey(live.TargetId!));

            // The undo happened before the newer work was sent.
            var attempts = await ledger.ListAttemptsAsync(runtime.Flow.Id, key, 20);
            var undone = attempts.Single(a => a.Outcome == AttemptOutcome.Undone);
            var deliveredAttempt = attempts.Single(a => a.Outcome == AttemptOutcome.Delivered);
            Assert.True(undone.AttemptId < deliveredAttempt.AttemptId);
        }
    }

    [Fact]
    public async Task An_undo_that_cannot_reach_OSDU_is_left_failed_and_the_sweep_finishes_it()
    {
        var (_, runtime, route, ledger) = await EstateAsync();
        using (runtime)
        {
            var key = Keys()[2];
            route.FailAt = (work, step) => work.Key == key && step == "commit" ? new RecordHeldException("HTTP 400 from the DDMS") : null;
            var reachable = false;
            route.UndoWith = (_, item) => reachable ? null : UndoResult.Failed(item, "HTTP 503 Service Unavailable from POST /api/storage/v2/records/delete");

            await RunAsync(runtime, route, ledger);

            var failed = await ledger.OpenArtifactsAsync(runtime.Flow.Id, [key]);
            Assert.Equal(3, failed.Count);
            Assert.All(failed, a => Assert.Equal((ArtifactStatus.Failed, 1), (a.Status, a.UndoAttempts)));
            Assert.All(failed, a => Assert.Equal(ArtifactLimits.RetryAt(Now, 1), a.NextUndoUtc));
            Assert.All(failed, a => Assert.Contains("503", a.Note, StringComparison.Ordinal));
            var counts = await ledger.ArtifactCountsAsync(runtime.Flow.Id);
            Assert.Equal((3L, 3L), (counts.Failed, counts.ToUndo));
            var first = (await ledger.ListAttemptsAsync(runtime.Flow.Id, key, 10)).Single(a => a.Outcome == AttemptOutcome.Undone);
            Assert.Contains("3 of 3 artifact(s) could not be undone yet", first.Error, StringComparison.Ordinal);

            // Before the backoff has passed, the sweep leaves them; after it, it takes them, and OSDU answers now.
            Assert.True((await runtime.UndoUnfinishedAsync(exhausted: false)).Idle);
            reachable = true;
            _clock.Advance(TimeSpan.FromMinutes(2));
            var swept = await runtime.UndoUnfinishedAsync(exhausted: false);

            Assert.Equal((1, 3, 0), (swept.Records, swept.Removed, swept.Failed));
            Assert.All(await ledger.RecordArtifactsAsync(runtime.Flow.Id, key, 50), a => Assert.Equal(ArtifactStatus.Removed, a.Status));
            Assert.Equal(ArtifactCounts.None, await ledger.ArtifactCountsAsync(runtime.Flow.Id));
            Assert.Equal(2, (await ledger.ListAttemptsAsync(runtime.Flow.Id, key, 20)).Count(a => a.Outcome == AttemptOutcome.Undone));
            var activities = await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = runtime.Flow.Id, Kind = DeliveryOperations.Undo });
            Assert.Equal([false, true], activities.Select(a => a.Idle));
            Assert.Equal("1 record(s) with what unfinished deliveries left: 3 removed", activities[0].Summary);
            Assert.Equal("nothing left by an unfinished delivery to undo", activities[1].Summary);
        }
    }

    [Fact]
    public async Task A_drain_of_the_whole_flow_ends_with_the_sweep_and_its_outcome_says_what_the_sweep_undid()
    {
        var (_, runtime, route, ledger) = await EstateAsync();
        using (runtime)
        {
            var key = Keys()[2];
            route.FailAt = (work, step) => work.Key == key && step == "commit" ? new RecordHeldException("HTTP 400 from the DDMS") : null;
            var reachable = false;
            route.UndoWith = (_, item) => reachable ? null : UndoResult.Failed(item, "HTTP 503 Service Unavailable from POST /api/storage/v2/records/delete");
            await RunAsync(runtime, route, ledger);
            reachable = true;
            _clock.Advance(TimeSpan.FromMinutes(2));

            // A drain of one submission is a fan-out member's share: it does not sweep.
            var submission = (await ledger.GetRecordAsync(runtime.Flow.Id, key))!.LastSubmissionId;
            Assert.NotNull(submission);
            var member = await runtime.WorkAsync(once: false, submission.Value);
            Assert.True(member.Undone.Idle);
            Assert.True(DrainOutcome.From(member, null).Undone.Idle);

            var drained = await runtime.WorkAsync(once: false);

            Assert.Equal((1, 3), (drained.Undone.Records, drained.Undone.Removed));
            var outcome = DrainOutcome.From(drained, null);
            Assert.Equal(drained.Undone, outcome.Undone);
            Assert.Contains("3 removed", drained.ToString(), StringComparison.Ordinal);
            Assert.Equal(ArtifactCounts.None, await ledger.ArtifactCountsAsync(runtime.Flow.Id));
        }
    }

    [Fact]
    public void An_undo_run_of_a_source_adds_up_what_each_interface_undid_and_counts_the_artifacts_it_settled()
    {
        InterfaceOutcome Interface(string name, object result) => new(
            name, Guid.NewGuid(), name, "ddms", null, [], [], 0, InterfaceStates.Completed, null, Now, Now, result);
        var outcome = new SourceRunOutcome(DeliveryOperations.Undo, "wells", [
            Interface("Wellbore", new UndoRunOutcome(DeliveryOperations.Undo, 2, 3, 1, 0, 1, 0, 1)),
            Interface("WellLog", new UndoRunOutcome(DeliveryOperations.Undo, 1, 2, 0, 1, 0, 0, 0)),
        ]);

        Assert.Equal(new UndoSummary(3, 5, 1, 1, 1, 0, 1), outcome.Undone);
        Assert.Equal(8, outcome.RowsLoaded);
        Assert.Contains("3 record(s) with what unfinished deliveries left", outcome.Describe(), StringComparison.Ordinal);
        Assert.DoesNotContain("delivered", outcome.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_undo_refused_every_time_is_left_for_an_operator_once_the_sweep_has_tried_it_enough()
    {
        var (_, runtime, route, ledger) = await EstateAsync();
        using (runtime)
        {
            var key = Keys()[0];
            route.FailAt = (work, step) => work.Key == key && step == "commit" ? new RecordHeldException("HTTP 400 from the DDMS") : null;
            route.UndoWith = (_, item) => UndoResult.Failed(item, "HTTP 403 Forbidden: the identity may not delete records");
            await RunAsync(runtime, route, ledger);

            for (var i = 1; i < ArtifactLimits.MaxUndoAttempts; i++)
            {
                _clock.Advance(TimeSpan.FromHours(7));
                Assert.Equal(1, (await runtime.UndoUnfinishedAsync(exhausted: false)).Records);
            }

            var exhausted = await ledger.OpenArtifactsAsync(runtime.Flow.Id, [key]);
            Assert.All(exhausted, a => Assert.Equal((ArtifactStatus.Failed, ArtifactLimits.MaxUndoAttempts), (a.Status, a.UndoAttempts)));
            Assert.All(exhausted, a => Assert.Null(a.NextUndoUtc));
            Assert.Equal(3L, (await ledger.ArtifactCountsAsync(runtime.Flow.Id)).FailedExhausted);

            // The sweep no longer takes them, a deliver run's included; an operator's undo run with force does.
            _clock.Advance(TimeSpan.FromDays(1));
            Assert.True((await runtime.UndoUnfinishedAsync(exhausted: false)).Idle);
            route.UndoWith = null;
            var forced = await runtime.UndoUnfinishedAsync(exhausted: true);
            Assert.Equal(3, forced.Removed);
            Assert.Equal(ArtifactCounts.None, await ledger.ArtifactCountsAsync(runtime.Flow.Id));
        }
    }

    [Fact]
    public async Task Registrations_whose_answers_were_lost_are_each_found_by_where_their_file_landed_and_removed()
    {
        var (_, runtime, route, ledger) = await EstateAsync();
        using (runtime)
        {
            var key = Keys()[1];

            // Each registration lands and its answer never comes back, and the index never lists it in time: every try uploads
            // and registers again, as the file route does, and the record spends its budget.
            route.IndexLists = false;
            route.LoseAnswerAt = (work, step) => work.Key == key && step == "register" ? new DeliveryException("the connection to POST /api/file/v2/files/metadata was reset") : null;
            await RunAsync(runtime, route, ledger);
            var intent = Assert.Single(await ledger.OpenArtifactsAsync(runtime.Flow.Id, [key]));
            Assert.Equal((ArtifactStatus.Intent, null), (intent.Status, intent.TargetId));
            for (var i = 0; i < 4 && (await ledger.GetRecordAsync(runtime.Flow.Id, key))!.Status == RecordStatus.Pending; i++)
            {
                _clock.Advance(TimeSpan.FromHours(1));
                await DrainAsync(runtime, route, ledger);
            }

            Assert.Equal(RecordStatus.Failed, (await ledger.GetRecordAsync(runtime.Flow.Id, key))!.Status);
            var registered = route.Registrations[key];
            Assert.Equal(runtime.Flow.Reliability.Retry.Attempts, registered);

            // Not one of them is lost: every intent reaches the undo, which finds each dataset by its landing path.
            var undo = Assert.Single(route.Undos);
            Assert.Equal(registered, undo.Items.Count);
            Assert.All(undo.Items, item => Assert.Equal((ArtifactStatus.Intent, (string?)null), (item.Artifact.Status, item.Artifact.TargetId)));
            Assert.All(undo.Items, item => Assert.False(route.Live.ContainsKey(route.ByLocator[item.Artifact.Locator!])));
            Assert.All(await ledger.RecordArtifactsAsync(runtime.Flow.Id, key, 50), a => Assert.Equal(ArtifactStatus.Removed, a.Status));
        }
    }

    [Fact]
    public async Task A_registration_whose_answer_was_lost_is_taken_over_by_the_next_try_and_registered_once()
    {
        var (_, runtime, route, ledger) = await EstateAsync();
        using (runtime)
        {
            var key = Keys()[2];
            var lost = 1;
            route.LoseAnswerAt = (work, step) => work.Key == key && step == "register" && lost-- > 0 ? new DeliveryException("the connection to POST /api/file/v2/files/metadata was reset") : null;

            await RunAsync(runtime, route, ledger);
            _clock.Advance(TimeSpan.FromMinutes(5));
            Assert.Equal(1, (await DrainAsync(runtime, route, ledger)).Delivered);

            Assert.Equal(1, route.Registrations[key]);
            var dataset = Assert.Single(await ledger.RecordArtifactsAsync(runtime.Flow.Id, key, 50));
            Assert.Equal(ArtifactStatus.Live, dataset.Status);
            Assert.True(route.Live.ContainsKey(dataset.TargetId!));
            Assert.Empty(route.Undos);
        }
    }

    [Fact]
    public async Task The_datasets_a_later_delivery_replaced_are_superseded_and_stay_live()
    {
        var (tables, runtime, route, ledger) = await EstateAsync();
        using (runtime)
        {
            await RunAsync(runtime, route, ledger);
            var key = Keys()[0];
            var first = Assert.Single(await ledger.RecordArtifactsAsync(runtime.Flow.Id, key, 50));

            // The curve values move: the payload goes again, and the route registers the new file.
            var log = SampleWellLogs.Logs()[0];
            var moved = log.WithValues(log.Columns()[1], value => value is null ? null : value + 10);
            _clock.Advance(TimeSpan.FromMinutes(10));
            await SampleEstate.RewritePayloadAsync(_root, tables.Records[0], moved, Now);
            Assert.Equal(1, (await RunAsync(runtime, route, ledger, force: true)).Delivered);

            var artifacts = (await ledger.RecordArtifactsAsync(runtime.Flow.Id, key, 50)).OrderBy(a => a.ArtifactId).ToList();
            Assert.Equal([ArtifactStatus.Superseded, ArtifactStatus.Live], artifacts.Select(a => a.Status));
            Assert.Equal(first.TargetId, artifacts[0].TargetId);
            Assert.True(route.Live.ContainsKey(first.TargetId!));
            Assert.Empty(route.Undos);
        }
    }

    [Fact]
    public async Task What_a_step_created_survives_a_worker_that_stopped_and_the_unit_that_resumes_it_takes_it_over()
    {
        var ledger = _db.Ledger(_clock);
        var flow = FlowId.Of("atomic-crash");
        await ledger.RegisterAsync(flow);
        var submission = Guid.NewGuid();
        var key = DeliveryKey.Derive("atomic", ["crash-1"]);
        await ledger.UpsertPendingAsync(flow, [new RecordState
        {
            DeliveryKey = key, FlowId = flow, SourceKey = "crash-1", MappingName = "Thing", TargetId = "dev:x:crash-1",
            LastSubmissionId = submission, PendingDocumentRef = "0:0:10", PendingRenderContext = "{}", PendingMetadataHash = "mh", PendingMetadata = true,
        }]);
        var claim = await ledger.ClaimAsync(flow, submission, "w1", 1, TimeSpan.FromMinutes(5), Now);
        var token = claim.Lease!.Token;
        var unit = new DeliveryUnit(Guid.NewGuid(), Now);
        var steps = StepsJson(unit, ("register", "datasetId", "dev:dataset--File.Generic:9"));
        await ledger.AppendAsync(flow, token, new LeaseAppend(
            [new RecordStep(key, submission, "0:0:10", steps, Now) { Unit = unit, Artifacts = [TargetArtifact.Created("dataset:0", ArtifactRoles.Dataset, "dev:dataset--File.Generic:9")] }],
            []));

        // The worker stops here. While its lease holds, nothing of the record is swept.
        Assert.Empty((await ledger.SweepArtifactsAsync(flow, Now, null, 10, exhausted: false)).Artifacts);

        // Its lease runs out and is recovered: the record carries the unit again, so its artifact is still that unit's, not abandoned.
        _clock.Advance(TimeSpan.FromMinutes(10));
        await ledger.RecoverExpiredLeasesAsync(flow, Now);
        var record = (await ledger.GetRecordAsync(flow, key))!;
        Assert.Null(record.LeaseOwner);
        var kept = JsonMerge.Parse(record.PendingStepJson).ToDictionary(kv => kv.Key, kv => JsonMerge.ToValues(kv.Value!.ToJsonString()), StringComparer.Ordinal);
        Assert.Equal(unit.Id, DeliveryUnit.FromSteps(kept)!.Id);
        Assert.Empty((await ledger.SweepArtifactsAsync(flow, Now, null, 10, exhausted: false)).Artifacts);
        var artifact = Assert.Single(await ledger.OpenArtifactsAsync(flow, [key]));
        Assert.Equal((ArtifactStatus.Pending, unit.Id), (artifact.Status, artifact.UnitId));
    }

    [Fact]
    public async Task An_intent_completed_by_its_id_is_one_row_and_a_settled_artifact_is_never_settled_again()
    {
        var ledger = _db.Ledger(_clock);
        var flow = FlowId.Of("atomic-rows");
        await ledger.RegisterAsync(flow);
        var submission = Guid.NewGuid();
        var key = DeliveryKey.Derive("atomic", ["rows-1"]);
        await ledger.UpsertPendingAsync(flow, [new RecordState
        {
            DeliveryKey = key, FlowId = flow, SourceKey = "rows-1", MappingName = "Thing", TargetId = "dev:x:rows-1",
            LastSubmissionId = submission, PendingDocumentRef = "0:0:10", PendingRenderContext = "{}", PendingMetadataHash = "mh", PendingMetadata = true,
        }]);
        var token = (await ledger.ClaimAsync(flow, submission, "w1", 1, TimeSpan.FromMinutes(5), Now)).Lease!.Token;
        var unit = new DeliveryUnit(Guid.NewGuid(), Now);
        var steps = StepsJson(unit);
        RecordStep Step(TargetArtifact artifact) => new(key, submission, "0:0:10", steps, Now) { Unit = unit, Artifacts = [artifact] };

        await ledger.AppendAsync(flow, token, new LeaseAppend([Step(TargetArtifact.Intent("dataset:0", ArtifactRoles.Dataset, "landing/a"))], []));
        await ledger.AppendAsync(flow, token, new LeaseAppend([Step(TargetArtifact.Created("dataset:0", ArtifactRoles.Dataset, "dev:dataset--File.Generic:1"))], []));
        var artifact = Assert.Single(await ledger.RecordArtifactsAsync(flow, key, 10));
        Assert.Equal(("dev:dataset--File.Generic:1", "landing/a", ArtifactStatus.Pending), (artifact.TargetId, artifact.Locator, artifact.Status));

        // An undo settles it; a second undo of the same artifact (a sweep meeting what the worker already undid) changes nothing.
        AttemptRecord Attempt() => new()
        {
            DeliveryKey = key, Worker = "w1", StartedUtc = Now, CompletedUtc = Now, Outcome = AttemptOutcome.Undone, Phase = AttemptPhases.Undo,
        };
        await ledger.SettleArtifactsAsync(flow, [new RecordUndo(key, Attempt(), [new ArtifactSettlement(artifact.ArtifactId, ArtifactStatus.Removed, "removed", null)], null, "w1")]);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await ledger.SettleArtifactsAsync(flow, [new RecordUndo(key, Attempt(), [new ArtifactSettlement(artifact.ArtifactId, ArtifactStatus.Failed, "late", Now)], null, "sweep")]);
        var settled = Assert.Single(await ledger.RecordArtifactsAsync(flow, key, 10));
        Assert.Equal((ArtifactStatus.Removed, "removed", "w1", 1), (settled.Status, settled.Note, settled.SettledBy, settled.UndoAttempts));

        // A report of the slot after it was settled does not reopen it.
        await ledger.AppendAsync(flow, token, new LeaseAppend([Step(TargetArtifact.Created("dataset:0", ArtifactRoles.Dataset, "dev:dataset--File.Generic:2"))], []));
        Assert.Equal("dev:dataset--File.Generic:1", Assert.Single(await ledger.RecordArtifactsAsync(flow, key, 10)).TargetId);
    }

    [Fact]
    public async Task Artifacts_outside_a_unit_and_undo_outcomes_no_undo_can_have_are_refused_before_anything_is_written()
    {
        var ledger = _db.Ledger(_clock);
        var flow = FlowId.Of("atomic-refusals");
        await ledger.RegisterAsync(flow);
        var key = DeliveryKey.Derive("atomic", ["refused"]);
        await ledger.UpsertPendingAsync(flow, [new RecordState
        {
            DeliveryKey = key, FlowId = flow, SourceKey = "refused", MappingName = "Thing", TargetId = "dev:x:refused",
            LastSubmissionId = null, PendingDocumentRef = "0:0:10", PendingRenderContext = "{}", PendingMetadataHash = "mh", PendingMetadata = true,
        }]);

        var outside = new RecordStep(key, null, "0:0:10", "{}", Now) { Artifacts = [TargetArtifact.Created("dataset:0", ArtifactRoles.Dataset, "dev:dataset--File.Generic:1")] };
        var error = await Assert.ThrowsAsync<ArgumentException>(() => ledger.AppendAsync(flow, "tests/" + Guid.NewGuid().ToString("N"), new LeaseAppend([outside], [])));
        Assert.Contains("outside a unit of work", error.Message, StringComparison.Ordinal);

        var attempt = new AttemptRecord { DeliveryKey = key, Worker = "w1", StartedUtc = Now, CompletedUtc = Now, Outcome = AttemptOutcome.Undone, Phase = AttemptPhases.Undo };
        var live = new RecordUndo(key, attempt, [new ArtifactSettlement(1, ArtifactStatus.Live, null, null)], null, "w1");
        error = await Assert.ThrowsAsync<ArgumentException>(() => ledger.SettleArtifactsAsync(flow, [live]));
        Assert.Contains("not an outcome an undo can have", error.Message, StringComparison.Ordinal);
        Assert.Empty(await ledger.ListAttemptsAsync(flow, key, 10));
        Assert.Empty(await ledger.RecordArtifactsAsync(flow, key, 10));
    }

    [Fact]
    public async Task Newer_work_waits_uncharged_while_the_unit_it_abandoned_cannot_be_undone_and_goes_once_it_can()
    {
        var (tables, runtime, route, ledger) = await EstateAsync();
        using (runtime)
        {
            var key = Keys()[0];
            var failing = true;
            route.FailAt = (work, step) => work.Key == key && step == "commit" && failing ? new DeliveryException("HTTP 504 Gateway Timeout from PATCH /sessions") : null;
            await RunAsync(runtime, route, ledger);
            var abandoned = await ledger.OpenArtifactsAsync(runtime.Flow.Id, [key]);
            Assert.Equal(3, abandoned.Count);
            var sent = route.Deliveries.Count(d => d.Key == key);

            // The source changes, and OSDU refuses the undo of what the abandoned unit left.
            failing = false;
            var reachable = false;
            route.UndoWith = (_, item) => reachable ? null : UndoResult.Failed(item, "HTTP 503 Service Unavailable from POST /api/storage/v2/records/delete");
            _clock.Advance(TimeSpan.FromMinutes(10));
            SampleEstate.Change(tables.Records[0], "creator", "HAL", Now, SampleWellLogs.UpdatedUtc(0).AddHours(2));
            var waited = await RunAsync(runtime, route, ledger, force: true);

            // The newer work was not sent: it waits for the undo's next try, and the try is not charged.
            Assert.Equal((0L, 1L), (waited.Delivered, waited.Retried));
            Assert.Equal(sent, route.Deliveries.Count(d => d.Key == key));
            var record = (await ledger.GetRecordAsync(runtime.Flow.Id, key))!;
            Assert.Equal((RecordStatus.Pending, 0), (record.Status, record.AttemptCount));
            Assert.Equal(ArtifactLimits.RetryAt(Now, 1), record.NextAttemptUtc);
            var attempts = await ledger.ListAttemptsAsync(runtime.Flow.Id, key, 20);
            var skipped = attempts.Single(a => a.Phase == AttemptPhases.UndoWait);
            Assert.Equal((AttemptOutcome.Skipped, (string?)null), (skipped.Outcome, skipped.Error));
            Assert.Contains("waits for the undo's next try", JsonNode.Parse(skipped.ResultJson!)!["detail"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.All(await ledger.OpenArtifactsAsync(runtime.Flow.Id, [key]), a => Assert.Equal((ArtifactStatus.Failed, 1), (a.Status, a.UndoAttempts)));

            // Before the undo's backoff has passed, nothing is due; after it, the undo lands and the newer work goes.
            Assert.True((await DrainAsync(runtime, route, ledger)).Idle);
            reachable = true;
            _clock.Advance(TimeSpan.FromMinutes(2));
            Assert.Equal(1, (await DrainAsync(runtime, route, ledger)).Delivered);

            Assert.Equal(sent + 1, route.Deliveries.Count(d => d.Key == key));
            Assert.Equal(RecordStatus.Delivered, (await ledger.GetRecordAsync(runtime.Flow.Id, key))!.Status);
            var settled = (await ledger.RecordArtifactsAsync(runtime.Flow.Id, key, 50)).Where(a => a.UnitId == abandoned[0].UnitId).ToList();
            Assert.Equal(
                [(ArtifactRoles.Dataset, ArtifactStatus.Removed), (ArtifactRoles.Record, ArtifactStatus.Superseded), (ArtifactRoles.Session, ArtifactStatus.Removed)],
                settled.OrderBy(a => a.ArtifactId).Select(a => (a.Role, a.Status)));
            Assert.Equal(ArtifactCounts.None, await ledger.ArtifactCountsAsync(runtime.Flow.Id));
        }
    }

    [Fact]
    public async Task Newer_work_is_held_once_the_undo_it_waits_for_has_used_its_tries_and_a_release_tries_the_undo_first()
    {
        var (tables, runtime, route, ledger) = await EstateAsync();
        using (runtime)
        {
            var key = Keys()[1];
            var failing = true;
            route.FailAt = (work, step) => work.Key == key && step == "commit" && failing ? new DeliveryException("HTTP 504 Gateway Timeout from PATCH /sessions") : null;
            await RunAsync(runtime, route, ledger);
            failing = false;
            route.UndoWith = (_, item) => UndoResult.Failed(item, "HTTP 403 Forbidden: the identity may not delete records");
            _clock.Advance(TimeSpan.FromMinutes(10));
            SampleEstate.Change(tables.Records[1], "creator", "HAL", Now, SampleWellLogs.UpdatedUtc(1).AddHours(2));
            await RunAsync(runtime, route, ledger, force: true);

            // Every claim tries the undo again, and none of them is charged, until the undo has used its tries.
            for (var i = 1; i < ArtifactLimits.MaxUndoAttempts && (await ledger.GetRecordAsync(runtime.Flow.Id, key))!.Status == RecordStatus.Pending; i++)
            {
                _clock.Advance(TimeSpan.FromHours(7));
                await DrainAsync(runtime, route, ledger);
            }

            var record = (await ledger.GetRecordAsync(runtime.Flow.Id, key))!;
            Assert.Equal(RecordStatus.Held, record.Status);
            Assert.Contains("release the record", record.LastError, StringComparison.Ordinal);
            Assert.All(await ledger.OpenArtifactsAsync(runtime.Flow.Id, [key]), a => Assert.Equal((ArtifactStatus.Failed, ArtifactLimits.MaxUndoAttempts, (DateTime?)null), (a.Status, a.UndoAttempts, a.NextUndoUtc)));
            var attempts = await ledger.ListAttemptsAsync(runtime.Flow.Id, key, 50);
            Assert.Equal(ArtifactLimits.MaxUndoAttempts - 1, attempts.Count(a => a.Outcome == AttemptOutcome.Skipped && a.Phase == AttemptPhases.UndoWait));
            Assert.Single(attempts, a => a.Outcome == AttemptOutcome.Held && a.Phase == AttemptPhases.UndoWait);
            Assert.Equal(ArtifactLimits.MaxUndoAttempts, attempts.Count(a => a.Outcome == AttemptOutcome.Undone));
            Assert.Single(route.Deliveries, d => d.Key == key);

            // What stopped the undo is fixed; the operator releases the record, and the undo goes before the newer work.
            route.UndoWith = null;
            Assert.Equal(1, await ledger.ReleaseAsync(runtime.Flow.Id, [key], Now));
            Assert.Equal(1, (await DrainAsync(runtime, route, ledger)).Delivered);
            Assert.Equal(RecordStatus.Delivered, (await ledger.GetRecordAsync(runtime.Flow.Id, key))!.Status);
            Assert.Equal(ArtifactCounts.None, await ledger.ArtifactCountsAsync(runtime.Flow.Id));
        }
    }

    [Fact]
    public async Task Newer_work_that_writes_no_metadata_takes_back_the_record_version_the_abandoned_unit_wrote()
    {
        var (tables, runtime, route, ledger) = await EstateAsync();
        using (runtime)
        {
            await RunAsync(runtime, route, ledger);
            var key = Keys()[0];
            var committed = (await ledger.GetRecordAsync(runtime.Flow.Id, key))!.TargetVersion!.Value;
            var source = tables.Records[0];
            var creator = source.Row["creator"];

            // A metadata change goes, writes the record and stops for now before the try ends.
            var failing = true;
            route.FailAt = (work, step) => work.Key == key && step == "finish" && failing ? new DeliveryException("HTTP 502 Bad Gateway from GET /welllogs") : null;
            _clock.Advance(TimeSpan.FromMinutes(10));
            SampleEstate.Change(source, "creator", "HAL", Now, SampleWellLogs.UpdatedUtc(0).AddHours(2));
            Assert.Equal(1, (await RunAsync(runtime, route, ledger, force: true)).Retried);
            var version = Assert.Single(await ledger.OpenArtifactsAsync(runtime.Flow.Id, [key]));
            Assert.Equal((ArtifactRoles.Version, committed), (version.Role, version.PriorVersion));

            // The source puts the metadata back and moves the curves instead: the newer work sends the payload alone, so the
            // record version the abandoned unit wrote is not the newer work's to replace, and is taken back.
            failing = false;
            _clock.Advance(TimeSpan.FromMinutes(10));
            SampleEstate.Change(source, "creator", creator, Now, SampleWellLogs.UpdatedUtc(0).AddHours(3));
            var log = SampleWellLogs.Logs()[0];
            await SampleEstate.RewritePayloadAsync(_root, source, log.WithValues(log.Columns()[1], value => value is null ? null : value + 10), Now);
            Assert.Equal(1, (await RunAsync(runtime, route, ledger, force: true)).Delivered);

            var newer = route.Deliveries.Last(d => d.Key == key);
            Assert.Equal((false, true), (newer.DeliverMetadata, newer.DeliverPayload));
            var undo = route.Undos.Single(u => u.Key == key);
            Assert.Equal((UndoReason.Abandoned, false), (undo.Reason, undo.KeepRecord));
            Assert.Equal((source.Row["creator"], committed), (creator, Assert.Single(route.Restores, r => r.TargetId == newer.TargetId).Version));
            Assert.Equal(ArtifactStatus.Restored, (await ledger.RecordArtifactsAsync(runtime.Flow.Id, key, 50)).Single(a => a.ArtifactId == version.ArtifactId).Status);
        }
    }

    [Fact]
    public async Task A_slot_reported_again_keeps_the_record_it_was_first_and_the_version_its_write_first_replaced()
    {
        var ledger = _db.Ledger(_clock);
        var flow = FlowId.Of("atomic-resume");
        await ledger.RegisterAsync(flow);
        var submission = Guid.NewGuid();
        var key = DeliveryKey.Derive("atomic", ["resume-1"]);
        await ledger.UpsertPendingAsync(flow, [new RecordState
        {
            DeliveryKey = key, FlowId = flow, SourceKey = "resume-1", MappingName = "Thing", TargetId = "dev:x:resume-1",
            LastSubmissionId = submission, PendingDocumentRef = "0:0:10", PendingRenderContext = "{}", PendingMetadataHash = "mh", PendingMetadata = true,
        }]);
        var token = (await ledger.ClaimAsync(flow, submission, "w1", 1, TimeSpan.FromMinutes(5), Now)).Lease!.Token;
        var unit = new DeliveryUnit(Guid.NewGuid(), Now);
        var steps = StepsJson(unit);
        RecordStep Step(params TargetArtifact[] artifacts) => new(key, submission, "0:0:10", steps, Now) { Unit = unit, Artifacts = artifacts };
        TargetArtifact Dataset(string slot, string role, long? prior, ArtifactStatus status, long? version = null)
            => new() { Slot = slot, Role = role, TargetId = $"dev:dataset--File.Collection:{slot}", PriorVersion = prior, Version = version, Status = status };

        // A first try declared a registration that creates its dataset, and one that writes a new version of another, and lost
        // both answers after they landed. The resumed try reads storage again, and sees what the first try wrote.
        await ledger.AppendAsync(flow, token, new LeaseAppend([Step(
            Dataset("files-dataset", ArtifactRoles.Record, null, ArtifactStatus.Intent),
            Dataset("input", ArtifactRoles.Version, 40, ArtifactStatus.Intent))], []));
        await ledger.AppendAsync(flow, token, new LeaseAppend([Step(
            Dataset("files-dataset", ArtifactRoles.Version, 51, ArtifactStatus.Intent),
            Dataset("input", ArtifactRoles.Version, 52, ArtifactStatus.Intent))], []));
        await ledger.AppendAsync(flow, token, new LeaseAppend([Step(
            Dataset("files-dataset", ArtifactRoles.Version, 51, ArtifactStatus.Pending, version: 60),
            Dataset("input", ArtifactRoles.Version, 52, ArtifactStatus.Pending, version: 61))], []));

        var artifacts = (await ledger.RecordArtifactsAsync(flow, key, 10)).ToDictionary(a => a.Slot);
        Assert.Equal((ArtifactRoles.Record, (long?)null, 60L, ArtifactStatus.Pending), (artifacts["files-dataset"].Role, artifacts["files-dataset"].PriorVersion, artifacts["files-dataset"].Version!.Value, artifacts["files-dataset"].Status));
        Assert.Equal((ArtifactRoles.Version, 40L, 61L), (artifacts["input"].Role, artifacts["input"].PriorVersion!.Value, artifacts["input"].Version!.Value));
    }

    [Fact]
    public async Task A_removal_first_undoes_what_the_records_unfinished_delivery_left()
    {
        var (_, runtime, route, ledger) = await EstateAsync();
        using (runtime)
        {
            var key = Keys()[0];
            route.FailAt = (work, step) => work.Key == key && step == "commit" ? new RecordHeldException("HTTP 400 from the DDMS") : null;
            var reachable = false;
            route.UndoWith = (_, item) => reachable ? null : UndoResult.Failed(item, "HTTP 503 Service Unavailable from POST /api/storage/v2/records/delete");
            await RunAsync(runtime, route, ledger);
            var left = await ledger.OpenArtifactsAsync(runtime.Flow.Id, [key]);
            Assert.Equal(3, left.Count);
            var targetId = (await ledger.GetRecordAsync(runtime.Flow.Id, key))!.TargetId!;
            Assert.True(route.Live.ContainsKey(targetId));

            // OSDU answers now; the operator removes the record, and what its unfinished delivery left goes first.
            reachable = true;
            var summary = await runtime.RemoveAsync(RemovalSelection.Of([key]), RemovalChoice.Record);

            Assert.Equal((1, 3, 0), (summary.Undone.Records, summary.Undone.Removed, summary.Undone.Failed));
            Assert.Equal(1, summary.AlreadyGone);
            Assert.Contains("first 1 record(s) with what unfinished deliveries left: 3 removed", summary.Describe(), StringComparison.Ordinal);
            Assert.All(await ledger.RecordArtifactsAsync(runtime.Flow.Id, key, 50), a => Assert.Equal(ArtifactStatus.Removed, a.Status));
            Assert.DoesNotContain(route.Live.Keys, k => left.Any(a => a.TargetId == k));
            var undo = route.Undos.Last();
            Assert.Equal((UndoReason.Removed, false), (undo.Reason, undo.KeepRecord));
            var attempt = (await ledger.ListAttemptsAsync(runtime.Flow.Id, key, 20)).Where(a => a.Outcome == AttemptOutcome.Undone).MaxBy(a => a.AttemptId)!;
            Assert.Equal("removed", JsonNode.Parse(attempt.ResultJson!)!["undo"]!["reason"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task A_record_whose_unfinished_delivery_could_not_be_undone_is_removed_but_kept_in_the_ledger()
    {
        var (_, runtime, route, ledger) = await EstateAsync();
        using (runtime)
        {
            var key = Keys()[1];
            route.FailAt = (work, step) => work.Key == key && step == "commit" ? new RecordHeldException("HTTP 400 from the DDMS") : null;
            route.UndoWith = (work, item) => item.Artifact.Role == ArtifactRoles.Dataset ? UndoResult.Failed(item, "HTTP 403 Forbidden: the identity may not delete datasets") : null;
            await RunAsync(runtime, route, ledger);

            var summary = await runtime.RemoveAsync(RemovalSelection.Of([key]), RemovalChoice.Record, purgeLedger: true);

            // The record is out of OSDU, and stays in the ledger, the one place that still names the dataset its delivery left.
            Assert.Equal((0, 1), (summary.Purged, summary.Undone.Failed));
            var result = Assert.Single(summary.Records);
            Assert.Contains("kept in the ledger", result.Detail, StringComparison.Ordinal);
            Assert.Equal(RecordStatus.Deleted, (await ledger.GetRecordAsync(runtime.Flow.Id, key))!.Status);
            var dataset = Assert.Single(await ledger.OpenArtifactsAsync(runtime.Flow.Id, [key]));
            Assert.Equal((ArtifactRoles.Dataset, ArtifactStatus.Failed, 2), (dataset.Role, dataset.Status, dataset.UndoAttempts));

            // Deleting it from the ledger alone, by name or with every removed record, leaves it too: the sweep reaches the
            // dataset through its record.
            var named = await runtime.PurgeFromLedgerAsync([key]);
            Assert.Equal((1, 0, 1), (named.Selected, named.Purged, named.Left));
            Assert.Contains("its undo has not taken back yet", named.Describe(), StringComparison.Ordinal);
            var every = await runtime.PurgeFromLedgerAsync(null);
            Assert.Equal((1, 0), (every.Selected, every.Purged));
            Assert.NotNull(await ledger.GetRecordAsync(runtime.Flow.Id, key));

            // The sweep goes on undoing it once OSDU lets it, and the record can go from the ledger after.
            route.UndoWith = null;
            _clock.Advance(TimeSpan.FromHours(1));
            Assert.Equal(1, (await runtime.UndoUnfinishedAsync(exhausted: false)).Removed);
            Assert.Equal(ArtifactCounts.None, await ledger.ArtifactCountsAsync(runtime.Flow.Id));
            Assert.Equal(1, (await runtime.PurgeFromLedgerAsync([key])).Purged);
            Assert.Null(await ledger.GetRecordAsync(runtime.Flow.Id, key));
        }
    }

    [Fact]
    public async Task Deleting_the_ledger_removes_the_datasets_its_deliveries_minted_since_nothing_names_them_afterwards()
    {
        var (_, runtime, route, ledger) = await EstateAsync();
        using (runtime)
        {
            await RunAsync(runtime, route, ledger);
            var datasets = new List<LedgerArtifact>();
            foreach (var key in Keys())
            {
                datasets.AddRange(await ledger.RecordArtifactsAsync(runtime.Flow.Id, key, 50));
            }

            Assert.Equal(LogCount, datasets.Count);
            Assert.All(datasets, d => Assert.Equal((ArtifactRoles.Dataset, ArtifactStatus.Live), (d.Role, d.Status)));
            var partition = (await ledger.GetLedgerAsync(runtime.Flow.Id))!.Partition!;

            var summary = await runtime.DeleteLedgerAsync(partition);

            Assert.Equal((LogCount, LogCount, 0), (summary.Removed, summary.Undone.Removed, summary.Undone.Failed));
            Assert.All(datasets, d => Assert.False(route.Live.ContainsKey(d.TargetId!)));
            Assert.Contains("removed", summary.Describe(), StringComparison.Ordinal);

            // The ledger is gone, and what its deliveries minted is still on record, as removed, for the inventory.
            Assert.Null(await ledger.GetRecordAsync(runtime.Flow.Id, Keys()[0]));
            foreach (var dataset in datasets)
            {
                var kept = Assert.Single(await ledger.RecordArtifactsAsync(runtime.Flow.Id, dataset.Key, 50), a => a.ArtifactId == dataset.ArtifactId);
                Assert.Equal(ArtifactStatus.Removed, kept.Status);
            }
        }
    }

    [Fact]
    public async Task Deleting_the_ledger_is_refused_while_something_a_delivery_left_cannot_be_undone()
    {
        var (_, runtime, route, ledger) = await EstateAsync();
        using (runtime)
        {
            var key = Keys()[2];
            route.FailAt = (work, step) => work.Key == key && step == "commit" ? new RecordHeldException("HTTP 400 from the DDMS") : null;
            route.UndoWith = (work, item) => work.Key == key && item.Artifact.Role == ArtifactRoles.Session ? UndoResult.Failed(item, "HTTP 503 Service Unavailable from DELETE /sessions") : null;
            await RunAsync(runtime, route, ledger);
            var partition = (await ledger.GetLedgerAsync(runtime.Flow.Id))!.Partition!;

            var refused = await Assert.ThrowsAsync<DeliveryException>(() => runtime.DeleteLedgerAsync(partition));

            Assert.Contains("1 item(s) the deliveries of", refused.Message, StringComparison.Ordinal);
            Assert.Contains("so the ledger was kept as it was", refused.Message, StringComparison.Ordinal);
            Assert.NotNull(await ledger.GetRecordAsync(runtime.Flow.Id, key));
            var session = Assert.Single(await ledger.OpenArtifactsAsync(runtime.Flow.Id, [key]));
            Assert.Equal((ArtifactRoles.Session, ArtifactStatus.Failed), (session.Role, session.Status));

            // Once the session can be abandoned, deleting the ledger goes through.
            route.UndoWith = null;
            _clock.Advance(TimeSpan.FromHours(1));
            var summary = await runtime.DeleteLedgerAsync(partition);
            Assert.Equal(0, summary.Undone.Failed);
            Assert.Null(await ledger.GetRecordAsync(runtime.Flow.Id, key));
        }
    }

    private static IReadOnlyList<DeliveryKey> Keys() => Enumerable.Range(0, LogCount).Select(SampleEstate.Key).ToList();

    /// <summary>A record's steps as the worker keeps them: its unit, and each step with one value it returned.</summary>
    private static string StepsJson(DeliveryUnit unit, params (string Step, string Name, string Value)[] steps)
    {
        var node = new JsonObject
        {
            [DeliveryUnit.StepName] = new JsonObject { ["id"] = unit.Id.ToString("D"), ["startedUtc"] = unit.StartedUtc.ToString("O", CultureInfo.InvariantCulture) },
        };
        foreach (var (step, name, value) in steps)
        {
            node[step] = new JsonObject { [name] = value };
        }

        return node.ToJsonString();
    }

    private async Task<(MemoryIngestionTables Tables, FlowRuntime Runtime, StagedRoute Route, OsduLedger Ledger)> EstateAsync()
    {
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        var ledger = _db.Ledger(_clock);
        var route = new StagedRoute();
        var engine = Samples.Engine(ledger, _clock, sources: tables) with { Protocols = new FakeProtocolFactory(route) };
        var flow = Samples.LocalFlow(_root);
        var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);
        runtime.Actor = "tests:atomic";
        return (tables, runtime, route, ledger);
    }

    /// <summary>One run's planning and draining, as a deliver run does, with the worker over the staged route.</summary>
    private async Task<WorkerSummary> RunAsync(FlowRuntime runtime, StagedRoute route, OsduLedger ledger, bool force = false)
    {
        var intake = await runtime.Intake.IntakeAsync(runtime.Flow, runtime.Mapping, runtime.Parameters, runtime.Request, force);
        if (intake.NothingToDo)
        {
            return WorkerSummary.Empty;
        }

        var summary = await Worker(runtime, route, ledger).DrainAsync(intake.Submission.SubmissionId);
        await runtime.Intake.CompleteAsync(intake.Submission.SubmissionId, runtime.Flow.Id);
        return summary;
    }

    /// <summary>A drain of what is due, as a later run's worker meets it.</summary>
    private Task<WorkerSummary> DrainAsync(FlowRuntime runtime, StagedRoute route, OsduLedger ledger)
        => Worker(runtime, route, ledger).DrainAsync(submissionId: null);

    private DeliveryWorker Worker(FlowRuntime runtime, StagedRoute route, OsduLedger ledger) => new(
        ledger, runtime.Context.Payloads, runtime.Context.Stores, route, runtime.Flow, _clock,
        CompositeDeliveryListener.Empty, Samples.Logger<DeliveryWorker>(), "atomic-worker") { MaxWait = null };
}
