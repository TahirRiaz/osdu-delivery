using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine.Inventories;

/// <summary>
/// Counts the requests a match sends to the search service (its plain search, its cursor search and the release of a cursor),
/// each request once however many attempts it took, and hands every attempt and retry on to the run's own observer, so the
/// match can say what it cost and the run's live trace still shows each call. Token requests and anything else the stack sends
/// are not counted.
/// </summary>
internal sealed class SearchRequestCounter(InventorySource source, IHttpObserver? inner) : IHttpObserver
{
    private readonly string _queryPath = source.QueryPath.TrimEnd('/');
    private readonly string _cursorPath = source.SearchPath.TrimEnd('/');
    private long _sent;
    private long _taken;

    /// <summary>Every search request sent so far.</summary>
    public long Sent => Interlocked.Read(ref _sent);

    /// <summary>The search requests sent since the last take, which a write of the match adds to its count.</summary>
    public long Take()
    {
        var sent = Interlocked.Read(ref _sent);
        var taken = Interlocked.Exchange(ref _taken, sent);
        return Math.Max(0, sent - taken);
    }

    public void Ended(HttpAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (attempt.Attempt == 1 && IsSearch(attempt.Url))
        {
            Interlocked.Increment(ref _sent);
        }

        inner?.Ended(attempt);
    }

    public void Retrying(HttpRetry retry) => inner?.Retrying(retry);

    private bool IsSearch(string url)
    {
        var path = (Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed.AbsolutePath : url).TrimEnd('/');
        return path.EndsWith(_queryPath, StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(_cursorPath, StringComparison.OrdinalIgnoreCase)
            || path.Contains(_cursorPath + "/", StringComparison.OrdinalIgnoreCase);
    }
}
