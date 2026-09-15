using SqlFlow.Core;

namespace SqlFlow.Delivery;

/// <summary>
/// Base exception for every failure the delivery domain raises deliberately, with an actionable message. It is a
/// <see cref="SqlFlowException"/>, so the platform's run write-back, redaction and error surfaces treat it exactly
/// like any other engine failure; document failures use the platform's <see cref="FlowValidationException"/> and
/// non-retryable HTTP statuses its <see cref="OsduStatusException"/>.
/// </summary>
public class DeliveryException : SqlFlowException
{
    public DeliveryException(string message)
        : base(message)
    {
    }

    public DeliveryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A record cannot be delivered as it stands and must not be retried without intervention: the terminal
/// <c>held</c> state (design.md section 7.4).
/// </summary>
public sealed class RecordHeldException : DeliveryException
{
    public RecordHeldException(string message)
        : base(message)
    {
    }

    public RecordHeldException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
