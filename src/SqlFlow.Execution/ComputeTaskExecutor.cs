using SqlFlow.Core;
using SqlFlow.Core.Compute;

namespace SqlFlow.Execution;

/// <summary>
/// One ad-hoc operation a worker node can execute for a queued compute task. Registered by the host next to its
/// document kind, so the execution layer dispatches by name without knowing the engines.
/// </summary>
public interface IComputeOperation
{
    /// <summary>The operation name the payload carries (case-insensitive).</summary>
    string Name { get; }

    /// <summary>Executes the operation and returns its result as a JSON document (what the GUI reads back).</summary>
    Task<string> ExecuteAsync(ComputeTaskPayload payload, CancellationToken ct);
}

/// <summary>
/// The executor behind queued compute tasks: validates the payload, finds the registered operation, runs it, and
/// caps the result size so a runaway result never bloats the catalog row. A node runs this for every task it
/// claims, exactly as it runs the <see cref="DocumentExecutor"/> for every run it claims.
/// </summary>
public sealed class ComputeTaskExecutor
{
    /// <summary>The largest result stored on a task row; a larger one is truncated with a marker.</summary>
    public const int MaxResultBytes = 4 * 1024 * 1024;

    private readonly Dictionary<string, IComputeOperation> _operations;

    public ComputeTaskExecutor(IEnumerable<IComputeOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        _operations = new Dictionary<string, IComputeOperation>(StringComparer.OrdinalIgnoreCase);
        foreach (var operation in operations)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
            if (!_operations.TryAdd(operation.Name, operation))
            {
                throw new InvalidOperationException($"Two compute operations claim the name '{operation.Name}'.");
            }
        }
    }

    /// <summary>The operation names this host can execute, sorted.</summary>
    public IReadOnlyList<string> Operations => _operations.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

    public bool IsKnown(string? operation) => !string.IsNullOrWhiteSpace(operation) && _operations.ContainsKey(operation);

    public async Task<string> ExecuteAsync(ComputeTaskPayload payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payload);
        payload.Validate();
        if (!_operations.TryGetValue(payload.Operation, out var operation))
        {
            throw new SqlFlowException(
                $"Unknown compute operation '{payload.Operation}'. This host knows: {string.Join(", ", Operations)}.");
        }

        var result = await operation.ExecuteAsync(payload, ct).ConfigureAwait(false);
        if (result.Length <= MaxResultBytes)
        {
            return result;
        }

        // A truncated result is still valid JSON: the marker object replaces it, naming the cap, so the GUI can say
        // "too large" instead of failing to parse.
        return $"{{\"truncated\":true,\"maxBytes\":{MaxResultBytes},\"actualBytes\":{result.Length}}}";
    }
}
