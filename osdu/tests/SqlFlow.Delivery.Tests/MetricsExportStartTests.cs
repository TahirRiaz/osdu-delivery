using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Diagnostics;
using SqlFlow.Delivery.Telemetry;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The metrics export is a hosted service, so it starts when a host starts its hosted services: the control plane's
/// generic host, and a SQLFlow worker node, which starts the hosted services its modules register before it takes work.
/// Registered but never started, nothing is exported, which is what a node did when the export was a singleton nothing
/// resolved. A small OTLP collector on a loopback port stands in for the backend.
/// </summary>
public sealed class MetricsExportStartTests
{
    [Fact]
    public async Task Once_the_host_starts_its_hosted_services_the_metrics_reach_the_collector()
    {
        using var collector = new OtlpCollector();
        var logs = new ListLogger();
        await using var provider = Services(collector, logs);

        var hosted = provider.GetServices<IHostedService>().ToList();
        foreach (var service in hosted)
        {
            await service.StartAsync(CancellationToken.None);
        }

        DeliveryMetrics.ProbeSettled("metrics-export-start", "partition-a", null, "reachable");
        foreach (var service in Enumerable.Reverse(hosted))
        {
            await service.StopAsync(CancellationToken.None);
        }

        // Disposing the host's services disposes the meter provider, which exports what it holds before it goes.
        await provider.DisposeAsync();

        var request = await collector.FirstRequestAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(request);
        Assert.StartsWith("POST /v1/metrics ", request, StringComparison.Ordinal);

        // The headers are a reference the host's own resolver answered when the exporter was built.
        Assert.Contains("\nx-collector-key: resolved-by-the-host", request, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(logs.Lines, line => line.Contains("exported to Otlp every 5s as service metrics-export-start", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Registered_but_never_started_the_export_sends_nothing()
    {
        using var collector = new OtlpCollector();
        var logs = new ListLogger();
        await using var provider = Services(collector, logs);

        DeliveryMetrics.ProbeSettled("metrics-export-start", "partition-a", null, "reachable");
        await provider.DisposeAsync();

        Assert.Null(await collector.FirstRequestAsync(TimeSpan.FromSeconds(3)));
        Assert.Empty(logs.Lines);
    }

    [Fact]
    public async Task With_no_exporter_the_start_says_the_metrics_go_nowhere_and_how_to_read_them()
    {
        var logs = new ListLogger();
        var services = new ServiceCollection().AddSingleton<ILoggerFactory>(new ListLoggerFactory(logs));
        await using var provider = services.AddDeliveryMetricsExport(new TelemetryOptions()).BuildServiceProvider();

        foreach (var service in provider.GetServices<IHostedService>())
        {
            await service.StartAsync(CancellationToken.None);
        }

        var line = Assert.Single(logs.Lines);
        Assert.Contains("exported nowhere (Osdu:Telemetry:Exporter is none)", line, StringComparison.Ordinal);
        Assert.Contains("dotnet-counters monitor --counters SqlFlow.Delivery", line, StringComparison.Ordinal);
    }

    private static ServiceProvider Services(OtlpCollector collector, ListLogger logs)
    {
        var options = new TelemetryOptions
        {
            Exporter = TelemetryExporter.Otlp,
            OtlpEndpoint = collector.Endpoint,
            OtlpProtocol = "httpprotobuf",
            OtlpHeadersRef = "${env:COLLECTOR_HEADERS}",
            ExportSeconds = 5,
            ServiceName = "metrics-export-start",
        };

        return new ServiceCollection()
            .AddSingleton<ILoggerFactory>(new ListLoggerFactory(logs))
            .AddSingleton<ISecretResolver>(new HostSecrets())
            .AddDeliveryMetricsExport(options)
            .BuildServiceProvider();
    }

    /// <summary>The host's resolver: it answers the one reference the options name, as a node answers from its environment.</summary>
    private sealed class HostSecrets : ISecretResolver
    {
        public string Resolve(string reference)
            => reference == "${env:COLLECTOR_HEADERS}" ? "x-collector-key=resolved-by-the-host" : throw new InvalidOperationException($"unexpected reference {reference}");

        public Task<string> ResolveAsync(string reference, CancellationToken ct = default) => Task.FromResult(Resolve(reference));
    }

    /// <summary>An OTLP/HTTP endpoint on a loopback port: it answers every request 200 and keeps the first request line.</summary>
    private sealed class OtlpCollector : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly TaskCompletionSource<string> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _accepting;

        public OtlpCollector()
        {
            _listener.Start();
            Endpoint = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1/metrics";
            _accepting = AcceptAsync();
        }

        public string Endpoint { get; }

        public async Task<string?> FirstRequestAsync(TimeSpan wait)
        {
            var finished = await Task.WhenAny(_first.Task, Task.Delay(wait)).ConfigureAwait(false);
            return finished == _first.Task ? await _first.Task.ConfigureAwait(false) : null;
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }

                using (client)
                {
                    await AnswerAsync(client.GetStream()).ConfigureAwait(false);
                }
            }
        }

        private async Task AnswerAsync(NetworkStream stream)
        {
            var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var head = new StringBuilder(await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false) ?? string.Empty);
            var length = 0;
            string? header;
            while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false)))
            {
                head.Append('\n').Append(header);
                if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    length = int.Parse(header["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            // The body is protobuf; the reader's buffer may hold part of it, and what is left is read off the stream.
            var body = new char[length];
            var read = 0;
            while (read < length)
            {
                var n = await reader.ReadAsync(body.AsMemory(read, length - read), _stop.Token).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            var answer = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/x-protobuf\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(answer, _stop.Token).ConfigureAwait(false);
            await stream.FlushAsync(_stop.Token).ConfigureAwait(false);
            _first.TrySetResult(head.ToString());
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            try
            {
                _accepting.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // The accept loop ends on the stopped listener; how it ended is no part of the test.
            }

            _listener.Dispose();
            _stop.Dispose();
        }
    }

    private sealed class ListLogger : ILogger
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return [.. _lines];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var line = formatter(state, exception);
            if (!line.Contains("Metrics", StringComparison.Ordinal))
            {
                return;
            }

            lock (_lines)
            {
                _lines.Add(line);
            }
        }
    }

    private sealed class ListLoggerFactory(ListLogger logger) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => logger;

        public void Dispose()
        {
        }
    }
}
