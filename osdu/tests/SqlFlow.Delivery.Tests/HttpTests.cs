using System.Net;
using System.Net.Http.Headers;
using System.Text;
using SqlFlow.Core;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.Delivery.Tests;

public class RetryPolicyTests
{
    private static RetryPolicy Policy(int attempts = 4, int baseMs = 100, int maxMs = 1000, bool honor = true)
        => new(new FlowRetry { Attempts = attempts, BaseDelayMs = baseMs, MaxDelayMs = maxMs, HonorRetryAfter = honor }, new TestClock());

    [Fact]
    public void Retries_transient_statuses_with_exponential_backoff_and_stops_at_the_cap()
    {
        var policy = Policy();
        Assert.Equal(TimeSpan.FromMilliseconds(100), policy.Next(1, HttpStatusCode.ServiceUnavailable, null).Delay);
        Assert.Equal(TimeSpan.FromMilliseconds(200), policy.Next(2, HttpStatusCode.TooManyRequests, null).Delay);
        Assert.Equal(TimeSpan.FromMilliseconds(400), policy.Next(3, null, null).Delay);
        Assert.False(policy.Next(4, HttpStatusCode.ServiceUnavailable, null).ShouldRetry);
        Assert.False(policy.Next(1, HttpStatusCode.BadRequest, null).ShouldRetry);
        Assert.False(policy.Next(1, HttpStatusCode.Conflict, null).ShouldRetry);
    }

    [Fact]
    public void Retry_after_is_honoured_in_full_and_never_shortened()
    {
        // Ported from the OSDU C# client's RetryAfter_UsesFullDeltaOrUtcDateWithoutShortening: a wait the service
        // names is a wait, not a hint to be trimmed to whatever this layer is willing to sit through.
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(500));
        Assert.Equal(TimeSpan.FromMilliseconds(500), Policy().Next(1, HttpStatusCode.TooManyRequests, response.Headers).Delay);

        // Longer than the max: not waited out inline, and not shortened either. The request stops and the wait
        // travels with it for the record-level retry.
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
        var longer = Policy().Next(1, HttpStatusCode.TooManyRequests, response.Headers);
        Assert.False(longer.ShouldRetry);
        Assert.Equal(TimeSpan.FromMinutes(10), longer.RetryAfter);

        // A date already past is no wait at all, so the ordinary backoff applies.
        var clock = new TestClock();
        response.Headers.RetryAfter = new RetryConditionHeaderValue(clock.GetUtcNow().AddSeconds(-1));
        var past = new RetryPolicy(new FlowRetry { Attempts = 4, BaseDelayMs = 100, MaxDelayMs = 1000 }, clock).Next(1, HttpStatusCode.TooManyRequests, response.Headers);
        Assert.Equal(TimeSpan.FromMilliseconds(100), past.Delay);

        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(500));
        Assert.Equal(TimeSpan.FromMilliseconds(100), Policy(honor: false).Next(1, HttpStatusCode.TooManyRequests, response.Headers).Delay);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(502)]
    public void Statuses_the_service_did_not_call_transient_are_not_repeated(int status)
    {
        // Ported from the client's OtherStatuses_AreNotRetried. 500 and 502 are retried by the worker on its own
        // backoff, not replayed here within milliseconds.
        Assert.False(Policy().Next(1, (HttpStatusCode)status, null).ShouldRetry);
    }

    [Theory]
    [InlineData(408)]
    [InlineData(425)]
    [InlineData(429)]
    [InlineData(503)]
    [InlineData(504)]
    public void Transient_statuses_are_repeated(int status)
        => Assert.True(Policy().Next(1, (HttpStatusCode)status, null).ShouldRetry);

    [Fact]
    public void Record_backoff_grows_in_minutes_and_caps()
    {
        var retry = new FlowRetry { RecordBaseDelayMinutes = 1, RecordMaxDelayMinutes = 60 };
        Assert.Equal(TimeSpan.FromMinutes(1), RetryPolicy.RecordBackoff(retry, 1));
        Assert.Equal(TimeSpan.FromMinutes(4), RetryPolicy.RecordBackoff(retry, 3));
        Assert.Equal(TimeSpan.FromMinutes(60), RetryPolicy.RecordBackoff(retry, 12));
    }
}

public class UrlGuardTests
{
    [Fact]
    public void Blocks_private_loopback_and_metadata_addresses_and_non_http_schemes()
    {
        var guard = new UrlGuard([]);
        Assert.Throws<DeliveryException>(() => guard.Check(new Uri("http://169.254.169.254/latest")));
        Assert.Throws<DeliveryException>(() => guard.Check(new Uri("http://10.0.0.5/")));
        Assert.Throws<DeliveryException>(() => guard.Check(new Uri("http://127.0.0.1/")));
        Assert.Throws<DeliveryException>(() => guard.Check(new Uri("ftp://example.org/")));
        guard.Check(new Uri("https://api.example.org/x"));
        new UrlGuard([], allowLoopback: true).Check(new Uri("http://localhost:5000/x"));
    }

    [Fact]
    public void Allowlist_matches_exact_and_wildcard_hosts()
    {
        var guard = new UrlGuard(["*.equinor.com", "api.example.org"]);
        guard.Check(new Uri("https://api-dev.gateway.equinor.com/petrodb"));
        guard.Check(new Uri("https://api.example.org/"));
        Assert.Throws<DeliveryException>(() => guard.Check(new Uri("https://other.org/")));
    }
}

public class HeaderRedactionTests
{
    [Fact]
    public void Redacts_sensitive_headers_and_secrets_in_messages()
    {
        var redacted = HeaderRedaction.Redact(new Dictionary<string, string> { ["Authorization"] = "Bearer abc", ["Ocp-Apim-Subscription-Key"] = "k", ["Accept"] = "json" });
        Assert.Equal("***", redacted["Authorization"]);
        Assert.Equal("***", redacted["Ocp-Apim-Subscription-Key"]);
        Assert.Equal("json", redacted["Accept"]);
        var message = HeaderRedaction.RedactMessage("HTTP 401 from https://x/?api_key=secret123 with Bearer eyJhbGciOi.abc and {\"access_token\":\"tok\"}");
        Assert.DoesNotContain("secret123", message, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJhbGciOi", message, StringComparison.Ordinal);
        Assert.DoesNotContain("\"tok\"", message, StringComparison.Ordinal);
    }
}

public class HttpExecutorTests
{
    private static HttpRuntime Runtime(FakeHttpHandler handler, TestClock? clock = null, int attempts = 3)
        => new(new FlowReliability { Retry = new FlowRetry { Attempts = attempts, BaseDelayMs = 1, MaxDelayMs = 2 }, TimeoutSeconds = 5 }, new SecretResolver([new EnvSecretProvider()]), clock ?? new TestClock(), handler, allowLoopback: true);

    [Fact]
    public async Task Retries_from_the_factory_and_reopens_stream_content_per_attempt()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/data", hit => hit == 0 ? FakeHttpHandler.Json(HttpStatusCode.ServiceUnavailable, null) : FakeHttpHandler.Json(HttpStatusCode.OK, "{\"ok\":true}"));
        using var runtime = Runtime(handler);
        var opened = 0;
        var result = await runtime.Data.SendAsync(() =>
        {
            opened++;
            var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/data");
            request.Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes("payload-" + opened)));
            return request;
        }, idempotent: true);
        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.Equal(2, opened);
        Assert.Equal(["payload-1", "payload-2"], handler.Calls.Select(c => c.Body));
    }

    [Fact]
    public async Task Non_retryable_statuses_throw_with_status_and_preview()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Put, "/records", HttpStatusCode.BadRequest, "{\"error\":\"bad acl\"}");
        using var runtime = Runtime(handler);
        var ex = await Assert.ThrowsAsync<OsduStatusException>(() => runtime.Data.SendAsync(() => new HttpRequestMessage(HttpMethod.Put, "http://localhost/records")));
        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("bad acl", ex.Message, StringComparison.Ordinal);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Exhausted_retries_surface_the_last_status()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Get, "/x", HttpStatusCode.ServiceUnavailable, "down");
        using var runtime = Runtime(handler, attempts: 2);
        await Assert.ThrowsAsync<OsduStatusException>(() => runtime.Data.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "http://localhost/x")));
        Assert.Equal(2, handler.Calls.Count);
    }

    [Theory]
    [InlineData("GET", 429)]
    [InlineData("GET", 503)]
    [InlineData("HEAD", 504)]
    public async Task Eligible_reads_are_repeated_up_to_the_configured_attempts(string method, int status)
    {
        // Ported from the client's EligibleReads_RetryOnlyConfiguredNumber.
        var handler = new FakeHttpHandler().On(new HttpMethod(method), "/records", (HttpStatusCode)status, null);
        using var runtime = Runtime(handler);
        var ex = await Assert.ThrowsAsync<OsduStatusException>(() => runtime.Data.SendAsync(() => new HttpRequestMessage(new HttpMethod(method), "http://localhost/records")));
        Assert.Equal(status, ex.StatusCode);
        Assert.Equal(3, handler.Calls.Count);
    }

    [Theory]
    [InlineData("POST", "/records")]
    [InlineData("PATCH", "/records")]
    [InlineData("POST", "/ddms/v3/welllogs/id/sessions")]
    [InlineData("POST", "/ddms/v3/welllogs/id/sessions/sid/data")]
    [InlineData("POST", "/api/file/v2/files/metadata")]
    public async Task Writes_that_are_not_safe_to_repeat_are_sent_once(string method, string path)
    {
        // Ported from the client's WritesUploadsAndNonAllowlistedBodies_AreNotRetried: a lost response on one of
        // these may mean the service acted, so resending would act twice.
        var handler = new FakeHttpHandler().On(new HttpMethod(method), path, HttpStatusCode.ServiceUnavailable, null);
        using var runtime = Runtime(handler);
        await Assert.ThrowsAsync<OsduStatusException>(() => runtime.Data.SendAsync(() => new HttpRequestMessage(new HttpMethod(method), "http://localhost" + path)));
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task A_post_the_caller_declares_safe_to_repeat_is_repeated_with_the_same_body()
    {
        // Ported from the client's SearchPost_ReplaysJsonAndPreservesHeadersAndOptions. The client's allowlist is
        // search; here each protocol states it for the calls it knows to be safe.
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/api/search/v2/query", hit => FakeHttpHandler.Json(hit == 0 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, "{}"));
        using var runtime = Runtime(handler);
        var result = await runtime.Data.SendAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/api/search/v2/query") { Content = new StringContent("{\"kind\":\"osdu:*:*:*\"}", Encoding.UTF8, "application/json") };
                request.Headers.TryAddWithoutValidation("data-partition-id", "partition");
                return request;
            },
            idempotent: true);
        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.Equal(2, handler.Calls.Count);
        Assert.Equal(handler.Calls[0].Body, handler.Calls[1].Body);
        Assert.All(handler.Calls, c => Assert.Equal("partition", c.Headers["data-partition-id"]));
    }

    [Fact]
    public async Task A_transport_failure_on_a_write_is_never_repeated()
    {
        // Ported from the client's TransportExceptions_AreNeverRetried, for the requests where it matters: the
        // connection going says nothing about whether the service acted.
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/sessions/sid/data", _ => throw new HttpRequestException("connection reset"));
        using var runtime = Runtime(handler);
        var ex = await Assert.ThrowsAsync<DeliveryException>(() => runtime.Data.SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "http://localhost/sessions/sid/data")));
        Assert.Contains("transport failure", ex.Message, StringComparison.Ordinal);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task A_transport_failure_on_a_read_is_repeated()
    {
        // Deliberately unlike the client, which retries no transport failure at all: a GET has no effect to
        // repeat, so a dropped connection on one is exactly the transient failure a retry exists for.
        var handler = new FakeHttpHandler().On(HttpMethod.Get, "/records/x", hit => hit == 0 ? throw new HttpRequestException("connection reset") : FakeHttpHandler.Json(HttpStatusCode.OK, "{}"));
        using var runtime = Runtime(handler);
        var result = await runtime.Data.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "http://localhost/records/x"));
        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task A_wait_longer_than_the_transport_will_sit_through_travels_with_the_failure()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Get, "/records", _ =>
        {
            var response = FakeHttpHandler.Json(HttpStatusCode.TooManyRequests, "{}");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(20));
            return response;
        });
        using var runtime = Runtime(handler);
        var ex = await Assert.ThrowsAsync<OsduStatusException>(() => runtime.Data.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "http://localhost/records")));
        Assert.Equal(TimeSpan.FromMinutes(20), ex.RetryAfter);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Allowed_statuses_are_returned_instead_of_thrown()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Get, "/x", HttpStatusCode.NotFound, null);
        using var runtime = Runtime(handler);
        var result = await runtime.Data.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "http://localhost/x"), new HashSet<int> { 404 });
        Assert.Equal(HttpStatusCode.NotFound, result.Status);
    }
}

public class AuthResolverTests
{
    [Fact]
    public async Task Client_credentials_token_is_acquired_once_and_refreshed_before_expiry()
    {
        Environment.SetEnvironmentVariable("OSDU_TEST_SECRET", "s3cret");
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/token", hit => FakeHttpHandler.Json(HttpStatusCode.OK, $"{{\"access_token\":\"tok{hit}\",\"expires_in\":600}}"));
        var clock = new TestClock();
        using var runtime = new HttpRuntime(new FlowReliability(), new SecretResolver([new EnvSecretProvider()]), clock, handler, allowLoopback: true);
        var auth = new TargetAuth
        {
            Type = TargetAuthType.OAuth2ClientCredentials,
            SecondarySecretRef = "client-1",
            SecretRef = "${env:OSDU_TEST_SECRET}",
            Token = new TargetTokenEndpoint { Url = "http://localhost/token", Body = new Dictionary<string, string> { ["scope"] = "api://x/.default" } },
        };

        var first = await runtime.AuthResolver.ResolveAsync(auth, runtime.Auth);
        var second = await runtime.AuthResolver.ResolveAsync(auth, runtime.Auth);
        Assert.Equal("Bearer tok0", first.Headers["Authorization"]);
        Assert.Same(first, second);
        Assert.Single(handler.Calls);
        Assert.Contains("client_secret=s3cret", handler.Calls[0].Body, StringComparison.Ordinal);
        Assert.Contains("grant_type=client_credentials", handler.Calls[0].Body, StringComparison.Ordinal);

        clock.Advance(TimeSpan.FromMinutes(9.5));
        var third = await runtime.AuthResolver.ResolveAsync(auth, runtime.Auth);
        Assert.Equal("Bearer tok1", third.Headers["Authorization"]);
    }

    [Fact]
    public async Task Static_schemes_resolve_secret_references()
    {
        Environment.SetEnvironmentVariable("OSDU_TEST_KEY", "k-123");
        var resolver = new AuthResolver(new SecretResolver([new EnvSecretProvider()]));
        using var runtime = new HttpRuntime(new FlowReliability(), new SecretResolver([new EnvSecretProvider()]), null, new FakeHttpHandler(), allowLoopback: true);
        var apiKey = await resolver.ResolveAsync(new TargetAuth { Type = TargetAuthType.ApiKeyHeader, HeaderName = "X-Key", SecretRef = "${env:OSDU_TEST_KEY}" }, runtime.Auth);
        Assert.Equal("k-123", apiKey.Headers["X-Key"]);
        var basic = await resolver.ResolveAsync(new TargetAuth { Type = TargetAuthType.Basic, SecondarySecretRef = "u", SecretRef = "p" }, runtime.Auth);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("u:p")), basic.Headers["Authorization"]);
        await Assert.ThrowsAsync<DeliveryException>(() => resolver.ResolveAsync(new TargetAuth { Type = TargetAuthType.Bearer }, runtime.Auth));
    }
}

/// <summary>
/// Path-segment escaping. OSDU identifiers are colon separated and every endpoint that takes one takes it in the
/// path, so what goes on the wire has to be what RFC 3986 says a segment may carry rather than what a form field
/// may carry.
/// </summary>
public class UrlPathTests
{
    [Fact]
    public void A_record_id_keeps_its_colons_and_its_other_legal_characters()
    {
        Assert.Equal("opendes:master-data--Well:1234-abc", UrlPath.EscapeSegment("opendes:master-data--Well:1234-abc"));
        Assert.Equal("osdu:wks:work-product-component--WellLog:1.0.0", UrlPath.EscapeSegment("osdu:wks:work-product-component--WellLog:1.0.0"));
        Assert.Equal("a_b~c.d-e", UrlPath.EscapeSegment("a_b~c.d-e"));
    }

    [Fact]
    public void A_base64_search_cursor_keeps_its_padding()
    {
        Assert.Equal("DXF1ZXJ5QW5kRmV0Y2gBAAAAAAAA==", UrlPath.EscapeSegment("DXF1ZXJ5QW5kRmV0Y2gBAAAAAAAA=="));
    }

    [Fact]
    public void What_a_segment_cannot_carry_is_percent_encoded()
    {
        Assert.Equal("a%2Fb", UrlPath.EscapeSegment("a/b"));
        Assert.Equal("a%3Fb", UrlPath.EscapeSegment("a?b"));
        Assert.Equal("a%23b", UrlPath.EscapeSegment("a#b"));
        Assert.Equal("a%25b", UrlPath.EscapeSegment("a%b"));
        Assert.Equal("a%20b", UrlPath.EscapeSegment("a b"));
        Assert.Equal("caf%C3%A9", UrlPath.EscapeSegment("café"));
        Assert.Equal(string.Empty, UrlPath.EscapeSegment(string.Empty));
    }

    [Fact]
    public void The_escaped_segment_survives_being_put_in_a_Uri()
    {
        var url = new Uri("https://osdu.example.com/api/storage/v2/records/" + UrlPath.EscapeSegment("opendes:master-data--Well:1") + ":delete");
        Assert.Equal("/api/storage/v2/records/opendes:master-data--Well:1:delete", url.AbsolutePath);
    }
}
