using System.Globalization;
using SqlFlow.Delivery.Tests;

namespace SqlFlow.Delivery.Tools.SampleData;

/// <summary>
/// The sample estate's data, made up: a handful of well logs with their curve grids, and the lookup tables the sample
/// mapping translates their values through. Nothing here comes from a real well, a real company or a real database; every
/// value is generated from a seed, so the same seed always gives the same logs, byte for byte, and the suites can check
/// that the committed files are exactly what the generator writes (SampleDataGeneratorTests).
/// <para>The logs keep the shape the sample flows read and the relationships the suites rely on. The first log is a full
/// suite logged from the surface (its index curve starts at depth 0), the second a density-neutron suite with a
/// photoelectric factor, and the rest gamma ray and resistivity alone; two of them are the sidetracks A and B of one
/// wellbore, which share its elevation and were updated together. The grid of each log is built the way a well database's
/// export builds it: depths from the lowest curve top to the highest curve base at the log's increment, and each curve
/// holding a value at every depth between its own top and base and a gap elsewhere.</para>
/// </summary>
public static class SyntheticEstate
{
    /// <summary>The seed the committed sample data was generated with.</summary>
    public const ulong DefaultSeed = 20260924;

    /// <summary>How many logs the committed sample data holds.</summary>
    public const int DefaultLogs = 5;

    /// <summary>The most logs one estate holds: an example, never a load test.</summary>
    public const int MaxLogs = 40;

    /// <summary>The source project every sample log belongs to.</summary>
    public const string Project = "PROJECT_A";

    /// <summary>What the well database records as the source and the creator of every log it holds.</summary>
    public const string SourceSystem = "WELLDB";

    /// <summary>The sampling increment of every sample log: half a foot, in metres.</summary>
    public const double Increment = 0.1524;

    /// <summary>The folder under the cache data root holding the curve unit spellings.</summary>
    public const string UnitAliasFolder = "unit-alias";

    /// <summary>The folder under the cache data root holding the depth unit spellings.</summary>
    public const string DepthUnitAliasFolder = "depth-unit-alias";

    /// <summary>The folder under the cache data root holding the curve dictionary.</summary>
    public const string CurveDictionaryFolder = "curve-dictionary";

    /// <summary>The columns of both unit spelling files.</summary>
    public static IReadOnlyList<string> UnitAliasColumns { get; } = ["source_unit", "osdu_unit"];

    /// <summary>The columns of the curve dictionary file.</summary>
    public static IReadOnlyList<string> CurveDictionaryColumns { get; } =
        ["mnemonic", "log_curve_type_id", "log_curve_main_family_id", "log_curve_family_id", "unit_quantity_id", "unit", "comment"];

    /// <summary>The curve suites a log is logged with, in the order the logs take them (a sixth log starts again).</summary>
    private static readonly string[][] Suites =
    [
        ["BS", "CALI", "DRHO", "DT", "DTS", "GR", "NPHI", "RD", "RHOB"],
        ["DRHO", "GR", "NPHI", "PEF", "RD", "RHOB"],
        ["GR", "RD"],
        ["GR", "RD"],
        ["GR", "RD"],
    ];

    /// <summary>
    /// The wellbore of each log in a round of five, as (well, wellbore, sidetrack): the third and fourth logs are the
    /// sidetracks A and B of the same wellbore, so their names share a prefix and their well shares an elevation.
    /// </summary>
    private static readonly (int Well, int Wellbore, string? Sidetrack)[] Wellbores =
    [
        (0, 1, null),
        (1, 1, null),
        (1, 2, "A"),
        (1, 2, "B"),
        (2, 1, null),
    ];

    /// <summary>
    /// The curve types a sample log can carry: the spelling the well database writes the unit in, the description it
    /// gives, and the physical range of the values a log of that type holds.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, CurveType> CurveTypes = new Dictionary<string, CurveType>(StringComparer.Ordinal)
    {
        ["BS"] = new("IN", "Bit Size", 8.5, 12.25, Shape.Constant),
        ["CALI"] = new("IN", "Caliper [hole diameter]", 0, 1.6, Shape.AboveBitSize),
        ["DRHO"] = new("G/CC", "Density correction", -0.05, 0.12, Shape.Linear),
        ["DT"] = new("US/F", "Compressional Slowness", 55, 140, Shape.Linear),
        ["DTS"] = new("US/F", "Shear Slowness", 90, 260, Shape.Linear),
        ["GR"] = new("GAPI", "Gamma Ray", 15, 150, Shape.Linear),
        ["NPHI"] = new("V/V", "Neutron Porosity", 0.03, 0.45, Shape.Linear),
        ["PEF"] = new("B/E", "Photoelectric Factor", 1.8, 5.5, Shape.Linear),
        ["RD"] = new("OHMM", "Deep resistivity", 0.3, 300, Shape.Logarithmic),
        ["RHOB"] = new("G/CC", "Bulk Density", 1.95, 2.75, Shape.Linear),
    };

    /// <summary>
    /// <paramref name="count"/> sample logs generated from <paramref name="seed"/>, in the order a well database lists them
    /// (by wellbore). A log's values depend on the seed and its position only, so asking for more logs adds logs and
    /// leaves the earlier ones as they were.
    /// </summary>
    public static IReadOnlyList<SampleLog> WellLogs(int count, ulong seed)
    {
        if (count is < 1 or > MaxLogs)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, $"A sample estate holds between 1 and {MaxLogs} logs.");
        }

        var logs = new List<SampleLog>(count);
        for (var index = 0; index < count; index++)
        {
            logs.Add(WellLog(index, seed));
        }

        return logs.OrderBy(l => l.WellboreUwi, StringComparer.Ordinal).ToList();
    }

    /// <summary>The curve unit spellings the well database writes, each with the code of the UnitOfMeasure record it means.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string?>> UnitAliasRows() => Rows(UnitAliasColumns,
    [
        ["%", "%"],
        ["1/S", "1/s"],
        ["B/CC", "b/cm3"],
        ["B/E", "b/e"],
        ["B/ELEC", "b/e"],
        ["BAR", "bar"],
        ["CPS", "1/s"],
        ["CU", "cu"],
        ["DAPI", "dAPI"],
        ["DEG", "dega"],
        ["DEGC", "degC"],
        ["DEGF", "degF"],
        ["F/HR", "ft/h"],
        ["FRAC", "v/v"],
        ["G/CC", "g/cm3"],
        ["G/CM3", "g/cm3"],
        ["GAPI", "gAPI"],
        ["GPA", "GPa"],
        ["HR", "h"],
        ["IN", "in"],
        ["INCH", "in"],
        ["K/M3", "kg/m3"],
        ["KG/M3", "kg/m3"],
        ["KLBF", "klbf"],
        ["KNM", "kN.m"],
        ["KPA", "kPa"],
        ["KPA.S/M", "kPa.s/m"],
        ["M", "m"],
        ["M/HR", "m/h"],
        ["M/MIN", "m/min"],
        ["M/S", "m/s"],
        ["M3", "m3"],
        ["M3/D", "m3/d"],
        ["M3/M3", "m3/m3"],
        ["MD", "mD"],
        ["MD/CP", "mD/cP"],
        ["MD/MD", "mD/mD"],
        ["MIN", "min"],
        ["MM", "mm"],
        // A pressure written in upper case is megapascals: the partition also holds mPa, which it never means.
        ["MPA", "MPa"],
        ["MPA.S/M", "MPa.s/m"],
        ["MS", "ms"],
        ["MV", "mV"],
        ["NAPI", "nAPI"],
        ["OHM.M", "ohm.m"],
        ["OHMM", "ohm.m"],
        ["PPM", "ppm"],
        ["PSI", "psi"],
        ["PSIA", "psia"],
        ["RPM", "rpm"],
        ["T", "t"],
        ["UNITLESS", "unitless"],
        ["US", "us"],
        ["US/F", "us/ft"],
        ["US/FT", "us/ft"],
        ["US/M", "us/m"],
        ["V/V", "v/v"],
    ]);

    /// <summary>The depth and elevation unit spellings the well database writes, each with the UnitOfMeasure code it means.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string?>> DepthUnitAliasRows() => Rows(UnitAliasColumns,
    [
        ["M", "m"],
        ["metre", "m"],
        ["FT", "ft"],
        ["FEET", "ft"],
    ]);

    /// <summary>
    /// The curve dictionary: what each mnemonic is, as the codes of the partition's LogCurveType, LogCurveMainFamily and
    /// LogCurveFamily records its id ends with (percent-escaped, as an id writes them), the unit quantity, and the unit
    /// of a curve of that type. A type the partition defines itself carries the <c>Local-</c> prefix. The comment is the
    /// word null where an entry has none, as the export writes it.
    /// </summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string?>> CurveDictionaryRows() => Rows(CurveDictionaryColumns,
    [
        ["MD", "Local-DEPTH", "Reference", "Local-Measured%20Depth", "length", "m", "null"],
        ["BS", "Local-BS", "BoreholeProperties", "Local-Bit%20Size", "length", "in", "null"],
        ["CALI", "Local-CALI", "BoreholeProperties", "Caliper", "length", "in", "null"],
        ["GR", "Local-GR", "GammaRay", "Gamma%20Ray", "API%20gamma%20ray", "gAPI", "null"],
        ["GRC", "Local-GR", "GammaRay", "Gamma%20Ray", "API%20gamma%20ray", "gAPI", "Environmentally corrected gamma ray; filed as GR"],
        ["GRN", "Local-GRN", "GammaRay", "Gamma%20Ray%20Normalized", "API%20gamma%20ray", "gAPI", "null"],
        ["K", "Local-K", "WeightFraction-Element", "Local-Potassium%20Weight%20Fraction", "mass%20per%20mass", "%", "null"],
        ["TH", "Local-TH", "WeightFraction-Element", "Local-Thorium%20Weight%20Fraction", "mass%20per%20mass", "ppm", "null"],
        ["U", "Local-U", "WeightFraction-Element", "Local-Uranium%20Weight%20Fraction", "mass%20per%20mass", "ppm", "null"],
        ["SP", "Local-SP", "SpontaneousPotential", "Spontaneous%20Potential", "electric%20potential%20difference", "mV", "null"],
        ["RD", "Local-RD", "Resistivity", "Resistivity%20-%20Deep", "electrical%20resistivity", "ohm.m", "null"],
        ["RDEP", "Local-RD", "Resistivity", "Resistivity%20-%20Deep", "electrical%20resistivity", "ohm.m", "Deep resistivity under another mnemonic; filed as RD"],
        ["RM", "Local-RM", "Resistivity", "Resistivity%20-%20Medium", "electrical%20resistivity", "ohm.m", "null"],
        ["RS", "Local-RS", "Resistivity", "Resistivity%20-%20Shallow", "electrical%20resistivity", "ohm.m", "null"],
        ["RT", "Local-RT", "Resistivity", "Resistivity%20-%20True%20Formation", "electrical%20resistivity", "ohm.m", "null"],
        ["RXO", "Local-RXO", "Resistivity", "Resistivity%20-%20Micro", "electrical%20resistivity", "ohm.m", "null"],
        ["RW", "Local-RW", "FluidProperties", "Water%20Resistivity", "electrical%20resistivity", "ohm.m", "null"],
        ["RHOB", "Local-RHOB", "Density", "Bulk%20Density", "mass%20per%20volume", "g/cm3", "null"],
        ["DRHO", "Local-DRHO", "Density", "Bulk%20Density%20Correction", "mass%20per%20volume", "g/cm3", "null"],
        ["NPHI", "Local-NPHI", "Porosity", "Local-Neutron%20Porosity", "volume%20per%20volume", "v/v", "null"],
        ["PHID", "Local-PHID", "Porosity", "Local-Density%20Porosity", "volume%20per%20volume", "v/v", "null"],
        ["PHIE", "Local-PHIE", "Porosity", "Local-Effective%20Porosity", "volume%20per%20volume", "v/v", "null"],
        ["PHIT", "Local-PHIT", "Porosity", "Local-Total%20Porosity", "volume%20per%20volume", "v/v", "null"],
        ["DT", "Local-DT", "Slowness", "Compressional%20Slowness", "time%20per%20length", "us/ft", "null"],
        ["DTS", "Local-DTS", "Slowness", "Shear%20Slowness", "time%20per%20length", "us/ft", "null"],
        ["DTST", "Local-DTST", "Slowness", "Stoneley%20Slowness", "time%20per%20length", "us/ft", "null"],
        ["PEF", "Local-PEF", "PhotoelectricFactor", "Photoelectric%20Factor", "dimensionless", "b/e", "null"],
        ["SWE", "Local-SWE", "Saturation", "Local-Effective%20Water%20Saturation", "volume%20per%20volume", "v/v", "null"],
        ["SWT", "Local-SWT", "Saturation", "Local-Water%20Saturation", "volume%20per%20volume", "v/v", "null"],
        ["SXOT", "Local-SXOT", "Saturation", "Local-Water%20Saturation", "volume%20per%20volume", "v/v", "null"],
        ["VSH", "Local-VSH", "VolumeFraction-Matrix", "Local-Shale%20Volume%20Fraction", "volume%20per%20volume", "v/v", "null"],
        ["VCALC", "Local-VCALC", "VolumeFraction-Matrix", "Local-Calcite%20Volume%20Fraction", "volume%20per%20volume", "v/v", "null"],
        ["VDOLO", "Local-VDOLO", "VolumeFraction-Matrix", "Local-Dolomite%20Volume%20Fraction", "volume%20per%20volume", "v/v", "null"],
        ["KLOGH", "Local-KLOGH", "Permeability", "Horizontal%20Permeability", "permeability%20rock", "mD", "null"],
        ["KLOGV", "Local-KLOGV", "Permeability", "Vertical%20Permeability", "permeability%20rock", "mD", "null"],
        ["KSDR", "Local-KSDR", "Permeability", "NMR%20Permeability", "permeability%20rock", "mD", "null"],
        ["KTIM", "Local-KTIM", "Permeability", "NMR%20Permeability", "permeability%20rock", "mD", "null"],
        ["FTEMP", "Local-FTEMP", "Temperature", "Local-Formation%20Temperature", "thermodynamic%20temperature", "degC", "null"],
        ["ROP", "Local-ROP", "DrillingParameters", "Local-Rate%20Of%20Penetration", "length%20per%20time", "m/h", "null"],
        ["WOB", "Local-WOB", "DrillingParameters", "Local-Weight%20On%20Bit", "mass", "t", "null"],
        ["BVW", "Local-BVW", "VolumeFraction", "Local-Water%20Volume%20Fraction", "volume%20per%20volume", "v/v", "null"],
        ["FFV", "Local-FFV", "VolumeFraction", "NMR%20Free%20Fluid%20Volume%20Fraction", "volume%20per%20volume", "v/v", "null"],
    ]);

    /// <summary>
    /// Writes the lookup tables under <paramref name="cacheDataRoot"/>: one file per table in a folder of its own, named for
    /// <paramref name="date"/> (yyyyMMdd), as the lookup tables' pre-ingestion flows read them.
    /// </summary>
    public static async Task WriteLookupsAsync(string cacheDataRoot, string date, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(date);
        await SampleWellLogs.WriteCsvAsync(LookupFile(cacheDataRoot, UnitAliasFolder, "unit_alias", date), UnitAliasColumns, UnitAliasRows(), ct).ConfigureAwait(false);
        await SampleWellLogs.WriteCsvAsync(LookupFile(cacheDataRoot, DepthUnitAliasFolder, "depth_unit_alias", date), UnitAliasColumns, DepthUnitAliasRows(), ct).ConfigureAwait(false);
        await SampleWellLogs.WriteCsvAsync(LookupFile(cacheDataRoot, CurveDictionaryFolder, "curve_dictionary", date), CurveDictionaryColumns, CurveDictionaryRows(), ct).ConfigureAwait(false);
    }

    /// <summary>The name a wellbore of the estate has: its well's letter, its number, and its sidetrack when it has one.</summary>
    public static string WellboreName(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        var (well, wellbore, sidetrack) = WellboreOf(index);
        var name = string.Create(CultureInfo.InvariantCulture, $"Wellbore {(char)('A' + well)}-{wellbore}");
        return sidetrack is null ? name : name + " " + sidetrack;
    }

    /// <summary>
    /// The id of the log at <paramref name="index"/>: the log and its version, as the well database writes them, so
    /// LOG-0001/1 for the first. The slash is what a key part, an OSDU id made from a key and a payload folder must each
    /// carry through (escaped, or as an underscore in a folder name).
    /// </summary>
    public static string LogId(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return string.Create(CultureInfo.InvariantCulture, $"LOG-{index + 1:D4}/1");
    }

    private static string LookupFile(string root, string folder, string stem, string date) => Path.Combine(root, folder, $"{stem}_{date}.csv");

    private static IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows(IReadOnlyList<string> columns, string[][] cells)
    {
        var rows = new List<IReadOnlyDictionary<string, string?>>(cells.Length);
        foreach (var row in cells)
        {
            if (row.Length != columns.Count)
            {
                throw new InvalidOperationException($"A lookup row of the catalog has {row.Length} cells where the table has {columns.Count} columns: {string.Join(",", row)}.");
            }

            rows.Add(columns.Select((column, i) => (column, row[i])).ToDictionary(c => c.column, c => (string?)c.Item2, StringComparer.Ordinal));
        }

        return rows;
    }

    /// <summary>The wellbore of the log at <paramref name="index"/>: each round of five takes the next three well letters.</summary>
    private static (int Well, int Wellbore, string? Sidetrack) WellboreOf(int index)
    {
        var (well, wellbore, sidetrack) = Wellbores[index % Wellbores.Length];
        return (well + (3 * (index / Wellbores.Length)), wellbore, sidetrack);
    }

    private static SampleLog WellLog(int index, ulong seed)
    {
        // Each log draws from a stream of its own, so its values depend on the seed and its position alone.
        var random = new SplitMix64(seed ^ (0x9E3779B97F4A7C15UL * (ulong)(index + 1)));
        var (well, _, _) = WellboreOf(index);
        var wellRandom = new SplitMix64(seed ^ (0xC2B2AE3D27D4EB4FUL * (ulong)(well + 1)));
        var suite = Suites[index % Suites.Length];
        var logId = LogId(index);

        // The wellbore's elevation and the moment the log last changed belong to the well, so the two sidetracks of one
        // wellbore were measured from the same point and updated together.
        var elevation = Math.Round(wellRandom.Between(20, 90), 1, MidpointRounding.AwayFromZero);
        var updated = new DateTime(2026, 8, 18, 10, 0, 0, DateTimeKind.Utc).AddSeconds(Math.Floor(wellRandom.Between(0, 7200)));
        var recorded = new DateTime(2021, 11, 10, 6, 0, 0, DateTimeKind.Utc)
            .AddDays(index * 2)
            .AddSeconds(Math.Floor(random.Between(0, 43200)));

        // The log's interval: a whole number of increments below the surface, a few hundred samples long. The first log
        // of every round is logged from the surface, so its index curve starts at depth 0 while its other curves cover
        // only the interval its tools were run over.
        var rows = (int)random.Between(120, 460);
        var startStep = (long)random.Between(9_000, 21_000);
        var curves = new List<SampleCurve>(suite.Length + 1);
        var placed = new List<(string CurveId, int First, int Last)>(suite.Length);
        foreach (var curveId in suite)
        {
            // One curve of the suite spans the whole interval; the others start and end a few samples in.
            var first = placed.Count == 0 ? 0 : (int)random.Between(0, 12);
            var last = placed.Count == 0 ? rows - 1 : rows - 1 - (int)random.Between(0, 12);
            placed.Add((curveId, first, last));
        }

        var depths = Enumerable.Range(0, rows).Select(i => Depth((startStep + i) * Increment)).ToList();
        var surface = index % Suites.Length == 0;
        var indexMin = surface ? 0 : depths[0];
        var indexMax = surface ? Depth(depths[^1] + Math.Round(random.Between(250, 1800), 0)) : depths[^1];

        curves.Add(new SampleCurve(SampleWellLogs.IndexCurveId, "M", "Measured depth", "HIGH", indexMin, indexMax, null, null));
        var values = new Dictionary<string, IReadOnlyList<double?>>(StringComparer.Ordinal);
        var bitSize = random.Between(0, 1) < 0.5 ? 8.5 : 12.25;
        for (var c = 0; c < placed.Count; c++)
        {
            var (curveId, first, last) = placed[c];
            var type = CurveTypes[curveId];
            values[curveId] = Series(type, rows, first, last, bitSize, random);

            // A curve the source holds no business value for is written without one; the index curve always has one.
            var businessValue = random.Between(0, 1) < 0.3 ? null : "HIGH";
            var stamp = recorded.AddSeconds(c / 2).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            curves.Add(new SampleCurve(curveId, type.Unit, type.Description, businessValue, depths[first], depths[last], "1", stamp));
        }

        return new SampleLog(
            Project, logId, WellboreName(index), "COMPOSITE", "C", SourceSystem, "DEPTH", "M",
            indexMin, indexMax, Increment, "REGULAR",
            elevation.ToString("0.0", CultureInfo.InvariantCulture) + " M", "KB", SourceSystem, "HYBRID", "1", "INTERPRETED", SourceSystem,
            "C", "FINAL", logId, updated, curves, depths, values);
    }

    /// <summary>
    /// One curve's values over the grid: a smooth walk through the curve type's physical range between its first and last
    /// sample, and a gap at every other depth. Values are rounded to four decimals, as an export writes them.
    /// </summary>
    private static List<double?> Series(CurveType type, int rows, int first, int last, double bitSize, SplitMix64 random)
    {
        var values = new double?[rows];
        var level = random.Between(0.2, 0.8);
        for (var i = first; i <= last; i++)
        {
            // A mean-reverting walk in the unit interval: beds come and go without the curve leaving its range.
            level = Math.Clamp(level + (0.15 * (0.5 - level)) + (0.06 * random.Normal()), 0, 1);
            var value = type.Shape switch
            {
                Shape.Constant => bitSize,
                Shape.AboveBitSize => bitSize + type.Min + (level * (type.Max - type.Min)),
                Shape.Logarithmic => Math.Exp(Math.Log(type.Min) + (level * (Math.Log(type.Max) - Math.Log(type.Min)))),
                _ => type.Min + (level * (type.Max - type.Min)),
            };
            values[i] = Math.Round(value, 4, MidpointRounding.AwayFromZero);
        }

        return [.. values];
    }

    private static double Depth(double value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);

    private enum Shape
    {
        Linear,
        Logarithmic,
        Constant,
        AboveBitSize,
    }

    private sealed record CurveType(string Unit, string Description, double Min, double Max, Shape Shape);

    /// <summary>
    /// SplitMix64: a small generator whose sequence is fixed by its seed on every platform and runtime, which
    /// <see cref="Random"/> does not promise, so a seed names the same estate wherever the generator runs.
    /// </summary>
    private sealed class SplitMix64(ulong seed)
    {
        private ulong _state = seed;

        private bool _hasSpare;

        private double _spare;

        /// <summary>The next value, uniform in [0, 1).</summary>
        public double Next()
        {
            _state += 0x9E3779B97F4A7C15UL;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (z >> 11) * (1.0 / (1UL << 53));
        }

        /// <summary>A value uniform in [<paramref name="min"/>, <paramref name="max"/>).</summary>
        public double Between(double min, double max) => min + (Next() * (max - min));

        /// <summary>A value of the standard normal distribution (Box-Muller, the second value of a pair kept for the next call).</summary>
        public double Normal()
        {
            if (_hasSpare)
            {
                _hasSpare = false;
                return _spare;
            }

            var u1 = 1.0 - Next();
            var u2 = Next();
            var radius = Math.Sqrt(-2.0 * Math.Log(u1));
            _spare = radius * Math.Sin(2.0 * Math.PI * u2);
            _hasSpare = true;
            return radius * Math.Cos(2.0 * Math.PI * u2);
        }
    }
}
