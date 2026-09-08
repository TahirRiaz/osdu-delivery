using System.Globalization;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Delivery.Drops;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Planning;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.Engine.Planning;

/// <summary>One record's place in a plan.</summary>
public sealed record PlanEntry
{
    public DeliveryKey? Key { get; init; }

    public required string SourceKey { get; init; }

    public string? Label { get; init; }

    public string? TargetId { get; init; }

    public required PlannedAction Action { get; init; }

    public SkipTier SkipTier { get; init; }

    public required string Reason { get; init; }

    public RenderResult? Render { get; init; }

    public string? SourceFingerprint { get; init; }

    public string? PayloadHash { get; init; }

    public string? PayloadLocation { get; init; }

    public int? ChunkCount { get; init; }

    public RecordState? Existing { get; init; }

    public bool DeliverMetadata { get; init; }

    public bool DeliverPayload { get; init; }
}

/// <summary>What a run would do (design.md section 11: plan changes nothing).</summary>
public sealed record DeliveryPlan
{
    public required FlowDefinition Flow { get; init; }

    public required Drop Drop { get; init; }

    public required ResolvedMapping Mapping { get; init; }

    public required IReadOnlyDictionary<string, string> Parameters { get; init; }

    public required IReadOnlyList<PlanEntry> Entries { get; init; }

    public required IReadOnlyList<ValidationIssue> Issues { get; init; }

    public bool SkippedWholeRun { get; init; }

    public string? SkipReason { get; init; }

    public int Count(PlannedAction action) => Entries.Count(e => e.Action == action);

    public int Deliveries => Entries.Count(e => e.DeliverMetadata || e.DeliverPayload);

    public int Skips => Entries.Count(e => e.Action == PlannedAction.Skip);

    public int Holds => Entries.Count(e => e.Action == PlannedAction.Hold);

    public int Blocked => Entries.Count(e => e.Action == PlannedAction.Blocked);
}

/// <summary>
/// Renders a drop against the ledger and decides per record what would change (design.md sections 6.6 and 11).
/// Works offline: the only inputs are the drop, the pinned snapshots and the ledger (which may be absent, in which
/// case every record is a create).
/// </summary>
public sealed class Planner
{
    private readonly IDropReader _drops;
    private readonly ILedger? _ledger;
    private readonly ILogger<Planner> _logger;

    public Planner(IDropReader drops, ILedger? ledger, ILogger<Planner> logger)
    {
        ArgumentNullException.ThrowIfNull(drops);
        ArgumentNullException.ThrowIfNull(logger);
        _drops = drops;
        _ledger = ledger;
        _logger = logger;
    }

    /// <param name="flow">The flow whose drop is planned.</param>
    /// <param name="resolved">The pinned render inputs (mapping, schema and reference snapshots).</param>
    /// <param name="parameters">The resolved flow parameter values.</param>
    /// <param name="dropLocation">The drop root to read.</param>
    /// <param name="force">Skip the tier-0 whole-run gate: plan every record even when no source table advanced.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<DeliveryPlan> PlanAsync(
        FlowDefinition flow,
        ResolvedMapping resolved,
        IReadOnlyDictionary<string, string> parameters,
        string dropLocation,
        bool force = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(parameters);
        var where = flow.SourcePath ?? flow.Name;

        var drop = await _drops.OpenAsync(dropLocation, flow.Source.Manifest, ct).ConfigureAwait(false);
        var manifest = drop.Manifest;
        CheckManifestAgainstFlow(flow, manifest, parameters, where);

        var issues = Preflight.Check(resolved.Mapping, resolved.Schema, resolved.References, resolved.Context, manifest.DeclaredColumns());
        Preflight.ThrowIfFailed(issues, where);
        CheckFlowBindings(flow, manifest, where);

        var protocolPayload = PayloadName(flow);
        var scopeKey = ScopeKey(parameters);

        if (!force && _ledger is not null && flow.Change.UseSourceVersions && manifest.SourceVersions.Count > 0)
        {
            var watermarks = await _ledger.GetWatermarksAsync(flow.Id, scopeKey, ct).ConfigureAwait(false);
            var known = watermarks.ToDictionary(w => w.Table, w => w.Version, StringComparer.Ordinal);
            var advanced = manifest.SourceVersions.Where(kv => !known.TryGetValue(kv.Key, out var v) || v < kv.Value).Select(kv => kv.Key).ToList();
            if (advanced.Count == 0)
            {
                _logger.LogInformation("Tier 0: no source table advanced since the last run for scope {Scope}; skipping the whole run.", scopeKey);
                return new DeliveryPlan
                {
                    Flow = flow,
                    Drop = drop,
                    Mapping = resolved,
                    Parameters = parameters,
                    Entries = [],
                    Issues = issues,
                    SkippedWholeRun = true,
                    SkipReason = $"none of {manifest.SourceVersions.Count} source table(s) advanced since the last run",
                };
            }
        }

        var entries = new List<PlanEntry>();
        var batch = new List<SourceRecord>(200);
        await foreach (var record in _drops.ReadRecordsAsync(drop, ct).ConfigureAwait(false))
        {
            batch.Add(record);
            if (batch.Count == 200)
            {
                await PlanBatchAsync(flow, resolved, drop, batch, protocolPayload, entries, ct).ConfigureAwait(false);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await PlanBatchAsync(flow, resolved, drop, batch, protocolPayload, entries, ct).ConfigureAwait(false);
        }

        if (manifest.RecordCount > 0 && manifest.RecordCount != entries.Count)
        {
            issues = [.. issues, ValidationIssue.Warning($"{where}: the manifest declares {manifest.RecordCount} record(s) but the drop holds {entries.Count}.")];
        }

        return new DeliveryPlan
        {
            Flow = flow,
            Drop = drop,
            Mapping = resolved,
            Parameters = parameters,
            Entries = entries,
            Issues = issues,
        };
    }

    /// <summary>The tier-0 scope key: one flow instance per distinct parameter set.</summary>
    public static string ScopeKey(IReadOnlyDictionary<string, string> parameters)
        => string.Join(";", parameters.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value));

    public static string? PayloadName(FlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (!DeliveryProtocols.CarriesPayload(flow.Target.Protocol))
        {
            return null;
        }

        return flow.Target.ProtocolOptions.Payload ?? (flow.Source.Payloads.Count == 1 ? flow.Source.Payloads.Keys.First() : null);
    }

    private async Task PlanBatchAsync(
        FlowDefinition flow,
        ResolvedMapping resolved,
        Drop drop,
        List<SourceRecord> batch,
        string? payloadName,
        List<PlanEntry> entries,
        CancellationToken ct)
    {
        var renderer = resolved.Renderer;
        var context = resolved.Context.Canonical();
        var keyed = new List<(SourceRecord Record, DeliveryKey? Key, string SourceKey, string? Label)>(batch.Count);
        foreach (var record in batch)
        {
            var key = renderer.DeriveKey(record.Row, out var values);
            keyed.Add((record, key, SourceKey.Display(resolved.Mapping.Source.System, values), renderer.Label(record.Row)));
        }

        var existing = _ledger is null
            ? new Dictionary<DeliveryKey, RecordState>()
            : await _ledger.GetRecordsAsync(flow.Id, keyed.Where(k => k.Key is not null).Select(k => k.Key!.Value), ct).ConfigureAwait(false);

        var payload = payloadName is null ? null : drop.Manifest.Payloads.GetValueOrDefault(payloadName);

        foreach (var (record, key, sourceKey, label) in keyed)
        {
            ct.ThrowIfCancellationRequested();
            if (key is null)
            {
                entries.Add(new PlanEntry { Key = null, SourceKey = sourceKey, Label = label, Action = PlannedAction.Hold, Reason = "natural key incomplete: every key column must be non-empty" });
                continue;
            }

            var state = existing.GetValueOrDefault(key.Value);
            var fingerprint = flow.Source.Fingerprint is { } fp ? record.Row.GetString(fp) : null;
            var payloadHash = payload is null ? null : record.Row.GetString(payload.HashColumn);
            var hasPayload = payload is not null;

            // A record held, failed or deleted earlier stays where it is until an operator releases it or the source
            // changes (design.md section 7.4). Without a fingerprint column, only a release can unblock it.
            if (state is { Blocked: true } && (fingerprint is null || string.Equals(state.PendingSourceFingerprint, fingerprint, StringComparison.Ordinal)))
            {
                entries.Add(new PlanEntry
                {
                    Key = key,
                    SourceKey = sourceKey,
                    Label = label,
                    TargetId = state.TargetId,
                    Existing = state,
                    Action = PlannedAction.Blocked,
                    Reason = $"{state.Status.ToString().ToLowerInvariant()} since {state.UpdatedUtc:u}: {state.LastError ?? "no reason recorded"}; release the record or change the source to plan it again",
                    SourceFingerprint = fingerprint,
                    PayloadHash = payloadHash,
                });
                continue;
            }

            if (hasPayload && string.IsNullOrWhiteSpace(payloadHash))
            {
                entries.Add(new PlanEntry { Key = key, SourceKey = sourceKey, Label = label, Existing = state, Action = PlannedAction.Hold, Reason = $"payload '{payloadName}' declares hash column '{payload!.HashColumn}' but the row carries no value; no curve data was prepared", SourceFingerprint = fingerprint });
                continue;
            }

            if (ChangeDetector.CanSkipWithoutRender(state, fingerprint, payloadHash, context, flow.Change))
            {
                entries.Add(new PlanEntry
                {
                    Key = key,
                    SourceKey = sourceKey,
                    Label = label,
                    TargetId = state!.TargetId,
                    Existing = state,
                    Action = PlannedAction.Skip,
                    SkipTier = SkipTier.Fingerprint,
                    Reason = "source fingerprint, payload hash and render context unchanged",
                    SourceFingerprint = fingerprint,
                    PayloadHash = payloadHash,
                });
                continue;
            }

            var render = renderer.Render(record);
            if (render.IsHeld)
            {
                entries.Add(new PlanEntry
                {
                    Key = key,
                    SourceKey = sourceKey,
                    Label = label,
                    TargetId = render.TargetId,
                    Existing = state,
                    Action = PlannedAction.Hold,
                    Reason = string.Join("; ", render.Holds),
                    Render = render,
                    SourceFingerprint = fingerprint,
                    PayloadHash = payloadHash,
                });
                continue;
            }

            var decision = ChangeDetector.Decide(state, render.MetadataHash, payloadHash, hasPayload, flow.Change);
            string? payloadLocation = null;
            int? chunkCount = null;
            if (decision.DeliverPayload)
            {
                payloadLocation = _drops.PayloadLocation(drop, payloadName!, key.Value.Value);
                var chunks = await _drops.ListPayloadChunksAsync(payloadLocation, ct).ConfigureAwait(false);
                chunkCount = chunks.Count;
                if (chunks.Count == 0)
                {
                    entries.Add(new PlanEntry
                    {
                        Key = key,
                        SourceKey = sourceKey,
                        Label = label,
                        TargetId = render.TargetId,
                        Existing = state,
                        Action = PlannedAction.Hold,
                        Reason = $"payload hash present but no chunk files under {payloadLocation}",
                        Render = render,
                        SourceFingerprint = fingerprint,
                        PayloadHash = payloadHash,
                    });
                    continue;
                }
            }

            entries.Add(new PlanEntry
            {
                Key = key,
                SourceKey = sourceKey,
                Label = label,
                TargetId = render.TargetId,
                Existing = state,
                Action = decision.Action,
                SkipTier = decision.SkipTier,
                Reason = decision.Reason,
                Render = render,
                SourceFingerprint = fingerprint,
                PayloadHash = payloadHash,
                PayloadLocation = payloadLocation,
                ChunkCount = chunkCount,
                DeliverMetadata = decision.DeliverMetadata,
                DeliverPayload = decision.DeliverPayload,
            });
        }
    }

    private static void CheckManifestAgainstFlow(FlowDefinition flow, DropManifest manifest, IReadOnlyDictionary<string, string> parameters, string where)
    {
        if (!manifest.Flow.Equals(flow.Name, StringComparison.Ordinal))
        {
            throw new FlowValidationException($"{where}: the drop was prepared for flow '{manifest.Flow}', not '{flow.Name}'.");
        }

        if (!manifest.Mapping.Equals(flow.Render.Mapping, StringComparison.Ordinal))
        {
            throw new FlowValidationException(
                $"{where}: the drop was prepared for mapping '{manifest.Mapping}' but the flow pins '{flow.Render.Mapping}'. Re-prepare the drop or promote the flow deliberately.");
        }

        foreach (var (name, value) in manifest.Parameters)
        {
            if (parameters.TryGetValue(name, out var supplied) && !supplied.Equals(value, StringComparison.Ordinal))
            {
                throw new FlowValidationException($"{where}: parameter '{name}' is '{supplied}' for this run but the drop was prepared with '{value}'.");
            }
        }
    }

    private static void CheckFlowBindings(FlowDefinition flow, DropManifest manifest, string where)
    {
        var columns = manifest.DeclaredColumns();
        var root = columns[DropManifest.RootScope];
        if (flow.Source.Fingerprint is { } fp && !root.Contains(fp))
        {
            throw new FlowValidationException($"{where}: source.fingerprint names column '{fp}', which the drop's root scope does not declare.");
        }

        var payloadName = PayloadName(flow);
        if (payloadName is not null)
        {
            if (!manifest.Payloads.TryGetValue(payloadName, out var payload))
            {
                throw new FlowValidationException($"{where}: the protocol streams payload '{payloadName}' but the manifest declares no such payload.");
            }

            if (!root.Contains(payload.HashColumn))
            {
                throw new FlowValidationException($"{where}: payload '{payloadName}' hashColumn '{payload.HashColumn}' is not declared in the drop's root scope.");
            }
        }

        foreach (var (name, _) in flow.Source.Scopes)
        {
            if (!manifest.Scopes.ContainsKey(name))
            {
                throw new FlowValidationException($"{where}: source.scopes declares '{name}', which the manifest does not declare.");
            }
        }
    }
}

public static class PlanFormatting
{
    public static string Describe(PlanEntry e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var action = e.Action switch
        {
            PlannedAction.Skip => e.SkipTier == SkipTier.Fingerprint ? "skip (tier 1)" : "skip (tier 2)",
            PlannedAction.Create => "create",
            PlannedAction.UpdateMetadata => "update metadata",
            PlannedAction.UpdatePayload => "update payload",
            PlannedAction.UpdateBoth => "update metadata+payload",
            PlannedAction.Hold => "hold",
            PlannedAction.Blocked => "blocked",
            _ => e.Action.ToString().ToLowerInvariant(),
        };
        var chunks = e.ChunkCount is { } c ? $", {c.ToString(CultureInfo.InvariantCulture)} chunk(s)" : string.Empty;
        var label = e.Label is null ? string.Empty : $" [{e.Label}]";
        return $"{action,-24} {e.SourceKey}{label}  {e.TargetId ?? "-"}  ({e.Reason}{chunks})";
    }
}
