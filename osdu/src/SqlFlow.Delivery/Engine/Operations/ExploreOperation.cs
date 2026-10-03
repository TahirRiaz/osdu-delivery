using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Core.Compute;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Operations;

/// <summary>
/// <c>delivery-explore</c>: the explorer's reads of what an OSDU partition holds, through the connection (endpoint, credentials
/// and partition) of the flow the control plane picked for that partition, and nothing else of the flow: no ledger, mapping
/// or cache is read. The task's <c>action</c> says which read:
/// <list type="bullet">
/// <item><description><c>types</c>: the kinds of the records a search finds, with their counts (<c>search</c>).</description></item>
/// <item><description><c>search</c>: one page of the records a search finds (<c>search</c>).</description></item>
/// <item><description><c>fields</c>: the properties the records of a <c>kind</c> hold, read from one of them.</description></item>
/// <item><description><c>read</c>: one record from the storage service by its <c>targetId</c>, at its latest or at a <c>version</c>, with its version list.</description></item>
/// </list>
/// Every read goes to the platform's own services (search and storage, openapi v2), whatever route the flow delivers by.
/// </summary>
public sealed class ExploreOperation : DeliveryOperation
{
    public const string OperationName = "delivery-explore";

    /// <summary>The task argument naming the read: <see cref="TypesAction"/>, <see cref="SearchAction"/>, <see cref="FieldsAction"/> or <see cref="ReadAction"/>.</summary>
    public const string ActionArgument = "action";

    /// <summary>The task argument carrying the search, as <see cref="ExplorerSearch.ToJson"/> writes it.</summary>
    public const string SearchArgument = "search";

    /// <summary>The task argument naming the kind whose properties are read.</summary>
    public const string KindArgument = "kind";

    public const string TypesAction = "types";
    public const string SearchAction = "search";
    public const string FieldsAction = "fields";
    public const string ReadAction = "read";

    /// <summary>Every read the task can name.</summary>
    public static readonly IReadOnlyList<string> Actions = [TypesAction, SearchAction, FieldsAction, ReadAction];

    private readonly HttpMessageHandler? _transport;
    private readonly bool _allowLoopback;

    public ExploreOperation(EngineContext context)
        : this(context, null, EngineContext.LoopbackAllowed)
    {
    }

    /// <summary>The operation sending through <paramref name="transport"/> instead of the network (the tests).</summary>
    internal ExploreOperation(EngineContext context, HttpMessageHandler? transport, bool allowLoopback)
        : base(context)
    {
        _transport = transport;
        _allowLoopback = allowLoopback;
    }

    public override string Name => OperationName;

    protected override async Task<object> RunAsync(EngineContext context, FlowDefinition flow, ComputeTaskPayload payload, CancellationToken ct)
    {
        var action = payload.RequireArgument(ActionArgument);
        if (!Actions.Contains(action, StringComparer.Ordinal))
        {
            throw new SqlFlowException($"'{action}' is not a read the explorer makes: {string.Join(", ", Actions)}.");
        }

        using var http = new HttpRuntime(flow.Reliability, context.Secrets, context.Time, _transport, _allowLoopback);
        var client = await ProtocolFactory.ClientAsync(http, flow.Target.Endpoint, flow.Target.Auth, flow.Target.Headers, context.Secrets, ct).ConfigureAwait(false);
        if (action == ReadAction)
        {
            // The storage service's own read, whatever the flow delivers by: the explorer shows the record OSDU keeps.
            var storage = new OsduRecordProtocol(client, new ProtocolOptions { VerifyPath = ReadPathOf(flow) }, context.Time);
            var targetId = TargetId.WithoutVersion(payload.RequireArgument("targetId").Trim());
            return await ReadRecordOperation.ReadBackAsync(storage, flow.Label, targetId, null, ReadRecordOperation.VersionOf(payload), null, context.Time, ct).ConfigureAwait(false);
        }

        var partition = await PartitionOfAsync(context, flow, ct).ConfigureAwait(false);
        var explorer = new RecordExplorer(client, partition, context.Loggers.CreateLogger<RecordExplorer>());
        using var correlation = OsduCorrelation.Begin();
        object answer = action switch
        {
            TypesAction => await explorer.TypesAsync(ExplorerSearch.Parse(payload.Argument(SearchArgument)), ct).ConfigureAwait(false),
            SearchAction => await explorer.SearchAsync(ExplorerSearch.Parse(payload.Argument(SearchArgument)), ct).ConfigureAwait(false),
            _ => await explorer.FieldsAsync(payload.RequireArgument(KindArgument), ct).ConfigureAwait(false),
        };

        return new
        {
            connection = flow.Label,
            partition,
            correlationId = correlation.Id,
            answeredUtc = context.Time.GetUtcNow().UtcDateTime,
            answer,
        };
    }

    /// <summary>
    /// The storage service's record path the explorer reads by: the one a storage flow points its reads at, which follows a
    /// deployment that serves storage elsewhere, and the platform's own for a flow of any other route, whose read path (where
    /// it names one) is its DDMS's.
    /// </summary>
    internal static string ReadPathOf(FlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return flow.Target.Protocol == DeliveryProtocol.Storage && flow.Target.ProtocolOptions.VerifyPath is { Length: > 0 } path
            ? path
            : OsduRecordProtocol.DefaultVerifyPath;
    }

    /// <summary>
    /// The data partition the flow's connection reads: the one it is bound to, or what its partition header resolves to. A
    /// connection whose partition cannot be told still reads; only an id written without its partition then stays as typed.
    /// </summary>
    private static async Task<string?> PartitionOfAsync(EngineContext context, FlowDefinition flow, CancellationToken ct)
    {
        try
        {
            return await LedgerRegistration.PartitionAsync(flow, context.Secrets, ct).ConfigureAwait(false);
        }
        catch (DeliveryException)
        {
            return null;
        }
    }
}
