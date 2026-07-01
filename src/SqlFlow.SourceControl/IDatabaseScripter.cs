using SqlFlow.Core.SourceControl;

namespace SqlFlow.SourceControl;

/// <summary>Scripts a database's objects to an in-memory snapshot. Abstracted so the orchestration service can
/// be exercised without a live SQL Server, while <see cref="SmoDatabaseScripter"/> is the production engine.</summary>
public interface IDatabaseScripter
{
    ScriptedDatabase Script(string connectionString, string? database, SourceControlScripting scripting, CancellationToken ct = default);
}
