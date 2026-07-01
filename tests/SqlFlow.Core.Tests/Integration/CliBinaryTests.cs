using System.Diagnostics;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Exercises the actual compiled CLI binary (not just the library): argument parsing, the DI
/// composition root, exit codes, and a real end-to-end run against the sink. Skips when the built CLI
/// or the sink is unavailable, so the default unit run is unaffected.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CliBinaryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_cli_" + Guid.NewGuid().ToString("N"));

    public CliBinaryTests() => Directory.CreateDirectory(_dir);

    [SkippableFact]
    public async Task Cli_Validate_ParsesAndExitsZero()
    {
        var dll = CliDllPath();
        Skip.If(dll is null, "Built CLI not found; run 'dotnet build -c Release' first.");

        var csv = Path.Combine(_dir, "orders.csv");
        File.WriteAllText(csv, "OrderId,Customer\n1,Acme\n");
        var yaml = WriteYaml("cli_validate", csv, "Cli_Validate", "Server=localhost;Database=Db;Trusted_Connection=True;TrustServerCertificate=True");

        var (exit, stdout, stderr) = await RunCliAsync(dll!, ["validate", yaml]);

        Assert.True(exit == 0, $"validate exit={exit}, stderr={stderr}");
        Assert.Contains("OK", stdout, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Cli_Run_LoadsTableAgainstSink()
    {
        var cs = IntegrationDb.Require();
        var dll = CliDllPath();
        Skip.If(dll is null, "Built CLI not found; run 'dotnet build -c Release' first.");

        var table = "IT_Cli_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        var csv = Path.Combine(_dir, "orders.csv");
        File.WriteAllText(csv, "OrderId,Customer\n1,Acme\n2,Globex\n3,Initech\n");
        var yaml = WriteYaml("cli_run", csv, table, "${env:SQLFlowSinkConStr}");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(dll!, ["run", yaml], ("SQLFlowSinkConStr", cs));

            Assert.True(exit == 0, $"run exit={exit}, stdout={stdout}, stderr={stderr}");
            Assert.True(await IntegrationDb.TableExistsAsync(cs, table));
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, table));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    private string WriteYaml(string name, string csvPath, string table, string connection)
    {
        var path = Path.Combine(_dir, name + ".flow.yaml");
        var yaml = $"""
            name: {table}
            source:
              type: csv
              location: {csvPath.Replace("\\", "/")}
            target:
              connection: "{connection}"
              schema: dbo
              table: {table}
            """;
        File.WriteAllText(path, yaml);
        return path;
    }

    private static string? CliDllPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var projectBin = Path.Combine(dir.FullName, "src", "SqlFlow.Cli", "bin");
            if (Directory.Exists(projectBin))
            {
                // The CLI project's assembly name is 'sqlflow' (sqlflow.dll / sqlflow.exe).
                foreach (var config in new[] { "Release", "Debug" })
                {
                    var candidate = Path.Combine(projectBin, config, "net9.0", "sqlflow.dll");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static async Task<(int Exit, string StdOut, string StdErr)> RunCliAsync(string dll, string[] args, params (string Name, string Value)[] env)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(dll)!,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(dll);
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        foreach (var (name, value) in env)
        {
            psi.Environment[name] = value;
        }

        using var process = Process.Start(psi)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await process.WaitForExitAsync(cts.Token);

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
