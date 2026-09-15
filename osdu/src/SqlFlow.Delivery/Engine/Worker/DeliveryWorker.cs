using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Delivery.Drops;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Planning;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Storage;

namespace SqlFlow.Delivery.Engine.Worker;

/// <summary>
/// What a drain did. <c>Unchanged</c> counts the records the final hash check found OSDU already holding, settled
/// without sending anything.
/// </summary>
public sealed record WorkerSummary(long Processed, long Delivered, long Retried, long Held, long Failed, int Batches = 0, long Unchanged = 0)
{
    public static WorkerSummary Empty { get; } = new(0, 0, 0, 0, 0);

    public WorkerSummary Add(WorkerSummary other) => new(
        Processed + other.Processed, Delivered + other.Delivered, Retried + other.Retried, Held + other.Held, Failed + other.Failed, Batches + other.Batches, Unchanged + other.Unchanged);

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{Processed} processed in {Batches} batch(es): {Delivered} delivered, {Unchanged} already held (nothing sent), {Retried} retrying later, {Held} held, {Failed} failed");
}

/// <summary>
/// The lease-and-retry worker (design.md sections 7.5 and 16.2): the fan-out. Claims whole work batches (a file of
/// rendered documents and every record of it that is due) and, when no batch is claimable, the individual records
/// that came due for a retry; processes them with bounded concurrency, in protocol batches when the protocol
/// accepts arrays; writes one append-only attempt per record with every step the target answered; keeps a
/// record's completed steps so a retry never repeats an upload; and fires the completion callback
/// (<see cref="IDeliveryListener"/>) after every outcome. Any number of workers, on any number of nodes, share
/// the ledger safely: a crashed worker's leases expire and its records are reclaimed by the next claim, a stopping
/// worker releases them at once. The run trace carries batch-level lines, never one per record.
/// </summary>
public sealed class DeliveryWorker
{
    /// <summary>Completions are written to the ledger in groups of this many, or when a batch closes.</summary>
    public const int CompletionFlush = 200;

    /// <summary>The longest one wait for records in backoff lasts before the queue is looked at again.</summary>
    public static readonly TimeSpan DefaultMaxWait = TimeSpan.FromMinutes(5);

    private const int FailuresLoggedPerBatch = 10;

    private readonly ILedger _ledger;
    private readonly IDropReader _drops;
    private readonly FileStoreRegistry _stores;
    private readonly IDeliveryProtocol _protocol;
    private readonly FlowDefinition _flow;
    private readonly TimeProvider _time;
    private readonly IDeliveryListener _listener;
    private readonly ILogger<DeliveryWorker> _logger;
    private readonly string _workerId;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Lazy<Task<SubmissionState>>> _submissions = new();

    /// <summary>The platform run the worker drains for, stamped on every attempt it writes.</summary>
    public Guid? RunId { get; init; }

    /// <summary>
    /// How long a drain waits, at most in one go, for records in backoff before looking at the queue again; null
    /// returns as soon as nothing is due (a single pass of what is claimable now).
    /// </summary>
    public TimeSpan? MaxWait { get; init; } = DefaultMaxWait;

    public DeliveryWorker(
        ILedger ledger,
        IDropReader drops,
        FileStoreRegistry stores,
        IDeliveryProtocol protocol,
        FlowDefinition flow,
        TimeProvider time,
        IDeliveryListener listener,
        ILogger<DeliveryWorker> logger,
        string? workerId = null)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(drops);
        ArgumentNullException.ThrowIfNull(stores);
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(logger);
        _ledger = ledger;
        _drops = drops;
        _stores = stores;
        _protocol = protocol;
        _flow = flow;
        _time = time;
        _listener = listener;
        _logger = logger;
        _workerId = workerId ?? $"{Environment.MachineName}/{Environment.ProcessId}";
    }

    public string WorkerId => _workerId;

    private TimeSpan Lease => TimeSpan.FromSeconds(Math.Max(_flow.Reliability.LeaseSeconds, 30));

    /// <summary>
    /// Processes until nothing of the submission (or flow) is pending for this worker: batches first, then records
    /// that came due for a retry, waiting for records in backoff when that is all that is left. Records another
    /// worker holds are not waited for.
    /// </summary>
    public async Task<WorkerSummary> DrainAsync(Guid? submissionId, CancellationToken ct = default)
    {
        var total = WorkerSummary.Empty;
        while (!ct.IsCancellationRequested)
        {
            var summary = await PassAsync(submissionId, ct).ConfigureAwait(false);
            total = total.Add(summary);
            if (summary.Processed > 0 || summary.Batches > 0)
            {
                continue;
            }

            if (MaxWait is not { } maxWait)
            {
                break;
            }

            var now = _time.GetUtcNow().UtcDateTime;
            var nextDue = await _ledger.NextDueAsync(_flow.Id, submissionId, now, ct).ConfigureAwait(false);
            if (nextDue is null)
            {
                break;
            }

            var wait = nextDue.Value - now;
            if (wait > maxWait)
            {
                wait = maxWait;
            }

            if (wait < TimeSpan.FromSeconds(1))
            {
                wait = TimeSpan.FromSeconds(1);
            }

            _logger.LogInformation("Nothing is due; waiting {Seconds}s for records in backoff (next at {Next:u}).", (int)wait.TotalSeconds, nextDue.Value);
            await Task.Delay(wait, _time, ct).ConfigureAwait(false);
        }

        return total;
    }

    /// <summary>One claim: a work batch when one is claimable, else one batch of due records.</summary>
    public async Task<WorkerSummary> PassAsync(Guid? submissionId, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var claimed = await _ledger.ClaimWorkBatchAsync(_flow.Id, submissionId, _workerId, Lease, now, RunId, ct).ConfigureAwait(false);
        if (claimed is not null)
        {
            return await ProcessBatchAsync(claimed, ct).ConfigureAwait(false);
        }

        var records = await _ledger.ClaimAsync(_flow.Id, submissionId, _workerId, _flow.Reliability.BatchSize, Lease, now, ct).ConfigureAwait(false);
        if (records.Count == 0)
        {
            return WorkerSummary.Empty;
        }

        _logger.LogInformation("Claimed {Count} record(s) due for a retry.", records.Count);
        var owner = records[0].LeaseOwner ?? _workerId;
        using var renewals = new CancellationTokenSource();
        var renewTask = RenewRecordsLoopAsync(records.Select(r => r.DeliveryKey).ToList(), owner, renewals.Token);
        try
        {
            var loaded = new List<(RecordState Record, WorkItem? Item)>(records.Count);
            foreach (var record in records)
            {
                loaded.Add((record, await LoadItemAsync(record, ct).ConfigureAwait(false)));
            }

            return await ProcessRecordsAsync(loaded, batch: null, owner, ct).ConfigureAwait(false);
        }
        finally
        {
            await renewals.CancelAsync().ConfigureAwait(false);
            await AwaitQuietlyAsync(renewTask).ConfigureAwait(false);
        }
    }

    private async Task<WorkerSummary> ProcessBatchAsync(ClaimedWorkBatch claimed, CancellationToken ct)
    {
        var batch = claimed.Batch;
        var owner = batch.LeaseOwner ?? _workerId;
        var started = _time.GetUtcNow().UtcDateTime;
        _logger.LogInformation("Claimed batch {Batch} of submission {SubmissionId}: {Records} record(s) due.", batch.Index, batch.SubmissionId, claimed.Records.Count);
        using var renewals = new CancellationTokenSource();
        var renewTask = RenewBatchLoopAsync(batch, owner, renewals.Token);
        WorkerSummary summary;
        try
        {
            var submission = await SubmissionAsync(batch.SubmissionId, ct).ConfigureAwait(false);
            var wanted = claimed.Records.ToDictionary(r => r.DeliveryKey.Value);
            var loaded = new List<(RecordState Record, WorkItem? Item)>(wanted.Count);
            if (wanted.Count > 0)
            {
                await foreach (var (item, _) in WorkBatchFile.ReadAllAsync(_stores, submission.WorkLocation ?? throw MissingWorkLocation(submission), batch.SubmissionId, batch.Index, ct).ConfigureAwait(false))
                {
                    if (wanted.Remove(item.Key, out var record))
                    {
                        loaded.Add((record, item));
                    }
                }

                // Leased records the file does not hold (the work location was pruned) are held, not silently dropped.
                foreach (var missing in wanted.Values)
                {
                    loaded.Add((missing, null));
                }
            }

            summary = await ProcessRecordsAsync(loaded, batch, owner, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await renewals.CancelAsync().ConfigureAwait(false);
            await AwaitQuietlyAsync(renewTask).ConfigureAwait(false);
            await ReleaseBatchAsync(batch, owner).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is SqlFlowException or IOException or InvalidOperationException or JsonException)
        {
            await renewals.CancelAsync().ConfigureAwait(false);
            await AwaitQuietlyAsync(renewTask).ConfigureAwait(false);
            var message = HeaderRedaction.RedactMessage(ex.Message);
            _logger.LogError("Batch {Batch} of submission {SubmissionId} could not be processed: {Message}", batch.Index, batch.SubmissionId, message);
            await _ledger.CompleteWorkBatchAsync(batch.SubmissionId, batch.Index, owner, WorkBatchStatus.Failed, 0, 0, 0, 0, message, _time.GetUtcNow().UtcDateTime, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await renewals.CancelAsync().ConfigureAwait(false);
        await AwaitQuietlyAsync(renewTask).ConfigureAwait(false);
        var completed = _time.GetUtcNow().UtcDateTime;
        await _ledger.CompleteWorkBatchAsync(batch.SubmissionId, batch.Index, owner, WorkBatchStatus.Done, summary.Delivered, summary.Held, summary.Failed, summary.Retried, null, completed, CancellationToken.None).ConfigureAwait(false);
        _logger.LogInformation("Batch {Batch} done in {Seconds:0.#}s: {Summary}", batch.Index, (completed - started).TotalSeconds, summary);
        await _listener.OnEventAsync(new DeliveryEvent
        {
            AtUtc = completed,
            FlowId = _flow.Id,
            FlowName = _flow.Name,
            Kind = "batch.completed",
            SubmissionId = batch.SubmissionId,
            Worker = _workerId,
            Phase = batch.Index.ToString(CultureInfo.InvariantCulture),
            Duration = completed - started,
            Detail = summary.ToString(),
        }, CancellationToken.None).ConfigureAwait(false);
        return summary with { Batches = 1 };
    }

    /// <summary>Delivers a set of leased records: protocol batches when the protocol takes arrays, bounded concurrency otherwise.</summary>
    private async Task<WorkerSummary> ProcessRecordsAsync(IReadOnlyList<(RecordState Record, WorkItem? Item)> loaded, WorkBatchState? batch, string owner, CancellationToken ct)
    {
        var results = new WorkerSummary[loaded.Count];
        var completions = new List<RecordCompletion>();
        var events = new List<DeliveryEvent>();
        var flushLock = new SemaphoreSlim(1, 1);
        var failuresLogged = 0;

        async Task RecordAsync(int index, RecordCompletion completion, DeliveryEvent evt, WorkerSummary summary)
        {
            results[index] = summary;
            List<RecordCompletion>? toWrite = null;
            List<DeliveryEvent>? toEmit = null;
            await flushLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                completions.Add(completion);
                events.Add(evt);
                if (completions.Count >= CompletionFlush)
                {
                    toWrite = [.. completions];
                    toEmit = [.. events];
                    completions.Clear();
                    events.Clear();
                }
            }
            finally
            {
                flushLock.Release();
            }

            if (toWrite is not null)
            {
                await FlushAsync(toWrite, toEmit!).ConfigureAwait(false);
            }

            if (summary.Held > 0 || summary.Failed > 0 || summary.Retried > 0)
            {
                if (Interlocked.Increment(ref failuresLogged) <= FailuresLoggedPerBatch)
                {
                    _logger.LogWarning("{Kind} {SourceKey}: {Detail}", evt.Kind, evt.SourceKey, evt.Detail);
                }
            }
        }

        var groups = Group(loaded, Math.Max(1, _protocol.MaxBatch));
        using var gate = new SemaphoreSlim(Math.Max(1, _flow.Reliability.Concurrency));
        var tasks = groups.Select(async group =>
        {
            try
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Claimed but never started: hand them back so the next pass does not wait for the lease to expire.
                foreach (var (index, record, _) in group)
                {
                    results[index] = WorkerSummary.Empty;
                    await ReleaseAsync(record, owner).ConfigureAwait(false);
                }

                throw;
            }

            try
            {
                await DeliverGroupAsync(group, batch, owner, RecordAsync, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            List<RecordCompletion> rest;
            List<DeliveryEvent> restEvents;
            await flushLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                rest = [.. completions];
                restEvents = [.. events];
                completions.Clear();
                events.Clear();
            }
            finally
            {
                flushLock.Release();
            }

            if (rest.Count > 0)
            {
                await FlushAsync(rest, restEvents).ConfigureAwait(false);
            }
        }

        var total = results.Aggregate(WorkerSummary.Empty, (acc, r) => acc.Add(r ?? WorkerSummary.Empty));
        if (failuresLogged > FailuresLoggedPerBatch)
        {
            _logger.LogWarning("{Count} more record(s) were held, failed or scheduled for retry; see the submission's records.", failuresLogged - FailuresLoggedPerBatch);
        }

        return total;
    }

    private async Task FlushAsync(List<RecordCompletion> completions, List<DeliveryEvent> events)
    {
        await _ledger.CompleteManyAsync(completions, CancellationToken.None).ConfigureAwait(false);
        // The completion callback: everything a listener needs to trace each try, after the ledger rows are written.
        foreach (var evt in events)
        {
            await _listener.OnEventAsync(evt, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static List<List<(int Index, RecordState Record, WorkItem? Item)>> Group(IReadOnlyList<(RecordState Record, WorkItem? Item)> loaded, int size)
    {
        var groups = new List<List<(int, RecordState, WorkItem?)>>();
        var current = new List<(int, RecordState, WorkItem?)>(size);
        for (var i = 0; i < loaded.Count; i++)
        {
            var (record, item) = loaded[i];
            // Records without a document are settled alone: they never reach the protocol.
            if (item is null)
            {
                groups.Add([(i, record, null)]);
                continue;
            }

            current.Add((i, record, item));
            if (current.Count == size)
            {
                groups.Add(current);
                current = new List<(int, RecordState, WorkItem?)>(size);
            }
        }

        if (current.Count > 0)
        {
            groups.Add(current);
        }

        return groups;
    }

    private async Task DeliverGroupAsync(
        List<(int Index, RecordState Record, WorkItem? Item)> group,
        WorkBatchState? batch,
        string owner,
        Func<int, RecordCompletion, DeliveryEvent, WorkerSummary, Task> record,
        CancellationToken ct)
    {
        var started = _time.GetUtcNow().UtcDateTime;
        var works = new List<(int Index, RecordState Record, DeliveryWork Work)>(group.Count);
        // The step progress each record reported during this try, so the completion keeps it for the next try.
        var reportedSteps = new System.Collections.Concurrent.ConcurrentDictionary<Guid, string>();
        foreach (var (index, state, item) in group)
        {
            if (item is null || state.TargetId is null)
            {
                var (completion, evt, summary) = Settle(state, batch, started, RecordStatus.Held, AttemptOutcome.Held, "none", null, null, "no pending document on the record; re-submit the drop", null, null);
                await record(index, completion, evt, summary).ConfigureAwait(false);
                continue;
            }

            // The final check before anything is sent: the queued document and payload against what OSDU holds at the
            // moment this worker has the record. What was planned against an earlier state can already have landed.
            var (sendMetadata, sendPayload) = ChangeDetector.AtPush(state, _flow.Change);
            if (!sendMetadata && !sendPayload)
            {
                var held = $"the final hash check found OSDU already holding this version (metadata hash {state.PendingMetadataHash ?? "none"}"
                    + (state.PendingPayload ? $", payload hash {state.PendingPayloadHash ?? "none"}" : string.Empty) + "); nothing was sent";
                var (completion, evt, summary) = Settle(state, batch, started, RecordStatus.Delivered, AttemptOutcome.Skipped, AttemptPhases.Unchanged, null, held, null, null, null, promote: true, nothingSent: true);
                await record(index, completion, evt, summary).ConfigureAwait(false);
                continue;
            }

            JsonObject document;
            try
            {
                document = JsonNode.Parse(item.Document) as JsonObject ?? throw new DeliveryException("the pending document is not a JSON object");
            }
            catch (JsonException ex)
            {
                var (completion, evt, summary) = Settle(state, batch, started, RecordStatus.Held, AttemptOutcome.Held, "none", null, null, $"the pending document is not valid JSON: {ex.Message}", null, null);
                await record(index, completion, evt, summary).ConfigureAwait(false);
                continue;
            }

            var completedSteps = ParseSteps(state.PendingStepJson);
            var key = state.DeliveryKey;
            works.Add((index, state, new DeliveryWork
            {
                Key = key,
                TargetId = state.TargetId,
                Document = document,
                DeliverMetadata = sendMetadata,
                DeliverPayload = sendPayload,
                Payload = state.PendingPayloadLocation is { } location ? new DropPayloadSource(_drops, location) : null,
                ExistingVersion = state.TargetVersion,
                SourceKey = state.SourceKey,
                Label = state.Label,
                CompletedSteps = completedSteps,
                TargetState = JsonMerge.ToValues(state.TargetStateJson),
                StepCompleted = (step, returned, token) => SaveStepAsync(state, completedSteps, step, returned, reportedSteps, token),
            }));
        }

        if (works.Count == 0)
        {
            return;
        }

        // One id for the try: every OSDU request the protocol sends for these records carries it, and each attempt names
        // it, so what happened can be followed into the services' own logs.
        using var correlation = OsduCorrelation.Begin();
        IReadOnlyList<DeliveryOutcome> outcomes;
        try
        {
            outcomes = works.Count == 1
                ? [await DeliverOneAsync(works[0].Work, ct).ConfigureAwait(false)]
                : await _protocol.DeliverBatchAsync(works.Select(w => w.Work).ToList(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A stop, not a failure: hand the records back now rather than letting the leases expire, and do not
            // charge the interrupted try to the retry budget.
            foreach (var (_, state, _) in works)
            {
                await ReleaseAsync(state, owner).ConfigureAwait(false);
            }

            throw;
        }

        if (outcomes.Count != works.Count)
        {
            throw new DeliveryException($"The {_protocol.Kind} protocol answered {outcomes.Count} outcome(s) for {works.Count} record(s).");
        }

        for (var i = 0; i < works.Count; i++)
        {
            var (index, state, work) = works[i];
            var latestSteps = reportedSteps.TryGetValue(state.DeliveryKey.Value, out var reported) ? reported : state.PendingStepJson;
            var (completion, evt, summary) = Classify(state, batch, started, work, outcomes[i], latestSteps, correlation.Id);
            await record(index, completion, evt, summary).ConfigureAwait(false);
        }
    }

    private async Task<DeliveryOutcome> DeliverOneAsync(DeliveryWork work, CancellationToken ct)
    {
        try
        {
            return await _protocol.DeliverAsync(work, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return DeliveryOutcome.Failed(ex);
        }
    }

    /// <summary>Turns a protocol outcome into the record's next state, its attempt and its event.</summary>
    private (RecordCompletion Completion, DeliveryEvent Event, WorkerSummary Summary) Classify(RecordState record, WorkBatchState? batch, DateTime started, DeliveryWork work, DeliveryOutcome outcome, string? latestSteps, string correlationId)
    {
        var resultJson = ResultJson(outcome, work.CompletedSteps, latestSteps, correlationId);
        record = record with { PendingStepJson = latestSteps };
        if (outcome.Succeeded)
        {
            var phase = (outcome.MetadataDelivered, outcome.PayloadDelivered) switch
            {
                (true, true) => "metadata+payload",
                (true, false) => "metadata",
                (false, true) => "payload",
                _ => "none",
            };
            var targetState = JsonMerge.Merge(record.TargetStateJson, JsonMerge.FromValues(outcome.Returned));
            _logger.LogTrace("Delivered {SourceKey} ({Phase}) as {TargetId} version {Version}.", record.SourceKey, phase, record.TargetId, outcome.TargetVersion);
            return Settle(record, batch, started, RecordStatus.Delivered, AttemptOutcome.Delivered, phase, outcome.TargetVersion, outcome.Detail, null, resultJson, targetState, promote: true);
        }

        var failure = outcome.Failure!;
        switch (failure)
        {
            case RecordHeldException held:
                return Settle(record, batch, started, RecordStatus.Held, AttemptOutcome.Held, "none", null, null, held.Message, resultJson, null, keepSteps: true);
            case HttpStatusException http when IsTerminal(http.StatusCode):
                return Settle(record, batch, started, RecordStatus.Held, AttemptOutcome.Held, "none", null, null, $"HTTP {http.StatusCode} is not retryable: {http.Message}", resultJson, null, keepSteps: true);
            case SqlFlowException or HttpRequestException or IOException or TimeoutException or JsonException or InvalidOperationException:
                {
                    var attempts = record.AttemptCount;
                    if (attempts >= _flow.Reliability.Retry.Attempts)
                    {
                        return Settle(record, batch, started, RecordStatus.Failed, AttemptOutcome.Failed, "none", null, null, $"failed after {attempts} attempt(s): {failure.Message}", resultJson, null, keepSteps: true);
                    }

                    var backoff = RetryPolicy.RecordBackoff(_flow.Reliability.Retry, attempts);

                    // A service that said how long to wait is not asked again sooner: the transport hands a wait
                    // longer than it will sit through inline up here, and the record's next attempt honours it.
                    if (failure is HttpStatusException { RetryAfter: { } asked } && asked > backoff)
                    {
                        backoff = asked;
                    }

                    var next = _time.GetUtcNow().UtcDateTime + backoff;
                    return Settle(record, batch, started, RecordStatus.Pending, AttemptOutcome.Failed, "none", null, null, failure.Message, resultJson, null, keepSteps: true, nextAttempt: next);
                }

            default:
                return Settle(record, batch, started, RecordStatus.Failed, AttemptOutcome.Failed, "none", null, null, $"unexpected failure: {failure.GetType().Name}: {failure.Message}", resultJson, null, keepSteps: true);
        }
    }

    private bool IsTerminal(int status)
    {
        if (_flow.Reliability.SkipStatusCodes.Contains(status))
        {
            return true;
        }

        // Client errors that a retry cannot fix. 401/408/425/429 are transient (token refresh, throttling).
        return status is (int)HttpStatusCode.BadRequest or (int)HttpStatusCode.Forbidden or (int)HttpStatusCode.NotFound
            or (int)HttpStatusCode.MethodNotAllowed or (int)HttpStatusCode.Conflict or (int)HttpStatusCode.RequestEntityTooLarge
            or (int)HttpStatusCode.UnsupportedMediaType or (int)HttpStatusCode.UnprocessableEntity;
    }

    private (RecordCompletion Completion, DeliveryEvent Event, WorkerSummary Summary) Settle(
        RecordState record,
        WorkBatchState? batch,
        DateTime started,
        RecordStatus status,
        AttemptOutcome outcome,
        string phase,
        long? version,
        string? detail,
        string? error,
        string? resultJson,
        string? targetStateJson,
        bool promote = false,
        bool keepSteps = false,
        DateTime? nextAttempt = null,
        bool nothingSent = false)
    {
        var completed = _time.GetUtcNow().UtcDateTime;
        var redacted = error is null ? null : HeaderRedaction.RedactMessage(error);
        var completion = new RecordCompletion
        {
            DeliveryKey = record.DeliveryKey,
            Status = status,
            Promote = promote,
            NothingSent = nothingSent,
            Claimed = ClaimedWork.Of(record),
            TargetVersion = version,
            TargetId = record.TargetId,
            NextAttemptUtc = nextAttempt,
            Error = redacted,
            TargetStateJson = targetStateJson,
            // A retry resumes after the steps that completed; a settled record starts afresh next time.
            PendingStepJson = keepSteps && status == RecordStatus.Pending ? record.PendingStepJson : null,
            Attempt = new AttemptRecord
            {
                DeliveryKey = record.DeliveryKey,
                SubmissionId = record.LastSubmissionId,
                RunId = RunId,
                Worker = _workerId,
                StartedUtc = started,
                CompletedUtc = completed,
                Outcome = outcome,
                Phase = phase,
                MetadataHash = record.PendingMetadataHash,
                PayloadHash = record.PendingPayloadHash,
                TargetVersion = version,
                // The error column holds errors; what an attempt that did not fail has to say goes with its result.
                Error = redacted,
                ResultJson = redacted is null ? AttemptResult.WithDetail(resultJson, detail) : resultJson,
                WorkBatch = batch?.Index ?? record.WorkBatch,
            },
        };

        var evt = new DeliveryEvent
        {
            AtUtc = completed,
            FlowId = _flow.Id,
            FlowName = _flow.Name,
            Kind = status switch
            {
                RecordStatus.Delivered when nothingSent => "record.unchanged",
                RecordStatus.Delivered => "record.delivered",
                RecordStatus.Pending => "record.retry",
                RecordStatus.Held => "record.held",
                _ => "record.failed",
            },
            SubmissionId = record.LastSubmissionId,
            DeliveryKey = record.DeliveryKey,
            SourceKey = record.SourceKey,
            Label = record.Label,
            TargetId = record.TargetId,
            TargetVersion = version,
            Worker = _workerId,
            Phase = phase,
            Duration = completed - started,
            Detail = redacted ?? detail ?? (nextAttempt is { } n ? $"next attempt at {n:u}" : null),
            Returned = resultJson,
        };

        var summary = status switch
        {
            RecordStatus.Delivered when nothingSent => new WorkerSummary(1, 0, 0, 0, 0, Unchanged: 1),
            RecordStatus.Delivered => new WorkerSummary(1, 1, 0, 0, 0),
            RecordStatus.Pending => new WorkerSummary(1, 0, 1, 0, 0),
            RecordStatus.Held => new WorkerSummary(1, 0, 0, 1, 0),
            _ => new WorkerSummary(1, 0, 0, 0, 1),
        };
        return (completion, evt, summary);
    }

    /// <summary>
    /// The attempt's result: the correlation id its OSDU requests carried, every step (including the ones resumed from an
    /// earlier try) and what came back.
    /// </summary>
    internal static string? ResultJson(DeliveryOutcome outcome, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> completedBefore, string? reportedDuringTry = null, string? correlationId = null)
    {
        var steps = new JsonArray();
        foreach (var (name, returned) in completedBefore)
        {
            if (!outcome.Steps.Any(s => s.Name == name))
            {
                steps.Add(new JsonObject { ["name"] = name, ["resumed"] = true, ["returned"] = ToNode(returned) });
            }
        }

        // Steps the protocol reported before the try failed: they completed against the target, and the next try
        // resumes after them, so the attempt says so even though the protocol produced no outcome.
        foreach (var (name, returned) in ParseSteps(reportedDuringTry))
        {
            if (!completedBefore.ContainsKey(name) && !outcome.Steps.Any(s => s.Name == name))
            {
                steps.Add(new JsonObject { ["name"] = name, ["completed"] = true, ["returned"] = ToNode(returned) });
            }
        }

        foreach (var step in outcome.Steps)
        {
            var node = new JsonObject
            {
                ["name"] = step.Name,
                ["startedUtc"] = step.StartedUtc.ToString("O", CultureInfo.InvariantCulture),
                ["ms"] = (long)(step.CompletedUtc - step.StartedUtc).TotalMilliseconds,
            };
            if (step.Status is { } status)
            {
                node["status"] = status;
            }

            if (step.Resumed)
            {
                node["resumed"] = true;
            }

            if (step.Error is { } error)
            {
                node["error"] = HeaderRedaction.RedactMessage(error);
            }

            if (step.Returned.Count > 0)
            {
                node["returned"] = ToNode(step.Returned);
            }

            steps.Add(node);
        }

        if (steps.Count == 0 && outcome.Returned.Count == 0 && correlationId is null)
        {
            return null;
        }

        var result = new JsonObject();
        if (correlationId is not null)
        {
            result["correlationId"] = correlationId;
        }

        result["steps"] = steps;
        if (outcome.Returned.Count > 0)
        {
            result["returned"] = ToNode(outcome.Returned);
        }

        return result.ToJsonString();
    }

    private static JsonObject ToNode(IReadOnlyDictionary<string, string> values)
    {
        var node = new JsonObject();
        foreach (var (name, value) in values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            node[name] = value;
        }

        return node;
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ParseSteps(string? json)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (name, value) in JsonMerge.Parse(json))
        {
            if (value is JsonObject values)
            {
                result[name] = JsonMerge.ToValues(values.ToJsonString());
            }
        }

        return result;
    }

    /// <summary>
    /// Persists a completed step before the protocol moves on, so a crash never repeats it. The step belongs to the
    /// document the record was claimed with; newer work queued behind the try never inherits it.
    /// </summary>
    private async Task SaveStepAsync(
        RecordState claimed,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> completed,
        string step,
        IReadOnlyDictionary<string, string> returned,
        System.Collections.Concurrent.ConcurrentDictionary<Guid, string> reported,
        CancellationToken ct)
    {
        var key = claimed.DeliveryKey;
        var reference = claimed.PendingDocumentRef
            ?? throw new DeliveryException($"Record {key} is being delivered without a pending document reference, so its step progress cannot be tied to the document it belongs to.");
        var node = new JsonObject();
        foreach (var (name, values) in completed)
        {
            node[name] = ToNode(values);
        }

        if (reported.TryGetValue(key.Value, out var earlier))
        {
            foreach (var (name, value) in JsonMerge.Parse(earlier))
            {
                node[name] = value?.DeepClone();
            }
        }

        node[step] = ToNode(returned);
        var json = node.ToJsonString();
        reported[key.Value] = json;
        await _ledger.SaveStepAsync(key, claimed.LastSubmissionId, reference, json, ct).ConfigureAwait(false);
    }

    private async Task<WorkItem?> LoadItemAsync(RecordState record, CancellationToken ct)
    {
        if (record.LastSubmissionId is not { } submissionId || !DocumentRef.TryParse(record.PendingDocumentRef, out var reference))
        {
            return null;
        }

        var submission = await SubmissionAsync(submissionId, ct).ConfigureAwait(false);
        if (submission.WorkLocation is null)
        {
            return null;
        }

        try
        {
            return await WorkBatchFile.ReadOneAsync(_stores, submission.WorkLocation, submissionId, reference, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DeliveryException or IOException or JsonException)
        {
            _logger.LogWarning("The pending document of {SourceKey} could not be read from batch {Batch}: {Message}", record.SourceKey, reference.Batch, HeaderRedaction.RedactMessage(ex.Message));
            return null;
        }
    }

    private Task<SubmissionState> SubmissionAsync(Guid submissionId, CancellationToken ct)
    {
        var lazy = _submissions.GetOrAdd(submissionId, id => new Lazy<Task<SubmissionState>>(() => LoadSubmissionAsync(id, ct)));
        if (lazy.Value.IsFaulted || lazy.Value.IsCanceled)
        {
            _submissions.TryRemove(submissionId, out _);
        }

        return lazy.Value;
    }

    private async Task<SubmissionState> LoadSubmissionAsync(Guid submissionId, CancellationToken ct)
        => await _ledger.GetSubmissionAsync(submissionId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Submission {submissionId} is not in the ledger.");

    private static DeliveryException MissingWorkLocation(SubmissionState submission)
        => new($"Submission {submission.SubmissionId} records no work location; its batches cannot be read.");

    private async Task ReleaseAsync(RecordState record, string owner)
    {
        try
        {
            var released = await _ledger.ReleaseLeaseAsync(record.DeliveryKey, owner, countAttempt: false, _time.GetUtcNow().UtcDateTime, CancellationToken.None).ConfigureAwait(false);
            if (released)
            {
                await _listener.OnEventAsync(new DeliveryEvent
                {
                    AtUtc = _time.GetUtcNow().UtcDateTime,
                    FlowId = _flow.Id,
                    FlowName = _flow.Name,
                    Kind = "record.released",
                    SubmissionId = record.LastSubmissionId,
                    DeliveryKey = record.DeliveryKey,
                    SourceKey = record.SourceKey,
                    Label = record.Label,
                    TargetId = record.TargetId,
                    Worker = _workerId,
                    Detail = "worker stopped; lease released without charging an attempt",
                }, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is DeliveryException or InvalidOperationException or System.Data.Common.DbException)
        {
            // The lease will expire on its own; losing one attempt of budget is the worst case.
            _logger.LogWarning("Could not release {SourceKey} on shutdown: {Message}", record.SourceKey, ex.Message);
        }
    }

    private async Task ReleaseBatchAsync(WorkBatchState batch, string owner)
    {
        try
        {
            await _ledger.ReleaseWorkBatchAsync(batch.SubmissionId, batch.Index, owner, _time.GetUtcNow().UtcDateTime, CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("Released batch {Batch} on shutdown; it will be picked up by the next pass.", batch.Index);
        }
        catch (Exception ex) when (ex is DeliveryException or InvalidOperationException or System.Data.Common.DbException)
        {
            _logger.LogWarning("Could not release batch {Batch} on shutdown: {Message}", batch.Index, ex.Message);
        }
    }

    private async Task RenewBatchLoopAsync(WorkBatchState batch, string owner, CancellationToken ct)
    {
        var lease = Lease;
        var interval = TimeSpan.FromMilliseconds(Math.Max(lease.TotalMilliseconds / 2, 1000));
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(interval, _time, ct).ConfigureAwait(false);
            var renewed = await _ledger.RenewWorkBatchLeaseAsync(batch.SubmissionId, batch.Index, owner, lease, _time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            if (!renewed)
            {
                _logger.LogWarning("Lease on batch {Batch} could not be renewed; another worker may have reclaimed it.", batch.Index);
            }
        }
    }

    private async Task RenewRecordsLoopAsync(IReadOnlyList<DeliveryKey> keys, string owner, CancellationToken ct)
    {
        var lease = Lease;
        var interval = TimeSpan.FromMilliseconds(Math.Max(lease.TotalMilliseconds / 2, 1000));
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(interval, _time, ct).ConfigureAwait(false);
            foreach (var key in keys)
            {
                await _ledger.RenewLeaseAsync(key, owner, lease, _time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task AwaitQuietlyAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected: the renewal loop was told to stop
        }
    }
}

/// <summary>Opens payload chunks from the drop; each open is a fresh stream so a retried request re-reads the blob.</summary>
public sealed class DropPayloadSource : IPayloadSource
{
    private readonly IDropReader _drops;
    private readonly string _location;
    private IReadOnlyList<PayloadChunk>? _chunks;

    public DropPayloadSource(IDropReader drops, string location)
    {
        ArgumentNullException.ThrowIfNull(drops);
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        _drops = drops;
        _location = location;
    }

    public async Task<IReadOnlyList<PayloadChunk>> ListChunksAsync(CancellationToken ct = default)
        => _chunks ??= await _drops.ListPayloadChunksAsync(_location, ct).ConfigureAwait(false);

    public Task<Stream> OpenAsync(PayloadChunk chunk, CancellationToken ct = default) => _drops.OpenChunkAsync(chunk, ct);
}
