using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Model;

/// <summary>One payload set a route sends as a part of the record: the part it is and the set it is read from.</summary>
/// <param name="Role">The part: <see cref="PayloadParts.Files"/> or <see cref="PayloadParts.Bulk"/>.</param>
/// <param name="Payload">The payload set (<c>source.payloads</c>, or the interface's <c>files</c>, <c>bulk</c> or workflow input).</param>
/// <param name="Optional">True when a record may carry no files for it (a workflow input the workflow accepts empty).</param>
public sealed record PayloadPart(string Role, string Payload, bool Optional = false);

/// <summary>
/// What a route sends beside the record, part by part (docs/interfaces-design.md section 5.5). A route that sends one
/// payload set (file, manifest, ddms, dataset) streams it as the record's payload, as it always has. A composed route
/// sends several: its files, its DDMS bulk data, the inputs its workflow reads, and for the workflow route the run
/// itself. Each part is sent when its content changed, or when a redelivery names it, and the ledger keeps them in the
/// payload hash and location it has always kept (<see cref="CompositePayload"/>).
/// </summary>
public static class PayloadParts
{
    /// <summary>Files: the datasets a record refers to, the record's own content, or the inputs a workflow reads.</summary>
    public const string Files = "files";

    /// <summary>The bulk data a DDMS stores for the record.</summary>
    public const string Bulk = "bulk";

    /// <summary>The workflow run of the workflow route.</summary>
    public const string Workflow = "workflow";

    /// <summary>The prefix of a delivered payload hash that a redelivery by part replaced: the parts it names follow.</summary>
    public const string RedeliverPrefix = "redeliver:";

    /// <summary>The target state value holding the content hash a payload set was last delivered with.</summary>
    public static string StateKey(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        return "payload." + payload;
    }

    /// <summary>True for the routes that send their payload in parts.</summary>
    public static bool Composed(DeliveryProtocol protocol)
        => protocol is DeliveryProtocol.OsduFileAndDdms or DeliveryProtocol.OsduManifestAndDdms or DeliveryProtocol.OsduWorkflow or DeliveryProtocol.OsduEtp;

    /// <summary>
    /// The payload sets <paramref name="flow"/>'s route sends as parts, in the order it sends them, or null for a route
    /// that sends at most one payload set. The files come first, since a record refers to them; then the bulk data a DDMS
    /// keeps for the record once it is written; the inputs a workflow reads come with the files.
    /// </summary>
    public static IReadOnlyList<PayloadPart>? Of(FlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var declaresFiles = flow.Source.Payloads.ContainsKey(Files);
        switch (flow.Target.Protocol)
        {
            case DeliveryProtocol.OsduFileAndDdms:
                return [new PayloadPart(Files, Files), new PayloadPart(Bulk, Bulk)];
            case DeliveryProtocol.OsduManifestAndDdms:
                return declaresFiles ? [new PayloadPart(Files, Files), new PayloadPart(Bulk, Bulk)] : [new PayloadPart(Bulk, Bulk)];
            case DeliveryProtocol.OsduEtp:
                // The object's XML and the values of its arrays, each optional: a mapping may render either into the
                // document instead (osdu/specs/reservoir-ddms/INTEGRATION.md section 5).
                var etp = new List<PayloadPart>();
                if (declaresFiles)
                {
                    etp.Add(new PayloadPart(Files, Files, Optional: true));
                }

                if (flow.Source.Payloads.ContainsKey(Bulk))
                {
                    etp.Add(new PayloadPart(Bulk, Bulk, Optional: true));
                }

                return etp.Count > 0 ? etp : null;
            case DeliveryProtocol.OsduWorkflow:
                var parts = new List<PayloadPart>();
                if (declaresFiles)
                {
                    parts.Add(new PayloadPart(Files, Files));
                }

                foreach (var input in flow.Target.Workflow?.Inputs ?? [])
                {
                    parts.Add(new PayloadPart(Files, input.Name, input.Optional));
                }

                return parts;
            default:
                return null;
        }
    }

    /// <summary>The parts a flow's route can send again on their own, by the names a redelivery gives them.</summary>
    public static IReadOnlyList<string> Roles(FlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (Of(flow) is not { } parts)
        {
            return [];
        }

        var roles = parts.Select(p => p.Role).Distinct(StringComparer.Ordinal).ToList();
        if (flow.Target.Protocol == DeliveryProtocol.OsduWorkflow)
        {
            roles.Add(Workflow);
        }

        return roles;
    }

    /// <summary>The delivered payload hash a redelivery of <paramref name="roles"/> leaves on a record until the payload lands again.</summary>
    public static string RedeliverMarker(IEnumerable<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        var named = roles.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (named.Count == 0 || named.Any(r => !IsRole(r)))
        {
            throw new ArgumentException($"a redelivery marker names one or more of {Files}, {Bulk} and {Workflow}.", nameof(roles));
        }

        return RedeliverPrefix + string.Join(",", named);
    }

    /// <summary>The parts a delivered payload hash names for redelivery, or null when it is a content hash (or none).</summary>
    public static IReadOnlySet<string>? RedeliverRoles(string? deliveredPayloadHash)
    {
        if (deliveredPayloadHash is null || !deliveredPayloadHash.StartsWith(RedeliverPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        return deliveredPayloadHash[RedeliverPrefix.Length..]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsRole)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The parts a plan sends whatever their hashes say: every one when the record never delivered its payload, a
    /// redelivery of the whole payload cleared its hash, or the flow always sends its payload; the ones a redelivery by
    /// part named; otherwise none, and each part goes when its own hash moved.
    /// </summary>
    public static IReadOnlySet<string> Forced(string? deliveredPayloadHash, FlowChange change, IReadOnlyList<string> roles)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(roles);
        if (deliveredPayloadHash is null || change.PayloadDetect == ChangeDetection.Always || change.OnUnchanged == UnchangedAction.Deliver)
        {
            return roles.ToHashSet(StringComparer.Ordinal);
        }

        return RedeliverRoles(deliveredPayloadHash) is { } named
            ? roles.Where(named.Contains).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
    }

    private static bool IsRole(string role) => role is Files or Bulk or Workflow;
}

/// <summary>One part of a composed payload as a plan resolved it.</summary>
/// <param name="Role">The part (<see cref="PayloadParts.Files"/>, <see cref="PayloadParts.Bulk"/>).</param>
/// <param name="Payload">The payload set it is read from.</param>
/// <param name="Location">Where its files are listed from (folder and pattern as one text); null for an optional part the record leaves empty.</param>
/// <param name="Hash">Its content hash; <see cref="CompositePayload.NoFiles"/> for an empty optional part.</param>
public sealed record CompositePayloadPart(string Role, string Payload, string? Location, string Hash);

/// <summary>
/// A payload sent in parts, as the ledger keeps it: the record's payload hash is the hash of the parts' hashes, so a
/// change to any part is a payload change, and the pending payload location is this object as JSON, listing every part
/// with its location and hash and the parts the delivery sends whatever their hashes say. A single payload set's
/// location is a folder and never starts with a brace, so the two cannot be mistaken for each other.
/// </summary>
public sealed record CompositePayload(IReadOnlyList<CompositePayloadPart> Parts, IReadOnlySet<string> Forced)
{
    /// <summary>The width the ledger keeps a pending payload location in.</summary>
    public const int MaxStoredLength = 2000;

    /// <summary>The hash of an optional part a record carries no files for.</summary>
    public const string NoFiles = "none";

    private const string FormatVersion = "1";

    /// <summary>True when <paramref name="stored"/> is a payload sent in parts rather than one payload set's location.</summary>
    public static bool IsComposite(string? stored)
        => stored is not null && stored.AsSpan().TrimStart().StartsWith("{", StringComparison.Ordinal);

    /// <summary>
    /// The record's payload hash: SHA-256 over each part's set and hash, in the order the route sends them, so it fits
    /// the ledger's hash column however many parts there are.
    /// </summary>
    public static string HashOf(IEnumerable<CompositePayloadPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var text = new StringBuilder();
        foreach (var part in parts)
        {
            text.Append(part.Role).Append(':').Append(part.Payload).Append('=').Append(part.Hash).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    public string Encode()
    {
        var parts = new JsonArray();
        foreach (var part in Parts)
        {
            var node = new JsonObject { ["role"] = part.Role, ["payload"] = part.Payload, ["hash"] = part.Hash };
            if (part.Location is not null)
            {
                node["location"] = part.Location;
            }

            parts.Add(node);
        }

        var root = new JsonObject
        {
            ["composite"] = FormatVersion,
            ["forced"] = new JsonArray(Forced.Order(StringComparer.Ordinal).Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
            ["parts"] = parts,
        };
        return root.ToJsonString();
    }

    /// <summary>Reads a stored composite payload; throws <see cref="DeliveryException"/> when it is not one this version wrote.</summary>
    public static CompositePayload Decode(string stored)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stored);
        JsonObject root;
        try
        {
            root = JsonNode.Parse(stored) as JsonObject ?? throw new DeliveryException("the pending payload is not a JSON object; redeliver the record to plan it again");
        }
        catch (JsonException ex)
        {
            throw new DeliveryException($"the pending payload is not valid JSON ({ex.Message}); redeliver the record to plan it again", ex);
        }

        if (Text(root, "composite") != FormatVersion || root["parts"] is not JsonArray array)
        {
            throw new DeliveryException("the pending payload is not a payload in parts this version of OSDU Delivery wrote; redeliver the record to plan it again");
        }

        var parts = new List<CompositePayloadPart>(array.Count);
        foreach (var node in array)
        {
            if (node is not JsonObject part
                || Text(part, "role") is not { } role
                || Text(part, "payload") is not { } payload
                || Text(part, "hash") is not { } hash)
            {
                throw new DeliveryException("the pending payload lists a part without its role, payload set and hash; redeliver the record to plan it again");
            }

            parts.Add(new CompositePayloadPart(role, payload, Text(part, "location"), hash));
        }

        var forced = (root["forced"] as JsonArray ?? [])
            .Select(f => f is JsonValue v && v.TryGetValue<string>(out var text) ? text : null)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        return new CompositePayload(parts, forced);
    }

    /// <summary>Why an encoded payload cannot be kept by the ledger, or null when it fits.</summary>
    public static string? TooLong(string encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        return encoded.Length <= MaxStoredLength
            ? null
            : string.Create(
                CultureInfo.InvariantCulture,
                $"the record's payload parts take {encoded.Length} characters to list with their folders, and the ledger keeps {MaxStoredLength}; shorten the payload folders (the roots or the location columns)");
    }

    private static string? Text(JsonObject node, string name)
        => node[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text) ? text : null;
}
