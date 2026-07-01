using SqlFlow.Core.Connections;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Secrets;
using SqlFlow.SqlServer;
using SqlFlow.SqlServer.Catalog;
using SqlFlow.SqlServer.Ingestion;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Shared wiring for relational ingestion integration tests: a runner backed by the real sink, plus staging
/// bookkeeping helpers. The store maps any alias to the sink, authenticated by a whole secret reference so the
/// secretless gate trusts the resolved (SQL-auth) connection string.
/// </summary>
internal static class RelationalIngestionHarness
{
    public static IngestionFlowRunner BuildRunner()
    {
        var resolver = new ConnectionResolver(
            new SinkStore(),
            new SecretResolver([new EnvSecretProvider()]),
            [new SqlConnectionStringCanonicalizer()]);
        return new IngestionFlowRunner(resolver, new SqlConnectionFactory(), CatalogFactory());
    }

    /// <summary>A runner in with-database mode: the run log persists to flw.SysLog/SysStats on the given
    /// connection.</summary>
    public static IngestionFlowRunner BuildRunnerWithRunLog(string logConnectionString)
    {
        var resolver = new ConnectionResolver(
            new SinkStore(),
            new SecretResolver([new EnvSecretProvider()]),
            [new SqlConnectionStringCanonicalizer()]);
        return new IngestionFlowRunner(
            resolver,
            new SqlConnectionFactory(),
            CatalogFactory(),
            desiredIndexes: null,
            runLog: new SqlIngestionRunLog(logConnectionString));
    }

    /// <summary>A runner in with-database mode for data-quality assertions.</summary>
    public static IngestionFlowRunner BuildRunnerWithAssertions(IAssertionRunner assertions)
    {
        var resolver = new ConnectionResolver(
            new SinkStore(),
            new SecretResolver([new EnvSecretProvider()]),
            [new SqlConnectionStringCanonicalizer()]);
        return new IngestionFlowRunner(
            resolver,
            new SqlConnectionFactory(),
            CatalogFactory(),
            desiredIndexes: null,
            runLog: null,
            assertions: assertions);
    }

    private static CompositeCatalogReaderFactory CatalogFactory()
        => new([new SqlServerCatalogReader()]);

    /// <summary>A connection resolver whose store maps any alias to the sink.</summary>
    public static IConnectionResolver BuildResolver()
        => new ConnectionResolver(
            new SinkStore(),
            new SecretResolver([new EnvSecretProvider()]),
            [new SqlConnectionStringCanonicalizer()]);

    public static Task<int?> StagingCountAsync(string cs, int flowId)
        => IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM sys.tables WHERE name LIKE 'stg[_]{flowId}[_]%'");

    public static Task DropStagingAsync(string cs, int flowId)
        => IntegrationDb.ExecuteAsync(cs,
            $"DECLARE @sql nvarchar(max) = N''; " +
            $"SELECT @sql += 'DROP TABLE [dbo].[' + name + '];' FROM sys.tables WHERE name LIKE 'stg[_]{flowId}[_]%'; " +
            "IF LEN(@sql) > 0 EXEC sys.sp_executesql @sql;");

    private sealed class SinkStore : IDataSourceStore
    {
        public bool SupportsAliases => true;

        public Task<DataSource> ResolveAsync(string aliasName, CancellationToken ct = default)
            => Task.FromResult(new DataSource
            {
                Alias = aliasName,
                Kind = DataSourceKind.MSSQL,
                ConnectionRef = "${env:SQLFlowSinkConStr}",
                Credential = new CredentialProfile { Mode = CredentialMode.InlineConnectionString },
            });
    }
}
