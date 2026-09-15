using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// The integration tests that read the WHOLE sink database (a source-control snapshot scripts every table, view and
/// procedure in it) rather than only the objects they create. Every other integration test creates and drops its own
/// objects in that same database while test classes run in parallel, so a whole-database reader must not run
/// alongside them: parallelization is disabled for this collection, which xUnit runs after the parallel ones.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WholeDatabaseIntegrationGroup
{
    public const string Name = "Whole-database integration";
}
