using SqlFlow.Core.Secrets;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Catalog.Modules;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Hosting;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Where the module's rows are written when the repository sync reconciles them, against real SQL Server databases.
/// An estate can keep SQLFlow's catalog and the OSDU Delivery module in one database or in two, and on Azure SQL two
/// means no cross-database statement and possibly two servers, so the two shapes are not one code path with a setting:
/// in one database the rows are part of the sync's own transaction, and in two they are written and committed on the
/// module's connection. Both are proven here, over the sample estate, with the catalog database watched for anything
/// the module might have written into it.
/// <para>These tests need a reachable SQL Server whose login may create a database: SQLFlow's catalog is the suites'
/// scratch database (<see cref="OsduScratchDatabase"/>), created for each test and dropped when it ends, and a module of
/// its own is the suites' test database (<see cref="OsduTestServer"/>). A server that refuses to create the scratch
/// database fails the tests.</para>
/// </summary>
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryModuleDatabaseTests
{
    private static readonly Guid Repo = Guid.NewGuid();

    private static readonly DateTime Now = new(2026, 9, 18, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>The mapping documents the sample estate holds, which is what a reconciliation of it writes.</summary>
    private static readonly string[] SampleMappings =
        ["Document@1.0.0", "WellLog@1.4.0", "Wellbore@1.0.0", "WellboreTrajectory@1.3.0"];

    /// <summary>The partition the sample cache flow declares its types for.</summary>
    private const string SampleScope = "dev";

    /// <summary>
    /// The sample estate names its partition as the reference a node holds, so this assembly supplies it before any test
    /// reads a sample document, and never over a value the process was started with.
    /// </summary>
    [ModuleInitializer]
    internal static void UseSampleEstateReferences()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OSDU_DATA_PARTITION")))
        {
            Environment.SetEnvironmentVariable("OSDU_DATA_PARTITION", SampleScope);
        }
    }

    [Fact]
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

    [Theory]
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

    [Fact]
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
    /// The databases a test runs against: SQLFlow's catalog in the suites' scratch database, created empty for the test and
    /// dropped when it ends, and the OSDU Delivery module either beside it in the same database or in a database of its
    /// own, which is the suites' test database with the module's schema emptied. The module's schema is migrated exactly
    /// as a host migrates it, into a database provisioned first, which is what a deployment does.
    /// </summary>
    private sealed class TestEstate : IAsyncDisposable
    {
        private readonly OsduScratchDatabase _catalog;
        private readonly OsduTestDatabase? _module;
        private readonly string? _moduleConnectionString;

        private TestEstate(OsduScratchDatabase catalog, OsduTestDatabase? module, string? moduleConnectionString)
        {
            _catalog = catalog;
            _module = module;
            _moduleConnectionString = moduleConnectionString;
        }

        /// <summary>SQLFlow's catalog database.</summary>
        public string CatalogConnectionString => _catalog.ConnectionString;

        /// <summary>
        /// Two databases, or one holding both. With <paramref name="declareModule"/> false the module is given no connection
        /// of its own, which is how a host that keeps the <c>osdu</c> schema in the catalog's database is configured.
        /// </summary>
        public static async Task<TestEstate> CreateAsync(bool separateDatabases, bool declareModule = true)
        {
            OsduScratchDatabase catalog;
            try
            {
                catalog = await OsduScratchDatabase.CreateAsync();
            }
            catch (SqlException ex)
            {
                throw new InvalidOperationException(
                    "These tests prove where the module's rows land by keeping SQLFlow's catalog in a database of its own, the suites' "
                    + $"scratch database, which they create beside the test database and drop again. This server refused: {ex.Message}",
                    ex);
            }

            OsduTestDatabase? module = null;
            try
            {
                module = separateDatabases ? new OsduTestDatabase() : null;
                var moduleConnectionString = module?.ConnectionString ?? (declareModule ? catalog.ConnectionString : null);

                // The module's schema, migrated into the database that is to hold it, as 'sqlflow db migrate' does.
                await ModuleDatabases.MigrateAsync(
                    OsduModuleDatabase.Create(),
                    module?.ConnectionString ?? catalog.ConnectionString,
                    allowCreate: false,
                    appliedBy: "module database tests",
                    appliedUtc: Now,
                    catalogAppliedMigrations: CatalogDatabase.KnownMigrations);
                return new TestEstate(catalog, module, moduleConnectionString);
            }
            catch
            {
                // Whatever was taken before the failure is still this test's to give back.
                module?.Dispose();
                await catalog.DisposeAsync();
                throw;
            }
        }

        /// <summary>
        /// The sync as a host composes it: the delivery loader, the module database when the host has one, and the
        /// resolver that turns a document's ${env:...} into the partition a cache is keyed by, which is what a capture and
        /// a render both key it under.
        /// </summary>
        public DeliveryCatalogSync Sync()
            => new(
                new DeliveryDocumentLoader(),
                _moduleConnectionString is null ? null : new ModuleContexts(_moduleConnectionString),
                new SecretResolver([new EnvSecretProvider()]));

        /// <summary>A context on the database the module's rows are in, for reading what a sync left.</summary>
        public OsduDbContext ModuleContext()
            => new(OsduDbContext.SqlServerOptions(_moduleConnectionString ?? CatalogConnectionString));

        public async ValueTask DisposeAsync()
        {
            try
            {
                // The scratch database clears its own connection pool and evicts any session left on it before it drops.
                await _catalog.DisposeAsync();
            }
            finally
            {
                _module?.Dispose();
            }
        }
    }

    /// <summary>Contexts on one connection string, as a host's module database registration hands them to the module.</summary>
    private sealed class ModuleContexts(string connectionString) : IDbContextFactory<OsduDbContext>
    {
        public OsduDbContext CreateDbContext() => new(OsduDbContext.SqlServerOptions(connectionString));
    }
}
