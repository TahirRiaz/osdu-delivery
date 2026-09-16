using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace SqlFlow.Delivery.Data;

/// <summary>
/// The OSDU module's database: every table and view of the delivery ledger in the <c>osdu</c> schema, with its own
/// migrations recorded in <c>[osdu].[__EFMigrationsHistory]</c> and its own version row
/// (<see cref="OsduSchemaVersion"/>). Nothing here references SQLFlow's catalog. On the control plane the context opens
/// on the catalog database's connection and enlists in its transaction, so a run queued with the rows that describe it
/// commits as one unit; a node opens it with a login that reaches only the <c>osdu</c> schema.
/// </summary>
public sealed class OsduDbContext : DbContext
{
    /// <summary>The name of the migrations history table, which lives in <see cref="DeliveryModel.SchemaName"/>.</summary>
    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    /// <summary>The module version the current migrations produce; written to <see cref="OsduSchemaVersion.ModuleVersion"/>.</summary>
    public const string ModuleVersion = "1.6.0";

    /// <summary>
    /// The oldest SQLFlow catalog migration this schema works with: the one that added fan-out run groups and run
    /// results, which the OSDU flow's intake and drain rely on beside the run kind arguments added before it.
    /// </summary>
    public const string MinimumCatalogMigration = "20260915212521_RunFanOutAndResult";

    public OsduDbContext(DbContextOptions<OsduDbContext> options)
        : base(options)
    {
    }

    public DbSet<DeliverySubmission> DeliverySubmissions => Set<DeliverySubmission>();

    public DbSet<DeliveryRecord> DeliveryRecords => Set<DeliveryRecord>();

    public DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();

    public DbSet<DeliveryRecordCount> DeliveryRecordCounts => Set<DeliveryRecordCount>();

    public DbSet<DeliveryWorkBatch> DeliveryWorkBatches => Set<DeliveryWorkBatch>();

    public DbSet<DeliveryLease> DeliveryLeases => Set<DeliveryLease>();

    public DbSet<DeliveryRecordEvent> DeliveryRecordEvents => Set<DeliveryRecordEvent>();

    public DbSet<DeliverySourceWatermark> DeliverySourceWatermarks => Set<DeliverySourceWatermark>();

    public DbSet<DeliveryActivity> DeliveryActivities => Set<DeliveryActivity>();

    public DbSet<DeliveryRetrieval> DeliveryRetrievals => Set<DeliveryRetrieval>();

    public DbSet<DeliveryMapping> DeliveryMappings => Set<DeliveryMapping>();

    public DbSet<DeliveryInterface> DeliveryInterfaces => Set<DeliveryInterface>();

    public DbSet<DeliveryTemplate> DeliveryTemplates => Set<DeliveryTemplate>();

    public DbSet<DeliveryCacheDefinition> DeliveryCacheDefinitions => Set<DeliveryCacheDefinition>();

    public DbSet<DeliveryCacheVersion> DeliveryCacheVersions => Set<DeliveryCacheVersion>();

    public DbSet<DeliveryCacheItem> DeliveryCacheItems => Set<DeliveryCacheItem>();

    public DbSet<DeliveryCacheMember> DeliveryCacheMembers => Set<DeliveryCacheMember>();

    public DbSet<DeliveryCacheSet> DeliveryCacheSets => Set<DeliveryCacheSet>();

    public DbSet<DeliveryCacheSetEntry> DeliveryCacheSetEntries => Set<DeliveryCacheSetEntry>();

    public DbSet<DeliveryUpdateTag> DeliveryUpdateTags => Set<DeliveryUpdateTag>();

    public DbSet<OsduSchemaVersion> SchemaVersions => Set<OsduSchemaVersion>();

    /// <summary>Options for SQL Server over a connection string, with the history table in the <c>osdu</c> schema.</summary>
    public static DbContextOptions<OsduDbContext> SqlServerOptions(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return new DbContextOptionsBuilder<OsduDbContext>()
            .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable(MigrationsHistoryTable, DeliveryModel.SchemaName))
            .Options;
    }

    /// <summary>
    /// Options for SQL Server over an existing connection the caller owns (the catalog's, on the control plane), with the
    /// history table in the <c>osdu</c> schema. The context never opens or disposes a connection it was given; to commit
    /// with the owner's transaction, the caller enlists it through <c>Database.UseTransaction</c>.
    /// </summary>
    public static DbContextOptions<OsduDbContext> SqlServerOptions(DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new DbContextOptionsBuilder<OsduDbContext>()
            .UseSqlServer(connection, sql => sql.MigrationsHistoryTable(MigrationsHistoryTable, DeliveryModel.SchemaName))
            .Options;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        // The model differs by provider (OSDU id collations exist only on SQL Server), so it is cached per provider.
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, ProviderModelCacheKeyFactory>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => DeliveryModel.Configure(modelBuilder, Database.IsSqlServer());

    /// <summary>Keys the cached model on the provider as well as the context type, so a process that opens the context on
    /// SQLite and on SQL Server builds a model for each.</summary>
    private sealed class ProviderModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime)
            => (context.GetType(), context.Database.ProviderName, designTime);
    }
}
