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
/// the wrong target.
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
        var flow = _context.Documents.LoadFlow(flowFile);
        if (!string.Equals(flow.Name, payload.SourceRef, StringComparison.OrdinalIgnoreCase))
        {
            throw new SqlFlowException($"The flow file declares '{flow.Name}', not '{payload.SourceRef}'. The file and the catalog have drifted; re-sync the repository.");
        }

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
            var protocol = await _context.Protocols.CreateAsync(flow, http, ct).ConfigureAwait(false);
            return (http, protocol);
        }
        catch
        {
            http.Dispose();
            throw;
        }
    }

    protected ILedger RequireLedger()
        => _context.Ledger ?? throw new SqlFlowException($"The '{Name}' operation needs the ledger, which lives in the catalog database this node was started without.");

    protected static string Actor(ComputeTaskPayload payload)
        => payload.Argument("actor") ?? "unknown";

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
                flow = flow.Name,
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
/// <c>delivery-read</c>: the record as OSDU holds it right now, for a record's detail page. Takes the ledger's
/// <c>deliveryKey</c> (resolved to the target id) or a <c>targetId</c> directly.
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
        Guid? deliveryKey = null;
        if (targetId is null)
        {
            var key = DeliveryKey.Parse(payload.RequireArgument("deliveryKey"));
            deliveryKey = key.Value;
            var record = await RequireLedger().GetRecordAsync(flow.Id, key, ct).ConfigureAwait(false)
                ?? throw new SqlFlowException($"Record {key} is not in the ledger for flow '{flow.Name}'.");
            targetId = record.TargetId ?? throw new SqlFlowException($"Record {key} has no OSDU id yet; nothing to read back.");
        }

        var (http, protocol) = await OpenTargetAsync(flow, ct).ConfigureAwait(false);
        using (http)
        {
            JsonObject? document = await protocol.ReadAsync(targetId, ct).ConfigureAwait(false);
            return new
            {
                flow = flow.Name,
                deliveryKey,
                targetId,
                found = document is not null,
                version = document is null ? null : RecordWriter.ParseVersion(Json.JsonPathReader.SelectValue(JsonSerializer.SerializeToElement(document), "version")),
                record = document,
                readUtc = Context.Time.GetUtcNow().UtcDateTime,
            };
        }
    }
}

/// <summary>
/// <c>delivery-delete</c>: removes a record from OSDU through the flow's protocol (a logical delete, or a purge
/// with <c>purge=true</c>) and records the removal in the ledger under the requesting <c>actor</c>. The record
/// stays blocked from redelivery until its source changes or an operator releases it.
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
        var key = DeliveryKey.Parse(payload.RequireArgument("deliveryKey"));
        var purge = string.Equals(payload.Argument("purge"), "true", StringComparison.OrdinalIgnoreCase);
        RequireLedger();
        using var runtime = FlowRuntime.ForTarget(Context, flow);
        runtime.Actor = Actor(payload);
        var outcome = await runtime.DeleteAsync(key, purge, ct).ConfigureAwait(false);
        return new
        {
            flow = flow.Name,
            deliveryKey = key.Value,
            purge,
            outcome.Deleted,
            outcome.AlreadyGone,
            outcome.Detail,
            actor = runtime.Actor,
            deletedUtc = Context.Time.GetUtcNow().UtcDateTime,
        };
    }
}
