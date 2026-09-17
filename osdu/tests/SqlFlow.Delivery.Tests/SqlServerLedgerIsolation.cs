using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The SQL Server ledger tests that watch the test database as a whole: how often it locked a whole ledger table rather
/// than rows. Every other SQL Server class writes the same tables while the classes run in parallel, and a lock one of
/// them holds decides whether an escalation happens at all, so this collection runs with parallelization disabled, which
/// xUnit schedules after the parallel collections.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqlServerLedgerIsolation
{
    public const string Name = "SQL Server ledger, run apart";
}
