using System.Text.Json;
using SqlFlow.Core.Compute;
using SqlFlow.Delivery.Engine.Preview;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine.Operations;

/// <summary>
/// <c>delivery-preview</c>: one record of the flow rendered as a delivery would render it, and nothing sent
/// (<see cref="RecordPreviewer"/>), for the flow's Preview tab and a record page's comparison with what OSDU holds. The
/// node reads the ingestion tables with the flow's own connection reference, renders with the pinned template and the
/// partition's cache, and asks the platform's search only what the mapping's searches ask a run. The task names the row by
/// <c>key</c> (a delivery key, an OSDU id the ledger holds, a source key or a JSON array of the key's parts), or names none
/// for the scope's first renderable row; <c>values</c> are the flow parameter values the scope is read with. Nothing is
/// written: not the ledger, not the work location, not OSDU.
/// </summary>
public sealed class PreviewRecordOperation : DeliveryOperation
{
    public const string OperationName = "delivery-preview";

    /// <summary>The most characters the answer carries, under the 8,000,000 a node task's result may hold.</summary>
    public const int MaxResultChars = 7_000_000;

    public PreviewRecordOperation(EngineContext context)
        : base(context)
    {
    }

    public override string Name => OperationName;

    protected override async Task<object> RunAsync(FlowDefinition flow, ComputeTaskPayload payload, CancellationToken ct)
    {
        using var runtime = await FlowRuntime.CreateAsync(flow.Interface is null ? Context : Context.ForInterface(flow.Interface), flow, Values(payload), ct).ConfigureAwait(false);
        var preview = await new RecordPreviewer(runtime).PreviewAsync(payload.Argument("key"), ct).ConfigureAwait(false);

        // Measured as the answer is written, escapes and all, so what fits here fits the task result.
        return RecordPreviewer.Fit(preview, MaxResultChars, p => JsonSerializer.Serialize(p, JsonOptions).Length);
    }
}
