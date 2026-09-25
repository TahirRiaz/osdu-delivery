namespace SqlFlow.Delivery.Http;

/// <summary>
/// One attempt of an HTTP request as the stack sent it: the method, the URL without its query string (a signed upload URL
/// carries its credential there), the status the service answered with or, when none came, what went wrong, and how long
/// the attempt took.
/// </summary>
/// <param name="Method">The request method.</param>
/// <param name="Url">The URL the attempt ended at, without its query string.</param>
/// <param name="Attempt">The 1-based attempt of the request.</param>
/// <param name="Status">The status the service answered with; null when the attempt ended without one.</param>
/// <param name="Failure">What ended the attempt without a status (a transport failure, a timeout); null when a status came.</param>
/// <param name="Elapsed">How long the attempt took, its response body included.</param>
public sealed record HttpAttempt(string Method, string Url, int Attempt, int? Status, string? Failure, TimeSpan Elapsed);

/// <summary>A request the stack is about to send again, after the wait it decided on.</summary>
/// <param name="Method">The request method.</param>
/// <param name="Url">The URL of the attempt that failed, without its query string.</param>
/// <param name="Attempt">The 1-based attempt that failed.</param>
/// <param name="MaxAttempts">The most attempts the flow's retry policy makes of one request.</param>
/// <param name="Cause">Why the attempt is repeated: the status the service answered, or the transport failure.</param>
/// <param name="Wait">How long the stack waits before the next attempt.</param>
public sealed record HttpRetry(string Method, string Url, int Attempt, int MaxAttempts, string Cause, TimeSpan Wait);

/// <summary>
/// Watches what an HTTP stack sends: every attempt as it ends and every retry before its wait. A run hands one to the
/// stack it delivers through, so its live trace can say which calls a record made and why a request is taking longer
/// than it should. Called on the sending thread, in order; implementations must be cheap and must not throw.
/// </summary>
public interface IHttpObserver
{
    void Ended(HttpAttempt attempt);

    void Retrying(HttpRetry retry);
}
