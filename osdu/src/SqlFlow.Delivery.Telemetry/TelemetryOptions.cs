using System.Globalization;

namespace SqlFlow.Delivery.Telemetry;

/// <summary>Where a deployment sends the module's metrics.</summary>
public enum TelemetryExporter
{
    /// <summary>Nowhere. The meters still publish, so <c>dotnet-counters</c> and any in-process listener read them.</summary>
    None,

    /// <summary>An OTLP endpoint: a collector, or any backend that speaks the protocol.</summary>
    Otlp,

    /// <summary>Azure Monitor (Application Insights), through its own exporter.</summary>
    AzureMonitor,

    /// <summary>The process's own console, for a node an operator is watching.</summary>
    Console,
}

/// <summary>
/// Where the module's metrics go (section <c>Osdu:Telemetry</c>). The meters publish whatever a deployment decides
/// here: with no exporter the metrics are still readable in process (<c>dotnet-counters monitor --counters
/// SqlFlow.Delivery</c>), and with one they leave the process on the schedule below.
/// <para>
/// The choice is deliberately a deployment's, not the product's: OTLP reaches any backend that speaks it, Azure
/// Monitor is what this product's own deployment runs on, and the console is for watching a node. Every secret among
/// these settings is a reference (<c>${env:NAME}</c>, <c>${keyvault:NAME}</c>) resolved on the host, never a literal.
/// </para>
/// </summary>
public sealed class TelemetryOptions
{
    /// <summary>The configuration section a host binds this from.</summary>
    public const string SectionName = "Osdu:Telemetry";

    /// <summary>The floor under <see cref="ExportSeconds"/>: below this the export costs more than it tells.</summary>
    public const int MinimumExportSeconds = 5;

    /// <summary>Which exporter carries the metrics, if any. Default <see cref="TelemetryExporter.None"/>.</summary>
    public TelemetryExporter Exporter { get; set; } = TelemetryExporter.None;

    /// <summary>
    /// The OTLP endpoint, when <see cref="Exporter"/> is <see cref="TelemetryExporter.Otlp"/>: the collector's URL
    /// (<c>http://collector:4317</c> for grpc, <c>http://collector:4318/v1/metrics</c> for HTTP). Left out, the
    /// exporter reads the standard <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> environment variable and its defaults.
    /// </summary>
    public string? OtlpEndpoint { get; set; }

    /// <summary>The OTLP transport: <c>grpc</c> (default) or <c>httpprotobuf</c>.</summary>
    public string OtlpProtocol { get; set; } = "grpc";

    /// <summary>
    /// Headers the OTLP exporter sends, as <c>key=value</c> pairs separated by commas: how a hosted backend takes its
    /// API key. A secret reference, never a literal, since the whole value is one a vault can hold.
    /// </summary>
    public string? OtlpHeadersRef { get; set; }

    /// <summary>
    /// The Azure Monitor connection string, when <see cref="Exporter"/> is <see cref="TelemetryExporter.AzureMonitor"/>.
    /// A reference: the string carries an instrumentation key.
    /// </summary>
    public string? AzureMonitorConnectionRef { get; set; }

    /// <summary>Seconds between exports. Default 60, never under <see cref="MinimumExportSeconds"/>.</summary>
    public int ExportSeconds { get; set; } = 60;

    /// <summary>
    /// The service name the metrics are attributed to, which is what a backend groups them by. Default
    /// <c>osdu-delivery</c>; a deployment running the control plane and its nodes apart names them apart.
    /// </summary>
    public string ServiceName { get; set; } = "osdu-delivery";

    /// <summary>
    /// The instance the metrics come from, so two replicas do not read as one. Default the machine name, which is the
    /// pod or container name where this product runs.
    /// </summary>
    public string? ServiceInstanceId { get; set; }

    /// <summary>
    /// The options a process with no configuration binder reads from its environment: a node, which takes every setting
    /// it has that way (<c>OSDU_TELEMETRY_EXPORTER</c>, <c>_OTLP_ENDPOINT</c>, <c>_OTLP_PROTOCOL</c>, <c>_OTLP_HEADERS</c>,
    /// <c>_AZURE_MONITOR_CONNECTION</c>, <c>_EXPORT_SECONDS</c>, <c>_SERVICE_NAME</c>, <c>_SERVICE_INSTANCE</c>). An
    /// exporter name the enum does not have is a configuration error rather than a silent "none": a deployment that
    /// asked for export and got none would find out from an empty dashboard.
    /// </summary>
    /// <param name="variable">Reads one environment variable.</param>
    /// <exception cref="InvalidOperationException">A variable holds something the setting cannot be.</exception>
    public static TelemetryOptions FromEnvironment(Func<string, string?> variable)
    {
        ArgumentNullException.ThrowIfNull(variable);
        var options = new TelemetryOptions();
        if (Text(variable, "OSDU_TELEMETRY_EXPORTER") is { } exporter)
        {
            options.Exporter = Enum.TryParse<TelemetryExporter>(exporter, ignoreCase: true, out var parsed)
                ? parsed
                : throw new InvalidOperationException(
                    $"OSDU_TELEMETRY_EXPORTER is '{exporter}'; it is one of {string.Join(", ", Enum.GetNames<TelemetryExporter>()).ToLowerInvariant()}.");
        }

        options.OtlpEndpoint = Text(variable, "OSDU_TELEMETRY_OTLP_ENDPOINT");
        options.OtlpProtocol = Text(variable, "OSDU_TELEMETRY_OTLP_PROTOCOL") ?? options.OtlpProtocol;
        options.OtlpHeadersRef = Text(variable, "OSDU_TELEMETRY_OTLP_HEADERS");
        options.AzureMonitorConnectionRef = Text(variable, "OSDU_TELEMETRY_AZURE_MONITOR_CONNECTION");
        options.ServiceName = Text(variable, "OSDU_TELEMETRY_SERVICE_NAME") ?? options.ServiceName;
        options.ServiceInstanceId = Text(variable, "OSDU_TELEMETRY_SERVICE_INSTANCE");
        if (Text(variable, "OSDU_TELEMETRY_EXPORT_SECONDS") is { } seconds)
        {
            options.ExportSeconds = int.TryParse(seconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : throw new InvalidOperationException($"OSDU_TELEMETRY_EXPORT_SECONDS is '{seconds}'; it is a whole number of seconds.");
        }

        return options;
    }

    /// <summary>One environment variable, or null when it is absent or blank.</summary>
    private static string? Text(Func<string, string?> variable, string name)
    {
        var value = variable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>True when an exporter is to be built at all.</summary>
    public bool Enabled => Exporter != TelemetryExporter.None;

    /// <exception cref="InvalidOperationException">A setting is missing or outside its range; the message names it.</exception>
    public void Validate()
    {
        if (ExportSeconds < MinimumExportSeconds)
        {
            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture, $"{SectionName}:ExportSeconds must be at least {MinimumExportSeconds}."));
        }

        if (string.IsNullOrWhiteSpace(ServiceName))
        {
            throw new InvalidOperationException($"{SectionName}:ServiceName must name the service the metrics are attributed to.");
        }

        // 'collector:4317' parses as an absolute URI whose scheme is 'collector', so the scheme is what is checked:
        // an endpoint that is not http or https would fail at the first export rather than here.
        if (Exporter == TelemetryExporter.Otlp
            && !string.IsNullOrWhiteSpace(OtlpEndpoint)
            && (!Uri.TryCreate(OtlpEndpoint, UriKind.Absolute, out var endpoint)
                || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)))
        {
            throw new InvalidOperationException(
                $"{SectionName}:OtlpEndpoint is not an http or https URL: '{OtlpEndpoint}'. A collector is reached at "
                + "http://host:4317 (grpc) or http://host:4318/v1/metrics (httpprotobuf).");
        }

        if (Exporter == TelemetryExporter.Otlp
            && !string.Equals(OtlpProtocol, "grpc", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(OtlpProtocol, "httpprotobuf", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{SectionName}:OtlpProtocol is 'grpc' or 'httpprotobuf', not '{OtlpProtocol}'.");
        }

        if (Exporter == TelemetryExporter.AzureMonitor && string.IsNullOrWhiteSpace(AzureMonitorConnectionRef))
        {
            throw new InvalidOperationException(
                $"{SectionName}:AzureMonitorConnectionRef must name the Azure Monitor connection string as a reference "
                + "(${env:NAME} or ${keyvault:NAME}); the exporter has nowhere to send without it.");
        }

        foreach (var (name, value) in new[] { (nameof(OtlpHeadersRef), OtlpHeadersRef), (nameof(AzureMonitorConnectionRef), AzureMonitorConnectionRef) })
        {
            if (!string.IsNullOrWhiteSpace(value) && !value.Contains("${", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{SectionName}:{name} must be a secret reference (${{env:NAME}} or ${{keyvault:NAME}}), not the value itself.");
            }
        }
    }
}
