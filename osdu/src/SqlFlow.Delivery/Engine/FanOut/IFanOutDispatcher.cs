using SqlFlow.Core.Runs;

namespace SqlFlow.Delivery.Engine.FanOut;

/// <summary>The member runs a fan-out produced, as one run group.</summary>
public sealed record FanOutHandle(Guid GroupId, IReadOnlyList<Guid> RunIds);

/// <summary>One member run of a fan-out as the catalog sees it.</summary>
public sealed record FanOutMemberState(Guid RunId, int Slot, string Status, string? Error, string? ResultJson)
{
    public bool IsTerminal => Status is "succeeded" or "failed" or "cancelled" or "skipped";

    public bool Succeeded => Status == "succeeded";
}

/// <summary>The state of a fan-out: every member and the counts a coordinator logs while it waits.</summary>
public sealed record FanOutState(IReadOnlyList<FanOutMemberState> Members)
{
    public bool AllTerminal => Members.All(m => m.IsTerminal);

    public int Queued => Members.Count(m => m.Status == "queued");

    public int Running => Members.Count(m => m.Status == "running");

    public int Succeeded => Members.Count(m => m.Succeeded);

    public int Failed => Members.Count(m => m.IsTerminal && !m.Succeeded);

    public override string ToString() => $"{Members.Count} member(s): {Succeeded} succeeded, {Failed} failed or cancelled, {Running} running, {Queued} queued";
}

/// <summary>
/// How a deliver run spreads its work across the fleet (design.md section 16.4): it asks the platform to enqueue
/// member runs of its own flow (intake partitions, then drains), reads their state while it waits, and takes them
/// with it when it is cancelled. A seam: the catalog implements it over the run queue; tests and hosts without a
/// catalog get one that is not available, and the run does everything itself.
/// </summary>
public interface IFanOutDispatcher
{
    /// <summary>False when this host cannot enqueue runs (no catalog); the run then works alone.</summary>
    bool Available { get; }

    /// <summary>Enqueues one member run per parameter set under the root run; rejoins members that already exist and are not finished.</summary>
    Task<FanOutHandle> EnqueueAsync(Guid rootRunId, string operation, IReadOnlyList<RunParameters> members, CancellationToken ct = default);

    Task<FanOutState> StateAsync(FanOutHandle handle, CancellationToken ct = default);

    Task CancelAsync(FanOutHandle handle, CancellationToken ct = default);
}

/// <summary>The dispatcher of a host without a catalog: never available.</summary>
public sealed class NoFanOutDispatcher : IFanOutDispatcher
{
    public static NoFanOutDispatcher Instance { get; } = new();

    public bool Available => false;

    public Task<FanOutHandle> EnqueueAsync(Guid rootRunId, string operation, IReadOnlyList<RunParameters> members, CancellationToken ct = default)
        => throw new DeliveryException("This host has no catalog connection, so a run cannot fan out.");

    public Task<FanOutState> StateAsync(FanOutHandle handle, CancellationToken ct = default)
        => throw new DeliveryException("This host has no catalog connection, so a run cannot fan out.");

    public Task CancelAsync(FanOutHandle handle, CancellationToken ct = default)
        => throw new DeliveryException("This host has no catalog connection, so a run cannot fan out.");
}
