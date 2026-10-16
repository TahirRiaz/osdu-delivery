using SqlFlow.Delivery.Telemetry;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// An OTLP endpoint the settings refuse is named the way every URL in a message is named: scheme, host, port and path,
/// never the user info, query or fragment where a collector's token or key can sit, so the refusal cannot carry a
/// credential into a log.
/// </summary>
public sealed class TelemetryEndpointRedactionTests
{
    [Theory]
    [InlineData("ftp://exporter:s3cret-pass@collector:4317/v1/metrics?api_key=k3y-value#frag", "ftp://collector:4317/v1/metrics")]
    [InlineData("grpc://collector:4317/?token=k3y-value", "grpc://collector:4317/")]
    [InlineData("collector:4317?key=k3y-value", "collector:4317")]
    public void A_refused_endpoint_is_named_without_its_credentials(string endpoint, string named)
    {
        var options = new TelemetryOptions { Exporter = TelemetryExporter.Otlp, OtlpEndpoint = endpoint };

        var refused = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains($"is not an http or https URL: '{named}'", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret-pass", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("k3y-value", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("exporter:", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_http_endpoint_with_credentials_is_accepted_and_only_its_refusal_is_redacted()
    {
        // The setting itself is the deployment's to give; only what a message says of it is bounded.
        new TelemetryOptions { Exporter = TelemetryExporter.Otlp, OtlpEndpoint = "https://collector.example.test:4318/v1/metrics?api_key=k3y-value" }.Validate();
    }
}
