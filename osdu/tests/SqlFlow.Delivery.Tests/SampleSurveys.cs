using System.Globalization;
using System.Text;
using Parquet;
using Parquet.Schema;
using SqlFlow.Delivery.Hashing;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Storage;

namespace SqlFlow.Delivery.Tests;

/// <summary>One property a sample survey's stations carry, as the well database's survey metadata describes it.</summary>
/// <param name="Name">The property's column in the payload file, which the record names it by.</param>
/// <param name="PropertyType">The code of the TrajectoryStationPropertyType record the property is (MD, Inclination, AzimuthTN).</param>
/// <param name="Unit">The unit as the well database writes it (M, DEG), which the mapping translates through the cache.</param>
public sealed record SampleStationProperty(string Name, string PropertyType, string Unit);

/// <summary>
/// One directional survey of the sample estate: its header row, the properties its stations carry, and the stations its
/// payload file holds (one row per station, one column per property, the measured depth first).
/// </summary>
public sealed record SampleSurvey(
    string SourceProject,
    string SurveyId,
    string SurveyName,
    string SurveyType,
    string SurveyVersion,
    string WellboreUwi,
    string ElevMeasRef,
    DateTime UpdateDateUtc,
    IReadOnlyList<SampleStationProperty> Properties,
    IReadOnlyList<IReadOnlyList<double>> Stations)
{
    /// <summary>
    /// Where the survey's payload file sits, relative to the flow's payload root: a folder per project and one per survey,
    /// a slash of the survey id written as an underscore, as a log's folder is.
    /// </summary>
    public string Folder => $"{SourceProject}/{SurveyId.Replace('/', '_')}";

    /// <summary>The delivery key the mapping derives for this survey, which is what the ledger holds it under.</summary>
    public DeliveryKey Key => DeliveryKey.Derive(SampleWellLogs.System, [SourceProject, SurveyId]);

    /// <summary>The measured depth of the first station, which the record carries as its top.</summary>
    public double TopMd => Stations[0][0];

    /// <summary>The measured depth of the last station, which the record carries as its base.</summary>
    public double BaseMd => Stations[^1][0];

    /// <summary>The stations as the payload file holds them: one row per station, keyed by the property's column.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Grid()
        => [.. Stations.Select(station => (IReadOnlyDictionary<string, object?>)Properties
            .Select((property, i) => (property.Name, Value: (object?)station[i]))
            .ToDictionary(c => c.Name, c => c.Value, StringComparer.Ordinal))];

    /// <summary>
    /// The payload's content hash, written as a log's is: a SHA-256 over the stations as text, one line per station,
    /// values separated by the unit separator and written round-trip. It is what the survey row carries in
    /// <c>payload_hash</c>, and what a plan compares to decide whether the stations moved.
    /// </summary>
    public string StationHash()
    {
        var text = new StringBuilder();
        foreach (var station in Stations)
        {
            text.Append(string.Join('\u001F', station.Select(value => value.ToString("R", CultureInfo.InvariantCulture)))).Append('\n');
        }

        return ContentHash.Of(text.ToString());
    }
}

/// <summary>One marker of a sample marker set: the formation top it picks and the measured depth it is picked at.</summary>
public sealed record SampleMarker(string Name, double MeasuredDepth);

/// <summary>One set of formation tops the well database holds for a wellbore, as its marker set and marker files carry it.</summary>
public sealed record SampleMarkerSet(
    string SourceProject,
    string MarkerSetId,
    string Name,
    string WellboreUwi,
    string ElevMeasRef,
    DateTime UpdateDateUtc,
    IReadOnlyList<SampleMarker> Markers)
{
    /// <summary>The delivery key the mapping derives for this marker set.</summary>
    public DeliveryKey Key => DeliveryKey.Derive(SampleWellLogs.System, [SourceProject, MarkerSetId]);
}

/// <summary>
/// The sample estate's directional surveys and formation tops, in one place: one synthetic survey and one marker set per
/// wellbore of the made-up well database, as the generator writes them (osdu/tools/SampleData), and the files that carry
/// them: the CSV files the pre flows read and the parquet file per survey whose stations the delivery streams to the
/// wellbore DDMS. The suites read the committed files back through <see cref="Surveys"/> and <see cref="MarkerSets"/>, as
/// they read the logs through <see cref="SampleWellLogs"/>.
/// </summary>
public static class SampleSurveys
{
    /// <summary>The folder under the data root the pre flow reads the survey header file from.</summary>
    public const string SurveyFolder = "survey";

    /// <summary>The folder under the data root the pre flow reads the station property file from.</summary>
    public const string PropertyFolder = "survey-properties";

    /// <summary>The folder under the data root the payload files sit in, one folder per survey below it.</summary>
    public const string PayloadFolder = "stations";

    /// <summary>The folder under the data root the pre flow reads the marker set file from.</summary>
    public const string MarkerSetFolder = "marker-set";

    /// <summary>The folder under the data root the pre flow reads the marker file from.</summary>
    public const string MarkerFolder = "marker";

    /// <summary>The payload file of one survey; each sample survey fits one chunk.</summary>
    public const string ChunkFileName = SampleWellLogs.ChunkFileName;

    /// <summary>The columns of the survey file the pre flow reads, in the order it writes them.</summary>
    public static IReadOnlyList<string> SurveyColumns { get; } =
    [
        "source_project", "survey_id", "survey_name", "survey_type", "survey_version", "wellbore_uwi", "top_md", "base_md",
        "elev_meas_ref", "update_date", "station_folder", "payload_hash", "chunk_count",
    ];

    /// <summary>The columns of the station property file: one row per property a survey's stations carry.</summary>
    public static IReadOnlyList<string> PropertyColumns { get; } =
        ["source_project", "survey_id", "station_ordinal", "station_property", "property_type", "property_unit"];

    /// <summary>The columns of the marker set file.</summary>
    public static IReadOnlyList<string> MarkerSetColumns { get; } =
        ["source_project", "marker_set_id", "marker_set_name", "wellbore_uwi", "elev_meas_ref", "update_date"];

    /// <summary>The columns of the marker file: one row per marker of a set.</summary>
    public static IReadOnlyList<string> MarkerColumns { get; } =
        ["source_project", "marker_set_id", "marker_ordinal", "marker_name", "marker_md"];

    private static readonly Lazy<IReadOnlyList<SampleSurvey>> CommittedSurveys = new(() => LoadSurveysAsync(SampleWellLogs.DataRoot).GetAwaiter().GetResult());

    private static readonly Lazy<IReadOnlyList<SampleMarkerSet>> CommittedMarkerSets = new(() => LoadMarkerSets(SampleWellLogs.DataRoot));

    /// <summary>The committed sample surveys, in the order their file lists them.</summary>
    public static IReadOnlyList<SampleSurvey> Surveys() => CommittedSurveys.Value;

    /// <summary>The committed sample marker sets, in the order their file lists them.</summary>
    public static IReadOnlyList<SampleMarkerSet> MarkerSets() => CommittedMarkerSets.Value;

    /// <summary>
    /// Reads the sample surveys under <paramref name="dataRoot"/>: the survey and station property files, and each survey's
    /// payload file, checking that the payload holds the properties the survey lists, that its first and last measured
    /// depths are the survey's top and base, and that the row's payload hash is the hash of its stations.
    /// </summary>
    public static async Task<IReadOnlyList<SampleSurvey>> LoadSurveysAsync(string dataRoot, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var headers = SampleWellLogs.ReadCsv(SampleWellLogs.SingleCsv(Path.Combine(dataRoot, SurveyFolder)));
        var properties = SampleWellLogs.ReadCsv(SampleWellLogs.SingleCsv(Path.Combine(dataRoot, PropertyFolder)))
            .GroupBy(r => (SampleWellLogs.Text(r, "source_project"), SampleWellLogs.Text(r, "survey_id")))
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(r => int.Parse(SampleWellLogs.Text(r, "station_ordinal"), CultureInfo.InvariantCulture))
                    .Select(r => new SampleStationProperty(SampleWellLogs.Text(r, "station_property"), SampleWellLogs.Text(r, "property_type"), SampleWellLogs.Text(r, "property_unit")))
                    .ToList());

        var surveys = new List<SampleSurvey>(headers.Count);
        foreach (var row in headers)
        {
            var key = (SampleWellLogs.Text(row, "source_project"), SampleWellLogs.Text(row, "survey_id"));
            if (!properties.TryGetValue(key, out var listed))
            {
                throw new InvalidDataException($"The sample survey {key.Item1}/{key.Item2} has no station property rows.");
            }

            var file = Path.Combine(dataRoot, PayloadFolder, SampleWellLogs.Text(row, "station_folder"), ChunkFileName);
            var stations = await ReadStationsAsync(file, listed, ct).ConfigureAwait(false);
            var survey = new SampleSurvey(
                key.Item1, key.Item2, SampleWellLogs.Text(row, "survey_name"), SampleWellLogs.Text(row, "survey_type"),
                SampleWellLogs.Text(row, "survey_version"), SampleWellLogs.Text(row, "wellbore_uwi"), SampleWellLogs.Text(row, "elev_meas_ref"),
                ParseMoment(SampleWellLogs.Text(row, "update_date")), listed, stations);
            if (survey.TopMd != SampleWellLogs.Double(row, "top_md") || survey.BaseMd != SampleWellLogs.Double(row, "base_md"))
            {
                throw new InvalidDataException(
                    $"The sample survey {survey.Folder} says it runs from {row["top_md"]} to {row["base_md"]}, but its stations run from {SampleWellLogs.Number(survey.TopMd)} to {SampleWellLogs.Number(survey.BaseMd)}.");
            }

            if (!string.Equals(survey.StationHash(), row["payload_hash"], StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The sample survey {survey.Folder} says its payload hash is {row["payload_hash"]}, but its stations hash to {survey.StationHash()}.");
            }

            surveys.Add(survey);
        }

        return surveys;
    }

    /// <summary>Reads the sample marker sets under <paramref name="dataRoot"/>: the marker set file and the marker file.</summary>
    public static IReadOnlyList<SampleMarkerSet> LoadMarkerSets(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var markers = SampleWellLogs.ReadCsv(SampleWellLogs.SingleCsv(Path.Combine(dataRoot, MarkerFolder)))
            .GroupBy(r => (SampleWellLogs.Text(r, "source_project"), SampleWellLogs.Text(r, "marker_set_id")))
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(r => int.Parse(SampleWellLogs.Text(r, "marker_ordinal"), CultureInfo.InvariantCulture))
                    .Select(r => new SampleMarker(SampleWellLogs.Text(r, "marker_name"), SampleWellLogs.Double(r, "marker_md")))
                    .ToList());

        return [.. SampleWellLogs.ReadCsv(SampleWellLogs.SingleCsv(Path.Combine(dataRoot, MarkerSetFolder))).Select(row =>
        {
            var key = (SampleWellLogs.Text(row, "source_project"), SampleWellLogs.Text(row, "marker_set_id"));
            return markers.TryGetValue(key, out var picked)
                ? new SampleMarkerSet(
                    key.Item1, key.Item2, SampleWellLogs.Text(row, "marker_set_name"), SampleWellLogs.Text(row, "wellbore_uwi"),
                    SampleWellLogs.Text(row, "elev_meas_ref"), ParseMoment(SampleWellLogs.Text(row, "update_date")), picked)
                : throw new InvalidDataException($"The sample marker set {key.Item1}/{key.Item2} has no marker rows.");
        })];
    }

    /// <summary>
    /// Writes the sample surveys and marker sets under <paramref name="dataRoot"/>: the survey, station property, marker set
    /// and marker files named for <paramref name="date"/> (yyyyMMdd), and one parquet file per survey under
    /// <c>stations/&lt;project&gt;/&lt;survey&gt;</c>. Writing the same surveys again produces exactly the same bytes.
    /// </summary>
    public static async Task WriteAsync(
        string dataRoot, IReadOnlyList<SampleSurvey> surveys, IReadOnlyList<SampleMarkerSet> markerSets, string date, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(surveys);
        ArgumentNullException.ThrowIfNull(markerSets);
        ArgumentException.ThrowIfNullOrWhiteSpace(date);
        await SampleWellLogs.WriteCsvAsync(Path.Combine(dataRoot, SurveyFolder, $"survey_{date}.csv"), SurveyColumns, [.. surveys.Select(SurveyRow)], ct).ConfigureAwait(false);
        await SampleWellLogs.WriteCsvAsync(Path.Combine(dataRoot, PropertyFolder, $"survey_properties_{date}.csv"), PropertyColumns, [.. surveys.SelectMany(PropertyRows)], ct).ConfigureAwait(false);
        await SampleWellLogs.WriteCsvAsync(Path.Combine(dataRoot, MarkerSetFolder, $"marker_set_{date}.csv"), MarkerSetColumns, [.. markerSets.Select(MarkerSetRow)], ct).ConfigureAwait(false);
        await SampleWellLogs.WriteCsvAsync(Path.Combine(dataRoot, MarkerFolder, $"marker_{date}.csv"), MarkerColumns, [.. markerSets.SelectMany(MarkerRows)], ct).ConfigureAwait(false);
        foreach (var survey in surveys)
        {
            await WriteChunkAsync(Path.Combine(dataRoot, PayloadFolder, survey.Folder), survey, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Writes one survey's payload file (its stations as one row group) and returns the file's path.</summary>
    public static async Task<string> WriteChunkAsync(string folder, SampleSurvey survey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(survey);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, ChunkFileName);
        var columns = survey.Properties.Select(p => (p.Name, typeof(double))).ToList();
        // As a log's grid is written: the measured depth an ordinary column and the rows labelled 0..n-1, which is how
        // the DDMS reads a trajectory's stations.
        var metadata = PandasMetadata.Range(columns, 0, survey.Stations.Count);

        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await ParquetFiles.WriteAsync(file, columns, survey.Grid(), metadata, ct).ConfigureAwait(false);
        return path;
    }

    /// <summary>The header row of one survey, as the file carries it.</summary>
    public static IReadOnlyDictionary<string, string?> SurveyRow(SampleSurvey survey)
    {
        ArgumentNullException.ThrowIfNull(survey);
        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["source_project"] = survey.SourceProject,
            ["survey_id"] = survey.SurveyId,
            ["survey_name"] = survey.SurveyName,
            ["survey_type"] = survey.SurveyType,
            ["survey_version"] = survey.SurveyVersion,
            ["wellbore_uwi"] = survey.WellboreUwi,
            ["top_md"] = SampleWellLogs.Number(survey.TopMd),
            ["base_md"] = SampleWellLogs.Number(survey.BaseMd),
            ["elev_meas_ref"] = survey.ElevMeasRef,
            ["update_date"] = SampleWellLogs.Moment(survey.UpdateDateUtc),
            ["station_folder"] = survey.Folder,
            ["payload_hash"] = survey.StationHash(),
            ["chunk_count"] = "1",
        };
    }

    /// <summary>The station property rows of one survey, in the order the record lists them.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string?>> PropertyRows(SampleSurvey survey)
    {
        ArgumentNullException.ThrowIfNull(survey);
        return [.. survey.Properties.Select((property, i) => (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["source_project"] = survey.SourceProject,
            ["survey_id"] = survey.SurveyId,
            ["station_ordinal"] = i.ToString(CultureInfo.InvariantCulture),
            ["station_property"] = property.Name,
            ["property_type"] = property.PropertyType,
            ["property_unit"] = property.Unit,
        })];
    }

    /// <summary>The row of one marker set, as the file carries it.</summary>
    public static IReadOnlyDictionary<string, string?> MarkerSetRow(SampleMarkerSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["source_project"] = set.SourceProject,
            ["marker_set_id"] = set.MarkerSetId,
            ["marker_set_name"] = set.Name,
            ["wellbore_uwi"] = set.WellboreUwi,
            ["elev_meas_ref"] = set.ElevMeasRef,
            ["update_date"] = SampleWellLogs.Moment(set.UpdateDateUtc),
        };
    }

    /// <summary>The marker rows of one set, shallowest first.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string?>> MarkerRows(SampleMarkerSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        return [.. set.Markers.Select((marker, i) => (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["source_project"] = set.SourceProject,
            ["marker_set_id"] = set.MarkerSetId,
            ["marker_ordinal"] = i.ToString(CultureInfo.InvariantCulture),
            ["marker_name"] = marker.Name,
            ["marker_md"] = SampleWellLogs.Number(marker.MeasuredDepth),
        })];
    }

    private static DateTime ParseMoment(string text)
        => DateTime.SpecifyKind(DateTime.ParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), DateTimeKind.Utc);

    private static async Task<IReadOnlyList<IReadOnlyList<double>>> ReadStationsAsync(string path, IReadOnlyList<SampleStationProperty> properties, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        await using var reader = await ParquetReader.CreateAsync(file, cancellationToken: ct).ConfigureAwait(false);
        var fields = reader.Schema.GetDataFields();
        var names = fields.Select(f => f.Name).ToList();
        if (!names.SequenceEqual(properties.Select(p => p.Name), StringComparer.Ordinal))
        {
            throw new InvalidDataException($"{path} holds the columns {string.Join(", ", names)}, where its survey lists {string.Join(", ", properties.Select(p => p.Name))}.");
        }

        var columns = names.ToDictionary(n => n, _ => new List<double>(), StringComparer.Ordinal);
        for (var group = 0; group < reader.RowGroupCount; group++)
        {
            using var rowGroup = reader.OpenRowGroupReader(group);
            foreach (DataField field in fields)
            {
                foreach (var value in await ParquetFiles.ReadColumnValuesAsync(rowGroup, field, ct).ConfigureAwait(false))
                {
                    columns[field.Name].Add(value is null
                        ? throw new InvalidDataException($"{path} has a station with no {field.Name}; every station of a sample survey holds every property.")
                        : Convert.ToDouble(value, CultureInfo.InvariantCulture));
                }
            }
        }

        var count = columns[names[0]].Count;
        return [.. Enumerable.Range(0, count).Select(i => (IReadOnlyList<double>)[.. names.Select(n => columns[n][i])])];
    }
}
