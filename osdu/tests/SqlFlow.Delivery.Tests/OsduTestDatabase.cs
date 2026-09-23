using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The SQL Server the module's suites run on. Development and testing expect a local one: without
/// <c>SQLFLOW_TEST_DB</c> the suites use <see cref="LocalDefault"/>, the local default instance under Windows
/// authentication; with it, the server and database it names (CI starts a server of its own and names one there). A
/// database that is missing is created and allowed snapshot transactions, as the ledger's reads need; one that exists is
/// never reconfigured, and one that does not allow them fails the suite naming the statement that does. A server that
/// does not answer fails the suite with a message naming it, never skips it: the module runs on SQL Server in
/// production, and a suite that skipped its database would prove nothing about it.
/// </summary>
/// <remarks>
/// The database must be disposable. The suites migrate and seed it, and create the per-test databases of
/// <see cref="OsduTestDatabases"/> beside it on the same server.
/// </remarks>
public static class OsduTestServer
{
    /// <summary>The environment variable that points the suites at a database other than the local default.</summary>
    public const string Variable = "SQLFLOW_TEST_DB";

    /// <summary>The database the suites use when <see cref="Variable"/> names none: the local default instance.</summary>
    public const string LocalDefault = "Server=localhost;Database=OsduDeliveryTests;Integrated Security=True;TrustServerCertificate=True";

    /// <summary>The SQL Server error a <c>CREATE DATABASE</c> raises when another process created it first.</summary>
    private const int DatabaseExists = 1801;

    private static readonly Lazy<string> Prepared = new(Prepare, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The test database's connection string, the database created and ready the first time it is asked for.</summary>
    public static string ConnectionString => Prepared.Value;

    /// <summary>
    /// The test database, for a suite that shares it (the control plane's API suites, the SQL Server ledger suites), each
    /// test under ids of its own. Fails, naming the server, when the server does not answer.
    /// </summary>
    public static string Require() => ConnectionString;

    /// <summary>A connection string to <paramref name="database"/> on the test server, under the same credentials.</summary>
    public static string ForDatabase(string database)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        return new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = database }.ConnectionString;
    }

    /// <summary>
    /// Creates <paramref name="database"/> on the server <paramref name="server"/> names when it is missing, and lets a
    /// database the suites own run snapshot transactions: one this call created, or any when <paramref name="owned"/> says
    /// the suites made it (the per-test databases). Another process creating it at the same moment is not an error.
    /// Returns whether the database allows snapshot transactions now.
    /// </summary>
    internal static bool EnsureDatabase(SqlConnectionStringBuilder server, string database, bool owned)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        using var connection = new SqlConnection(new SqlConnectionStringBuilder(server.ConnectionString) { InitialCatalog = "master" }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("@name", database);
        // EXEC takes a string built in a variable; it does not evaluate a function call in its argument.
        command.CommandText = """
            DECLARE @create nvarchar(300) = N'CREATE DATABASE ' + QUOTENAME(@name);
            IF DB_ID(@name) IS NULL BEGIN EXEC (@create); SELECT CAST(1 AS bit); END ELSE SELECT CAST(0 AS bit);
            """;
        bool created;
        try
        {
            created = (bool)command.ExecuteScalar()!;
        }
        catch (SqlException ex) when (ex.Number == DatabaseExists)
        {
            // Created by another suite between the check and the statement: the database is there, and it is that suite's
            // to configure.
            created = false;
        }

        if (created || owned)
        {
            command.CommandText = """
                DECLARE @allow nvarchar(300) = N'ALTER DATABASE ' + QUOTENAME(@name) + N' SET ALLOW_SNAPSHOT_ISOLATION ON';
                IF EXISTS (SELECT 1 FROM sys.databases WHERE name = @name AND snapshot_isolation_state = 0) EXEC (@allow);
                """;
            command.ExecuteNonQuery();
        }

        command.CommandText = "SELECT CAST(CASE WHEN snapshot_isolation_state = 1 THEN 1 ELSE 0 END AS bit) FROM sys.databases WHERE name = @name;";
        return (bool)command.ExecuteScalar()!;
    }

    private static string Prepare()
    {
        var configured = Environment.GetEnvironmentVariable(Variable);
        var server = new SqlConnectionStringBuilder(string.IsNullOrWhiteSpace(configured) ? LocalDefault : configured);
        if (string.IsNullOrWhiteSpace(server.InitialCatalog))
        {
            throw new InvalidOperationException($"{Variable} names server '{server.DataSource}' but no database; name a disposable one with Database=.");
        }

        bool snapshot;
        try
        {
            snapshot = EnsureDatabase(server, server.InitialCatalog, owned: false);
        }
        catch (SqlException ex)
        {
            throw new InvalidOperationException(
                $"The module's suites run on SQL Server and expect a local one for development and testing, and server '{server.DataSource}' "
                + $"(database '{server.InitialCatalog}', from {(string.IsNullOrWhiteSpace(configured) ? "the local default" : Variable)}) could not be reached or "
                + $"prepared: {ex.Message} Start a local SQL Server the Windows login can create databases on, or set {Variable} to a disposable database on "
                + "another server.",
                ex);
        }

        if (!snapshot)
        {
            throw new InvalidOperationException(
                $"The ledger reads under snapshot isolation, and database '{server.InitialCatalog}' on server '{server.DataSource}' does not allow it. "
                + $"The suites never reconfigure a database they were lent; enable it once with ALTER DATABASE [{server.InitialCatalog}] SET ALLOW_SNAPSHOT_ISOLATION ON.");
        }

        return server.ConnectionString;
    }
}

/// <summary>
/// The databases the ledger, store and engine suites run on, one test at a time each: <c>{test database}_osdu_{nn}</c>
/// beside the test database, created and brought to the module's latest migration the first time a process takes it,
/// and emptied of every row of the <c>osdu</c> schema each time a test takes it, so every test starts from a database
/// that holds nothing, exactly as a new deployment does.
/// </summary>
/// <remarks>
/// A test holds its database through an exclusive session lock taken on the test database, released when the test
/// disposes it, so two suites running at once in separate processes never share one. The pool grows to as many
/// databases as tests run at once, up to <see cref="MaxDatabases"/>, and they stay on the server for the next run.
/// </remarks>
internal static class OsduTestDatabases
{
    /// <summary>The most databases the pool keeps; tests beyond it at once are a runaway, not a workload.</summary>
    private const int MaxDatabases = 64;

    private static readonly ConcurrentDictionary<string, Lazy<bool>> Migrated = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Takes the first free database of the pool, creating and migrating it when it is new, and empties it.</summary>
    public static Lease Take()
    {
        var test = new SqlConnectionStringBuilder(OsduTestServer.ConnectionString);

        // The locks live in the test database's sessions. Pooling is off so that closing the connection ends its session,
        // and with it every lock it still holds, whatever happened to the test.
        var locks = new SqlConnection(new SqlConnectionStringBuilder(test.ConnectionString) { Pooling = false }.ConnectionString);
        try
        {
            locks.Open();
            for (var n = 0; n < MaxDatabases; n++)
            {
                var name = $"{test.InitialCatalog}_osdu_{n:00}";
                if (!TryLock(locks, name))
                {
                    continue;
                }

                var connectionString = OsduTestServer.ForDatabase(name);
                _ = Migrated.GetOrAdd(name, _ => new Lazy<bool>(() => Migrate(test, name, connectionString), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
                Empty(connectionString);
                return new Lease(locks, name, connectionString);
            }
        }
        catch
        {
            locks.Dispose();
            throw;
        }

        locks.Dispose();
        throw new InvalidOperationException($"All {MaxDatabases} of the suites' databases are held by running tests; a test is not disposing the one it took.");
    }

    private static bool TryLock(SqlConnection locks, string name)
    {
        using var command = locks.CreateCommand();
        command.CommandText = """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 0;
            SELECT @result;
            """;
        command.Parameters.AddWithValue("@resource", ResourceOf(name));
        return (int)command.ExecuteScalar()! >= 0;
    }

    private static string ResourceOf(string name) => "osdu-test-database/" + name;

    private static bool Migrate(SqlConnectionStringBuilder test, string name, string connectionString)
    {
        OsduTestServer.EnsureDatabase(test, name, owned: true);
        using var db = new OsduDbContext(OsduDbContext.SqlServerOptions(connectionString));
        db.Database.Migrate();
        return true;
    }

    /// <summary>
    /// Empties every table of the module's schema but its migration history. The schema has no foreign keys and no
    /// views, so each table is truncated, which also starts its identity column from its seed again.
    /// </summary>
    private static void Empty(string connectionString)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("@schema", DeliveryModel.SchemaName);
        command.Parameters.AddWithValue("@history", OsduDbContext.MigrationsHistoryTable);
        command.CommandText = """
            DECLARE @sql nvarchar(max) = N'';
            SELECT @sql = @sql + N'TRUNCATE TABLE ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N';'
            FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = @schema AND t.name <> @history;
            EXEC sys.sp_executesql @sql;
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>One test's hold on one database of the pool.</summary>
    internal sealed class Lease : IDisposable
    {
        private readonly SqlConnection _locks;
        private bool _disposed;

        internal Lease(SqlConnection locks, string name, string connectionString)
        {
            _locks = locks;
            Name = name;
            ConnectionString = connectionString;
        }

        public string Name { get; }

        public string ConnectionString { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // Closing the unpooled connection ends its session, which releases the lock however the test ended.
            _locks.Dispose();
        }
    }
}

/// <summary>
/// The module's own database (schema <c>osdu</c>) on SQL Server, empty when a test takes it, for the suites that run the
/// ledger, the template and cache stores and the engine: the real <see cref="OsduLedger"/> over the real model and the
/// schema its migrations build, on the provider production runs on. Each instance holds one database of
/// <see cref="OsduTestDatabases"/> until it is disposed.
/// </summary>
public sealed class OsduTestDatabase : IDisposable
{
    private static readonly ConcurrentDictionary<string, Lazy<bool>> CatalogMigrated = new(StringComparer.OrdinalIgnoreCase);

    private readonly OsduTestDatabases.Lease _lease;
    private readonly DbContextOptions<OsduDbContext> _options;

    public OsduTestDatabase()
    {
        _lease = OsduTestDatabases.Take();
        _options = OsduDbContext.SqlServerOptions(_lease.ConnectionString);
    }

    /// <summary>The database's connection string, for a suite that reaches it as a host would.</summary>
    public string ConnectionString => _lease.ConnectionString;

    public OsduDbContext CreateDbContext() => new(_options);

    public OsduLedger Ledger(TimeProvider? time = null) => new(CreateDbContext, time);

    public OsduTemplateStore Templates(TimeProvider? time = null) => new(CreateDbContext, time);

    public OsduCacheStore Caches() => new(CreateDbContext);

    /// <summary>
    /// A context over SQLFlow's catalog in the same database, for the seams the module shares with the platform (the
    /// catalog sync extension, the search contributor). The catalog is migrated there the first time a process asks. Only
    /// the module's schema is emptied between tests, so a test writing catalog rows writes them under ids of its own and
    /// removes them when it is done.
    /// </summary>
    public CatalogDbContext CreateCatalogContext()
    {
        var options = CatalogDatabase.BuildOptions(ConnectionString);
        _ = CatalogMigrated.GetOrAdd(_lease.Name, _ => new Lazy<bool>(
            () =>
            {
                using var db = new CatalogDbContext(options);
                db.Database.Migrate();
                return true;
            },
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        return new CatalogDbContext(options);
    }

    /// <summary>
    /// Declares what <paramref name="flowName"/> caches for <paramref name="scope"/> exactly as the repository sync leaves it:
    /// one <c>osdu.CacheDefinition</c> row per type, replacing every row the flow had. No types declares nothing for the flow.
    /// </summary>
    public async Task DeclareCacheAsync(string scope, string flowName, params ReferenceTypeSpec[] types)
    {
        ArgumentNullException.ThrowIfNull(types);
        await using var db = CreateDbContext();
        await db.DeliveryCacheDefinitions.Where(d => d.FlowName == flowName).ExecuteDeleteAsync();
        var repoId = Guid.NewGuid();
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var type in types)
        {
            db.DeliveryCacheDefinitions.Add(new DeliveryCacheDefinition
            {
                Id = Guid.NewGuid(),
                RepoId = repoId,
                FlowName = flowName,
                Scope = scope,
                Origin = CacheOrigins.Text(type.Origin),
                Endpoint = type.Origin == CacheOrigin.Osdu ? "https://osdu.example.test" : null,
                Connection = type.Origin == CacheOrigin.Table ? "${env:INGESTION_DB}" : null,
                SourceObject = type.Table,
                KeyField = type.Key,
                DictionaryPath = type.DictionaryPath,
                RelativePath = "cache/" + flowName + ".yaml",
                Name = type.Name,
                EntityType = type.EntityType,
                Kind = type.Kind,
                Query = type.Origin == CacheOrigin.Osdu ? type.Query : null,
                FieldsJson = new JsonArray(type.Fields.Select(f => (JsonNode)new JsonObject { ["path"] = f.Path, ["as"] = f.Name }).ToArray()).ToJsonString(),
                OnChange = type.OnChange == CacheChangeMode.Approve ? "approve" : "auto",
                FirstSeenUtc = now,
                LastSeenUtc = now,
            });
        }

        await db.SaveChangesAsync();
    }

    public void Dispose() => _lease.Dispose();
}
