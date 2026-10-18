using Microsoft.Data.SqlClient;

namespace SqlFlow.Delivery.Setup;

/// <summary>Where a SQL Server name points.</summary>
internal enum SqlServerPlace
{
    /// <summary>An instance on this machine: localhost, ., (local), the loopback address or the machine's name.</summary>
    ThisMachine,

    /// <summary>A LocalDB instance, <c>(localdb)\name</c>.</summary>
    LocalDb,

    /// <summary>A server elsewhere.</summary>
    Elsewhere,
}

/// <summary>
/// A SQL Server connection string as the hosts read it: parsed by Microsoft.Data.SqlClient, the client every host connects
/// with, so a keyword, a default (it encrypts unless told otherwise) or a sign-in mode means here what it means to them.
/// </summary>
internal sealed class SqlTarget
{
    private SqlTarget(string text, SqlConnectionStringBuilder builder)
    {
        Text = text;
        Builder = builder;
    }

    /// <summary>The connection string as written.</summary>
    public string Text { get; }

    /// <summary>The connection string as the client reads it.</summary>
    public SqlConnectionStringBuilder Builder { get; }

    /// <summary>The server, as written.</summary>
    public string Server => Builder.DataSource;

    /// <summary>The database, or empty when the login's default database is meant.</summary>
    public string Database => Builder.InitialCatalog;

    /// <summary>True for Windows authentication.</summary>
    public bool Integrated => Builder.IntegratedSecurity;

    /// <summary>The SQL login, or empty.</summary>
    public string User => Builder.UserID;

    /// <summary>True when the string names a secret reference, which only a host resolves.</summary>
    public bool HasReference => Text.Contains("${", StringComparison.Ordinal);

    /// <summary>True when the string says how to sign in: Windows authentication, a SQL login, or an Entra mode.</summary>
    public bool SaysHowToSignIn
        => Integrated || User.Length > 0 || Builder.Authentication != SqlAuthenticationMethod.NotSpecified;

    /// <summary>How the string signs in, in words.</summary>
    public string SignIn => Integrated
        ? "Windows authentication"
        : Builder.Authentication != SqlAuthenticationMethod.NotSpecified
            ? $"Authentication={Builder.Authentication}"
            : User.Length > 0 ? $"SQL login {User}" : "no sign-in";

    /// <summary>Where the server is.</summary>
    public SqlServerPlace Place => PlaceOf(Server).Place;

    /// <summary>The instance name the server names after its backslash, or empty for a default instance.</summary>
    public string Instance => PlaceOf(Server).Instance;

    /// <summary>The string read, or null when the client cannot read it or it names no server.</summary>
    public static SqlTarget? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            var builder = new SqlConnectionStringBuilder(text);
            return string.IsNullOrWhiteSpace(builder.DataSource) ? null : new SqlTarget(text, builder);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The server, the database and the sign-in, never a password.</summary>
    public string Describe() => $"{Server} / {(Database.Length > 0 ? Database : "(the login's default database)")}, {SignIn}";

    /// <summary>
    /// This connection to <paramref name="database"/> instead, for a probe: a short connect timeout, no pooling (each probe
    /// stands alone) and the setup's application name, with every other keyword (encryption, sign-in) as written.
    /// </summary>
    public string For(string database)
    {
        ArgumentNullException.ThrowIfNull(database);
        var probe = new SqlConnectionStringBuilder(Builder.ConnectionString)
        {
            InitialCatalog = database,
            ConnectTimeout = 8,
            Pooling = false,
            ApplicationName = "OSDU Delivery dev setup",
        };
        return probe.ConnectionString;
    }

    /// <summary>The server's identity for caching a probe: the same server under the same sign-in answers the same.</summary>
    public string ServerKey
        => string.Join('|', Server.ToUpperInvariant(), Integrated, User, Builder.Authentication, Builder.Encrypt, Builder.TrustServerCertificate);

    /// <summary>
    /// A connection string for the setup to write: the server, the database, Windows authentication or the SQL login, and,
    /// for a server on this machine (whose certificate is self-signed), TrustServerCertificate. A server elsewhere is
    /// verified as the client verifies it by default.
    /// </summary>
    public static string Compose(string server, string database, string? user, string? password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        var text = $"Server={DevValues.QuoteConnectionValue(server)};Database={DevValues.QuoteConnectionValue(database)};";
        text += string.IsNullOrEmpty(user)
            ? "Integrated Security=True;"
            : $"User ID={DevValues.QuoteConnectionValue(user)};Password={DevValues.QuoteConnectionValue(password ?? string.Empty)};";
        text += PlaceOf(server).Place == SqlServerPlace.Elsewhere ? "Encrypt=True" : "TrustServerCertificate=True";
        return text;
    }

    /// <summary>Where a server name points, and the instance it names.</summary>
    public static (SqlServerPlace Place, string Instance) PlaceOf(string server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var name = server.Trim();
        foreach (var prefix in new[] { "tcp:", "np:", "lpc:", "admin:" })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[prefix.Length..];
                break;
            }
        }

        var comma = name.IndexOf(',', StringComparison.Ordinal);
        if (comma >= 0)
        {
            name = name[..comma];
        }

        var instance = string.Empty;
        var slash = name.IndexOf('\\', StringComparison.Ordinal);
        if (slash >= 0)
        {
            instance = name[(slash + 1)..];
            name = name[..slash];
        }

        if (string.Equals(name, "(localdb)", StringComparison.OrdinalIgnoreCase))
        {
            return (SqlServerPlace.LocalDb, instance);
        }

        var local = name is "." || name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || name.Equals("(local)", StringComparison.OrdinalIgnoreCase) || name is "127.0.0.1" or "::1" or "[::1]"
            || name.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        return (local ? SqlServerPlace.ThisMachine : SqlServerPlace.Elsewhere, instance);
    }
}
