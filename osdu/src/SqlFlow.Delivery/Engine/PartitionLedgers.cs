using System.Collections.Concurrent;
using System.Globalization;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// The guards that keep a flow's ledgers apart once it names its partitions (docs/partitions-design.md section 4). A flow
/// that delivered before it named its partitions kept its records in its own ledger; naming them gives every partition but
/// the one marked <c>keepLedger</c> a new ledger. Two mistakes would then deliver records again or mix them, and a run is
/// refused before it starts rather than let either happen:
/// <list type="bullet">
/// <item>no partition keeps the flow's own ledger, while it holds records delivered to a partition the flow still names:
/// that partition's next run would find an empty ledger and deliver every one of them again as new;</item>
/// <item>the partition that keeps it finds records in it delivered to another partition: its runs would treat them as its
/// own.</item>
/// </list>
/// Once a flow is bound to its partitions no run adds a record of another partition to a ledger, so a check that passed
/// stays passed. The host keeps one instance, which remembers what passed, so each check reads the ledger once per flow and
/// partition for the life of the process, not on every run.
/// </summary>
public sealed class PartitionLedgers
{
    private readonly ConcurrentDictionary<string, bool> _passed = new(StringComparer.Ordinal);

    /// <summary>Refuses a run of <paramref name="bound"/>, a source bound to one partition, when its ledgers would be lost or mixed.</summary>
    /// <exception cref="DeliveryException">A guard does not hold; the message names the records and what to change.</exception>
    public async Task CheckAsync(ILedger ledger, SourceDefinition bound, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(bound);
        if (!bound.DeclaresPartitions || bound.Partition is null)
        {
            return;
        }

        var keeping = bound.Partitions.FirstOrDefault(p => p.KeepsLedger)?.Name;
        var declared = bound.Partitions.Select(p => p.Name).ToList();
        foreach (var flow in bound.Interfaces)
        {
            var own = FlowId.Of(flow.OwnLedgerName);
            if (keeping is null)
            {
                await CheckUnkeptAsync(ledger, flow, own, declared, ct).ConfigureAwait(false);
            }
            else if (flow.KeepsOwnLedger)
            {
                await CheckKeptAsync(ledger, flow, own, keeping, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task CheckUnkeptAsync(ILedger ledger, FlowDefinition flow, Guid own, IReadOnlyList<string> declared, CancellationToken ct)
    {
        var key = $"unkept|{own:N}|{string.Join(",", declared.Select(d => d.ToLowerInvariant()))}";
        if (_passed.ContainsKey(key))
        {
            return;
        }

        if (await ledger.HoldsRecordsAsync(own, ct).ConfigureAwait(false))
        {
            var delivered = await ledger.DeliveredPartitionsAsync(own, ct).ConfigureAwait(false);
            var reached = delivered.Where(d => declared.Contains(d.Partition, StringComparer.OrdinalIgnoreCase)).ToList();
            if (reached.Count > 0)
            {
                throw new DeliveryException(
                    $"Flow '{flow.Label}' names the partitions {PartitionNames.Listed(declared)}, and no partition keeps the ledger '{flow.OwnLedgerName}' it kept before: "
                    + $"that ledger holds {Describe(reached)}. A partition that keeps a ledger of its own starts it empty, so its next run would deliver those records again as new. "
                    + $"Mark the partition they were delivered to with keepLedger: true under partitions ({reached[0].Partition}). Nothing ran.");
            }
        }

        _passed.TryAdd(key, true);
    }

    private async Task CheckKeptAsync(ILedger ledger, FlowDefinition flow, Guid own, string keeping, CancellationToken ct)
    {
        var key = $"kept|{own:N}|{keeping.ToLowerInvariant()}";
        if (_passed.ContainsKey(key))
        {
            return;
        }

        var delivered = await ledger.DeliveredPartitionsAsync(own, ct).ConfigureAwait(false);
        var foreign = delivered.Where(d => !string.Equals(d.Partition, keeping, StringComparison.OrdinalIgnoreCase)).ToList();
        if (foreign.Count > 0)
        {
            throw new DeliveryException(
                $"Partition '{keeping}' keeps the ledger '{flow.OwnLedgerName}' of flow '{flow.Label}', and that ledger holds {Describe(foreign)}, "
                + $"delivered while the flow named no partitions and took its partition from the environment. A run of '{keeping}' would treat them as its own. "
                + "Keep the ledger for the partition its records went to, and deliver the others again from their own partition's ledger. Nothing ran.");
        }

        _passed.TryAdd(key, true);
    }

    private static string Describe(IReadOnlyList<PartitionRecords> partitions)
        => string.Join(", ", partitions.Select(p => string.Create(
            CultureInfo.InvariantCulture, $"{p.Records:N0} record{(p.Records == 1 ? string.Empty : "s")} delivered to '{p.Partition}'")));
}
