using System.Diagnostics;

namespace SqlFlow.Delivery.Setup;

/// <summary>
/// Runs tools as processes. A tool is found on the PATH as the shell finds it (with the system's executable extensions on
/// Windows, so <c>az</c> is <c>az.cmd</c>); one not attached to the console has its input closed and its output read, and is
/// stopped, with every process it started, when it outlasts its timeout or the command is cancelled.
/// </summary>
internal sealed class ProcessToolRunner : IToolRunner
{
    /// <summary>How long a tool not attached to the console may run when its run names no timeout.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public string? Find(string tool)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        if (Path.IsPathRooted(tool))
        {
            return File.Exists(tool) ? tool : null;
        }

        string[] extensions = OperatingSystem.IsWindows()
            ? [string.Empty, .. (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)]
            : [string.Empty];
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = folder.Trim().Trim('"');
            if (directory.Length == 0)
            {
                continue;
            }

            foreach (var extension in extensions)
            {
                // On Windows a name without an extension is not what CreateProcess runs: only the extensions run.
                if (extension.Length == 0 && OperatingSystem.IsWindows() && !Path.HasExtension(tool))
                {
                    continue;
                }

                string candidate;
                try
                {
                    candidate = Path.Combine(directory, tool + extension);
                }
                catch (ArgumentException)
                {
                    break;
                }

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    public async Task<ToolResult> RunAsync(string tool, IReadOnlyList<string> arguments, ToolRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(run);
        var path = Find(tool);
        if (path is null)
        {
            return ToolResult.NotFound;
        }

        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            RedirectStandardInput = !run.Attached,
            RedirectStandardOutput = !run.Attached,
            RedirectStandardError = !run.Attached,
            WorkingDirectory = run.WorkingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in run.Environment ?? new Dictionary<string, string?>())
        {
            if (value is null)
            {
                start.Environment.Remove(name);
            }
            else
            {
                start.Environment[name] = value;
            }
        }

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
            {
                return ToolResult.NotFound;
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Found but not runnable here (a file without the right to run it, a broken link): as good as missing.
            return ToolResult.NotFound;
        }

        Task<string> output = Task.FromResult(string.Empty);
        Task<string> error = Task.FromResult(string.Empty);
        if (!run.Attached)
        {
            process.StandardInput.Close();
            output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeout = run.Timeout ?? (run.Attached ? (TimeSpan?)null : DefaultTimeout);
        if (timeout is { } after)
        {
            limit.CancelAfter(after);
        }

        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Stop(process);
            ct.ThrowIfCancellationRequested();
            return new ToolResult(true, -1, (await output.ConfigureAwait(false)).Trim(), (await error.ConfigureAwait(false)).Trim(), TimedOut: true);
        }

        return new ToolResult(true, process.ExitCode, (await output.ConfigureAwait(false)).Trim(), (await error.ConfigureAwait(false)).Trim(), TimedOut: false);
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            process.WaitForExit(5000);
        }
        catch (InvalidOperationException)
        {
            // It ended on its own between the check and the kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // It could not be stopped (it is ending, or belongs to another session); its output is read as it stands.
        }
    }
}
