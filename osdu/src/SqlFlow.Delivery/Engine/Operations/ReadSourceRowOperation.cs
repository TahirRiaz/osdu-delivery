using System.Text.Json;
using SqlFlow.Core;
using SqlFlow.Core.Compute;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Source;

namespace SqlFlow.Delivery.Engine.Operations;

/// <summary>
/// <c>delivery-source</c>: one record's rows as the ingestion tables hold them right now, for a record's detail page.
/// The node opens the flow's source with the flow's own connection reference and reads the record named by the ledger's
/// <c>deliveryKey</c> (whose stored key tuple says which row it is) or by <c>sourceKey</c>, a JSON array of the key
/// parts in key order. Nothing is written: this is the read that answers "what does the source say about this record
/// today" without planning or delivering anything.
/// </summary>
public sealed class ReadSourceRowOperation : DeliveryOperation
{
    public const string OperationName = "delivery-source";

    /// <summary>The most child rows of one dataset the answer carries; a record with more says so and is read in full by a run.</summary>
    public const int MaxChildRows = 500;

    public ReadSourceRowOperation(EngineContext context)
        : base(context)
    {
    }

    public override string Name => OperationName;

    protected override async Task<object> RunAsync(FlowDefinition flow, ComputeTaskPayload payload, CancellationToken ct)
    {
        var values = FlowParameters.Resolve(flow, ReadValues(payload));
        var key = await ResolveKeyAsync(flow, payload, ct).ConfigureAwait(false);
        var source = Context.Sources.Open(flow, values, Context.Loggers);
        var header = await source.OpenAsync(SourceSelection.ForKeys([key.Key]), null, ct).ConfigureAwait(false);
        if (header.MissingKeys.Count > 0)
        {
            return new
            {
                flow = flow.Label,
                deliveryKey = key.DeliveryKey,
                sourceKey = key.Key.Values,
                found = false,
                reason = $"the record table {flow.Source.Record.Object} holds no row with this key",
                readUtc = Context.Time.GetUtcNow().UtcDateTime,
            };
        }

        if (header.OutOfScopeKeys.Count > 0)
        {
            return new
            {
                flow = flow.Label,
                deliveryKey = key.DeliveryKey,
                sourceKey = key.Key.Values,
                found = false,
                reason = "the row is outside the scope these parameter values name; read it with the values of its own scope",
                readUtc = Context.Time.GetUtcNow().UtcDateTime,
            };
        }

        await foreach (var record in source.ReadAsync(header, null, ct).ConfigureAwait(false))
        {
            return new
            {
                flow = flow.Label,
                deliveryKey = key.DeliveryKey,
                sourceKey = key.Key.Values,
                found = true,
                record = Row(record.Row),
                datasets = record.Scopes.ToDictionary(
                    s => s.Key,
                    s => new
                    {
                        rows = s.Value.Take(MaxChildRows).Select(Row).ToList(),
                        total = s.Value.Count,
                        truncated = s.Value.Count > MaxChildRows,
                    }),
                origin = new { file = record.Origin.FileName, row = record.Origin.RowNumber, updatedUtc = record.Origin.UpdatedUtc },
                fingerprint = record.Version.Fingerprint,
                deletedUtc = record.DeletedUtc,
                hold = record.Hold,
                readUtc = Context.Time.GetUtcNow().UtcDateTime,
            };
        }

        return new
        {
            flow = flow.Label,
            deliveryKey = key.DeliveryKey,
            sourceKey = key.Key.Values,
            found = false,
            reason = "the row was there when the read opened and gone when it was read; it was removed while this operation ran",
            readUtc = Context.Time.GetUtcNow().UtcDateTime,
        };
    }

    private async Task<(KeyTuple Key, Guid? DeliveryKey)> ResolveKeyAsync(FlowDefinition flow, ComputeTaskPayload payload, CancellationToken ct)
    {
        if (payload.Argument("sourceKey") is { } json)
        {
            return (KeyTuple.FromJson(json), null);
        }

        var key = DeliveryKey.Parse(payload.RequireArgument("deliveryKey"));
        var record = await RequireLedger().GetRecordAsync(flow.Id, key, ct).ConfigureAwait(false)
            ?? throw new SqlFlowException($"Record {key} is not in the ledger for flow '{flow.Label}'.");
        var stored = record.SourceKeyJson
            ?? throw new SqlFlowException($"Record {key} carries no source key, so the row it came from cannot be looked up; deliver the flow once to record it.");
        return (KeyTuple.FromJson(stored), key.Value);
    }

    private static IReadOnlyDictionary<string, string> ReadValues(ComputeTaskPayload payload)
    {
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

    private static IReadOnlyDictionary<string, string?> Row(SourceRow row)
        => row.Columns.ToDictionary(c => c, row.GetString, StringComparer.Ordinal);
}
