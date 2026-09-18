using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Telemetry;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Where a deployment sends the module's metrics (<see cref="TelemetryOptions"/>). Nothing leaves the process unless a
/// deployment says so; when it does, the settings are checked before an exporter is built, and the ones that carry a
/// secret are references the host resolves rather than values in a configuration file.
/// </summary>
public sealed class TelemetryOptionsTests
{
    [Fact]
    public void Nothing_is_exported_until_a_deployment_names_an_exporter()
    {
        var options = new TelemetryOptions();

        Assert.Equal(TelemetryExporter.None, options.Exporter);
        Assert.False(options.Enabled);

        // The meters still publish: an in-process listener reads them whatever this says.
        Assert.Null(DeliveryTelemetry.BuildMeterProvider(options, secrets: null));
        Assert.Empty(new ServiceCollection().AddNodeMetricsExport(options));
    }

    [Theory]
    [InlineData(TelemetryExporter.Otlp)]
    [InlineData(TelemetryExporter.Console)]
    public void An_exporter_a_deployment_names_is_built(TelemetryExporter exporter)
    {
        var options = new TelemetryOptions { Exporter = exporter, ExportSeconds = 5, OtlpEndpoint = "http://127.0.0.1:4317" };

        using var provider = DeliveryTelemetry.BuildMeterProvider(options, secrets: null);

        Assert.NotNull(provider);
    }

    [Fact]
    public void A_node_reads_where_its_metrics_go_from_its_environment()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["OSDU_TELEMETRY_EXPORTER"] = "otlp",
            ["OSDU_TELEMETRY_OTLP_ENDPOINT"] = "http://collector:4318/v1/metrics",
            ["OSDU_TELEMETRY_OTLP_PROTOCOL"] = "httpprotobuf",
            ["OSDU_TELEMETRY_OTLP_HEADERS"] = "${env:OTLP_HEADERS}",
            ["OSDU_TELEMETRY_EXPORT_SECONDS"] = "30",
            ["OSDU_TELEMETRY_SERVICE_NAME"] = "osdu-delivery-node",
            ["OSDU_TELEMETRY_SERVICE_INSTANCE"] = "node-7",
        };

        var options = TelemetryOptions.FromEnvironment(name => environment.GetValueOrDefault(name));

        Assert.Equal(TelemetryExporter.Otlp, options.Exporter);
        Assert.Equal("http://collector:4318/v1/metrics", options.OtlpEndpoint);
        Assert.Equal("httpprotobuf", options.OtlpProtocol);
        Assert.Equal("${env:OTLP_HEADERS}", options.OtlpHeadersRef);
        Assert.Equal(30, options.ExportSeconds);
        Assert.Equal("osdu-delivery-node", options.ServiceName);
        Assert.Equal("node-7", options.ServiceInstanceId);
        options.Validate();
    }

    [Fact]
    public void An_environment_that_asks_for_an_exporter_nobody_has_is_refused_rather_than_read_as_none()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal) { ["OSDU_TELEMETRY_EXPORTER"] = "prometheus" };

        var refused = Assert.Throws<InvalidOperationException>(() => TelemetryOptions.FromEnvironment(name => environment.GetValueOrDefault(name)));

        Assert.Contains("OSDU_TELEMETRY_EXPORTER is 'prometheus'", refused.Message, StringComparison.Ordinal);
        Assert.Contains("otlp", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ExportSeconds", "must be at least 5")]
    [InlineData("ServiceName", "must name the service")]
    [InlineData("OtlpEndpoint", "is not an http or https URL")]
    [InlineData("OtlpProtocol", "is 'grpc' or 'httpprotobuf'")]
    [InlineData("AzureMonitorConnectionRef", "must name the Azure Monitor connection string as a reference")]
    [InlineData("OtlpHeadersRef", "must be a secret reference")]
    public void A_setting_that_cannot_work_is_refused_by_name(string setting, string says)
    {
        var options = new TelemetryOptions { Exporter = TelemetryExporter.Otlp, OtlpEndpoint = "http://collector:4317" };
        switch (setting)
        {
            case "ExportSeconds": options.ExportSeconds = 1; break;
            case "ServiceName": options.ServiceName = " "; break;
            case "OtlpEndpoint": options.OtlpEndpoint = "collector:4317"; break;
            case "OtlpProtocol": options.OtlpProtocol = "thrift"; break;
            case "AzureMonitorConnectionRef": options.Exporter = TelemetryExporter.AzureMonitor; break;
            default: options.OtlpHeadersRef = "api-key=a-real-key"; break;
        }

        var refused = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains(says, refused.Message, StringComparison.Ordinal);
        Assert.Contains(setting is "AzureMonitorConnectionRef" or "OtlpHeadersRef" ? setting : TelemetryOptions.SectionName, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_secret_setting_is_the_hosts_reference_to_resolve_and_never_the_value()
    {
        var options = new TelemetryOptions
        {
            Exporter = TelemetryExporter.AzureMonitor,
            AzureMonitorConnectionRef = "${env:AZURE_MONITOR_CONNECTION}",
        };
        options.Validate();

        // With no resolver the exporter is refused rather than built against the reference text itself.
        var refused = Assert.Throws<InvalidOperationException>(() => DeliveryTelemetry.BuildMeterProvider(options, secrets: null));
        Assert.Contains("secret reference", refused.Message, StringComparison.Ordinal);

        using var built = DeliveryTelemetry.BuildMeterProvider(options, new FakeSecrets("InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://example.test/"));
        Assert.NotNull(built);
    }

    /// <summary>A resolver that answers every reference with one value, as a host's own would answer from its vault.</summary>
    private sealed class FakeSecrets(string value) : ISecretResolver
    {
        public string Resolve(string reference) => value;

        public Task<string> ResolveAsync(string reference, CancellationToken ct = default) => Task.FromResult(value);
    }
}
