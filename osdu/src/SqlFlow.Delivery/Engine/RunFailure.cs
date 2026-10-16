using System.Text.Json;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// How a delivery or retrieval run names what stopped it. Whatever went wrong, the run ends as a recorded failure with
/// its log and its artifact, never as a crashed process that leaves no run behind.
/// </summary>
public static class RunFailure
{
    /// <summary>The failures a run is expected to meet: validation, the target, the transport, the files, the ledger.</summary>
    public static bool IsExpected(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is SqlFlowException or HttpRequestException or IOException or InvalidOperationException or JsonException or UnauthorizedAccessException;
    }

    /// <summary>
    /// The redacted message chain. An unexpected exception is a defect whose message alone rarely says what happened
    /// (an index out of range, a null reference), so its type leads.
    /// </summary>
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var message = SecretHygiene.RedactedMessage(exception);
        return IsExpected(exception) ? message : $"{exception.GetType().Name}: {message}";
    }
}
