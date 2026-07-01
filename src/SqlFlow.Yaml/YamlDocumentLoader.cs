using SqlFlow.Core;
using SqlFlow.Core.Model;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SqlFlow.Yaml;

/// <summary>A loaded flow document: a file flow (CSV/JSON/XML/XLS/Parquet into SQL), a relational
/// table-to-table ingestion flow, a file export, a stored-procedure flow, a standalone invoke flow, or an ML
/// health check, discriminated by the document's <c>flowType</c> key.</summary>
public abstract record FlowDocument
{
    /// <summary>The flow's declared schedule from its top-level <c>schedule:</c> block, or null when it declares
    /// none. Captured at the document envelope so every flow kind carries a schedule the same way; the control
    /// plane turns it into actual runs (the engine itself never schedules anything).</summary>
    public ScheduleSpec? Schedule { get; init; }
}

/// <summary>A file-source flow document (no <c>flowType</c> key, the original document shape).</summary>
public sealed record FileFlowDocument : FlowDocument
{
    public required FlowDefinition Flow { get; init; }
}

/// <summary>A relational ingestion flow document (<c>flowType: ing</c>).</summary>
public sealed record IngestionFlowDocument : FlowDocument
{
    public required IngestionDocument Document { get; init; }
}

/// <summary>An export flow document (<c>flowType: exp</c>).</summary>
public sealed record ExportFlowDocument : FlowDocument
{
    public required ExportDocument Document { get; init; }
}

/// <summary>A stored-procedure flow document (<c>flowType: sp</c>).</summary>
public sealed record StoredProcedureFlowDocument : FlowDocument
{
    public required StoredProcedureDocument Document { get; init; }
}

/// <summary>A standalone invoke flow document (<c>flowType: inv</c>).</summary>
public sealed record InvokeFlowDocument : FlowDocument
{
    public required InvokeDocument Document { get; init; }
}

/// <summary>An ML health-check flow document (<c>flowType: hc</c>).</summary>
public sealed record HealthCheckFlowDocument : FlowDocument
{
    public required HealthCheckDocument Document { get; init; }
}

/// <summary>A source-control flow document (<c>flowType: scm</c>).</summary>
public sealed record SourceControlFlowDocument : FlowDocument
{
    public required SourceControlDocument Document { get; init; }
}

/// <summary>A batch flow document (<c>flowType: batch</c>).</summary>
public sealed record BatchFlowDocument : FlowDocument
{
    public required BatchDocument Document { get; init; }
}

/// <summary>
/// The single entry point for loading any flow document: it sniffs the root <c>flowType</c> key with a cheap
/// probe pass, then delegates to the matching loader. No key (the long-standing default) means a file flow;
/// <c>ing</c> means a relational ingestion flow, <c>exp</c> a file export, <c>sp</c> a stored-procedure flow,
/// <c>inv</c> a standalone invoke, <c>hc</c> an ML health check; anything else is a clear error rather than a
/// confusing downstream validation failure.
/// </summary>
public sealed class YamlDocumentLoader
{
    private sealed class DocumentKindYaml
    {
        public string? FlowType { get; set; }

        public ScheduleYaml? Schedule { get; set; }
    }

    private sealed class ScheduleYaml
    {
        public string? Cron { get; set; }

        public int? IntervalSeconds { get; set; }

        public string? Timezone { get; set; }

        public bool? Enabled { get; set; }
    }

    private readonly IDeserializer _probe = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly YamlFlowLoader _fileFlows;
    private readonly YamlIngestionFlowLoader _ingestionFlows;
    private readonly YamlExportFlowLoader _exportFlows;
    private readonly YamlStoredProcedureFlowLoader _storedProcedureFlows;
    private readonly YamlInvokeFlowLoader _invokeFlows;
    private readonly YamlHealthCheckFlowLoader _healthCheckFlows;
    private readonly YamlSourceControlFlowLoader _sourceControlFlows;
    private readonly YamlBatchFlowLoader _batchFlows;

    public YamlDocumentLoader(
        YamlFlowLoader fileFlows,
        YamlIngestionFlowLoader ingestionFlows,
        YamlExportFlowLoader exportFlows,
        YamlStoredProcedureFlowLoader storedProcedureFlows,
        YamlInvokeFlowLoader invokeFlows,
        YamlHealthCheckFlowLoader healthCheckFlows,
        YamlSourceControlFlowLoader sourceControlFlows,
        YamlBatchFlowLoader batchFlows)
    {
        ArgumentNullException.ThrowIfNull(fileFlows);
        ArgumentNullException.ThrowIfNull(ingestionFlows);
        ArgumentNullException.ThrowIfNull(exportFlows);
        ArgumentNullException.ThrowIfNull(storedProcedureFlows);
        ArgumentNullException.ThrowIfNull(invokeFlows);
        ArgumentNullException.ThrowIfNull(healthCheckFlows);
        ArgumentNullException.ThrowIfNull(sourceControlFlows);
        ArgumentNullException.ThrowIfNull(batchFlows);
        _fileFlows = fileFlows;
        _ingestionFlows = ingestionFlows;
        _exportFlows = exportFlows;
        _storedProcedureFlows = storedProcedureFlows;
        _invokeFlows = invokeFlows;
        _healthCheckFlows = healthCheckFlows;
        _sourceControlFlows = sourceControlFlows;
        _batchFlows = batchFlows;
    }

    public FlowDocument LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Pipeline file not found: '{path}'.");
        }

        return Parse(File.ReadAllText(path), path);
    }

    public FlowDocument Parse(string yaml, string source = "<inline>")
    {
        DocumentKindYaml? probe;
        try
        {
            probe = _probe.Deserialize<DocumentKindYaml>(yaml);
        }
        catch (YamlException ex)
        {
            throw new FlowValidationException($"{source}: invalid YAML - {ex.Message}", ex);
        }

        var flowType = probe?.FlowType?.Trim();
        var schedule = MapSchedule(probe?.Schedule);

        if (string.IsNullOrEmpty(flowType))
        {
            return new FileFlowDocument { Flow = _fileFlows.Parse(yaml, source), Schedule = schedule };
        }

        if (string.Equals(flowType, "ing", StringComparison.OrdinalIgnoreCase))
        {
            return new IngestionFlowDocument { Document = _ingestionFlows.Parse(yaml, source), Schedule = schedule };
        }

        if (string.Equals(flowType, "exp", StringComparison.OrdinalIgnoreCase))
        {
            return new ExportFlowDocument { Document = _exportFlows.Parse(yaml, source), Schedule = schedule };
        }

        if (string.Equals(flowType, "sp", StringComparison.OrdinalIgnoreCase))
        {
            return new StoredProcedureFlowDocument { Document = _storedProcedureFlows.Parse(yaml, source), Schedule = schedule };
        }

        if (string.Equals(flowType, "inv", StringComparison.OrdinalIgnoreCase))
        {
            return new InvokeFlowDocument { Document = _invokeFlows.Parse(yaml, source), Schedule = schedule };
        }

        if (string.Equals(flowType, "hc", StringComparison.OrdinalIgnoreCase))
        {
            return new HealthCheckFlowDocument { Document = _healthCheckFlows.Parse(yaml, source), Schedule = schedule };
        }

        if (string.Equals(flowType, "scm", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceControlFlowDocument { Document = _sourceControlFlows.Parse(yaml, source), Schedule = schedule };
        }

        if (string.Equals(flowType, "batch", StringComparison.OrdinalIgnoreCase))
        {
            return new BatchFlowDocument { Document = _batchFlows.Parse(yaml, source), Schedule = schedule };
        }

        throw new FlowValidationException(
            $"{source}: unknown flowType '{flowType}'. Use 'ing' for a table-to-table ingestion flow, 'exp' for a " +
            "file export, 'sp' for a stored-procedure flow, 'inv' for an ADF/Automation trigger, 'hc' for an ML " +
            "health check, 'scm' for a database source-control snapshot, 'batch' for an ordered multi-flow batch, " +
            "or omit flowType for a file flow.");
    }

    private static ScheduleSpec? MapSchedule(ScheduleYaml? schedule)
    {
        if (schedule is null)
        {
            return null;
        }

        // A schedule block carrying neither a cron nor an interval declares nothing to fire; treat it as absent so
        // an empty/placeholder block is not stored as a broken schedule. The cron syntax itself is validated where
        // the schedule is armed (the catalog/control plane owns the cron library).
        if (string.IsNullOrWhiteSpace(schedule.Cron) && schedule.IntervalSeconds is null)
        {
            return null;
        }

        return new ScheduleSpec
        {
            Cron = string.IsNullOrWhiteSpace(schedule.Cron) ? null : schedule.Cron.Trim(),
            IntervalSeconds = schedule.IntervalSeconds,
            Timezone = string.IsNullOrWhiteSpace(schedule.Timezone) ? "UTC" : schedule.Timezone.Trim(),
            Enabled = schedule.Enabled ?? true,
        };
    }
}
