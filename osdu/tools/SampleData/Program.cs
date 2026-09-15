using System.Globalization;
using SqlFlow.Delivery.Tests;

namespace SqlFlow.Delivery.Tools.SampleData;

/// <summary>
/// Writes the sample estate's data files (docs/stage4-design.md section 1.4): the well log and curve metadata the
/// pre-ingestion flows read, the wellbore master data beside them, and one parquet payload chunk per log. The files are
/// deterministic, so running this again after a change to the fixture shows exactly what moved.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var root = args.Length > 0
            ? Path.GetFullPath(args[0])
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "recall-welllog", "data"));

        try
        {
            await SampleWellLogs.WriteAsync(root).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync($"The sample data could not be written under {root}: {ex.Message}").ConfigureAwait(false);
            return 1;
        }

        var logs = SampleWellLogs.Logs();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Wrote the sample data under {root}: {logs.Count} log(s), {logs.Sum(l => l.Curves.Count)} curve(s), {SampleWellLogs.Wellbores.Count} wellbore(s)."));
        foreach (var log in logs)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {log.SourceProject}/{log.LogId}: {log.Depths().Count} depth(s), payload hash {log.GridHash()}, delivery key {log.Key}"));
        }

        return 0;
    }
}
