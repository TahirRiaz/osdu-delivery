using System.Text.Json;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// The CLI verbs that need no database at all, driven through the compiled binary: usage and exit-code
/// conventions, the validate failure modes that need no document kind (malformed YAML, a missing file), the
/// guard rails of the remote verbs when no control plane is configured, and the local helpers (doctor,
/// completions, the on-disk run listing). Skips only when the built binary is missing.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CliOfflineVerbTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_cliov_" + Guid.NewGuid().ToString("N"));
    private readonly string _dll;

    public CliOfflineVerbTests()
    {
        Directory.CreateDirectory(_dir);
        _dll = CliBinary.DllPath() ?? string.Empty;
    }

    private void RequireCli() => Skip.If(_dll.Length == 0, "Built CLI not found; run 'dotnet build -c Release' first.");

    // ---- usage and exit codes -----------------------------------------------------------------------------

    [SkippableFact]
    public async Task NoArguments_PrintsUsage_Exit1()
    {
        RequireCli();
        var result = await CliBinary.RunAsync(_dll, [], workingDirectory: _dir);
        Assert.Equal(1, result.Exit);
        Assert.Contains("Usage:", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("sqlflow validate", result.StdOut, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task HelpFlag_PrintsUsage_Exit0()
    {
        RequireCli();
        var result = await CliBinary.RunAsync(_dll, ["run", "whatever.yaml", "--help"], workingDirectory: _dir);
        Assert.Equal(0, result.Exit);
        Assert.Contains("Usage:", result.StdOut, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task UnknownCommand_Exit1_NamesIt()
    {
        RequireCli();
        var result = await CliBinary.RunAsync(_dll, ["frobnicate", "x.yaml"], workingDirectory: _dir);
        Assert.Equal(1, result.Exit);
        Assert.Contains("Unknown command 'frobnicate'", result.StdErr, StringComparison.Ordinal);
    }

    // ---- validate: the failure modes that need no document kind ----------------------------------------------

    [SkippableFact]
    public async Task Validate_MalformedYaml_Exit1_ExplainsWhy()
    {
        RequireCli();
        var yaml = WriteFile("broken.flow.yaml", "name: [unclosed\nsource: ::::\n");

        var result = await CliBinary.RunAsync(_dll, ["validate", yaml], workingDirectory: _dir);

        Assert.Equal(1, result.Exit);
        Assert.Contains("ERROR", result.StdErr, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Validate_MissingFile_Exit1()
    {
        RequireCli();
        var result = await CliBinary.RunAsync(_dll, ["validate", Path.Combine(_dir, "nope.yaml")], workingDirectory: _dir);
        Assert.Equal(1, result.Exit);
        Assert.Contains("ERROR", result.StdErr, StringComparison.Ordinal);
    }

    // ---- remote verbs without a control plane configured ----------------------------------------------------

    [SkippableFact]
    public async Task Trigger_WithoutUrl_Exit1_PointsAtConfiguration()
    {
        RequireCli();
        var result = await CliBinary.RunAsync(_dll, ["trigger", "--repo", "x", "--flow", "y"], workingDirectory: _dir);
        Assert.Equal(1, result.Exit);
        Assert.Contains("SQLFLOW_URL", result.StdErr, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Health_WithoutUrl_Exit1_PointsAtConfiguration()
    {
        RequireCli();
        var result = await CliBinary.RunAsync(_dll, ["health"], workingDirectory: _dir);
        Assert.Equal(1, result.Exit);
        Assert.Contains("SQLFLOW_URL", result.StdErr, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task RunsList_WithoutUrl_Exit1_ButCancelKeepsTheDirectDbContract()
    {
        RequireCli();

        // list is remote-only: no URL is a configuration error.
        var list = await CliBinary.RunAsync(_dll, ["runs", "list"], workingDirectory: _dir);
        Assert.Equal(1, list.Exit);
        Assert.Contains("SQLFLOW_URL", list.StdErr, StringComparison.Ordinal);

        // cancel without a URL falls back to the direct-catalog path, whose own contract (a run id is a GUID)
        // answers before any connection is attempted.
        var cancel = await CliBinary.RunAsync(_dll, ["runs", "cancel", "not-a-guid"], workingDirectory: _dir);
        Assert.Equal(1, cancel.Exit);
        Assert.Contains("requires a run id", cancel.StdErr, StringComparison.Ordinal);
    }

    // ---- doctor, completions, local runs ----------------------------------------------------------

    [SkippableFact]
    public async Task Doctor_NothingConfigured_SkipsEverySurface_Exit0()
    {
        RequireCli();
        var result = await CliBinary.RunAsync(_dll, ["doctor"], workingDirectory: _dir);

        Assert.True(result.Exit == 0, result.AllOutput);
        Assert.Contains("control plane: SKIP", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("catalog db:    SKIP", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("checks out", result.StdOut, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Completions_EachShell_Exit0_CoversTheVerbs()
    {
        RequireCli();
        foreach (var shell in new[] { "bash", "zsh", "powershell" })
        {
            var result = await CliBinary.RunAsync(_dll, ["completions", shell], workingDirectory: _dir);
            Assert.True(result.Exit == 0, $"{shell}: {result.AllOutput}");
            Assert.Contains("trigger", result.StdOut, StringComparison.Ordinal);
            Assert.Contains("schedules", result.StdOut, StringComparison.Ordinal);
        }

        var unknown = await CliBinary.RunAsync(_dll, ["completions", "fish"], workingDirectory: _dir);
        Assert.Equal(1, unknown.Exit);
    }

    [SkippableFact]
    public async Task RunsLocal_EmptyFolder_Exit0_ReportsZero()
    {
        RequireCli();
        var result = await CliBinary.RunAsync(_dll, ["runs", "local", _dir], workingDirectory: _dir);
        Assert.True(result.Exit == 0, result.AllOutput);
        Assert.Contains("(0 of 0 local run(s)", result.StdOut, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Logout_WithNothingStored_IsIdempotent_Exit0()
    {
        RequireCli();
        var credentialFile = Path.Combine(_dir, "credentials.json");

        var result = await CliBinary.RunAsync(
            _dll, ["logout", "--url", "http://localhost:59999"],
            env: [("SQLFLOW_CREDENTIALS_FILE", credentialFile)], workingDirectory: _dir);

        Assert.Equal(0, result.Exit);
        Assert.Contains("no stored credential", result.StdOut, StringComparison.Ordinal);
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
