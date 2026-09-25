using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Delivery.Tests;

namespace SqlFlow.Delivery.Tools.SampleData;

/// <summary>
/// Exports the sample estate's data from the Databricks tables recall_to_osdu delivers well logs from, and writes the files
/// the sample flows read: the well log header and curve CSV files, and one parquet chunk per log. The queries sit beside
/// this program (queries/*.sql) and run through the Databricks CLI the developer is signed in with, so no credential passes
/// through here. The grid of each log is built the way recall_to_osdu's prepare builds it for a metadata-gridded log source:
/// depths from the lowest curve top to the highest curve base at the log's increment, and each sample at its curve's top
/// plus its position times the increment.
/// </summary>
public static partial class Program
{
    private static readonly string[] DefaultWellbores = ["NO 33/9-C-28 B", "NO 15/5-7 AT2", "NO 33/9-C-28 A", "NO 34/10-B-31 AT2", "NO 33/9-A-24 AT2"];

    private const string Usage = """
        Usage: SampleData --warehouse <sql-warehouse-id> [--profile <databricks-profile>] [--catalog <catalog>]
                          [--log-source <STAT_COMP>] [--wellbore <uwi>]... [--date <yyyyMMdd>] [--out <data folder>]

        Exports at most five Recall well logs (the wellbores named, five delivered ones by default) into the sample estate's
        data folder, replacing the header file, the curve file and the payload chunks it held.
        """;

    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex Identifier();

    [GeneratedRegex("^[0-9]{8}$")]
    private static partial Regex DateStamp();

    public static async Task<int> Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message + Environment.NewLine + Usage).ConfigureAwait(false);
            return 2;
        }

        try
        {
            var logs = await ExportAsync(options).ConfigureAwait(false);
            foreach (var folder in new[] { SampleWellLogs.LogFolder, SampleWellLogs.CurveFolder, SampleWellLogs.PayloadFolder })
            {
                var path = Path.Combine(options.Out, folder);
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }

            await SampleWellLogs.WriteAsync(options.Out, logs, options.Date).ConfigureAwait(false);
            var reread = await SampleWellLogs.LoadAsync(options.Out).ConfigureAwait(false);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Wrote {reread.Count} log(s) under {options.Out}:"));
            foreach (var log in reread)
            {
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {log.WellboreUwi} {log.SourceProject}/{log.LogId}: {log.Curves.Count} curve(s), {log.Depths.Count} depth(s), payload hash {log.GridHash()}, delivery key {log.Key}"));
            }

            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or JsonException)
        {
            await Console.Error.WriteLineAsync("The sample data could not be exported: " + ex.Message).ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<IReadOnlyList<SampleLog>> ExportAsync(Options options)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["log_name"] = options.LogSource,
            ["wellbores"] = string.Join('|', options.Wellbores),
        };
        var curveRows = await QueryAsync(options, "curves.sql", new(parameters) { ["table"] = $"{options.Catalog}.wl_pipelines_dsis_intermediate.recall_logcurve_enriched" }).ConfigureAwait(false);
        var sampleRows = await QueryAsync(
            options, "samples.sql", new(parameters) { ["table"] = $"{options.Catalog}.wl_pipelines_dsis_curated.{options.LogSource.ToLowerInvariant()}_logcurve__dsis_logcurve_json_v1" }).ConfigureAwait(false);
        var samples = sampleRows
            .GroupBy(r => Required(r, "row_key"), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => long.Parse(Required(r, "sample_index"), CultureInfo.InvariantCulture)).ToList(), StringComparer.Ordinal);

        var logs = new List<SampleLog>();
        foreach (var group in curveRows.GroupBy(r => (Required(r, "wellbore_uwi"), Required(r, "source_project"), Required(r, "log_id"))))
        {
            logs.Add(BuildLog(group.ToList(), samples));
        }

        if (logs.Count == 0)
        {
            throw new InvalidDataException($"No {options.LogSource} log was found for {string.Join(", ", options.Wellbores)}.");
        }

        return logs.OrderBy(l => l.WellboreUwi, StringComparer.Ordinal).ToList();
    }

    private static SampleLog BuildLog(IReadOnlyList<IReadOnlyDictionary<string, string?>> rows, IReadOnlyDictionary<string, List<IReadOnlyDictionary<string, string?>>> samples)
    {
        var head = rows[0];
        var name = $"{Required(head, "wellbore_uwi")} {Required(head, "source_project")}/{Required(head, "log_id")}";
        var increment = Number(Required(head, "index_increment"));
        if (increment <= 0)
        {
            throw new InvalidDataException($"{name} has no positive index increment, so its samples have no depths.");
        }

        // recall_to_osdu drops a curve whose metadata resolves more than one way within its log.
        var duplicates = rows.GroupBy(r => Required(r, "curve_id"), StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var duplicate in duplicates)
        {
            Console.WriteLine($"  {name}: curve {duplicate} is described more than once and is left out, as recall_to_osdu leaves it out.");
        }

        var curveRows = rows.Where(r => !duplicates.Contains(Required(r, "curve_id")) && Required(r, "curve_id") != SampleWellLogs.IndexCurveId)
            .OrderBy(r => Required(r, "curve_id"), StringComparer.Ordinal)
            .ToList();
        if (curveRows.Count == 0)
        {
            throw new InvalidDataException($"{name} has no curve to export.");
        }

        var start = curveRows.Min(r => Number(Required(r, "curve_index_min")));
        var stop = curveRows.Max(r => Number(Required(r, "curve_index_max")));
        // A tolerance below the rounding of the depths keeps the last depth that floating point division would lose.
        var steps = (long)Math.Floor(((stop - start) / increment) + 1e-6);
        var depths = new List<double>((int)steps + 1);
        for (long step = 0; step <= steps; step++)
        {
            depths.Add(Depth(start + (step * increment)));
        }

        var position = depths.Select((depth, i) => (depth, i)).ToDictionary(p => p.depth, p => p.i);
        var values = new Dictionary<string, IReadOnlyList<double?>>(StringComparer.Ordinal);
        var curves = new List<SampleCurve>
        {
            // recall_to_osdu adds the index curve itself, spanning the log's sampling range.
            new(SampleWellLogs.IndexCurveId, Required(head, "log_index_unit"), "Measured depth", "HIGH",
                Number(Required(head, "log_index_min")), Number(Required(head, "log_index_max")), null, null),
        };
        foreach (var row in curveRows)
        {
            var curveId = Required(row, "curve_id");
            var top = Number(Required(row, "curve_index_min"));
            var column = new double?[depths.Count];
            var unplaced = 0;
            foreach (var sample in samples.GetValueOrDefault(Required(row, "row_key"), []))
            {
                var depth = Depth(top + (long.Parse(Required(sample, "sample_index"), CultureInfo.InvariantCulture) * increment));
                if (!position.TryGetValue(depth, out var at))
                {
                    unplaced++;
                    continue;
                }

                column[at] ??= sample["value"] is { } text ? Number(text) : null;
            }

            if (unplaced > 0)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {name}: {unplaced} sample(s) of {curveId} fall between the grid's depths and are left out."));
            }

            values[curveId] = column;
            curves.Add(new SampleCurve(
                curveId, Required(row, "curve_unit"), Required(row, "curve_description"), row["business_value"],
                top, Number(Required(row, "curve_index_max")), row["curve_version"], Moment(row["curve_update_date"])));
        }

        var elevation = string.Join(' ', new[] { head["elev_meas_ref"], head["elev_meas_ref_unit"] }.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()));
        return new SampleLog(
            Required(head, "source_project"), Required(head, "log_id"), Required(head, "wellbore_uwi"), Required(head, "log_name"),
            Required(head, "log_run"), Required(head, "recall_log_source"), Required(head, "index_type"), Required(head, "log_index_unit"),
            Number(Required(head, "log_index_min")), Number(Required(head, "log_index_max")), increment,
            Required(head, "depth_coding"), elevation, Required(head, "logs_meas_from"), Required(head, "creator"), Required(head, "log_service"),
            Required(head, "log_version"), Required(head, "data_type"), Required(head, "logging_contractor"), Required(head, "log_pass"),
            Required(head, "log_pass_type"), Required(head, "native_uid"), Instant(Required(head, "log_update_date")),
            curves, depths, values);
    }

    /// <summary>Runs one query of queries/ on the warehouse through the Databricks CLI and returns its rows by column name.</summary>
    private static async Task<IReadOnlyList<IReadOnlyDictionary<string, string?>>> QueryAsync(Options options, string file, Dictionary<string, string> parameters)
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "queries", file)).ConfigureAwait(false);
        var body = new JsonObject
        {
            ["warehouse_id"] = options.Warehouse,
            ["statement"] = sql,
            ["wait_timeout"] = "50s",
            ["on_wait_timeout"] = "CONTINUE",
            ["format"] = "JSON_ARRAY",
            ["disposition"] = "INLINE",
            ["parameters"] = new JsonArray(parameters.Select(p => (JsonNode)new JsonObject { ["name"] = p.Key, ["value"] = p.Value, ["type"] = "STRING" }).ToArray()),
        };

        var request = Path.Combine(Path.GetTempPath(), "sampledata-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(request, body.ToJsonString(), new UTF8Encoding(false)).ConfigureAwait(false);
        JsonNode response;
        try
        {
            response = await CliAsync(options, "api", "post", "/api/2.0/sql/statements", "--json", "@" + request).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(request);
        }

        var id = response["statement_id"]?.GetValue<string>() ?? throw new InvalidDataException($"{file}: the warehouse returned no statement id.");
        while (State(response) is "PENDING" or "RUNNING")
        {
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            response = await CliAsync(options, "api", "get", "/api/2.0/sql/statements/" + id).ConfigureAwait(false);
        }

        if (State(response) != "SUCCEEDED")
        {
            throw new InvalidOperationException($"{file} ended {State(response)}: {response["status"]?["error"]?["message"]?.GetValue<string>() ?? "no message"}");
        }

        var columns = response["manifest"]?["schema"]?["columns"]?.AsArray().Select(c => c!["name"]!.GetValue<string>()).ToList()
            ?? throw new InvalidDataException($"{file}: the result has no schema.");
        var rows = new List<IReadOnlyDictionary<string, string?>>();
        var chunk = response["result"];
        while (chunk is not null)
        {
            foreach (var data in chunk["data_array"]?.AsArray() ?? [])
            {
                var cells = data!.AsArray();
                var row = new Dictionary<string, string?>(StringComparer.Ordinal);
                for (var i = 0; i < columns.Count; i++)
                {
                    row[columns[i]] = cells[i]?.GetValue<string>();
                }

                rows.Add(row);
            }

            chunk = chunk["next_chunk_internal_link"]?.GetValue<string>() is { } next ? await CliAsync(options, "api", "get", next).ConfigureAwait(false) : null;
        }

        return rows;
    }

    private static string? State(JsonNode response) => response["status"]?["state"]?.GetValue<string>();

    private static async Task<JsonNode> CliAsync(Options options, params string[] arguments)
    {
        var start = new ProcessStartInfo("databricks")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        if (options.Profile is { } profile)
        {
            start.ArgumentList.Add("--profile");
            start.ArgumentList.Add(profile);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("The Databricks CLI (databricks) could not be started.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"databricks {arguments[0]} {arguments[1]} exited {process.ExitCode}: {(await error.ConfigureAwait(false)).Trim()}");
        }

        return JsonNode.Parse(await output.ConfigureAwait(false)) ?? throw new InvalidDataException("The Databricks CLI answered with no JSON.");
    }

    private static string Required(IReadOnlyDictionary<string, string?> row, string column)
        => row.TryGetValue(column, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : throw new InvalidDataException($"A row has no '{column}'.");

    private static double Number(string text) => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static double Depth(double value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);

    private static DateTime Instant(string text)
        => DateTime.SpecifyKind(DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal), DateTimeKind.Utc);

    private static string? Moment(string? text)
        => string.IsNullOrWhiteSpace(text) ? null : Instant(text).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private sealed record Options(string Warehouse, string? Profile, string Catalog, string LogSource, IReadOnlyList<string> Wellbores, string Date, string Out)
    {
        public static Options Parse(string[] args)
        {
            string? warehouse = null;
            string? profile = null;
            var catalog = "sub_wlt_datahub_dev";
            var logSource = SampleWellLogs.LogSource;
            var wellbores = new List<string>();
            var date = DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "recall", "data"));
            for (var i = 0; i < args.Length; i++)
            {
                var value = i + 1 < args.Length ? args[i + 1] : throw new ArgumentException($"{args[i]} needs a value.");
                switch (args[i])
                {
                    case "--warehouse": warehouse = value; break;
                    case "--profile": profile = value; break;
                    case "--catalog": catalog = value; break;
                    case "--log-source": logSource = value; break;
                    case "--wellbore": wellbores.Add(value); break;
                    case "--date": date = value; break;
                    case "--out": output = Path.GetFullPath(value); break;
                    default: throw new ArgumentException($"Unknown argument {args[i]}.");
                }

                i++;
            }

            if (string.IsNullOrWhiteSpace(warehouse) || !Identifier().IsMatch(warehouse))
            {
                throw new ArgumentException("--warehouse names the SQL warehouse id to run the export on.");
            }

            if (!Identifier().IsMatch(catalog) || !Identifier().IsMatch(logSource))
            {
                throw new ArgumentException("--catalog and --log-source are plain identifiers (letters, digits, underscore).");
            }

            if (!DateStamp().IsMatch(date))
            {
                throw new ArgumentException("--date is yyyyMMdd.");
            }

            if (wellbores.Count == 0)
            {
                wellbores.AddRange(DefaultWellbores);
            }

            // The estate is an example: a handful of logs, never a copy of the source.
            if (wellbores.Count > 5 || wellbores.Any(w => w.Contains('|', StringComparison.Ordinal) || string.IsNullOrWhiteSpace(w)))
            {
                throw new ArgumentException("Name at most five wellbores, none empty or holding '|'.");
            }

            return new Options(warehouse, profile, catalog, logSource, wellbores, date, output);
        }
    }
}
