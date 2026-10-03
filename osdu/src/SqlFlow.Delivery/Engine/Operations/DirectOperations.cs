using SqlFlow.Execution;

namespace SqlFlow.Delivery.Engine.Operations;

/// <summary>
/// The operations a person asks of a flow and waits on, run in the process that is asked rather than queued for a node: a
/// read of OSDU (a record, its versions, the explorer's searches), a probe of the target, a read of the ingestion tables (a
/// record's rows, a scope's values), and one record's preview. Each is the same operation a node would run, given the same
/// arguments (the flow file, the interface, the partition, the central configuration), so it reaches the same target with
/// the same credentials and answers the same thing; only the queue and the wait on it are gone.
/// </summary>
/// <remarks>
/// What runs long or writes stays a node task: a value check, which may render every row of a scope, and a removal, which
/// writes to OSDU and the ledger for any number of records and has to finish whatever happens to the page that asked.
/// </remarks>
public sealed class DirectOperations
{
    private readonly Dictionary<string, IComputeOperation> _byName;

    public DirectOperations(IEnumerable<DeliveryOperation> operations)
        : this((IEnumerable<IComputeOperation>)operations)
    {
    }

    /// <summary>The operations given, whatever implements them: the tests' stand-ins, which record what they were given.</summary>
    internal DirectOperations(IEnumerable<IComputeOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        _byName = operations.ToDictionary(o => o.Name, StringComparer.Ordinal);
    }

    /// <summary>The names of the operations run in process.</summary>
    public IReadOnlyCollection<string> Names => _byName.Keys;

    /// <summary>The operation <paramref name="name"/> names.</summary>
    /// <exception cref="InvalidOperationException">No operation of that name runs in process.</exception>
    public IComputeOperation Get(string name)
        => _byName.TryGetValue(name, out var operation)
            ? operation
            : throw new InvalidOperationException($"'{name}' is not an operation run in process; those are {string.Join(", ", _byName.Keys.Order(StringComparer.Ordinal))}.");
}
