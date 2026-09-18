using System.Net;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Diagnostics;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

public sealed class DeliveryMetricsTests
{
    [Fact]
    public void A_settled_try_is_counted_with_its_flow_route_and_outcome()
    {
        using var capture = new MetricsCapture();
        var flow = $"metrics-{Guid.NewGuid():N}";

        DeliveryMetrics.RecordSettled(flow, "dspdm", "held", TimeSpan.FromMilliseconds(250));
        DeliveryMetrics.RecordSettled(flow, "dspdm", "delivered", TimeSpan.FromSeconds(-1));

        var counted = capture.Of("osdu_delivery.records", "flow", flow);
        Assert.Equal(["held", "delivered"], counted.Select(c => c.Tags["outcome"]));
        Assert.All(counted, c => Assert.Equal("dspdm", c.Tags["route"]));
        Assert.All(counted, c => Assert.Equal(1, c.Value));
        Assert.Equal([0.25, 0], capture.Of("osdu_delivery.record.duration", "flow", flow).Select(c => c.Value));
        Assert.Equal("4xx", DeliveryMetrics.StatusClass(404));
        Assert.Equal("other", DeliveryMetrics.StatusClass(101));
    }

    [Fact]
    public void A_settled_probe_is_counted_with_its_flow_interface_and_outcome()
    {
        using var capture = new MetricsCapture();
        var flow = $"metrics-{Guid.NewGuid():N}";

        DeliveryMetrics.ProbeSettled($"{flow}/welllogs", "welllogs", "reachable");
        DeliveryMetrics.ProbeSettled($"{flow}/welllogs", "welllogs", "unreachable");
        DeliveryMetrics.ProbeSettled(flow, null, "error");

        var counted = capture.Of("osdu_delivery.probes", "flow", $"{flow}/welllogs");
        Assert.Equal(["reachable", "unreachable"], counted.Select(c => c.Tags["outcome"]));
        Assert.All(counted, c => Assert.Equal("welllogs", c.Tags["interface"]));
        Assert.All(counted, c => Assert.Equal(1, c.Value));

        // A flow in the single form has no interface, and is counted under an empty one rather than left out.
        var single = Assert.Single(capture.Of("osdu_delivery.probes", "flow", flow));
        Assert.Equal(("error", string.Empty), (single.Tags["outcome"], single.Tags["interface"]));
    }

    [Fact]
    public async Task Every_call_is_counted_with_its_result_and_every_retry_with_its_cause()
    {
        using var capture = new MetricsCapture();
        var host = $"metrics-{Guid.NewGuid():N}.example.com";
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Get, "/flaky", hit => hit == 0 ? FakeHttpHandler.Json(HttpStatusCode.ServiceUnavailable, "{}") : FakeHttpHandler.Json(HttpStatusCode.OK, "{}"));
        using var runtime = new HttpRuntime(
            new FlowReliability { Retry = new FlowRetry { Attempts = 3, BaseDelayMs = 1, MaxDelayMs = 1 } },
            new SecretResolver([new EnvSecretProvider()]), new TestClock(), handler, privateNetworks: []);

        await runtime.Data.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"https://{host}/flaky"));
        await Assert.ThrowsAsync<UrlRefusedException>(() => runtime.Data.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"http://{host}.localhost/x")));

        var requests = capture.Of("osdu_delivery.http.requests", "host", host);
        Assert.Equal(["5xx", "2xx"], requests.Select(r => r.Tags["result"]));
        Assert.All(requests, r => Assert.Equal("GET", r.Tags["method"]));
        var retries = capture.Of("osdu_delivery.http.retries", "host", host);
        Assert.Equal(["503"], retries.Select(r => r.Tags["reason"]));
        Assert.Equal(2, capture.Of("osdu_delivery.http.request.duration", "host", host).Count);
        Assert.Equal(["refused"], capture.Of("osdu_delivery.http.requests", "host", $"{host}.localhost").Select(r => r.Tags["result"]));
    }

    [Fact]
    public async Task An_attempt_is_counted_once_by_how_it_ended_and_the_wait_after_it_is_not_part_of_it()
    {
        using var capture = new MetricsCapture();
        var host = $"metrics-{Guid.NewGuid():N}.example.com";
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Get, "/cut", hit => hit == 0
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream()) }
                : FakeHttpHandler.Json(HttpStatusCode.OK, "{}"));
        var clock = new SteppingClock();
        using var runtime = new HttpRuntime(
            new FlowReliability { Retry = new FlowRetry { Attempts = 2, BaseDelayMs = 5000, MaxDelayMs = 5000 } },
            new SecretResolver([new EnvSecretProvider()]), clock, handler, privateNetworks: []);

        await runtime.Data.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"https://{host}/cut"));

        // The first answer broke off after its status: that attempt is a transport failure, and not a 2xx as well.
        Assert.Equal(["transport", "2xx"], capture.Of("osdu_delivery.http.requests", "host", host).Select(r => r.Tags["result"]));
        Assert.Equal(["transport"], capture.Of("osdu_delivery.http.retries", "host", host).Select(r => r.Tags["reason"]));

        // Five seconds passed between the attempts, and neither attempt's duration holds them.
        Assert.Equal(TimeSpan.FromSeconds(5), clock.GetElapsedTime(0));
        Assert.Equal([0d, 0d], capture.Of("osdu_delivery.http.request.duration", "host", host).Select(d => d.Value));
    }

    [Fact]
    public async Task A_call_its_caller_stopped_waiting_for_is_cancelled_and_a_redirect_loop_is_an_error()
    {
        using var capture = new MetricsCapture();
        var host = $"metrics-{Guid.NewGuid():N}.example.com";
        using var stop = new CancellationTokenSource();
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Get, "/stopped", _ =>
            {
                // The status arrives, and the caller gives up before the body is read.
                stop.Cancel();
                return FakeHttpHandler.Json(HttpStatusCode.OK, "{}");
            })
            .On(HttpMethod.Get, "/loop", _ =>
            {
                var moved = new HttpResponseMessage(HttpStatusCode.Found);
                moved.Headers.Location = new Uri($"https://{host}/loop");
                return moved;
            });
        using var runtime = new HttpRuntime(
            new FlowReliability(), new SecretResolver([new EnvSecretProvider()]), new TestClock(), handler, privateNetworks: []);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runtime.Data.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"https://{host}/stopped"), ct: stop.Token));
        await Assert.ThrowsAsync<DeliveryException>(
            () => runtime.Data.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"https://{host}/loop")));

        Assert.Equal(["cancelled", "error"], capture.Of("osdu_delivery.http.requests", "host", host).Select(r => r.Tags["result"]));
        Assert.Empty(capture.Of("osdu_delivery.http.retries", "host", host));
    }

    /// <summary>A clock whose timers fire at once, moving time forward by what they waited for.</summary>
    private sealed class SteppingClock : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            Interlocked.Add(ref _timestamp, dueTime.Ticks);
            callback(state);
            return new FiredTimer();
        }

        private sealed class FiredTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>A response body whose connection resets on the first read.</summary>
    private sealed class BrokenStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw Reset();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(Reset());

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromException<int>(Reset());

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private static IOException Reset() => new("The connection was reset by the peer.");
    }
}
