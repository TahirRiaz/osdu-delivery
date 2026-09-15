using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Planning;
using SqlFlow.Delivery.Replica;
using SqlFlow.Delivery.Storage;

namespace SqlFlow.Delivery.Engine.Intake;

/// <summary>
/// What one intake pass produced: the counts of what it planned and the batches it wrote. <c>Stale</c> counts the
/// records the drop carried in a version older than the ledger holds, delivered or queued, which are never sent.
/// </summary>
public sealed record IntakeCounts(long Records, long Planned, long Skipped, long Held, long Blocked, long Untracked, int Batches, long Stale = 0, long AwaitingApproval = 0)
{
    public static IntakeCounts Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);

    /// <summary>Records a load found without a derivable delivery key: counted, never planned.</summary>
    public static IntakeCounts OfUntracked(long untracked) => new(untracked, 0, 0, 0, 0, untracked, 0);

    public IntakeCounts Add(IntakeCounts other) => new(
        Records + other.Records, Planned + other.Planned, Skipped + other.Skipped, Held + other.Held, Blocked + other.Blocked, Untracked + other.Untracked, Batches + other.Batches, Stale + other.Stale, AwaitingApproval + other.AwaitingApproval);

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{Records} record(s): {Planned} to deliver in {Batches} batch(es), {Skipped} unchanged, {AwaitingApproval} awaiting approval, {Stale} stale, {Held} held, {Blocked} blocked, {Untracked} untracked");
}

public sealed record IntakeResult(SubmissionState Submission, PlanHeader? Header, IntakeCounts Counts, bool AlreadyProcessed)
{
    public bool NothingToDo => AlreadyProcessed || Header is null || Header.SkippedWholeRun || Counts.Planned == 0;
}

/// <summary>What an intake works on: the drop at a location, and the submission a run was asked to work on, when it was.</summary>
public sealed record IntakeSource(string DropLocation, Guid? SubmissionId = null);

/// <summary>
/// A submission ready to plan from the flow's replica: registered, loaded, and opened. <see cref="Done"/> is set instead when
/// there is nothing to plan (the submission was already completed, or the tier-0 gate skipped it).
/// </summary>
public sealed record PreparedIntake(SubmissionState Submission, PlanHeader Header, string WorkRoot, IntakeResult? Done, DropManifestSummary Summary);

/// <summary>
/// Turns a manifest notification into ledger state (design.md sections 3.3, 7.2 and 16.2): registers the submission under its
/// idempotency key, plans its records, writes the rendered documents to work batches at the flow's work location, and stages
/// the pending work per record in the ledger, batch by batch. A flow with a replica (docs/delivery/replica.md) loads the drop's
/// metadata rows into it first and plans from the replica, so a re-run, a fan-out and a replan never read the drop again; a
/// flow without one plans straight from the drop. Re-running an intake for a completed submission does nothing unless forced;
/// re-running one that was interrupted picks up where it stopped. An intake can be restricted to a share of the work (slices
/// of a replica submission, or root partitions of a drop), which is how a fan-out spreads it across nodes.
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

    /// <summary>The flow's replica, when it declares one; null plans straight from drops.</summary>
    public ReplicaStore? Replica { get; init; }

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

    /// <summary>Registers and plans the drop at a location; see <see cref="IntakeAsync(FlowDefinition, ResolvedMapping, IReadOnlyDictionary{string, string}, IntakeSource, bool, IReadOnlyList{int}?, CancellationToken)"/>.</summary>
    public Task<IntakeResult> IntakeAsync(
        FlowDefinition flow,
        ResolvedMapping resolved,
        IReadOnlyDictionary<string, string> parameters,
        string dropLocation,
        bool force,
        IReadOnlyList<int>? partitions = null,
        CancellationToken ct = default)
        => IntakeAsync(flow, resolved, parameters, new IntakeSource(dropLocation), force, partitions, ct);

    /// <summary>
    /// Registers and plans a submission. With <paramref name="shares"/> null the whole submission is planned here and closed with
    /// its counts (or left planned for the drain); with a share (slice indexes of a replica submission, root partition indexes of a
    /// drop) only that share is planned and the submission is left for the coordinating run to finalise.
    /// </summary>
    public async Task<IntakeResult> IntakeAsync(
        FlowDefinition flow,
        ResolvedMapping resolved,
        IReadOnlyDictionary<string, string> parameters,
        IntakeSource source,
        bool force,
        IReadOnlyList<int>? shares = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(source);

        if (Replica is null)
        {
            return await IntakeDropAsync(flow, resolved, parameters, source.DropLocation, force, shares, ct).ConfigureAwait(false);
        }

        if (shares is not null)
        {
            return await IntakeSlicesAsync(flow, resolved, parameters, source, shares, ct).ConfigureAwait(false);
        }

        var prepared = await PrepareAsync(flow, resolved, parameters, source, force, ct).ConfigureAwait(false);
        return prepared.Done ?? await PlanPreparedAsync(flow, parameters, prepared, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a submission of a flow with a replica for planning: registers the drop's submission, or opens the submission the run
    /// names (a loaded one, or a replan) from the replica without reading its drop, then loads the drop into the replica unless
    /// the submission is already loaded. Returns early with <see cref="PreparedIntake.Done"/> when there is nothing to plan.
    /// </summary>
    public async Task<PreparedIntake> PrepareAsync(
        FlowDefinition flow, ResolvedMapping resolved, IReadOnlyDictionary<string, string> parameters, IntakeSource source, bool force, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(source);
        var replica = Replica ?? throw new DeliveryException($"Flow '{flow.Name}' declares no source.replica; its drops are planned directly.");
        var now = _time.GetUtcNow().UtcDateTime;

        var existing = source.SubmissionId is { } named ? await _ledger.GetSubmissionAsync(named, ct).ConfigureAwait(false) : null;
        if (existing is not null && existing.FlowId != flow.Id)
        {
            throw new DeliveryException($"Submission {existing.SubmissionId:D} belongs to flow '{existing.FlowName}', not '{flow.Name}'.");
        }

        PlanHeader header;
        SubmissionState submission;
        var created = false;
        if (existing is { IsReplan: true } or { IsLoaded: true })
        {
            header = await _planner.OpenStoredAsync(flow, resolved, parameters, existing, gate: !force && !existing.IsReplan, ct).ConfigureAwait(false);
            submission = existing;
        }
        else
        {
            header = await _planner.OpenAsync(flow, resolved, parameters, source.DropLocation, force, ct).ConfigureAwait(false);
            var manifest = header.Drop!.Manifest;
            if (source.SubmissionId is { } wanted && manifest.SubmissionId != wanted)
            {
                throw new DeliveryException(
                    $"Submission {wanted:D} is not loaded in the flow's replica, and the drop at {source.DropLocation} carries submission {manifest.SubmissionId:D}, not it. Run the drop as it is, or restore the submission's drop.");
            }

            (submission, created) = await _ledger.RegisterSubmissionAsync(new SubmissionState
            {
                SubmissionId = manifest.SubmissionId,
                FlowId = flow.Id,
                FlowName = flow.Name,
                MappingReference = resolved.Mapping.Reference,
                RenderContext = resolved.Context.Canonical(),
                DropLocation = header.Drop.Location,
                Partitions = manifest.PartitionCount,
                ParametersJson = JsonSerializer.Serialize(parameters),
                Reference = manifest.Reference,
                RecordCount = manifest.RecordCount,
                ReceivedUtc = now,
                ManifestJson = manifest.ToJson(),
            }, ct).ConfigureAwait(false);
        }

        var workRoot = FlowParameters.WorkLocation(flow, parameters, header.Drop?.Location ?? source.DropLocation);
        if (!created && submission.Status == SubmissionStatus.Completed && !force)
        {
            _logger.LogInformation("Submission {SubmissionId} was already completed at {CompletedUtc}; nothing to do (use --force to re-plan).", submission.SubmissionId, submission.CompletedUtc);
            return new PreparedIntake(submission, header, workRoot, new IntakeResult(submission, header, IntakeCounts.Empty, AlreadyProcessed: true), DropManifestSummary.Empty);
        }

        if (!created)
        {
            _logger.LogInformation("Submission {SubmissionId} exists with status {Status}; re-planning against the current ledger.", submission.SubmissionId, submission.Status);
        }

        if (header.SkippedWholeRun)
        {
            submission = submission with { Status = SubmissionStatus.Completed, StartedUtc = now, CompletedUtc = now, Error = null };
            await _ledger.UpdateSubmissionAsync(submission, ct).ConfigureAwait(false);
            await EmitAsync(flow, submission, "submission.completed", header.SkipReason, ct).ConfigureAwait(false);
            return new PreparedIntake(submission, header, workRoot, new IntakeResult(submission, header, IntakeCounts.Empty, AlreadyProcessed: false), DropManifestSummary.Empty);
        }

        submission = submission with { Status = SubmissionStatus.Received, StartedUtc = now, Error = null, WorkLocation = workRoot, Partitions = header.Partitions };
        await _ledger.UpdateSubmissionAsync(submission, ct).ConfigureAwait(false);
        if (!submission.IsReplan && !submission.IsLoaded)
        {
            submission = await replica.LoadAsync(resolved, parameters, header.Drop!, submission, ct).ConfigureAwait(false);
        }

        var summary = header.Drop is null ? DropManifestSummary.Empty : DropManifestSummary.Of(header.Drop.Manifest);
        return new PreparedIntake(submission, header, workRoot, null, summary);
    }

    /// <summary>Plans a whole prepared submission from the replica and closes its planning phase.</summary>
    public async Task<IntakeResult> PlanPreparedAsync(FlowDefinition flow, IReadOnlyDictionary<string, string> parameters, PreparedIntake prepared, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(prepared);
        var counts = (await PlanSlicesAsync(flow, prepared, null, ct).ConfigureAwait(false)).Add(IntakeCounts.OfUntracked(prepared.Submission.Untracked));
        var submission = await FinalizePlanningAsync(flow, prepared.Submission.SubmissionId, parameters, prepared.Summary, counts, ct).ConfigureAwait(false);
        return new IntakeResult(submission, prepared.Header, counts, AlreadyProcessed: false);
    }

    /// <summary>Plans slices of a prepared submission from the replica (all of it when <paramref name="slices"/> is null) into work batches.</summary>
    public Task<IntakeCounts> PlanSlicesAsync(FlowDefinition flow, PreparedIntake prepared, IReadOnlyList<int>? slices, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(prepared);
        var replica = Replica ?? throw new DeliveryException($"Flow '{flow.Name}' declares no source.replica.");
        var submission = prepared.Submission;
        var inputs = slices is null
            ? replica.ReadSubmissionAsync(submission.SubmissionId, 0, long.MaxValue, ct)
            : SliceInputsAsync(replica, submission, flow, slices, ct);
        var batchBase = slices is { Count: > 0 } ? BatchBase(slices[0]) : 0;
        return StageAsync(flow, prepared.Header, submission, prepared.WorkRoot, inputs, batchBase, ct);
    }

    /// <summary>A fan-out member's share of a replica submission its coordinating run loaded.</summary>
    private async Task<IntakeResult> IntakeSlicesAsync(
        FlowDefinition flow, ResolvedMapping resolved, IReadOnlyDictionary<string, string> parameters, IntakeSource source, IReadOnlyList<int> slices, CancellationToken ct)
    {
        var id = source.SubmissionId
            ?? throw new DeliveryException("An intake of slices works on a submission its coordinating run loaded into the replica; the run names no submission.");
        var submission = await _ledger.GetSubmissionAsync(id, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Submission {id:D} is not in the ledger.");
        if (submission.FlowId != flow.Id)
        {
            throw new DeliveryException($"Submission {id:D} belongs to flow '{submission.FlowName}', not '{flow.Name}'.");
        }

        if (!submission.IsLoaded)
        {
            throw new DeliveryException($"Submission {id:D} is not loaded in the flow's replica; its coordinating run loads it before it hands out slices.");
        }

        var header = await _planner.OpenStoredAsync(flow, resolved, parameters, submission, gate: false, ct).ConfigureAwait(false);
        var workRoot = submission.WorkLocation ?? FlowParameters.WorkLocation(flow, parameters, header.Drop?.Location ?? source.DropLocation);
        var counts = await PlanSlicesAsync(flow, new PreparedIntake(submission, header, workRoot, null, DropManifestSummary.Empty), slices, ct).ConfigureAwait(false);
        _logger.LogInformation("Intake of slices {Slices}: {Counts}.", DescribePartitions(slices), counts);
        return new IntakeResult(submission, header, counts, AlreadyProcessed: false);
    }

    /// <summary>The records of the given slices, read range by range; consecutive slices are read as one range.</summary>
    private static async IAsyncEnumerable<PlanInput> SliceInputsAsync(
        ReplicaStore replica, SubmissionState submission, FlowDefinition flow, IReadOnlyList<int> slices, [EnumeratorCancellation] CancellationToken ct)
    {
        var ranges = new List<(long From, long To)>();
        foreach (var slice in slices.Distinct().Order())
        {
            var (from, to) = SourceSlices.Range(slice, submission.LoadedOrdinals, flow.Reliability.BatchRecords);
            if (ranges.Count > 0 && ranges[^1].To == from)
            {
                ranges[^1] = (ranges[^1].From, to);
            }
            else
            {
                ranges.Add((from, to));
            }
        }

        foreach (var (from, to) in ranges)
        {
            await foreach (var input in replica.ReadSubmissionAsync(submission.SubmissionId, from, to, ct).ConfigureAwait(false))
            {
                yield return input;
            }
        }
    }

    /// <summary>Registers and plans a drop straight from storage: the path of a flow without a replica.</summary>
    private async Task<IntakeResult> IntakeDropAsync(
        FlowDefinition flow,
        ResolvedMapping resolved,
        IReadOnlyDictionary<string, string> parameters,
        string dropLocation,
        bool force,
        IReadOnlyList<int>? partitions,
        CancellationToken ct)
    {
        var header = await _planner.OpenAsync(flow, resolved, parameters, dropLocation, force, ct).ConfigureAwait(false);
        var drop = header.Drop!;
        var manifest = drop.Manifest;
        var now = _time.GetUtcNow().UtcDateTime;
        var workRoot = FlowParameters.WorkLocation(flow, parameters, drop.Location);

        var (submission, created) = await _ledger.RegisterSubmissionAsync(new SubmissionState
        {
            SubmissionId = manifest.SubmissionId,
            FlowId = flow.Id,
            FlowName = flow.Name,
            MappingReference = resolved.Mapping.Reference,
            RenderContext = resolved.Context.Canonical(),
            DropLocation = drop.Location,
            WorkLocation = workRoot,
            Partitions = manifest.PartitionCount,
            ParametersJson = JsonSerializer.Serialize(parameters),
            Reference = manifest.Reference,
            RecordCount = manifest.RecordCount,
            ReceivedUtc = now,
            ManifestJson = manifest.ToJson(),
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

        var batchBase = partitions is { Count: > 0 } ? BatchBase(partitions[0]) : 0;
        var counts = await StageAsync(flow, header, submission, workRoot, _planner.DropInputsAsync(header, partitions, ct), batchBase, ct).ConfigureAwait(false);

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
        var submission = await _ledger.GetSubmissionAsync(submissionId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Submission {submissionId} is not in the ledger.");
        if (flow.Change.UseSourceVersions && manifest.SourceVersions.Count > 0)
        {
            // The watermark carries the render context this scope was planned under, so the next run's tier-0 gate
            // can tell "nothing changed" from "the source is the same but the cache, mapping or schema moved".
            var scope = Planner.ScopeKey(parameters);
            var contextHash = Hashing.ContentHash.Of(submission.RenderContext);
            await _ledger.SetWatermarksAsync(
                manifest.SourceVersions.Select(kv => new SourceWatermark(flow.Id, scope, kv.Key, kv.Value, now, contextHash)), ct).ConfigureAwait(false);
        }

        submission = submission with
        {
            Status = counts.Planned == 0 ? SubmissionStatus.Completed : SubmissionStatus.Planned,
            StartedUtc = submission.StartedUtc ?? now,
            CompletedUtc = counts.Planned == 0 ? now : null,
            Planned = counts.Planned,
            SkippedUnchanged = counts.Skipped,
            AwaitingApproval = counts.AwaitingApproval,
            SkippedStale = counts.Stale,
            UnchangedAtPush = 0,
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

    /// <summary>Streams the plan of the given inputs into work batches and the ledger.</summary>
    private async Task<IntakeCounts> StageAsync(
        FlowDefinition flow, PlanHeader header, SubmissionState submission, string workRoot, IAsyncEnumerable<PlanInput> inputs, int batchBase, CancellationToken ct)
    {
        var summary = new PlanSummary();
        var batchRecords = Math.Max(1, flow.Reliability.BatchRecords);
        var nextBatch = batchBase;
        var batches = 0;
        long staged = 0;

        WorkBatchWriter? writer = null;
        var pending = new List<RecordState>(batchRecords);
        var skipped = new List<SkippedRecord>(Planner.RenderBatch);
        var context = header.Mapping.Context.Canonical();
        long refused = 0;
        var held = new List<RecordState>();
        var untrackedLogged = 0;
        var lastProgress = _time.GetUtcNow();

        try
        {
            await foreach (var entry in _planner.EntriesOfAsync(header, inputs, flow.Reliability.EffectiveRenderParallelism, summary, ct).ConfigureAwait(false))
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
                        skipped.Add(Skipped(entry, context));
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
                        // The values this document was built from become one set id on the record: no row per
                        // dependency, and the id is resolved once per distinct combination for the whole run.
                        var cacheSet = entry.Render!.CacheUsages.Count == 0
                            ? (long?)null
                            : await _ledger.EnsureCacheSetAsync(
                                header.Mapping.Context.CacheScope
                                    ?? throw new DeliveryException($"Mapping {header.Mapping.Mapping.Reference} read cached values under a render context that names no cache partition; the render resolver names the partition whenever a mapping reads the cache."),
                                entry.Render.CacheUsages,
                                ct).ConfigureAwait(false);
                        pending.Add(PendingState(flow, submission, header.Mapping, entry, reference, nextBatch) with { CacheSetId = cacheSet });
                        if (pending.Count >= batchRecords)
                        {
                            var closed = await CloseBatchAsync(flow, submission, writer, pending, skipped, ct).ConfigureAwait(false);
                            staged += closed.Staged;
                            refused += closed.Refused.Count;
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
                var closed = await CloseBatchAsync(flow, submission, writer, pending, skipped, ct).ConfigureAwait(false);
                staged += closed.Staged;
                refused += closed.Refused.Count;
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

        return new IntakeCounts(summary.Records, staged, summary.Skips, summary.Holds, summary.Blocked, summary.Untracked, batches, summary.Stale + refused, summary.AwaitingApproval);
    }

    /// <summary>What the ledger is told about one skipped plan entry.</summary>
    private SkippedRecord Skipped(PlanEntry entry, string renderContext) => new()
    {
        DeliveryKey = entry.Key!.Value,
        Kind = entry.SkipTier switch
        {
            SkipTier.Stale => SkipKind.Stale,
            SkipTier.ContentHash => SkipKind.Rendered,
            _ => SkipKind.Unchanged,
        },
        Reason = entry.Reason,
        SourceFingerprint = entry.SourceFingerprint,
        SourceModifiedUtc = entry.SourceModifiedUtc,
        PayloadModifiedUtc = entry.PayloadModifiedUtc,
        RenderContext = entry.SkipTier == SkipTier.ContentHash ? renderContext : null,
        RunId = RunId,
    };

    /// <summary>
    /// Commits the batch file, stages its records in the ledger and registers the batch. Records the ledger refuses
    /// because a newer version landed or was queued since they were planned go to <paramref name="stale"/>, to be
    /// recorded like any other stale skip.
    /// </summary>
    private async Task<PendingStaging> CloseBatchAsync(
        FlowDefinition flow, SubmissionState submission, WorkBatchWriter writer, List<RecordState> pending, List<SkippedRecord> stale, CancellationToken ct)
    {
        await writer.DisposeAsync().ConfigureAwait(false);
        var staging = await _ledger.UpsertPendingAsync(pending, ct).ConfigureAwait(false);
        if (staging.Refused.Count > 0)
        {
            var refused = staging.Refused.ToHashSet();
            foreach (var record in pending.Where(p => refused.Contains(p.DeliveryKey)))
            {
                var carried = record.PendingSourceModifiedUtc is { } modified
                    ? string.Create(CultureInfo.InvariantCulture, $" (this drop carried the row as last modified {modified:yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'})")
                    : string.Empty;
                stale.Add(new SkippedRecord
                {
                    DeliveryKey = record.DeliveryKey,
                    Kind = SkipKind.Stale,
                    Reason = $"a newer version of the record was delivered or queued while this drop was being planned{carried}; OSDU keeps the newer version",
                    SourceFingerprint = record.PendingSourceFingerprint,
                    SourceModifiedUtc = record.PendingSourceModifiedUtc,
                    PayloadModifiedUtc = record.PendingPayloadModifiedUtc,
                    RunId = RunId,
                });
            }

            _logger.LogWarning("Batch {Batch}: {Refused} record(s) were not staged because a newer version was delivered or queued while this drop was planned; they are recorded as stale.", writer.Batch, staging.Refused.Count);
        }

        await _ledger.AddWorkBatchAsync(new WorkBatchState
        {
            SubmissionId = submission.SubmissionId,
            FlowId = flow.Id,
            Index = writer.Batch,
            Location = writer.Path,
            RecordCount = staging.Staged,
            CreatedUtc = _time.GetUtcNow().UtcDateTime,
        }, ct).ConfigureAwait(false);
        pending.Clear();
        return staging;
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
        PendingSourceModifiedUtc = entry.SourceModifiedUtc,
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
        PendingSourceModifiedUtc = entry.SourceModifiedUtc,
        PendingMetadataHash = entry.Render!.MetadataHash,
        PendingPayloadHash = entry.PayloadHash,
        PendingPayloadModifiedUtc = entry.DeliverPayload ? entry.PayloadModifiedUtc : null,
        PendingPayloadLocation = entry.PayloadLocation,
        PendingMetadata = entry.DeliverMetadata,
        PendingPayload = entry.DeliverPayload,
    };

    /// <summary>Closes the submission after the drains finished, with honest counts scoped to the records it touched.</summary>
    public async Task<SubmissionState> CompleteAsync(Guid submissionId, Guid flowId, CancellationToken ct = default)
    {
        var submission = await _ledger.GetSubmissionAsync(submissionId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Submission {submissionId} is not in the ledger.");
        // Deliveries are counted from the attempts the submission wrote, not from where record pointers now stand: a
        // record can move on to a newer submission while its delivery for this one is still in flight.
        var delivered = await _ledger.CountAttemptsAsync(submissionId, AttemptOutcome.Delivered, null, ct).ConfigureAwait(false);
        var unchangedAtPush = await _ledger.CountAttemptsAsync(submissionId, AttemptOutcome.Skipped, AttemptPhases.Unchanged, ct).ConfigureAwait(false);
        var held = await _ledger.CountAsync(flowId, submissionId, RecordStatus.Held, ct).ConfigureAwait(false);
        var failed = await _ledger.CountAsync(flowId, submissionId, RecordStatus.Failed, ct).ConfigureAwait(false);
        var stillPending = await _ledger.HasPendingAsync(flowId, submissionId, _time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        var now = _time.GetUtcNow().UtcDateTime;
        var wasClosed = submission.Status is SubmissionStatus.Completed or SubmissionStatus.Failed;
        submission = submission with
        {
            Delivered = delivered,
            UnchangedAtPush = unchangedAtPush,
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
        return string.Create(CultureInfo.InvariantCulture, $"{s.Status.ToString().ToLowerInvariant()}: {s.Planned} planned in {s.BatchCount} batch(es), {s.Delivered} delivered, {s.SkippedUnchanged + s.UnchangedAtPush} unchanged, {s.AwaitingApproval} awaiting approval, {s.SkippedStale} stale, {s.Blocked} blocked, {s.Held} held, {s.Failed} failed");
    }

    /// <summary>The batch numbers a share writes: a namespace per first slice or partition, so fan-out members never collide.</summary>
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
    /// <summary>No source versions: a replan, or a submission whose watermarks an earlier run recorded.</summary>
    public static DropManifestSummary Empty { get; } = new(new Dictionary<string, long>(StringComparer.Ordinal));

    public static DropManifestSummary Of(Drops.DropManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return new DropManifestSummary(manifest.SourceVersions);
    }
}
