using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// Builds the search a flow's mapping answers its <c>search.</c> sources from. A seam like <see cref="IProtocolFactory"/>:
/// a host searches the platform the flow delivers to, and a suite that delivers to no platform answers from what it
/// was given.
/// </summary>
public interface IRecordSearchFactory
{
    /// <param name="flow">The flow whose mapping searches.</param>
    /// <param name="target">The flow's target, reached when the first question is asked and not before.</param>
    IRecordSearch Create(FlowDefinition flow, Func<CancellationToken, Task<OsduHttpClient>> target);
}

/// <summary>The search a host uses: the OSDU search service of the platform the flow delivers to.</summary>
public sealed class PlatformRecordSearchFactory : IRecordSearchFactory
{
    private readonly ILoggerFactory _loggers;

    public PlatformRecordSearchFactory(ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(loggers);
        _loggers = loggers;
    }

    public IRecordSearch Create(FlowDefinition flow, Func<CancellationToken, Task<OsduHttpClient>> target)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(target);
        return new OsduRecordSearch(target, _loggers.CreateLogger<OsduRecordSearch>());
    }
}

/// <summary>
/// Answers a run's search questions from the OSDU search service (openapi search v2, <c>POST /query</c>): one query per
/// question, sent under the flow's own target, auth and partition, asking for the ids of at most a handful of the
/// records that match exactly. One record is the answer; none is an answer too; several are named so the hold says
/// which records answer to the value.
/// </summary>
/// <remarks>
/// <para>
/// Answers are kept for the run, found or not, so each distinct question is asked once however many records ask it,
/// and a question two renderers ask at the same moment is sent once. At most <see cref="MaxConcurrentQueries"/> queries
/// are in flight at a time, so a batch of new values is asked in parallel without flooding the service.
/// </para>
/// <para>
/// A query the service refuses (400) is that question's answer, and the records asking it are held with the service's
/// words. Anything else that keeps an answer from coming (the service unreachable, the credentials refused, a failure
/// that outlasts the flow's retries, a body that is not a search result) fails the call, since taking a missing answer
/// for finding nothing would hold records for a reason that is not true.
/// </para>
/// </remarks>
public sealed class OsduRecordSearch : IRecordSearch, IDisposable
{
    /// <summary>The search the questions are asked of (openapi search v2).</summary>
    public const string QueryPath = "/api/search/v2/query";

    /// <summary>How many queries one run keeps in flight at a time.</summary>
    public const int MaxConcurrentQueries = 8;

    /// <summary>How much of a refusal's body a hold reason quotes.</summary>
    private const int RefusalPreview = 500;

    private static readonly IReadOnlySet<int> Refusals = new HashSet<int> { (int)HttpStatusCode.BadRequest };

    private readonly Func<CancellationToken, Task<OsduHttpClient>> _client;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<SearchQuestion, SearchAnswer> _answers = new();
    private readonly ConcurrentDictionary<SearchQuestion, Lazy<Task<SearchAnswer>>> _asking = new();
    private readonly SemaphoreSlim _slots = new(MaxConcurrentQueries, MaxConcurrentQueries);

    /// <param name="client">The flow's target, reached when the first question is asked and not before.</param>
    /// <param name="log">Where each round of questions is reported.</param>
    public OsduRecordSearch(Func<CancellationToken, Task<OsduHttpClient>> client, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(log);
        _client = client;
        _log = log;
    }

    public bool TryAnswer(SearchQuestion question, [NotNullWhen(true)] out SearchAnswer? answer)
    {
        ArgumentNullException.ThrowIfNull(question);
        return _answers.TryGetValue(question, out answer);
    }

    public async Task AnswerAsync(IReadOnlyCollection<SearchQuestion> questions, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(questions);
        var asked = questions.Where(q => !_answers.ContainsKey(q)).Distinct().ToList();
        if (asked.Count == 0)
        {
            return;
        }

        var answers = await Task.WhenAll(asked.Select(q => AnswerOnceAsync(q, ct))).ConfigureAwait(false);
        _log.LogInformation(
            "Searched the platform for {Count} value(s): {Found} found, {NotFound} not found, {Ambiguous} matching several records, {Refused} refused.",
            asked.Count,
            answers.Count(a => a.Outcome == SearchOutcome.Found),
            answers.Count(a => a.Outcome == SearchOutcome.NotFound),
            answers.Count(a => a.Outcome == SearchOutcome.Ambiguous),
            answers.Count(a => a.Outcome == SearchOutcome.Refused));
    }

    /// <summary>The answer to <paramref name="question"/>, asking it only when no other caller is asking it already.</summary>
    private async Task<SearchAnswer> AnswerOnceAsync(SearchQuestion question, CancellationToken ct)
    {
        if (_answers.TryGetValue(question, out var known))
        {
            return known;
        }

        var asking = _asking.GetOrAdd(question, q => new Lazy<Task<SearchAnswer>>(() => AskAsync(q, ct)));
        try
        {
            var answer = await asking.Value.WaitAsync(ct).ConfigureAwait(false);
            _answers.TryAdd(question, answer);
            return answer;
        }
        finally
        {
            // Settled either way: an answer is kept above, and a failure leaves the question to be asked again.
            _asking.TryRemove(KeyValuePair.Create(question, asking));
        }
    }

    private async Task<SearchAnswer> AskAsync(SearchQuestion question, CancellationToken ct)
    {
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var client = await _client(ct).ConfigureAwait(false);
            var body = new JsonObject
            {
                ["kind"] = question.Kind,
                ["query"] = question.Query,
                ["limit"] = SearchAnswer.NamedCandidates,
                ["returnedFields"] = new JsonArray("id"),
            };

            var result = await client.SendJsonAsync(HttpMethod.Post, client.Url(QueryPath), body, Refusals, ct, idempotent: true).ConfigureAwait(false);
            if (result.Status == HttpStatusCode.BadRequest)
            {
                var said = Preview(result.BodyText);
                _log.LogWarning("The search service refused the query {Query} on {Kind}: {Refusal}", question.Query, question.Kind, said);
                return SearchAnswer.Refused(said);
            }

            var answer = Read(question, result.BodyText);
            _log.LogDebug("Searched {Kind} with {Query}: {Answer}", question.Kind, question.Query, answer.Describe());
            return answer;
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>
    /// The answer a search result gives: its ids, checked to be records of the kind asked, and its count. A body that is
    /// not a search result, or names records of another kind, is a platform answering something else, and fails.
    /// </summary>
    internal static SearchAnswer Read(SearchQuestion question, string body)
    {
        JsonObject result;
        try
        {
            result = JsonNode.Parse(body) as JsonObject
                ?? throw new DeliveryException($"The search service answered the query {question.Query} on {question.Kind} with something that is not a JSON object: {Preview(body)}");
        }
        catch (JsonException ex)
        {
            throw new DeliveryException($"The search service answered the query {question.Query} on {question.Kind} with a body that is not JSON: {Preview(body)}", ex);
        }

        if (result["results"] is not JsonArray results)
        {
            throw new DeliveryException($"The search service answered the query {question.Query} on {question.Kind} without a 'results' list: {Preview(body)}");
        }

        var entityType = question.Kind.Split(':')[2];
        var ids = new List<string>(results.Count);
        foreach (var hit in results)
        {
            if (hit is not JsonObject record || record["id"] is not JsonValue value || !value.TryGetValue<string>(out var id) || string.IsNullOrWhiteSpace(id))
            {
                throw new DeliveryException($"The search service answered the query {question.Query} on {question.Kind} with a result that has no id: {Preview(body)}");
            }

            var segments = id.Split(':');
            if (segments.Length < 3 || !string.Equals(segments[1], entityType, StringComparison.Ordinal))
            {
                throw new DeliveryException($"The search service answered the query {question.Query} on {question.Kind} with {id}, which is not a {entityType} record.");
            }

            ids.Add(id);
        }

        // The count is the service's; a result list longer than it would be the service contradicting itself, and the
        // records in hand are the surer of the two.
        var total = result["totalCount"] is JsonValue count && count.TryGetValue<long>(out var counted) ? Math.Max(counted, ids.Count) : ids.Count;
        return total switch
        {
            0 => SearchAnswer.None,
            1 when ids.Count == 1 => SearchAnswer.Found(ids[0]),
            1 => throw new DeliveryException($"The search service counted one record for the query {question.Query} on {question.Kind} and returned none: {Preview(body)}"),
            _ => SearchAnswer.Ambiguous(total, ids.Distinct(StringComparer.Ordinal).ToList()),
        };
    }

    public void Dispose() => _slots.Dispose();

    private static string Preview(string text)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= RefusalPreview ? flat : flat[..RefusalPreview] + "...";
    }
}
