using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SqlFlow.Core;
using SqlFlow.Core.Compute;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Execution;

namespace SqlFlow.Delivery.Engine.Operations;

/// <summary>
/// The ad-hoc operations a node runs near the target on the control plane's behalf: what the GUI's buttons do
/// when they need OSDU rather than the ledger. A task names the flow (<c>sourceRef</c>) whose target and
/// credentials it uses and carries the flow file's location (<c>repoRoot</c> + <c>relativePath</c> as the
/// catalog knows them, or an absolute <c>flowFile</c>); the node resolves every credential itself, exactly as it
/// does for a run. The document must declare the named flow, so a stale catalog can never aim an operation at
/// the wrong target. A flow that declares interfaces is acted on through the one the task names (<c>interface</c>); the
/// others are never touched.
/// </summary>
public abstract class DeliveryOperation : IComputeOperation
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly EngineContext _context;

    protected DeliveryOperation(EngineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    public abstract string Name { get; }

    protected EngineContext Context => _context;

    public async Task<string> ExecuteAsync(ComputeTaskPayload payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var flowFile = ResolveFlowFile(payload);
        var source = _context.Documents.LoadSource(flowFile);
        if (!string.Equals(source.Name, payload.SourceRef, StringComparison.OrdinalIgnoreCase))
        {
            throw new SqlFlowException($"The flow file declares '{source.Name}', not '{payload.SourceRef}'. The file and the catalog have drifted; re-sync the repository.");
        }

        var flow = source.Interface(payload.Argument("interface"));

        var result = await RunAsync(flow, payload, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    protected abstract Task<object> RunAsync(FlowDefinition flow, ComputeTaskPayload payload, CancellationToken ct);

    /// <summary>The flow's protocol over a fresh HTTP runtime; the caller disposes the runtime.</summary>
    protected async Task<(HttpRuntime Http, IDeliveryProtocol Protocol)> OpenTargetAsync(FlowDefinition flow, CancellationToken ct)
    {
        var http = new HttpRuntime(flow.Reliability, _context.Secrets, _context.Time, allowLoopback: EngineContext.LoopbackAllowed);
        try
        {
            var protocol = await _context.Protocols.CreateAsync(flow, http, _context.Loggers, ct).ConfigureAwait(false);
            return (http, protocol);
        }
        catch
        {
            http.Dispose();
            throw;
        }
    }

    protected ILedger RequireLedger()
        => _context.Ledger ?? throw new SqlFlowException($"The '{Name}' operation needs the ledger, which lives in the module's database this node was started without (Osdu:Database:Connection or SQLFLOW_OSDU_DB).");

    protected static string Actor(ComputeTaskPayload payload)
        => payload.Argument("actor") ?? "unknown";

    /// <summary>
    /// The flow parameter values the task carries in <c>values</c> (a JSON object of strings), which fill the record scope's
    /// predicate when the operation reads the ingestion tables; none when it carries none.
    /// </summary>
    protected static IReadOnlyDictionary<string, string> Values(ComputeTaskPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Argument("values") is not { } json)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            throw new SqlFlowException($"The task's 'values' argument is not a JSON object of strings: {ex.Message}", ex);
        }
    }

    private static string ResolveFlowFile(ComputeTaskPayload payload)
    {
        var file = payload.Argument("flowFile");
        if (file is null)
        {
            var root = payload.RequireArgument("repoRoot");
            var relative = payload.RequireArgument("relativePath");
            file = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        }

        file = Path.GetFullPath(file);
        if (!File.Exists(file))
        {
            throw new SqlFlowException($"The flow file for '{payload.SourceRef}' was not found on this node: {file}");
        }

        return file;
    }
}

/// <summary>
/// <c>delivery-probe</c>: is the target reachable with the flow's credentials? Calls the service's info endpoint
/// and reports the answer; a refusal is a result, not a failure.
/// </summary>
public sealed class ProbeTargetOperation : DeliveryOperation
{
    public const string OperationName = "delivery-probe";

    public ProbeTargetOperation(EngineContext context)
        : base(context)
    {
    }

    public override string Name => OperationName;

    protected override async Task<object> RunAsync(FlowDefinition flow, ComputeTaskPayload payload, CancellationToken ct)
    {
        var (http, protocol) = await OpenTargetAsync(flow, ct).ConfigureAwait(false);
        using (http)
        {
            var probe = await protocol.ProbeAsync(ct).ConfigureAwait(false);
            return new
            {
                flow = flow.Label,
                protocol = protocol.Kind.ToString(),
                endpoint = flow.Target.Endpoint,
                auth = flow.Target.Auth.Type.ToString(),
                probe.Reachable,
                probe.Status,
                probe.Detail,
                probe.Path,
                checkedUtc = Context.Time.GetUtcNow().UtcDateTime,
            };
        }
    }
}

/// <summary>
/// <c>delivery-read</c>: the record as OSDU holds it right now, for a record's detail page, with the versions the
/// target keeps of it. Takes the ledger's <c>deliveryKey</c> (resolved to the target id) or a <c>targetId</c> directly,
/// and a <c>version</c> to read the record as it was at that version instead of its latest.
/// </summary>
public sealed class ReadRecordOperation : DeliveryOperation
{
    public const string OperationName = "delivery-read";

    public ReadRecordOperation(EngineContext context)
        : base(context)
    {
    }

    public override string Name => OperationName;

    protected override async Task<object> RunAsync(FlowDefinition flow, ComputeTaskPayload payload, CancellationToken ct)
    {
        var targetId = payload.Argument("targetId");
        long? version = null;
        if (payload.Argument("version") is { } versionText)
        {
            if (!long.TryParse(versionText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
            {
                throw new SqlFlowException($"'{versionText}' is not a record version: a positive whole number.");
            }

            version = parsed;
        }

        Guid? deliveryKey = null;
        IReadOnlyDictionary<string, string>? targetState = null;
        if (targetId is null)
        {
            var key = DeliveryKey.Parse(payload.RequireArgument("deliveryKey"));
            deliveryKey = key.Value;
            var record = await RequireLedger().GetRecordAsync(flow.Id, key, ct).ConfigureAwait(false)
                ?? throw new SqlFlowException($"Record {key} is not in the ledger for flow '{flow.Label}'.");
            // What a record's page reads back is what this flow wrote: an id the record never claimed can be another flow's.
            targetId = record.ClaimedTargetId
                ?? throw new SqlFlowException($"Record {key} has not queued a document for OSDU in flow '{flow.Label}', so this flow wrote nothing to read back.");
            targetState = JsonMerge.ToValues(record.TargetStateJson);
        }
        else if (Context.Ledger is { } ledger)
        {
            // A target that keeps a record under a key it gave (a DSPDM row) is read by what the record's deliveries recorded.
            var matches = await ledger.ListAsync(flow.Id, new RecordQuery { Search = targetId, Max = 2 }, ct).ConfigureAwait(false);
            if (matches.FirstOrDefault(r => string.Equals(r.TargetId, targetId, StringComparison.Ordinal)) is { } record)
            {
                targetState = JsonMerge.ToValues(record.TargetStateJson);
            }
        }

        var (http, protocol) = await OpenTargetAsync(flow, ct).ConfigureAwait(false);
        using var correlation = Http.OsduCorrelation.Begin();
        using (http)
        {
            JsonObject? document = version is null
                ? await protocol.ReadAsync(targetId, targetState, ct).ConfigureAwait(false)
                : await protocol.ReadVersionAsync(targetId, version.Value, ct).ConfigureAwait(false);

            // The version list is the record's history, read beside the record. A target that refuses the list still
            // answered with the record, so the refusal is reported with it rather than failing the read.
            IReadOnlyList<long>? versions = null;
            string? historyError = null;
            try
            {
                versions = await protocol.VersionsAsync(targetId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DeliveryException or HttpRequestException && !ct.IsCancellationRequested)
            {
                historyError = Http.HeaderRedaction.RedactMessage(ex.Message);
            }

            return new
            {
                flow = flow.Label,
                deliveryKey,
                targetId,
                correlationId = correlation.Id,
                found = document is not null,
                version = document is null ? null : RecordWriter.ParseVersion(Json.JsonPathReader.SelectValue(JsonSerializer.SerializeToElement(document), "version")),
                readVersion = version,
                versions,
                historyError,
                record = document,
                readUtc = Context.Time.GetUtcNow().UtcDateTime,
            };
        }
    }
}

/// <summary>
/// <c>delivery-delete</c>: removes records from OSDU through the flow's protocol and records what happened to each
/// one in the ledger under the requesting <c>actor</c>. <c>scope</c> says how much goes (<c>record</c> reversibly,
/// <c>history</c> for the earlier versions only, <c>everything</c> for the record and all its versions), and the
/// records are named either by <c>deliveryKeys</c> (a comma-separated list, one key for the single-record case) or
/// by <c>filter</c> (the listing whose every match is to be removed), resolved here against the ledger.
/// </summary>
public sealed class DeleteRecordOperation : DeliveryOperation
{
    public const string OperationName = "delivery-delete";

    public DeleteRecordOperation(EngineContext context)
        : base(context)
    {
    }

    public override string Name => OperationName;

    protected override async Task<object> RunAsync(FlowDefinition flow, ComputeTaskPayload payload, CancellationToken ct)
    {
        var scope = RemovalScopes.Parse(payload.Argument("scope"));
        var selection = ReadSelection(payload);
        RequireLedger();
        using var runtime = FlowRuntime.ForTarget(Context, flow);
        runtime.Actor = Actor(payload);
        var summary = await runtime.RemoveAsync(selection, scope, ct).ConfigureAwait(false);
        return new
        {
            flow = flow.Label,
            scope = RemovalScopes.Wire(scope),
            summary.Selected,
            summary.Removed,
            summary.AlreadyGone,
            summary.Skipped,
            summary.Failed,
            summary.Truncated,
            records = summary.Records,
            summary = summary.Describe(),
            actor = runtime.Actor,
            removedUtc = Context.Time.GetUtcNow().UtcDateTime,
        };
    }

    /// <summary>The keys the caller listed, or the listing filter it asked to have emptied. Exactly one of the two.</summary>
    private static RemovalSelection ReadSelection(ComputeTaskPayload payload)
    {
        var listed = payload.Argument("deliveryKeys");
        var filter = payload.Argument("filter");
        if (!string.IsNullOrWhiteSpace(listed))
        {
            var keys = listed
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(DeliveryKey.Parse)
                .ToList();
            return RemovalSelection.Of(keys);
        }

        if (string.IsNullOrWhiteSpace(filter))
        {
            throw new SqlFlowException("A removal needs either 'deliveryKeys' or 'filter'; the task carries neither.");
        }

        return RemovalSelection.Of(RemovalFilter.FromJson(filter));
    }
}

/// <summary>The removal scope on the wire: the lower-case names the API, the task payload and the GUI all use.</summary>
public static class RemovalScopes
{
    public static string Wire(RemovalScope scope) => scope switch
    {
        RemovalScope.Record => "record",
        RemovalScope.History => "history",
        RemovalScope.Everything => "everything",
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };

    /// <summary>Parses a wire scope. Null is not a default: a removal must say how much it takes.</summary>
    public static RemovalScope Parse(string? wire) => wire switch
    {
        "record" => RemovalScope.Record,
        "history" => RemovalScope.History,
        "everything" => RemovalScope.Everything,
        _ => throw new SqlFlowException($"'{wire ?? "(none)"}' is not a removal scope; use record, history or everything."),
    };

    public static bool TryParse(string? wire, out RemovalScope scope)
    {
        switch (wire)
        {
            case "record": scope = RemovalScope.Record; return true;
            case "history": scope = RemovalScope.History; return true;
            case "everything": scope = RemovalScope.Everything; return true;
            default: scope = RemovalScope.Record; return false;
        }
    }
}

/// <summary>
/// The listing filter of a removal, carried through the task payload as JSON. It is the same filter the records
/// list is built from, so what an operator selected with "every record matching this" is what the node resolves.
/// </summary>
public sealed class RemovalFilter
{
    public string? Status { get; set; }

    public string? Search { get; set; }

    public string? Mode { get; set; }

    public Guid? SubmissionId { get; set; }

    /// <summary>The submission whose delivered records the removal is aimed at: what the batch put into OSDU.</summary>
    public Guid? DeliveredBySubmissionId { get; set; }

    public Guid? RunId { get; set; }

    public bool Drifted { get; set; }

    public static RecordQuery FromJson(string json)
    {
        var filter = JsonSerializer.Deserialize<RemovalFilter>(json, DeliveryOperation.JsonOptions)
            ?? throw new SqlFlowException("The removal's filter is not a JSON object.");
        RecordStatus? status = null;
        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            status = Enum.TryParse<RecordStatus>(filter.Status, ignoreCase: true, out var parsed)
                ? parsed
                : throw new SqlFlowException($"'{filter.Status}' is not a record status.");
        }

        return new RecordQuery
        {
            Status = status,
            Search = string.IsNullOrWhiteSpace(filter.Search) ? null : filter.Search.Trim(),
            Mode = string.Equals(filter.Mode, "contains", StringComparison.OrdinalIgnoreCase) ? SearchMode.Contains : SearchMode.Prefix,
            SubmissionId = filter.SubmissionId,
            DeliveredBySubmissionId = filter.DeliveredBySubmissionId,
            RunId = filter.RunId,
            Drifted = filter.Drifted,
        };
    }

    public static string ToJson(RecordQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return JsonSerializer.Serialize(
            new RemovalFilter
            {
                Status = query.Status?.ToString().ToLowerInvariant(),
                Search = query.Search,
                Mode = query.Mode == SearchMode.Contains ? "contains" : "prefix",
                SubmissionId = query.SubmissionId,
                DeliveredBySubmissionId = query.DeliveredBySubmissionId,
                RunId = query.RunId,
                Drifted = query.Drifted,
            },
            DeliveryOperation.JsonOptions);
    }
}
