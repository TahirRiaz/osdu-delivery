namespace SqlFlow.Core.Ingestion;

/// <summary>
/// Runs the flow's data-quality assertions against the loaded target. Like the run log, this is a two-mode
/// seam: WITHOUT-database mode uses <see cref="NullAssertionRunner"/> (no registry, runs nothing); WITH-database
/// mode injects a SQL-backed runner over an <see cref="IAssertionDefinitionStore"/>. Log-only by default: an
/// assertion outcome never fails or rolls back the load.
/// </summary>
public interface IAssertionRunner
{
    Task<IReadOnlyList<AssertionResult>> RunAsync(IngestionFlow flow, string targetConnectionString, CancellationToken ct = default);
}

/// <summary>The without-database default: evaluates no assertions.</summary>
public sealed class NullAssertionRunner : IAssertionRunner
{
    public static readonly NullAssertionRunner Instance = new();

    public Task<IReadOnlyList<AssertionResult>> RunAsync(IngestionFlow flow, string targetConnectionString, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AssertionResult>>([]);
}
