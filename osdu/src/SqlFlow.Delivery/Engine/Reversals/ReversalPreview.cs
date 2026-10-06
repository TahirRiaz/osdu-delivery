using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine.Reversals;

/// <summary>
/// What reversing a source would reach, with nothing written (docs/reversal-plan.md): the submissions it covers, how many
/// records it delivered, and for a sample of them what the reversal would do, decided by <see cref="ReversalPlan"/> exactly
/// as the run decides each record. Read by the API's preview and by <c>sqlflow records reverse --preview</c> alike.
/// </summary>
/// <param name="Source">What would be reversed.</param>
/// <param name="Submissions">The submissions the source covers.</param>
/// <param name="Records">The records the source delivered.</param>
/// <param name="Sampled">How many of them the sample decided.</param>
/// <param name="Restore">Of the sample, the records that would be given back the version OSDU held before.</param>
/// <param name="Remove">Of the sample, the records that would be removed again, the source having created them.</param>
/// <param name="ResolvedFromOsdu">Of the sample, the records whose earlier version OSDU's version list would decide, the ledger no longer saying.</param>
/// <param name="PassedOver">Of the sample, the records that would be passed over, by why (<see cref="ReversalOutcomes"/>).</param>
/// <param name="Route">What the flow's route can do, for records of the kind its mapping renders.</param>
/// <param name="Existing">The reversal of this source, when one was asked for.</param>
/// <param name="ExistingCounts">That reversal's records counted by state and outcome.</param>
public sealed record ReversalPreview(
    ReversalSource Source, IReadOnlyList<Guid> Submissions, long Records, int Sampled, int Restore, int Remove, int ResolvedFromOsdu,
    IReadOnlyDictionary<string, int> PassedOver, ReversalRoute Route, ReversalState? Existing, ReversalCounts? ExistingCounts)
{
    /// <summary>Whether the sample is every record the source delivered.</summary>
    public bool SampleIsAll => Sampled >= Records;

    /// <summary>
    /// Reads what reversing <paramref name="source"/> in <paramref name="flow"/>'s ledger would reach, deciding up to
    /// <paramref name="sample"/> of its records; <paramref name="kind"/> is the kind the flow's mapping renders, when known.
    /// </summary>
    /// <exception cref="DeliveryException">The source is not the ledger's: a submission of another ledger, or a run that neither planned nor delivered anything of it.</exception>
    public static async Task<ReversalPreview> ReadAsync(ILedger ledger, FlowDefinition flow, ReversalSource source, string? kind, int sample, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(source);
        var read = await ledger.ReadReversalSourceAsync(flow.Id, flow.Label, source, Math.Clamp(sample, 0, ReversalLimits.PreviewSample), ct).ConfigureAwait(false);
        var records = await ledger.GetRecordsAsync(flow.Id, read.Sample.Select(s => s.DeliveryKey), ct).ConfigureAwait(false);
        var restore = 0;
        var remove = 0;
        var resolve = 0;
        var passed = new Dictionary<string, int>(StringComparer.Ordinal);
        var routes = new Dictionary<string, ReversalRoute>(StringComparer.Ordinal);
        foreach (var item in read.Sample)
        {
            records.TryGetValue(item.DeliveryKey, out var record);
            var route = item.TargetId is { } id ? RouteOf(routes, flow, id) : ReversalRoute.Of(flow, null);
            var step = ReversalPlan.Decide(item, record, route);
            switch (step.Action)
            {
                case ReversalAction.Restore:
                    restore++;
                    break;
                case ReversalAction.Remove:
                    remove++;
                    break;
                case ReversalAction.ResolvePrior:
                    resolve++;
                    break;
                default:
                    passed[step.Outcome!] = passed.GetValueOrDefault(step.Outcome!) + 1;
                    break;
            }
        }

        var existing = await ledger.FindReversalAsync(flow.Id, source, ct).ConfigureAwait(false);
        var counts = existing is null ? null : await ledger.CountReversalAsync(existing.ReversalId, ct).ConfigureAwait(false);
        // The route for records of the kind the mapping renders, or without it for the records the sample holds.
        var described = kind is null && read.Sample.FirstOrDefault(s => s.TargetId is not null)?.TargetId is { } sampled
            ? RouteOf(routes, flow, sampled)
            : ReversalRoute.Of(flow, kind);
        return new ReversalPreview(source, read.Submissions, read.Records, read.Sample.Count, restore, remove, resolve, passed, described, existing, counts);
    }

    private static ReversalRoute RouteOf(Dictionary<string, ReversalRoute> routes, FlowDefinition flow, string targetId)
    {
        var entityType = DdmsRouting.EntityTypeOf(targetId) ?? string.Empty;
        if (!routes.TryGetValue(entityType, out var route))
        {
            route = ReversalRoute.ForRecord(flow, targetId);
            routes[entityType] = route;
        }

        return route;
    }
}
