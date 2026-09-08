using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlFlow.Core.Compute;

/// <summary>
/// The payload of one queued compute task: an ad-hoc operation a worker node executes near the data on the
/// control plane's behalf (a target probe, a record read-back, a record removal), with a result the GUI reads
/// back. References only: the executing node resolves every credential itself, so nothing secret ever travels
/// through the queue. Operations are registered by name in the host; this payload is the transport contract.
/// </summary>
public sealed record ComputeTaskPayload
{
    /// <summary>The operation name, as registered in the executing host (for example <c>osduProbe</c>).</summary>
    public required string Operation { get; init; }

    /// <summary>What the operation targets: the flow name whose target and credentials it uses.</summary>
    public required string SourceRef { get; init; }

    /// <summary>The operation's arguments, all as strings so the contract stays JSON-stable across versions.</summary>
    public IReadOnlyDictionary<string, string> Arguments { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public const int MaxOperationLength = 32;

    public const int MaxSourceRefLength = 512;

    public const int MaxArguments = 32;

    public const int MaxArgumentLength = 4000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Parses a stored payload. A payload that does not parse is a <see cref="SqlFlowException"/>,
    /// never a null: the queue row is corrupt and the task must fail loudly.</summary>
    public static ComputeTaskPayload FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        try
        {
            return JsonSerializer.Deserialize<ComputeTaskPayload>(json, JsonOptions)
                ?? throw new SqlFlowException("The compute task payload is empty.");
        }
        catch (JsonException ex)
        {
            throw new SqlFlowException($"The compute task payload is not valid JSON: {ex.Message}", ex);
        }
    }

    /// <summary>The argument named <paramref name="name"/>, or null when absent or blank.</summary>
    public string? Argument(string name)
        => Arguments.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    /// <summary>The argument named <paramref name="name"/>, or a <see cref="SqlFlowException"/> naming it when absent.</summary>
    public string RequireArgument(string name)
        => Argument(name) ?? throw new SqlFlowException($"The '{Operation}' operation requires the '{name}' argument.");

    /// <summary>Validates the transport contract: a bounded operation name and target reference, bounded
    /// arguments. What the arguments MEAN is validated by the operation itself when it runs.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Operation) || Operation.Length > MaxOperationLength
            || !Operation.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            throw new SqlFlowException($"operation must be 1-{MaxOperationLength} letters, digits, '-' or '_'.");
        }

        if (string.IsNullOrWhiteSpace(SourceRef) || SourceRef.Length > MaxSourceRefLength)
        {
            throw new SqlFlowException($"sourceRef must be 1-{MaxSourceRefLength} characters.");
        }

        if (Arguments.Count > MaxArguments)
        {
            throw new SqlFlowException($"a compute task takes at most {MaxArguments} arguments.");
        }

        foreach (var (name, value) in Arguments)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 64)
            {
                throw new SqlFlowException("every argument needs a name of at most 64 characters.");
            }

            if (value is not null && value.Length > MaxArgumentLength)
            {
                throw new SqlFlowException($"argument '{name}' is longer than {MaxArgumentLength} characters.");
            }
        }
    }
}
