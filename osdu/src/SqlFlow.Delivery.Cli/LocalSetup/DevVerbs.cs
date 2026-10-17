using System.Collections;
using SqlFlow.Cli.Hosting;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Json;

namespace SqlFlow.Delivery.Cli.LocalSetup;

/// <summary>
/// <c>sqlflow dev</c> (osdu/docs/reference/cli/dev.md): sets a checkout up for a local run and checks it. <c>setup</c> asks
/// the questions and writes <c>.sqlflow/env</c>; <c>check</c> says what the run has and what it is missing, each with its fix;
/// <c>env</c> prints the file's settings for dev.bat and dev.sh to set. The verb reads the env file itself (the CLI does not
/// apply it first), so a file the CLI would refuse is still checked and reported line by line.
/// </summary>
public static class DevVerbs
{
    /// <summary>The verb's usage lines.</summary>
    public static IReadOnlyList<string> Usage { get; } =
    [
        "sqlflow dev setup [--env-file <path>]",
        "                                   Set this checkout up for a local run: asks for the SQL Server (and starts",
        "                                   SQL Server in Docker when the machine has none), the databases, the first",
        "                                   admin, the OSDU platform and its client, and a flow repository; shows the",
        "                                   plan; then writes .sqlflow/env from dev.env.example (keeping the file it",
        "                                   replaces), creates the databases, registers the partition, and checks.",
        "                                   dev.bat and dev.sh run it when .sqlflow/env is missing, and 'setup' again",
        "sqlflow dev check [--env-file <path>] [--api] [--test-osdu] [--prepare] [--json]",
        "                                   What a local run has and what it is missing, each with its fix: the tools,",
        "                                   every setting, the SQL Server and both databases, sign-in, partitions,",
        "                                   repositories and the ${env:...} their flows name. --api: no GUI, so no",
        "                                   Node.js; --test-osdu signs in to OSDU and reads the legal tag (writes",
        "                                   nothing); --prepare first generates a copied template's <generated> values",
        "                                   and signs in to Azure when the file reads it. Exits 2 when a start would fail",
        "sqlflow dev env [--env-file <path>]",
        "                                   The file's settings as NAME=value lines, read as the hosts read them, for",
        "                                   dev.bat and dev.sh to set. Exits 2, printing none, for a file they refuse",
    ];

    public static Task<int> DevAsync(CliVerbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return RunAsync(ModuleCommand.Of(context), CreateMachine(context.Out, context.CancellationToken), context.Error, Directory.GetCurrentDirectory());
    }

    /// <summary>The verb, over the command it runs for and the machine it reaches.</summary>
    internal static async Task<int> RunAsync(ModuleCommand command, DevMachine machine, TextWriter error, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(error);
        var ct = command.CancellationToken;
        var subcommand = command.Arguments.Positional(1)?.ToLowerInvariant();
        if (subcommand is not ("setup" or "check" or "env"))
        {
            return command.UsageError(subcommand is null ? "name what to do: setup, check or env." : $"'{subcommand}' is none of setup, check and env.");
        }

        if (command.Json && subcommand != "check")
        {
            return command.UsageError("--json applies to 'dev check' alone.");
        }

        var checkout = DevCheckout.Find(workingDirectory, command.Arguments.GetOption("--env-file"));
        if (subcommand == "env")
        {
            return Env(checkout, command.Out, error, machine.Platform);
        }

        var template = await DevEnvTemplate.LoadAsync(checkout.TemplatePath, ct).ConfigureAwait(false);
        var gui = !command.Arguments.HasFlag("--api");
        if (subcommand == "setup")
        {
            try
            {
                if (!await new DevSetupWizard(checkout, template, machine).RunAsync(ct).ConfigureAwait(false))
                {
                    machine.Prompter.Say("Stopped; nothing was written.", Tone.Caution);
                    return 1;
                }
            }
            catch (SetupStoppedException stopped)
            {
                machine.Prompter.Say(string.Empty);
                machine.Prompter.Say(stopped.Message, Tone.Caution);
                return 1;
            }

            return await CheckAsync(checkout, template, machine, gui, testOsdu: false, json: false, signIn: false, command.Out, ct).ConfigureAwait(false);
        }

        if (command.Arguments.HasFlag("--prepare") && File.Exists(checkout.EnvFile))
        {
            var generated = await DevEnvFile.CompleteGeneratedAsync(checkout.EnvFile, ct).ConfigureAwait(false);
            if (generated.Count > 0)
            {
                error.WriteLine($"Generated {string.Join(" and ", generated)} in {checkout.Show(checkout.EnvFile)}.");
            }
        }

        return await CheckAsync(
            checkout, template, machine, gui, command.Arguments.HasFlag("--test-osdu"), command.Json, command.Arguments.HasFlag("--prepare"), command.Out, ct)
            .ConfigureAwait(false);
    }

    private static async Task<int> CheckAsync(
        DevCheckout checkout, DevEnvTemplate template, DevMachine machine, bool gui, bool testOsdu, bool json, bool signIn, TextWriter output, CancellationToken ct)
    {
        var facts = await DevFactsCollector.CollectAsync(checkout, template, machine, gui, testOsdu, ct).ConfigureAwait(false);

        // A start that reads Azure through the Azure CLI signs in here rather than failing on its first Key Vault reference.
        if (signIn && !json && facts.Tools is { AzureNeeded: true, AzureAccount: { Found: true, Succeeded: false } } && !Console.IsInputRedirected)
        {
            machine.Prompter.Say("Not signed in to Azure, which this file reads through the Azure CLI; opening the sign-in ...");
            await machine.Tools.RunAsync("az", ["login", "--output", "none"], new ToolRun(Attached: true), ct).ConfigureAwait(false);
            facts = await DevFactsCollector.CollectAsync(checkout, template, machine, gui, testOsdu, ct).ConfigureAwait(false);
        }

        var report = DevReadiness.Evaluate(facts);
        if (json)
        {
            output.WriteLine(CanonicalJson.Pretty(report.ToJson()));
        }
        else
        {
            report.Write(machine.Prompter);
        }

        return report.Ready ? 0 : 2;
    }

    /// <summary>
    /// The file's settings, one NAME=value line each, the first of a name set twice, as the hosts read the file; a file they
    /// refuse prints nothing (exit 2), each refused line named on <paramref name="error"/>.
    /// </summary>
    private static int Env(DevCheckout checkout, TextWriter output, TextWriter error, DevPlatform platform)
    {
        var launcher = platform == DevPlatform.Windows ? "dev.bat" : "./dev.sh";
        if (!File.Exists(checkout.EnvFile))
        {
            error.WriteLine($"ERROR  {checkout.Show(checkout.EnvFile)} is missing; {launcher} setup writes it.");
            return 2;
        }

        var content = LocalEnvFile.Read(checkout.EnvFile);
        if (content.Problems.Count > 0)
        {
            foreach (var problem in content.Problems)
            {
                error.WriteLine($"ERROR  {checkout.Show(checkout.EnvFile)}({problem.Line}): {problem.Message}");
            }

            return 2;
        }

        foreach (var entry in content.Entries.DistinctBy(e => e.Name, StringComparer.Ordinal))
        {
            output.WriteLine($"{entry.Name}={entry.Value}");
        }

        return 0;
    }

    /// <summary>The machine this process runs on, as the verb reaches it.</summary>
    internal static DevMachine CreateMachine(TextWriter output, CancellationToken ct)
    {
        var platform = OperatingSystem.IsWindows() ? DevPlatform.Windows : OperatingSystem.IsMacOS() ? DevPlatform.MacOS : DevPlatform.Linux;
        var environment = new Dictionary<string, string>(platform == DevPlatform.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            if (variable.Key is string name && variable.Value is string value)
            {
                environment[name] = value;
            }
        }

        var account = platform == DevPlatform.Windows ? $"{Environment.UserDomainName}\\{Environment.UserName}" : Environment.UserName;
        return new DevMachine(
            new ProcessToolRunner(), new SqlServerProbe(), new OsduSignIn(), new ConsolePrompter(output, ct), environment, platform, account,
            () => DateTimeOffset.Now, SelfCommand.Current());
    }
}
