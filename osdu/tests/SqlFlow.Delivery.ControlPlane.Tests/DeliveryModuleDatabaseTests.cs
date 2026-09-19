using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Catalog.Modules;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Hosting;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Where the module's rows are written when the repository sync reconciles them, against real SQL Server databases.
/// An estate can keep SQLFlow's catalog and the OSDU Delivery module in one database or in two, and on Azure SQL two
/// means no cross-database statement and possibly two servers, so the two shapes are not one code path with a setting:
/// in one database the rows are part of the sync's own transaction, and in two they are written and committed on the
/// module's connection. Both are proven here, over the sample estate, with the catalog database watched for anything
/// the module might have written into it.
/// <para>These tests need a reachable SQL Server whose login may create a database: they create two of their own beside
/// the one <c>SQLFLOW_TEST_DB</c> names, and drop both when they end. They skip when there is no such server.</para>
/// </summary>
public sealed class DeliveryModuleDatabaseTests
{
    private static readonly Guid Repo = Guid.NewGuid();

    private static readonly DateTime Now = new(2026, 9, 18, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>The mapping documents the sample estate holds, which is what a reconciliation of it writes.</summary>
    private static readonly string[] SampleMappings =
        ["Document@1.0.0", "WellLog@1.4.0", "Wellbore@1.0.0", "WellboreTrajectory@1.3.0"];

    /// <summary>The partition the sample cache flow declares its types for.</summary>
    private const string SampleScope = "opendes";

    [SkippableFact]
    public async Task A_module_database_of_its_own_takes_the_rows_and_the_catalog_database_never_sees_them()
    {
        await using var estate = await TestEstate.CreateAsync(separateDatabases: true);
        using var repository = new SampleRepository();
        var warnings = new List<string>();

        var result = await SyncAsync(estate, repository.Root, warnings, commit: false);

        Assert.Empty(warnings);
        Assert.True(result.Added > 0, "the sample estate reconciles to rows");

        // The rows are the module database's, and they are there although the catalog's transaction rolled back: an
        // isolated module commits its own work, because nothing can enlist two Azure SQL databases in one transaction.
        await using (var module = estate.ModuleContext())
        {
            Assert.Equal(
                SampleMappings,
                await ReferencesAsync(module));
            Assert.NotEmpty(await module.DeliveryCacheDefinitions.Where(d => d.RepoId == Repo).ToListAsync());
            Assert.NotEmpty(await module.DeliveryInterfaces.Where(i => i.RepoId == Repo).ToListAsync());
        }

        // And read back through the module's own store, which is how the GUI and a run reach them.
        var declaration = await new OsduCacheStore(estate.ModuleContext).DeclarationAsync(SampleScope, CancellationToken.None);
        Assert.NotEmpty(declaration.TypeNames);

        // The catalog database is untouched: not a row, not a table, not even the module's schema. This is the whole
        // point of the shape, and it is also why the sync cannot have reached the module's rows through this connection.
        Assert.Equal(0, await ScalarAsync(estate.CatalogConnectionString, "SELECT COUNT(*) FROM sys.tables"));
        Assert.Equal(0, await ScalarAsync(estate.CatalogConnectionString, "SELECT COUNT(*) FROM sys.schemas WHERE name = 'osdu'"));
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task In_one_database_the_rows_commit_and_roll_back_with_the_sync(bool moduleDatabaseDeclared)
    {
        // Two ways to say the same estate: a deployment that points the module at the database the catalog is in, and
        // one that declares no module connection at all, which is the default and means the catalog's own database.
        await using var estate = await TestEstate.CreateAsync(separateDatabases: false, declareModule: moduleDatabaseDeclared);
        using var repository = new SampleRepository();

        var rolledBack = await SyncAsync(estate, repository.Root, new List<string>(), commit: false);
        Assert.True(rolledBack.Added > 0);
        await using (var module = estate.ModuleContext())
        {
            // The sync's transaction is what the rows were written under, so its rollback took them with it.
            Assert.Empty(await module.DeliveryMappings.Where(m => m.RepoId == Repo).ToListAsync());
            Assert.Empty(await module.DeliveryInterfaces.Where(i => i.RepoId == Repo).ToListAsync());
        }

        var committed = await SyncAsync(estate, repository.Root, new List<string>(), commit: true);
        Assert.Equal(rolledBack.Added, committed.Added);
        await using (var module = estate.ModuleContext())
        {
            Assert.Equal(
                SampleMappings,
                await ReferencesAsync(module));
        }
    }

    [SkippableFact]
    public async Task A_mapping_change_is_read_from_wherever_the_module_database_is()
    {
        await using var estate = await TestEstate.CreateAsync(separateDatabases: true);
        using var repository = new SampleRepository();
        await SyncAsync(estate, repository.Root, new List<string>(), commit: false);

        // The lineage check reads the mapping rows the sync wrote, which are a database and a connection away.
        Assert.False(await ChangedAsync(estate, repository.Root));

        var mapping = SampleEstate.MappingIn(repository.Root, "Wellbore@1.0.0");
        await File.AppendAllTextAsync(mapping, Environment.NewLine + "# a comment the last sync did not see" + Environment.NewLine);

        Assert.True(await ChangedAsync(estate, repository.Root));
    }

    /// <summary>
    /// One reconciliation, given exactly what the repository sync gives the extension: the catalog's context inside the
    /// catalog's transaction. <paramref name="commit"/> says what becomes of that transaction, which is what separates
    /// the rows that ride it from the rows an isolated module database committed on its own connection.
    /// </summary>
    private static async Task<CatalogSyncExtensionResult> SyncAsync(
        TestEstate estate, string root, ICollection<string> warnings, bool commit)
    {
        await using var catalog = CatalogDatabase.Create(estate.CatalogConnectionString);
        await using var transaction = await catalog.Database.BeginTransactionAsync();
        var result = await estate.Sync().SyncAsync(catalog, Repo, root, Now, warnings, CancellationToken.None);
        if (commit)
        {
            await transaction.CommitAsync();
        }
        else
        {
            await transaction.RollbackAsync();
        }

        return result;
    }

    private static async Task<bool> ChangedAsync(TestEstate estate, string root)
    {
        await using var catalog = CatalogDatabase.Create(estate.CatalogConnectionString);
        return await estate.Sync().LineageInputsChangedAsync(catalog, Repo, root, CancellationToken.None);
    }

    /// <summary>The mapping references a reconciliation left, ordered here rather than by the server's collation.</summary>
    private static async Task<string[]> ReferencesAsync(OsduDbContext module)
    {
        var references = await module.DeliveryMappings.Where(m => m.RepoId == Repo).Select(m => m.Reference).ToListAsync();
        references.Sort(StringComparer.Ordinal);
        return [.. references];
    }

    private static async Task<int> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The sample estate copied to a temp directory, and removed with the test.</summary>
    private sealed class SampleRepository : IDisposable
    {
        public SampleRepository()
        {
            Root = SampleEstate.CopyTo(Path.Combine(Path.GetTempPath(), "osdu_module_db_" + Guid.NewGuid().ToString("N")[..8]));
        }

        public string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    /// <summary>
    /// The databases a test runs against, created on the server <c>SQLFLOW_TEST_DB</c> names and dropped when the test
    /// ends: SQLFlow's catalog, and the OSDU Delivery module either beside it in the same database or in one of its own.
    /// The module's schema is migrated exactly as a host migrates it, into a database provisioned first, which is what
    /// a deployment does.
    /// </summary>
    private sealed class TestEstate : IAsyncDisposable
    {
        private static readonly Regex SafeName = new("^[A-Za-z][A-Za-z0-9_]{0,120}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        private readonly string _master;
        private readonly List<string> _created = [];
        private readonly string? _moduleConnectionString;

        private TestEstate(string master, string catalogConnectionString, string? moduleConnectionString)
        {
            _master = master;
            CatalogConnectionString = catalogConnectionString;
            _moduleConnectionString = moduleConnectionString;
        }

        /// <summary>SQLFlow's catalog database.</summary>
        public string CatalogConnectionString { get; }

        /// <summary>
        /// Two databases with the names an estate that separates them uses, or one holding both. With
        /// <paramref name="declareModule"/> false the module is given no connection of its own, which is how a host that
        /// keeps the <c>osdu</c> schema in the catalog's database is configured.
        /// </summary>
        public static async Task<TestEstate> CreateAsync(bool separateDatabases, bool declareModule = true)
        {
            var builder = new SqlConnectionStringBuilder(CatalogTestDb.Require());
            var master = new SqlConnectionStringBuilder(builder.ConnectionString) { InitialCatalog = "master" }.ConnectionString;
            var suffix = Guid.NewGuid().ToString("N")[..8];

            var estate = new TestEstate(
                master,
                Named(builder, "SQLFlow_Test_" + suffix),
                separateDatabases ? Named(builder, "OSDUDelivery_Test_" + suffix) : declareModule ? Named(builder, "SQLFlow_Test_" + suffix) : null);

            try
            {
                await estate.CreateDatabaseAsync("SQLFlow_Test_" + suffix);
                if (separateDatabases)
                {
                    await estate.CreateDatabaseAsync("OSDUDelivery_Test_" + suffix);
                }

                // The module's schema, migrated into the database that is to hold it, as 'sqlflow db migrate' does.
                await ModuleDatabases.MigrateAsync(
                    OsduModuleDatabase.Create(),
                    separateDatabases ? estate._moduleConnectionString! : estate.CatalogConnectionString,
                    allowCreate: false,
                    appliedBy: "module database tests",
                    appliedUtc: Now,
                    catalogAppliedMigrations: CatalogDatabase.KnownMigrations);
            }
            catch
            {
                // Whatever was created before the failure is still this test's to remove.
                await estate.DisposeAsync();
                throw;
            }

            return estate;
        }

        /// <summary>The sync as a host composes it: the delivery loader, and the module database when the host has one.</summary>
        public DeliveryCatalogSync Sync()
            => new(new DeliveryDocumentLoader(), _moduleConnectionString is null ? null : new ModuleContexts(_moduleConnectionString));

        /// <summary>A context on the database the module's rows are in, for reading what a sync left.</summary>
        public OsduDbContext ModuleContext()
            => new(OsduDbContext.SqlServerOptions(_moduleConnectionString ?? CatalogConnectionString));

        public async ValueTask DisposeAsync()
        {
            // The contexts above pool their connections, and a pooled connection keeps the database in use.
            SqlConnection.ClearAllPools();
            foreach (var name in _created)
            {
                await using var connection = new SqlConnection(_master);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"IF DB_ID('{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END";
                await command.ExecuteNonQueryAsync();
            }
        }

        private static string Named(SqlConnectionStringBuilder server, string database)
            => new SqlConnectionStringBuilder(server.ConnectionString) { InitialCatalog = database }.ConnectionString;

        private async Task CreateDatabaseAsync(string name)
        {
            if (!SafeName.IsMatch(name))
            {
                throw new InvalidOperationException($"'{name}' is not a database name this test may create.");
            }

            await using var connection = new SqlConnection(_master);
            try
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = $"CREATE DATABASE [{name}]";
                await command.ExecuteNonQueryAsync();
            }
            catch (SqlException ex)
            {
                throw new SkipException(
                    "These tests prove where the module's rows land by giving it a database of its own, so they create two databases "
                    + $"beside the one SQLFLOW_TEST_DB names and drop them again. This server refused: {ex.Message}");
            }

            _created.Add(name);
        }
    }

    /// <summary>Contexts on one connection string, as a host's module database registration hands them to the module.</summary>
    private sealed class ModuleContexts(string connectionString) : IDbContextFactory<OsduDbContext>
    {
        public OsduDbContext CreateDbContext() => new(OsduDbContext.SqlServerOptions(connectionString));
    }
}
