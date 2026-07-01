using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Model;

namespace SqlFlow.Execution;

/// <summary>Streams engine events to the console as they happen (skips low-level Trace events). The engine wiring
/// registers it as the default <see cref="IFlowEventSink"/>; a host with no console (the control plane, a worker)
/// overrides the registration with its own sink.</summary>
public sealed class ConsoleFlowEventSink : IFlowEventSink
{
    public void Publish(FlowEvent flowEvent)
    {
        if (flowEvent.Level == FlowEventLevel.Trace)
        {
            return;
        }

        var marker = flowEvent.Level switch
        {
            FlowEventLevel.Error => "x ",
            FlowEventLevel.Warning => "! ",
            _ => ". ",
        };

        // Prefix with flow name + short run id so concurrent runs stay attributable in one stream.
        var tag = flowEvent.FlowName is { } name
            ? $"[{name}#{flowEvent.RunId.ToString("N")[..8]}] "
            : string.Empty;

        Console.WriteLine($"  {tag}{marker}{flowEvent.Message}");
    }
}
