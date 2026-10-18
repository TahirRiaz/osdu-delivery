using System.Text.RegularExpressions;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Delivery.Setup;

/// <summary>
/// The settings a local run starts with, as the launchers set them: the env file's (the first of a name set twice, as the
/// hosts take it), then this terminal's for a name the file leaves out.
/// </summary>
internal sealed class DevSettings
{
    private readonly Dictionary<string, string> _values;
    private readonly HashSet<string> _fromTerminal;

    private DevSettings(Dictionary<string, string> values, HashSet<string> fromTerminal)
    {
        _values = values;
        _fromTerminal = fromTerminal;
    }

    /// <summary>Every name with a value, the file's and the terminal's.</summary>
    public IReadOnlyDictionary<string, string> Values => _values;

    /// <summary>The settings of <paramref name="file"/> (none when null), with <paramref name="terminal"/>'s for the names <paramref name="known"/> lists that it leaves out.</summary>
    public static DevSettings From(LocalEnvFileContent? file, IReadOnlyDictionary<string, string> terminal, IEnumerable<string> known)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(known);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in file?.Entries ?? [])
        {
            values.TryAdd(entry.Name, entry.Value);
        }

        var fromTerminal = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in known)
        {
            if (!values.ContainsKey(name) && terminal.TryGetValue(name, out var value) && value.Length > 0)
            {
                values[name] = value;
                fromTerminal.Add(name);
            }
        }

        return new DevSettings(values, fromTerminal);
    }

    /// <summary>The value of <paramref name="name"/>, or null when it has none (or an empty one).</summary>
    public string? Get(string name) => _values.TryGetValue(name, out var value) && value.Length > 0 ? value : null;

    /// <summary>True when <paramref name="name"/> has a value that is not a template's placeholder.</summary>
    public bool IsSet(string name) => Get(name) is { } value && !DevValues.IsPlaceholder(value);

    /// <summary>True when the value came from this terminal rather than the file.</summary>
    public bool FromTerminal(string name) => _fromTerminal.Contains(name);
}

/// <summary>The tools a local run uses, as found.</summary>
/// <param name="Dotnet">dotnet --version, run in the checkout (so global.json decides).</param>
/// <param name="Node">node --version.</param>
/// <param name="Npm">True when npm is on the PATH.</param>
/// <param name="AzureNeeded">True when the settings read Azure through the Azure CLI.</param>
/// <param name="AzureAccount">az account show; null when Azure is not needed.</param>
/// <param name="DotnetOffPath">Where the SDK is when this terminal's PATH does not have it (installed since it started), or null.</param>
internal sealed record ToolFacts(ToolResult Dotnet, ToolResult Node, bool Npm, bool AzureNeeded, ToolResult? AzureAccount, string? DotnetOffPath = null);

/// <summary>A repository a local run syncs, as configured or registered, and its folder on this machine when it has one.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Where">Its folder or remote.</param>
/// <param name="Folder">The folder its flows are read from on this machine, or null for a remote.</param>
/// <param name="FolderExists">True when that folder exists.</param>
/// <param name="Enabled">False when the catalog has it disabled.</param>
/// <param name="LastError">Its last sync's error, or empty.</param>
/// <param name="Configured">True when the env file configures it (registered at the next start).</param>
/// <param name="Registered">True when the catalog holds it.</param>
internal sealed record RepositoryScan(string Name, string Where, string? Folder, bool FolderExists, bool Enabled, string LastError, bool Configured, bool Registered);

/// <summary>A <c>${env:NAME}</c> the repositories' documents name, and the documents that name it.</summary>
internal sealed record ReferenceUse(string Name, IReadOnlyList<string> Documents);

/// <summary>A database a setting names, as found: the server's facts and the database's.</summary>
/// <param name="Setting">The setting that names it.</param>
/// <param name="Target">The connection string, read.</param>
/// <param name="Tested">Why it was not tested, or null when it was.</param>
/// <param name="Server">The server's facts, when tested.</param>
/// <param name="Database">The database's facts, when the server answered.</param>
internal sealed record DatabaseSide(string Setting, SqlTarget Target, string? Tested, ServerFacts? Server, DatabaseFacts? Database);

/// <summary>Everything the check found, before it says what any of it means.</summary>
internal sealed record DevFacts(
    DevAdvice Advice,
    DevEnvTemplate Template,
    bool FileExists,
    LocalEnvFileContent? File,
    DevSettings Settings,
    IReadOnlyDictionary<string, string> Terminal,
    bool Gui,
    ToolFacts Tools,
    IReadOnlyList<SqlInstance> Instances,
    DockerState Docker,
    DatabaseSide? Catalog,
    CatalogFacts? CatalogContents,
    DatabaseSide? Data,
    IReadOnlyList<RepositoryScan> Repositories,
    IReadOnlyList<ReferenceUse>? References,
    int DocumentsRead,
    OsduSignInResult? Osdu,
    string? OsduNotTested);

/// <summary>Gathers a local run's facts: the file, the tools, the SQL Servers, the catalog, the repositories and OSDU.</summary>
internal static partial class DevFactsCollector
{
    /// <summary>The settings an OSDU sign-in needs.</summary>
    private static readonly string[] ClientSettings = ["OSDU_TOKEN_URL", "OSDU_CLIENT_ID", "OSDU_CLIENT_SECRET", "OSDU_SCOPE"];

    /// <summary>The most documents a repository's scan reads, and the largest one.</summary>
    private const int MaxDocuments = 5000;

    private const long MaxDocumentBytes = 4 * 1024 * 1024;

    /// <summary>Gathers the facts.</summary>
    /// <param name="checkout">The checkout.</param>
    /// <param name="template">Its template.</param>
    /// <param name="machine">What reaches outside.</param>
    /// <param name="gui">True when the GUI starts too (Node.js is needed).</param>
    /// <param name="testOsdu">True to sign in to OSDU and read the legal tag.</param>
    /// <param name="ct">Stops it.</param>
    public static async Task<DevFacts> CollectAsync(DevCheckout checkout, DevEnvTemplate template, DevMachine machine, bool gui, bool testOsdu, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(checkout);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(machine);
        var advice = new DevAdvice(machine.Platform, checkout.Show(checkout.EnvFile), machine.Account);
        var fileExists = File.Exists(checkout.EnvFile);
        var file = fileExists ? LocalEnvFile.Read(checkout.EnvFile) : null;
        var settings = DevSettings.From(file, machine.Environment, template.Settings.Where(s => !s.IsFamily).Select(s => s.Name));

        // The tools answer in seconds each, so they are asked together.
        var toolsTask = ToolsAsync(checkout, machine, settings, ct);
        var instancesTask = LocalSqlServers.FindAsync(machine.Tools, machine.IsWindows, ct);
        await Task.WhenAll(toolsTask, instancesTask).ConfigureAwait(false);
        var tools = await toolsTask.ConfigureAwait(false);
        var (instances, docker) = await instancesTask.ConfigureAwait(false);

        // Without a file, the template's catalog says whether this machine has the SQL Server a setup would use.
        var catalogText = fileExists ? settings.Get("SQLFLOW_CATALOG_DB") : template.Find("SQLFLOW_CATALOG_DB")?.Value;
        var catalog = await SideAsync("SQLFLOW_CATALOG_DB", catalogText, machine, null, ct).ConfigureAwait(false);
        CatalogFacts? contents = null;
        if (catalog?.Database is { Exists: true, Online: true, Access: true })
        {
            contents = await machine.Sql.ReadCatalogAsync(catalog.Target, settings.Get("ControlPlane__Bootstrap__AdminUsername"), ct).ConfigureAwait(false);
        }

        var data = fileExists ? await SideAsync("OSDU_DATA_DB", settings.Get("OSDU_DATA_DB"), machine, catalog, ct).ConfigureAwait(false) : null;

        var repositories = Repositories(settings, contents);
        IReadOnlyList<ReferenceUse>? references = null;
        var documents = 0;
        if (fileExists)
        {
            (references, documents) = await ReferencesAsync(repositories.Where(r => r.Enabled && r.FolderExists && r.Folder is not null), ct).ConfigureAwait(false);
        }

        OsduSignInResult? osdu = null;
        string? notTested = null;
        if (testOsdu)
        {
            (osdu, notTested) = await OsduAsync(settings, machine, ct).ConfigureAwait(false);
        }

        return new DevFacts(
            advice, template, fileExists, file, settings, machine.Environment, gui, tools, instances, docker, catalog, contents, data,
            repositories, references, documents, osdu, notTested);
    }

    private static async Task<ToolFacts> ToolsAsync(DevCheckout checkout, DevMachine machine, DevSettings settings, CancellationToken ct)
    {
        var needed = AzureNeeded(settings);
        var located = DotnetLocator.Find(machine);
        var offPath = located is not null && machine.Tools.Find("dotnet") is null ? located : null;
        var dotnet = machine.Tools.RunAsync(located ?? "dotnet", ["--version"], new ToolRun(WorkingDirectory: checkout.Root), ct);
        var node = machine.Tools.RunAsync("node", ["--version"], new ToolRun(), ct);
        var account = needed
            ? machine.Tools.RunAsync("az", ["account", "show", "--query", "user.name", "--output", "tsv"], new ToolRun(), ct)
            : Task.FromResult<ToolResult>(ToolResult.NotFound);
        await Task.WhenAll(dotnet, node, account).ConfigureAwait(false);
        var npm = machine.Tools.Find("npm") is not null;
        return new ToolFacts(await dotnet.ConfigureAwait(false), await node.ConfigureAwait(false), npm, needed, needed ? await account.ConfigureAwait(false) : null, offPath);
    }

    /// <summary>
    /// True when the settings read Azure through the Azure CLI: SQLFLOW_AZURE_AUTH names it (cli, azurecli, azlogin), or a value
    /// names a Key Vault secret under the default credential chain, of which the Azure CLI is part off Azure. A service
    /// principal (sp) or a managed identity (mi) needs no az login.
    /// </summary>
    public static bool AzureNeeded(DevSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var mode = (settings.Get("SQLFLOW_AZURE_AUTH") ?? string.Empty).Trim().ToLowerInvariant();
        if (mode is "cli" or "azurecli" or "azlogin")
        {
            return true;
        }

        var vault = settings.Values.Values.Any(v => v.Contains("${keyvault:", StringComparison.Ordinal));
        return vault && mode is not ("serviceprincipal" or "sp" or "managedidentity" or "mi" or "msi");
    }

    /// <summary>
    /// The database <paramref name="text"/> names, tested when it can be: a reference, a string the client cannot read or one
    /// that says no way to sign in is not. A data database on the catalog's server when that server did not answer is not
    /// tested again.
    /// </summary>
    private static async Task<DatabaseSide?> SideAsync(string setting, string? text, DevMachine machine, DatabaseSide? catalog, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text) || DevValues.IsPlaceholder(text) || DevValues.IsReference(text))
        {
            return null;
        }

        var target = SqlTarget.TryParse(text);
        if (target is null)
        {
            return null;
        }

        if (target.HasReference)
        {
            return new DatabaseSide(setting, target, "it names a secret reference, which the hosts resolve", null, null);
        }

        if (!target.SaysHowToSignIn)
        {
            return new DatabaseSide(setting, target, "it says no way to sign in (Integrated Security, User ID or Authentication)", null, null);
        }

        if (target.Database.Length == 0)
        {
            return new DatabaseSide(setting, target, "it names no Database", null, null);
        }

        if (catalog?.Server is { Answered: false } down && string.Equals(catalog.Target.ServerKey, target.ServerKey, StringComparison.Ordinal))
        {
            return new DatabaseSide(setting, target, null, down, null);
        }

        var server = await machine.Sql.ProbeServerAsync(target, ct).ConfigureAwait(false);
        var database = server.Answered ? await machine.Sql.ProbeDatabaseAsync(target, target.Database, ct).ConfigureAwait(false) : null;
        return new DatabaseSide(setting, target, null, server, database);
    }

    /// <summary>The repositories the env file configures and the catalog holds, the configured first.</summary>
    public static IReadOnlyList<RepositoryScan> Repositories(DevSettings settings, CatalogFacts? contents)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var scans = new List<RepositoryScan>();
        var names = settings.Values.Keys
            .Select(k => RepositorySetting().Match(k))
            .Where(m => m.Success && m.Groups["key"].Value is "LocalPath" or "RemoteUrl")
            .Select(m => m.Groups["name"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var local = settings.Get($"ControlPlane__Bootstrap__Repositories__{name}__LocalPath") ?? string.Empty;
            var remote = settings.Get($"ControlPlane__Bootstrap__Repositories__{name}__RemoteUrl") ?? string.Empty;
            var registered = contents?.Repositories.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)) == true;
            var lastError = contents?.Repositories.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))?.LastError ?? string.Empty;
            scans.Add(Scan(name, local, remote, enabled: true, lastError, configured: true, registered));
        }

        foreach (var repository in contents?.Repositories ?? [])
        {
            if (!scans.Any(s => string.Equals(s.Name, repository.Name, StringComparison.OrdinalIgnoreCase)))
            {
                scans.Add(Scan(repository.Name, repository.LocalPath, repository.RemoteUrl, repository.Enabled, repository.LastError, configured: false, registered: true));
            }
        }

        return scans;
    }

    private static RepositoryScan Scan(string name, string local, string remote, bool enabled, string lastError, bool configured, bool registered)
    {
        var folder = FolderOf(local, remote);
        var exists = folder is not null && Directory.Exists(folder);
        return new RepositoryScan(name, local.Length > 0 ? local : remote, folder, exists, enabled, lastError, configured, registered);
    }

    /// <summary>
    /// The folder a repository's flows are read from on this machine: its local path, or a remote that is a path here (a
    /// folder, or a file:// URL), else null for a remote reached over the network.
    /// </summary>
    public static string? FolderOf(string local, string remote)
    {
        if (!string.IsNullOrWhiteSpace(local))
        {
            return local;
        }

        if (string.IsNullOrWhiteSpace(remote))
        {
            return null;
        }

        if (Uri.TryCreate(remote, UriKind.Absolute, out var uri))
        {
            return uri.IsFile ? uri.LocalPath : null;
        }

        // user@host:path is git's scp form, a network remote; anything else that is not a URL is a path.
        return ScpRemote().IsMatch(remote) ? null : remote;
    }

    /// <summary>
    /// Every <c>${env:NAME}</c> the YAML documents of <paramref name="repositories"/> name, outside full-line comments, with
    /// the documents that name it. Folders a build or a package manager owns are not read, nor a document over 4 MB, and a
    /// repository is read up to 5,000 documents.
    /// </summary>
    public static async Task<(IReadOnlyList<ReferenceUse> Uses, int Documents)> ReferencesAsync(IEnumerable<RepositoryScan> repositories, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repositories);
        var uses = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        var documents = 0;
        foreach (var repository in repositories)
        {
            var root = Path.GetFullPath(repository.Folder!);
            var read = 0;
            foreach (var path in YamlDocuments(root))
            {
                if (read >= MaxDocuments)
                {
                    break;
                }

                var relative = Path.GetRelativePath(root, path);
                read++;
                string[] lines;
                try
                {
                    lines = await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var line in lines)
                {
                    if (line.TrimStart().StartsWith('#'))
                    {
                        continue;
                    }

                    foreach (Match match in EnvReference().Matches(line))
                    {
                        var name = match.Groups["name"].Value;
                        var document = $"{repository.Name}:{relative}";
                        if (!uses.TryGetValue(name, out var list))
                        {
                            uses[name] = list = [];
                        }

                        if (!list.Contains(document, StringComparer.Ordinal))
                        {
                            list.Add(document);
                        }
                    }
                }
            }

            documents += read;
        }

        return ([.. uses.Select(u => new ReferenceUse(u.Key, u.Value))], documents);
    }

    /// <summary>
    /// The YAML documents under <paramref name="root"/>, up to 4 MB each, without descending into a folder a tool owns
    /// (.git, node_modules, bin, obj), a system folder, or a link (which may lead out of the repository or round).
    /// </summary>
    private static IEnumerable<string> YamlDocuments(string root)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
        var folders = new Stack<string>();
        folders.Push(root);
        while (folders.Count > 0)
        {
            var folder = folders.Pop();
            IEnumerable<string> files;
            IEnumerable<string> children;
            try
            {
                files = [.. Directory.EnumerateFiles(folder, "*", options)];
                children = [.. Directory.EnumerateDirectories(folder, "*", options)];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files.Order(StringComparer.Ordinal))
            {
                var extension = Path.GetExtension(file);
                if ((extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase) || extension.Equals(".yml", StringComparison.OrdinalIgnoreCase))
                    && new FileInfo(file).Length <= MaxDocumentBytes)
                {
                    yield return file;
                }
            }

            foreach (var child in children.OrderDescending(StringComparer.Ordinal))
            {
                if (Path.GetFileName(child) is not (".git" or "node_modules" or "bin" or "obj"))
                {
                    folders.Push(child);
                }
            }
        }
    }

    private static async Task<(OsduSignInResult? Result, string? NotTested)> OsduAsync(DevSettings settings, DevMachine machine, CancellationToken ct)
    {
        var missing = ClientSettings.Where(n => !settings.IsSet(n)).ToList();
        if (missing.Count > 0)
        {
            return (null, $"not tested: {string.Join(", ", missing)} not set");
        }

        if (ClientSettings.Any(n => DevValues.IsReference(settings.Get(n))))
        {
            return (null, "not tested: the client's settings name references, which a run resolves");
        }

        if (!Uri.TryCreate(settings.Get("OSDU_TOKEN_URL"), UriKind.Absolute, out var tokenUrl))
        {
            return (null, "not tested: OSDU_TOKEN_URL is not a URL");
        }

        Uri? platform = settings.IsSet("OSDU_URL") && Uri.TryCreate(settings.Get("OSDU_URL"), UriKind.Absolute, out var url) ? url : null;
        var partition = settings.IsSet("OSDU_DATA_PARTITION") ? settings.Get("OSDU_DATA_PARTITION") : null;
        var tag = settings.IsSet("OSDU_LEGAL_TAG") ? settings.Get("OSDU_LEGAL_TAG") : null;
        var result = await machine.Osdu.TestAsync(
            tokenUrl, settings.Get("OSDU_CLIENT_ID")!, settings.Get("OSDU_CLIENT_SECRET")!, settings.Get("OSDU_SCOPE")!, platform, partition, tag, ct).ConfigureAwait(false);
        return (result, null);
    }

    [GeneratedRegex(@"^ControlPlane__Bootstrap__Repositories__(?<name>[A-Za-z0-9_]+?)__(?<key>LocalPath|RemoteUrl|Branch|SyncIntervalSeconds|CredentialReference|CredentialUsername)$", RegexOptions.CultureInvariant)]
    internal static partial Regex RepositorySetting();

    [GeneratedRegex(@"\$\{env:(?<name>[A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex EnvReference();

    [GeneratedRegex(@"^[^@\s/\\]+@[^:\s/\\]+:", RegexOptions.CultureInvariant)]
    private static partial Regex ScpRemote();
}
