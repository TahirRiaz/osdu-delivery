using System.Text.Json;
using SqlFlow.Catalog;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Drops;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// An inline submission as the ledger holds it (design.md section 3.4): the records a source sent in the request, exactly
/// as the control plane accepted them, with the flow, the mapping and the parameter values they were accepted for, the
/// run options, who sent them and when, and where a run wrote them out as a drop. Its id is the idempotency key, and the
/// id of the <see cref="SubmissionState"/> the intake registers once a run plans the records.
/// </summary>
public sealed record InlineSubmissionState
{
    public const int MaxActorLength = 200;

    public required Guid SubmissionId { get; init; }

    public required Guid FlowId { get; init; }

    public required string FlowName { get; init; }

    /// <summary>
    /// The mapping the flow pinned when the records were accepted. The written drop's manifest names it, so a flow promoted
    /// since refuses the drop exactly as it refuses a drop prepared for an earlier mapping.
    /// </summary>
    public required string MappingReference { get; init; }

    /// <summary>The operation the submission asked for: <c>deliver</c> or <c>plan</c>.</summary>
    public required string Operation { get; init; }

    public bool Force { get; init; }

    /// <summary>The flow parameter values resolved against the flow's declarations (defaults applied), a JSON object sorted by name.</summary>
    public required string ParametersJson { get; init; }

    /// <summary>
    /// What the sending system calls this submission in its own records, trimmed; null when it named none. It travels
    /// onto the written drop's manifest and from there onto the submission the intake registers, so one search finds a
    /// submission by the name its source knows it by. Part of the request the id names, so a repeat carrying a different
    /// one is a conflict rather than a silent relabel.
    /// </summary>
    public string? Reference { get; init; }

    /// <summary>The records in canonical form (<see cref="InlineRecords.Json"/>).</summary>
    public required string RecordsJson { get; init; }

    /// <summary>SHA-256 of <see cref="RecordsJson"/>.</summary>
    public required string ContentHash { get; init; }

    /// <summary>SHA-256 over everything a replay of the request has to repeat: flow, mapping, operation, force, parameters and records.</summary>
    public required string RequestHash { get; init; }

    public int RecordCount { get; init; }

    public long ChildRowCount { get; init; }

    public int ContentBytes { get; init; }

    public DateTime ReceivedUtc { get; init; }

    /// <summary>Who sent the records: the caller's actor label.</summary>
    public required string ReceivedBy { get; init; }

    /// <summary>Where the last run that took the submission wrote its drop; null until one has.</summary>
    public string? DropLocation { get; init; }

    public DateTime? WrittenUtc { get; init; }

    /// <summary>The operations a submission can ask for: deliver the records, or plan them and change nothing.</summary>
    public static IReadOnlyList<string> Operations { get; } = [RunParameters.DeliverOperation, RunParameters.PlanOperation];

    /// <summary>The accepted form of a request: the records parsed, the parameters resolved against <paramref name="flow"/>.</summary>
    public static InlineSubmissionState Accept(
        Guid submissionId, FlowDefinition flow, string operation, bool force, IReadOnlyDictionary<string, string> parameters,
        InlineRecords records, DateTime receivedUtc, string receivedBy, string? reference = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentException.ThrowIfNullOrWhiteSpace(receivedBy);
        if (submissionId == Guid.Empty)
        {
            throw new ArgumentException("A submission id is a non-empty UUID.", nameof(submissionId));
        }

        var normalized = operation.Trim().ToLowerInvariant();
        if (!Operations.Contains(normalized, StringComparer.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(operation), operation, $"A submission's operation is {string.Join(" or ", Operations)}.");
        }

        if (SubmissionReference.Refusal(reference) is { } refusal)
        {
            throw new ArgumentException(refusal, nameof(reference));
        }

        var parametersJson = SerializeParameters(parameters);
        var normalizedReference = SubmissionReference.Normalize(reference);
        return new InlineSubmissionState
        {
            SubmissionId = submissionId,
            FlowId = flow.Id,
            FlowName = flow.Name,
            MappingReference = flow.Render.Mapping,
            Operation = normalized,
            Force = force,
            ParametersJson = parametersJson,
            Reference = normalizedReference,
            RecordsJson = records.Json,
            ContentHash = records.ContentHash,
            RequestHash = RequestHashOf(flow.Id, flow.Render.Mapping, normalized, force, parametersJson, records.ContentHash, normalizedReference),
            RecordCount = records.Records.Count,
            ChildRowCount = records.ChildRowCount,
            ContentBytes = records.ContentBytes,
            ReceivedUtc = DateTime.SpecifyKind(receivedUtc, DateTimeKind.Utc),
            ReceivedBy = receivedBy.Length <= MaxActorLength ? receivedBy : receivedBy[..MaxActorLength],
        };
    }

    /// <summary>The flow parameter values the records were accepted with.</summary>
    public IReadOnlyDictionary<string, string> Parameters()
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(ParametersJson) is { } values
                ? new Dictionary<string, string>(values, StringComparer.Ordinal)
                : throw new DeliveryException($"Inline submission {SubmissionId:D} records its parameters as JSON null.");
        }
        catch (JsonException ex)
        {
            throw new DeliveryException($"Inline submission {SubmissionId:D} records parameters that are not a JSON object of strings: {ex.Message}", ex);
        }
    }

    /// <summary>What differs between this accepted request and <paramref name="other"/>, for the conflict a reused id answers with; empty when they are the same request.</summary>
    public IReadOnlyList<string> Differences(InlineSubmissionState other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var differences = new List<string>();
        if (FlowId != other.FlowId)
        {
            differences.Add($"the flow ('{FlowName}', not '{other.FlowName}')");
        }

        if (!MappingReference.Equals(other.MappingReference, StringComparison.Ordinal))
        {
            differences.Add($"the mapping the flow pinned ('{MappingReference}', not '{other.MappingReference}')");
        }

        if (!Operation.Equals(other.Operation, StringComparison.Ordinal))
        {
            differences.Add($"the operation ('{Operation}', not '{other.Operation}')");
        }

        if (Force != other.Force)
        {
            differences.Add($"force ({(Force ? "true" : "false")}, not {(other.Force ? "true" : "false")})");
        }

        if (!ParametersJson.Equals(other.ParametersJson, StringComparison.Ordinal))
        {
            differences.Add("the flow parameter values");
        }

        if (!ContentHash.Equals(other.ContentHash, StringComparison.Ordinal))
        {
            differences.Add("the records");
        }

        if (!string.Equals(Reference, other.Reference, StringComparison.Ordinal))
        {
            differences.Add($"the reference ({Describe(Reference)}, not {Describe(other.Reference)})");
        }

        return differences;
    }

    private static string Describe(string? reference) => reference is null ? "none" : $"'{reference}'";

    public static string SerializeParameters(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return JsonSerializer.Serialize(new SortedDictionary<string, string>(values.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal), StringComparer.Ordinal));
    }

    public static string RequestHashOf(
        Guid flowId, string mappingReference, string operation, bool force, string parametersJson, string contentHash, string? reference = null)
    {
        ArgumentNullException.ThrowIfNull(mappingReference);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(parametersJson);
        ArgumentNullException.ThrowIfNull(contentHash);
        // The reference is part of the request the id names, so it is hashed with the rest: a repeat that relabels the
        // work is a different request, not the same one. Absence hashes as the empty string, which the length-prefixed
        // parts keep distinct from a reference that is genuinely empty, since a blank one normalises to absent anyway.
        return Hashing.ContentHash.OfParts(
            flowId.ToString("D"), mappingReference, operation, force ? "force" : "no-force", parametersJson, contentHash, reference ?? string.Empty);
    }
}

/// <summary>The catalog row of an inline submission and back.</summary>
public static class InlineSubmissionRows
{
    public static DeliveryInlineSubmission ToEntity(InlineSubmissionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new DeliveryInlineSubmission
        {
            SubmissionId = state.SubmissionId,
            FlowId = state.FlowId,
            FlowName = state.FlowName,
            MappingReference = state.MappingReference,
            Operation = state.Operation,
            Force = state.Force,
            ParametersJson = state.ParametersJson,
            Reference = state.Reference,
            RecordsJson = state.RecordsJson,
            ContentHash = state.ContentHash,
            RequestHash = state.RequestHash,
            RecordCount = state.RecordCount,
            ChildRowCount = state.ChildRowCount,
            ContentBytes = state.ContentBytes,
            ReceivedUtc = state.ReceivedUtc,
            ReceivedBy = state.ReceivedBy,
            DropLocation = state.DropLocation,
            WrittenUtc = state.WrittenUtc,
        };
    }

    public static InlineSubmissionState ToState(DeliveryInlineSubmission entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return new InlineSubmissionState
        {
            SubmissionId = entity.SubmissionId,
            FlowId = entity.FlowId,
            FlowName = entity.FlowName,
            MappingReference = entity.MappingReference,
            Operation = entity.Operation,
            Force = entity.Force,
            ParametersJson = entity.ParametersJson,
            Reference = entity.Reference,
            RecordsJson = entity.RecordsJson,
            ContentHash = entity.ContentHash,
            RequestHash = entity.RequestHash,
            RecordCount = entity.RecordCount,
            ChildRowCount = entity.ChildRowCount,
            ContentBytes = entity.ContentBytes,
            ReceivedUtc = DateTime.SpecifyKind(entity.ReceivedUtc, DateTimeKind.Utc),
            ReceivedBy = entity.ReceivedBy,
            DropLocation = entity.DropLocation,
            WrittenUtc = entity.WrittenUtc is { } written ? DateTime.SpecifyKind(written, DateTimeKind.Utc) : null,
        };
    }
}
