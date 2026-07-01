namespace SqlFlow.Core.Ingestion;

/// <summary>One assertion definition (the legacy flw.Assertion row): a name and a SQL expression template that
/// may contain the <c>@TableName</c> and <c>@FilterCriteria</c> macros.</summary>
public sealed record AssertionDefinition
{
    public required string Name { get; init; }

    public required string Expression { get; init; }
}

/// <summary>
/// Resolves assertion names to their definitions (the legacy flw.Assertion registry). A name with no
/// definition is omitted from the result, mirroring the legacy INNER JOIN drop. In without-database mode there
/// is no registry, so no assertion runner is wired and this is never consulted.
/// </summary>
public interface IAssertionDefinitionStore
{
    Task<IReadOnlyDictionary<string, AssertionDefinition>> ResolveAsync(IEnumerable<string> names, CancellationToken ct = default);
}
