using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Planning;

/// <summary>What a plan decides for one record.</summary>
public enum PlannedAction
{
    /// <summary>Nothing in OSDU would change.</summary>
    Skip,

    /// <summary>OSDU holds nothing for this key yet.</summary>
    Create,

    /// <summary>The metadata document changed; the payload did not (or there is none).</summary>
    UpdateMetadata,

    /// <summary>The payload changed; the metadata document did not.</summary>
    UpdatePayload,

    /// <summary>Both changed.</summary>
    UpdateBoth,

    /// <summary>The record cannot be delivered as it stands.</summary>
    Hold,

    /// <summary>
    /// Held, failed or deleted earlier and the source has not changed since: it stays where it is until an
    /// operator releases it (design.md section 7.4, "do not retry without intervention").
    /// </summary>
    Blocked,
}

/// <summary>Why a skip was decided, for honest run summaries.</summary>
public enum SkipTier
{
    None,

    /// <summary>Tier 1: source fingerprint and render context unchanged, no render needed.</summary>
    Fingerprint,

    /// <summary>Tier 2: rendered and compared; the content hashes matched.</summary>
    ContentHash,
}

public sealed record ChangeDecision(PlannedAction Action, SkipTier SkipTier, bool DeliverMetadata, bool DeliverPayload, string Reason)
{
    public bool IsDelivery => DeliverMetadata || DeliverPayload;
}

/// <summary>
/// The two-tier per-record gate (design.md section 6.6) and the two independently decided hashes (section 6.3).
/// Pure: given what the ledger holds and what the drop presents, decide what to send.
/// </summary>
public static class ChangeDetector
{
    /// <summary>
    /// Tier 1: can the record be skipped without rendering? True when the ledger holds a delivered record whose
    /// source fingerprint, payload hash and render context all match what the drop presents.
    /// </summary>
    public static bool CanSkipWithoutRender(RecordState? existing, string? sourceFingerprint, string? payloadHash, string renderContext, FlowChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (existing is null || change.OnUnchanged == UnchangedAction.Deliver)
        {
            return false;
        }

        if (existing.Status != RecordStatus.Delivered || sourceFingerprint is null || existing.SourceFingerprint is null)
        {
            return false;
        }

        if (change.Detect == ChangeDetection.Always || change.PayloadDetect == ChangeDetection.Always)
        {
            return false;
        }

        return string.Equals(existing.SourceFingerprint, sourceFingerprint, StringComparison.Ordinal)
            && string.Equals(existing.RenderContext, renderContext, StringComparison.Ordinal)
            && string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal);
    }

    /// <summary>Tier 2: decide from the rendered document's hash and the payload hash.</summary>
    public static ChangeDecision Decide(
        RecordState? existing,
        string metadataHash,
        string? payloadHash,
        bool hasPayload,
        FlowChange change)
    {
        ArgumentNullException.ThrowIfNull(metadataHash);
        ArgumentNullException.ThrowIfNull(change);

        var delivered = existing is { Status: RecordStatus.Delivered } || existing?.TargetVersion is not null;
        if (!delivered)
        {
            return new ChangeDecision(PlannedAction.Create, SkipTier.None, true, hasPayload, existing is null ? "not in ledger" : $"ledger status {existing.Status.ToString().ToLowerInvariant()}, never delivered");
        }

        var metadataChanged = change.Detect == ChangeDetection.Always
            || change.OnUnchanged == UnchangedAction.Deliver
            || !string.Equals(existing!.MetadataHash, metadataHash, StringComparison.Ordinal);

        var payloadChanged = hasPayload && (change.PayloadDetect == ChangeDetection.Always
            || change.OnUnchanged == UnchangedAction.Deliver
            || !string.Equals(existing!.PayloadHash, payloadHash, StringComparison.Ordinal));

        if (!metadataChanged && !payloadChanged)
        {
            return new ChangeDecision(PlannedAction.Skip, SkipTier.ContentHash, false, false, "metadata and payload hashes unchanged");
        }

        if (metadataChanged && payloadChanged)
        {
            return new ChangeDecision(PlannedAction.UpdateBoth, SkipTier.None, true, true, "metadata and payload changed");
        }

        return metadataChanged
            ? new ChangeDecision(PlannedAction.UpdateMetadata, SkipTier.None, true, false, "metadata changed, payload unchanged")
            : new ChangeDecision(PlannedAction.UpdatePayload, SkipTier.None, false, true, "payload changed, metadata unchanged");
    }
}
