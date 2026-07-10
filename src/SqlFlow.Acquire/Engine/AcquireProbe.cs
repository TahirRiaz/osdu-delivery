namespace SqlFlow.Acquire.Engine;

/// <summary>
/// A per-page capture sink for the debugger: it receives, for every request the engine issues, the resolved method
/// and URL, the (redacted) request headers, the response status, headers, timing, size, record count, and a bounded
/// body preview. The normal run path passes no probe; the control-plane test invoke passes one to render the
/// request/response inspector without landing anything to the lake.
/// </summary>
public interface IAcquireProbe
{
    void Page(AcquirePageProbe probe);
}

/// <summary>One captured request/response for the debugger.</summary>
public sealed record AcquirePageProbe
{
    public required int Iteration { get; init; }
    public required int Page { get; init; }
    public required string Method { get; init; }
    public required string Url { get; init; }
    public required IReadOnlyDictionary<string, string> RequestHeaders { get; init; }
    public required int Status { get; init; }
    public required IReadOnlyDictionary<string, string> ResponseHeaders { get; init; }
    public required string? ContentType { get; init; }
    public required long Bytes { get; init; }
    public required int RecordCount { get; init; }
    public required double DurationMs { get; init; }
    public required string BodyPreview { get; init; }
    public string? LandedTo { get; init; }
}

/// <summary>Collects probes into a list; the control plane serializes them into the debug response.</summary>
public sealed class CollectingProbe : IAcquireProbe
{
    private readonly List<AcquirePageProbe> _pages = [];

    public IReadOnlyList<AcquirePageProbe> Pages => _pages;

    public void Page(AcquirePageProbe probe) => _pages.Add(probe);
}
