using SqlFlow.Core;
using SqlFlow.Core.Identity;
using SqlFlow.Core.Runs;

namespace SqlFlow.Yaml;

/// <summary>
/// The shared mapping/validation vocabulary of every document kind: the envelope keys (<c>mode:</c>,
/// <c>lifecycle:</c>), dates, the required name, and the stable name-derived flow id. Every loader goes through
/// these helpers so the YAML dialect stays one dialect with the same error wording in every document kind.
/// </summary>
public static class YamlDocumentParts
{
    /// <summary>Parses a <c>mode:</c> value (auto | manual | disabled; blank/absent is auto). One vocabulary for
    /// every place a definition opts out of automatic execution. <c>manual</c> reserves a working definition for
    /// direct triggers; <c>disabled</c> deactivates a retired one, with the same automatic-execution exclusion and
    /// the retirement carried visibly on the pipeline row.</summary>
    public static ExecutionMode ParseExecutionMode(string? value, string property, string source)
        => value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "auto" => ExecutionMode.Auto,
            "manual" => ExecutionMode.Manual,
            "disabled" => ExecutionMode.Disabled,
            _ => throw new FlowValidationException(
                $"{source}: '{property}' has unknown value '{value}'. Allowed: auto, manual, disabled."),
        };

    /// <summary>Parses a <c>lifecycle:</c> value (production | development; blank/absent is production). A flow
    /// under active development declares <c>lifecycle: development</c> and stops generating notification events,
    /// while execution itself is unaffected. Production is the default so an estate that declares nothing keeps
    /// alerting exactly as before.</summary>
    public static FlowLifecycle ParseLifecycle(string? value, string source)
        => value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "production" => FlowLifecycle.Production,
            "development" => FlowLifecycle.Development,
            _ => throw new FlowValidationException(
                $"{source}: 'lifecycle' has unknown value '{value}'. Allowed: production, development."),
        };

    public static DateOnly? ParseDate(string? value, string field, string source)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateOnly.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new FlowValidationException($"{source}: '{field}' must be a date like 2024-01-31, got '{value}'.");
    }

    /// <summary>The document's required <c>name:</c>, which seeds the stable flow id.</summary>
    public static string RequireFlowName(string? name, string kindLabel, string source)
        => NullIfBlank(name)?.Trim()
            ?? throw new FlowValidationException($"{source}: 'name' is required for {kindLabel}.");

    /// <summary>A stable positive flow id derived from the flow name (the deterministic name-based identity),
    /// so logs key consistently across runs without a database to assign ids.</summary>
    public static int StableFlowId(string name)
        => BitConverter.ToInt32(FlowIdentity.FromName(name).ToByteArray(), 0) & 0x7FFFFFFF;

    public static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// Parses a schedule's <c>values</c> (the flow parameter values every fire supplies) with the same rules a
    /// manual trigger's values obey, so a schedule can never queue something a trigger would reject: identifier
    /// names, bounded lengths, no control characters.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseScheduleValues(
        IReadOnlyDictionary<string, string>? values, string property, string source)
    {
        if (values is null || values.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var parsed = new Dictionary<string, string>(values, StringComparer.Ordinal);
        try
        {
            new RunParameters { Values = parsed }.Validate();
        }
        catch (SqlFlowException ex)
        {
            throw new FlowValidationException($"{source}: {property} is invalid - {ex.Message}", ex);
        }

        return parsed;
    }

    /// <summary>Parses a schedule's <c>operation</c> (absent is deliver) against the run operations the platform knows.</summary>
    public static string ParseOperation(string? value, string property, string source)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return RunParameters.DeliverOperation;
        }

        var operation = value.Trim().ToLowerInvariant();
        return RunParameters.Operations.Contains(operation, StringComparer.Ordinal)
            ? operation
            : throw new FlowValidationException($"{source}: {property} '{value}' must be one of {string.Join(", ", RunParameters.Operations)}.");
    }
}
