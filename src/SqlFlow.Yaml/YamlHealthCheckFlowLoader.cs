using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.HealthChecks;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SqlFlow.Yaml;

/// <summary>
/// A parsed health-check document (flowType: hc): the flow itself plus the document-local connection registry
/// it declares. The connections become an in-memory data-source store, so the flow runs through the exact
/// same resolver and runner as full mode, with no control database anywhere.
/// </summary>
public sealed record HealthCheckDocument
{
    public required HealthCheckFlow Flow { get; init; }

    /// <summary>The document's named connections (the <c>connections:</c> block plus any synthesized from a
    /// direct <c>connection:</c> on the target endpoint).</summary>
    public required IReadOnlyList<DataSource> Connections { get; init; }
}

/// <summary>
/// Loads a health-check flow (an AutoML anomaly check over one monitored table's per-date metric) from YAML.
/// YamlDotNet handles the grammar; this class is the mapping/validation layer that turns the parsed document
/// into a validated <see cref="HealthCheckDocument"/>.
/// </summary>
public sealed class YamlHealthCheckFlowLoader
{
    private const string TargetConnectionName = "target";

    private const int MaxExperimentSecondsCeiling = 86_400;

    // The unquoted-scalar option types the connections block's map form the same way the other relational
    // loaders do; every typed DTO property is unaffected by it.
    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .WithAttemptingUnquotedStringTypeDeserialization()
        .Build();

    public HealthCheckDocument LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Pipeline file not found: '{path}'.");
        }

        return Parse(File.ReadAllText(path), path);
    }

    public HealthCheckDocument Parse(string yaml, string source = "<inline>")
    {
        HealthCheckYaml? dto;
        try
        {
            dto = _deserializer.Deserialize<HealthCheckYaml>(yaml);
        }
        catch (YamlException ex)
        {
            throw new FlowValidationException($"{source}: invalid YAML - {ex.Message}", ex);
        }

        if (dto is null)
        {
            throw new FlowValidationException($"{source}: the document is empty.");
        }

        return Map(dto, source);
    }

    private static HealthCheckDocument Map(HealthCheckYaml y, string source)
    {
        var name = YamlDocumentParts.RequireFlowName(y.Name, "a health-check flow", source);
        var connections = YamlDocumentParts.MapConnections(y.Connections, source);

        var targetYaml = y.Target ?? throw new FlowValidationException($"{source}: 'target' is required.");

        var server = YamlDocumentParts.ResolveEndpointConnection(
            targetYaml.Server, targetYaml.Connection, targetYaml.Provider,
            "target", TargetConnectionName, connections, source);

        // The series query is T-SQL (CAST AS float, GROUP BY) on the resolved server, so it is SQL Server by
        // design; a foreign provider is a configuration error caught at parse time, not deep in the run.
        YamlDocumentParts.RequireSqlServerConnection(connections, server, "target", "a health-check flow's target", source);

        var rawObject = YamlDocumentParts.NullIfBlank(targetYaml.Object)
            ?? throw new FlowValidationException(
                $"{source}: 'target.object' is required (a three-part name like Database.Schema.Table).");

        var dateColumn = YamlDocumentParts.NullIfBlank(y.DateColumn)?.Trim()
            ?? throw new FlowValidationException(
                $"{source}: 'dateColumn' is required (the date column the metrics are grouped by).");

        var ml = y.Ml ?? new HealthCheckMlYaml();
        var training = ParseTraining(ml.Training, source);

        var maxExperimentSeconds = ml.MaxExperimentSeconds ?? 120;
        if (maxExperimentSeconds < 1 || maxExperimentSeconds > MaxExperimentSecondsCeiling)
        {
            throw new FlowValidationException(
                $"{source}: 'ml.maxExperimentSeconds' must be between 1 and {MaxExperimentSecondsCeiling}, got {maxExperimentSeconds}.");
        }

        var anomalyThreshold = ml.AnomalyThreshold ?? 2.0;
        if (!double.IsFinite(anomalyThreshold) || anomalyThreshold <= 0 || anomalyThreshold > 100)
        {
            throw new FlowValidationException(
                $"{source}: 'ml.anomalyThreshold' must be a positive number of standard deviations (at most 100), got {ml.AnomalyThreshold}.");
        }

        var esdAlpha = ml.EsdAlpha ?? 0.025;
        if (!double.IsFinite(esdAlpha) || esdAlpha <= 0 || esdAlpha >= 0.5)
        {
            throw new FlowValidationException(
                $"{source}: 'ml.esdAlpha' must be a significance level strictly between 0 and 0.5, got {ml.EsdAlpha}.");
        }

        var maxAnomalyFraction = ml.MaxAnomalyFraction ?? 0.10;
        if (!double.IsFinite(maxAnomalyFraction) || maxAnomalyFraction <= 0 || maxAnomalyFraction > 0.49)
        {
            throw new FlowValidationException(
                $"{source}: 'ml.maxAnomalyFraction' must be in (0, 0.49], got {ml.MaxAnomalyFraction}.");
        }

        var maturityDays = y.MaturityDays ?? 1;
        if (maturityDays < 0 || maturityDays > 30)
        {
            throw new FlowValidationException(
                $"{source}: 'maturityDays' must be between 0 and 30, got {maturityDays}.");
        }

        var sentinelFloor = YamlDocumentParts.ParseDate(y.SentinelDateFloor, "sentinelDateFloor", source) ?? new DateOnly(1990, 1, 1);

        if (ml.RetrainAfterDays is { } retrainAfterDays)
        {
            if (retrainAfterDays < 1)
            {
                throw new FlowValidationException(
                    $"{source}: 'ml.retrainAfterDays' must be at least 1, got {retrainAfterDays}.");
            }

            if (training != HealthCheckTraining.Auto)
            {
                throw new FlowValidationException(
                    $"{source}: 'ml.retrainAfterDays' only applies with 'ml.training: auto' " +
                    $"('{training.ToString().ToLowerInvariant()}' ignores model age).");
            }
        }

        var flow = new HealthCheckFlow
        {
            FlowId = YamlDocumentParts.StableFlowId(name),
            SysAlias = name,
            Batch = YamlDocumentParts.NullIfBlank(y.Batch),
            Lifecycle = YamlDocumentParts.ParseLifecycle(y.Lifecycle, source),
            Server = server,
            Target = YamlDocumentParts.ParseQualifiedObject(rawObject, "target.object", source),
            DateColumn = dateColumn,
            Metrics = MapMetrics(y, source),
            FilterCriteria = YamlDocumentParts.NullIfBlank(y.Filter)?.Trim(),
            MaxExperimentSeconds = maxExperimentSeconds,
            AnomalyThreshold = anomalyThreshold,
            EsdAlpha = esdAlpha,
            MaxAnomalyFraction = maxAnomalyFraction,
            MaturityDays = maturityDays,
            SentinelDateFloor = sentinelFloor,
            Mode = YamlDocumentParts.ParseExecutionMode(y.Mode, "mode", source),
            Training = training,
            RetrainAfterDays = ml.RetrainAfterDays,
            Holidays = MapHolidays(y.Holidays, source),
            Description = YamlDocumentParts.NullIfBlank(y.Description),
        };

        return new HealthCheckDocument
        {
            Flow = flow,
            Connections = connections.Values.ToList(),
        };
    }

    /// <summary>Maps the monitored metrics: the <c>metrics:</c> list, or the single-metric <c>baseValue:</c>
    /// shorthand (named by convention). Names key state folders and report sections, so they are validated
    /// and deduplicated here.</summary>
    private static List<HealthCheckMetric> MapMetrics(HealthCheckYaml y, string source)
    {
        var shorthand = YamlDocumentParts.NullIfBlank(y.BaseValue)?.Trim();
        var list = y.Metrics;

        if (shorthand is not null && list is { Count: > 0 })
        {
            throw new FlowValidationException(
                $"{source}: set either 'baseValue' (one metric) or 'metrics' (a list), not both.");
        }

        if (shorthand is not null)
        {
            return [new HealthCheckMetric { Name = HealthCheckMetric.DefaultName(shorthand), Expression = shorthand }];
        }

        if (list is null || list.Count == 0)
        {
            throw new FlowValidationException(
                $"{source}: a health check needs a metric. Set 'baseValue' (an aggregate like COUNT(*) or SUM(Amount)) " +
                "or a 'metrics' list of {name, baseValue} entries.");
        }

        var metrics = new List<HealthCheckMetric>(list.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < list.Count; i++)
        {
            var expression = YamlDocumentParts.NullIfBlank(list[i].BaseValue)?.Trim()
                ?? throw new FlowValidationException($"{source}: 'metrics[{i}].baseValue' is required (an aggregate expression).");

            var metricName = YamlDocumentParts.NullIfBlank(list[i].Name)?.Trim() ?? HealthCheckMetric.DefaultName(expression);
            if (!IsValidMetricName(metricName))
            {
                throw new FlowValidationException(
                    $"{source}: 'metrics[{i}].name' '{metricName}' is invalid. Use letters, digits, '_', or '-'.");
            }

            if (!seen.Add(metricName))
            {
                throw new FlowValidationException($"{source}: metric name '{metricName}' is declared more than once.");
            }

            metrics.Add(new HealthCheckMetric { Name = metricName, Expression = expression });
        }

        return metrics;
    }

    private static bool IsValidMetricName(string name)
        => name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static HealthCheckTraining ParseTraining(string? value, string source)
        => value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "auto" => HealthCheckTraining.Auto,
            "always" => HealthCheckTraining.Always,
            "never" => HealthCheckTraining.Never,
            _ => throw new FlowValidationException(
                $"{source}: 'ml.training' has unknown value '{value}'. Allowed: auto, always, never."),
        };

    private static List<DateOnly> MapHolidays(List<string>? holidays, string source)
    {
        var parsed = new List<DateOnly>();
        if (holidays is null)
        {
            return parsed;
        }

        var seen = new HashSet<DateOnly>();
        for (var i = 0; i < holidays.Count; i++)
        {
            var date = YamlDocumentParts.ParseDate(holidays[i], $"holidays[{i}]", source)
                ?? throw new FlowValidationException($"{source}: 'holidays[{i}]' must not be blank.");

            // A duplicate date is harmless to the features; keeping one copy keeps the model deterministic.
            if (seen.Add(date))
            {
                parsed.Add(date);
            }
        }

        return parsed;
    }
}
