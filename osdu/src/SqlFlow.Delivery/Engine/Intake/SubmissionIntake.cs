using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Planning;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Storage;

namespace SqlFlow.Delivery.Engine.Intake;

/// <summary>
/// What one intake pass produced: the counts of what it planned and the batches it wrote. <c>Stale</c> counts the
/// records the source carried in a version older than the ledger holds, delivered or queued, which are never sent.
/// </summary>
public sealed record IntakeCounts(long Records, long Planned, long Skipped, long Held, long Blocked, long Untracked, int Batches, long Stale = 0, long AwaitingApproval = 0)
{
    public static IntakeCounts Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);

    public IntakeCounts Add(IntakeCounts other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new IntakeCounts(
            Records + other.Records, Planned + other.Planned, Skipped + other.Skipped, Held + other.Held, Blocked + other.Blocked,
            Untracked + other.Untracked, Batches + other.Batches, Stale + other.Stale, AwaitingApproval + other.AwaitingApproval);
    }

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{Records} record(s): {Planned} to deliver in {Batches} batch(es), {Skipped} unchanged, {AwaitingApproval} awaiting approval, {Stale} stale, {Held} held, {Blocked} blocked, {Untracked} untracked");
}

public sealed record IntakeResult(SubmissionState Submission, PlanHeader? Header, IntakeCounts Counts, bool AlreadyProcessed)
{
    public bool NothingToDo => AlreadyProcessed || Header is null || Header.SkippedWholeRun || Counts.Planned == 0;
}

/// <summary>
/// What an intake works on: which records to read, the submission it belongs to when a run was given one (a re-run, or a
/// fan-out member's share), and the key slices a member plans of it.
/// </summary>
public sealed record IntakeRequest(SourceSelection Selection, Guid? SubmissionId = null, IReadOnlyList<int>? Slices = null);

/// <summary>
/// A submission ready to plan: registered, its read opened, and the window it covers recorded. <see cref="Done"/> is set
/// instead when there is nothing to plan (the submission was already completed, or the tier-0 gate skipped the run).
/// </summary>
public sealed record PreparedIntake(SubmissionState Submission, PlanHeader Header, string WorkRoot, IntakeResult? Done, SourceWindowDescription? Window = null);

/// <summary>
/// Turns one read of the ingestion tables into ledger state (docs/stage4-design.md sections 2 and 3): registers the
/// submission, plans its records, writes the rendered documents to work batches at the flow's work location, and stages
/// the pending work per record in the ledger, batch by batch. Re-running an intake for a completed submission does
/// nothing unless forced; re-running one that was interrupted picks up where it stopped. An intake can be restricted to
/// a share of the key slices its coordinating run cut, which is how a fan-out spreads it across nodes.
/// </summary>
public sealed class SubmissionIntake
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(15);

    /// <summary>Batch numbers per fan-out slice namespace.</summary>
    public const int BatchesPerPartition = 1_000_000;

    /// <summary>The highest slice index a fan-out intake can namespace within a 32-bit batch number.</summary>
    public const int MaxFanOutPartition = (int.MaxValue / BatchesPerPartition) - 1;

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
    /// Registers and plans one read. With <paramref name="request"/> naming no slices the whole read is planned here and
    /// the submission is closed with its counts; with slices only that share is planned, and the coordinating run
    /// finalises the submission.
    /// </summary>
    public async Task<IntakeResult> IntakeAsync(
        FlowDefinition flow,
        ResolvedMapping resolved,
        IReadOnlyDictionary<string, string> parameters,
        IntakeRequest request,
        bool force,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(request);

        var prepared = await PrepareAsync(flow, resolved, parameters, request, force, ct).ConfigureAwait(false);
        if (prepared.Done is { } done)
        {
            return done;
        }

        var counts = await PlanSlicesAsync(flow, prepared, request.Slices, ct).ConfigureAwait(false);
        if (request.Slices is { Count: > 0 } slices)
        {
            _logger.LogInformation("Intake of slices {Slices}: {Counts}.", KeySlices.Describe(slices), counts);
            return new IntakeResult(prepared.Submission, prepared.Header, counts, AlreadyProcessed: false);
        }

        var submission = await FinalizePlanningAsync(flow, prepared.Submission.SubmissionId, parameters, counts, ct).ConfigureAwait(false);
        return new IntakeResult(submission, prepared.Header, counts, AlreadyProcessed: false);
    }

    /// <summary>
    /// Opens the read and registers (or reopens) the submission it belongs to. A run that names a submission reopens the
    /// window that submission recorded, so a member, a re-run and a drain all read the rows the coordinating run read.
    /// Returns early with <see cref="PreparedIntake.Done"/> when there is nothing to plan.
    /// </summary>
    public async Task<PreparedIntake> PrepareAsync(
        FlowDefinition flow, ResolvedMapping resolved, IReadOnlyDictionary<string, string> parameters, IntakeRequest request, bool force, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(request);
        var now = _time.GetUtcNow().UtcDateTime;
        var workRoot = FlowParameters.WorkLocation(flow, parameters);

        var existing = request.SubmissionId is { } named
            ? await _ledger.GetSubmissionAsync(named, ct).ConfigureAwait(false)
                ?? throw new DeliveryException($"Submission {named:D} is not in the ledger.")
            : null;
        if (existing is not null && existing.FlowId != flow.Id)
        {
            throw new DeliveryException($"Submission {existing.SubmissionId:D} belongs to flow '{existing.FlowName}', not '{flow.Label}'.");
        }

        var described = existing is null ? null : SourceWindowDescription.Parse(existing.SourceWindowJson);
        var selection = described is null ? request.Selection : described.ToSelection();
        var header = await _planner.OpenAsync(
            flow, resolved, parameters, selection, gate: !force && existing is null, stored: described?.Window(), ct).ConfigureAwait(false);

        SubmissionState submission;
        var created = false;
        if (existing is not null)
        {
            submission = existing;
        }
        else
        {
            (submission, created) = await _ledger.RegisterSubmissionAsync(new SubmissionState
            {
                SubmissionId = Guid.CreateVersion7(),
                FlowId = flow.Id,
                FlowName = flow.Label,
                MappingReference = resolved.Mapping.Reference,
                RenderContext = resolved.Context.Canonical(),
                ParametersJson = JsonSerializer.Serialize(parameters),
                Kind = SourceWindowDescription.Name(selection.Kind),
                SourceConnection = flow.Source.Connection,
                SourceObject = flow.Source.Record.Object,
                WindowFromUtc = header.Source.Window.LowerUtc,
                WindowToUtc = header.Source.Window.UpperUtc,
                SourceWindowJson = SourceWindowDescription.Of(header.Source, []).ToJson(),
                RecordCount = header.Source.EstimatedCandidates,
                WorkLocation = workRoot,
                Slices = 1,
                RunId = RunId,
                ReceivedUtc = now,
            }, ct).ConfigureAwait(false);
        }

        if (!created && submission.Status == SubmissionStatus.Completed && !force && request.Slices is null)
        {
            _logger.LogInformation("Submission {SubmissionId} was already completed at {CompletedUtc}; nothing to do (force re-plans it).", submission.SubmissionId, submission.CompletedUtc);
            return new PreparedIntake(submission, header, workRoot, new IntakeResult(submission, header, IntakeCounts.Empty, AlreadyProcessed: true), described);
        }

        if (!created && request.Slices is null)
        {
            _logger.LogInformation("Submission {SubmissionId} exists with status {Status}; re-planning against the current ledger.", submission.SubmissionId, submission.Status);
        }

        if (header.SkippedWholeRun)
        {
            // The scope did not move, so the plan is complete as it stands. The watermark still advances, which is what
            // keeps the next run's window from re-reading the same quiet stretch.
            submission = submission with { Status = SubmissionStatus.Completed, StartedUtc = now, CompletedUtc = now, Error = null };
            await _ledger.UpdateSubmissionAsync(submission, ct).ConfigureAwait(false);
            await WriteWatermarkAsync(flow, submission, parameters, ct).ConfigureAwait(false);
            await EmitAsync(flow, submission, "submission.completed", header.SkipReason, ct).ConfigureAwait(false);
            return new PreparedIntake(submission, header, workRoot, new IntakeResult(submission, header, IntakeCounts.Empty, AlreadyProcessed: false), described);
        }

        if (request.Slices is null)
        {
            submission = submission with { Status = SubmissionStatus.Received, StartedUtc = submission.StartedUtc ?? now, Error = null, WorkLocation = workRoot };
            await _ledger.UpdateSubmissionAsync(submission, ct).ConfigureAwait(false);
        }

        return new PreparedIntake(submission, header, workRoot, null, described);
    }

    /// <summary>
    /// Records the key slices a coordinating run cut its read into, so its members plan exactly the ranges it dealt out
    /// and no record falls between two of them.
    /// </summary>
    public async Task<PreparedIntake> RecordSlicesAsync(PreparedIntake prepared, IReadOnlyList<KeyRange> ranges, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(ranges);
        var description = SourceWindowDescription.Of(prepared.Header.Source, ranges);
        var submission = prepared.Submission with { Slices = ranges.Count, SourceWindowJson = description.ToJson() };
        await _ledger.UpdateSubmissionAsync(submission, ct).ConfigureAwait(false);
        return prepared with { Submission = submission, Window = description, Header = prepared.Header with { Slices = ranges.Count } };
    }

    /// <summary>Plans the whole read (<paramref name="slices"/> null) or the named key slices of it into work batches.</summary>
    public async Task<IntakeCounts> PlanSlicesAsync(FlowDefinition flow, PreparedIntake prepared, IReadOnlyList<int>? slices, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(prepared);
        if (slices is null or { Count: 0 })
        {
            return await StageAsync(flow, prepared, null, 0, ct).ConfigureAwait(false);
        }

        var description = prepared.Window
            ?? throw new DeliveryException(
                $"Submission {prepared.Submission.SubmissionId:D} records no key slices, so slice {slices[0].ToString(CultureInfo.InvariantCulture)} cannot be planned; its coordinating run cuts the slices before it hands them out.");

        var counts = IntakeCounts.Empty;
        foreach (var slice in slices.Distinct().Order())
        {
            var range = description.Range(slice)
                ?? throw new DeliveryException($"Submission {prepared.Submission.SubmissionId:D} was cut into {description.Slices.Count} slice(s); slice {slice.ToString(CultureInfo.InvariantCulture)} is not one of them.");
            counts = counts.Add(await StageAsync(flow, prepared, range, BatchBase(slice), ct).ConfigureAwait(false));
        }

        return counts;
    }

    /// <summary>
    /// Closes the planning phase of a submission with the totals of every intake pass (one, or one per fan-out member)
    /// and records the scope's watermark when the plan covered the whole scope. The submission is planned when there is
    /// work, completed when there is none.
    /// </summary>
    public async Task<SubmissionState> FinalizePlanningAsync(
        FlowDefinition flow, Guid submissionId, IReadOnlyDictionary<string, string> parameters, IntakeCounts counts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(counts);
        var now = _time.GetUtcNow().UtcDateTime;
        var submission = await _ledger.GetSubmissionAsync(submissionId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Submission {submissionId} is not in the ledger.");

        submission = submission with
        {
            Status = counts.Planned == 0 ? SubmissionStatus.Completed : SubmissionStatus.Planned,
            StartedUtc = submission.StartedUtc ?? now,
            CompletedUtc = counts.Planned == 0 ? now : null,
            RecordCount = counts.Records,
            Planned = counts.Planned,
            SkippedUnchanged = counts.Skipped,
            AwaitingApproval = counts.AwaitingApproval,
            SkippedStale = counts.Stale,
            UnchangedAtPush = 0,
            Blocked = counts.Blocked,
            Held = counts.Held,
            Untracked = counts.Untracked,
            BatchCount = counts.Batches,
            Delivered = 0,
            Failed = 0,
            Error = null,
        };
        await _ledger.UpdateSubmissionAsync(submission, ct).ConfigureAwait(false);
        await WriteWatermarkAsync(flow, submission, parameters, ct).ConfigureAwait(false);
        _logger.LogInformation("Submission {SubmissionId}: {Counts}.", submission.SubmissionId, counts);
        await EmitAsync(flow, submission, counts.Planned == 0 ? "submission.completed" : "submission.planned", Summarize(submission), ct).ConfigureAwait(false);
        return submission;
    }

    /// <summary>
    /// Moves the scope's watermark to the window this plan covered. Only a plan of the whole scope may: a key-scoped
    /// submission reads a few records and says nothing about the rows it never looked at.
    /// </summary>
    private async Task WriteWatermarkAsync(FlowDefinition flow, SubmissionState submission, IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        if (!flow.Change.UseSourceVersions || !submission.CoversScope || submission.WindowToUtc is not { } through)
        {
            return;
        }

        await _ledger.SetWatermarkAsync(
            new SourceWatermark(
                flow.Id,
                Planner.ScopeKey(parameters),
                through,
                submission.SubmissionId,
                _time.GetUtcNow().UtcDateTime,
                Hashing.ContentHash.Of(submission.RenderContext)),
            ct).ConfigureAwait(false);
    }

    /// <summary>Streams the plan of one read (or one key range of it) into work batches and the ledger.</summary>
    private async Task<IntakeCounts> StageAsync(FlowDefinition flow, PreparedIntake prepared, KeyRange? range, int batchBase, CancellationToken ct)
    {
        var header = prepared.Header;
        var submission = prepared.Submission;
        var workRoot = prepared.WorkRoot;
        var summary = new PlanSummary();
        var batchRecords = Math.Max(1, flow.Reliability.BatchRecords);
        var nextBatch = batchBase;
        var batches = 0;
        long staged = 0;

        WorkBatchWriter? writer = null;
        var pending = new List<RecordState>(batchRecords);
        var skipped = new List<SkippedRecord>(Planner.RenderBatch);
        var blocked = new List<DeliveryKey>(Planner.RenderBatch);
        var context = header.Mapping.Context.Canonical();
        long refused = 0;
        long conflicted = 0;
        var held = new List<RecordState>();
        var untrackedLogged = 0;
        var lastProgress = _time.GetUtcNow();

        try
        {
            await foreach (var entry in _planner.EntriesAsync(header, range, flow.Reliability.EffectiveRenderParallelism, summary, ct).ConfigureAwait(false))
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
                        // Left untouched: its state and history belong to the submission that held it. The request to
                        // plan it again is cleared, so a run does not meet it again on every pass.
                        blocked.Add(entry.Key.Value);
                        if (blocked.Count >= Planner.RenderBatch)
                        {
                            await _ledger.ClearPlanRequestedAsync(flow.Id, blocked, ct).ConfigureAwait(false);
                            blocked.Clear();
                        }

                        break;

                    case PlannedAction.Hold:
                        held.Add(HeldState(flow, submission, entry, header.Mapping));
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
                            conflicted += closed.Conflicts.Count;
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
                conflicted += closed.Conflicts.Count;
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

        if (blocked.Count > 0)
        {
            await _ledger.ClearPlanRequestedAsync(flow.Id, blocked, ct).ConfigureAwait(false);
        }

        if (held.Count > 0)
        {
            await FlushHeldAsync(flow, submission, held, ct).ConfigureAwait(false);
        }

        return new IntakeCounts(summary.Records, staged, summary.Skips, summary.Holds + conflicted, summary.Blocked, summary.Untracked, batches, summary.Stale + refused, summary.AwaitingApproval);
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
        SourceKeyJson = entry.SourceKeyJson,
        SourceFingerprint = entry.SourceFingerprint,
        SourceModifiedUtc = entry.SourceModifiedUtc,
        Origin = new RecordOrigin(entry.Origin.FileName, entry.Origin.RowNumber, entry.Origin.UpdatedUtc),
        PayloadModifiedUtc = entry.PayloadModifiedUtc,
        RenderContext = entry.SkipTier == SkipTier.ContentHash ? renderContext : null,
        RunId = RunId,
    };

    /// <summary>
    /// Commits the batch file, stages its records in the ledger and registers the batch. Records the ledger refuses
    /// because a newer version landed or was queued since they were planned go to <paramref name="stale"/>, to be
    /// recorded like any other stale skip. Records whose OSDU id another flow has claimed are held here, each naming the
    /// flow that owns the id, so the conflict is on the record's page and in its history rather than only in a log.
    /// </summary>
    private async Task<PendingStaging> CloseBatchAsync(
        FlowDefinition flow, SubmissionState submission, WorkBatchWriter writer, List<RecordState> pending, List<SkippedRecord> stale, CancellationToken ct)
    {
        await writer.DisposeAsync().ConfigureAwait(false);
        var staging = await _ledger.UpsertPendingAsync(flow.Id, pending, ct).ConfigureAwait(false);
        if (staging.Conflicts.Count > 0)
        {
            var byKey = pending.GroupBy(p => p.DeliveryKey).ToDictionary(g => g.Key, g => g.Last());
            var held = staging.Conflicts
                .Select(conflict => ConflictState(byKey[conflict.DeliveryKey], conflict))
                .ToList();
            _logger.LogWarning(
                "Batch {Batch}: {Conflicts} record(s) were held because another flow has claimed their OSDU ids; the first: {Detail}",
                writer.Batch, staging.Conflicts.Count, staging.Conflicts[0].Describe());
            await FlushHeldAsync(flow, submission, held, ct).ConfigureAwait(false);
        }

        if (staging.Refused.Count > 0)
        {
            var refused = staging.Refused.ToHashSet();
            foreach (var record in pending.Where(p => refused.Contains(p.DeliveryKey)))
            {
                var carried = record.PendingSourceModifiedUtc is { } modified
                    ? string.Create(CultureInfo.InvariantCulture, $" (this run read the row as last modified {modified:yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'})")
                    : string.Empty;
                stale.Add(new SkippedRecord
                {
                    DeliveryKey = record.DeliveryKey,
                    Kind = SkipKind.Stale,
                    Reason = $"a newer version of the record was delivered or queued while this run was planning{carried}; OSDU keeps the newer version",
                    SourceKeyJson = record.SourceKeyJson,
                    SourceFingerprint = record.PendingSourceFingerprint,
                    SourceModifiedUtc = record.PendingSourceModifiedUtc,
                    Origin = record.PendingOrigin,
                    PayloadModifiedUtc = record.PendingPayloadModifiedUtc,
                    RunId = RunId,
                });
            }

            _logger.LogWarning(
                "Batch {Batch}: {Refused} record(s) were not staged because a newer version was delivered or queued while this run was planning; they are recorded as stale.",
                writer.Batch, staging.Refused.Count);
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

    /// <summary>
    /// A record staging refused because another flow claimed its OSDU id, as a hold: the origin and version the work was
    /// built from, no OSDU id (the one it names is not this flow's), and the conflict as its reason.
    /// </summary>
    private static RecordState ConflictState(RecordState refused, TargetIdConflict conflict) => refused with
    {
        TargetId = null,
        PendingDocumentRef = null,
        WorkBatch = null,
        PendingMetadataHash = null,
        PendingPayloadHash = null,
        PendingPayloadLocation = null,
        PendingMetadata = false,
        PendingPayload = false,
        CacheSetId = null,
        LastError = conflict.Describe(),
    };

    private async Task FlushHeldAsync(FlowDefinition flow, SubmissionState submission, List<RecordState> held, CancellationToken ct)
    {
        await _ledger.MarkHeldAsync(flow.Id, held, ct).ConfigureAwait(false);
        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var record in held)
        {
            await _listener.OnEventAsync(new DeliveryEvent
            {
                AtUtc = now,
                FlowId = flow.Id,
                FlowName = flow.Label,
                Interface = flow.Interface,
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
        SourceKeyJson = entry.SourceKeyJson,
        Label = entry.Label,
        Identities = entry.Identities,
        MappingName = resolvedMapping.Mapping.Name,
        TargetId = entry.TargetId,
        LastSubmissionId = submission.SubmissionId,
        RunId = RunId,
        PendingSourceFingerprint = entry.SourceFingerprint,
        PendingSourceModifiedUtc = entry.SourceModifiedUtc,
        PendingSourceFileName = entry.Origin.FileName,
        PendingSourceRowNumber = entry.Origin.RowNumber,
        PendingSourceUpdatedUtc = entry.Origin.UpdatedUtc,
        LastError = Http.HeaderRedaction.RedactMessage(entry.Reason),
    };

    private RecordState PendingState(FlowDefinition flow, SubmissionState submission, ResolvedMapping resolved, PlanEntry entry, DocumentRef reference, int batch) => new()
    {
        DeliveryKey = entry.Key!.Value,
        FlowId = flow.Id,
        SourceKey = entry.SourceKey,
        SourceKeyJson = entry.SourceKeyJson,
        Label = entry.Label,
        Identities = entry.Identities,
        MappingName = resolved.Mapping.Name,
        TargetId = entry.TargetId,
        LastSubmissionId = submission.SubmissionId,
        RunId = RunId,
        PendingDocumentRef = reference.ToString(),
        WorkBatch = batch,
        PendingRenderContext = resolved.Context.Canonical(),
        PendingSourceFingerprint = entry.SourceFingerprint,
        PendingSourceModifiedUtc = entry.SourceModifiedUtc,
        PendingSourceFileName = entry.Origin.FileName,
        PendingSourceRowNumber = entry.Origin.RowNumber,
        PendingSourceUpdatedUtc = entry.Origin.UpdatedUtc,
        PendingMetadataHash = entry.Render!.MetadataHash,
        PendingPayloadHash = entry.PayloadHash,
        PendingPayloadModifiedUtc = entry.DeliverPayload ? entry.PayloadModifiedUtc : null,
        PendingPayloadLocation = entry.PayloadLocation,
        PendingMetadata = entry.DeliverMetadata,
        PendingPayload = entry.DeliverPayload,
        PendingReferences = entry.References,
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
        var waiting = await _ledger.CountAsync(flowId, submissionId, RecordStatus.Waiting, ct).ConfigureAwait(false);
        var stillPending = await _ledger.HasPendingAsync(flowId, submissionId, _time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        var now = _time.GetUtcNow().UtcDateTime;
        var wasClosed = submission.Status is SubmissionStatus.Completed or SubmissionStatus.Failed;
        submission = submission with
        {
            Delivered = delivered,
            UnchangedAtPush = unchangedAtPush,
            Held = held,
            Failed = failed,
            Waiting = waiting,
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

    /// <summary>
    /// Closes a submission whose run stopped before its records were all sent (a failure guard tripped, the run was
    /// cancelled): its totals counted as <see cref="CompleteAsync"/> counts them, and, while records of it are still
    /// pending, failed with <paramref name="reason"/>. A closed submission's pending records are sent by the flow's next
    /// run, so what the stop left is not stranded behind a submission nobody works on any more.
    /// </summary>
    public async Task<SubmissionState> StopAsync(Guid submissionId, Guid flowId, string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var submission = await CompleteAsync(submissionId, flowId, ct).ConfigureAwait(false);
        if (submission.Status is SubmissionStatus.Completed or SubmissionStatus.Failed)
        {
            return submission;
        }

        submission = submission with
        {
            Status = SubmissionStatus.Failed,
            Error = reason,
            CompletedUtc = _time.GetUtcNow().UtcDateTime,
        };
        await _ledger.UpdateSubmissionAsync(submission, ct).ConfigureAwait(false);
        await EmitAsync(null, submission, "submission.completed", $"{reason}; {Summarize(submission)}", ct).ConfigureAwait(false);
        return submission;
    }

    public static string Summarize(SubmissionState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return string.Create(CultureInfo.InvariantCulture, $"{s.Status.ToString().ToLowerInvariant()}: {s.Planned} planned in {s.BatchCount} batch(es), {s.Delivered} delivered, {s.SkippedUnchanged + s.UnchangedAtPush} unchanged, {s.AwaitingApproval} awaiting approval, {s.SkippedStale} stale, {s.Blocked} blocked, {s.Held} held, {s.Failed} failed, {s.Waiting} waiting");
    }

    /// <summary>The batch numbers a share writes: a namespace per first slice, so fan-out members never collide.</summary>
    public static int BatchBase(int firstSlice)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(firstSlice);
        if (firstSlice > MaxFanOutPartition)
        {
            throw new DeliveryException($"A fan-out intake covers at most {MaxFanOutPartition + 1} slices; slice {firstSlice} is out of range.");
        }

        return firstSlice * BatchesPerPartition;
    }

    private ValueTask EmitAsync(FlowDefinition? flow, SubmissionState submission, string kind, string? detail, CancellationToken ct)
        => _listener.OnEventAsync(new DeliveryEvent
        {
            AtUtc = _time.GetUtcNow().UtcDateTime,
            FlowId = submission.FlowId,
            FlowName = flow?.Label ?? submission.FlowName,
            Interface = flow?.Interface,
            Kind = kind,
            SubmissionId = submission.SubmissionId,
            Worker = "intake",
            Detail = detail,
        }, ct);
}
