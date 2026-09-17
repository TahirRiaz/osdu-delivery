using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
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
/// rendered documents and every record of it that is due) and, when no batch is claimable, a group of records that came
/// due for a retry, each under one lease; processes them with bounded concurrency, in protocol batches when the protocol
/// accepts arrays. While it delivers it writes nothing to the records: it renews its lease's one row and appends what it
/// learns (every step the target answered, before the delivery goes on, and each try's attempt and outcome) through a
/// <see cref="LeaseJournal"/>, which writes what its concurrent deliveries hand over together. At every renewal the
/// lease applies what was appended so far and the worker reports the batch's progress on the run's trace; closing the
/// lease applies the rest and settles the batch. The completion callback (<see cref="IDeliveryListener"/>) fires after
/// every outcome is written. Any number of workers, on any number of nodes, share the ledger safely: a crashed worker's
/// lease runs out and the next claim of its flow recovers it, a stopping worker closes its lease at once, and a worker
/// whose lease was taken over stops sending under it. The run trace carries batch-level lines, never one per record.
/// </summary>
public sealed class DeliveryWorker
{
    /// <summary>The longest one wait for records in backoff lasts before the queue is looked at again.</summary>
    public static readonly TimeSpan DefaultMaxWait = TimeSpan.FromMinutes(5);

    private const int FailuresLoggedPerBatch = 10;

    private readonly ILedger _ledger;
    private readonly IPayloadFiles _payloads;
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

    /// <summary>
    /// What judges the outcomes of this worker's records for the interface as a whole (docs/interfaces-design.md section
    /// 8.2), or null when nothing does. The worker reports every settled or retried record to it; stopping the work when it
    /// trips is up to whoever cancels the token the worker was handed.
    /// </summary>
    public FailureGuard? Guard { get; init; }

    /// <summary>How often a lease is renewed and checkpointed while the worker delivers: half the lease unless set (tests shorten it).</summary>
    internal TimeSpan? KeepInterval { get; init; }

    public DeliveryWorker(
        ILedger ledger,
        IPayloadFiles payloads,
        FileStoreRegistry stores,
        IDeliveryProtocol protocol,
        FlowDefinition flow,
        TimeProvider time,
        IDeliveryListener listener,
        ILogger<DeliveryWorker> logger,
        string? workerId = null)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(payloads);
        ArgumentNullException.ThrowIfNull(stores);
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(logger);
        _ledger = ledger;
        _payloads = payloads;
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

    /// <summary>One claim: a work batch when one is claimable, else one group of due records.</summary>
    public async Task<WorkerSummary> PassAsync(Guid? submissionId, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var claimed = await _ledger.ClaimWorkBatchAsync(_flow.Id, submissionId, _workerId, Lease, now, RunId, ct).ConfigureAwait(false);
        if (claimed is not null)
        {
            _logger.LogInformation(
                "Claimed batch {Batch} of submission {SubmissionId}: {Records} record(s) due.", claimed.Batch.Index, claimed.Batch.SubmissionId, claimed.Records.Count);
            return await ProcessLeaseAsync(claimed.Lease, claimed.Batch, claimed.Records, ct).ConfigureAwait(false);
        }

        var due = await _ledger.ClaimAsync(_flow.Id, submissionId, _workerId, _flow.Reliability.BatchSize, Lease, now, RunId, ct).ConfigureAwait(false);
        if (due.Lease is not { } lease)
        {
            return WorkerSummary.Empty;
        }

        _logger.LogInformation("Claimed {Count} record(s) due for a retry.", due.Records.Count);
        return await ProcessLeaseAsync(lease, batch: null, due.Records, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Delivers what one lease holds and closes it: done with the batch's counts, stopped when the run is cancelled,
    /// failed when the work cannot be read. A lease another worker took over is closed for what this worker sent, and the
    /// rest is left to that worker.
    /// </summary>
    private async Task<WorkerSummary> ProcessLeaseAsync(LeaseState lease, WorkBatchState? batch, IReadOnlyList<RecordState> records, CancellationToken ct)
    {
        var started = _time.GetUtcNow().UtcDateTime;
        var journal = new LeaseJournal(_ledger, _flow.Id, lease.Token, _listener);
        using var lost = new CancellationTokenSource();
        using var keeping = new CancellationTokenSource();
        using var sending = CancellationTokenSource.CreateLinkedTokenSource(ct, lost.Token);
        var keeper = KeepLeaseAsync(lease, journal, records.Count, batch, lost, keeping.Token);
        WorkerSummary summary;
        try
        {
            var loaded = batch is null
                ? await LoadRecordsAsync(records, sending.Token).ConfigureAwait(false)
                : await LoadBatchAsync(batch, records, sending.Token).ConfigureAwait(false);
            summary = await ProcessRecordsAsync(loaded, batch, journal, sending.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lost.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            await StopKeepingAsync(keeping, keeper).ConfigureAwait(false);
            var sent = Summarize(journal) with { Batches = batch is null ? 0 : 1 };
            _logger.LogWarning(
                "The lease on {What} is no longer this worker's (it ran out and another worker took it over), so it stopped sending: {Summary}. The rest is left to that worker.",
                Describe(lease, batch), sent);
            await _ledger.CloseLeaseAsync(lease.Token, new LeaseClosing { End = LeaseEnd.Stopped }, _time.GetUtcNow().UtcDateTime, CancellationToken.None).ConfigureAwait(false);
            return sent;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A stop, not a failure: hand the work back now rather than letting the lease run out, and do not charge the
            // interrupted tries to the retry budget.
            await StopKeepingAsync(keeping, keeper).ConfigureAwait(false);
            await CloseOnStopAsync(lease, batch).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is SqlFlowException or IOException or InvalidOperationException or JsonException)
        {
            await StopKeepingAsync(keeping, keeper).ConfigureAwait(false);
            var message = HeaderRedaction.RedactMessage(ex.Message);
            _logger.LogError("{What} could not be processed: {Message}", Describe(lease, batch), message);
            await _ledger.CloseLeaseAsync(lease.Token, new LeaseClosing { End = LeaseEnd.Failed, Failure = message }, _time.GetUtcNow().UtcDateTime, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            await StopKeepingAsync(keeping, keeper).ConfigureAwait(false);
        }

        var completed = _time.GetUtcNow().UtcDateTime;
        await _ledger.CloseLeaseAsync(
            lease.Token,
            new LeaseClosing { End = LeaseEnd.Done, Delivered = summary.Delivered, Held = summary.Held, Failed = summary.Failed, Retrying = summary.Retried },
            completed,
            CancellationToken.None).ConfigureAwait(false);
        if (batch is null)
        {
            return summary;
        }

        _logger.LogInformation("Batch {Batch} done in {Seconds:0.#}s: {Summary}", batch.Index, (completed - started).TotalSeconds, summary);
        await _listener.OnEventAsync(new DeliveryEvent
        {
            AtUtc = completed,
            FlowId = _flow.Id,
            FlowName = _flow.Label,
            Interface = _flow.Interface,
            Kind = "batch.completed",
            SubmissionId = batch.SubmissionId,
            Worker = _workerId,
            Phase = batch.Index.ToString(CultureInfo.InvariantCulture),
            Duration = completed - started,
            Detail = summary.ToString(),
        }, CancellationToken.None).ConfigureAwait(false);
        return summary with { Batches = 1 };
    }

    /// <summary>The batch's documents for the records its lease holds, read once from its file.</summary>
    private async Task<IReadOnlyList<(RecordState Record, WorkItem? Item)>> LoadBatchAsync(WorkBatchState batch, IReadOnlyList<RecordState> records, CancellationToken ct)
    {
        var loaded = new List<(RecordState Record, WorkItem? Item)>(records.Count);
        if (records.Count == 0)
        {
            return loaded;
        }

        var submission = await SubmissionAsync(batch.SubmissionId, ct).ConfigureAwait(false);
        var wanted = records.ToDictionary(r => r.DeliveryKey.Value);
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

        return loaded;
    }

    /// <summary>The documents of records due for a retry, each read from its own batch.</summary>
    private async Task<IReadOnlyList<(RecordState Record, WorkItem? Item)>> LoadRecordsAsync(IReadOnlyList<RecordState> records, CancellationToken ct)
    {
        var loaded = new List<(RecordState Record, WorkItem? Item)>(records.Count);
        foreach (var record in records)
        {
            loaded.Add((record, await LoadItemAsync(record, ct).ConfigureAwait(false)));
        }

        return loaded;
    }

    private async Task CloseOnStopAsync(LeaseState lease, WorkBatchState? batch)
    {
        try
        {
            await _ledger.CloseLeaseAsync(lease.Token, new LeaseClosing { End = LeaseEnd.Stopped }, _time.GetUtcNow().UtcDateTime, CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("Handed {What} back on shutdown; the next pass picks it up.", Describe(lease, batch));
        }
        catch (Exception ex) when (ex is DeliveryException or InvalidOperationException or DbException or TimeoutException)
        {
            // The lease runs out on its own and the next claim of the flow recovers it, charging the interrupted tries.
            _logger.LogWarning("Could not hand {What} back on shutdown ({Message}); its lease runs out and the next claim recovers it.", Describe(lease, batch), ex.Message);
        }
    }

    /// <summary>
    /// Keeps a lease while the worker delivers under it: renews its one row every half lease and, at each renewal, applies
    /// what the worker appended so far, so the records show their outcomes while the batch runs, and reports the progress
    /// on the run's trace. A lease that is no longer this worker's, or that could not be renewed before it ran out, is
    /// lost: <paramref name="lost"/> is cancelled and the worker stops sending under it.
    /// </summary>
    private async Task KeepLeaseAsync(LeaseState lease, LeaseJournal journal, int records, WorkBatchState? batch, CancellationTokenSource lost, CancellationToken ct)
    {
        var length = Lease;
        var interval = KeepInterval ?? TimeSpan.FromMilliseconds(Math.Max(length.TotalMilliseconds / 2, 1000));
        var retry = KeepInterval ?? TimeSpan.FromMilliseconds(Math.Max(length.TotalMilliseconds / 10, 500));
        var expires = lease.ExpiresUtc;
        var wait = interval;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(wait, _time, ct).ConfigureAwait(false);
            var now = _time.GetUtcNow().UtcDateTime;
            bool renewed;
            try
            {
                renewed = await _ledger.RenewLeaseAsync(lease.Token, length, now, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DbException or TimeoutException or InvalidOperationException)
            {
                if (now + retry >= expires)
                {
                    _logger.LogWarning("The lease on {What} could not be renewed before it ran out ({Message}); the worker stops sending under it.", Describe(lease, batch), ex.Message);
                    await lost.CancelAsync().ConfigureAwait(false);
                    return;
                }

                _logger.LogWarning("Renewing the lease on {What} failed ({Message}); trying again in {Seconds}s.", Describe(lease, batch), ex.Message, (int)retry.TotalSeconds);
                wait = retry;
                continue;
            }

            if (!renewed)
            {
                await lost.CancelAsync().ConfigureAwait(false);
                return;
            }

            expires = now + length;
            wait = interval;
            try
            {
                await _ledger.CheckpointLeaseAsync(lease.Token, now, ct).ConfigureAwait(false);
                await ReportProgressAsync(lease, journal, records, batch, now).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DbException or TimeoutException or InvalidOperationException or DeliveryException)
            {
                _logger.LogWarning("The progress of {What} could not be applied yet ({Message}); closing its lease applies it.", Describe(lease, batch), ex.Message);
            }
        }
    }

    private async Task ReportProgressAsync(LeaseState lease, LeaseJournal journal, int records, WorkBatchState? batch, DateTime now)
    {
        var sent = Summarize(journal);
        await _listener.OnEventAsync(new DeliveryEvent
        {
            AtUtc = now,
            FlowId = _flow.Id,
            FlowName = _flow.Label,
            Interface = _flow.Interface,
            Kind = "batch.progress",
            SubmissionId = batch?.SubmissionId ?? lease.SubmissionId,
            Worker = _workerId,
            Phase = batch?.Index.ToString(CultureInfo.InvariantCulture) ?? "retries",
            Duration = now - lease.AcquiredUtc,
            Detail = string.Create(CultureInfo.InvariantCulture, $"{sent.Processed} of {records} record(s) settled so far: {sent}"),
        }, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>What a lease's journal has written so far, as a summary.</summary>
    private static WorkerSummary Summarize(LeaseJournal journal)
    {
        var (byStatus, unchanged) = journal.Written;
        var delivered = byStatus.GetValueOrDefault(RecordStatus.Delivered);
        var retried = byStatus.GetValueOrDefault(RecordStatus.Pending);
        var held = byStatus.GetValueOrDefault(RecordStatus.Held);
        var failed = byStatus.GetValueOrDefault(RecordStatus.Failed);
        return new WorkerSummary(delivered + retried + held + failed, delivered - unchanged, retried, held, failed, Unchanged: unchanged);
    }

    private static string Describe(LeaseState lease, WorkBatchState? batch)
        => batch is null
            ? $"the records due for a retry (lease {lease.Token})"
            : string.Create(CultureInfo.InvariantCulture, $"batch {batch.Index} of submission {batch.SubmissionId}");

    private static async Task StopKeepingAsync(CancellationTokenSource keeping, Task keeper)
    {
        if (!keeping.IsCancellationRequested)
        {
            await keeping.CancelAsync().ConfigureAwait(false);
        }

        await AwaitQuietlyAsync(keeper).ConfigureAwait(false);
    }

    /// <summary>Delivers a set of leased records: protocol batches when the protocol takes arrays, bounded concurrency otherwise.</summary>
    private async Task<WorkerSummary> ProcessRecordsAsync(IReadOnlyList<(RecordState Record, WorkItem? Item)> loaded, WorkBatchState? batch, LeaseJournal journal, CancellationToken ct)
    {
        var results = new WorkerSummary[loaded.Count];
        var failuresLogged = 0;

        async Task RecordAsync(int index, RecordCompletion completion, DeliveryEvent evt, WorkerSummary summary, Exception? failure)
        {
            // Written whatever happens to the run meanwhile: the try happened, and the ledger says so.
            await journal.OutcomeAsync(completion, evt).ConfigureAwait(false);
            results[index] = summary;
            Observe(completion.Status, evt.Detail, failure);
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
            void NeverStarted()
            {
                // Claimed but never started: closing the lease hands them back.
                foreach (var (index, _, _) in group)
                {
                    results[index] = WorkerSummary.Empty;
                }
            }

            try
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                NeverStarted();
                throw;
            }

            // A slot the delivery before gave up can be handed to this group just as that delivery stops the work (a failure
            // guard tripping, the run being cancelled): the semaphore then reports the slot taken although the stop came
            // first. The stop wins, so nothing is sent after it.
            if (ct.IsCancellationRequested)
            {
                gate.Release();
                NeverStarted();
                ct.ThrowIfCancellationRequested();
            }

            try
            {
                await DeliverGroupAsync(group, batch, journal, RecordAsync, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
        var total = results.Aggregate(WorkerSummary.Empty, (acc, r) => acc.Add(r ?? WorkerSummary.Empty));
        if (failuresLogged > FailuresLoggedPerBatch)
        {
            _logger.LogWarning("{Count} more record(s) were held, failed or scheduled for retry; see the submission's records.", failuresLogged - FailuresLoggedPerBatch);
        }

        return total;
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
        LeaseJournal journal,
        Func<int, RecordCompletion, DeliveryEvent, WorkerSummary, Exception?, Task> record,
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
                var (completion, evt, summary) = Settle(
                    state, batch, started, RecordStatus.Held, AttemptOutcome.Held, "none", null, null,
                    "no pending document on the record; release or redeliver it to plan it again", null, null);
                await record(index, completion, evt, summary, null).ConfigureAwait(false);
                continue;
            }

            // A flow writes only the OSDU ids its records claimed when their documents were queued; staging claims them,
            // so an unclaimed id here means the record's state was changed outside the ledger, and nothing is sent.
            if (!string.Equals(state.ClaimedTargetId, state.TargetId, StringComparison.Ordinal))
            {
                var (completion, evt, summary) = Settle(
                    state, batch, started, RecordStatus.Held, AttemptOutcome.Held, "none", null, null,
                    $"the record's OSDU id {state.TargetId} is not claimed by this flow (claimed: {state.ClaimedTargetId ?? "none"}), so nothing was sent; redeliver it to plan it again",
                    null, null);
                await record(index, completion, evt, summary, null).ConfigureAwait(false);
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
                await record(index, completion, evt, summary, null).ConfigureAwait(false);
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
                await record(index, completion, evt, summary, null).ConfigureAwait(false);
                continue;
            }

            // A payload sent in parts lists each part with its files and hash; one payload set is its folder.
            IPayloadSource? single = null;
            IReadOnlyList<WorkPayloadPart> parts = [];
            IReadOnlySet<string> forced = new HashSet<string>(StringComparer.Ordinal);
            if (state.PendingPayloadLocation is { } location)
            {
                if (CompositePayload.IsComposite(location))
                {
                    CompositePayload composite;
                    try
                    {
                        composite = CompositePayload.Decode(location);
                    }
                    catch (DeliveryException ex)
                    {
                        var (completion, evt, summary) = Settle(state, batch, started, RecordStatus.Held, AttemptOutcome.Held, "none", null, null, ex.Message, null, null);
                        await record(index, completion, evt, summary, null).ConfigureAwait(false);
                        continue;
                    }

                    parts = composite.Parts
                        .Select(p => new WorkPayloadPart(p.Role, p.Payload, p.Location is null ? null : new StoragePayloadSource(_payloads, PayloadLocation.Parse(p.Location)), p.Hash))
                        .ToList();
                    forced = composite.Forced;
                }
                else
                {
                    single = new StoragePayloadSource(_payloads, PayloadLocation.Parse(location));
                }
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
                Payload = single,
                Parts = parts,
                ForcedParts = forced,
                ExistingVersion = state.TargetVersion,
                SourceKey = state.SourceKey,
                Label = state.Label,
                CompletedSteps = completedSteps,
                TargetState = JsonMerge.ToValues(state.TargetStateJson),
                StepCompleted = (step, returned, _) => SaveStepAsync(state, completedSteps, step, returned, reportedSteps, journal),
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
            // A stop, not a failure: closing the lease hands the records back without charging the interrupted try.
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
            await record(index, completion, evt, summary, outcomes[i].Failure).ConfigureAwait(false);
        }
    }

    /// <summary>Reports a record's outcome to the guard: a delivery ends every run of failures, a failure counts in its class.</summary>
    private void Observe(RecordStatus status, string? detail, Exception? failure)
    {
        if (Guard is not { } guard)
        {
            return;
        }

        switch (status)
        {
            case RecordStatus.Delivered:
                guard.Succeeded();
                break;
            case RecordStatus.Pending:
                guard.Failed(FailureGuard.Classify(failure), settled: false, detail);
                break;
            default:
                guard.Failed(FailureGuard.Classify(failure), settled: true, detail);
                break;
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
            case OsduStatusException http when IsTerminal(http.StatusCode):
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
                    if (failure is OsduStatusException { RetryAfter: { } asked } && asked > backoff)
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
                // The ingestion file and row the document being delivered was built from: what ties this try to the
                // exact line of the exact file, whatever the record later moves on to.
                SourceFileName = record.PendingSourceFileName,
                SourceRowNumber = record.PendingSourceRowNumber,
                SourceUpdatedUtc = record.PendingSourceUpdatedUtc,
            },
        };

        var evt = new DeliveryEvent
        {
            AtUtc = completed,
            FlowId = _flow.Id,
            FlowName = _flow.Label,
            Interface = _flow.Interface,
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
    /// Appends a completed step before the protocol moves on, so a crash never repeats it. The step belongs to the document
    /// the record was claimed with; newer work queued behind the try never inherits it. It is written whatever happens to
    /// the run meanwhile, since what it records has already happened at the target.
    /// </summary>
    private Task SaveStepAsync(
        RecordState claimed,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> completed,
        string step,
        IReadOnlyDictionary<string, string> returned,
        System.Collections.Concurrent.ConcurrentDictionary<Guid, string> reported,
        LeaseJournal journal)
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
        return journal.StepAsync(new RecordStep(key, claimed.LastSubmissionId, reference, json, _time.GetUtcNow().UtcDateTime));
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
