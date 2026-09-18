using System.Globalization;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
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
/// A host adds this once. The control plane runs on a generic host, where the exporter is a hosted service; a node
/// runs from a plain service provider, where the meter provider is a singleton that lives as long as the provider.
/// Both paths build the same exporter from the same options, so what a node reports and what the control plane reports
/// are the same metrics under the same names.
/// </para>
/// </summary>
public static class DeliveryTelemetry
{
    /// <summary>The meters this exports: the module's own, and the runtime's HTTP client counters beside them.</summary>
    public static IReadOnlyList<string> Meters { get; } = [DeliveryMetrics.MeterName];

    /// <summary>
    /// Registers the metrics export named by <paramref name="options"/> into <paramref name="services"/>, for a host
    /// that starts hosted services (the control plane). With no exporter configured this registers nothing and says so
    /// once in the log, because "no metrics arrived" is otherwise indistinguishable from "nothing happened".
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="options">Where the metrics go.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddDeliveryMetricsExport(this IServiceCollection services, TelemetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (!options.Enabled)
        {
            return services;
        }

        services
            .AddOpenTelemetry()
            .ConfigureResource(resource => Describe(resource, options))
            .WithMetrics(metrics =>
            {
                foreach (var meter in Meters)
                {
                    metrics.AddMeter(meter);
                }
            });

        // The exporter is configured against the host's own provider, so a secret reference among the settings is
        // resolved by whatever the host registered (environment, Key Vault) rather than by a provider built here.
        services.ConfigureOpenTelemetryMeterProvider((provider, metrics) => Export(metrics, options, provider.GetService<ISecretResolver>()));
        return services;
    }

    /// <summary>
    /// Registers the metrics export for a process with no generic host, whose services are a plain provider (a node).
    /// Nothing is registered when no exporter is configured, so the provider carries no dead singleton; the meter
    /// provider that is registered lives as long as the container's services and flushes when they are disposed.
    /// </summary>
    /// <param name="services">The node's services.</param>
    /// <param name="options">Where the metrics go.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddNodeMetricsExport(this IServiceCollection services, TelemetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (!options.Enabled)
        {
            return services;
        }

        services.AddSingleton(provider => BuildMeterProvider(
            options,
            provider.GetService<ISecretResolver>(),
            provider.GetService<ILoggerFactory>()?.CreateLogger(typeof(DeliveryTelemetry)))
            ?? throw new InvalidOperationException($"{TelemetryOptions.SectionName}:Exporter names an exporter, and none was built."));
        return services;
    }

    /// <summary>
    /// Builds the metrics export named by <paramref name="options"/> for a process with no generic host (a node, the
    /// CLI), or null when no exporter is configured. The caller keeps the provider for the life of the process and
    /// disposes it at the end, which flushes what has not been exported yet.
    /// </summary>
    /// <param name="options">Where the metrics go.</param>
    /// <param name="secrets">Resolves the references among the options; required when one carries a secret.</param>
    /// <param name="logger">Says once where the metrics go, or that they go nowhere.</param>
    public static MeterProvider? BuildMeterProvider(TelemetryOptions options, ISecretResolver? secrets, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (!options.Enabled)
        {
            logger?.LogInformation(
                "Metrics are published on the meter {Meter} and exported nowhere ({Section}:Exporter is none); "
                + "'dotnet-counters monitor --counters {Meter}' reads them on this process.",
                DeliveryMetrics.MeterName, TelemetryOptions.SectionName, DeliveryMetrics.MeterName);
            return null;
        }

        var builder = Sdk.CreateMeterProviderBuilder().ConfigureResource(resource => Describe(resource, options));
        foreach (var meter in Meters)
        {
            builder.AddMeter(meter);
        }

        Export(builder, options, secrets);
        logger?.LogInformation(
            "Metrics on the meter {Meter} are exported to {Exporter} every {Seconds}s as service {Service}.",
            DeliveryMetrics.MeterName,
            options.Exporter,
            options.ExportSeconds.ToString(CultureInfo.InvariantCulture),
            options.ServiceName);
        return builder.Build();
    }

    /// <summary>What the metrics are attributed to: the service, and the instance so two replicas do not read as one.</summary>
    private static void Describe(ResourceBuilder resource, TelemetryOptions options)
        => resource.AddService(
            serviceName: options.ServiceName,
            serviceVersion: typeof(DeliveryTelemetry).Assembly.GetName().Version?.ToString(),
            serviceInstanceId: string.IsNullOrWhiteSpace(options.ServiceInstanceId) ? Environment.MachineName : options.ServiceInstanceId);

    /// <summary>Adds the one exporter the options name, with the reader interval they set.</summary>
    private static void Export(MeterProviderBuilder metrics, TelemetryOptions options, ISecretResolver? secrets)
    {
        var interval = (int)TimeSpan.FromSeconds(options.ExportSeconds).TotalMilliseconds;
        switch (options.Exporter)
        {
            case TelemetryExporter.Otlp:
                metrics.AddOtlpExporter((exporter, reader) =>
                {
                    if (!string.IsNullOrWhiteSpace(options.OtlpEndpoint))
                    {
                        exporter.Endpoint = new Uri(options.OtlpEndpoint);
                    }

                    exporter.Protocol = string.Equals(options.OtlpProtocol, "httpprotobuf", StringComparison.OrdinalIgnoreCase)
                        ? OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf
                        : OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
                    if (Resolve(options.OtlpHeadersRef, secrets, nameof(TelemetryOptions.OtlpHeadersRef)) is { } headers)
                    {
                        exporter.Headers = headers;
                    }

                    reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = interval;
                });
                break;

            case TelemetryExporter.AzureMonitor:
                var connection = Resolve(options.AzureMonitorConnectionRef, secrets, nameof(TelemetryOptions.AzureMonitorConnectionRef))
                    ?? throw new InvalidOperationException(
                        $"{TelemetryOptions.SectionName}:AzureMonitorConnectionRef resolved to nothing; the exporter has nowhere to send.");
                metrics.AddAzureMonitorMetricExporter(exporter => exporter.ConnectionString = connection);
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
}
