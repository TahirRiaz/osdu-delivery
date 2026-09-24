using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Every test that uses the suites' SQL Server database. There is one test database (<see cref="OsduTestServer"/>), and a
/// test that takes it empties the module's schema, so these tests run one at a time: the collection has parallelization
/// disabled, which xUnit schedules after the parallel collections and runs on its own, and its fixture holds the database
/// against every other test process for as long as the collection runs. A test class outside it that reaches the database
/// fails, naming this collection.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqlServerSuite : ICollectionFixture<OsduTestDatabaseFixture>
{
    public const string Name = "SQL Server";
}
