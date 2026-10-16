using Microsoft.EntityFrameworkCore.Design;

namespace SqlFlow.Delivery.Data;

/// <summary>
/// Builds the context for the EF Core tools (<c>dotnet ef migrations add</c>, <c>migrations script</c>): SQL Server with
/// the history table in the <c>osdu</c> schema. The tools generate code from the model without connecting, so the
/// connection string names a local server only to select the provider; migrations are applied through the module
/// database's migrate step against the configured connection, never through this factory.
/// </summary>
public sealed class OsduDbContextDesignTimeFactory : IDesignTimeDbContextFactory<OsduDbContext>
{
    public OsduDbContext CreateDbContext(string[] args)
        => new(OsduDbContext.SqlServerOptions("Server=localhost;Database=osdu_design_time;Integrated Security=true;TrustServerCertificate=true"));
}
