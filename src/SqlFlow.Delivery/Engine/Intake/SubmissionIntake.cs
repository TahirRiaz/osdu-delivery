using System.Text.Json;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Planning;

namespace SqlFlow.Delivery.Engine.Intake;

public sealed record IntakeResult(SubmissionState Submission, DeliveryPlan? Plan, bool AlreadyProcessed)
{
    public bool NothingToDo => AlreadyProcessed || Plan is null || Plan.SkippedWholeRun || Plan.Deliveries == 0;
}

/// <summary>
/// Turns a manifest notification into ledger state (design.md sections 3.3 and 7.2): registers the submission under
/// its idempotency key, plans the drop, and writes the pending work per record. Re-running an intake for a
/// completed submission does nothing unless forced; re-running one that was interrupted picks up where it stopped.
/// </summary>
public sealed class SubmissionIntake
{
    private readonly ILedger _ledger;
    private readonly Planner _planner;
    private readonly TimeProvider _time;
    private readonly IDeliveryListener _listener;
    private readonly ILogger<SubmissionIntake> _logger;

    /// <summary>The platform run the intake happens in, stamped on the pending work it writes.</summary>
    public Guid? RunId { get; init; }

    public SubmissionIntake(ILedger ledger, Planner planner, TimeProvider time, IDeliveryListener listener, ILogger<SubmissionIntake> logger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(logger);
        _ledger = ledger;
        _planner = planner;
        _time = time;
        _listener = listener;
        _logger = logger;
    }

    public async Task<IntakeResult> IntakeAsync(
        FlowDefinition flow,
        ResolvedMapping resolved,
        IReadOnlyDictionary<string, string> parameters,
        string dropLocation,
        bool force,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(parameters);

        var plan = await _planner.PlanAsync(flow, resolved, parameters, dropLocation, force, ct).ConfigureAwait(false);
        var manifest = plan.Drop.Manifest;
        var now = _time.GetUtcNow().UtcDateTime;

        var (submission, created) = await _ledger.RegisterSubmissionAsync(new SubmissionState
        {
            SubmissionId = manifest.SubmissionId,
            FlowId = flow.Id,
            FlowName = flow.Name,
            MappingReference = resolved.Mapping.Reference,
            RenderContext = resolved.Context.Canonical(),
            DropLocation = plan.Drop.Location,
            ParametersJson = JsonSerializer.Serialize(parameters),
            RecordCount = manifest.RecordCount,
            ReceivedUtc = now,
        }, ct).ConfigureAwait(false);

        if (!created && submission.Status == SubmissionStatus.Completed && !force)
        {
            _logger.LogInformation("Submission {SubmissionId} was already completed at {CompletedUtc}; nothing to do (use --force to re-plan).", submission.SubmissionId, submission.CompletedUtc);
            return new IntakeResult(submission, null, AlreadyProcessed: true);
        }

        if (!created)
        {
            _logger.LogInformation("Submission {SubmissionId} exists with status {Status}; re-planning against the current ledger.", submission.SubmissionId, submission.Status);
        }

        if (plan.SkippedWholeRun)
        {
            submission = submission with { Status = SubmissionStatus.Completed, StartedUtc = now, CompletedUtc = now, Error = null };
            await _ledger.UpdateSubmissionAsync(submission, ct).ConfigureAwait(false);
            await EmitAsync(flow, submission, "submission.completed", plan.SkipReason, ct).ConfigureAwait(false);
            return new IntakeResult(submission, plan, AlreadyProcessed: false);
        }

        var pending = new List<RecordState>();
        var skipped = new List<Identity.DeliveryKey>();
        var held = new List<RecordState>();
        var blocked = 0;
        foreach (var entry in plan.Entries)
        {
            if (entry.Key is null)
            {
                _logger.LogWarning("Record {SourceKey} has no derivable delivery key and cannot be tracked: {Reason}", entry.SourceKey, entry.Reason);
                continue;
            }

            switch (entry.Action)
            {
                case PlannedAction.Skip:
                    skipped.Add(entry.Key.Value);
                    break;
                case PlannedAction.Blocked:
                    // Left untouched: its state and history belong to the submission that held it.
                    blocked++;
                    break;
                case PlannedAction.Hold:
                    held.Add(new RecordState
                    {
                        DeliveryKey = entry.Key.Value,
                        FlowId = flow.Id,
                        SourceKey = entry.SourceKey,
                        Label = entry.Label,
                        MappingName = resolved.Mapping.Name,
                        TargetId = entry.TargetId,
                        LastSubmissionId = submission.SubmissionId,
                        RunId = RunId,
                        PendingSourceFingerprint = entry.SourceFingerprint,
                        LastError = Http.HeaderRedaction.RedactMessage(entry.Reason),
                    });
                    break;
                default:
                    pending.Add(new RecordState
                    {
                        DeliveryKey = entry.Key.Value,
                        FlowId = flow.Id,
                        SourceKey = entry.SourceKey,
                        Label = entry.Label,
                        MappingName = resolved.Mapping.Name,
                        TargetId = entry.TargetId,
                        LastSubmissionId = submission.SubmissionId,
                        RunId = RunId,
                        PendingDocument = entry.Render!.Canonical,
                        PendingRenderContext = resolved.Context.Canonical(),
                        PendingSourceFingerprint = entry.SourceFingerprint,
                        PendingMetadataHash = entry.Render.MetadataHash,
                        PendingPayloadHash = entry.PayloadHash,
                        PendingPayloadLocation = entry.PayloadLocation,
                        PendingMetadata = entry.DeliverMetadata,
                        PendingPayload = entry.DeliverPayload,
                    });
                    break;
            }
        }

        await _ledger.UpsertPendingAsync(pending, ct).ConfigureAwait(false);
        await _ledger.MarkSkippedAsync(flow.Id, skipped, submission.SubmissionId, ct).ConfigureAwait(false);
        await _ledger.MarkHeldAsync(held, ct).ConfigureAwait(false);

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

        if (flow.Change.UseSourceVersions && manifest.SourceVersions.Count > 0)
        {
            var scope = Planner.ScopeKey(parameters);
            await _ledger.SetWatermarksAsync(manifest.SourceVersions.Select(kv => new SourceWatermark(flow.Id, scope, kv.Key, kv.Value, now)), ct).ConfigureAwait(false);
        }

        submission = submission with
        {
            Status = pending.Count == 0 ? SubmissionStatus.Completed : SubmissionStatus.Planned,
            StartedUtc = now,
            CompletedUtc = pending.Count == 0 ? now : null,
            Planned = pending.Count,
            SkippedUnchanged = skipped.Count,
            Blocked = blocked,
            Held = held.Count,
            Delivered = 0,
            Failed = 0,
            Error = null,
        };
        await _ledger.UpdateSubmissionAsync(submission, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Submission {SubmissionId}: {Planned} to deliver, {Skipped} unchanged, {Blocked} blocked, {Held} held (of {Total}).",
            submission.SubmissionId, pending.Count, skipped.Count, blocked, held.Count, plan.Entries.Count);
        await EmitAsync(flow, submission, pending.Count == 0 ? "submission.completed" : "submission.planned", Summarize(submission), ct).ConfigureAwait(false);
        return new IntakeResult(submission, plan, AlreadyProcessed: false);
    }

    /// <summary>Closes the submission after the worker drained it, with honest counts scoped to the records it touched.</summary>
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
        return $"{s.Status.ToString().ToLowerInvariant()}: {s.Planned} planned, {s.Delivered} delivered, {s.SkippedUnchanged} unchanged, {s.Blocked} blocked, {s.Held} held, {s.Failed} failed";
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
