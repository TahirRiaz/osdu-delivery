using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The cursor reader (openapi search v2, POST /query_with_cursor) against a service that holds scroll contexts the way
/// Elasticsearch does: each page asked with a cursor moves its context on, whatever becomes of the answer. The reader hands
/// out every record the search matches once each, or ends in an exception; it never ends short and never hands a record
/// out twice, whichever page fails and however.
/// </summary>
public sealed class OsduSearchCursorTests
{
    private const string Kind = "osdu:wks:master-data--Wellbore:1.*";

    /// <summary>What becomes of one request the scrolling service is sent.</summary>
    private enum Fault
    {
        /// <summary>Answered as the service would.</summary>
        None,

        /// <summary>The context moves on and the answer is lost on the way back, as a 503 from a gateway.</summary>
        LostAs503,

        /// <summary>The context moves on and the answer is lost on the way back, as a 500.</summary>
        LostAs500,

        /// <summary>The context moves on and the answer is lost on the way back, as a 502 from a gateway.</summary>
        LostAs502,

        /// <summary>The context moves on and the answer is lost on the way back, as a 504 from a gateway.</summary>
        LostAs504,

        /// <summary>The context moves on and the connection drops before the answer arrives.</summary>
        LostInTransport,

        /// <summary>The context moves on and the request times out before the answer arrives.</summary>
        LostToTimeout,

        /// <summary>The context moves on and the answer arrives garbled.</summary>
        Garbled,

        /// <summary>The context moves on and the answer is JSON that is not an object.</summary>
        NotAnObject,

        /// <summary>The context moves on and the answer's results are not a list.</summary>
        ResultsNotAList,

        /// <summary>The context moves on and one of the page's hits carries no id.</summary>
        Nameless,

        /// <summary>The context is gone: the service no longer knows the cursor.</summary>
        Expired,

        /// <summary>The service refuses the request unread, too many requests; the context does not move.</summary>
        Throttled,

        /// <summary>The context ends early: an empty page and no cursor, though records are left.</summary>
        EndsEarly,
    }

    [Fact]
    public async Task A_search_is_read_whole_each_record_once_asking_the_total_on_the_first_page_alone()
    {
        var service = new ScrollingSearch(2500);
        var (search, runtime) = Reader(service);
        using (runtime)
        {
            var read = await ReadAsync(search, Query());

            Assert.Equal(service.Records, read.Ids);
            Assert.Equal(2500, read.Total);
            var pages = service.Posts();
            Assert.Equal(4, pages.Count);
            Assert.True(pages[0]["trackTotalCount"]!.GetValue<bool>());
            Assert.Null(pages[0]["cursor"]);
            Assert.All(pages.Skip(1), p =>
            {
                Assert.Equal("c1", p["cursor"]!.GetValue<string>());
                Assert.Null(p["trackTotalCount"]);
            });

            // The service ended the read by naming no cursor, so there is no context left to release, and no count asked.
            Assert.Empty(service.Deletes);
            Assert.Equal(0, service.Counts);
        }
    }

    [Fact]
    public async Task A_context_the_service_still_holds_after_the_last_page_is_released()
    {
        var service = new ScrollingSearch(1500) { CursorPastTheEnd = true };
        var (search, runtime) = Reader(service);
        using (runtime)
        {
            var read = await ReadAsync(search, Query());

            Assert.Equal(1500, read.Ids.Count);
            Assert.Equal(["c1"], service.Deletes);
        }
    }

    [Theory]
    [InlineData(nameof(Fault.LostAs503))]
    [InlineData(nameof(Fault.LostAs500))]
    [InlineData(nameof(Fault.LostAs502))]
    [InlineData(nameof(Fault.LostAs504))]
    [InlineData(nameof(Fault.LostInTransport))]
    [InlineData(nameof(Fault.LostToTimeout))]
    [InlineData(nameof(Fault.Garbled))]
    [InlineData(nameof(Fault.NotAnObject))]
    [InlineData(nameof(Fault.ResultsNotAList))]
    [InlineData(nameof(Fault.Nameless))]
    [InlineData(nameof(Fault.Expired))]
    public async Task A_page_that_fails_is_never_asked_for_again_with_its_cursor_and_the_search_is_read_again_losing_nothing(string failure)
    {
        var fault = Enum.Parse<Fault>(failure);

        // The third request is the second page with a cursor. However it fails, the context may have moved on, so asking with
        // the same cursor again would be answered with the page after it and the lost page would never be read.
        var service = new ScrollingSearch(3500) { Faults = call => call == 3 ? fault : Fault.None };
        var (search, runtime) = Reader(service);
        using (runtime)
        {
            var read = await ReadAsync(search, Query());

            Assert.Equal(service.Records, read.Ids.Order(StringComparer.Ordinal));
            Assert.Equal(read.Ids.Count, read.Ids.Distinct(StringComparer.Ordinal).Count());

            // Read 1: the first page, the second, and the third that failed; read 2 starts with a first page of its own.
            var pages = service.Posts();
            Assert.Equal("c1", pages[2]["cursor"]!.GetValue<string>());
            Assert.Null(pages[3]["cursor"]);
            Assert.True(pages[3]["trackTotalCount"]!.GetValue<bool>());
            Assert.DoesNotContain(pages.Skip(3), p => p["cursor"]?.GetValue<string>() == "c1");

            // The failed read's context is released, and the second read's ends with the service naming no cursor.
            Assert.Equal(["c1"], service.Deletes);
        }
    }

    [Fact]
    public async Task A_page_that_fails_part_way_leaves_none_of_its_records_behind_for_the_second_read()
    {
        // The second page holds a hit without an id beside 999 good records. None of the 999 may be marked as handed out,
        // or the second read would pass them over as already read.
        var service = new ScrollingSearch(2000) { Faults = call => call == 2 ? Fault.Nameless : Fault.None };
        var (search, runtime) = Reader(service);
        using (runtime)
        {
            var read = await ReadAsync(search, Query());

            Assert.Equal(service.Records, read.Ids.Order(StringComparer.Ordinal));
            Assert.Equal(2000, read.Ids.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public async Task A_page_the_service_refuses_as_too_many_requests_is_asked_for_again_with_the_same_cursor()
    {
        // A 429 is a refusal made before the search runs: the context has not moved, so the same page is still the next one.
        var service = new ScrollingSearch(2500) { Faults = call => call is 2 or 3 ? Fault.Throttled : Fault.None };
        var (search, runtime) = Reader(service);
        using (runtime)
        {
            var read = await ReadAsync(search, Query());

            Assert.Equal(service.Records, read.Ids);
            var pages = service.Posts();
            Assert.Single(pages, p => p["cursor"] is null);
            Assert.Equal(["c1", "c1", "c1"], pages.Skip(1).Take(3).Select(p => p["cursor"]!.GetValue<string>()));
        }
    }

    [Fact]
    public async Task A_read_that_ends_short_of_the_total_is_read_again_and_the_second_read_hands_out_only_what_the_first_did_not()
    {
        var service = new ScrollingSearch(3000) { Faults = call => call == 3 ? Fault.EndsEarly : Fault.None };
        var (search, runtime) = Reader(service);
        using (runtime)
        {
            var read = await ReadAsync(search, Query());

            Assert.Equal(service.Records, read.Ids.Order(StringComparer.Ordinal));
            Assert.Equal(3000, read.Ids.Distinct(StringComparer.Ordinal).Count());

            // Read 1 handed out two pages and ended on an empty one; read 2's first two pages bring records read 1 handed
            // out already, so they pass on none, and its third passes on the thousand read 1 never reached.
            Assert.Equal([1000, 1000, 0, 0, 0, 1000, 0], read.PageSizes);
        }
    }

    [Fact]
    public async Task Two_reads_that_fail_end_the_search_with_what_each_came_to()
    {
        var service = new ScrollingSearch(2500) { ContinuationFault = Fault.LostAs503 };
        var (search, runtime) = Reader(service);
        using (runtime)
        {
            var handedOut = new List<string>();
            var error = await Assert.ThrowsAsync<DeliveryException>(async () =>
            {
                await foreach (var page in search.PagesAsync(Query(), OsduSearch.MaxPage, CancellationToken.None))
                {
                    handedOut.AddRange(page.Hits.Select(h => OsduSearch.IdOf(h)!));
                }
            });

            Assert.Contains($"The search of kind {Kind} for 'data.FacilityName:*' could not be read whole in 2 reads", error.Message, StringComparison.Ordinal);
            Assert.Contains("read 1: page 2 failed: HTTP 503", error.Message, StringComparison.Ordinal);
            Assert.Contains("read 2: page 2 failed: HTTP 503", error.Message, StringComparison.Ordinal);

            // What was handed out before the failure is handed out once: the second read's first page brought nothing new.
            Assert.Equal(1000, handedOut.Count);
            Assert.Equal(1000, handedOut.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(2, service.Posts().Count(p => p["cursor"] is null));
            Assert.Equal(["c1", "c2"], service.Deletes);
        }
    }

    [Fact]
    public async Task A_service_going_in_circles_short_of_the_total_fails_the_search_after_two_reads()
    {
        var service = new ScrollingSearch(2500) { Circling = true };
        var (search, runtime) = Reader(service);
        using (runtime)
        {
            var error = await Assert.ThrowsAsync<DeliveryException>(() => ReadAsync(search, Query()));

            Assert.Contains("read 1: it returned 1000 of the 2500 record(s) the search matches in 4 page(s), the last 3 bringing no record it had not returned", error.Message, StringComparison.Ordinal);
            Assert.Contains("read 2: it returned 1000 of the 2500 record(s)", error.Message, StringComparison.Ordinal);
            Assert.Equal(["c1", "c2"], service.Deletes);
        }
    }

    [Fact]
    public async Task A_cursor_that_names_no_total_is_checked_against_the_exact_count()
    {
        var service = new ScrollingSearch(2500) { ReportTotal = false };
        var (search, runtime) = Reader(service);
        using (runtime)
        {
            var read = await ReadAsync(search, Query());

            Assert.Equal(2500, read.Ids.Count);
            Assert.Null(read.Total);
            Assert.Equal(1, service.Counts);
        }
    }

    [Fact]
    public async Task A_cursor_that_names_no_total_and_comes_back_short_of_the_count_fails_the_search()
    {
        var service = new ScrollingSearch(2500) { ReportTotal = false, Counted = 2600 };
        var (search, runtime) = Reader(service);
        using (runtime)
        {
            var error = await Assert.ThrowsAsync<DeliveryException>(() => ReadAsync(search, Query()));

            Assert.Contains("read 1: it returned 2500 of the 2600 record(s)", error.Message, StringComparison.Ordinal);
            Assert.Equal(2, service.Counts);
        }
    }

    [Fact]
    public async Task A_total_at_the_ten_thousand_an_uncounted_total_stops_at_is_counted_exactly()
    {
        // A deployment that does not track the total on the cursor names 10,000 for any search matching more: taken as the
        // total, it would pass a read that lost every page past the ten thousandth.
        var service = new ScrollingSearch(10_500) { Reported = 10_000 };
        var (search, runtime) = Reader(service);
        using (runtime)
        {
            var read = await ReadAsync(search, Query());

            Assert.Equal(10_500, read.Ids.Count);
            Assert.Equal(1, service.Counts);
        }
    }

    [Fact]
    public async Task Records_the_service_returns_twice_are_handed_out_once_and_counted_once()
    {
        var service = new ScrollingSearch(3) { Records = ["a", "b", "a", "c", "b"], Reported = 3 };
        var (search, runtime) = Reader(service);
        using (runtime)
        {
            var read = await ReadAsync(search, Query(), pageSize: 2);

            Assert.Equal(["a", "b", "c"], read.Ids);
            Assert.Single(service.Posts(), p => p["cursor"] is null);
        }
    }

    [Fact]
    public async Task A_caller_that_stops_early_releases_the_context_and_asks_no_count()
    {
        var service = new ScrollingSearch(5000) { ReportTotal = false };
        var (search, runtime) = Reader(service);
        using (runtime)
        {
            await foreach (var page in search.PagesAsync(Query(), OsduSearch.MaxPage, CancellationToken.None))
            {
                Assert.Equal(1000, page.Hits.Count);
                break;
            }

            Assert.Single(service.Posts());
            Assert.Equal(["c1"], service.Deletes);
            Assert.Equal(0, service.Counts);
        }
    }

    [Fact]
    public async Task A_cancelled_read_releases_its_context_and_is_not_read_again()
    {
        var service = new ScrollingSearch(5000);
        var (search, runtime) = Reader(service);
        using (runtime)
        using (var cancel = new CancellationTokenSource())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (var page in search.PagesAsync(Query(), OsduSearch.MaxPage, cancel.Token))
                {
                    await cancel.CancelAsync();
                }
            });

            Assert.Single(service.Posts());
            Assert.Equal(["c1"], service.Deletes);
        }
    }

    [Fact]
    public async Task A_first_page_the_service_refuses_fails_the_search_without_a_second_read()
    {
        var service = new ScrollingSearch(10) { Faults = call => call == 1 ? Fault.Expired : Fault.None };
        var (search, runtime) = Reader(service);
        using (runtime)
        {
            var error = await Assert.ThrowsAsync<OsduStatusException>(() => ReadAsync(search, Query()));

            Assert.Equal(400, error.StatusCode);
            Assert.Single(service.Posts());
        }
    }

    [Fact]
    public async Task A_narrowed_search_returns_the_id_the_read_knows_its_records_by()
    {
        var service = new ScrollingSearch(5);
        var (search, runtime) = Reader(service);
        using (runtime)
        {
            await ReadAsync(search, Query() with { ReturnedFields = ["data.FacilityName"] });

            Assert.All(service.Posts(), p => Assert.Equal(["data.FacilityName", "id"], p["returnedFields"]!.AsArray().Select(f => f!.GetValue<string>())));
        }
    }

    [Fact]
    public async Task An_empty_search_is_whole_with_nothing_to_hand_out()
    {
        var service = new ScrollingSearch(0) { CursorPastTheEnd = true };
        var (search, runtime) = Reader(service);
        using (runtime)
        {
            var read = await ReadAsync(search, Query());

            Assert.Empty(read.Ids);
            Assert.Equal(0, read.Total);
            Assert.Single(service.Posts());
            Assert.Equal(["c1"], service.Deletes);
        }
    }

    private static OsduSearchQuery Query() => new() { Kind = Kind, Query = "data.FacilityName:*" };

    private static (OsduSearch Search, HttpRuntime Runtime) Reader(ScrollingSearch service)
    {
        // Three attempts with the shortest backoff, so the retry policy's own repeats (a throttled page, a first page) show.
        var runtime = new HttpRuntime(
            new FlowReliability { Retry = new FlowRetry { Attempts = 3, BaseDelayMs = 1, MaxDelayMs = 1 } },
            new SecretResolver([new EnvSecretProvider()]), handler: service, allowLoopback: true);
        var client = new OsduHttpClient(
            runtime, "http://localhost", new TargetAuth { Type = TargetAuthType.None },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
        return (new OsduSearch(client, "/api/search/v2/query", "/api/search/v2/query_with_cursor", NullLogger.Instance), runtime);
    }

    private sealed record Read(List<string> Ids, long? Total, List<int> PageSizes);

    private static async Task<Read> ReadAsync(OsduSearch search, OsduSearchQuery query, int pageSize = OsduSearch.MaxPage)
    {
        var ids = new List<string>();
        var sizes = new List<int>();
        long? total = null;
        var first = true;
        await foreach (var page in search.PagesAsync(query, pageSize, CancellationToken.None))
        {
            if (first)
            {
                total = page.TotalCount;
                first = false;
            }

            sizes.Add(page.Hits.Count);
            ids.AddRange(page.Hits.Select(h => OsduSearch.IdOf(h)!));
        }

        return new Read(ids, total, sizes);
    }

    /// <summary>
    /// A search service holding scroll contexts: a first page opens context <c>c1</c>, <c>c2</c> and so on; each page asked
    /// with a cursor moves its context on by what it returns, before anything becomes of the answer. A fault decided for a
    /// request is applied to it alone; requests are counted from 1 across every POST to the cursor path.
    /// </summary>
    private sealed class ScrollingSearch : HttpMessageHandler
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, int> _contexts = new(StringComparer.Ordinal);
        private readonly List<JsonObject> _posts = [];
        private int _opened;

        public ScrollingSearch(int records)
        {
            Records = Enumerable.Range(0, records).Select(i => "dev:master-data--Wellbore:w" + i.ToString("D6", CultureInfo.InvariantCulture)).ToList();
        }

        /// <summary>The records the search matches, in the order the contexts return them.</summary>
        public List<string> Records { get; init; }

        /// <summary>What becomes of the request numbered so; none by default.</summary>
        public Func<int, Fault> Faults { get; init; } = _ => Fault.None;

        /// <summary>What becomes of every request that asks a page with a cursor, unless <see cref="Faults"/> names a fault.</summary>
        public Fault ContinuationFault { get; init; }

        /// <summary>Whether a first page names the total.</summary>
        public bool ReportTotal { get; init; } = true;

        /// <summary>The total a first page names in place of the records' count.</summary>
        public long? Reported { get; init; }

        /// <summary>The count the plain search answers in place of the records' count.</summary>
        public long? Counted { get; init; }

        /// <summary>Whether the empty page after the last still names the cursor, as Elasticsearch's scroll does.</summary>
        public bool CursorPastTheEnd { get; init; }

        /// <summary>Whether every context returns its first page again and again, as a service going in circles does.</summary>
        public bool Circling { get; init; }

        /// <summary>The cursors released, in order.</summary>
        public List<string> Deletes { get; } = [];

        /// <summary>The counts the plain search was asked for.</summary>
        public int Counts { get; private set; }

        /// <summary>The bodies of the cursor requests, in order.</summary>
        public List<JsonObject> Posts()
        {
            lock (_gate)
            {
                return [.. _posts];
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var text = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_gate)
            {
                if (request.Method == HttpMethod.Delete && path.StartsWith("/api/search/v2/query_with_cursor/", StringComparison.Ordinal))
                {
                    var cursor = path[(path.LastIndexOf('/') + 1)..];
                    Deletes.Add(cursor);
                    return _contexts.Remove(cursor) ? Ok(new JsonObject()) : Status(HttpStatusCode.NotFound, "no such cursor");
                }

                var body = JsonNode.Parse(text!)!.AsObject();
                if (request.Method == HttpMethod.Post && path == "/api/search/v2/query")
                {
                    Counts++;
                    return Ok(new JsonObject { ["results"] = new JsonArray(), ["totalCount"] = Counted ?? Records.Count });
                }

                if (request.Method != HttpMethod.Post || path != "/api/search/v2/query_with_cursor")
                {
                    return Status(HttpStatusCode.NotFound, "no route for " + path);
                }

                _posts.Add(body);
                var call = _posts.Count;
                var asked = body["cursor"]?.GetValue<string>();
                var fault = Faults(call);
                if (fault == Fault.None && asked is not null)
                {
                    fault = ContinuationFault;
                }

                if (fault == Fault.Throttled)
                {
                    var refused = Status(HttpStatusCode.TooManyRequests, "slow down");
                    refused.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                    return refused;
                }

                if (fault == Fault.Expired || (asked is not null && !_contexts.ContainsKey(asked)))
                {
                    return Status(HttpStatusCode.BadRequest, "the cursor is not valid");
                }

                var limit = body["limit"]!.GetValue<int>();
                string handle;
                if (asked is null)
                {
                    handle = "c" + (++_opened).ToString(CultureInfo.InvariantCulture);
                    _contexts[handle] = 0;
                }
                else
                {
                    handle = asked;
                }

                var from = Circling ? 0 : _contexts[handle];
                var page = fault == Fault.EndsEarly ? [] : Records.Skip(from).Take(limit).ToList();
                _contexts[handle] = from + page.Count;
                var answer = new JsonObject
                {
                    ["results"] = new JsonArray(page.Select(id => (JsonNode?)new JsonObject { ["id"] = id, ["kind"] = Kind }).ToArray()),
                    ["cursor"] = page.Count == 0 && !CursorPastTheEnd && !Circling ? null : handle,
                };
                if (asked is null && ReportTotal)
                {
                    answer["totalCount"] = Reported ?? Records.Count;
                }

                // Everything below happens after the context moved on: the page is lost however it is lost.
                switch (fault)
                {
                    case Fault.LostAs503:
                        return Status(HttpStatusCode.ServiceUnavailable, "busy");
                    case Fault.LostAs500:
                        return Status(HttpStatusCode.InternalServerError, "failed");
                    case Fault.LostAs502:
                        return Status(HttpStatusCode.BadGateway, "bad gateway");
                    case Fault.LostAs504:
                        return Status(HttpStatusCode.GatewayTimeout, "gateway timeout");
                    case Fault.LostInTransport:
                        throw new HttpRequestException("The response ended prematurely.");
                    case Fault.LostToTimeout:
                        throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
                    case Fault.Garbled:
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>proxy error</html>", Encoding.UTF8, "text/html") };
                    case Fault.NotAnObject:
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[1,2,3]", Encoding.UTF8, "application/json") };
                    case Fault.ResultsNotAList:
                        answer["results"] = "nothing";
                        return Ok(answer);
                    case Fault.Nameless:
                        answer["results"]!.AsArray().Add(new JsonObject { ["kind"] = Kind });
                        return Ok(answer);
                    default:
                        return Ok(answer);
                }
            }
        }

        private static HttpResponseMessage Ok(JsonObject body)
            => new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Status(HttpStatusCode status, string message)
            => new(status) { Content = new StringContent(new JsonObject { ["code"] = (int)status, ["message"] = message }.ToJsonString(), Encoding.UTF8, "application/json") };
    }
}
