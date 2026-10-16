using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Delivery.Data;

/// <summary>
/// The version of the <c>osdu</c> schema a database holds, recorded in the single <see cref="OsduSchemaVersion"/> row. The
/// module database's migrate step calls <see cref="RecordAsync"/> on the migrated context's own connection once the
/// migrations are applied, and status and startup verification call <see cref="ReadAsync"/>. Both are what the host
/// module's <c>ModuleDatabase&lt;OsduDbContext&gt;</c> declaration passes as its after-migrate and read-version hooks.
/// </summary>
public static class OsduSchema
{
    /// <summary>The module name the <c>osdu</c> schema is registered under.</summary>
    public const string Module = "osdu";

    /// <summary>The longest actor a version row keeps.</summary>
    public const int MaxAppliedByLength = 200;

    /// <summary>What a migrate applied, as the version row records it.</summary>
    /// <param name="ModuleVersion">The module version the applied migrations produce.</param>
    /// <param name="LastMigration">The newest migration applied, or null when the build knows none.</param>
    /// <param name="AppliedBy">The host and actor that ran the migrate.</param>
    /// <param name="AppliedUtc">When it ran.</param>
    /// <param name="MinimumCatalogMigration">The oldest SQLFlow catalog migration the schema needs beside it.</param>
    public sealed record Applied(string ModuleVersion, string? LastMigration, string AppliedBy, DateTime AppliedUtc, string? MinimumCatalogMigration);

    /// <summary>The version a database recorded: the module version and the last migration applied.</summary>
    public sealed record Recorded(string ModuleVersion, string LastMigration, DateTime AppliedUtc, string AppliedBy, string MinimumCatalogMigration);

    /// <summary>
    /// Writes the version row: inserted on the first migrate, updated on every later one, never a second row (the table's
    /// check constraint allows only id 1). Runs on whatever connection and transaction the context was opened with.
    /// </summary>
    public static async Task RecordAsync(OsduDbContext context, Applied applied, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(applied);
        ArgumentException.ThrowIfNullOrWhiteSpace(applied.ModuleVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(applied.AppliedBy);

        var row = await context.SchemaVersions.AsTracking().FirstOrDefaultAsync(v => v.Id == 1, ct).ConfigureAwait(false);
        if (row is null)
        {
            row = new OsduSchemaVersion { Id = 1 };
            context.SchemaVersions.Add(row);
        }

        row.ModuleVersion = applied.ModuleVersion;
        row.LastMigration = applied.LastMigration ?? string.Empty;
        row.AppliedUtc = DateTime.SpecifyKind(applied.AppliedUtc, DateTimeKind.Utc);
        row.AppliedBy = applied.AppliedBy.Length <= MaxAppliedByLength ? applied.AppliedBy : applied.AppliedBy[..MaxAppliedByLength];
        row.MinimumCatalogMigration = applied.MinimumCatalogMigration ?? OsduDbContext.MinimumCatalogMigration;
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The recorded version, or null when no migrate has recorded one yet.</summary>
    public static async Task<Recorded?> ReadAsync(OsduDbContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var row = await context.SchemaVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == 1, ct).ConfigureAwait(false);
        return row is null
            ? null
            : new Recorded(row.ModuleVersion, row.LastMigration, DateTime.SpecifyKind(row.AppliedUtc, DateTimeKind.Utc), row.AppliedBy, row.MinimumCatalogMigration);
    }
}
