using System.Data.Common;
using MySqlConnector;
using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Ingestion;

namespace SqlFlow.Providers.MySql;

/// <summary>Opens pooled MySQL connections (MySqlConnector, true async I/O).</summary>
public sealed class MySqlProviderConnectionFactory : IProviderConnectionFactory
{
    public bool CanHandle(DataSourceKind kind) => kind == DataSourceKind.MySQL;

    public async Task<DbConnection> OpenAsync(ResolvedConnection connection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var mySqlConnection = new MySqlConnection(connection.CanonicalString);
        try
        {
            await mySqlConnection.OpenAsync(ct).ConfigureAwait(false);
            return mySqlConnection;
        }
        catch
        {
            await mySqlConnection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>
/// Canonicalizes MySQL connection strings through <see cref="MySqlConnectionStringBuilder"/> and enforces the
/// secretless gate. MySQL has no self-authenticating connection-string mode, so a literal inline string can
/// never satisfy <see cref="SecretlessPolicy.RequireSelfAuthenticating"/>: a MySQL connection must arrive as a
/// whole <c>${...}</c> secret reference or through a registry entry whose credential mode trusts the value.
/// </summary>
public sealed class MySqlConnectionStringCanonicalizer : IConnectionStringCanonicalizer
{
    public bool CanHandle(DataSourceKind kind) => kind == DataSourceKind.MySQL;

    public CanonicalConnection Canonicalize(string connectionString, ConnectionRole role, SecretlessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        MySqlConnectionStringBuilder builder;
        try
        {
            builder = new MySqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException ex)
        {
            throw new SqlFlowException($"Malformed MySQL connection string: {ex.Message}", ex);
        }

        switch (policy)
        {
            case SecretlessPolicy.Trusted:
                break; // expanded from a whole ${...} secret reference
            case SecretlessPolicy.RejectRestingSecret when string.IsNullOrEmpty(builder.Password):
                break;
            case SecretlessPolicy.RejectRestingSecret:
                throw new SqlFlowException(
                    "MySQL connection strings must not contain a resting password. Supply the whole string as a ${...} secret reference.");
            default:
                throw new SqlFlowException(
                    "MySQL has no self-authenticating connection-string mode. Supply the whole connection string as a ${...} secret reference.");
        }

        builder.ApplicationName = role == ConnectionRole.Source ? "SQLFlow Source" : "SQLFlow Target";
        var canonical = builder.ConnectionString;

        var safe = new MySqlConnectionStringBuilder(canonical) { Password = string.Empty, UserID = string.Empty };
        return new CanonicalConnection { Canonical = canonical, Redacted = safe.ConnectionString };
    }
}

/// <summary>
/// T-SQL-to-MySQL dialect for the SOURCE side: backtick quoting (a literal backtick doubles), the schema part
/// of the three-part name addressing the MySQL database (MySQL schema IS the database), and DATE_SUB
/// arithmetic. Binary watermark literals use the 0x hex form MySQL shares with SQL Server.
/// </summary>
public sealed class MySqlSourceDialect : ISourceSqlDialect
{
    public bool CanHandle(DataSourceKind kind) => kind == DataSourceKind.MySQL;

    public string QuoteIdentifier(string identifier)
        => $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`";

    public string QualifyObject(RelationalObject table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return $"{QuoteIdentifier(table.Schema)}.{QuoteIdentifier(table.Name)}";
    }

    public string DateSubtractDays(string expression, int days) => $"DATE_SUB({expression}, INTERVAL {days} DAY)";

    public string FormatBinaryLiteral(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "0x" + Convert.ToHexString(value);
    }
}
