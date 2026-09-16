using SqlFlow.Catalog.Modules;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Hosting;

/// <summary>
/// The OSDU module's database as a host registers it with the platform (docs/stage4-design.md section 3.1): the
/// <c>osdu</c> schema, its migrations and its version row, built from <see cref="OsduDbContext"/>. One declaration
/// serves every host, so the control plane, the CLI and a worker node migrate, report and verify exactly the same
/// database: <c>sqlflow db migrate</c> and <c>db status</c> cover it after the catalog, a node verifies it before it
/// takes work, and the control plane refuses to run over one that is missing, behind or ahead.
/// </summary>
public static class OsduModuleDatabase
{
    /// <summary>The configuration key a host reads the module's own connection reference from.</summary>
    public const string ConnectionOption = "Osdu:Database:Connection";

    /// <summary>The environment variable the module's connection reference names by default.</summary>
    public const string ConnectionVariable = "SQLFLOW_OSDU_DB";

    /// <summary>The reference a host uses when the module's connection is the environment variable above.</summary>
    public const string EnvironmentReference = "${env:" + ConnectionVariable + "}";

    /// <summary>
    /// The module database over <paramref name="connectionReference"/>, a <c>${env:NAME}</c> or <c>${keyvault:NAME}</c>
    /// reference resolved on the host that opens it. Null puts the <c>osdu</c> schema in the catalog's own database,
    /// which is what the control plane does unless a deployment gives the module a database of its own; a worker node has
    /// no catalog connection, so it always passes a reference.
    /// </summary>
    /// <exception cref="ModuleDatabaseException">The reference is not a secret reference; the message never echoes it.</exception>
    public static ModuleDatabase<OsduDbContext> Create(string? connectionReference = null)
        => new(
            OsduSchema.Module,
            DeliveryModel.SchemaName,
            OsduDbContext.ModuleVersion,
            connectionString => new OsduDbContext(OsduDbContext.SqlServerOptions(connectionString)),
            connection => new OsduDbContext(OsduDbContext.SqlServerOptions(connection)),
            string.IsNullOrWhiteSpace(connectionReference) ? null : connectionReference.Trim(),
            OsduDbContext.MinimumCatalogMigration)
        {
            AfterMigrate = (context, applied, ct) => OsduSchema.RecordAsync(
                context,
                new OsduSchema.Applied(applied.Version, applied.LastMigration, applied.AppliedBy, applied.AppliedUtc, applied.MinimumCatalogMigration),
                ct),
            ReadRecordedVersion = async (context, ct) =>
                await OsduSchema.ReadAsync(context, ct).ConfigureAwait(false) is { } recorded
                    ? new ModuleRecordedVersion(recorded.ModuleVersion, recorded.LastMigration)
                    : null,
        };

    /// <summary>
    /// The module's connection reference for a host that reads configuration: the value of
    /// <see cref="ConnectionOption"/>, else <see cref="EnvironmentReference"/> when that variable is set, else null for
    /// the catalog's own database.
    /// </summary>
    /// <param name="configured">The value the host's configuration carries for <see cref="ConnectionOption"/>, or null.</param>
    /// <param name="environment">Reads an environment variable; <see cref="Environment.GetEnvironmentVariable(string)"/> in a host.</param>
    public static string? ResolveReference(string? configured, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        return string.IsNullOrWhiteSpace(environment(ConnectionVariable)) ? null : EnvironmentReference;
    }
}
