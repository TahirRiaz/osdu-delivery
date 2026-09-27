using System.Text.RegularExpressions;

namespace SqlFlow.Delivery.Model;

/// <summary>
/// A delivery flow document as a source (docs/interfaces-design.md): one source system and the interfaces it delivers,
/// each one OSDU type with its own record table, mapping, route and ledger identity, all of them sharing the document's
/// connection, target, parameters and schedule. A document in the single form (no <c>interfaces</c>) is a source with one
/// interface whose ledger identity is the flow's own name, so a flow written before interfaces existed reads and runs as
/// it always did.
/// </summary>
public sealed partial record SourceDefinition
{
    /// <summary>The most interfaces one document declares.</summary>
    public const int MaxInterfaces = 200;

    /// <summary>How many interfaces with nothing left to wait for run at once, unless the document says otherwise.</summary>
    public const int DefaultParallelInterfaces = 4;

    /// <summary>The most interfaces a document lets run at once.</summary>
    public const int MaxParallelInterfaces = 32;

    /// <summary>The longest interface name.</summary>
    public const int MaxInterfaceNameLength = 64;

    /// <summary>Path of the file the source was loaded from, for error messages. Null for inline documents.</summary>
    public string? SourcePath { get; init; }

    /// <summary>The flow's name: its pipeline in the catalog, its schedules and its run history.</summary>
    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>The platform batch the flow belongs to, from the envelope.</summary>
    public string? Batch { get; init; }

    /// <summary>True when the document lists its interfaces under <c>interfaces</c>; false for the single form.</summary>
    public bool DeclaresInterfaces { get; init; }

    /// <summary>
    /// The interfaces in document order. The single form holds exactly one, whose <see cref="FlowDefinition.Interface"/> is
    /// null.
    /// </summary>
    public required IReadOnlyList<FlowDefinition> Interfaces { get; init; }

    /// <summary>How many interfaces with nothing left to wait for run at once (<c>reliability.parallelInterfaces</c>).</summary>
    public int ParallelInterfaces { get; init; } = DefaultParallelInterfaces;

    /// <summary>The first interface: every interface carries the source's shared connection, target and parameters.</summary>
    public FlowDefinition First => Interfaces[0];

    /// <summary>The parameters the document declares, shared by every interface.</summary>
    public IReadOnlyDictionary<string, FlowParameter> Parameters => First.Parameters;

    /// <summary>The connection reference of the ingestion database every interface reads.</summary>
    public string Connection => First.Source.Connection;

    /// <summary>The OSDU endpoint reference every interface delivers to.</summary>
    public string Endpoint => First.Target.Endpoint;

    /// <summary>The secret references the document declares; every interface shares the connection and the target.</summary>
    public IEnumerable<KeyValuePair<string, string>> CredentialReferences() => First.CredentialReferences();

    /// <summary>The source a document in the single form is: its one flow, under the flow's own name.</summary>
    public static SourceDefinition Of(FlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (flow.Interface is not null)
        {
            throw new ArgumentException($"Interface '{flow.Interface}' belongs to a source that declares its interfaces; it is not a document of its own.", nameof(flow));
        }

        return new SourceDefinition { SourcePath = flow.SourcePath, Name = flow.Name, Description = flow.Description, Batch = flow.Batch, Interfaces = [flow] };
    }

    /// <summary>True when <paramref name="name"/> is a valid interface name: a letter, then letters, digits, '_' and '-'.</summary>
    public static bool IsInterfaceName(string name)
        => !string.IsNullOrEmpty(name) && name.Length <= MaxInterfaceNameLength && InterfaceName().IsMatch(name);

    /// <summary>The same source, loaded from <paramref name="path"/>: the path every interface resolves its files against.</summary>
    public SourceDefinition WithSourcePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return this with { SourcePath = path, Interfaces = Interfaces.Select(i => i with { SourcePath = path }).ToList() };
    }

    /// <summary>
    /// The interface named <paramref name="name"/>, compared ignoring case. A document in the single form has one interface
    /// and answers to no name at all; a document with interfaces answers to no name only when it declares exactly one.
    /// </summary>
    public FlowDefinition Interface(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Interfaces.Count == 1
                ? First
                : throw new DeliveryException(
                    $"Flow '{Name}' delivers {Interfaces.Count} interfaces ({string.Join(", ", Names)}); name the one this operation is for.");
        }

        var wanted = name.Trim();
        if (!DeclaresInterfaces)
        {
            throw new DeliveryException($"Flow '{Name}' declares no interfaces, so it has no interface '{wanted}'.");
        }

        return Interfaces.FirstOrDefault(i => string.Equals(i.Interface, wanted, StringComparison.OrdinalIgnoreCase))
            ?? throw new DeliveryException($"Flow '{Name}' has no interface '{wanted}'; it declares {string.Join(", ", Names)}.");
    }

    /// <summary>
    /// The partitions the source may deliver to (<c>partitions</c>), shared by every interface; empty for a source that
    /// names none.
    /// </summary>
    public IReadOnlyList<DeclaredPartition> Partitions => First.Partitions;

    /// <summary>True when the source names the partitions it may deliver to.</summary>
    public bool DeclaresPartitions => First.DeclaresPartitions;

    /// <summary>The partition every interface is bound to (<see cref="ForPartition"/>), or null.</summary>
    public string? Partition => First.Partition;

    /// <summary>
    /// The source bound to the partition a run or a request targets (docs/partitions-design.md section 3): every interface
    /// bound to it. A source that names no partitions takes none and is returned as it is. One that names a single partition
    /// is bound to it when <paramref name="partition"/> is null; one that names several has to be told which.
    /// </summary>
    /// <exception cref="DeliveryException">
    /// A partition is named for a source that names none, the source names several and none is named, or the one named is
    /// not among them.
    /// </exception>
    public SourceDefinition ForPartition(string? partition)
    {
        var wanted = string.IsNullOrWhiteSpace(partition) ? null : partition.Trim();
        if (!DeclaresPartitions)
        {
            return wanted is null
                ? this
                : throw new DeliveryException(
                    $"Flow '{Name}' names no partitions; it delivers to the partition its target.headers name, so a run or request cannot target '{wanted}'. Leave the partition out, or name the flow's partitions under 'partitions'.");
        }

        if (wanted is null)
        {
            wanted = Partitions.Count == 1
                ? Partitions[0].Name
                : throw new DeliveryException(
                    $"Flow '{Name}' delivers to {Partitions.Count} partitions ({PartitionNames.Listed(Partitions.Select(p => p.Name))}); name the one this run or request targets.");
        }

        return this with { Interfaces = Interfaces.Select(i => i.ForPartition(wanted)).ToList() };
    }

    /// <summary>
    /// The interface whose ledger identity is <paramref name="flowId"/>, or null when none of them has it. A source that
    /// names its partitions and is not yet bound is looked up in every one of them, and the interface found comes bound to
    /// the partition whose ledger it is.
    /// </summary>
    public FlowDefinition? ByFlowId(Guid flowId)
    {
        if (!DeclaresPartitions || Partition is not null)
        {
            return Interfaces.FirstOrDefault(i => i.Id == flowId);
        }

        foreach (var partition in Partitions)
        {
            var bound = Interfaces.Select(i => i.ForPartition(partition.Name)).FirstOrDefault(i => i.Id == flowId);
            if (bound is not null)
            {
                return bound;
            }
        }

        return null;
    }

    /// <summary>
    /// Every interface of the source for every partition it names, bound; the interfaces as they are for a source that names
    /// none. What a read model that keeps one row per ledger enumerates.
    /// </summary>
    public IEnumerable<FlowDefinition> EveryLedger()
        => DeclaresPartitions && Partition is null
            ? Partitions.SelectMany(p => Interfaces.Select(i => i.ForPartition(p.Name)))
            : Interfaces;

    /// <summary>
    /// The interfaces <paramref name="names"/> selects, in document order: every interface when the list is empty. Every
    /// name must be one of the source's interfaces.
    /// </summary>
    public IReadOnlyList<FlowDefinition> Select(IReadOnlyCollection<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count == 0)
        {
            return Interfaces;
        }

        if (!DeclaresInterfaces)
        {
            throw new DeliveryException($"Flow '{Name}' declares no interfaces, so a run cannot select {string.Join(", ", names)}.");
        }

        var unknown = names.Where(n => !Interfaces.Any(i => string.Equals(i.Interface, n, StringComparison.OrdinalIgnoreCase))).ToList();
        if (unknown.Count > 0)
        {
            throw new DeliveryException($"Flow '{Name}' has no interface {string.Join(", ", unknown.Select(u => $"'{u}'"))}; it declares {string.Join(", ", Names)}.");
        }

        return Interfaces.Where(i => names.Contains(i.Interface!, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>The interface names, in document order; the single form has none.</summary>
    public IEnumerable<string> Names => Interfaces.Select(i => i.Interface).OfType<string>();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex InterfaceName();
}

/// <summary>
/// When record failures turn into the failure of their interface (docs/interfaces-design.md section 8.2). A record with a
/// data problem is held and the interface goes on; these thresholds say when so many records fail that the interface as a
/// whole has a problem, and an outage (the same connection or permission failure on record after record) stops it without
/// waiting for any threshold the document sets.
/// </summary>
public sealed record FlowFailWhen
{
    /// <summary>Consecutive connection or permission failures that stop an interface unless the document says otherwise.</summary>
    public const int DefaultOutageFailures = 25;

    /// <summary>The settled records a percentage is judged over, at least, unless the document says otherwise.</summary>
    public const int DefaultMinRecords = 100;

    /// <summary>
    /// Held and failed records as a percentage of the records a run settled, at or above which the interface stops (from
    /// above 0 up to 100). Null never stops on a percentage.
    /// </summary>
    public double? FailedPercent { get; init; }

    /// <summary>How many records a run settles before <see cref="FailedPercent"/> is judged.</summary>
    public int MinRecords { get; init; } = DefaultMinRecords;

    /// <summary>
    /// Consecutive failures of one error class (data, connection or permission) at or above which the interface stops.
    /// Null stops only on the outage rule.
    /// </summary>
    public int? ConsecutiveFailures { get; init; }

    /// <summary>Consecutive connection or permission failures that stop the interface as an outage; 0 turns the rule off.</summary>
    public int OutageFailures { get; init; } = DefaultOutageFailures;
}
