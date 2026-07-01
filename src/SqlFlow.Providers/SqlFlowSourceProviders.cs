using SqlFlow.Core.Connections;
using SqlFlow.Providers.MySql;
using SqlFlow.Providers.Postgres;

namespace SqlFlow.Providers;

/// <summary>
/// The MySQL and PostgreSQL provider bundle. A composition root (the CLI, a host builder, a test) appends
/// this to the built-in SQL Server set so non-SQL-Server SOURCES resolve, open, introspect, and read with
/// their own canonicalizer, connection factory, catalog reader, SQL dialect, and type mapper. The target side
/// of every flow remains SQL Server by design.
/// </summary>
public static class SqlFlowSourceProviders
{
    public static SourceProviderRegistry CreateRegistry()
        => new()
        {
            Canonicalizers = [new MySqlConnectionStringCanonicalizer(), new PostgresConnectionStringCanonicalizer()],
            ConnectionFactories = [new MySqlProviderConnectionFactory(), new PostgresProviderConnectionFactory()],
            CatalogReaders = [new MySqlCatalogReader(), new PostgresCatalogReader()],
            Dialects = [new MySqlSourceDialect(), new PostgresSourceDialect()],
            TypeMappers = [new MySqlSourceTypeMapper(), new PostgresSourceTypeMapper()],
        };
}
