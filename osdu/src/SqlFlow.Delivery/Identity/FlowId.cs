namespace SqlFlow.Delivery.Identity;

/// <summary>
/// A stable id derived deterministically from the flow name (SQLFlow pattern, design.md section 12.1), so runs key
/// consistently without a database assigning one.
/// </summary>
public static class FlowId
{
    private static readonly Guid FlowNamespace = DeterministicGuid.Namespace("flow");

    // A namespace of its own, so no flow name, whatever it is spelled with, derives the id of another flow's partition.
    private static readonly Guid PartitionNamespace = DeterministicGuid.Namespace("flow-partition");

    public static Guid Of(string flowName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        return DeterministicGuid.V5(FlowNamespace, flowName.Trim().ToLowerInvariant());
    }

    /// <summary>
    /// The ledger identity of a flow delivering to one of the partitions it names (docs/partitions-design.md section 4):
    /// derived from the ledger name and the partition together, so each partition keeps a ledger of its own.
    /// </summary>
    public static Guid Of(string ledgerName, string partition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ledgerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        return DeterministicGuid.V5(PartitionNamespace, ledgerName.Trim().ToLowerInvariant() + "\n" + partition.Trim().ToLowerInvariant());
    }
}
