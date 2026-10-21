using System.Globalization;
using System.Text.RegularExpressions;
using SqlFlow.Delivery.Tests;

namespace SqlFlow.Delivery.Tools.SampleData;

/// <summary>
/// Writes the sample estate's data: a handful of made-up well logs (the header file, the curve file and one parquet chunk
/// per log, as the sample flows read them) with the wells and wellbores they are logged in and each wellbore's survey and
/// formation tops, the lookup tables the sample mapping translates their values through, or the reference data the
/// sample's records refer to. The logs, wells, wellbores, surveys, tops and lookup tables are synthetic and deterministic
/// (<see cref="SyntheticEstate"/>): the same seed writes the
/// same bytes, nothing is read from a network or a database, and no credential is needed. The reference data is OSDU's
/// own, read from a local checkout of the OSDU data definitions (<see cref="OsduReferenceValues"/>). The committed files of
/// <c>osdu/samples/welldb</c> are what <c>--out osdu/samples/welldb/data --date 20260924</c>,
/// <c>--lookups osdu/samples/welldb/cache/data --date 20260901</c> and <c>--reference osdu/samples/welldb/reference/data
/// --osdu &lt;manifests&gt; --cache-records osdu/samples/cache-records --date 20261005</c> write, which the suites check
/// (SampleDataGeneratorTests).
/// </summary>
public static partial class Program
{
    private const string Usage = """
        Usage: SampleData --out <data folder> [--logs <n>] [--seed <n>] [--date <yyyyMMdd>]
               SampleData --lookups <cache data folder> [--date <yyyyMMdd>]
               SampleData --reference <reference data folder> --osdu <manifests folder> [--cache-records <folder>] [--date <yyyyMMdd>]

          --out <folder>       Writes the well logs: <folder>/welllog/welllog_<date>.csv, <folder>/curves-meta/curves_<date>.csv
                               and <folder>/curves/<project>/<log id>/chunk_00000.parquet; the wells and wellbores they
                               are logged in: <folder>/well/well_<date>.csv and <folder>/wellbore/wellbore_<date>.csv;
                               and each wellbore's survey and formation tops: <folder>/survey/survey_<date>.csv,
                               <folder>/survey-properties/survey_properties_<date>.csv,
                               <folder>/stations/<project>/<survey id>/chunk_00000.parquet,
                               <folder>/marker-set/marker_set_<date>.csv and <folder>/marker/marker_<date>.csv,
                               replacing the files the generator wrote there before.
          --lookups <folder>   Writes the lookup tables: <folder>/unit-alias/unit_alias_<date>.csv,
                               <folder>/depth-unit-alias/depth_unit_alias_<date>.csv and
                               <folder>/curve-dictionary/curve_dictionary_<date>.csv, replacing the files there before.
          --reference <folder> Writes the reference data the sample's records refer to, one folder per kind
                               (<folder>/unit-of-measure/unit_of_measure_<date>.csv, ...), replacing the files there before:
                               OSDU's values, read from the reference value manifests of a checkout of the OSDU data
                               definitions, and the well database's own curve types.
          --osdu <folder>      The checkout's ReferenceValues/Manifests/reference-data folder (with --reference).
          --cache-records <folder>
                               Also writes the records a refresh of the reference cache captures once that data is
                               delivered, one <entity type>.json per kind (with --reference).
          --logs <n>           How many logs to write, 1 to 40 (default 5).
          --seed <n>           The seed the values are drawn from, an unsigned 64-bit integer (default 20260924).
          --date <yyyyMMdd>    The date the file names carry (default today, UTC).
          --help               Prints this text.
        """;

    [GeneratedRegex("^[0-9]{8}$")]
    private static partial Regex DateStamp();

    public static async Task<int> Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Any(a => a is "--help" or "-h" or "-?"))
        {
            Console.WriteLine(Usage);
            return 0;
        }

        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message + Environment.NewLine + Environment.NewLine + Usage).ConfigureAwait(false);
            return 2;
        }

        try
        {
            if (options.Lookups is { } lookups)
            {
                await WriteLookupsAsync(lookups, options.Date).ConfigureAwait(false);
            }
            else if (options.Reference is { } reference)
            {
                await WriteReferenceAsync(reference, options.Osdu!, options.CacheRecords, options.Date).ConfigureAwait(false);
            }
            else
            {
                await WriteLogsAsync(options.Out!, options.Logs, options.Seed, options.Date).ConfigureAwait(false);
            }

            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            await Console.Error.WriteLineAsync("The sample data could not be written: " + ex.Message).ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task WriteLogsAsync(string root, int count, ulong seed, string date)
    {
        var logs = SyntheticEstate.WellLogs(count, seed);
        Directory.CreateDirectory(root);
        Clear(root, SampleWellLogs.LogFolder, "*.csv", SearchOption.TopDirectoryOnly);
        Clear(root, SampleWellLogs.CurveFolder, "*.csv", SearchOption.TopDirectoryOnly);
        Clear(root, SampleWellLogs.PayloadFolder, SampleWellLogs.ChunkFileName, SearchOption.AllDirectories);
        Clear(root, SyntheticEstate.WellFolder, "*.csv", SearchOption.TopDirectoryOnly);
        Clear(root, SyntheticEstate.WellboreFolder, "*.csv", SearchOption.TopDirectoryOnly);
        Clear(root, SampleSurveys.SurveyFolder, "*.csv", SearchOption.TopDirectoryOnly);
        Clear(root, SampleSurveys.PropertyFolder, "*.csv", SearchOption.TopDirectoryOnly);
        Clear(root, SampleSurveys.PayloadFolder, SampleSurveys.ChunkFileName, SearchOption.AllDirectories);
        Clear(root, SampleSurveys.MarkerSetFolder, "*.csv", SearchOption.TopDirectoryOnly);
        Clear(root, SampleSurveys.MarkerFolder, "*.csv", SearchOption.TopDirectoryOnly);

        await SampleWellLogs.WriteAsync(root, logs, date).ConfigureAwait(false);
        await SyntheticEstate.WriteMasterDataAsync(root, count, seed, date).ConfigureAwait(false);
        await SyntheticEstate.WriteSurveysAsync(root, count, seed, date).ConfigureAwait(false);

        // Read back through the same reader the suites use, which checks every header's payload hash against its chunk.
        var reread = await SampleWellLogs.LoadAsync(root).ConfigureAwait(false);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Wrote {reread.Count} log(s) under {root} (seed {seed}):"));
        foreach (var log in reread)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {log.WellboreUwi} {log.SourceProject}/{log.LogId}: {log.Curves.Count} curve(s), {log.Depths.Count} depth(s), payload hash {log.GridHash()}, delivery key {log.Key}"));
        }

        // The surveys are read back the same way, which checks each one's top, base and payload hash against its stations.
        var surveys = await SampleSurveys.LoadSurveysAsync(root).ConfigureAwait(false);
        var markerSets = SampleSurveys.LoadMarkerSets(root);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Wrote {surveys.Count} survey(s) and {markerSets.Count} marker set(s):"));
        foreach (var survey in surveys)
        {
            var tops = markerSets.Single(m => m.WellboreUwi == survey.WellboreUwi).Markers;
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {survey.WellboreUwi} {survey.SourceProject}/{survey.SurveyId}: {survey.Stations.Count} station(s) to {survey.BaseMd} m, payload hash {survey.StationHash()}; {tops.Count} top(s)"));
        }
    }

    private static async Task WriteReferenceAsync(string root, string manifests, string? cacheRecords, string date)
    {
        Directory.CreateDirectory(root);
        foreach (var folder in OsduReferenceValues.Kinds.Select(k => k.Folder).Append(OsduReferenceValues.LogCurveTypeFolder))
        {
            Clear(root, folder, "*.csv", SearchOption.TopDirectoryOnly);
        }

        await OsduReferenceValues.WriteAsync(root, manifests, date).ConfigureAwait(false);
        foreach (var folder in OsduReferenceValues.Kinds.Select(k => k.Folder).Append(OsduReferenceValues.LogCurveTypeFolder))
        {
            var file = Directory.GetFiles(Path.Combine(root, folder), "*.csv").Single();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Wrote {SampleWellLogs.ReadCsv(file).Count} row(s) to {file}."));
        }

        if (cacheRecords is not null)
        {
            await OsduReferenceValues.WriteCacheRecordsAsync(cacheRecords, root).ConfigureAwait(false);
            Console.WriteLine($"Wrote the reference cache's records under {cacheRecords}.");
        }
    }

    private static async Task WriteLookupsAsync(string root, string date)
    {
        Directory.CreateDirectory(root);
        foreach (var folder in new[] { SyntheticEstate.UnitAliasFolder, SyntheticEstate.DepthUnitAliasFolder, SyntheticEstate.CurveDictionaryFolder })
        {
            Clear(root, folder, "*.csv", SearchOption.TopDirectoryOnly);
        }

        await SyntheticEstate.WriteLookupsAsync(root, date).ConfigureAwait(false);
        foreach (var (folder, key) in new[]
        {
            (SyntheticEstate.UnitAliasFolder, "source_unit"),
            (SyntheticEstate.DepthUnitAliasFolder, "source_unit"),
            (SyntheticEstate.CurveDictionaryFolder, "mnemonic"),
        })
        {
            // An ingestion flow keys each table on a case-insensitive database, so two keys that differ only by case would
            // be one row there and the capture would not hold what the file says.
            var file = Directory.GetFiles(Path.Combine(root, folder), "*.csv").Single();
            var rows = SampleWellLogs.ReadCsv(file);
            var clash = rows.GroupBy(r => r[key], StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (clash is not null)
            {
                throw new InvalidDataException($"{file} holds the key '{clash.Key}' more than once, ignoring case.");
            }

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Wrote {rows.Count} row(s) to {file}."));
        }
    }

    /// <summary>
    /// Removes what the generator wrote under <paramref name="root"/>/<paramref name="folder"/> before, and refuses when the
    /// folder holds anything else: a folder named by mistake is never emptied.
    /// </summary>
    private static void Clear(string root, string folder, string pattern, SearchOption depth)
    {
        var path = Path.Combine(root, folder);
        if (!Directory.Exists(path))
        {
            return;
        }

        var owned = Directory.GetFiles(path, pattern, depth).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var foreign = Directory.GetFiles(path, "*", SearchOption.AllDirectories).FirstOrDefault(f => !owned.Contains(f));
        if (foreign is not null)
        {
            throw new InvalidOperationException(
                $"{path} holds {foreign}, which the generator does not write; it replaces only {pattern} files there. Move that file, or name the folder the sample data belongs in.");
        }

        foreach (var file in owned)
        {
            File.Delete(file);
        }

        // The folders a previous run made for its logs, deepest first, once they are empty.
        foreach (var directory in Directory.GetDirectories(path, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
    }

    private sealed record Options(string? Out, string? Lookups, string? Reference, string? Osdu, string? CacheRecords, int Logs, ulong Seed, string Date)
    {
        public static Options Parse(string[] args)
        {
            string? output = null;
            string? lookups = null;
            string? reference = null;
            string? osdu = null;
            string? cacheRecords = null;
            int? logs = null;
            ulong? seed = null;
            string? date = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < args.Length; i++)
            {
                var name = args[i];
                if (!seen.Add(name))
                {
                    throw new ArgumentException($"{name} is given more than once.");
                }

                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"{name} needs a value.");
                }

                var value = args[++i];
                switch (name)
                {
                    case "--out":
                        output = Folder(name, value);
                        break;
                    case "--lookups":
                        lookups = Folder(name, value);
                        break;
                    case "--reference":
                        reference = Folder(name, value);
                        break;
                    case "--osdu":
                        osdu = Folder(name, value);
                        break;
                    case "--cache-records":
                        cacheRecords = Folder(name, value);
                        break;
                    case "--logs":
                        logs = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count is >= 1 and <= SyntheticEstate.MaxLogs
                            ? count
                            : throw new ArgumentException($"--logs is a whole number from 1 to {SyntheticEstate.MaxLogs.ToString(CultureInfo.InvariantCulture)}, not '{value}'.");
                        break;
                    case "--seed":
                        seed = ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                            ? parsed
                            : throw new ArgumentException($"--seed is an unsigned 64-bit whole number, not '{value}'.");
                        break;
                    case "--date":
                        date = DateStamp().IsMatch(value) && DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                            ? value
                            : throw new ArgumentException($"--date is a calendar date written yyyyMMdd, not '{value}'.");
                        break;
                    default:
                        throw new ArgumentException($"Unknown argument {name}.");
                }
            }

            if (new[] { output, lookups, reference }.Count(o => o is not null) != 1)
            {
                throw new ArgumentException("Name exactly one of --out (the well logs), --lookups (the lookup tables) and --reference (the reference data).");
            }

            if (output is null && (logs is not null || seed is not null))
            {
                throw new ArgumentException("--logs and --seed choose the well logs; the lookup tables and the reference data take neither.");
            }

            if ((reference is null) != (osdu is null) || (cacheRecords is not null && reference is null))
            {
                throw new ArgumentException("--reference reads the OSDU values from --osdu, and --osdu and --cache-records go with --reference alone.");
            }

            return new Options(
                output,
                lookups,
                reference,
                osdu,
                cacheRecords,
                logs ?? SyntheticEstate.DefaultLogs,
                seed ?? SyntheticEstate.DefaultSeed,
                date ?? DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        }

        private static string Folder(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException($"{name} names a folder.");
            }

            try
            {
                var full = Path.GetFullPath(value);
                return Path.GetPathRoot(full) == full
                    ? throw new ArgumentException($"{name} names the root of a drive ({full}); name the folder the sample data belongs in.")
                    : full;
            }
            catch (Exception ex) when (ex is NotSupportedException or PathTooLongException or System.Security.SecurityException)
            {
                throw new ArgumentException($"{name} '{value}' is not a folder path: {ex.Message}", ex);
            }
        }
    }
}
