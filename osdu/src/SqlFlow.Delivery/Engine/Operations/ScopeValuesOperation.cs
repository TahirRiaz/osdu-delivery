using SqlFlow.Core.Compute;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Source;

namespace SqlFlow.Delivery.Engine.Operations;

/// <summary>
/// <c>delivery-scope-values</c>: the values each parameter of a flow's scope predicate can take (<see cref="IIngestionSource.ScopeValuesAsync"/>),
/// for the pages that ask for a scope's values (a flow's Preview tab, a mapping's value check) to offer them rather than
/// have them typed. Each parameter's values are the distinct values the column <c>source.record.scope</c> binds it to holds
/// in the flow's own record table, read with the flow's own connection reference, so a source scoping by any column of any
/// table answers the same way and no value list is kept anywhere to go stale. Nothing is written.
/// </summary>
public sealed class ScopeValuesOperation : DeliveryOperation
{
    public const string OperationName = "delivery-scope-values";

    /// <summary>The most values listed for one parameter; a column holding more says so.</summary>
    public const int MaxValues = 500;

    public ScopeValuesOperation(EngineContext context)
        : base(context)
    {
    }

    public override string Name => OperationName;

    protected override async Task<object> RunAsync(EngineContext context, FlowDefinition flow, ComputeTaskPayload payload, CancellationToken ct)
    {
        // The listing reads no row of a scope, so it needs no parameter value: it is what a value is picked from.
        var source = context.Sources.Open(flow, new Dictionary<string, string>(StringComparer.Ordinal), context.Loggers);
        var parameters = await source.ScopeValuesAsync(MaxValues, ct).ConfigureAwait(false);
        return new ScopeValuesAnswer(flow.Label, flow.Interface, flow.Source.Record.Object, parameters, context.Time.GetUtcNow().UtcDateTime);
    }
}

/// <summary>What <see cref="ScopeValuesOperation"/> answers: the flow, its record table, and the values of each scope parameter.</summary>
public sealed record ScopeValuesAnswer(string Flow, string? Interface, string RecordObject, IReadOnlyList<ScopeParameterValues> Parameters, DateTime ReadUtc);
