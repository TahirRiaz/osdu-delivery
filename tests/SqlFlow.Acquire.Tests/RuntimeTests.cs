using System.Globalization;
using System.Net;
using System.Text.Json;
using SqlFlow.Acquire.Runtime;
using SqlFlow.Core;
using SqlFlow.Core.Acquire;
using Xunit;

namespace SqlFlow.Acquire.Tests;

/// <summary>A TimeProvider pinned to a fixed instant, for deterministic date-window and watermark tests.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

public sealed class TemplateEngineTests
{
    private static readonly DateTimeOffset Ref = new(2026, 7, 9, 13, 45, 12, TimeSpan.Zero);

    [Fact]
    public void Formats_bare_date_tokens_against_reference_date()
    {
        var ctx = new TemplateContext(Ref);
        Assert.Equal("2026", TemplateEngine.Render("{yyyy}", ctx));
        Assert.Equal("20260709", TemplateEngine.Render("{yyyyMMdd}", ctx));
        Assert.Equal("2026-07-09", TemplateEngine.Render("{yyyy-MM-dd}", ctx));
        Assert.Equal("13:45:12", TemplateEngine.Render("{HH:mm:ss}", ctx));
    }

    [Fact]
    public void Substitutes_string_and_formatted_date_variables()
    {
        var ctx = new TemplateContext(Ref).WithString("operatorId", "1295").WithDate("window.from", Ref);
        Assert.Equal("op=1295", TemplateEngine.Render("op={operatorId}", ctx));
        Assert.Equal("2026-07-09", TemplateEngine.Render("{window.from:yyyy-MM-dd}", ctx));
    }

    [Fact]
    public void Url_encodes_when_requested()
    {
        var ctx = new TemplateContext(Ref).WithString("q", "a b&c");
        Assert.Equal("a%20b%26c", TemplateEngine.RenderEncoded("{q}", ctx));
        Assert.Equal("a b&c", TemplateEngine.Render("{q}", ctx));
    }

    [Fact]
    public void Missing_variable_throws_naming_the_token()
    {
        var ex = Assert.Throws<SqlFlowException>(() => TemplateEngine.Render("{nope}", new TemplateContext(Ref)));
        Assert.Contains("nope", ex.Message, StringComparison.Ordinal);
    }
}

public sealed class RelativeTimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 9, 13, 45, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("now", "2026-07-09T13:45:00")]
    [InlineData("now-3d", "2026-07-06T13:45:00")]
    [InlineData("now-2h", "2026-07-09T11:45:00")]
    [InlineData("today", "2026-07-09T00:00:00")]
    [InlineData("yesterday", "2026-07-08T00:00:00")]
    [InlineData("startOfMonth", "2026-07-01T00:00:00")]
    [InlineData("startOfMonth-1mo", "2026-06-01T00:00:00")]
    [InlineData("2026-01-15", "2026-01-15T00:00:00")]
    public void Resolves_expressions(string expression, string expected)
        => Assert.Equal(DateTimeOffset.Parse(expected + "+00:00", CultureInfo.InvariantCulture), RelativeTime.Resolve(expression, Now));

    [Fact]
    public void Rejects_garbage() => Assert.Throws<SqlFlowException>(() => RelativeTime.Resolve("banana", Now));
}

public sealed class UrlGuardTests
{
    [Fact]
    public void Blocks_cloud_metadata_and_private_addresses()
    {
        var guard = new UrlGuard([]);
        Assert.Throws<SqlFlowException>(() => guard.Check(new Uri("http://169.254.169.254/latest/meta-data")));
        Assert.Throws<SqlFlowException>(() => guard.Check(new Uri("http://127.0.0.1/")));
        Assert.Throws<SqlFlowException>(() => guard.Check(new Uri("http://10.1.2.3/")));
        Assert.Throws<SqlFlowException>(() => guard.Check(new Uri("ftp://example.com/")));
    }

    [Fact]
    public void Allowlist_matches_suffix_and_exact()
    {
        var guard = new UrlGuard(["*.example.com", "api.acme.io"]);
        guard.Check(new Uri("https://data.example.com/x"));
        guard.Check(new Uri("https://example.com/x"));
        guard.Check(new Uri("https://api.acme.io/x"));
        Assert.Throws<SqlFlowException>(() => guard.Check(new Uri("https://evil.test/x")));
    }
}

public sealed class RetryPolicyTests
{
    private static readonly TimeProvider Time = TimeProvider.System;

    [Fact]
    public void Retries_transient_stops_permanent()
    {
        var policy = new RetryPolicy(new AcquireRetry { MaxAttempts = 4 }, Time);
        Assert.True(policy.Next(1, HttpStatusCode.ServiceUnavailable, null).ShouldRetry);
        Assert.True(policy.Next(1, HttpStatusCode.TooManyRequests, null).ShouldRetry);
        Assert.True(policy.Next(1, null, null).ShouldRetry); // transport failure
        Assert.False(policy.Next(1, HttpStatusCode.BadRequest, null).ShouldRetry);
        Assert.False(policy.Next(4, HttpStatusCode.ServiceUnavailable, null).ShouldRetry); // attempt cap
    }

    [Fact]
    public void Backoff_grows_exponentially_and_caps()
    {
        var policy = new RetryPolicy(new AcquireRetry { MaxAttempts = 10, BaseDelayMs = 100, MaxDelayMs = 1000 }, Time);
        Assert.Equal(TimeSpan.FromMilliseconds(100), policy.Next(1, HttpStatusCode.InternalServerError, null).Delay);
        Assert.Equal(TimeSpan.FromMilliseconds(200), policy.Next(2, HttpStatusCode.InternalServerError, null).Delay);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), policy.Next(9, HttpStatusCode.InternalServerError, null).Delay); // capped
    }
}

public sealed class HttpExecutorTests
{
    private static readonly TimeProvider Time = TimeProvider.System;

    private static HttpExecutor Build(HttpMessageHandler handler) => new(
        new HttpClient(handler),
        new RetryPolicy(new AcquireRetry { MaxAttempts = 4, BaseDelayMs = 1, MaxDelayMs = 2 }, Time),
        new RateLimiter(1000, Time),
        new UrlGuard(["*.example.com"]),
        maxResponseBytes: 1 << 20,
        Time);

    [Fact]
    public async Task Retries_a_reset_while_streaming_the_response_body()
    {
        // First attempt: 200 OK whose body stream throws the exact reset that escaped the retry loop before this
        // fix (it is raised during ReadCappedAsync, after SendAsync has already returned the headers). Second
        // attempt: a clean body. The executor must retry and return the good bytes rather than surfacing the reset.
        var handler = new ScriptedHandler(
            _ => ResponseWithBodyStream(new ThrowingStream(new IOException(
                "Unable to read data from the transport connection: An existing connection was forcibly closed by the remote host."))),
            _ => ResponseWithBodyStream(new MemoryStream("ok"u8.ToArray())));

        var result = await Build(handler).SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/x"));

        Assert.Equal("ok", System.Text.Encoding.UTF8.GetString(result.Body));
        Assert.Equal(2, handler.Attempts);
    }

    [Fact]
    public async Task Surfaces_a_persistent_body_reset_after_exhausting_retries()
    {
        var handler = new ScriptedHandler(_ => ResponseWithBodyStream(new ThrowingStream(new IOException(
            "An existing connection was forcibly closed by the remote host."))));

        var ex = await Assert.ThrowsAsync<SqlFlowException>(() =>
            Build(handler).SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/x")));

        Assert.Contains("reading the response", ex.Message, StringComparison.Ordinal);
        Assert.Equal(4, handler.Attempts); // MaxAttempts, then surface
    }

    private static HttpResponseMessage ResponseWithBodyStream(Stream body)
        => new(HttpStatusCode.OK) { Content = new StreamContent(body) };

    /// <summary>Replays a fixed script of responses, one per attempt (the last entry repeats), counting attempts.</summary>
    private sealed class ScriptedHandler(params Func<int, HttpResponseMessage>[] script) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = Math.Min(Attempts, script.Length - 1);
            Attempts++;
            return Task.FromResult(script[index](Attempts));
        }
    }

    /// <summary>A read-only stream that throws the given exception on first read, simulating a mid-body connection reset.</summary>
    private sealed class ThrowingStream(Exception toThrow) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw toThrow;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw toThrow;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed class JsonPathReaderTests
{
    [Fact]
    public void Reads_scalar_and_wildcard_paths()
    {
        using var doc = JsonDocument.Parse("""{"access_token":"abc","data":[{"id":1},{"id":2},{"id":3}]}""");
        var root = doc.RootElement;
        Assert.Equal("abc", JsonPathReader.SelectValue(root, "access_token"));
        Assert.Equal(["1", "2", "3"], JsonPathReader.SelectValues(root, "$.data[*].id"));
        Assert.Equal(3, JsonPathReader.CountRecords(root, "$.data"));
        Assert.Equal("3", JsonPathReader.MaxColumn(root, "id", "$.data"));
    }

    [Fact]
    public void Auto_locates_record_array_and_counts_empty()
    {
        using var top = JsonDocument.Parse("[]");
        Assert.Equal(0, JsonPathReader.CountRecords(top.RootElement, null));
        using var wrapped = JsonDocument.Parse("""{"items":[{"a":1}]}""");
        Assert.Equal(1, JsonPathReader.CountRecords(wrapped.RootElement, null));
    }
}
