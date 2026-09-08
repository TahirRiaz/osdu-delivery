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
    public void Honors_retry_after_as_a_floor_bounded_by_the_max()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromMilliseconds(1000), Policy().Next(1, HttpStatusCode.TooManyRequests, response.Headers).Delay);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(500));
        Assert.Equal(TimeSpan.FromMilliseconds(500), Policy().Next(1, HttpStatusCode.TooManyRequests, response.Headers).Delay);
        Assert.Equal(TimeSpan.FromMilliseconds(100), Policy(honor: false).Next(1, HttpStatusCode.TooManyRequests, response.Headers).Delay);
    }

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
        });
        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.Equal(2, opened);
        Assert.Equal(["payload-1", "payload-2"], handler.Calls.Select(c => c.Body));
    }

    [Fact]
    public async Task Non_retryable_statuses_throw_with_status_and_preview()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Put, "/records", HttpStatusCode.BadRequest, "{\"error\":\"bad acl\"}");
        using var runtime = Runtime(handler);
        var ex = await Assert.ThrowsAsync<HttpStatusException>(() => runtime.Data.SendAsync(() => new HttpRequestMessage(HttpMethod.Put, "http://localhost/records")));
        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("bad acl", ex.Message, StringComparison.Ordinal);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Exhausted_retries_surface_the_last_status()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Get, "/x", HttpStatusCode.BadGateway, "down");
        using var runtime = Runtime(handler, attempts: 2);
        await Assert.ThrowsAsync<HttpStatusException>(() => runtime.Data.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "http://localhost/x")));
        Assert.Equal(2, handler.Calls.Count);
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
