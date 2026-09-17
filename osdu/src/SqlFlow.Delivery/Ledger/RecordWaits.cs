using System.Text.Json;
using System.Text.Json.Serialization;
using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Ledger;

/// <summary>An OSDU id a rendered record refers to (without its version), and the property of the record that holds it.</summary>
public sealed record RecordReference(string Id, string Property);

/// <summary>
/// What a flow's records wait for (docs/interfaces-design.md section 7): a record waits for a record it refers to that
/// another record of the ledger holds and has not delivered, except the records of the flows named here. Those are the
/// interfaces of the same source whose references the source's order cuts, because the two sides refer to each other and
/// the order says which goes first: a reference from this flow to one of them points back, and resolves once the other
/// side has landed.
/// </summary>
public sealed record WaitRules
{
    /// <summary>Wait for every record the ledger holds and has not delivered.</summary>
    public static WaitRules WaitForAll { get; } = new();

    /// <summary>The ledger identities of the flows whose records this flow's records do not wait for.</summary>
    public IReadOnlySet<Guid> NotWaitedFor { get; init; } = new HashSet<Guid>();
}

/// <summary>A record a claim did not take because it waits for a record it refers to: the id it waits for, and why.</summary>
public sealed record WaitingRecord(DeliveryKey DeliveryKey, string WaitingFor, string Reason);

/// <summary>How the references of a pending document are kept on its record: a JSON array of <c>{ "id", "property" }</c>.</summary>
public static class RecordReferences
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>The references as the record column holds them, or null when there are none.</summary>
    public static string? Encode(IReadOnlyList<RecordReference>? references)
        => references is null || references.Count == 0 ? null : JsonSerializer.Serialize(references, Options);

    /// <summary>The references a record column holds; none for an empty column. A column that is not such an array is a defect of the ledger.</summary>
    public static IReadOnlyList<RecordReference> Decode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            var references = JsonSerializer.Deserialize<List<RecordReference>>(json, Options) ?? [];
            if (references.Any(r => string.IsNullOrWhiteSpace(r.Id) || r.Property is null))
            {
                throw new DeliveryException("A record's pending references name an entry without an id or a property.");
            }

            return references;
        }
        catch (JsonException ex)
        {
            throw new DeliveryException($"A record's pending references are not the JSON array the ledger writes: {ex.Message}", ex);
        }
    }
}
