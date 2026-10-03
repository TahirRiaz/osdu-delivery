using System.Text.Json;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Core.Compute;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Search;

namespace SqlFlow.Delivery.Engine.Operations;

/// <summary>
/// <c>delivery-explore</c>: the explorer's reads of what an OSDU partition holds, through the connection (endpoint, credentials
/// and partition) of the flow the control plane picked for that partition, and nothing else of the flow: no ledger, mapping
/// or cache is read. The payload's <c>action</c> says which read:
/// <list type="bullet">
/// <item><description><c>types</c>: the kinds of the records a search finds, with their counts (<c>search</c>).</description></item>
/// <item><description><c>search</c>: one page of the records a search finds (<c>search</c>).</description></item>
/// <item><description><c>fields</c>: the properties the records of a <c>kind</c> hold, read from one of them.</description></item>
/// <item><description><c>read</c>: one record from the storage service by its <c>targetId</c>, at its latest or at a <c>version</c>, with its version list.</description></item>
/// <item><description><c>dimension-sample</c>: what the dimension builder shows of the records a dimension reads (<see cref="DimensionSampler.SampleAsync"/>), asked by the <c>sample</c> long argument.</description></item>
/// <item><description><c>dimension-example</c>: one key of a drafted dimension made into its row as a build makes it (<see cref="DimensionSampler.ExampleAsync"/>): the dimension's item as YAML (<c>item</c>), the <c>key</c>, and how the key and each collected path are indexed (<c>fields</c>).</description></item>
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

    /// <summary>The long argument (<see cref="LongArgument"/>) carrying a <see cref="DimensionSampleRequest"/> as JSON.</summary>
    public const string SampleArgument = "sample";

    /// <summary>The long argument carrying a drafted dimension's item, as <see cref="DimensionBuilder.ToYaml"/> writes it.</summary>
    public const string ItemArgument = "item";

    /// <summary>The task argument carrying the key a dimension example is made of.</summary>
    public const string KeyArgument = "key";

    /// <summary>The long argument carrying how a dimension example's key and collected paths are indexed, as <see cref="DimensionExampleFields"/> JSON.</summary>
    public const string FieldsArgument = "fields";

    public const string TypesAction = "types";
    public const string SearchAction = "search";
    public const string FieldsAction = "fields";
    public const string ReadAction = "read";
    public const string DimensionSampleAction = "dimension-sample";
    public const string DimensionExampleAction = "dimension-example";

    /// <summary>Every read the task can name.</summary>
    public static readonly IReadOnlyList<string> Actions = [TypesAction, SearchAction, FieldsAction, ReadAction, DimensionSampleAction, DimensionExampleAction];

    /// <summary>How the dimension builder's requests and answers are written: the web's conventions, as the API writes them.</summary>
    public static JsonSerializerOptions BuilderJson { get; } = ReadOnly(new JsonSerializerOptions(JsonSerializerDefaults.Web));

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

        // The connection the engine keeps for the flow's target, so a person browsing is not given a new token and new
        // connections for every page; a transport of the tests' own is a connection of its own.
        using var target = _transport is null && context.Clients is { } clients
            ? await clients.OpenAsync(context, flow, ct).ConfigureAwait(false)
            : TargetClients.OneOff(context, flow, _transport, _allowLoopback);
        var client = await target.ClientAsync(ct).ConfigureAwait(false);
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
            DimensionSampleAction => await Sampler(client, context).SampleAsync(SampleOf(payload, partition), ct).ConfigureAwait(false),
            DimensionExampleAction => await ExampleAsync(context, Sampler(client, context), payload, partition, ct).ConfigureAwait(false),
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

    private static DimensionSampler Sampler(OsduHttpClient client, EngineContext context)
    {
        var log = context.Loggers.CreateLogger<DimensionSampler>();
        return new DimensionSampler(new OsduSearch(client, RecordExplorer.QueryPath, RetrievalSource.DefaultSearchPath, log), log);
    }

    /// <summary>
    /// The builder's sample request, checked again here as the control plane checked it, its query's <c>{partition}</c> filled
    /// with the partition the connection reads, as a build fills it.
    /// </summary>
    internal static DimensionSampleRequest SampleOf(ComputeTaskPayload payload, string? partition)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var request = Deserialize<DimensionSampleRequest>(LongArgument.Require(payload, SampleArgument), SampleArgument);
        if (ExplorerKinds.Problem(request.Kind) is { } wrong)
        {
            throw new SqlFlowException(wrong);
        }

        if (request.At is < 0 or >= DimensionSampler.ExampleRecords)
        {
            throw new SqlFlowException($"A sample shows one of the first {DimensionSampler.ExampleRecords} records, from 0; {request.At} is not one.");
        }

        if (request.Trails.Count > DimensionSampler.MaxTrails || request.Trails.Any(t => t is null || t.Count > DimensionSampler.MaxTrailSteps))
        {
            throw new SqlFlowException($"A sample follows at most {DimensionSampler.MaxTrails} trails, each of at most {DimensionSampler.MaxTrailSteps} paths.");
        }

        foreach (var step in request.Trails.SelectMany(t => t))
        {
            if (DimensionPath.Parse(step).Problem is { } problem)
            {
                throw new SqlFlowException($"'{step}' is not a path a trail can follow: {problem}.");
            }
        }

        return request with { Query = Filled(request.Query, partition) };
    }

    /// <summary>
    /// One key of the drafted dimension the payload carries made into its row: the item read back by the document loader, as a
    /// flow's own would be, so what the builder shows is what a build of that YAML would make.
    /// </summary>
    private static async Task<DimensionExample> ExampleAsync(
        EngineContext context, DimensionSampler sampler, ComputeTaskPayload payload, string? partition, CancellationToken ct)
    {
        var item = LongArgument.Require(payload, ItemArgument);
        var key = payload.RequireArgument(KeyArgument);
        var fields = Deserialize<DimensionExampleFields>(LongArgument.Read(payload, FieldsArgument) ?? "{}", FieldsArgument);
        var dimension = context.Documents.ParseDimension(DimensionBuilder.Document(item), DimensionBuilder.CheckSource).Dimensions[0];
        var collected = new Dictionary<string, OsduField>(StringComparer.Ordinal);
        foreach (var (name, wire) in fields.Collected ?? new Dictionary<string, DimensionFieldWire>(StringComparer.Ordinal))
        {
            if (wire.Field() is { } field)
            {
                collected[name] = field;
            }
        }

        DimensionCleaner cleaner;
        try
        {
            // A map step reads a dictionary of the flow's repository, which a drafted dimension has none of.
            cleaner = DimensionCleaner.Build(dimension.Clean, name => throw new DeliveryException(
                $"The clean step map reads the dictionary {name} of the flow's repository, which the builder cannot read, so the example is not cleaned by it."));
        }
        catch (DeliveryException ex)
        {
            throw new SqlFlowException(ex.Message, ex);
        }

        return await sampler.ExampleAsync(dimension, key, Filled(dimension.Query, partition), fields.Key?.Field(), collected, cleaner, ct).ConfigureAwait(false);
    }

    /// <summary>A query with its <c>{partition}</c> token filled with the partition the connection reads; any other token stays as written.</summary>
    private static string? Filled(string? query, string? partition)
        => query is null || partition is null
            ? query
            : FlowParameters.Substitute(query, new Dictionary<string, string>(StringComparer.Ordinal) { [PartitionNames.RunValue] = partition });

    private static JsonSerializerOptions ReadOnly(JsonSerializerOptions options)
    {
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private static T Deserialize<T>(string json, string argument)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, BuilderJson) ?? throw new SqlFlowException($"The '{argument}' argument is empty.");
        }
        catch (JsonException ex)
        {
            throw new SqlFlowException($"The '{argument}' argument is not what the dimension builder sends: {ex.Message}", ex);
        }
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

/// <summary>How a drafted dimension's key and each of its collected paths are indexed, as the control plane settled them from the saved templates.</summary>
public sealed record DimensionExampleFields(DimensionFieldWire? Key, IReadOnlyDictionary<string, DimensionFieldWire>? Collected);
