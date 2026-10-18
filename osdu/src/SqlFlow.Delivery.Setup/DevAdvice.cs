namespace SqlFlow.Delivery.Setup;

/// <summary>
/// The commands a fix names, for the platform it is on: the launcher (dev.bat or ./dev.sh), the command line in the
/// checkout, and the installers (winget on Windows, Homebrew on a Mac, the vendor's page on Linux).
/// </summary>
internal sealed record DevAdvice(DevPlatform Platform, string EnvFile, string Account)
{
    /// <summary>The launcher the user starts a local run with.</summary>
    public string Launcher => Platform == DevPlatform.Windows ? "dev.bat" : "./dev.sh";

    /// <summary>The checkout's command line, before a verb.</summary>
    public string Cli => Platform == DevPlatform.Windows
        ? @"dotnet run --project osdu\hosts\SqlFlow.Delivery.Cli.Host --"
        : "dotnet run --project osdu/hosts/SqlFlow.Delivery.Cli.Host --";

    /// <summary>Creates the catalog database with the catalog and the module's schema.</summary>
    public string CreateCatalog => $"{Cli} db migrate --create";

    public string InstallDotnet => Platform switch
    {
        DevPlatform.Windows => "Install the .NET 9 SDK: winget install Microsoft.DotNet.SDK.9",
        DevPlatform.MacOS => "Install the .NET SDK: brew install --cask dotnet-sdk",
        _ => "Install the .NET 9 SDK: https://learn.microsoft.com/dotnet/core/install/linux",
    };

    public string InstallNode => Platform switch
    {
        DevPlatform.Windows => "Install Node.js 22 or later: winget install OpenJS.NodeJS.LTS",
        DevPlatform.MacOS => "Install Node.js 22 or later: brew install node",
        _ => "Install Node.js 22 or later: https://nodejs.org/en/download",
    };

    public string InstallAzureCli => Platform switch
    {
        DevPlatform.Windows => "Install the Azure CLI: winget install Microsoft.AzureCLI",
        DevPlatform.MacOS => "Install the Azure CLI: brew install azure-cli",
        _ => "Install the Azure CLI: https://learn.microsoft.com/cli/azure/install-azure-cli-linux",
    };

    public string InstallDocker => Platform switch
    {
        DevPlatform.Windows => "Install Docker Desktop: winget install Docker.DockerDesktop",
        DevPlatform.MacOS => "Install Docker Desktop: brew install --cask docker (on Apple silicon, turn on its Rosetta emulation for x86/amd64 images)",
        _ => "Install Docker Engine: https://docs.docker.com/engine/install/",
    };

    /// <summary>What to do on a machine with no SQL Server, in the order to try it.</summary>
    public IReadOnlyList<string> GetSqlServer => Platform == DevPlatform.Windows
        ?
        [
            "Install SQL Server 2022 Developer (free for development): winget install Microsoft.SQLServer.2022.Developer",
            $"or, with Docker Desktop running, let {Launcher} setup start SQL Server 2022 in a container for you,",
            $"or use a SQL Server elsewhere: {Launcher} setup, and give its name.",
        ]
        :
        [
            $"SQL Server runs here in Docker. {InstallDocker}; start it,",
            $"then {Launcher} setup starts SQL Server 2022 in a container for you (or give a SQL Server elsewhere).",
        ];

    /// <summary>Opens a new terminal so a tool installed since this one started is on its PATH.</summary>
    public const string NewTerminal = "then open a new terminal, so the PATH has it.";
}
