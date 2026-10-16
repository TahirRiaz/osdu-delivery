using System.Globalization;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Diagnostics;

namespace SqlFlow.Delivery.Telemetry;

/// <summary>
/// Sends the module's metrics where a deployment says (<see cref="TelemetryOptions"/>). The meters themselves are
/// always live, so nothing here changes what is measured: this only decides whether the measurements leave the
/// process, and to what.
/// <para>
/// A host adds this once, and every host adds it the same way: the export is a hosted service, which the control plane's
/// generic host starts with it, and which a SQLFlow worker node starts before it takes work and stops after it drains, as
/// it does every hosted service its modules register. So what a node reports and what the control plane reports are the
/// same metrics under the same names, built by one code path. A one-shot command starts no hosted service, and exports
/// nothing: it ends before a first export would.
/// </para>
/// </summary>
public static class DeliveryTelemetry
{
    /// <summary>The name the OTLP exporter's options are kept under, so what is configured here reaches no other exporter.</summary>
    private const string ExporterName = "osdu-delivery";

    /// <summary>The meters this exports: the module's own.</summary>
    public static IReadOnlyList<string> Meters { get; } = [DeliveryMetrics.MeterName];

    /// <summary>
    /// Registers the metrics export named by <paramref name="options"/> into <paramref name="services"/>: OpenTelemetry's
    /// meter provider with the one exporter the options name, built when the host starts its hosted services and disposed
    /// (flushing what is not exported yet) with the host's services. Whatever the options say, the host's start says once
    /// in the log where the metrics go, or that they go nowhere, because "no metrics arrived" is otherwise
    /// indistinguishable from "nothing happened".
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="options">Where the metrics go.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="InvalidOperationException">A setting cannot work; the message names it.</exception>
    public static IServiceCollection AddDeliveryMetricsExport(this IServiceCollection services, TelemetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (options.Enabled)
        {
            services
                .AddOpenTelemetry()
                .ConfigureResource(resource => Describe(resource, options))
                .WithMetrics(metrics =>
                {
                    foreach (var meter in Meters)
                    {
                        metrics.AddMeter(meter);
                    }

                    Export(metrics, services, options);
                });
        }

        // Registered after OpenTelemetry's own hosted service, so it speaks once the meter provider is built.
        services.AddHostedService(provider => new MetricsExportNotice(
            options, provider.GetService<ILoggerFactory>()?.CreateLogger(typeof(DeliveryTelemetry)) ?? NullLogger.Instance));
        return services;
    }

    /// <summary>What the metrics are attributed to: the service, and the instance so two replicas do not read as one.</summary>
    private static void Describe(ResourceBuilder resource, TelemetryOptions options)
        => resource.AddService(
            serviceName: options.ServiceName,
            serviceVersion: typeof(DeliveryTelemetry).Assembly.GetName().Version?.ToString(),
            serviceInstanceId: string.IsNullOrWhiteSpace(options.ServiceInstanceId) ? Environment.MachineName : options.ServiceInstanceId);

    /// <summary>
    /// Adds the one exporter the options name, with the reader interval they set, while the meter provider is registered:
    /// an exporter registers services of its own, which can no longer be added once the host's provider is built. A
    /// setting that carries a secret is resolved later, when the exporter is built, by the resolver the host registered
    /// (environment, Key Vault), never by one made here.
    /// </summary>
    private static void Export(MeterProviderBuilder metrics, IServiceCollection services, TelemetryOptions options)
    {
        var interval = (int)TimeSpan.FromSeconds(options.ExportSeconds).TotalMilliseconds;
        switch (options.Exporter)
        {
            case TelemetryExporter.Otlp:
                // The exporter's options are named, so the headers configured here reach this exporter alone.
                services.AddOptions<OtlpExporterOptions>(ExporterName).Configure<IServiceProvider>((exporter, provider) =>
                {
                    if (Resolve(options.OtlpHeadersRef, provider.GetService<ISecretResolver>(), nameof(TelemetryOptions.OtlpHeadersRef)) is { } headers)
                    {
                        exporter.Headers = headers;
                    }
                });
                metrics.AddOtlpExporter(ExporterName, (exporter, reader) =>
                {
                    if (!string.IsNullOrWhiteSpace(options.OtlpEndpoint))
                    {
                        exporter.Endpoint = new Uri(options.OtlpEndpoint);
                    }

                    exporter.Protocol = string.Equals(options.OtlpProtocol, "httpprotobuf", StringComparison.OrdinalIgnoreCase)
                        ? OtlpExportProtocol.HttpProtobuf
                        : OtlpExportProtocol.Grpc;
                    reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = interval;
                });
                break;

            case TelemetryExporter.AzureMonitor:
                // Azure Monitor's exporter takes its connection string as it is built, which is when the host's resolver
                // can be asked for it, and adds itself to a provider already built.
                services.ConfigureOpenTelemetryMeterProvider((provider, built) =>
                {
                    var connection = Resolve(options.AzureMonitorConnectionRef, provider.GetService<ISecretResolver>(), nameof(TelemetryOptions.AzureMonitorConnectionRef))
                        ?? throw new InvalidOperationException(
                            $"{TelemetryOptions.SectionName}:AzureMonitorConnectionRef resolved to nothing; the exporter has nowhere to send.");
                    built.AddAzureMonitorMetricExporter(exporter => exporter.ConnectionString = connection);
                });
                break;

            case TelemetryExporter.Console:
                metrics.AddConsoleExporter((_, reader) => reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = interval);
                break;

            case TelemetryExporter.None:
            default:
                break;
        }
    }

    /// <summary>
    /// A setting that carries a secret, as the host resolves it. A reference with no resolver is a configuration error
    /// worth stopping for: exporting to the wrong place, or not at all, is not something to discover from a graph.
    /// </summary>
    private static string? Resolve(string? reference, ISecretResolver? secrets, string setting)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        if (secrets is null)
        {
            throw new InvalidOperationException(
                $"{TelemetryOptions.SectionName}:{setting} is a secret reference and this host resolves none; register an {nameof(ISecretResolver)}.");
        }

        var resolved = secrets.Resolve(reference);
        return string.IsNullOrWhiteSpace(resolved) ? null : resolved;
    }

    /// <summary>Says once, as the host starts, where the module's metrics go, or that they go nowhere and how to read them.</summary>
    private sealed class MetricsExportNotice(TelemetryOptions options, ILogger logger) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (options.Enabled)
            {
                logger.LogInformation(
                    "Metrics on the meter {Meter} are exported to {Exporter} every {Seconds}s as service {Service}.",
                    DeliveryMetrics.MeterName,
                    options.Exporter,
                    options.ExportSeconds.ToString(CultureInfo.InvariantCulture),
                    options.ServiceName);
            }
            else
            {
                logger.LogInformation(
                    "Metrics are published on the meter {Meter} and exported nowhere ({Section}:Exporter is none); "
                    + "'dotnet-counters monitor --counters {Meter}' reads them on this process.",
                    DeliveryMetrics.MeterName, TelemetryOptions.SectionName, DeliveryMetrics.MeterName);
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
