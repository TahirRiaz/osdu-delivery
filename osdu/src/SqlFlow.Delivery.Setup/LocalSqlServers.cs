using System.Text.RegularExpressions;

namespace SqlFlow.Delivery.Setup;

/// <summary>How a SQL Server on this machine runs.</summary>
internal enum SqlInstanceKind
{
    /// <summary>A Windows service (MSSQLSERVER, or MSSQL$NAME for a named instance).</summary>
    Service,

    /// <summary>A LocalDB instance.</summary>
    LocalDb,

    /// <summary>A Docker container from a SQL Server image.</summary>
    Container,
}

/// <summary>A SQL Server on this machine.</summary>
/// <param name="Server">The server name a connection string gives it.</param>
/// <param name="Kind">How it runs.</param>
/// <param name="Name">Its service, LocalDB instance or container name.</param>
/// <param name="State">running, stopped, or what its host calls the state otherwise, in lower case.</param>
/// <param name="Disabled">True for a Windows service whose start is disabled.</param>
internal sealed record SqlInstance(string Server, SqlInstanceKind Kind, string Name, string State, bool Disabled)
{
    /// <summary>True when it is running.</summary>
    public bool Running => State is "running";

    /// <summary>How it is listed: the server name and its state.</summary>
    public override string ToString() => Kind == SqlInstanceKind.Container ? $"{Server} (Docker container {Name}, {State})" : $"{Server} ({State})";
}

/// <summary>Whether Docker is here, and whether its engine answers.</summary>
internal enum DockerState
{
    NotInstalled,
    NotRunning,
    Running,
}

/// <summary>
/// The SQL Servers installed on this machine: Windows services (read through Windows PowerShell's Get-Service, whose status
/// names do not change with the system's language), LocalDB instances, and Docker containers started from a SQL Server image.
/// </summary>
internal static partial class LocalSqlServers
{
    /// <summary>The container the setup starts, and the volume that keeps its databases.</summary>
    public const string ContainerName = "osdu-delivery-sql";

    /// <summary>The image the setup starts: the SQL Server release the suites and CI run on.</summary>
    public const string Image = "mcr.microsoft.com/mssql/server:2022-latest";

    /// <summary>Every SQL Server on this machine, and whether Docker's engine answers.</summary>
    public static async Task<(IReadOnlyList<SqlInstance> Instances, DockerState Docker)> FindAsync(IToolRunner tools, bool isWindows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tools);
        // The three hosts are asked together: each answers in a second or two.
        var services = isWindows
            ? tools.RunAsync(
                "powershell",
                ["-NoProfile", "-NonInteractive", "-Command",
                    "Get-Service -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq 'MSSQLSERVER' -or $_.Name -like 'MSSQL$*' } | ForEach-Object { $_.Name + '|' + $_.Status + '|' + $_.StartType }"],
                new ToolRun(), ct)
            : Task.FromResult(ToolResult.NotFound);
        var localDb = isWindows ? tools.RunAsync("sqllocaldb", ["info"], new ToolRun(), ct) : Task.FromResult(ToolResult.NotFound);
        var dockerInstalled = tools.Find("docker") is not null;
        var containers = dockerInstalled
            ? tools.RunAsync("docker", ["ps", "--all", "--format", "{{.Names}}|{{.Image}}|{{.State}}|{{.Ports}}"], new ToolRun(Timeout: TimeSpan.FromSeconds(15)), ct)
            : Task.FromResult(ToolResult.NotFound);
        await Task.WhenAll(services, localDb, containers).ConfigureAwait(false);

        var found = new List<SqlInstance>();
        if ((await services.ConfigureAwait(false)) is { Succeeded: true } listed)
        {
            found.AddRange(ParseServices(listed.Output));
        }

        if ((await localDb.ConfigureAwait(false)) is { Succeeded: true } local)
        {
            found.AddRange(ParseLocalDb(local.Output));
        }

        var docker = DockerState.NotInstalled;
        if (dockerInstalled)
        {
            var ps = await containers.ConfigureAwait(false);
            docker = ps.Succeeded ? DockerState.Running : DockerState.NotRunning;
            if (ps.Succeeded)
            {
                found.AddRange(ParseContainers(ps.Output));
            }
        }

        return (found, docker);
    }

    /// <summary>Get-Service lines, <c>name|status|start type</c>, as instances.</summary>
    public static IEnumerable<SqlInstance> ParseServices(string output)
    {
        foreach (var line in Lines(output))
        {
            var parts = line.Split('|');
            if (parts.Length < 2 || parts[0].Length == 0)
            {
                continue;
            }

            var name = parts[0].Trim();
            var server = string.Equals(name, "MSSQLSERVER", StringComparison.OrdinalIgnoreCase) ? "localhost" : "localhost\\" + name[(name.IndexOf('$', StringComparison.Ordinal) + 1)..];
            var disabled = parts.Length > 2 && string.Equals(parts[2].Trim(), "Disabled", StringComparison.OrdinalIgnoreCase);
            yield return new SqlInstance(server, SqlInstanceKind.Service, name, parts[1].Trim().ToLowerInvariant(), disabled);
        }
    }

    /// <summary><c>sqllocaldb info</c> lines, one instance name each, as instances.</summary>
    public static IEnumerable<SqlInstance> ParseLocalDb(string output)
        => Lines(output).Select(name => new SqlInstance($"(localdb)\\{name}", SqlInstanceKind.LocalDb, name, "localdb", false));

    /// <summary>
    /// <c>docker ps</c> lines, <c>name|image|state|ports</c>, as instances: those of a SQL Server image, each under the
    /// server name its published port gives (<c>localhost</c> for 1433).
    /// </summary>
    public static IEnumerable<SqlInstance> ParseContainers(string output)
    {
        foreach (var line in Lines(output))
        {
            var parts = line.Split('|');
            if (parts.Length < 3 || !IsSqlServerImage(parts[1]))
            {
                continue;
            }

            var port = parts.Length > 3 && PublishedPort().Match(parts[3]) is { Success: true } published ? published.Groups["port"].Value : "1433";
            var server = port == "1433" ? "localhost" : $"localhost,{port}";
            yield return new SqlInstance(server, SqlInstanceKind.Container, parts[0].Trim(), parts[2].Trim().ToLowerInvariant(), false);
        }
    }

    /// <summary>True for an image that runs SQL Server.</summary>
    public static bool IsSqlServerImage(string image)
        => image.Contains("mssql/server", StringComparison.OrdinalIgnoreCase) || image.Contains("azure-sql-edge", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> Lines(string output)
        => output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => l.Length > 0);

    [GeneratedRegex(@":(?<port>\d{1,5})->1433/tcp", RegexOptions.CultureInvariant)]
    private static partial Regex PublishedPort();
}
