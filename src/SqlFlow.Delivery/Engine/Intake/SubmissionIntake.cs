using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Planning;
using SqlFlow.Delivery.Storage;

namespace SqlFlow.Delivery.Engine.Intake;

/// <summary>What one intake pass produced: the counts of what it planned and the batches it wrote.</summary>
public sealed record IntakeCounts(long Records, long Planned, long Skipped, long Held, long Blocked, long Untracked, int Batches)
{
    public static IntakeCounts Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);

    public IntakeCounts Add(IntakeCounts other) => new(
        Records + other.Records, Planned + other.Planned, Skipped + other.Skipped, Held + other.Held, Blocked + other.Blocked, Untracked + other.Untracked, Batches + other.Batches);

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{Records} record(s): {Planned} to deliver in {Batches} batch(es), {Skipped} unchanged, {Held} held, {Blocked} blocked, {Untracked} untracked");
}

public sealed record IntakeResult(SubmissionState Submission, PlanHeader? Header, IntakeCounts Counts, bool AlreadyProcessed)
{
    public bool NothingToDo => AlreadyProcessed || Header is null || Header.SkippedWholeRun || Counts.Planned == 0;
}

/// <summary>
/// Turns a manifest notification into ledger state (design.md sections 3.3, 7.2 and 16.2): registers the
/// submission under its idempotency key, streams the drop through the planner, writes the rendered documents to
/// work batches at the flow's work location, and stages the pending work per record in the ledger, batch by batch.
/// Nothing about the drop is held in memory beyond the batch being written. Re-running an intake for a completed
/// submission does nothing unless forced; re-running one that was interrupted picks up where it stopped. An intake
/// can be restricted to a subset of the drop's partitions, which is how a fan-out spreads it across nodes.
/// </summary>
public sealed class SubmissionIntake
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(15);

    private readonly ILedger _ledger;
    private readonly Planner _planner;
    private readonly FileStoreRegistry _stores;
    private readonly TimeProvider _time;
    private readonly IDeliveryListener _listener;
    private readonly ILogger<SubmissionIntake> _logger;

    /// <summary>The platform run the intake happens in, stamped on the pending work it writes.</summary>
    public Guid? RunId { get; init; }

    public SubmissionIntake(ILedger ledger, Planner planner, FileStoreRegistry stores, TimeProvider time, IDeliveryListener listener, ILogger<SubmissionIntake> logger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(stores);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(logger);
        _ledger = ledger;
        _planner = planner;
        _stores = stores;
        _time = time;
        _listener = listener;
        _logger = logger;
    }

    /// <summary>
    /// Registers and plans the drop. With <paramref name="partitions"/> null the whole drop is planned here and the
    /// submission is closed with its counts (or left planned for the drain); with a partition subset only those
    /// partitions are planned and the submission is left for the coordinating run to finalise.
    /// </summary>
    public async Task<IntakeResult> IntakeAsync(
        FlowDefinition flow,
        ResolvedMapping resolved,
        IReadOnlyDictionary<string, string> parameters,
        string dropLocation,
        bool force,
        IReadOnlyList<int>? partitions = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(parameters);

        var header = await _planner.OpenAsync(flow, resolved, parameters, dropLocation, force, ct).ConfigureAwait(false);
        var manifest = header.Drop.Manifest;
        var now = _time.GetUtcNow().UtcDateTime;
        var workRoot = FlowParameters.WorkLocation(flow, parameters, header.Drop.Location);

        var (submission, created) = await _ledger.RegisterSubmissionAsync(new SubmissionState
        {
            SubmissionId = manifest.SubmissionId,
            FlowId = flow.Id,
            FlowName = flow.Name,
            MappingReference = resolved.Mapping.Reference,
            RenderContext = resolved.Context.Canonical(),
            DropLocation = header.Drop.Location,
            WorkLocation = workRoot,
            Partitions = manifest.PartitionCount,
            ParametersJson = JsonSerializer.Serialize(parameters),
            RecordCount = manifest.RecordCount,
            ReceivedUtc = now,
        }, ct).ConfigureAwait(false);

        if (!created && submission.Status == SubmissionStatus.Completed && !force)
        {
            _logger.LogInformation("Submission {SubmissionId} was already completed at {CompletedUtc}; nothing to do (use --force to re-plan).", submission.SubmissionId, submission.CompletedUtc);
            return new IntakeResult(submission, header, IntakeCounts.Empty, AlreadyProcessed: true);
        }

        if (!created && partitions is null)
        {
            _logger.LogInformation("Submission {SubmissionId} exists with status {Status}; re-planning against the current ledger.", submission.SubmissionId, submission.Status);
        }

        if (header.SkippedWholeRun)
        {
            if (partitions is null)
            {
                submission = submission with { Status = SubmissionStatus.Completed, StartedUtc = now, CompletedUtc = now, Error = null };
                await _ledger.UpdateSubmissionAsync(submission, ct).ConfigureAwait(false);
                await EmitAsync(flow, submission, "submission.completed", header.SkipReason, ct).ConfigureAwait(false);
            }

            return new IntakeResult(submission, header, IntakeCounts.Empty, AlreadyProcessed: false);
        }

        if (partitions is null)
        {
            submission = submission with { Status = SubmissionStatus.Received, StartedUtc = now, Error = null, WorkLocation = workRoot, Partitions = manifest.PartitionCount };
            await _ledger.UpdateSubmissionAsync(submission, ct).ConfigureAwait(false);
        }

        var counts = await StageAsync(flow, header, submission, workRoot, partitions, ct).ConfigureAwait(false);

        if (partitions is null)
        {
            if (manifest.RecordCount > 0 && manifest.RecordCount != counts.Records)
            {
                _logger.LogWarning("{Issue}", Planner.RecordCountIssue(flow, manifest.RecordCount, counts.Records).Message);
            }

            submission = await FinalizePlanningAsync(flow, submission.SubmissionId, parameters, DropManifestSummary.Of(manifest), counts, ct).ConfigureAwait(false);
        }
        else
        {
            _logger.LogInformation("Intake of partitions {Partitions}: {Counts}.", DescribePartitions(partitions), counts);
        }

        return new IntakeResult(submission, header, counts, AlreadyProcessed: false);
    }

    /// <summary>
    /// Closes the planning phase of a submission with the totals of every intake pass (one, or one per fan-out
    /// member) and records the tier-0 watermarks. The submission is planned when there is work, completed when not.
    /// </summary>
    public async Task<SubmissionState> FinalizePlanningAsync(
        FlowDefinition flow, Guid submissionId, IReadOnlyDictionary<string, string> parameters, DropManifestSummary manifest, IntakeCounts counts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(counts);
        var now = _time.GetUtcNow().UtcDateTime;
        if (flow.Change.UseSourceVersions && manifest.SourceVersions.Count > 0)
        {
            var scope = Planner.ScopeKey(parameters);
            await _ledger.SetWatermarksAsync(manifest.SourceVersions.Select(kv => new SourceWatermark(flow.Id, scope, kv.Key, kv.Value, now)), ct).ConfigureAwait(false);
        }

        var submission = await _ledger.GetSubmissionAsync(submissionId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Submission {submissionId} is not in the ledger.");
        submission = submission with
        {
            Status = counts.Planned == 0 ? SubmissionStatus.Completed : SubmissionStatus.Planned,
            StartedUtc = submission.StartedUtc ?? now,
            CompletedUtc = counts.Planned == 0 ? now : null,
            Planned = counts.Planned,
            SkippedUnchanged = counts.Skipped,
            Blocked = counts.Blocked,
            Held = counts.Held,
            BatchCount = counts.Batches,
            Delivered = 0,
            Failed = 0,
            Error = null,
        };
        await _ledger.UpdateSubmissionAsync(submission, ct).ConfigureAwait(false);
        _logger.LogInformation("Submission {SubmissionId}: {Counts}.", submission.SubmissionId, counts);
        await EmitAsync(flow, submission, counts.Planned == 0 ? "submission.completed" : "submission.planned", Summarize(submission), ct).ConfigureAwait(false);
        return submission;
    }

    /// <summary>Streams the plan into work batches and the ledger.</summary>
    private async Task<IntakeCounts> StageAsync(FlowDefinition flow, PlanHeader header, SubmissionState submission, string workRoot, IReadOnlyList<int>? partitions, CancellationToken ct)
    {
        var summary = new PlanSummary();
        var batchRecords = Math.Max(1, flow.Reliability.BatchRecords);
        var batchBase = partitions is { Count: > 0 } ? BatchBase(partitions[0]) : 0;
        var nextBatch = batchBase;
        var batches = 0;
        long staged = 0;

        WorkBatchWriter? writer = null;
        var pending = new List<RecordState>(batchRecords);
        var skipped = new List<DeliveryKey>(Planner.RenderBatch);
        var held = new List<RecordState>();
        var untrackedLogged = 0;
        var lastProgress = _time.GetUtcNow();

        try
        {
            await foreach (var entry in _planner.EntriesAsync(header, partitions, flow.Reliability.EffectiveRenderParallelism, summary, ct).ConfigureAwait(false))
            {
                if (entry.Key is null)
                {
                    if (untrackedLogged++ < 20)
                    {
                        _logger.LogWarning("Record {SourceKey} has no derivable delivery key and cannot be tracked: {Reason}", entry.SourceKey, entry.Reason);
                    }

                    continue;
                }

                switch (entry.Action)
                {
                    case PlannedAction.Skip:
                        skipped.Add(entry.Key.Value);
                        if (skipped.Count >= Planner.RenderBatch)
                        {
                            await _ledger.MarkSkippedAsync(flow.Id, skipped, submission.SubmissionId, ct).ConfigureAwait(false);
                            skipped.Clear();
                        }

                        break;

                    case PlannedAction.Blocked:
                        // Left untouched: its state and history belong to the submission that held it.
                        break;

                    case PlannedAction.Hold:
                        held.Add(HeldState(flow, submission, entry, resolvedMapping: header.Mapping));
                        if (held.Count >= Planner.RenderBatch)
                        {
                            await FlushHeldAsync(flow, submission, held, ct).ConfigureAwait(false);
                        }

                        break;

                    default:
                        if (!entry.IsDelivery)
                        {
                            break;
                        }

                        writer ??= await WorkBatchWriter.OpenAsync(_stores, workRoot, submission.SubmissionId, nextBatch, ct).ConfigureAwait(false);
                        var reference = await writer.WriteAsync(new WorkItem(entry.Key.Value.Value, entry.TargetId!, entry.Render!.Canonical), ct).ConfigureAwait(false);
                        pending.Add(PendingState(flow, submission, header.Mapping, entry, reference, nextBatch));
                        if (pending.Count >= batchRecords)
                        {
                            staged += await CloseBatchAsync(flow, submission, writer, pending, ct).ConfigureAwait(false);
                            writer = null;
                            batches++;
                            nextBatch++;
                        }

                        break;
                }

                var nowUtc = _time.GetUtcNow();
                if (nowUtc - lastProgress >= ProgressInterval)
                {
                    lastProgress = nowUtc;
                    _logger.LogInformation("Intake progress: {Summary}; {Batches} batch(es) written.", summary, batches);
                }
            }

            if (writer is not null)
            {
                staged += await CloseBatchAsync(flow, submission, writer, pending, ct).ConfigureAwait(false);
                writer = null;
                batches++;
            }
        }
        catch
        {
            if (writer is not null)
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }

        if (skipped.Count > 0)
        {
            await _ledger.MarkSkippedAsync(flow.Id, skipped, submission.SubmissionId, ct).ConfigureAwait(false);
        }

        if (held.Count > 0)
        {
            await FlushHeldAsync(flow, submission, held, ct).ConfigureAwait(false);
        }

        return new IntakeCounts(summary.Records, staged, summary.Skips, summary.Holds, summary.Blocked, summary.Untracked, batches);
    }

    /// <summary>Commits the batch file, stages its records in the ledger and registers the batch. Returns the records staged.</summary>
    private async Task<int> CloseBatchAsync(FlowDefinition flow, SubmissionState submission, WorkBatchWriter writer, List<RecordState> pending, CancellationToken ct)
    {
        await writer.DisposeAsync().ConfigureAwait(false);
        var staged = await _ledger.UpsertPendingAsync(pending, ct).ConfigureAwait(false);
        if (staged < pending.Count)
        {
            _logger.LogWarning("Batch {Batch}: {Skipped} record(s) are being delivered by another worker right now and were not re-planned; the next drop plans them again.", writer.Batch, pending.Count - staged);
        }

        await _ledger.AddWorkBatchAsync(new WorkBatchState
        {
            SubmissionId = submission.SubmissionId,
            FlowId = flow.Id,
            Index = writer.Batch,
            Location = writer.Path,
            RecordCount = staged,
            CreatedUtc = _time.GetUtcNow().UtcDateTime,
        }, ct).ConfigureAwait(false);
        pending.Clear();
        return staged;
    }

    private async Task FlushHeldAsync(FlowDefinition flow, SubmissionState submission, List<RecordState> held, CancellationToken ct)
    {
        await _ledger.MarkHeldAsync(held, ct).ConfigureAwait(false);
        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var record in held)
        {
            await _listener.OnEventAsync(new DeliveryEvent
            {
                AtUtc = now,
                FlowId = flow.Id,
                FlowName = flow.Name,
                Kind = "record.held",
                SubmissionId = submission.SubmissionId,
                DeliveryKey = record.DeliveryKey,
                SourceKey = record.SourceKey,
                Label = record.Label,
                TargetId = record.TargetId,
                Worker = "intake",
                Phase = "render",
                Detail = record.LastError,
            }, ct).ConfigureAwait(false);
        }

        held.Clear();
    }

    private RecordState HeldState(FlowDefinition flow, SubmissionState submission, PlanEntry entry, ResolvedMapping resolvedMapping) => new()
    {
        DeliveryKey = entry.Key!.Value,
        FlowId = flow.Id,
        SourceKey = entry.SourceKey,
        Label = entry.Label,
        MappingName = resolvedMapping.Mapping.Name,
        TargetId = entry.TargetId,
        LastSubmissionId = submission.SubmissionId,
        RunId = RunId,
        PendingSourceFingerprint = entry.SourceFingerprint,
        LastError = Http.HeaderRedaction.RedactMessage(entry.Reason),
    };

    private RecordState PendingState(FlowDefinition flow, SubmissionState submission, ResolvedMapping resolved, PlanEntry entry, DocumentRef reference, int batch) => new()
    {
        DeliveryKey = entry.Key!.Value,
        FlowId = flow.Id,
        SourceKey = entry.SourceKey,
        Label = entry.Label,
        MappingName = resolved.Mapping.Name,
        TargetId = entry.TargetId,
        LastSubmissionId = submission.SubmissionId,
        RunId = RunId,
        PendingDocumentRef = reference.ToString(),
        WorkBatch = batch,
        PendingRenderContext = resolved.Context.Canonical(),
        PendingSourceFingerprint = entry.SourceFingerprint,
        PendingMetadataHash = entry.Render!.MetadataHash,
        PendingPayloadHash = entry.PayloadHash,
        PendingPayloadLocation = entry.PayloadLocation,
        PendingMetadata = entry.DeliverMetadata,
        PendingPayload = entry.DeliverPayload,
    };

    /// <summary>Closes the submission after the drains finished, with honest counts scoped to the records it touched.</summary>
    public async Task<SubmissionState> CompleteAsync(Guid submissionId, Guid flowId, CancellationToken ct = default)
    {
        var submission = await _ledger.GetSubmissionAsync(submissionId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Submission {submissionId} is not in the ledger.");
        var delivered = await _ledger.CountAsync(flowId, submissionId, RecordStatus.Delivered, ct).ConfigureAwait(false);
        var held = await _ledger.CountAsync(flowId, submissionId, RecordStatus.Held, ct).ConfigureAwait(false);
        var failed = await _ledger.CountAsync(flowId, submissionId, RecordStatus.Failed, ct).ConfigureAwait(false);
        var stillPending = await _ledger.HasPendingAsync(flowId, submissionId, _time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        var now = _time.GetUtcNow().UtcDateTime;
        var wasClosed = submission.Status is SubmissionStatus.Completed or SubmissionStatus.Failed;
        submission = submission with
        {
            Delivered = Math.Max(0, delivered - submission.SkippedUnchanged),
            Held = held,
            Failed = failed,
            Status = stillPending ? SubmissionStatus.Running : (failed > 0 ? SubmissionStatus.Failed : SubmissionStatus.Completed),
            CompletedUtc = stillPending ? null : now,
        };
        await _ledger.UpdateSubmissionAsync(submission, ct).ConfigureAwait(false);
        if (!stillPending && !wasClosed)
        {
            await EmitAsync(null, submission, "submission.completed", Summarize(submission), ct).ConfigureAwait(false);
        }

        return submission;
    }

    public static string Summarize(SubmissionState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return string.Create(CultureInfo.InvariantCulture, $"{s.Status.ToString().ToLowerInvariant()}: {s.Planned} planned in {s.BatchCount} batch(es), {s.Delivered} delivered, {s.SkippedUnchanged} unchanged, {s.Blocked} blocked, {s.Held} held, {s.Failed} failed");
    }

    /// <summary>The batch numbers a partition subset writes: a namespace per first partition, so fan-out members never collide.</summary>
    public static int BatchBase(int firstPartition)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(firstPartition);
        if (firstPartition > MaxFanOutPartition)
        {
            throw new DeliveryException($"A fan-out intake covers at most {MaxFanOutPartition + 1} partitions; partition {firstPartition} is out of range.");
        }

        return firstPartition * BatchesPerPartition;
    }

    /// <summary>Batch numbers per fan-out partition namespace.</summary>
    public const int BatchesPerPartition = 1_000_000;

    /// <summary>The highest partition index a fan-out intake can namespace within a 32-bit batch number.</summary>
    public const int MaxFanOutPartition = (int.MaxValue / BatchesPerPartition) - 1;

    public static string DescribePartitions(IReadOnlyList<int> partitions)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        return partitions.Count <= 8
            ? string.Join(",", partitions.Select(p => p.ToString(CultureInfo.InvariantCulture)))
            : string.Create(CultureInfo.InvariantCulture, $"{partitions[0]}..{partitions[^1]} ({partitions.Count})");
    }

    private ValueTask EmitAsync(FlowDefinition? flow, SubmissionState submission, string kind, string? detail, CancellationToken ct)
        => _listener.OnEventAsync(new DeliveryEvent
        {
            AtUtc = _time.GetUtcNow().UtcDateTime,
            FlowId = submission.FlowId,
            FlowName = flow?.Name ?? submission.FlowName,
            Kind = kind,
            SubmissionId = submission.SubmissionId,
            Worker = "intake",
            Detail = detail,
        }, ct);
}

/// <summary>The part of a manifest the planning finalisation needs: the source versions for the tier-0 watermarks.</summary>
public sealed record DropManifestSummary(IReadOnlyDictionary<string, long> SourceVersions)
{
    public static DropManifestSummary Of(Drops.DropManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return new DropManifestSummary(manifest.SourceVersions);
    }
}
