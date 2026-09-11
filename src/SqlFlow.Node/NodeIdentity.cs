namespace SqlFlow.Node;

/// <summary>
/// The name a compute node registers, claims runs and recovers its orphans under.
/// </summary>
/// <remarks>
/// It defaults to the machine name, which is unique per replica in an orchestrated fleet, where every pod has a name of
/// its own. Two node processes on one host, such as a control plane hosting its in-process worker beside a standalone
/// <c>sqlflow worker</c>, or two workers on one VM, need a name each. Sharing one, they share a fleet row, and the startup
/// recovery of either requeues the runs the other is executing, because recovery takes every run left running under
/// its name to be an orphan of its own previous incarnation.
/// </remarks>
public sealed record NodeIdentity
{
    /// <summary>The environment variable a node takes its name from when none is configured.</summary>
    public const string EnvironmentVariable = "SQLFLOW_NODE_NAME";

    /// <summary>The longest name the catalog holds: <c>[catalog].[Node].[Name]</c> and <c>[catalog].[Run].[ClaimedByNode]</c>.</summary>
    public const int MaxLength = 256;

    private NodeIdentity(string name) => Name = name;

    /// <summary>The name, trimmed.</summary>
    public string Name { get; }

    /// <summary>
    /// The configured name when one is given, else <see cref="EnvironmentVariable"/>, else the machine name. A name
    /// longer than <see cref="MaxLength"/> or holding a control character is refused, naming the rule, because the
    /// catalog could not store it and a log line could not show it.
    /// </summary>
    public static NodeIdentity Resolve(string? configured)
    {
        var candidate = string.IsNullOrWhiteSpace(configured) ? Environment.GetEnvironmentVariable(EnvironmentVariable) : configured;
        var name = string.IsNullOrWhiteSpace(candidate) ? Environment.MachineName : candidate.Trim();
        if (name.Length > MaxLength)
        {
            throw new ArgumentException(
                $"A node name is at most {MaxLength} characters, because it keys [catalog].[Node] and is stamped on [catalog].[Run].ClaimedByNode; the one given is {name.Length}.",
                nameof(configured));
        }

        if (name.Any(char.IsControl))
        {
            throw new ArgumentException("A node name cannot contain control characters.", nameof(configured));
        }

        return new NodeIdentity(name);
    }
}
