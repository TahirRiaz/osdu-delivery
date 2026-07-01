using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SqlFlow.Catalog;

/// <summary>
/// The bootstrap and upgrade surface for the shadow catalog database. <see cref="MigrateAsync"/> is the
/// enterprise migration path: on an empty server it creates the database and the <c>catalog</c> schema, and on an
/// existing database it applies exactly the pending migrations to bring it to the current schema version, tracked
/// in <c>catalog.__CatalogMigrationsHistory</c>. It is idempotent (a no-op when already current). Each migration
/// runs in its own transaction, so no single migration is ever half-applied; the initial CREATE DATABASE itself
/// is not transactional, so an upgrade interrupted at the very first bootstrap can leave an empty database that
/// the next run completes (re-running MigrateAsync is always safe).
/// </summary>
public static class CatalogDatabase
{
    /// <summary>The EF options for the catalog, with the migrations history table pinned into the catalog schema.</summary>
    public static DbContextOptions<CatalogDbContext> BuildOptions(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlServer(connectionString, sql =>
                sql.MigrationsHistoryTable("__CatalogMigrationsHistory", CatalogDbContext.SchemaName))
            .Options;
    }

    public static CatalogDbContext Create(string connectionString) => new(BuildOptions(connectionString));

    /// <summary>Creates the database if missing and applies all pending migrations (the in-place upgrade path).</summary>
    public static async Task MigrateAsync(string connectionString, CancellationToken ct = default)
    {
        await using var context = Create(connectionString);
        await context.Database.MigrateAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The migration names this build knows (applied) vs. those already in the database (so a caller can
    /// report drift). Empty pending means the database is current.</summary>
    public static async Task<(IReadOnlyList<string> Applied, IReadOnlyList<string> Pending)> StatusAsync(
        string connectionString, CancellationToken ct = default)
    {
        await using var context = Create(connectionString);
        var applied = (await context.Database.GetAppliedMigrationsAsync(ct).ConfigureAwait(false)).ToList();
        var pending = (await context.Database.GetPendingMigrationsAsync(ct).ConfigureAwait(false)).ToList();
        return (applied, pending);
    }
}

/// <summary>
/// Design-time factory so the EF tools (<c>dotnet ef migrations add ...</c>) can construct the context without the
/// app's DI. The connection string is irrelevant for generating migrations; a real value is only needed when the
/// tool talks to a database, supplied via <c>SQLFLOW_CATALOG_DB</c>.
/// </summary>
public sealed class CatalogDbContextFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("SQLFLOW_CATALOG_DB")
            ?? "Server=(localdb)\\MSSQLLocalDB;Database=SqlFlowCatalog;Trusted_Connection=True;TrustServerCertificate=True";
        return new CatalogDbContext(CatalogDatabase.BuildOptions(connectionString));
    }
}
