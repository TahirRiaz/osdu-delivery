namespace SqlFlow.Delivery.Http;

/// <summary>
/// The correlation id the OSDU requests of one unit of work carry in the <c>correlation-id</c> header, so a ledger
/// attempt can be matched to the services' own logs. None of the storage, search, file, legal, workflow or wellbore
/// DDMS OpenAPI descriptions declares the header (the policy service's does), but the storage service answers a request
/// with the id it was sent, and one that was sent none with an id of its own; either way a failed call quotes the id the
/// service answered with.
/// </summary>
public static class OsduCorrelation
{
    public const string HeaderName = "correlation-id";

    internal static readonly AsyncLocal<string?> Ambient = new();

    /// <summary>The id of the unit of work in progress on this flow of execution, or null outside one.</summary>
    public static string? Current => Ambient.Value;

    /// <summary>Starts a unit of work: every OSDU request sent until the scope is disposed carries a new id.</summary>
    public static OsduCorrelationScope Begin() => new(Guid.NewGuid().ToString("D"));
}

/// <summary>One unit of work's correlation id; disposing it restores the enclosing unit's.</summary>
public sealed class OsduCorrelationScope : IDisposable
{
    private readonly string? _previous;

    internal OsduCorrelationScope(string id)
    {
        _previous = OsduCorrelation.Ambient.Value;
        Id = id;
        OsduCorrelation.Ambient.Value = id;
    }

    public string Id { get; }

    public void Dispose() => OsduCorrelation.Ambient.Value = _previous;
}
