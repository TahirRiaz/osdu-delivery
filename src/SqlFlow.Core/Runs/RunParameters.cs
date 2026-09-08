using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SqlFlow.Core.Runs;

/// <summary>
/// The per-run parameters a trigger carries into a flow run: which operation the run performs, whether it forces
/// past the change gates, the flow's own parameter values (a <c>{logSource}</c> token, say), an explicit drop, a
/// submission to re-run, and the records the run is scoped to. They ride on the run row, reach the executing node
/// through the claim, and are recorded on the run so the history says exactly what was asked. Defaults mean "the
/// flow as declared": deliver the flow's drop, skipping what has not changed.
/// </summary>
public sealed partial record RunParameters
{
    public static readonly RunParameters None = new();

    /// <summary>Deliver: intake the drop, plan against the ledger, deliver what changed.</summary>
    public const string DeliverOperation = "deliver";

    /// <summary>Verify: the drift pass, reading delivered records back from the target and comparing versions.</summary>
    public const string VerifyOperation = "verify";

    /// <summary>Plan: render and compare, report what would be delivered, change nothing.</summary>
    public const string PlanOperation = "plan";

    /// <summary>Known state: publish the compact known-state snapshot the preparing side reads.</summary>
    public const string KnownStateOperation = "known-state";

    public static readonly IReadOnlyList<string> Operations = [DeliverOperation, VerifyOperation, PlanOperation, KnownStateOperation];

    public const int MaxDropLength = 2000;

    public const int MaxValueLength = 1000;

    public const int MaxValues = 32;

    public const int MaxRecordKeys = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The operation, one of <see cref="Operations"/>. Default deliver.</summary>
    public string Operation { get; init; } = DeliverOperation;

    /// <summary>Force past the change gates: plan every record even when no source table advanced, re-plan a
    /// submission that was already completed, verify records verified recently.</summary>
    public bool Force { get; init; }

    /// <summary>The flow's declared parameter values (name to value), substituted into its source location.</summary>
    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>An explicit drop location, overriding the flow's declared source location for this run.</summary>
    public string? Drop { get; init; }

    /// <summary>Re-run one submission (its drop and manifest) rather than the flow's current drop.</summary>
    public Guid? SubmissionId { get; init; }

    /// <summary>The delivery keys the run is scoped to (a verify of a few records, a redelivery of one); empty means every record.</summary>
    public IReadOnlyList<Guid> RecordKeys { get; init; } = [];

    /// <summary>Where a known-state publication is written; null uses the flow's declared location.</summary>
    public string? PublishTo { get; init; }

    public bool IsDefault
        => string.Equals(Operation, DeliverOperation, StringComparison.OrdinalIgnoreCase) && !Force && Values.Count == 0
           && string.IsNullOrWhiteSpace(Drop) && SubmissionId is null && RecordKeys.Count == 0 && string.IsNullOrWhiteSpace(PublishTo);

    /// <summary>True for the operations that write to the target (deliver) as opposed to reading it or the ledger.</summary>
    public bool WritesTarget => string.Equals(Operation, DeliverOperation, StringComparison.OrdinalIgnoreCase);

    public void Validate()
    {
        if (!Operations.Contains(Operation, StringComparer.OrdinalIgnoreCase))
        {
            throw new SqlFlowException($"operation must be one of {string.Join(", ", Operations)}; '{Operation}' is not.");
        }

        if (Values.Count > MaxValues)
        {
            throw new SqlFlowException($"At most {MaxValues} parameter values can be supplied.");
        }

        foreach (var (name, value) in Values)
        {
            if (!ParameterName().IsMatch(name))
            {
                throw new SqlFlowException($"Parameter name '{name}' must be an identifier (letters, digits, underscore).");
            }

            if (value is null || value.Length > MaxValueLength || value.Any(char.IsControl))
            {
                throw new SqlFlowException($"Parameter '{name}' must be 0 to {MaxValueLength} characters without control characters.");
            }
        }

        if (Drop is { } drop && (string.IsNullOrWhiteSpace(drop) || drop.Length > MaxDropLength || drop.Any(char.IsControl)))
        {
            throw new SqlFlowException($"drop must be 1 to {MaxDropLength} characters without control characters.");
        }

        if (SubmissionId is { } submission && submission == Guid.Empty)
        {
            throw new SqlFlowException("submissionId must be a non-empty UUID.");
        }

        if (RecordKeys.Count > MaxRecordKeys)
        {
            throw new SqlFlowException($"At most {MaxRecordKeys} record keys can be scoped in one run.");
        }

        if (RecordKeys.Any(k => k == Guid.Empty))
        {
            throw new SqlFlowException("recordKeys must be non-empty UUIDs.");
        }

        if (PublishTo is { } to && (string.IsNullOrWhiteSpace(to) || to.Length > MaxDropLength || to.Any(char.IsControl)))
        {
            throw new SqlFlowException($"publishTo must be 1 to {MaxDropLength} characters without control characters.");
        }

        if (string.Equals(Operation, KnownStateOperation, StringComparison.OrdinalIgnoreCase) && (SubmissionId is not null || RecordKeys.Count > 0))
        {
            throw new SqlFlowException("A known-state publication covers the whole flow; it takes no submission or record scope.");
        }
    }

    /// <summary>A one-line description for logs and listings ("none" for the defaults).</summary>
    public string Describe()
    {
        if (IsDefault)
        {
            return "none";
        }

        var parts = new List<string> { $"operation={Operation.ToLowerInvariant()}" };
        if (Force)
        {
            parts.Add("force");
        }

        foreach (var (name, value) in Values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            parts.Add($"{name}={value}");
        }

        if (!string.IsNullOrWhiteSpace(Drop))
        {
            parts.Add($"drop={Drop}");
        }

        if (SubmissionId is { } submission)
        {
            parts.Add($"submission={submission:D}");
        }

        if (RecordKeys.Count > 0)
        {
            parts.Add($"records={RecordKeys.Count}");
        }

        if (!string.IsNullOrWhiteSpace(PublishTo))
        {
            parts.Add($"publishTo={PublishTo}");
        }

        return string.Join(" ", parts);
    }

    /// <summary>The JSON form the catalog stores on the run row and the node reads back.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Parses the stored form; null or blank is the default set.</summary>
    public static RunParameters FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return None;
        }

        try
        {
            return JsonSerializer.Deserialize<RunParameters>(json, JsonOptions) ?? None;
        }
        catch (JsonException ex)
        {
            throw new SqlFlowException($"The stored run parameters are not valid JSON: {ex.Message}", ex);
        }
    }

    /// <summary>Parses <c>name=value</c> pairs (the CLI's <c>--set</c>) into parameter values.</summary>
    public static IReadOnlyDictionary<string, string> ParseValues(IEnumerable<string> assignments)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var assignment in assignments)
        {
            var eq = assignment.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                throw new SqlFlowException($"Parameter '{assignment}' must be written as name=value.");
            }

            values[assignment[..eq].Trim()] = assignment[(eq + 1)..];
        }

        return values;
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex ParameterName();
}
