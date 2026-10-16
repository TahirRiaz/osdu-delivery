namespace SqlFlow.Delivery;

/// <summary>
/// An OSDU service answered with a status the retry policy will not retry again. It carries the status code, so a
/// protocol can decide per request whether the failure is the record's (a 400 on one document of a batch) or the run's,
/// and the wait the service asked for in its <c>Retry-After</c> header, so a record's next attempt honours it rather
/// than coming back sooner than the service said it may.
/// </summary>
public sealed class OsduStatusException : DeliveryException
{
    public OsduStatusException(int statusCode, string message, TimeSpan? retryAfter = null)
        : base(message)
    {
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    public OsduStatusException(int statusCode, string message, Exception innerException)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    /// <summary>The HTTP status code the service answered with.</summary>
    public int StatusCode { get; }

    /// <summary>How long the service asked the caller to wait before trying again, when it said so; null otherwise.</summary>
    public TimeSpan? RetryAfter { get; }
}
