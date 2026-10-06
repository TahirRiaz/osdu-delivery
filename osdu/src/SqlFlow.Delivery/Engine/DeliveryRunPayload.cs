using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;

namespace SqlFlow.Delivery.Engine;

/// <summary>The operations a run of the delivery, cache, retrieval, assertion and dimension kinds performs, by the names runs carry.</summary>
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

    /// <summary>
    /// Read the rows of the ledger's records from the ingestion tables and consolidate the ledger with them: record what
    /// it lacks of a row, ask for the records whose rows changed unseen to be planned again, and report the rows that are
    /// gone. Nothing is rendered and nothing reaches OSDU.
    /// </summary>
    public const string Sync = "sync";

    /// <summary>
    /// Put OSDU back as it was before one run or one submission (docs/reversal-plan.md): what it created is removed again and
    /// what it updated is given back the version OSDU held before, record by record, every step written to the ledger. The
    /// payload names the source: <c>submissionId</c>, or <c>runId</c>.
    /// </summary>
    public const string Reverse = "reverse";

    /// <summary>Capture a cache flow's types into its partition's cache: the cache kind's default.</summary>
    public const string Refresh = "refresh";

    /// <summary>Retrieve records of OSDU kinds into files: the retrieval kind's default.</summary>
    public const string Retrieve = "retrieve";

    /// <summary>Run an assertion flow's tests against what OSDU holds and record their results: the assertion kind's default.</summary>
    public const string Test = "test";

    /// <summary>Read every distinct value of a dimension flow's dimensions and keep them with their clean values: the dimension kind's default.</summary>
    public const string Build = "build";

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
/// What a deliver run sends again, by the names a run's payload and the API carry
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

    /// <summary>The run a reverse run reverses (docs/reversal-plan.md).</summary>
    public const string RunIdProperty = "runId";

    public const string RecordKeysProperty = "recordKeys";

    public const string RedeliverProperty = "redeliver";

    public const string RerenderProperty = "rerender";

    public const string SlicesProperty = "slices";

    public const string InterfaceProperty = "interface";

    public const string InterfacesProperty = "interfaces";

    /// <summary>The central configuration the control plane supplied with this run.</summary>
    public const string ReferencesProperty = "references";

    /// <summary>The central configuration set for one partition, by partition, which a run bound to it resolves with first.</summary>
    public const string PartitionReferencesProperty = "partitionReferences";

    /// <summary>The tests of an assertion flow a run runs, by name.</summary>
    public const string TestsProperty = "tests";

    /// <summary>The tags of an assertion flow's tests a run runs: every test carrying one of them.</summary>
    public const string TagsProperty = "tags";

    /// <summary>The dimensions of a dimension flow a run builds, by name.</summary>
    public const string DimensionsProperty = "dimensions";

    /// <summary>The most test names, and the most tags, one run selects.</summary>
    public const int MaxSelected = AssertionFlowDefinition.MaxTests;

    private static readonly string[] Properties =
        [ForceProperty, SubmissionIdProperty, RunIdProperty, RecordKeysProperty, RedeliverProperty, RerenderProperty, SlicesProperty, InterfaceProperty, InterfacesProperty, ReferencesProperty,
            PartitionReferencesProperty, TestsProperty, TagsProperty, DimensionsProperty];

    public static DeliveryRunPayload None { get; } = new();

    /// <summary>
    /// Look at every record past the whole-run gates: tier 0 and an already completed submission. It sends nothing that
    /// has not changed, since each record's own hashes still decide; <see cref="Redeliver"/> sends records again.
    /// </summary>
    public bool Force { get; init; }

    /// <summary>The submission the run works on: a re-run, a fan-out member's share, or the submission a reverse run reverses.</summary>
    public Guid? SubmissionId { get; init; }

    /// <summary>
    /// The run a reverse run reverses: what was delivered under the submissions it coordinated, and what it delivered itself
    /// (docs/reversal-plan.md). Only a reverse run names it.
    /// </summary>
    public Guid? RunId { get; init; }

    /// <summary>The delivery keys the run is scoped to.</summary>
    public IReadOnlyList<Guid> RecordKeys { get; init; } = [];

    /// <summary>
    /// What a deliver run sends again whatever the hashes say: all, the record, or its payload or a part of it. It applies to
    /// the records <see cref="RecordKeys"/> names, or without keys to every record the flow has delivered. Null sends again
    /// everything of named records, and nothing of a run without keys.
    /// </summary>
    public string? Redeliver { get; init; }

    /// <summary>
    /// Brings records up to date: renders them again under the rules of now and lets each record's hashes decide what is
    /// sent, so only a part that renders differently goes, where <see cref="Redeliver"/> sends whatever the hashes say. A
    /// deliver run asks it of the records <see cref="RecordKeys"/> names, or without keys of every record the flow has
    /// delivered, as a request the ledger keeps until a run plans each record. A plan run renders every record it reads
    /// (those keys, or every row of the scope) without passing an unchanged one, and so says what a delivery would send.
    /// </summary>
    public bool Rerender { get; init; }

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

    /// <summary>
    /// The central configuration set for single partitions (docs/partitions-design.md section 5), by partition name: what a
    /// run bound to one of them resolves with ahead of <see cref="References"/>. Every partition the control plane holds
    /// values for travels, so a run that refreshes several partitions in turn resolves each with its own.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> PartitionReferences { get; init; }
        = ReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>.Empty;

    /// <summary>
    /// The tests of an assertion flow the run runs, by name (docs/assertions-design.md section 6): with <see cref="Tags"/>,
    /// a test runs when it is named here or carries one of the tags; with neither, every test runs.
    /// </summary>
    public IReadOnlyList<string> Tests { get; init; } = [];

    /// <summary>The tags of an assertion flow's tests the run runs: every test carrying one of them.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>True when the payload selects tests of an assertion flow, by name or by tag.</summary>
    public bool SelectsTests => Tests.Count > 0 || Tags.Count > 0;

    /// <summary>The dimensions of a dimension flow the run builds, by name; with none, every dimension is built.</summary>
    public IReadOnlyList<string> Dimensions { get; init; } = [];

    /// <summary>True when the payload selects dimensions of a dimension flow.</summary>
    public bool SelectsDimensions => Dimensions.Count > 0;

    /// <summary>True when the payload carries nothing.</summary>
    public bool IsEmpty
        => !Force && SubmissionId is null && RunId is null && RecordKeys.Count == 0 && Redeliver is null && !Rerender && Slices.Count == 0 && Interface is null && Interfaces.Count == 0
            && References.Count == 0 && PartitionReferences.Count == 0 && !SelectsTests && !SelectsDimensions;

    /// <summary>
    /// True when the payload carries nothing but the central configuration the control plane supplied: what the payload of
    /// a kind that names no submission, record, slice, test or dimension may hold.
    /// </summary>
    public bool CarriesOnlyConfiguration
        => !Force && SubmissionId is null && RunId is null && RecordKeys.Count == 0 && Redeliver is null && !Rerender && Slices.Count == 0 && Interface is null && Interfaces.Count == 0
            && !SelectsTests && !SelectsDimensions;

    /// <summary>
    /// The configuration a run resolves its references with when it acts on <paramref name="partition"/>: the partition's
    /// own values over the ones set for no partition. A value set for a partition always wins, so an estate-wide endpoint
    /// never sends a run of one partition to another's platform. With no partition, the values set for none.
    /// </summary>
    public IReadOnlyDictionary<string, string> ReferencesFor(string? partition)
    {
        if (partition is null)
        {
            return References;
        }

        var own = PartitionReferences.FirstOrDefault(p => string.Equals(p.Key, partition, StringComparison.OrdinalIgnoreCase)).Value;
        if (own is null || own.Count == 0)
        {
            return References;
        }

        var merged = new Dictionary<string, string>(References, StringComparer.Ordinal);
        foreach (var (name, value) in own)
        {
            merged[name] = value;
        }

        return merged;
    }

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
            RunId = root[RunIdProperty] is null ? null : Id(root[RunIdProperty], RunIdProperty),
            RecordKeys = Keys(root[RecordKeysProperty]),
            Redeliver = root[RedeliverProperty] is null ? null : Text(root[RedeliverProperty], RedeliverProperty),
            Rerender = Boolean(root, RerenderProperty),
            Slices = SliceList(root[SlicesProperty]),
            Interface = root[InterfaceProperty] is null ? null : Text(root[InterfaceProperty], InterfaceProperty),
            Interfaces = Names(root[InterfacesProperty]),
            References = ReferenceMap(root[ReferencesProperty], ReferencesProperty),
            PartitionReferences = PartitionReferenceMap(root[PartitionReferencesProperty]),
            Tests = Selection(root[TestsProperty], TestsProperty, "test names"),
            Tags = Selection(root[TagsProperty], TagsProperty, "tags"),
            Dimensions = Selection(root[DimensionsProperty], DimensionsProperty, "dimension names"),
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

        if (RunId == Guid.Empty)
        {
            throw new SqlFlowException("payload runId is an empty UUID.");
        }

        if (RunId is not null && operation != DeliveryOperations.Reverse)
        {
            throw new SqlFlowException($"payload {RunIdProperty} does not apply to the {operation} operation: only a reverse run names the run it reverses.");
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

        if (SelectsTests)
        {
            throw new SqlFlowException(
                $"payload {(Tests.Count > 0 ? TestsProperty : TagsProperty)} does not apply to a delivery flow: only an assertion flow's runs select tests.");
        }

        if (SelectsDimensions)
        {
            throw new SqlFlowException($"payload {DimensionsProperty} does not apply to a delivery flow: only a dimension flow's runs select dimensions.");
        }

        switch (operation)
        {
            case DeliveryOperations.Deliver:
                Refuse(Slices.Count > 0, SlicesProperty, operation, "only an intake member plans slices");
                Refuse(Redeliver is not null && SubmissionId is not null, RedeliverProperty, operation, "a run on a submission delivers what that submission planned");
                Refuse(Rerender && SubmissionId is not null, RerenderProperty, operation, "a run on a submission delivers what that submission planned");
                Refuse(Rerender && Redeliver is not null, RerenderProperty, operation,
                    "rerender sends what renders differently and redeliver sends whatever the hashes say; name one");
                break;
            case DeliveryOperations.Plan:
                Refuse(Slices.Count > 0, SlicesProperty, operation, "only an intake member plans slices");
                Refuse(Redeliver is not null, RedeliverProperty, operation, "a plan sends nothing");
                Refuse(Rerender && SubmissionId is not null, RerenderProperty, operation, "a plan of a submission reads the rows that submission recorded");
                break;
            case DeliveryOperations.Intake:
                Refuse(Redeliver is not null, RedeliverProperty, operation, "an intake sends nothing");
                Refuse(Rerender, RerenderProperty, operation, "an intake sends nothing; bring records up to date with a deliver run");
                Refuse(Slices.Count > 0 && SubmissionId is null, SlicesProperty, operation, "slices are cut from the submission their coordinating run registered, which submissionId names");
                break;
            case DeliveryOperations.Drain:
                Refuse(Force, ForceProperty, operation, "a drain plans nothing to force");
                Refuse(RecordKeys.Count > 0, RecordKeysProperty, operation, "a drain delivers the batches a submission planned");
                Refuse(Redeliver is not null, RedeliverProperty, operation, "a drain delivers what was planned");
                Refuse(Rerender, RerenderProperty, operation, "a drain delivers what was planned");
                Refuse(Slices.Count > 0, SlicesProperty, operation, "only an intake member plans slices");
                break;
            case DeliveryOperations.Verify:
                Refuse(SubmissionId is not null, SubmissionIdProperty, operation, "a verify reads the ledger's delivered records, not a submission");
                Refuse(Redeliver is not null, RedeliverProperty, operation, "a verify sends nothing");
                Refuse(Rerender, RerenderProperty, operation, "a verify sends nothing");
                Refuse(Slices.Count > 0, SlicesProperty, operation, "only an intake member plans slices");
                break;
            case DeliveryOperations.Sync:
                Refuse(Force, ForceProperty, operation, "a sync passes no gate: it reads every row it is given");
                Refuse(SubmissionId is not null, SubmissionIdProperty, operation, "a sync reads the rows of the ledger's records, not a submission's");
                Refuse(Redeliver is not null, RedeliverProperty, operation, "a sync sends nothing");
                Refuse(Rerender, RerenderProperty, operation, "a sync sends nothing");
                Refuse(Slices.Count > 0, SlicesProperty, operation, "only an intake member plans slices");
                break;
            case DeliveryOperations.Reverse:
                Refuse(SubmissionId is null && RunId is null, SubmissionIdProperty, operation, "a reverse run names what it reverses, a submissionId or a runId");
                Refuse(SubmissionId is not null && RunId is not null, RunIdProperty, operation, "a reverse run reverses one submission or one run, not both");
                Refuse(Force, ForceProperty, operation, "a reversal passes no gate: it takes every record its source delivered");
                Refuse(RecordKeys.Count > 0, RecordKeysProperty, operation, "a reversal takes every record its source delivered");
                Refuse(Redeliver is not null, RedeliverProperty, operation, "a reversal puts back what OSDU held; it sends nothing the flow renders");
                Refuse(Rerender, RerenderProperty, operation, "a reversal puts back what OSDU held; it renders nothing");
                Refuse(Slices.Count > 0, SlicesProperty, operation, "only an intake member plans slices");
                Refuse(Interfaces.Count > 0, InterfacesProperty, operation, "a reversal works in one interface's ledger, which 'interface' names");
                break;
            case DeliveryOperations.Replan:
                Refuse(SubmissionId is not null, SubmissionIdProperty, operation, "a replan reads every row of the scope under a submission of its own");
                Refuse(RecordKeys.Count > 0, RecordKeysProperty, operation, "a replan reads every row of the scope; scope a deliver run to records instead");
                Refuse(Redeliver is not null, RedeliverProperty, operation, "a replan decides by each record's hashes");
                Refuse(Rerender, RerenderProperty, operation, "a replan reads the rows of the scope; bring delivered records up to date with a deliver run");
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

        if (RunId is { } run)
        {
            root[RunIdProperty] = run.ToString("D");
        }

        if (RecordKeys.Count > 0)
        {
            root[RecordKeysProperty] = new JsonArray(RecordKeys.Select(k => (JsonNode?)JsonValue.Create(k.ToString("D"))).ToArray());
        }

        if (Redeliver is { } redeliver)
        {
            root[RedeliverProperty] = redeliver;
        }

        if (Rerender)
        {
            root[RerenderProperty] = true;
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

        if (PartitionReferences.Count > 0)
        {
            var partitions = new JsonObject();
            foreach (var (partition, values) in PartitionReferences.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var map = new JsonObject();
                foreach (var (name, value) in values.OrderBy(r => r.Key, StringComparer.Ordinal))
                {
                    map[name] = value;
                }

                partitions[partition] = map;
            }

            root[PartitionReferencesProperty] = partitions;
        }

        if (Tests.Count > 0)
        {
            root[TestsProperty] = new JsonArray(Tests.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
        }

        if (Tags.Count > 0)
        {
            root[TagsProperty] = new JsonArray(Tags.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
        }

        if (Dimensions.Count > 0)
        {
            root[DimensionsProperty] = new JsonArray(Dimensions.Select(d => (JsonNode?)JsonValue.Create(d)).ToArray());
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
    private static IReadOnlyDictionary<string, string> ReferenceMap(JsonNode? node, string property)
    {
        if (node is null)
        {
            return ReadOnlyDictionary<string, string>.Empty;
        }

        if (node is not JsonObject obj)
        {
            throw new SqlFlowException($"payload {property} must be a JSON object of reference name to value.");
        }

        if (obj.Count > DeliveryConfigNames.MaxPerRun)
        {
            throw new SqlFlowException($"payload {property} holds {obj.Count} properties; one run carries at most {DeliveryConfigNames.MaxPerRun}.");
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in obj)
        {
            if (!DeliveryConfigNames.IsName(name))
            {
                throw new SqlFlowException($"payload {property} property '{name}' does not name a reference: a letter or underscore followed by letters, digits and underscores, at most {DeliveryConfigNames.MaxNameLength} characters.");
            }

            if (value is not JsonValue text || !text.TryGetValue<string>(out var supplied) || !DeliveryConfigNames.IsValue(supplied))
            {
                throw new SqlFlowException($"payload {property} property '{name}' must be a non-empty string of at most {DeliveryConfigNames.MaxValueLength} characters without control characters.");
            }

            map[name] = supplied;
        }

        return map;
    }

    /// <summary>
    /// The central configuration set for single partitions a payload carries: a JSON object of partition name to the
    /// partition's reference map, each refused as a flow refuses a partition name and as <see cref="References"/> refuses a map.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> PartitionReferenceMap(JsonNode? node)
    {
        if (node is null)
        {
            return ReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>.Empty;
        }

        if (node is not JsonObject obj)
        {
            throw new SqlFlowException($"payload {PartitionReferencesProperty} must be a JSON object of partition name to reference map.");
        }

        if (obj.Count > PartitionNames.MaxPerFlow)
        {
            throw new SqlFlowException($"payload {PartitionReferencesProperty} holds {obj.Count} partitions; one run carries the values of at most {PartitionNames.MaxPerFlow}.");
        }

        var map = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (partition, values) in obj)
        {
            if (!CacheScope.IsPartitionId(partition))
            {
                throw new SqlFlowException($"payload {PartitionReferencesProperty} names '{partition}', which is not a data-partition-id.");
            }

            if (!map.TryAdd(partition, ReferenceMap(values, $"{PartitionReferencesProperty}.{partition}")))
            {
                throw new SqlFlowException($"payload {PartitionReferencesProperty} names partition '{partition}' more than once (ignoring case).");
            }
        }

        return map;
    }

    /// <summary>
    /// A selection of an assertion flow's tests or tags, or of a dimension flow's dimensions: an array of names, each a name as
    /// a flow writes one (<see cref="SelectableNames.IsName"/>), none twice. Whether each names something of the flow is the
    /// run's to check, since a payload is validated before the flow document is read.
    /// </summary>
    private static IReadOnlyList<string> Selection(JsonNode? node, string property, string what)
    {
        if (node is null)
        {
            return [];
        }

        if (node is not JsonArray array || array.Count > MaxSelected)
        {
            throw new SqlFlowException($"payload {property} must be an array of at most {MaxSelected} {what}.");
        }

        var names = new List<string>(array.Count);
        foreach (var item in array)
        {
            var name = Text(item, property);
            if (!SelectableNames.IsName(name))
            {
                throw new SqlFlowException($"payload {property} holds '{name}', which is not a name: {SelectableNames.Rule}.");
            }

            if (names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                throw new SqlFlowException($"payload {property} names '{name}' more than once.");
            }

            names.Add(name);
        }

        return names;
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
