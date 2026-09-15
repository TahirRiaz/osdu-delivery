namespace SqlFlow.Delivery.Identity;

/// <summary>
/// A stable id derived deterministically from the flow name (SQLFlow pattern, design.md section 12.1), so runs key
/// consistently without a database assigning one.
/// </summary>
public static class FlowId
{
    private static readonly Guid FlowNamespace = DeterministicGuid.Namespace("flow");

    public static Guid Of(string flowName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        return DeterministicGuid.V5(FlowNamespace, flowName.Trim().ToLowerInvariant());
    }
}
