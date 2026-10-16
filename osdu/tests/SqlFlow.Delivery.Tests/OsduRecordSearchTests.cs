using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The platform search a delivery answers its search sources from (openapi search v2, <c>POST /query</c>): one query per
/// distinct question, the answer read and checked, a refusal kept as that question's answer, and anything that keeps an
/// answer from coming failing the call rather than passing for finding nothing.
/// </summary>
public class OsduRecordSearchTests
{
    private const string Kind = "osdu:wks:master-data--Wellbore:*";

    private static SearchQuestion Question(string value)
        => new(Kind, "data.FacilityName", value, $"data.FacilityName.keyword:\"{value}\"");

    private static string Result(long total, params string[] ids)
        => new JsonObject
        {
            ["results"] = new JsonArray(ids.Select(id => (JsonNode?)new JsonObject { ["id"] = id }).ToArray()),
            ["totalCount"] = total,
        }.ToJsonString();

    private static (OsduRecordSearch Search, HttpRuntime Runtime) Search(FakeHttpHandler handler, int attempts = 1)
    {
        var runtime = new HttpRuntime(
            new FlowReliability { Retry = new FlowRetry { Attempts = attempts, BaseDelayMs = 1, MaxDelayMs = 1 } },
            new SecretResolver([new EnvSecretProvider()]), new TestClock(), handler, allowLoopback: true);
        var client = new OsduHttpClient(
            runtime, "http://localhost", new TargetAuth { Type = TargetAuthType.None },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
        return (new OsduRecordSearch(_ => Task.FromResult(client), NullLogger.Instance), runtime);
    }

    [Fact]
    public async Task One_record_found_is_the_answer_and_the_request_asks_for_its_id_and_no_more()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/api/search/v2/query", HttpStatusCode.OK, Result(1, "dev:master-data--Wellbore:abc"));
        var (search, runtime) = Search(handler);
        using (runtime)
        using (search)
        {
            await search.AnswerAsync([Question("WB-1")]);

            Assert.True(search.TryAnswer(Question("WB-1"), out var answer));
            Assert.Equal((SearchOutcome.Found, "dev:master-data--Wellbore:abc"), (answer.Outcome, answer.Id));

            var call = Assert.Single(handler.Calls);
            var body = JsonNode.Parse(call.Body!)!;
            Assert.Equal(Kind, body["kind"]!.GetValue<string>());
            Assert.Equal("data.FacilityName.keyword:\"WB-1\"", body["query"]!.GetValue<string>());
            Assert.Equal(SearchAnswer.NamedCandidates, body["limit"]!.GetValue<int>());
            Assert.Equal("id", Assert.Single(body["returnedFields"]!.AsArray())!.GetValue<string>());

            // The flow's partition goes with every question: a search only ever finds that partition's records.
            Assert.Equal("dev", call.Headers["data-partition-id"]);
        }

        OsduContracts.AssertConform(handler.Calls, null, OsduContracts.Search);
    }

    [Fact]
    public async Task No_record_found_is_an_answer_too_and_is_not_asked_again()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/api/search/v2/query", HttpStatusCode.OK, Result(0));
        var (search, runtime) = Search(handler);
        using (runtime)
        using (search)
        {
            await search.AnswerAsync([Question("MISSING")]);
            await search.AnswerAsync([Question("MISSING")]);

            Assert.True(search.TryAnswer(Question("MISSING"), out var answer));
            Assert.Equal(SearchOutcome.NotFound, answer.Outcome);
            Assert.Single(handler.Calls);
        }
    }

    [Fact]
    public async Task Several_records_found_name_the_first_ones_and_count_the_rest()
    {
        var handler = new FakeHttpHandler().On(
            HttpMethod.Post, "/api/search/v2/query", HttpStatusCode.OK,
            Result(7, "dev:master-data--Wellbore:a", "dev:master-data--Wellbore:b", "dev:master-data--Wellbore:c", "dev:master-data--Wellbore:d", "dev:master-data--Wellbore:e"));
        var (search, runtime) = Search(handler);
        using (runtime)
        using (search)
        {
            await search.AnswerAsync([Question("TWIN")]);

            Assert.True(search.TryAnswer(Question("TWIN"), out var answer));
            Assert.Equal((SearchOutcome.Ambiguous, 7L, 5), (answer.Outcome, answer.Total, answer.Candidates.Count));
            Assert.EndsWith("and 2 more)", answer.Describe(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_query_the_service_refuses_is_that_questions_answer_with_the_services_words()
    {
        var handler = new FakeHttpHandler().On(
            HttpMethod.Post, "/api/search/v2/query", HttpStatusCode.BadRequest,
            """{"code":400,"reason":"Malformed query","message":"Malformed unbalanced double quotes in query"}""");
        var (search, runtime) = Search(handler);
        using (runtime)
        using (search)
        {
            await search.AnswerAsync([Question("WB-1")]);

            Assert.True(search.TryAnswer(Question("WB-1"), out var answer));
            Assert.Equal(SearchOutcome.Refused, answer.Outcome);
            Assert.Contains("Malformed unbalanced double quotes", answer.Reason, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task A_platform_that_gives_no_answer_fails_the_call_and_leaves_the_question_unanswered(HttpStatusCode status)
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/api/search/v2/query", status, """{"message":"no"}""");
        var (search, runtime) = Search(handler);
        using (runtime)
        using (search)
        {
            await Assert.ThrowsAsync<OsduStatusException>(() => search.AnswerAsync([Question("WB-1")]));

            // Nothing is taken for an answer, so the records asking are never held for a reason that is not true.
            Assert.False(search.TryAnswer(Question("WB-1"), out _));
        }
    }

    [Fact]
    public async Task A_question_whose_asking_failed_is_asked_again_by_the_next_caller()
    {
        var handler = new FakeHttpHandler().On(
            HttpMethod.Post,
            "/api/search/v2/query",
            hit => hit == 0
                ? FakeHttpHandler.Json(HttpStatusCode.ServiceUnavailable, """{"message":"busy"}""")
                : FakeHttpHandler.Json(HttpStatusCode.OK, Result(1, "dev:master-data--Wellbore:abc")));
        var (search, runtime) = Search(handler);
        using (runtime)
        using (search)
        {
            await Assert.ThrowsAsync<OsduStatusException>(() => search.AnswerAsync([Question("WB-1")]));
            await search.AnswerAsync([Question("WB-1")]);

            Assert.True(search.TryAnswer(Question("WB-1"), out var answer));
            Assert.Equal(SearchOutcome.Found, answer.Outcome);
            Assert.Equal(2, handler.Calls.Count);
        }
    }

    [Fact]
    public async Task A_busy_service_is_retried_under_the_flows_reliability_before_the_call_fails()
    {
        var handler = new FakeHttpHandler().On(
            HttpMethod.Post,
            "/api/search/v2/query",
            hit => hit < 2
                ? FakeHttpHandler.Json(HttpStatusCode.TooManyRequests, """{"message":"slow down"}""")
                : FakeHttpHandler.Json(HttpStatusCode.OK, Result(1, "dev:master-data--Wellbore:abc")));
        var (search, runtime) = Search(handler, attempts: 3);
        using (runtime)
        using (search)
        {
            await search.AnswerAsync([Question("WB-1")]);

            Assert.True(search.TryAnswer(Question("WB-1"), out var answer));
            Assert.Equal(SearchOutcome.Found, answer.Outcome);
            Assert.Equal(3, handler.Calls.Count);
        }
    }

    [Fact]
    public async Task One_question_asked_by_many_callers_at_once_is_sent_once()
    {
        var release = new TaskCompletionSource();
        var handler = new SlowSearchHandler(release.Task, Result(1, "dev:master-data--Wellbore:abc"));
        var runtime = new HttpRuntime(
            new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
            new SecretResolver([new EnvSecretProvider()]), new TestClock(), handler, allowLoopback: true);
        var client = new OsduHttpClient(runtime, "http://localhost", new TargetAuth { Type = TargetAuthType.None }, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        using (runtime)
        using (var search = new OsduRecordSearch(_ => Task.FromResult(client), NullLogger.Instance))
        {
            var callers = Enumerable.Range(0, 16).Select(_ => Task.Run(() => search.AnswerAsync([Question("WB-1")]))).ToList();
            await handler.FirstCall;
            release.SetResult();
            await Task.WhenAll(callers);

            Assert.Equal(1, handler.Calls);
            Assert.True(search.TryAnswer(Question("WB-1"), out _));
        }
    }

    [Fact]
    public async Task Many_questions_are_asked_in_parallel_but_never_more_at_once_than_the_limit()
    {
        var handler = new CountingSearchHandler(Result(0));
        var runtime = new HttpRuntime(
            new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
            new SecretResolver([new EnvSecretProvider()]), new TestClock(), handler, allowLoopback: true);
        var client = new OsduHttpClient(runtime, "http://localhost", new TargetAuth { Type = TargetAuthType.None }, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        using (runtime)
        using (var search = new OsduRecordSearch(_ => Task.FromResult(client), NullLogger.Instance))
        {
            await search.AnswerAsync([.. Enumerable.Range(0, 100).Select(i => Question($"WB-{i}"))]);

            Assert.Equal(100, handler.Calls);
            Assert.InRange(handler.MostAtOnce, 2, OsduRecordSearch.MaxConcurrentQueries);
        }
    }

    [Theory]
    [InlineData("not json", "not JSON")]
    [InlineData("[]", "not a JSON object")]
    [InlineData("""{"totalCount":1}""", "without a 'results' list")]
    [InlineData("""{"results":[{"kind":"x"}],"totalCount":1}""", "a result that has no id")]
    [InlineData("""{"results":[{"id":"dev:master-data--Well:abc"}],"totalCount":1}""", "which is not a master-data--Wellbore record")]
    [InlineData("""{"results":[],"totalCount":1}""", "counted one record")]
    public void A_body_that_is_not_a_search_result_fails_rather_than_passing_for_an_answer(string body, string expected)
        => Assert.Contains(expected, Assert.Throws<DeliveryException>(() => OsduRecordSearch.Read(Question("WB-1"), body)).Message, StringComparison.Ordinal);

    [Fact]
    public void A_result_list_longer_than_its_count_is_taken_as_what_it_holds()
    {
        var answer = OsduRecordSearch.Read(Question("WB-1"), Result(1, "dev:master-data--Wellbore:a", "dev:master-data--Wellbore:b"));

        Assert.Equal((SearchOutcome.Ambiguous, 2L), (answer.Outcome, answer.Total));
    }

    [Fact]
    public void A_result_without_a_count_is_counted_by_what_it_holds()
    {
        var answer = OsduRecordSearch.Read(Question("WB-1"), """{"results":[{"id":"dev:master-data--Wellbore:a"}]}""");

        Assert.Equal((SearchOutcome.Found, "dev:master-data--Wellbore:a"), (answer.Outcome, answer.Id));
    }

    /// <summary>Holds every search until released, so callers asking at once meet one request in flight.</summary>
    private sealed class SlowSearchHandler(Task release, string body) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public Task FirstCall => _first.Task;

        public int Calls => Volatile.Read(ref _calls);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            _first.TrySetResult();
            await release.WaitAsync(cancellationToken);
            return FakeHttpHandler.Json(HttpStatusCode.OK, body);
        }
    }

    /// <summary>Answers every search after a pause, keeping the most requests it ever had in flight at once.</summary>
    private sealed class CountingSearchHandler(string body) : HttpMessageHandler
    {
        private int _inFlight;
        private int _most;
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public int MostAtOnce => Volatile.Read(ref _most);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while ((seen = Volatile.Read(ref _most)) < now && Interlocked.CompareExchange(ref _most, now, seen) != seen)
            {
            }

            await Task.Delay(5, cancellationToken);
            Interlocked.Decrement(ref _inFlight);
            return FakeHttpHandler.Json(HttpStatusCode.OK, body);
        }
    }
}
