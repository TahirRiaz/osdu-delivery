using System.Globalization;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;

namespace SqlFlow.Delivery.Engine;

/// <summary>Why a run reads what it reads of its scope (<see cref="ScopeReads"/>).</summary>
public enum ScopeReadReason
{
    /// <summary>No ledger keeps a watermark, so the whole scope is read.</summary>
    NoLedger,

    /// <summary>No whole-scope plan of the scope has completed yet, so the whole scope is read.</summary>
    NoWatermark,

    /// <summary>The mapping, template or parameters moved since the watermark was written, so the whole scope is read.</summary>
    RulesMoved,

    /// <summary>The rows changed since the watermark, less the flow's overlap.</summary>
    SinceWatermark,
}

/// <summary>What a run reads of its scope, and why: the scope, the selection, the watermark it went by, and the reason.</summary>
/// <param name="Scope">The scope's key (<see cref="Planner.ScopeKey"/>).</param>
/// <param name="Selection">The rows read: the whole scope, or the rows changed after a moment.</param>
/// <param name="Watermark">The scope's watermark, when the ledger holds one.</param>
/// <param name="Reason">Why the selection is what it is.</param>
public sealed record ScopeRead(string Scope, SourceSelection Selection, SourceWatermark? Watermark, ScopeReadReason Reason)
{
    /// <summary>The reason in words, for a check that reports what the next run would read.</summary>
    public string Why(FlowDefinition flow, RenderContext rendering)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(rendering);
        return Reason switch
        {
            ScopeReadReason.NoLedger => "no ledger keeps a watermark here, so a run reads every row in scope",
            ScopeReadReason.NoWatermark => $"no whole-scope plan of scope {Scope} has completed yet, so the next run reads every row in scope",
            ScopeReadReason.RulesMoved =>
                $"scope {Scope} was last planned with other rules than {rendering.MappingReference} renders with now (its mapping, template or parameters changed), so the next run reads every row in scope",
            _ => string.Create(
                CultureInfo.InvariantCulture,
                $"scope {Scope} was planned through {Watermark!.UpdatedThroughUtc:o}; the next run reads the rows changed after {Selection.LowerUtc:o} (an overlap of {flow.Source.Incremental.OverlapSeconds}s)"),
        };
    }
}

/// <summary>
/// What a run of a delivery flow reads of its scope when nothing narrows it (no submission, no named records, no replan):
/// the rows changed since the scope's watermark, less the flow's overlap; or the whole scope when no ledger keeps a
/// watermark, when no whole-scope plan of the scope has completed yet, or when the rules it renders with (the mapping,
/// the template, the parameters) moved since the watermark was written, since rows that did not change still render
/// differently then and nothing else would reach them. One decision, for a run and for <c>sqlflow check --connect</c>,
/// which reports what the next run would read.
/// </summary>
public static class ScopeReads
{
    /// <summary>What a run of <paramref name="flow"/> with <paramref name="values"/>, rendering with <paramref name="rendering"/>, reads of its scope.</summary>
    public static async Task<ScopeRead> DecideAsync(
        ILedger? ledger, FlowDefinition flow, IReadOnlyDictionary<string, string> values, RenderContext rendering, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(rendering);
        var scope = Planner.ScopeKey(values);
        if (ledger is null)
        {
            return new ScopeRead(scope, SourceSelection.Full(), null, ScopeReadReason.NoLedger);
        }

        var watermark = await ledger.GetWatermarkAsync(flow.Id, scope, ct).ConfigureAwait(false);
        if (watermark is null)
        {
            return new ScopeRead(scope, SourceSelection.Full(), null, ScopeReadReason.NoWatermark);
        }

        if (watermark.ContextHash is { } planned && !string.Equals(planned, rendering.RulesHash(), StringComparison.Ordinal))
        {
            return new ScopeRead(scope, SourceSelection.Full(), watermark, ScopeReadReason.RulesMoved);
        }

        // The overlap looks a little below the watermark again, for rows whose statement committed after the last read
        // fixed its upper bound: their update time is inside the window that has already been read.
        var lower = watermark.UpdatedThroughUtc.AddSeconds(-flow.Source.Incremental.OverlapSeconds);
        return new ScopeRead(scope, SourceSelection.Incremental(lower), watermark, ScopeReadReason.SinceWatermark);
    }
}
