using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlFlow.Azure;
using SqlFlow.Catalog;
using SqlFlow.Cli.Remote;
using SqlFlow.Core;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;
using SqlFlow.Execution;
using SqlFlow.Node;
using SqlFlow.Yaml;

namespace SqlFlow.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var verbose = args.Any(a => a is "-v" or "--verbose");
        var positional = PositionalArguments(args);

        // 'auth' is a pure environment check, 'db' takes a subcommand (migrate/sync/status), and the control-plane
        // verbs (health/login/logout/trigger/runs/groups and the estate family) address the remote API through
        // flags; none take a pipeline file.
        var command = positional.Length > 0 ? positional[0].ToLowerInvariant() : string.Empty;
        var needsFile = command is not ("auth" or "db" or "worker" or "runs" or "user"
            or "health" or "login" or "logout" or "trigger" or "groups"
            or "whoami" or "doctor" or "summary" or "nodes" or "schedules" or "repos" or "pipelines"
            or "search" or "completions");
        if (positional.Length < (needsFile ? 2 : 1) || args.Any(a => a is "-h" or "--help"))
        {
            PrintUsage();
            return positional.Length < (needsFile ? 2 : 1) ? 1 : 0;
        }

        var file = positional.Length > 1 ? positional[1] : string.Empty;

        // The git-ignored .sqlflow/env file supplies local-development secrets for the ${env:...} references in
        // flow documents; the process environment (CI, Kubernetes, the scheduler) always wins. Searched from the
        // flow document's directory upward, before anything resolves, for every command.
        try
        {
            var envAnchor = file.Length > 0 && File.Exists(file)
                ? Path.GetDirectoryName(Path.GetFullPath(file)) ?? Directory.GetCurrentDirectory()
                : file.Length > 0 && Directory.Exists(file)
                    ? Path.GetFullPath(file)
                    : Directory.GetCurrentDirectory();
            var (appliedEnv, envFile) = LocalEnvFile.ApplyNearest(envAnchor);
            if (envFile is not null && verbose)
            {
                Console.Error.WriteLine($"env: applied {appliedEnv.Count} variable(s) from {envFile}");
            }
        }
        catch (SqlFlowException ex)
        {
            Console.Error.WriteLine($"ERROR  {SecretHygiene.RedactedMessage(ex)}");
            return 1;
        }

        using var provider = BuildServiceProvider(verbose, json: args.Contains("--json"));
        var documents = provider.GetRequiredService<YamlDocumentLoader>();

        try
        {
            switch (command)
            {
                case "validate":
                {
                    // A directory validates the whole estate in one pass (the CI gate), and --json switches
                    // either shape to the machine-readable report; a single file keeps its detailed line.
                    if (Directory.Exists(file) || args.Contains("--json"))
                    {
                        return await LocalInspectVerbs.ValidateEstateAsync(documents, file, args.Contains("--json")).ConfigureAwait(false);
                    }

                    var document = DocumentLoader.Load(documents, file, Console.Error.WriteLine);
                    var endpoints = document.SourceReference is null && document.TargetReference is null
                        ? string.Empty
                        : $": {document.SourceReference ?? "-"} -> {document.TargetReference ?? "-"}";
                    Console.WriteLine($"OK  '{document.Name}' is valid ({document.Kind}{endpoints}).");
                    return 0;
                }

                case "run":
                {
                    var json = args.Contains("--json");
                    var loaded = DocumentLoader.Load(documents, file, Console.Error.WriteLine);

                    // Ctrl+C aborts the in-flight run rather than killing the process: intercept it and trip the
                    // token the engine threads through every operation, then report a clean interrupted exit (130,
                    // the conventional SIGINT code).
                    using var cts = new CancellationTokenSource();
                    Console.CancelKeyPress += (_, eventArgs) =>
                    {
                        eventArgs.Cancel = true;
                        cts.Cancel();
                    };

                    RunParameters parameters;
                    try
                    {
                        parameters = ParseRunParameters(args);
                    }
                    catch (SqlFlowException ex)
                    {
                        Console.Error.WriteLine($"ERROR  {ex.Message}");
                        return 1;
                    }

                    var options = new DocumentExecutionOptions
                    {
                        LogLevel = ParseLogLevel(GetOption(args, "--log-level")),
                        Echo = json ? null : Console.WriteLine,
                        Parameters = parameters,
                    };

                    DocumentExecutionResult exec;
                    try
                    {
                        exec = await provider.GetRequiredService<DocumentExecutor>()
                            .ExecuteAsync(loaded, file, options, cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cts.IsCancellationRequested)
                    {
                        Console.Error.WriteLine("CANCELLED  the run was interrupted (Ctrl+C); the in-flight work was aborted.");
                        return 130;
                    }

                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(exec.Result, ExecutionJson.Options));
                    }
                    else
                    {
                        PrintExecution(exec);
                    }

                    await RecordRunsInCatalogAsync(provider, args, json, file, [(file, exec.RunDirectory)]).ConfigureAwait(false);
                    return exec.Success ? 0 : 1;
                }

                case "auth":
                    return await RunAuthCheckAsync(provider, args).ConfigureAwait(false);

                case "db":
                    return await RunDbAsync(provider, positional, args).ConfigureAwait(false);

                case "worker":
                    return await RunWorkerAsync(provider, args, verbose).ConfigureAwait(false);

                case "runs":
                {
                    // 'runs local' reads the on-disk artifacts (no catalog, no control plane); 'runs cancel'
                    // keeps its direct-catalog path as the break-glass route (it works when the control plane
                    // itself is down): used when --db is passed explicitly or when no control plane is
                    // configured. Everything else on 'runs' (list/show/trace, and cancel with a configured
                    // URL) goes through the control plane, the same API the GUI uses.
                    var runsSub = positional.Length > 1 ? positional[1].ToLowerInvariant() : string.Empty;
                    if (runsSub == "local")
                    {
                        return await LocalInspectVerbs.ListLocalRunsAsync(positional, args).ConfigureAwait(false);
                    }

                    if (runsSub == "cancel" && (GetOption(args, "--db") is not null || !RemoteVerbs.IsConfigured(args)))
                    {
                        return await RunRunsAsync(provider, positional, args).ConfigureAwait(false);
                    }

                    return await RemoteVerbs.RunsAsync(positional, args).ConfigureAwait(false);
                }

                case "health":
                    return await RemoteVerbs.HealthAsync(args).ConfigureAwait(false);

                case "login":
                    return await RemoteVerbs.LoginAsync(args).ConfigureAwait(false);

                case "logout":
                    return await RemoteVerbs.LogoutAsync(args).ConfigureAwait(false);

                case "trigger":
                    return await RemoteVerbs.TriggerAsync(args).ConfigureAwait(false);

                case "groups":
                    return await RemoteVerbs.GroupsAsync(positional, args).ConfigureAwait(false);

                case "whoami":
                    return await RemoteVerbs.WhoAmIAsync(args).ConfigureAwait(false);

                case "doctor":
                    return await RemoteVerbs.DoctorAsync(args).ConfigureAwait(false);

                case "summary":
                    return await RemoteVerbs.SummaryAsync(args).ConfigureAwait(false);

                case "nodes":
                    return await RemoteVerbs.NodesAsync(args).ConfigureAwait(false);

                case "schedules":
                    return await RemoteVerbs.SchedulesAsync(positional, args).ConfigureAwait(false);

                case "repos":
                    return await RemoteVerbs.ReposAsync(positional, args).ConfigureAwait(false);

                case "pipelines":
                    return await RemoteVerbs.PipelinesAsync(positional, args).ConfigureAwait(false);

                case "search":
                    return await RemoteVerbs.SearchAsync(positional, args).ConfigureAwait(false);

                case "completions":
                    return CliCompletions.Print(positional);

                case "user":
                    return await RunUserAsync(provider, positional, args).ConfigureAwait(false);

                default:
                    Console.Error.WriteLine($"Unknown command '{command}'.");
                    PrintUsage();
                    return 1;
            }
        }
        catch (SqlFlowException ex)
        {
            Console.Error.WriteLine($"ERROR  {SecretHygiene.RedactedMessage(ex)}");
            return 1;
        }
    }

    /// <summary>
    /// Verifies Azure authentication end to end: 'sqlflow auth [--scope storage|keyvault|arm|&lt;uri&gt;]'. It reports
    /// the resolved mode (from SQLFLOW_AZURE_AUTH) and actually acquires a token for the chosen scope through the
    /// one credential factory every Azure path uses, so an operator can confirm service principal / managed
    /// identity / az-login works in THIS environment before running a cloud flow. Exit 0 on a token, 1 otherwise.
    /// </summary>
    private static async Task<int> RunAuthCheckAsync(IServiceProvider provider, string[] args)
    {
        var scopeArg = (GetOption(args, "--scope") ?? "storage").Trim();
        var scope = scopeArg.ToLowerInvariant() switch
        {
            "storage" or "adls" or "blob" => "https://storage.azure.com/.default",
            "keyvault" or "vault" => "https://vault.azure.net/.default",
            "arm" or "management" => "https://management.azure.com/.default",
            _ => scopeArg, // a verbatim scope URI
        };

        // Show both what the operator set and the mode it resolves to (normalized via the one resolver every Azure
        // path uses), plus whether managed identity is in play (the dev-PC vs in-Azure distinction).
        var raw = Environment.GetEnvironmentVariable("SQLFLOW_AZURE_AUTH");
        var resolved = AzureAuth.Mode();
        var resolvedLabel = resolved == CloudAuthMode.DefaultChain
            ? $"default chain ({(AzureEnvironment.IsRunningInAzure() ? "managed identity -> az CLI -> env" : "az CLI -> env; managed identity excluded off-cloud")})"
            : resolved.ToString();
        Console.WriteLine($"SQLFLOW_AZURE_AUTH: {(string.IsNullOrWhiteSpace(raw) ? "(unset)" : raw)} -> {resolvedLabel}");
        Console.WriteLine($"Acquiring a token for scope: {scope}");

        try
        {
            var credential = provider.GetRequiredService<IAzureCredentialFactory>().Create();
            var token = await credential.GetTokenAsync(new global::Azure.Core.TokenRequestContext([scope]), CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine($"OK   acquired a token (expires {token.ExpiresOn:u} UTC). Azure auth works.");
            return 0;
        }
        catch (Exception ex)
        {
            // A diagnostic verb: any failure to acquire a token is the answer the operator asked for, surfaced
            // with the underlying cause rather than a stack trace.
            Console.Error.WriteLine($"FAIL could not acquire a token: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Runs this host as a self-hosted compute node: <c>sqlflow worker [--db &lt;ref&gt;] [--poll-seconds N]
    /// [--drain-seconds N]</c>. It drains the durable run queue in the shadow catalog - atomically claiming queued
    /// runs, executing them through the same engine a direct CLI run uses, and recording each outcome under the id
    /// the trigger returned - so a node inside a private network runs the flows the control plane queued without
    /// the control plane ever reaching the node. The queue's atomic claim makes any number of workers safe to run
    /// at once. Every credential is resolved from THIS node's own environment, so nothing sensitive travels
    /// through the queue. Runs until a stop signal (Ctrl+C, or the SIGTERM an orchestrator sends when it reclaims
    /// the replica), which stops claiming and then DRAINS: the runs already in flight keep executing and record
    /// their own outcomes, for up to <c>--drain-seconds</c>.
    /// </summary>
    private static async Task<int> RunWorkerAsync(IServiceProvider provider, string[] args, bool verbose)
    {
        var reference = GetOption(args, "--db") ?? "${env:SQLFLOW_CATALOG_DB}";
        string catalogConnection;
        try
        {
            catalogConnection = provider.GetRequiredService<ISecretResolver>().Resolve(reference);
        }
        catch (SqlFlowException ex)
        {
            Console.Error.WriteLine($"ERROR  {SecretHygiene.RedactedMessage(ex)}");
            return 1;
        }

        var pollSeconds = Math.Max(1, ParseIntOption(args, 5, "--poll-seconds"));
        // How long a stopping node keeps finishing the runs it already claimed before severing them. It must stay
        // under the orchestrator's termination grace period, or the platform's kill lands mid-drain and severs the
        // work anyway; zero severs at once.
        var drainSeconds = Math.Max(0, ParseIntOption(args, (int)RunWorker.DefaultDrainTimeout.TotalSeconds, "--drain-seconds"));
        // The pools this node serves (comma-separated). Empty means it drains only untargeted runs.
        var pools = (GetOption(args, "--pool") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // A dedicated host for the node: the shared engine (so a worker run is byte-for-byte a CLI run), a scoped
        // catalog context per claim, and the shared RunWorker drain loop. The DocumentExecutor gets the stderr
        // warning sink exactly as the CLI's own runs do (a later registration wins over the engine's sink-less one).
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
                options.IncludeScopes = true;
            });
            // Diagnostics on stderr, like every CLI path.
            builder.Services.Configure<Microsoft.Extensions.Logging.Console.ConsoleLoggerOptions>(
                options => options.LogToStandardErrorThreshold = LogLevel.Trace);
            builder.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information);
        });
        AddCliEngine(services);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped(_ => CatalogDatabase.Create(catalogConnection));
        services.AddSingleton<RunWorker>();
        await using var workerProvider = services.BuildServiceProvider();

        var worker = workerProvider.GetRequiredService<RunWorker>();
        using var cts = new CancellationTokenSource();

        // Stop signals. SIGTERM is the one that matters in production: it is what an autoscaler reclaiming this
        // replica, a revision swap, or a `docker stop` sends, and .NET's DEFAULT handling of it terminates the
        // process immediately. Handling the signal (Cancel = true suppresses the default termination) hands control
        // back here, where the worker stops claiming and drains what it holds within the orchestrator's termination
        // grace period. SIGINT is the interactive Ctrl+C and drains the same way.
        var stopRequested = 0;
        void RequestStop(PosixSignalContext context)
        {
            // A SECOND signal means the sender is not willing to wait out the drain (an impatient operator, or an
            // orchestrator escalating). Leave Cancel false so the runtime terminates as it normally would: the
            // severed runs stay 'running' and the reaper requeues them, which is the honest outcome of refusing the
            // drain, and it keeps a worker from ever feeling unkillable.
            if (Interlocked.Exchange(ref stopRequested, 1) != 0)
            {
                return;
            }

            context.Cancel = true;
            cts.Cancel();
        }

        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, RequestStop);
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, RequestStop);

        var poolLabel = pools.Length > 0 ? string.Join(", ", pools) : "untargeted runs only";
        Console.WriteLine($"Worker '{worker.NodeName}' draining the run queue (poll {pollSeconds}s, pools: {poolLabel}, drain {drainSeconds}s). Press Ctrl+C to stop, again to stop without draining.");
        try
        {
            // An operator restart request (observed on the heartbeat) trips the same token a stop signal does: the
            // loop stops claiming, drains its in-flight work, and returns, the process exits cleanly, and the
            // orchestrator recreates the replica.
            await worker.RunAsync(
                TimeSpan.FromSeconds(pollSeconds), pools, (timeout, ct) => Task.Delay(timeout, ct), cts.Token,
                onRestartRequested: _ =>
                {
                    cts.Cancel();
                    return Task.CompletedTask;
                },
                drainTimeout: TimeSpan.FromSeconds(drainSeconds)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A stop signal or an honored restart request: a clean stop.
        }

        Console.WriteLine("Worker stopped.");
        return 0;
    }

    /// <summary>
    /// Operations against runs in the shadow catalog's durable queue: <c>sqlflow runs cancel &lt;runId&gt; [--db
    /// &lt;ref&gt;]</c>. Cancelling honors the run's lifecycle exactly as the control plane does (it shares
    /// <see cref="RunQueueStore.CancelAsync"/>): a still-queued run is dequeued outright; a run already executing has a
    /// durable cancel request stamped for its owning node to observe, abort the in-flight work, and record
    /// cancelled. Like the <c>worker</c> and <c>db</c> verbs it talks to the catalog directly (no control-plane HTTP
    /// hop), so it works from any host that can reach the catalog database; the connection is a reference (default
    /// <c>${env:SQLFLOW_CATALOG_DB}</c>), never an embedded secret.
    /// </summary>
    private static async Task<int> RunRunsAsync(IServiceProvider provider, string[] positional, string[] args)
    {
        var sub = positional.Length > 1 ? positional[1].ToLowerInvariant() : string.Empty;
        if (sub != "cancel")
        {
            Console.Error.WriteLine("ERROR  'runs' supports: cancel <runId>. Usage: sqlflow runs cancel <runId> [--db <conn-ref>]");
            return 1;
        }

        if (positional.Length < 3 || !Guid.TryParse(positional[2], out var runId))
        {
            Console.Error.WriteLine("ERROR  'runs cancel' requires a run id: sqlflow runs cancel <runId> [--db <conn-ref>]");
            return 1;
        }

        if (ResolveCatalogConnection(provider, args) is not { } connectionString)
        {
            return 1;
        }

        // EF Core throws SqlException / InvalidOperationException (not SqlFlowException) on a bad connection or
        // permission; catch them here so the verb reports a clean, redacted message instead of a stack trace.
        try
        {
            await using var db = CatalogDatabase.Create(connectionString);
            var outcome = await RunQueueStore.CancelAsync(db, runId, DateTime.UtcNow).ConfigureAwait(false);
            switch (outcome)
            {
                case CancelOutcome.Cancelled:
                    Console.WriteLine($"OK   run {runId} was queued and is now cancelled.");
                    return 0;
                case CancelOutcome.CancelRequested:
                    Console.WriteLine(
                        $"OK   run {runId} is running; cancellation requested. Its node will abort the in-flight work " +
                        "and record it cancelled.");
                    return 0;
                case CancelOutcome.NotFound:
                    Console.Error.WriteLine($"ERROR  no run '{runId}'.");
                    return 1;
                default:
                    Console.Error.WriteLine($"ERROR  run '{runId}' has already finished and cannot be cancelled.");
                    return 1;
            }
        }
        catch (Exception ex) when (ex is not SqlFlowException)
        {
            Console.Error.WriteLine($"ERROR  {SecretHygiene.RedactedMessage(ex)}");
            return 1;
        }
    }

    /// <summary>
    /// Local-user administration straight against the shadow catalog: <c>sqlflow user reset-password &lt;username&gt;
    /// [--db &lt;ref&gt;]</c>. Like <c>db</c>, <c>worker</c>, and <c>runs</c>, it talks to the catalog directly (no
    /// control-plane HTTP hop, no bearer token), so it works from any host that can reach the catalog database and
    /// needs only database access, not a running control plane. That makes it the recovery path when no admin can
    /// sign in, and it removes the reason to keep a break-glass bootstrap secret enabled: the new password is read
    /// from a hidden interactive prompt (never a flag, so it stays out of shell history) and hashed with the exact
    /// same hasher the control plane verifies against.
    /// </summary>
    private static async Task<int> RunUserAsync(IServiceProvider provider, string[] positional, string[] args)
    {
        var sub = positional.Length > 1 ? positional[1].ToLowerInvariant() : string.Empty;
        if (sub != "reset-password")
        {
            Console.Error.WriteLine(
                "ERROR  'user' supports: reset-password <username>. Usage: sqlflow user reset-password <username> [--db <conn-ref>]");
            return 1;
        }

        if (positional.Length < 3 || string.IsNullOrWhiteSpace(positional[2]))
        {
            Console.Error.WriteLine("ERROR  'user reset-password' requires a username: sqlflow user reset-password <username> [--db <conn-ref>]");
            return 1;
        }

        var username = positional[2];
        if (ResolveCatalogConnection(provider, args) is not { } connectionString)
        {
            return 1;
        }

        // EF Core throws SqlException / InvalidOperationException (not SqlFlowException) on a bad connection or
        // permission; catch them here so the verb reports a clean, redacted message instead of a stack trace.
        try
        {
            await using var db = CatalogDatabase.Create(connectionString);
            var user = await UserStore.FindByUsernameAsync(db, username).ConfigureAwait(false);
            if (user is null)
            {
                Console.Error.WriteLine($"ERROR  no user '{username}' in the catalog.");
                return 1;
            }

            // Only local users hold a password here; an SSO (Entra) user's credential lives in the external
            // provider, so refuse before prompting rather than after. (UserStore.SetPasswordHashAsync enforces the
            // same rule as a backstop.)
            if (user.Provider != UserProviders.Local)
            {
                Console.Error.WriteLine(
                    $"ERROR  user '{user.Username}' signs in through '{user.Provider}', not a local password; reset it in that provider.");
                return 1;
            }

            if (!user.Active)
            {
                Console.Error.WriteLine(
                    $"NOTE  user '{user.Username}' is currently deactivated; the reset succeeds but they cannot sign in until reactivated.");
            }

            var password = PromptForNewPassword();
            if (password is null)
            {
                // The prompt already explained why (mismatch, too short, or empty); treat as a clean abort.
                return 1;
            }

            var hash = LocalPasswords.Hash(user, password);
            var nowUtc = DateTime.UtcNow;
            var outcome = await UserStore.SetPasswordHashAsync(db, user.Id, hash, nowUtc).ConfigureAwait(false);
            switch (outcome)
            {
                case UserMutation.Applied:
                    Console.WriteLine($"OK   password reset for '{user.Username}' ({user.Role}).");
                    // A break-glass credential change is exactly what enterprise policy expects audited. There is no
                    // catalog audit table (the API's own reset writes none either), so emit a structured, secret-free
                    // record of who/what/when/where to the console the operator's session capture (PAM/bastion) logs.
                    // DescribeTarget is secret-free (server + database only); the password never appears anywhere.
                    Console.WriteLine(
                        $"     audit: '{user.Username}' ({user.Role}) reset by {Environment.UserName}@{Environment.MachineName} " +
                        $"on {CatalogDatabase.DescribeTarget(connectionString)} at {nowUtc.ToString("O", CultureInfo.InvariantCulture)}.");
                    return 0;
                case UserMutation.NotFound:
                    // The row vanished between lookup and update (an admin deleted the account through the API, or
                    // it was removed in the database directly); report it plainly.
                    Console.Error.WriteLine($"ERROR  user '{user.Username}' no longer exists.");
                    return 1;
                case UserMutation.NotLocal:
                    Console.Error.WriteLine($"ERROR  user '{user.Username}' is not a local-password account; nothing was changed.");
                    return 1;
                default:
                    Console.Error.WriteLine($"ERROR  password reset for '{user.Username}' did not apply.");
                    return 1;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not SqlFlowException)
        {
            Console.Error.WriteLine($"ERROR  user 'reset-password' failed: {SecretHygiene.RedactedMessage(ex)}");
            return 1;
        }
    }

    /// <summary>
    /// Reads a new local password without echoing it, then confirms it, returning null (after printing why) on any
    /// rejection so the caller aborts cleanly. When stdin is redirected (piped automation) a single line is read
    /// instead: there is no terminal to mask or to confirm against, so the one line is taken as-is and still
    /// length-checked. The value never touches a command-line flag, so it stays out of shell history either way.
    /// </summary>
    private static string? PromptForNewPassword()
    {
        if (Console.IsInputRedirected)
        {
            var piped = Console.ReadLine();
            if (string.IsNullOrEmpty(piped) || piped.Length < LocalPasswords.MinLength)
            {
                Console.Error.WriteLine($"ERROR  the password must be at least {LocalPasswords.MinLength} characters.");
                return null;
            }

            return piped;
        }

        Console.Write("New password: ");
        var password = CliConsole.ReadHiddenLine();
        if (password.Length < LocalPasswords.MinLength)
        {
            Console.Error.WriteLine($"ERROR  the password must be at least {LocalPasswords.MinLength} characters.");
            return null;
        }

        Console.Write("Confirm password: ");
        var confirm = CliConsole.ReadHiddenLine();
        if (!string.Equals(password, confirm, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("ERROR  the two entries did not match; nothing was changed.");
            return null;
        }

        return password;
    }

    /// <summary>
    /// The shadow-catalog (database mode) operations: <c>sqlflow db migrate|sync|status [path] [--db &lt;ref&gt;]</c>.
    /// The catalog is an EF-managed read-model of the git/YAML estate and the on-disk run history; git stays the
    /// source of truth. <c>migrate</c> upgrades an existing catalog to the current schema version, and with
    /// <c>--create</c> provisions a new one (create the database, or initialise the catalog in an empty database);
    /// without <c>--create</c> a missing or non-catalog database is refused. <c>sync</c> projects the estate +
    /// run.json artifacts into an existing catalog (upgrading it first); <c>status</c> reports applied vs pending
    /// migrations. The connection is a reference (default <c>${env:SQLFLOW_CATALOG_DB}</c>), never an embedded secret.
    /// </summary>
    private static async Task<int> RunDbAsync(IServiceProvider provider, string[] positional, string[] args)
    {
        var sub = positional.Length > 1 ? positional[1].ToLowerInvariant() : string.Empty;
        if (ResolveCatalogConnection(provider, args) is not { } connectionString)
        {
            return 1;
        }

        // The catalog operations talk to SQL Server through EF Core, which throws SqlException / DbUpdateException
        // / InvalidOperationException (none are SqlFlowException) on a bad connection, missing permission, or a
        // failed migration. Catch them here so the verb reports a clean, redacted message instead of crashing.
        try
        {
            switch (sub)
            {
                case "migrate":
                {
                    // Creating the database (or initialising the catalog in an empty one) requires the explicit
                    // --create flag, so a mistyped --db can never silently provision the wrong database. Without
                    // it, migrate upgrades an EXISTING catalog only and refuses anything else.
                    if (args.Contains("--create"))
                    {
                        await CatalogDatabase.MigrateAsync(connectionString).ConfigureAwait(false);
                    }
                    else
                    {
                        await CatalogDatabase.MigrateExistingAsync(connectionString).ConfigureAwait(false);
                    }

                    var (applied, pending) = await CatalogDatabase.StatusAsync(connectionString).ConfigureAwait(false);
                    Console.WriteLine(
                        $"OK   catalog database current at '{(applied.Count > 0 ? applied[^1] : "(none)")}' " +
                        $"({applied.Count} migration(s) applied, {pending.Count} pending).");
                    return 0;
                }

                case "status":
                {
                    var (applied, pending) = await CatalogDatabase.StatusAsync(connectionString).ConfigureAwait(false);
                    Console.WriteLine($"catalog: {applied.Count} migration(s) applied, {pending.Count} pending.");
                    foreach (var migration in pending)
                    {
                        Console.WriteLine($"  pending: {migration}");
                    }

                    return pending.Count == 0 ? 0 : 2;
                }

                case "sync":
                {
                    var directory = positional.Length > 2 ? positional[2] : Directory.GetCurrentDirectory();
                    var repoName = GetOption(args, "--repo") is { Length: > 0 } r
                        ? r
                        : (new DirectoryInfo(Path.GetFullPath(directory)).Name is { Length: > 0 } folder ? folder : "default");
                    var repoUrl = GetOption(args, "--repo-url");
                    // Upgrade an existing catalog's schema first, so a sync just works. Never creates: point at an
                    // existing catalog, or provision one with 'sqlflow db migrate --create'.
                    await CatalogDatabase.MigrateExistingAsync(connectionString).ConfigureAwait(false);
                    await using var context = CatalogDatabase.Create(connectionString);
                    var sync = new CatalogSync(provider.GetRequiredService<YamlDocumentLoader>());
                    var result = await sync.SyncAsync(context, directory, repoName, repoUrl, DateTime.UtcNow).ConfigureAwait(false);
                    Console.WriteLine(
                        $"OK   synced '{directory}': pipelines +{result.PipelinesAdded} added, {result.PipelinesUpdated} updated, " +
                        $"{result.PipelinesUnchanged} unchanged, {result.PipelinesDeactivated} deactivated, {result.PipelinesDeleted} removed; " +
                        $"runs +{result.RunsAdded} added ({result.RunEventsAdded} events), {result.RunsSkipped} known, {result.RunsFailed} unreadable.");
                    foreach (var warning in result.Warnings.Take(20))
                    {
                        Console.Error.WriteLine($"WARN  {warning}");
                    }

                    return 0;
                }

                default:
                    Console.Error.WriteLine("Usage: sqlflow db <migrate|sync|status> [path] [--db <conn-ref>] [--create]");
                    return 1;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"ERROR  catalog '{sub}' failed: {SecretHygiene.RedactedMessage(ex)}");
            return 1;
        }
    }

    /// <summary>Resolves the catalog connection from <c>--db</c> (default <c>${env:SQLFLOW_CATALOG_DB}</c>), warning
    /// when a literal credential was passed on the command line. Null (after printing the error) when it cannot.</summary>
    private static string? ResolveCatalogConnection(IServiceProvider provider, string[] args)
    {
        var reference = GetOption(args, "--db") ?? "${env:SQLFLOW_CATALOG_DB}";
        if (SecretHygiene.LooksLikeEmbeddedSecret(reference))
        {
            Console.Error.WriteLine(
                "WARN  --db embeds a credential on the command line (it lands in shell history). Prefer the canonical " +
                "${env:SQLFLOW_CATALOG_DB}, an explicit ${env:NAME} or ${keyvault:vault/secret} reference, with local " +
                "values in the git-ignored .sqlflow/env file.");
        }

        try
        {
            return provider.GetRequiredService<ISecretResolver>().Resolve(reference);
        }
        catch (SqlFlowException ex)
        {
            Console.Error.WriteLine($"ERROR  {SecretHygiene.RedactedMessage(ex)}");
            return null;
        }
    }

    /// <summary>
    /// The self-maintaining shadow: after a flow run, when a catalog database is configured (<c>--db</c> or
    /// <c>SQLFLOW_CATALOG_DB</c>), record the produced run(s) and their pipeline(s) into it automatically so
    /// database mode stays current without a manual <c>db sync</c>. Opt out with <c>--no-db-sync</c>. Best-effort:
    /// a write-back failure is reported as a warning and never changes the run's own exit code. The repo is
    /// <c>--repo</c> / <c>SQLFLOW_REPO</c> / the primary flow's folder name.
    /// </summary>
    private static async Task RecordRunsInCatalogAsync(
        IServiceProvider provider, string[] args, bool json, string primaryFlowFile,
        IReadOnlyList<(string FlowFile, string? RunDirectory)> runs)
    {
        if (args.Contains("--no-db-sync"))
        {
            return;
        }

        // In play ONLY when --db is passed or SQLFLOW_CATALOG_DB is set; otherwise the run is a pure file/YAML
        // operation and the catalog is simply absent (no error, no output).
        var reference = GetOption(args, "--db")
            ?? (Environment.GetEnvironmentVariable("SQLFLOW_CATALOG_DB") is { Length: > 0 } ? "${env:SQLFLOW_CATALOG_DB}" : null);
        if (reference is null)
        {
            return;
        }

        // Everything below (including path resolution) is inside the catch, so a write-back can never affect the
        // run's own outcome.
        try
        {
            var candidates = runs
                .Where(r => r.RunDirectory is not null)
                .Select(r => (r.FlowFile, RunJson: Path.Combine(r.RunDirectory!, "run.json")))
                .Where(r => File.Exists(r.RunJson))
                .ToList();
            if (candidates.Count == 0)
            {
                return;
            }

            var repoName = GetOption(args, "--repo")
                ?? (Environment.GetEnvironmentVariable("SQLFLOW_REPO") is { Length: > 0 } configured
                    ? configured
                    : new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(primaryFlowFile)) ?? ".").Name is { Length: > 0 } folder
                        ? folder
                        : "default");
            var repoUrl = GetOption(args, "--repo-url");

            var connectionString = provider.GetRequiredService<ISecretResolver>().Resolve(reference);
            // Upgrade an existing catalog's schema once, so a configured run's write-back just works. Never
            // creates: a mistyped SQLFLOW_CATALOG_DB must not silently conjure a catalog during a run.
            await CatalogDatabase.MigrateExistingAsync(connectionString).ConfigureAwait(false);

            var sync = new CatalogSync(provider.GetRequiredService<YamlDocumentLoader>());
            var recorded = 0;
            var warnings = new List<string>();
            foreach (var (flowFile, runJson) in candidates)
            {
                // A fresh context per run keeps the change tracker clean.
                await using var context = CatalogDatabase.Create(connectionString);
                var result = await sync.RecordRunAsync(context, flowFile, runJson, repoName, repoUrl, DateTime.UtcNow).ConfigureAwait(false);
                if (result.RunRecorded)
                {
                    recorded++;
                }

                warnings.AddRange(result.Warnings);
            }

            if (!json)
            {
                Console.WriteLine($"  catalog: {recorded} of {candidates.Count} run(s) recorded into [{repoName}].");
            }

            foreach (var warning in warnings.Take(10))
            {
                Console.Error.WriteLine($"WARN  {warning}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine(
                $"WARN  catalog write-back skipped ({SecretHygiene.RedactedMessage(ex)}); the run itself is unaffected. Run 'sqlflow db sync' to backfill.");
        }
    }

    private static ServiceProvider BuildServiceProvider(bool verbose, bool json)
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
                options.IncludeScopes = true;
            });
            // Diagnostics belong on stderr for a CLI: stdout carries the verb's own output (tables, YAML,
            // and especially --json payloads a script pipes into a parser).
            builder.Services.Configure<Microsoft.Extensions.Logging.Console.ConsoleLoggerOptions>(
                options => options.LogToStandardErrorThreshold = LogLevel.Trace);
            builder.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information);
        });

        AddCliEngine(services);
        if (json)
        {
            // With --json the engine's live event stream is re-aimed at stderr too, so stdout is exactly one
            // parsable JSON document.
            services.AddSingleton<IFlowEventSink>(new ConsoleFlowEventSink(Console.Error));
        }

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The engine as the CLI hosts it: the one shared composition every host uses (see
    /// <see cref="SqlFlowEngineServices.AddSqlFlowEngine"/>) plus the document kinds this build serves, with the
    /// <see cref="DocumentExecutor"/> re-registered so its hygiene/history warnings reach standard error (the
    /// extension registers it with no sink; a later registration of the same service wins).
    /// </summary>
    private static void AddCliEngine(IServiceCollection services)
    {
        services.AddSqlFlowEngine();
        services.AddSingleton(sp => new DocumentExecutor(sp, Console.Error.WriteLine));
    }

    private static void PrintExecution(DocumentExecutionResult exec)
    {
        Console.WriteLine(exec.Success
            ? $"OK  '{exec.FlowName}' ({exec.FlowKind}) succeeded in {exec.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture)}s"
            : $"FAILED  '{exec.FlowName}' ({exec.FlowKind}): {exec.Error}");
        if (exec.RunDirectory is not null)
        {
            Console.WriteLine($"  run log: {exec.RunDirectory}");
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            sqlflow - the OSDU Delivery command line
            Usage:
              sqlflow validate <flow.yaml|folder>  Validate a flow document, or every document under a folder
                               [--json]            (the CI gate: exit 0 only when every document is valid)
              sqlflow run      <flow.yaml>         Execute the flow (Ctrl+C aborts the in-flight work)
                               [--full] [--from <date>] [--to <date>] [--file-pattern <glob>]
                               [--log-level info|debug|trace] [--json] [--db <conn-ref>] [--no-db-sync]
              sqlflow auth     [--scope storage|keyvault|arm|<uri>]
                                                   Verify Azure auth in THIS environment: reports the resolved mode
                                                   (SQLFLOW_AZURE_AUTH: sp / mi / cli / default chain) and acquires a
                                                   token for the scope (default storage). Exit 0 on a token.
              sqlflow db <migrate|sync|status> [path] [--db <conn-ref>] [--create] [--repo name] [--repo-url url]
                                                   Database mode: the EF-managed shadow catalog (a read-model of the
                                                   git/YAML estate + run history, for the GUI). 'migrate' upgrades an
                                                   EXISTING catalog; add --create to provision a new one. 'sync'
                                                   projects the estate and run.json artifacts under [path] into it,
                                                   attributed to --repo (default: the folder name). 'status' lists
                                                   applied vs pending migrations. --db defaults to ${env:SQLFLOW_CATALOG_DB}.
              sqlflow worker   [--db <conn-ref>] [--poll-seconds N] [--pool a,b] [--drain-seconds N]
                                                   Run as a self-hosted compute node: drain the catalog's durable run
                                                   queue on THIS host, resolving every credential from this node's own
                                                   environment and recording each outcome. Runs until Ctrl+C or SIGTERM,
                                                   which stops claiming and lets in-flight runs finish (--drain-seconds,
                                                   default 540). --pool sets the pools this node serves.
              sqlflow runs cancel <runId> [--db <conn-ref>]
                                                   Cancel a run. With a control plane configured (--url/SQLFLOW_URL)
                                                   this goes through the API; with --db, or with no control plane
                                                   configured, it talks to the catalog directly (the break-glass route).
              sqlflow runs local [folder] [--flow name] [--last N] [--json]
                                                   The on-disk run history under .sqlflow/runs, newest first.
              sqlflow user reset-password <username> [--db <conn-ref>]
                                                   Reset a local user's password straight against the catalog (hidden
                                                   prompt; never a flag). SSO users are refused.
            Control plane (remote): the same API the GUI uses. The target resolves from --url or SQLFLOW_URL; the
            credential from --token, then SQLFLOW_TOKEN, then the per-URL store 'login' writes
            (~/.sqlflow/credentials.json, relocatable with SQLFLOW_CREDENTIALS_FILE). Human output on stdout, notes
            on stderr; --json switches stdout to the raw API shapes. Exit codes: 0 ok, 1 error or a followed run that
            did not succeed, 130 on Ctrl+C.
              sqlflow health                       Probe /health/live and /health/ready (anonymous).
              sqlflow login    [--username <u>] [--device] [--with-token] [--token-name <n>]
                               [--expires-days <N>|--no-expiry] [--scopes "read operate"] [--no-store]
                                                   Sign in and store a personal access token for the URL.
              sqlflow logout                       Revoke the stored token server-side and remove it locally.
              sqlflow trigger  --repo <name|id> --flow <f> [--pool <p>] [--commit <sha>]
                               [--full] [--from <date>] [--to <date>] [--file-pattern <glob>] [--preview] [--follow]
                                                   Enqueue a run on the fleet (POST /runs). --preview shows what would
                                                   run without enqueuing; --follow attaches to the live trace.
              sqlflow runs list [--status s] [--flow name] [--batch b] [--kind k] [--repo r] [--group g] [--latest]
              sqlflow runs show <runId> | trace <runId> [--follow]
              sqlflow groups show <groupId> [--follow] | cancel <groupId> | rerun <groupId> [--follow]
              sqlflow whoami | summary | nodes | doctor
              sqlflow schedules list | show <id> | create --repo r --flow f[,f2,...] (--cron <expr>|--interval <seconds>)
                               [--name <n>] [--timezone tz] [--max-concurrency <n>] [--disabled] [--catchup]
                               | run <id> | pause <id> | resume <id> | delete <id>
              sqlflow repos list | show <id> | register --name n --remote-url u [--branch b] [--credential-ref r]
                               | discover --remote-url u [--branch b] | sync <id>
              sqlflow pipelines list [--repo r] [--kind k] [--active true|false] [--name n] | show <id> [--yaml|--definition]
              sqlflow search <term> [--flows] [--page N --page-size N]
              sqlflow completions bash|zsh|powershell
            Options: -v/--verbose for debug logging on stderr; -h/--help for this text.
            """);
    }

    private static RunLogLevel ParseLogLevel(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "info" => RunLogLevel.Info,
        "debug" => RunLogLevel.Debug,
        "trace" => RunLogLevel.Trace,
        _ => throw new SqlFlowException($"Unknown --log-level '{value}'. Allowed: info, debug, trace."),
    };

    /// <summary>
    /// The per-run parameters' CLI surface: <c>--full</c> ignores the flow's change detection, <c>--from</c>/<c>--to</c>
    /// is an externally-bounded window, and <c>--file-pattern</c> narrows the selection to one glob. Parsed and
    /// validated here (dates are invariant-culture, e.g. <c>2023-01-15</c> or <c>2023-01-15 06:00:00</c>), the same
    /// <see cref="RunParameters"/> contract the control-plane trigger validates, so both entry points refuse exactly
    /// the same nonsense.
    /// </summary>
    internal static RunParameters ParseRunParameters(string[] args)
    {
        static DateTime? ParseDate(string? value, string flag)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (DateTime.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                return parsed;
            }

            throw new SqlFlowException($"{flag} '{value}' is not a date; use e.g. 2023-01-15 or '2023-01-15 06:00:00'.");
        }

        var parameters = new RunParameters
        {
            FullLoad = args.Contains("--full"),
            BackfillFrom = ParseDate(GetOption(args, "--from"), "--from"),
            BackfillTo = ParseDate(GetOption(args, "--to"), "--to"),
            FilePattern = GetOption(args, "--file-pattern"),
            AssertionsOnly = args.Contains("--assertions-only"),
            SourceFilter = GetOption(args, "--source-filter"),
        };
        parameters.Validate();
        return parameters;
    }

    /// <summary>The options that consume the argument after them, so positional-argument parsing can skip their values.</summary>
    internal static readonly HashSet<string> ValueTakingOptions = new(StringComparer.Ordinal)
    {
        "--log-level", "--db", "--repo", "--repo-url",
        // The control-plane verbs (health/login/logout/trigger/runs/groups and the estate family).
        "--url", "--token", "--username", "--token-name", "--expires-days", "--scopes",
        "--scope", "--batch", "--pool", "--poll-seconds", "--drain-seconds", "--commit", "--flow", "--status", "--kind", "--group",
        "--page", "--page-size", "--from", "--to", "--file-pattern", "--source-filter",
        "--cron", "--interval", "--timezone", "--max-concurrency",
        "--remote-url", "--credential-ref", "--credential-user",
        "--name", "--active", "--enabled", "--last",
    };

    internal static string[] PositionalArguments(string[] args)
    {
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith('-'))
            {
                if (ValueTakingOptions.Contains(args[i]))
                {
                    i++;
                }

                continue;
            }

            positional.Add(args[i]);
        }

        return [.. positional];
    }

    internal static string? GetOption(string[] args, params string[] names)
    {
        var index = Array.FindIndex(args, a => names.Contains(a));
        if (index < 0 || index + 1 >= args.Length)
        {
            return null;
        }

        // A value-taking flag with no value would otherwise swallow the next flag. A lone "-" is still allowed.
        var value = args[index + 1];
        return value.Length > 1 && value[0] == '-' ? null : value;
    }

    internal static int ParseIntOption(string[] args, int fallback, params string[] names)
    {
        var value = GetOption(args, names);
        return value is not null && int.TryParse(value, out var parsed) && parsed >= 0 ? parsed : fallback;
    }
}
