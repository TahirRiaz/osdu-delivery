using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Source;

namespace SqlFlow.Delivery.Engine;

/// <summary>The operations a run of the delivery, cache and retrieval kinds performs, by the names runs carry.</summary>
public static class DeliveryOperations
{
    /// <summary>Plan the source and deliver what changed: the delivery kind's default.</summary>
    public const string Deliver = "deliver";

    /// <summary>Plan and report, change nothing.</summary>
    public const string Plan = "plan";

    /// <summary>Plan into work batches without delivering: a fan-out member's share, or a planning run alone.</summary>
    public const string Intake = "intake";

    /// <summary>Deliver the work batches already planned.</summary>
    public const string Drain = "drain";

    /// <summary>Compare what OSDU holds with what the ledger recorded.</summary>
    public const string Verify = "verify";

    /// <summary>Read every row of the scope again and deliver what renders differently now.</summary>
    public const string Replan = "replan";

    /// <summary>Capture a cache flow's types into its partition's cache: the cache kind's default.</summary>
    public const string Refresh = "refresh";

    /// <summary>Retrieve records of OSDU kinds into files: the retrieval kind's default.</summary>
    public const string Retrieve = "retrieve";

    /// <summary>The operation a run of a delivery flow performs: the one it names, or <see cref="Deliver"/>.</summary>
    public static string Of(RunParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return parameters.Operation ?? Deliver;
    }

    /// <summary>
    /// Refuses the per-run overrides SQLFlow's own flow kinds take (a full load, a backfill window, a file pattern, a source
    /// filter, an assertions-only or reprocess run): a flow of a registered kind reads its source its own way, and quietly
    /// ignoring one of them would run something other than what the caller asked for.
    /// </summary>
    public static void RefuseBuiltInOverrides(RunParameters parameters, string flowType, string instead)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var named = new List<string>();
        if (parameters.FullLoad)
        {
            named.Add("fullLoad");
        }

        if (parameters.BackfillFrom is not null || parameters.BackfillTo is not null)
        {
            named.Add("a backfill window");
        }

        if (parameters.FilePattern is not null)
        {
            named.Add("filePattern");
        }

        if (parameters.SourceFilter is not null)
        {
            named.Add("sourceFilter");
        }

        if (parameters.AssertionsOnly)
        {
            named.Add("assertionsOnly");
        }

        if (parameters.ReprocessFromSourceMin)
        {
            named.Add("reprocessFromSourceMin");
        }

        if (named.Count > 0)
        {
            throw new SqlFlowException($"{string.Join(", ", named)} do(es) not apply to '{flowType}' flows; {instead}");
        }
    }
}

/// <summary>
/// What a record-scoped deliver run sends again, by the names a run's payload and the API carry
/// (docs/interfaces-design.md section 10): everything, or one part of the record, named by what it is on the record's
/// route. The part that is not named keeps what OSDU holds: the record keeps its dataset references and its DDMS bulk
/// link, and a bulk resend writes a new bulk version of the same record.
/// </summary>
public static class RedeliverScopes
{
    public const string All = "all";

    /// <summary>The record document alone.</summary>
    public const string Record = "record";

    /// <summary>The files a record carries (the file, dataset, manifest and composed routes, and a workflow route's own): uploaded and registered again, and the record rewritten with them.</summary>
    public const string Files = "files";

    /// <summary>The bulk data a DDMS stores for the record (the ddms route): written again as a new version of the record's bulk data.</summary>
    public const string Bulk = "bulk";

    /// <summary>The record document, by the name the single form has always used for it.</summary>
    public const string Metadata = "metadata";

    /// <summary>The files or the bulk data, whichever the route sends, by the name the single form has always used for them.</summary>
    public const string Payload = "payload";

    /// <summary>The workflow run of the workflow route: triggered again, without sending anything else again.</summary>
    public const string Workflow = PayloadParts.Workflow;

    public static IReadOnlyList<string> Names { get; } = [All, Record, Files, Bulk, Workflow, Metadata, Payload];

    /// <summary>The parts a record <paramref name="flow"/> delivers can be sent again by.</summary>
    public static IReadOnlyList<string> For(FlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (PayloadParts.Of(flow) is not null)
        {
            return [All, Record, .. PayloadParts.Roles(flow), Metadata, Payload];
        }

        return flow.Target.Protocol switch
        {
            DeliveryProtocol.File or DeliveryProtocol.Manifest or DeliveryProtocol.Dataset => [All, Record, Files, Metadata, Payload],
            DeliveryProtocol.Ddms => [All, Record, Bulk, Metadata, Payload],
            _ => [All, Record, Metadata],
        };
    }

    /// <summary>
    /// What <paramref name="name"/> sends again of a record <paramref name="flow"/> delivers: everything when it names
    /// nothing. A route that sends its payload in parts sends only the part named (files, bulk, workflow). Throws
    /// <see cref="DeliveryException"/> for a name that is not a part, or a part the flow's route does not send (files on
    /// a ddms route, bulk data on a file route, anything but the record on the storage route).
    /// </summary>
    public static RedeliverSelection Of(string? name, FlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var part = string.IsNullOrWhiteSpace(name) ? All : name.Trim().ToLowerInvariant();
        if (!Names.Contains(part, StringComparer.Ordinal))
        {
            throw new DeliveryException($"redeliver '{name}' is not one of {string.Join(", ", Names)}.");
        }

        var parts = For(flow);
        if (!parts.Contains(part, StringComparer.Ordinal))
        {
            throw new DeliveryException(
                $"'{flow.Label}' is delivered by the {DeliveryProtocols.Name(flow.Target.Protocol)} route, which sends {Sends(flow)}, so a redelivery of '{part}' has nothing to send; name one of {string.Join(", ", parts)}.");
        }

        return part switch
        {
            All => new RedeliverSelection(RedeliverScope.All, []),
            Record or Metadata => new RedeliverSelection(RedeliverScope.Metadata, []),
            Payload => new RedeliverSelection(RedeliverScope.Payload, []),
            _ when PayloadParts.Of(flow) is not null => new RedeliverSelection(RedeliverScope.Payload, [part]),
            _ => new RedeliverSelection(RedeliverScope.Payload, []),
        };
    }

    private static string Sends(FlowDefinition flow)
    {
        if (PayloadParts.Of(flow) is not null)
        {
            var roles = PayloadParts.Roles(flow);
            return "the record" + (roles.Count == 0 ? string.Empty : ", " + string.Join(", ", roles.Select(r => r switch
            {
                PayloadParts.Files => "its files",
                PayloadParts.Bulk => "its bulk data",
                _ => "its workflow run",
            })));
        }

        return flow.Target.Protocol switch
        {
            DeliveryProtocol.File or DeliveryProtocol.Manifest or DeliveryProtocol.Dataset => "the record and its files",
            DeliveryProtocol.Ddms => "the record and its bulk data",
            DeliveryProtocol.Etp => "the object alone, its XML and arrays rendered into its document",
            _ => "the record alone",
        };
    }
}

/// <summary>
/// The kind-owned arguments of a delivery run (<see cref="RunParameters.Payload"/>): whether it forces a re-plan, the
/// submission it works on, the records it is scoped to and what of them it redelivers, and the key slices a fan-out
/// member plans. Parsed strictly: an unknown property, a wrong type
/// or a value out of range is refused with a message naming it, at every trust boundary the platform validates a run at.
/// </summary>
public sealed record DeliveryRunPayload
{
    /// <summary>The most records one run can be scoped to.</summary>
    public const int MaxRecordKeys = 1000;

    public const string ForceProperty = "force";

    public const string SubmissionIdProperty = "submissionId";

    public const string RecordKeysProperty = "recordKeys";

    public const string RedeliverProperty = "redeliver";

    public const string SlicesProperty = "slices";

    public const string InterfaceProperty = "interface";

    public const string InterfacesProperty = "interfaces";

    /// <summary>The central configuration the control plane supplied with this run.</summary>
    public const string ReferencesProperty = "references";

    private static readonly string[] Properties = [ForceProperty, SubmissionIdProperty, RecordKeysProperty, RedeliverProperty, SlicesProperty, InterfaceProperty, InterfacesProperty, ReferencesProperty];

    public static DeliveryRunPayload None { get; } = new();

    /// <summary>Lift the whole-run gates: tier 0 and an already completed submission. Each record's own hashes still decide.</summary>
    public bool Force { get; init; }

    /// <summary>The submission the run works on: a re-run, or a fan-out member's share.</summary>
    public Guid? SubmissionId { get; init; }

    /// <summary>The delivery keys the run is scoped to.</summary>
    public IReadOnlyList<Guid> RecordKeys { get; init; } = [];

    /// <summary>What of the scoped records is sent again: all, metadata or payload; null means all.</summary>
    public string? Redeliver { get; init; }

    /// <summary>The key slices of <see cref="SubmissionId"/> an intake member plans.</summary>
    public IReadOnlyList<int> Slices { get; init; } = [];

    /// <summary>
    /// The one interface of the source the run works on: what a fan-out member, a record-scoped run and a run on a
    /// submission name when the flow declares several. Null for a flow in the single form, and for a run of the whole source.
    /// </summary>
    public string? Interface { get; init; }

    /// <summary>The interfaces a run of a source runs, in any order; empty runs every interface.</summary>
    public IReadOnlyList<string> Interfaces { get; init; } = [];

    /// <summary>
    /// The central configuration the control plane supplied with this run: the values a flow's ${env:NAME} references
    /// resolve to, ahead of the node's own environment. Empty when the control plane holds none, which leaves every
    /// reference to the node. A value here is a non-secret value or a reference the node resolves, never a secret.
    /// </summary>
    public IReadOnlyDictionary<string, string> References { get; init; } = ReadOnlyDictionary<string, string>.Empty;

    /// <summary>True when the payload carries nothing.</summary>
    public bool IsEmpty
        => !Force && SubmissionId is null && RecordKeys.Count == 0 && Redeliver is null && Slices.Count == 0 && Interface is null && Interfaces.Count == 0
            && References.Count == 0;

    /// <summary>The payload of a run's parameters; none when it carries none.</summary>
    public static DeliveryRunPayload Parse(RunParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return Parse(parameters.Payload);
    }

    /// <summary>Parses a payload's JSON text strictly; null or blank is no payload.</summary>
    public static DeliveryRunPayload Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return None;
        }

        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject ?? throw new SqlFlowException("payload must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new SqlFlowException($"payload is not valid JSON: {ex.Message}", ex);
        }

        foreach (var (name, _) in root)
        {
            if (!Properties.Contains(name, StringComparer.Ordinal))
            {
                throw new SqlFlowException($"payload property '{name}' is not one of {string.Join(", ", Properties)}.");
            }
        }

        return new DeliveryRunPayload
        {
            Force = Boolean(root, ForceProperty),
            SubmissionId = root[SubmissionIdProperty] is null ? null : Id(root[SubmissionIdProperty], SubmissionIdProperty),
            RecordKeys = Keys(root[RecordKeysProperty]),
            Redeliver = root[RedeliverProperty] is null ? null : Text(root[RedeliverProperty], RedeliverProperty),
            Slices = SliceList(root[SlicesProperty]),
            Interface = root[InterfaceProperty] is null ? null : Text(root[InterfaceProperty], InterfaceProperty),
            Interfaces = Names(root[InterfacesProperty]),
            References = ReferenceMap(root[ReferencesProperty]),
        };
    }

    /// <summary>
    /// Checks the payload against the operation it travels with, throwing <see cref="SqlFlowException"/> naming the property:
    /// what each operation takes, and the ranges every property keeps to.
    /// </summary>
    public void Validate(string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        if (RecordKeys.Count > MaxRecordKeys)
        {
            throw new SqlFlowException($"payload recordKeys holds {RecordKeys.Count} keys; one run is scoped to at most {MaxRecordKeys} records.");
        }

        if (RecordKeys.Any(k => k == Guid.Empty))
        {
            throw new SqlFlowException("payload recordKeys holds an empty UUID; a delivery key is a non-empty UUID.");
        }

        if (RecordKeys.Distinct().Count() != RecordKeys.Count)
        {
            throw new SqlFlowException("payload recordKeys names a record more than once.");
        }

        if (SubmissionId == Guid.Empty)
        {
            throw new SqlFlowException("payload submissionId is an empty UUID.");
        }

        if (Redeliver is { } scope && !RedeliverScopes.Names.Contains(scope, StringComparer.OrdinalIgnoreCase))
        {
            throw new SqlFlowException($"payload redeliver '{scope}' is not one of {string.Join(", ", RedeliverScopes.Names)}.");
        }

        if (Slices.Any(s => s < 0 || s >= KeySlices.MaxSlices))
        {
            throw new SqlFlowException($"payload slices holds an index outside 0 to {KeySlices.MaxSlices - 1}.");
        }

        if (Slices.Distinct().Count() != Slices.Count)
        {
            throw new SqlFlowException("payload slices names a slice more than once.");
        }

        if (Interface is { } named && !SourceDefinition.IsInterfaceName(named))
        {
            throw new SqlFlowException($"payload interface '{named}' is not an interface name: a letter followed by letters, digits, '_' and '-'.");
        }

        foreach (var name in Interfaces)
        {
            if (!SourceDefinition.IsInterfaceName(name))
            {
                throw new SqlFlowException($"payload interfaces holds '{name}', which is not an interface name: a letter followed by letters, digits, '_' and '-'.");
            }
        }

        if (Interfaces.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Interfaces.Count)
        {
            throw new SqlFlowException("payload interfaces names an interface more than once.");
        }

        if (Interface is not null && Interfaces.Count > 0)
        {
            throw new SqlFlowException("payload names both an interface and interfaces; a run works on one interface or runs a selection of them, not both.");
        }

        if (Interfaces.Count > 0 && (SubmissionId is not null || RecordKeys.Count > 0 || Slices.Count > 0))
        {
            throw new SqlFlowException("payload interfaces selects interfaces for a run of the source; a run on a submission, on records or on slices works on one interface, which 'interface' names.");
        }

        if (SubmissionId is not null && RecordKeys.Count > 0)
        {
            throw new SqlFlowException("payload names both a submissionId and recordKeys; a run works on a submission or is scoped to records, not both.");
        }

        switch (operation)
        {
            case DeliveryOperations.Deliver:
                Refuse(Slices.Count > 0, SlicesProperty, operation, "only an intake member plans slices");
                Refuse(Redeliver is not null && RecordKeys.Count == 0, RedeliverProperty, operation, "it says what of the records named by recordKeys is sent again");
                break;
            case DeliveryOperations.Plan:
                Refuse(Slices.Count > 0, SlicesProperty, operation, "only an intake member plans slices");
                Refuse(Redeliver is not null, RedeliverProperty, operation, "a plan sends nothing");
                break;
            case DeliveryOperations.Intake:
                Refuse(Redeliver is not null, RedeliverProperty, operation, "an intake sends nothing");
                Refuse(Slices.Count > 0 && SubmissionId is null, SlicesProperty, operation, "slices are cut from the submission their coordinating run registered, which submissionId names");
                break;
            case DeliveryOperations.Drain:
                Refuse(Force, ForceProperty, operation, "a drain plans nothing to force");
                Refuse(RecordKeys.Count > 0, RecordKeysProperty, operation, "a drain delivers the batches a submission planned");
                Refuse(Redeliver is not null, RedeliverProperty, operation, "a drain delivers what was planned");
                Refuse(Slices.Count > 0, SlicesProperty, operation, "only an intake member plans slices");
                break;
            case DeliveryOperations.Verify:
                Refuse(SubmissionId is not null, SubmissionIdProperty, operation, "a verify reads the ledger's delivered records, not a submission");
                Refuse(Redeliver is not null, RedeliverProperty, operation, "a verify sends nothing");
                Refuse(Slices.Count > 0, SlicesProperty, operation, "only an intake member plans slices");
                break;
            case DeliveryOperations.Replan:
                Refuse(SubmissionId is not null, SubmissionIdProperty, operation, "a replan reads every row of the scope under a submission of its own");
                Refuse(RecordKeys.Count > 0, RecordKeysProperty, operation, "a replan reads every row of the scope; scope a deliver run to records instead");
                Refuse(Redeliver is not null, RedeliverProperty, operation, "a replan decides by each record's hashes");
                Refuse(Slices.Count > 0, SlicesProperty, operation, "only an intake member plans slices");
                break;
            default:
                throw new SqlFlowException($"'{operation}' is not an operation of a delivery flow.");
        }
    }

    /// <summary>The compact JSON text a run carries, or null when the payload carries nothing.</summary>
    public string? ToJson()
    {
        if (IsEmpty)
        {
            return null;
        }

        var root = new JsonObject();
        if (Force)
        {
            root[ForceProperty] = true;
        }

        if (SubmissionId is { } submission)
        {
            root[SubmissionIdProperty] = submission.ToString("D");
        }

        if (RecordKeys.Count > 0)
        {
            root[RecordKeysProperty] = new JsonArray(RecordKeys.Select(k => (JsonNode?)JsonValue.Create(k.ToString("D"))).ToArray());
        }

        if (Redeliver is { } redeliver)
        {
            root[RedeliverProperty] = redeliver;
        }

        if (Slices.Count > 0)
        {
            root[SlicesProperty] = new JsonArray(Slices.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        }

        if (Interface is { } named)
        {
            root[InterfaceProperty] = named;
        }

        if (Interfaces.Count > 0)
        {
            root[InterfacesProperty] = new JsonArray(Interfaces.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
        }

        if (References.Count > 0)
        {
            // Ordered by name so the same configuration writes the same payload, which keeps a run row comparable.
            var references = new JsonObject();
            foreach (var (name, value) in References.OrderBy(r => r.Key, StringComparer.Ordinal))
            {
                references[name] = value;
            }

            root[ReferencesProperty] = references;
        }

        return root.ToJsonString();
    }

    private static void Refuse(bool refused, string property, string operation, string why)
    {
        if (refused)
        {
            throw new SqlFlowException($"payload {property} does not apply to the {operation} operation: {why}.");
        }
    }

    private static bool Boolean(JsonObject root, string property)
    {
        var node = root[property];
        if (node is null)
        {
            return false;
        }

        return node is JsonValue value && value.TryGetValue<bool>(out var flag)
            ? flag
            : throw new SqlFlowException($"payload {property} must be true or false.");
    }

    private static string Text(JsonNode? node, string property)
        => node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : throw new SqlFlowException($"payload {property} must be a non-empty string.");

    private static Guid Id(JsonNode? node, string property)
        => Guid.TryParse(Text(node, property), out var id)
            ? id
            : throw new SqlFlowException($"payload {property} must be a UUID.");

    private static IReadOnlyList<Guid> Keys(JsonNode? node)
    {
        if (node is null)
        {
            return [];
        }

        if (node is not JsonArray array)
        {
            throw new SqlFlowException($"payload {RecordKeysProperty} must be an array of delivery key UUIDs.");
        }

        if (array.Count > MaxRecordKeys)
        {
            throw new SqlFlowException($"payload {RecordKeysProperty} holds {array.Count} keys; one run is scoped to at most {MaxRecordKeys} records.");
        }

        return array.Select(item => Id(item, RecordKeysProperty)).ToList();
    }

    /// <summary>
    /// The central configuration a payload carries: a JSON object of reference name to value, refused property by
    /// property so a bad one names itself. Absent is none, which leaves every reference to the node.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ReferenceMap(JsonNode? node)
    {
        if (node is null)
        {
            return ReadOnlyDictionary<string, string>.Empty;
        }

        if (node is not JsonObject obj)
        {
            throw new SqlFlowException($"payload {ReferencesProperty} must be a JSON object of reference name to value.");
        }

        if (obj.Count > DeliveryConfigNames.MaxPerRun)
        {
            throw new SqlFlowException($"payload {ReferencesProperty} holds {obj.Count} properties; one run carries at most {DeliveryConfigNames.MaxPerRun}.");
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in obj)
        {
            if (!DeliveryConfigNames.IsName(name))
            {
                throw new SqlFlowException($"payload {ReferencesProperty} property '{name}' does not name a reference: a letter or underscore followed by letters, digits and underscores, at most {DeliveryConfigNames.MaxNameLength} characters.");
            }

            if (value is not JsonValue text || !text.TryGetValue<string>(out var supplied) || !DeliveryConfigNames.IsValue(supplied))
            {
                throw new SqlFlowException($"payload {ReferencesProperty} property '{name}' must be a non-empty string of at most {DeliveryConfigNames.MaxValueLength} characters without control characters.");
            }

            map[name] = supplied;
        }

        return map;
    }

    private static IReadOnlyList<string> Names(JsonNode? node)
    {
        if (node is null)
        {
            return [];
        }

        if (node is not JsonArray array || array.Count > SourceDefinition.MaxInterfaces)
        {
            throw new SqlFlowException($"payload {InterfacesProperty} must be an array of at most {SourceDefinition.MaxInterfaces} interface names.");
        }

        return array.Select(item => Text(item, InterfacesProperty)).ToList();
    }

    private static IReadOnlyList<int> SliceList(JsonNode? node)
    {
        if (node is null)
        {
            return [];
        }

        if (node is not JsonArray array || array.Count > KeySlices.MaxSlices)
        {
            throw new SqlFlowException($"payload {SlicesProperty} must be an array of at most {KeySlices.MaxSlices} slice indexes.");
        }

        return array
            .Select(item => item is JsonValue value && value.TryGetValue<int>(out var slice)
                ? slice
                : throw new SqlFlowException($"payload {SlicesProperty} must hold whole numbers."))
            .ToList();
    }
}
