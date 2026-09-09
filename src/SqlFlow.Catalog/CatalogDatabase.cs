using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace SqlFlow.Catalog;

/// <summary>
/// The provisioning surface for the catalog database. The EF model is the master: the schema is created from it
/// directly, and there are no migrations. While no production catalog exists, a schema change means dropping the
/// database and provisioning it again, which is deliberate (see the repository's CLAUDE.MD).
/// <list type="bullet">
/// <item><see cref="ProvisionAsync"/> is the EXPLICIT path: on an empty server it creates the database and the whole
/// schema from the model. Reserve it for callers that have deliberately asked to provision (the
/// <c>sqlflow db migrate --create</c> verb, tests, ephemeral databases).</item>
/// <item><see cref="ProvisionExistingAsync"/> is the GUARDED path used by every AUTOMATIC caller (control-plane
/// startup, the per-run catalog write-back, <c>db sync</c>): it refuses to create a missing database or to inject
/// catalog tables into a populated non-catalog database, so a wrong or mistyped connection fails loudly instead of
/// provisioning against the wrong (possibly production) server.</item>
/// </list>
/// Both are idempotent: an already-provisioned catalog is left alone. Because nothing alters an existing schema,
/// both also VERIFY it against the model afterwards and refuse to run against a database that is missing tables the
/// code needs, so a stale database fails at startup with the tables named instead of surfacing later as
/// "Invalid object name" on whichever request happened to touch one.
/// </summary>
public static class CatalogDatabase
{
    /// <summary>
    /// THE single definition of how a catalog <see cref="DbContext"/> talks to SQL Server: transient-error
    /// resiliency, applied everywhere. Every catalog context in the product is configured through here (this
    /// class's <see cref="BuildOptions"/> for the CLI, the worker and bootstrap; the control plane's pooled
    /// registration for the API), so no host can end up with weaker resiliency than another.
    ///
    /// The resiliency is not optional polish. The catalog is a genuinely concurrent OLTP workload: one schedule fire
    /// enqueues every member flow at once and each run writes its own claim, status, event and statement rows, and
    /// <see cref="CatalogTransaction"/> deliberately runs its units at SERIALIZABLE. SQL Server resolves the
    /// resulting lock cycles by picking a deadlock victim (error 1205), which is a retryable outcome, not a fault.
    /// Without a retrying execution strategy EF Core surfaces it as "An exception has been raised that is likely due
    /// to a transient failure...", which fails the RUN over a catalog bookkeeping collision that had nothing to do
    /// with the data. That is exactly what happened to a 23-flow schedule fire (batch Trapeze) where the worker,
    /// which builds its context here, had no retry while the control plane's pooled context did.
    ///
    /// EF Core forbids a user-initiated transaction under a retrying strategy, because a retry has to replay the
    /// whole transaction rather than half of it; <see cref="CatalogTransaction"/> already wraps its serializable
    /// unit in <c>CreateExecutionStrategy().ExecuteAsync</c> and clears the change tracker per attempt, so every
    /// transactional catalog path is replay-safe.
    /// </summary>
    public static void Configure(DbContextOptionsBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        builder.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure());
    }

    /// <summary>The EF options for the catalog, configured by <see cref="Configure"/>.</summary>
    public static DbContextOptions<CatalogDbContext> BuildOptions(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var builder = new DbContextOptionsBuilder<CatalogDbContext>();
        Configure(builder, connectionString);
        return builder.Options;
    }

    public static CatalogDbContext Create(string connectionString) => new(BuildOptions(connectionString));

    /// <summary>
    /// EXPLICIT provisioning: creates the database if missing and the whole schema from the model if the database
    /// holds no tables. This is the only path that may bring a database into existence, so reserve it for callers
    /// that have deliberately asked to provision (the <c>db migrate --create</c> verb, tests). Automatic callers use
    /// <see cref="ProvisionExistingAsync"/> instead. An already-provisioned catalog is left untouched and verified.
    /// </summary>
    public static async Task ProvisionAsync(string connectionString, CancellationToken ct = default)
    {
        await using var context = Create(connectionString);
        await context.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);
        await VerifyAsync(context, connectionString, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// GUARDED provisioning for automatic callers: initialises an EXISTING but empty database, and otherwise only
    /// verifies. It never creates a missing database and never initialises catalog tables into a database that
    /// already holds unrelated objects. Throws <see cref="CatalogProvisioningException"/> in those cases (a
    /// configuration mistake to surface, not retry). A server that is unreachable throws the underlying transient
    /// error, which a retrying caller can distinguish from the deterministic exception.
    /// </summary>
    public static async Task ProvisionExistingAsync(string connectionString, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        await using var context = Create(connectionString);

        // ExistsAsync connects to the server: an unreachable server throws (a transient the caller may retry),
        // while a reachable server with no such database returns false (a configuration problem we refuse to
        // paper over by silently creating it).
        var creator = context.GetService<IRelationalDatabaseCreator>();
        if (!await creator.ExistsAsync(ct).ConfigureAwait(false))
        {
            throw new CatalogProvisioningException(
                $"The catalog database does not exist ({DescribeTarget(connectionString)}). Refusing to create it " +
                "automatically. Provision it explicitly with 'sqlflow db migrate --create --db <ref>', or point the " +
                "connection at your existing catalog.");
        }

        // The database exists. With none of the catalog's own tables present it is either an empty database made
        // for the catalog (safe to initialise) or, dangerously, an unrelated populated database (e.g. a data
        // warehouse) this connection reached by mistake. Never inject catalog tables into the latter.
        var present = await PresentTablesAsync(context, ct).ConfigureAwait(false);
        if (present.Count == 0)
        {
            var existingTables = await CountUserTablesAsync(context, ct).ConfigureAwait(false);
            if (existingTables > 0)
            {
                throw new CatalogProvisioningException(
                    $"The database ({DescribeTarget(connectionString)}) exists and contains {existingTables} table(s) " +
                    "but none of the catalog's own. Refusing to initialise catalog tables into a populated database " +
                    "that may not be a catalog. If this really is a new, dedicated catalog database, provision it with " +
                    "'sqlflow db migrate --create'.");
            }

            await context.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);
        }

        await VerifyAsync(context, connectionString, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the catalog is provisioned, and which of the model's tables the database is missing. Nothing alters
    /// an existing schema, so a non-empty missing list means the database predates a model change and has to be
    /// provisioned again.
    /// </summary>
    public static async Task<(bool Provisioned, IReadOnlyList<string> Missing)> StatusAsync(
        string connectionString, CancellationToken ct = default)
    {
        await using var context = Create(connectionString);
        var expected = ExpectedTables(context);
        var present = await PresentTablesAsync(context, ct).ConfigureAwait(false);
        var missing = expected.Where(t => !present.Contains(t)).OrderBy(t => t, StringComparer.Ordinal).ToList();
        return (present.Count > 0, missing);
    }

    /// <summary>A human-readable, secret-free description of a connection's target (server and database), for logs
    /// and for the messages the provisioning guards raise.</summary>
    public static string DescribeTarget(string connectionString)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            var server = string.IsNullOrWhiteSpace(builder.DataSource) ? "(unknown server)" : builder.DataSource;
            var database = string.IsNullOrWhiteSpace(builder.InitialCatalog) ? "(unknown database)" : builder.InitialCatalog;
            return $"server '{server}', database '{database}'";
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or KeyNotFoundException)
        {
            return "(unparseable connection string)";
        }
    }

    /// <summary>
    /// Refuses to run against a database that is missing tables the model declares. Without migrations nothing
    /// upgrades a schema in place, so this is what turns "the database predates this build" into one clear failure
    /// at startup rather than an "Invalid object name" 500 on whichever request first touches a missing table.
    /// </summary>
    private static async Task VerifyAsync(CatalogDbContext context, string connectionString, CancellationToken ct)
    {
        var expected = ExpectedTables(context);
        var present = await PresentTablesAsync(context, ct).ConfigureAwait(false);
        var missing = expected.Where(t => !present.Contains(t)).OrderBy(t => t, StringComparer.Ordinal).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        throw new CatalogProvisioningException(
            $"The catalog database ({DescribeTarget(connectionString)}) is missing {missing.Count} table(s) this build " +
            $"declares: {string.Join(", ", missing)}. The schema is created from the model and nothing upgrades it in " +
            "place, so a database provisioned before a model change has to be provisioned again: drop it and run " +
            "'sqlflow db migrate --create --db <ref>'.");
    }

    /// <summary>Every table the model declares, as <c>schema.table</c>.</summary>
    private static HashSet<string> ExpectedTables(CatalogDbContext context)
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in context.Model.GetEntityTypes())
        {
            if (entity.GetTableName() is { Length: > 0 } table)
            {
                tables.Add((entity.GetSchema() ?? CatalogDbContext.SchemaName) + "." + table);
            }
        }

        return tables;
    }

    /// <summary>The model's tables that the connected database actually has, as <c>schema.table</c>.</summary>
    private static async Task<HashSet<string>> PresentTablesAsync(CatalogDbContext context, CancellationToken ct)
    {
        var expected = ExpectedTables(context);
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connection = context.Database.GetDbConnection();
        await context.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT s.name + N'.' + t.name FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id";
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var name = reader.GetString(0);
                if (expected.Contains(name))
                {
                    present.Add(name);
                }
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }

        return present;
    }

    /// <summary>Counts real user tables in the connected database. Used to tell an empty database (safe to
    /// initialise) from a populated one (refuse).</summary>
    private static async Task<int> CountUserTablesAsync(CatalogDbContext context, CancellationToken ct)
    {
        var connection = context.Database.GetDbConnection();
        await context.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sys.tables";
            var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return result is int n ? n : Convert.ToInt32(result, CultureInfo.InvariantCulture);
        }
        finally
        {
            await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Raised by <see cref="CatalogDatabase.ProvisionExistingAsync"/> when it refuses to provision: the target database
/// does not exist, exists but is not a SqlFlow catalog, or is missing tables this build declares. It marks a
/// deterministic configuration mistake (a wrong or mistyped connection, or a database that predates a model
/// change), so callers surface it to the operator rather than retrying.
/// </summary>
public sealed class CatalogProvisioningException : Exception
{
    public CatalogProvisioningException(string message)
        : base(message)
    {
    }

    public CatalogProvisioningException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
