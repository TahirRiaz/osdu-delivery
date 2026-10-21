using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using SqlFlow.Delivery.Tests;

namespace SqlFlow.Delivery.Tools.SampleData;

/// <summary>
/// The reference data the sample delivers: every OSDU reference value the sample's records refer to, taken as OSDU
/// publishes it, and the curve types the well database defines itself. The OSDU values are read from the reference value
/// manifests of the OSDU data definitions (<c>ReferenceValues/Manifests/reference-data</c> of
/// <c>https://community.opengroup.org/osdu/data/data-definitions</c>), selected by what the sample refers to: the units
/// the lookup tables translate to, the families and main families the curve dictionary files curves under, the station
/// properties the surveys carry, the codes the mappings write as literals (<see cref="SyntheticEstate.MappedCodes"/>), and
/// the unit quantities those units, families, station properties and curve types are measured in. Nothing else is taken, so the reference data and the records referring to it are one
/// closed set: every reference a sample record makes names a record the sample delivers.
/// <para>A record carries the values OSDU publishes for it, except its name aliases (<c>NameAlias</c>), which would bring
/// in the alias types and standards organisations they name, and the property type of a family, which would bring in
/// the property type catalog; neither is read by anything the sample delivers.</para>
/// </summary>
public static class OsduReferenceValues
{
    /// <summary>The partition the sample's cache records are captured from, as the sample flows name it.</summary>
    public const string Partition = "dev";

    /// <summary>The folder under the reference data root holding the well database's own curve types.</summary>
    public const string LogCurveTypeFolder = "log-curve-type";

    private static readonly (string Column, string Field)[] Common =
    [
        ("code", "Code"),
        ("name", "Name"),
        ("description", "Description"),
        ("attribution_authority", "AttributionAuthority"),
        ("attribution_publication", "AttributionPublication"),
        ("attribution_revision", "AttributionRevision"),
        ("source", "Source"),
        ("commit_date", "CommitDate"),
    ];

    /// <summary>
    /// The OSDU reference kinds the sample delivers from OSDU's values: the entity type, the folder and file stem the
    /// pre flow reads, the manifest the values are published in, the columns written (each with the record field it holds),
    /// and the fields the reference cache captures for it.
    /// </summary>
    public static IReadOnlyList<ReferenceKind> Kinds { get; } =
    [
        new("UnitQuantity", "unit-quantity", "unit_quantity", "OPEN/UnitQuantity.1.json",
            [.. Common, ("id_value", "ID"), ("base_for_conversion", "BaseForConversion"), ("parent_unit_quantity", "ParentUnitQuantity"),
                ("unit_dimension", "UnitDimension"), ("persistable_reference", "PersistableReference")],
            ["Code", "Name", "ID"]),
        new("UnitOfMeasure", "unit-of-measure", "unit_of_measure", "OPEN/UnitOfMeasure.1.json",
            [.. Common, ("id_value", "ID"), ("unit_quantity", "UnitQuantityID"), ("is_base_unit", "IsBaseUnit"),
                ("coefficient_a", "CoefficientA"), ("coefficient_b", "CoefficientB"), ("coefficient_c", "CoefficientC"), ("coefficient_d", "CoefficientD"),
                ("unit_dimension_code", "UnitDimensionCode"), ("unit_dimension_name", "UnitDimensionName"), ("persistable_reference", "PersistableReference")],
            ["Code", "Name", "ID"]),
        new("LogCurveFamily", "log-curve-family", "log_curve_family", "LOCAL/LogCurveFamily.1.json",
            [.. Common, ("unit_quantity", "UnitQuantityID")], ["Code", "Name"]),
        new("LogCurveMainFamily", "log-curve-main-family", "log_curve_main_family", "LOCAL/LogCurveMainFamily.1.json", Common, ["Code", "Name"]),
        new("LogCurveBusinessValue", "log-curve-business-value", "log_curve_business_value", "LOCAL/LogCurveBusinessValue.1.json", Common, ["Code", "Name"]),
        new("LogType", "log-type", "log_type", "LOCAL/LogType.1.json", Common, ["Code", "Name"]),
        new("WellLogSamplingDomainType", "well-log-sampling-domain-type", "well_log_sampling_domain_type", "OPEN/WellLogSamplingDomainType.1.json", Common, ["Code", "Name"]),
        new("TechnicalAssuranceType", "technical-assurance-type", "technical_assurance_type", "FIXED/TechnicalAssuranceType.1.json", Common, ["Code", "Name"]),
        new("VerticalMeasurementType", "vertical-measurement-type", "vertical_measurement_type", "OPEN/VerticalMeasurementType.1.json", Common, ["Code", "Name"]),
        new("TrajectoryStationPropertyType", "trajectory-station-property-type", "trajectory_station_property_type", "OPEN/TrajectoryStationPropertyType.1.json",
            [.. Common, ("unit_quantity", "UnitQuantityID")], ["Code", "Name"]),
    ];

    /// <summary>
    /// The codes the sample refers to, by entity type, before the unit quantities they are measured in are added: what the
    /// lookup tables translate to, what the curve dictionary files curves under, the station properties the surveys carry,
    /// and what the mappings write as literals.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ReferencedCodes()
    {
        var codes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["UnitOfMeasure"] = Distinct(SyntheticEstate.UnitAliasRows().Concat(SyntheticEstate.DepthUnitAliasRows()).Select(r => r["osdu_unit"]!)),
            ["LogCurveMainFamily"] = Distinct(SyntheticEstate.CurveDictionaryRows().Select(r => r["log_curve_main_family_id"]!)),
            ["LogCurveFamily"] = Distinct(SyntheticEstate.CurveDictionaryRows().Select(r => Uri.UnescapeDataString(r["log_curve_family_id"]!))),
            ["TrajectoryStationPropertyType"] = Distinct(SyntheticEstate.StationProperties.Select(p => p.PropertyType)),
        };
        foreach (var (entity, mapped) in SyntheticEstate.MappedCodes)
        {
            codes[entity] = mapped;
        }

        return codes;
    }

    /// <summary>
    /// Reads the manifests under <paramref name="manifestsRoot"/> (the data definitions'
    /// <c>ReferenceValues/Manifests/reference-data</c>) and returns the rows of each kind the sample delivers, in code
    /// order: the codes it refers to, and the unit quantities of the units, families, station properties and curve types
    /// among them. A code the
    /// manifests do not publish fails with the code and the manifest named.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>> Select(string manifestsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestsRoot);
        var published = Kinds.ToDictionary(k => k.Entity, k => Published(manifestsRoot, k), StringComparer.Ordinal);
        var referenced = ReferencedCodes();
        var selected = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>>(StringComparer.Ordinal);
        foreach (var kind in Kinds.Where(k => k.Entity != "UnitQuantity"))
        {
            selected[kind.Entity] = referenced[kind.Entity]
                .Select(code => Row(kind, Find(published[kind.Entity], kind, code)))
                .OrderBy(r => r["code"], StringComparer.Ordinal)
                .ToList();
        }

        var quantities = selected["UnitOfMeasure"].Concat(selected["LogCurveFamily"]).Concat(selected["TrajectoryStationPropertyType"])
            .Concat(SyntheticEstate.LogCurveTypeRows())
            .Select(r => r["unit_quantity"]!);
        var quantityKind = Kinds.Single(k => k.Entity == "UnitQuantity");
        selected["UnitQuantity"] = Distinct(quantities)
            .Select(code => Row(quantityKind, Find(published["UnitQuantity"], quantityKind, code)))
            .OrderBy(r => r["code"], StringComparer.Ordinal)
            .ToList();
        return selected;
    }

    /// <summary>
    /// Writes the reference data under <paramref name="referenceRoot"/>: one file per kind in a folder of its own, named for
    /// <paramref name="date"/> (yyyyMMdd), as the reference pre flows read them, the OSDU kinds from the manifests under
    /// <paramref name="manifestsRoot"/> and the curve types from the well database's curve dictionary.
    /// </summary>
    public static async Task WriteAsync(string referenceRoot, string manifestsRoot, string date, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(date);
        var selected = Select(manifestsRoot);
        foreach (var kind in Kinds)
        {
            await SampleWellLogs.WriteCsvAsync(
                Path.Combine(referenceRoot, kind.Folder, $"{kind.Stem}_{date}.csv"), [.. kind.Columns.Select(c => c.Column)], selected[kind.Entity], ct).ConfigureAwait(false);
        }

        await SampleWellLogs.WriteCsvAsync(
            Path.Combine(referenceRoot, LogCurveTypeFolder, $"log_curve_type_{date}.csv"), SyntheticEstate.LogCurveTypeColumns, SyntheticEstate.LogCurveTypeRows(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The records a refresh of the sample's reference cache flow (<c>osdu-reference-00-cache</c>) captures once the
    /// sample's reference data is delivered, by entity type: each row of the reference files under
    /// <paramref name="referenceRoot"/> as the id the reference delivery gives it (the code, as <c>idFrom: key</c> writes
    /// it) and the fields the cache flow captures, the unit quantities among them.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string>>> CacheRecords(string referenceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceRoot);
        var kinds = Kinds.Select(k => (k.Entity, k.Folder, k.CacheFields))
            .Append(("LogCurveType", LogCurveTypeFolder, (IReadOnlyList<string>)["Code", "Name"]));
        var records = new SortedDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string>>>(StringComparer.Ordinal);
        foreach (var (entity, folder, fields) in kinds)
        {
            var file = Directory.GetFiles(Path.Combine(referenceRoot, folder), "*.csv").Single();
            records[entity] = SampleWellLogs.ReadCsv(file).Select(row =>
            {
                var item = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["id"] = $"{Partition}:reference-data--{entity}:{IdPart(row["code"]!)}",
                };
                foreach (var field in fields)
                {
                    var column = field == "ID" ? "id_value" : field.ToLowerInvariant();
                    item[field] = row[column] ?? throw new InvalidDataException($"{file}: the {entity} record {row["code"]} has no {column}, which the reference cache captures.");
                }

                return (IReadOnlyDictionary<string, string>)item;
            }).ToList();
        }

        return records;
    }

    /// <summary>
    /// Writes the records <see cref="CacheRecords"/> gives under <paramref name="cacheRecordsRoot"/>, one file per entity
    /// type in the form <c>sqlflow cache import</c> reads, replacing those files.
    /// </summary>
    public static async Task WriteCacheRecordsAsync(string cacheRecordsRoot, string referenceRoot, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRecordsRoot);
        Directory.CreateDirectory(cacheRecordsRoot);
        foreach (var (entity, items) in CacheRecords(referenceRoot))
        {
            await File.WriteAllTextAsync(Path.Combine(cacheRecordsRoot, entity + ".json"), CacheFile(entity, items), new UTF8Encoding(false), ct).ConfigureAwait(false);
        }
    }

    /// <summary>One cache record file as the generator writes it: the entity type, then one record per line.</summary>
    public static string CacheFile(string entity, IReadOnlyList<IReadOnlyDictionary<string, string>> items)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentNullException.ThrowIfNull(items);
        var options = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var text = new StringBuilder();
        text.Append("{\n").Append("  \"entityType\": ").Append(JsonSerializer.Serialize("reference-data--" + entity, options)).Append(",\n").Append("  \"items\": [\n");
        for (var i = 0; i < items.Count; i++)
        {
            var fields = items[i].Select(f => JsonSerializer.Serialize(f.Key, options) + ": " + JsonSerializer.Serialize(f.Value, options));
            text.Append("    { ").Append(string.Join(", ", fields)).Append(" }").Append(i < items.Count - 1 ? ",\n" : "\n");
        }

        return text.Append("  ]\n}\n").ToString();
    }

    /// <summary>
    /// A code as an OSDU id made from a key carries it: letters, digits, <c>_</c>, <c>-</c>, <c>.</c> and <c>:</c> as they
    /// are, every other character as its UTF-8 bytes percent-encoded with upper-case hex digits.
    /// </summary>
    public static string IdPart(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        var text = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(code))
        {
            var c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':'))
            {
                text.Append(c);
            }
            else
            {
                text.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return text.ToString();
    }

    private static Dictionary<string, JsonElement> Published(string manifestsRoot, ReferenceKind kind)
    {
        var path = Path.Combine(manifestsRoot, kind.Manifest);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{path} is not there: name the data definitions' ReferenceValues/Manifests/reference-data folder.", path);
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        if (!document.RootElement.TryGetProperty("ReferenceData", out var records) || records.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{path} holds no ReferenceData list.");
        }

        var byCode = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var record in records.EnumerateArray())
        {
            if (record.TryGetProperty("data", out var data) && data.TryGetProperty("Code", out var code) && code.ValueKind == JsonValueKind.String)
            {
                byCode[code.GetString()!] = data.Clone();
            }
        }

        return byCode;
    }

    private static JsonElement Find(Dictionary<string, JsonElement> published, ReferenceKind kind, string code) =>
        published.TryGetValue(code, out var data)
            ? data
            : throw new InvalidDataException($"The sample refers to the {kind.Entity} code '{code}', which {kind.Manifest} does not publish.");

    private static Dictionary<string, string?> Row(ReferenceKind kind, JsonElement data)
    {
        var row = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (column, field) in kind.Columns)
        {
            row[column] = data.TryGetProperty(field, out var value) ? Text(field, value) : null;
        }

        return row;
    }

    /// <summary>
    /// A field's value as the CSV cell holds it: text as it stands, a number as OSDU writes it, a boolean as true or false,
    /// and a reference to a unit quantity as the code its id ends with.
    /// </summary>
    private static string? Text(string field, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String when field == "UnitQuantityID" => QuantityCode(value.GetString()!),
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => throw new InvalidDataException($"The field {field} holds {value.ValueKind}, which a reference file does not carry."),
    };

    private static string QuantityCode(string id)
    {
        const string Marker = "reference-data--UnitQuantity:";
        var at = id.IndexOf(Marker, StringComparison.Ordinal);
        if (at < 0 || !id.EndsWith(':'))
        {
            throw new InvalidDataException($"'{id}' is not a reference to a UnitQuantity record.");
        }

        return Uri.UnescapeDataString(id[(at + Marker.Length)..^1]);
    }

    private static List<string> Distinct(IEnumerable<string> values) => values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
}

/// <summary>
/// One OSDU reference kind the sample delivers: its entity type, the folder and file stem its pre flow reads, the manifest
/// of the data definitions its values are published in, the columns written with the record field each holds, and the
/// fields the sample's reference cache flow captures.
/// </summary>
public sealed record ReferenceKind(
    string Entity,
    string Folder,
    string Stem,
    string Manifest,
    IReadOnlyList<(string Column, string Field)> Columns,
    IReadOnlyList<string> CacheFields);
