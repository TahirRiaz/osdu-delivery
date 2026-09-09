using System.Globalization;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// Which records a removal acts on: either the exact keys an operator picked, or the listing filter they were
/// looking at when they asked for every record it matches. The filter form is resolved on the node against the
/// ledger at the moment the removal runs, so a selection of "everything this run touched" never has to travel as
/// tens of thousands of ids, and the resolved count is what the removal reports acting on.
/// </summary>
public sealed record RemovalSelection
{
    private RemovalSelection(IReadOnlyList<DeliveryKey>? keys, RecordQuery? filter)
    {
        Keys = keys;
        Filter = filter;
    }

    /// <summary>The exact records to remove, or null when the selection is a filter.</summary>
    public IReadOnlyList<DeliveryKey>? Keys { get; }

    /// <summary>The listing every matching record of which is to be removed, or null when the selection is a key list.</summary>
    public RecordQuery? Filter { get; }

    public static RemovalSelection Of(IReadOnlyList<DeliveryKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
        {
            throw new DeliveryException("A removal needs at least one record.");
        }

        if (keys.Count > RemovalLimits.MaxSelection)
        {
            throw new DeliveryException($"A removal takes at most {RemovalLimits.MaxSelection.ToString(CultureInfo.InvariantCulture)} records at a time; {keys.Count.ToString(CultureInfo.InvariantCulture)} were selected.");
        }

        return new RemovalSelection(keys, null);
    }

    public static RemovalSelection Of(RecordQuery filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return new RemovalSelection(null, filter);
    }

    /// <summary>The selection as the activity trail records it: what was asked for, never the resolved key list.</summary>
    public object Describe(RemovalScope scope) => Keys is not null
        ? new { scope = scope.ToString().ToLowerInvariant(), records = Keys.Count, keys = Keys.Take(20).Select(k => k.ToString()).ToList() }
        : new
        {
            scope = scope.ToString().ToLowerInvariant(),
            filter = new
            {
                status = Filter!.Status?.ToString().ToLowerInvariant(),
                search = Filter.Search,
                mode = Filter.Mode.ToString().ToLowerInvariant(),
                submissionId = Filter.SubmissionId,
                runId = Filter.RunId,
                drifted = Filter.Drifted,
            },
        };
}

/// <summary>
/// The three endpoints a removal of this flow's records would call, as the flow resolves them: the protocol's
/// defaults with the flow's own overrides applied. The GUI shows them next to the target so an operator can see
/// exactly which call each scope makes before asking for it, and the well log protocol genuinely differs from the
/// others, so this is derived from the flow rather than assumed.
/// </summary>
public sealed record RemovalEndpoints(string Record, string History, string Everything)
{
    public static RemovalEndpoints Of(FlowTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var options = target.ProtocolOptions;
        var history = options.PurgeVersionsPath ?? OsduRecordProtocol.DefaultPurgeVersionsPath;
        if (target.Protocol == DeliveryProtocol.OsduWellLog)
        {
            var delete = options.DeletePath ?? OsduWellLogProtocol.DefaultDeletePath;
            return new RemovalEndpoints(delete, history, delete + "?purge=true");
        }

        return new RemovalEndpoints(
            options.DeletePath ?? OsduRecordProtocol.DefaultDeletePath,
            history,
            options.PurgePath ?? OsduRecordProtocol.DefaultPurgePath);
    }
}

/// <summary>What a removal did to one record: enough to answer "what happened to this one" without a second query.</summary>
public sealed record RemovalRecordResult(
    Guid DeliveryKey, string? SourceKey, string? Label, string? TargetId, string Outcome, string Detail, Guid? SubmissionId)
{
    public static RemovalRecordResult Removed(DeliveryKey key, string? sourceKey, string? label, string? targetId, string detail, Guid? submissionId)
        => new(key.Value, sourceKey, label, targetId, "removed", detail, submissionId);

    public static RemovalRecordResult AlreadyGone(DeliveryKey key, string? sourceKey, string? label, string? targetId, string detail, Guid? submissionId)
        => new(key.Value, sourceKey, label, targetId, "already-gone", detail, submissionId);

    public static RemovalRecordResult Skipped(DeliveryKey key, string? sourceKey, string? label, string? targetId, string detail, Guid? submissionId)
        => new(key.Value, sourceKey, label, targetId, "skipped", detail, submissionId);

    public static RemovalRecordResult Failed(DeliveryKey key, string? sourceKey, string? label, string? targetId, string detail, Guid? submissionId)
        => new(key.Value, sourceKey, label, targetId, "failed", detail, submissionId);
}

/// <summary>
/// What a removal did, in total and per record. The per-record list is capped at
/// <see cref="RemovalLimits.MaxReported"/> so a removal of thousands still returns a result a page can render;
/// every record's own outcome is in the ledger regardless, on its attempt, which is where a removal is audited.
/// Failures are kept ahead of successes when the cap bites, because they are what an operator needs to see.
/// </summary>
public sealed record RemovalSummary(
    RemovalScope Scope, int Selected, int Removed, int AlreadyGone, int Skipped, int Failed,
    IReadOnlyList<RemovalRecordResult> Records, bool Truncated)
{
    public static RemovalSummary Of(RemovalScope scope, int selected, IReadOnlyList<RemovalRecordResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var removed = results.Count(r => r.Outcome == "removed");
        var alreadyGone = results.Count(r => r.Outcome == "already-gone");
        var skipped = results.Count(r => r.Outcome == "skipped");
        var failed = results.Count(r => r.Outcome == "failed");
        var reported = results.Count <= RemovalLimits.MaxReported
            ? results
            : results.OrderBy(r => r.Outcome == "failed" ? 0 : r.Outcome == "skipped" ? 1 : 2).Take(RemovalLimits.MaxReported).ToList();
        return new RemovalSummary(scope, selected, removed, alreadyGone, skipped, failed, reported, results.Count > reported.Count);
    }

    /// <summary>The one line the activity trail carries for the whole removal.</summary>
    public string Describe()
    {
        var what = Scope switch
        {
            RemovalScope.Record => "removed from OSDU (reversible)",
            RemovalScope.History => "earlier versions purged",
            RemovalScope.Everything => "purged from OSDU with every version",
            _ => throw new InvalidOperationException($"Unknown removal scope '{Scope}'."),
        };
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Selected} record(s) selected: {Removed} {what}, {AlreadyGone} already gone, {Skipped} skipped, {Failed} failed");
    }
}
