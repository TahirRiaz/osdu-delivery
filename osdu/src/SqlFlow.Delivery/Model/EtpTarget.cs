namespace SqlFlow.Delivery.Model;

/// <summary>
/// The Reservoir DDMS behind a flow's target (<c>target.etp</c>): where its ETP WebSocket answers under the endpoint,
/// the dataspace its records go into, and what one delivery works under
/// (osdu/specs/reservoir-ddms/INTEGRATION.md sections 1.1, 4.3, 5.4 and 8.6).
/// </summary>
public sealed record EtpTarget
{
    /// <summary>Where an OSDU deployment serves the ETP WebSocket (section 1.1).</summary>
    public const string DefaultPath = "/api/reservoir-ddms-etp/v2/";

    /// <summary>Objects per message, which is what the REST gateway of the same project sends (section 8.6).</summary>
    public const int DefaultObjectsPerMessage = 100;

    /// <summary>The server's own default wire limit, which a session narrows to whichever side allows less (section 8.6).</summary>
    public const long DefaultMaxMessageBytes = 16_000_000;

    /// <summary>The most bytes of one array a delivery reads into memory before it holds the record instead.</summary>
    public const long DefaultMaxArrayBytes = 256L * 1024 * 1024;

    /// <summary>Where the ETP WebSocket answers under the flow's endpoint.</summary>
    public string Path { get; init; } = DefaultPath;

    /// <summary>The dataspace records go into when their document names none; a two-level <c>project/study</c> path.</summary>
    public string? Dataspace { get; init; }

    /// <summary>Objects per message, which is also the batch the worker hands this route.</summary>
    public int ObjectsPerMessage { get; init; } = DefaultObjectsPerMessage;

    /// <summary>The largest message this side offers to send or accept; the server narrows it to its own maximum.</summary>
    public long MaxMessageBytes { get; init; } = DefaultMaxMessageBytes;

    /// <summary>The most bytes of one array a delivery reads into memory.</summary>
    public long MaxArrayBytes { get; init; } = DefaultMaxArrayBytes;

    /// <summary>
    /// Whether a delivery leaves the dataspace locked, which makes it read-only until the next delivery unlocks it
    /// (section 4.8). Off by default: a locked dataspace refuses every write, including this flow's next one.
    /// </summary>
    public bool Lock { get; init; }
}

/// <summary>
/// What marks a mapping as rendering an Energistics object rather than an OSDU record: the source segment of its
/// template's kind. An ETP object is no OSDU record, so only the etp route writes one, and the etp route writes
/// nothing else (osdu/specs/reservoir-ddms/INTEGRATION.md sections 5.1 and 6.2).
/// </summary>
public static class EtpKinds
{
    /// <summary>The source segment that marks an Energistics object template (<c>energistics:etp:obj_Grid2dRepresentation:2.0.1</c>).</summary>
    public const string Source = "etp";

    /// <summary>Whether <paramref name="kind"/> names an Energistics object template.</summary>
    public static bool Is(string? kind)
        => kind is not null && kind.Split(':') is { Length: 4 } parts && string.Equals(parts[1], Source, StringComparison.Ordinal);

    /// <summary>The Energistics type a kind names, or null when it is not one.</summary>
    public static string? TypeOf(string kind) => Is(kind) ? kind.Split(':')[2] : null;
}
