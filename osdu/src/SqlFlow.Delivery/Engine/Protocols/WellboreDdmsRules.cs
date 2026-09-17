using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>
/// The rules the Wellbore DDMS v3 applies to the records it keeps bulk data for, and to their bulk data, checked before
/// anything is sent so a record the service can only refuse is held with the rule it breaks
/// (osdu/specs/wellbore-ddms/INTEGRATION.md sections 3.1 and 4.3, from the service's <c>app/consistency</c> modules and
/// <c>app/bulk_persistence/consistency_checks.py</c> at the pinned commit). A value counts as given the way the
/// service's Python reads it: not null, not an empty string, array or object, not zero and not false. The rules that
/// need the bulk's values (a monotonic reference, sampling bounds) are the service's to check: the engine reads a
/// chunk's shape, never its rows.
/// </summary>
internal static partial class WellboreDdmsRules
{
    public const string WellLog = "work-product-component--WellLog";
    public const string WellboreTrajectory = "work-product-component--WellboreTrajectory";
    public const string PpfgDataset = "work-product-component--PPFGDataset";
    public const string PressureTest = "work-product-component--WellPressureTestRawMeasurement";

    private const string Curves = "Curves";
    private const string CurveId = "CurveID";
    private const string StationProperties = "AvailableTrajectoryStationProperties";

    /// <summary>
    /// Why the record breaks a rule the service applies when a record of <paramref name="entityType"/> is written, or,
    /// with <paramref name="withBulk"/>, when its bulk data is; null when it breaks none, and for every other type.
    /// </summary>
    public static string? RecordProblem(string entityType, JsonObject document, bool withBulk)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        ArgumentNullException.ThrowIfNull(document);
        if (document["data"] is not JsonObject data || data.Count == 0)
        {
            // The service checks nothing of a record without data (every check starts with "if not record.data").
            return null;
        }

        if (Is(entityType, WellLog))
        {
            return Duplicate(data, Curves, CurveId, "WellLog")
                ?? ReferenceProblem(data, "ReferenceCurveID", "WellLog")
                ?? (withBulk ? MissingCurveId(data, "WellLog") : null);
        }

        if (Is(entityType, WellboreTrajectory))
        {
            return Duplicate(data, StationProperties, "Name", "WellboreTrajectory");
        }

        if (Is(entityType, PpfgDataset))
        {
            return Required(data, "ContextTypeID")
                ?? Required(data, "ReferenceWellTrajectoryID")
                ?? Duplicate(data, Curves, CurveId, "PPFGDataset")
                ?? ReferenceProblem(data, "PrimaryReferenceCurveID", "PPFGDataset")
                ?? (withBulk ? MissingCurveId(data, "PPFGDataset") : null);
        }

        if (Is(entityType, PressureTest))
        {
            return Duplicate(data, Curves, CurveId, "WellPressureTestRawMeasurement")
                ?? (withBulk ? MissingCurveId(data, "WellPressureTestRawMeasurement") : null);
        }

        return null;
    }

    /// <summary>
    /// Why bulk data whose columns are <paramref name="labels"/> does not fit the record under <paramref name="columns"/>,
    /// or null when it does. The labels of all of a record's chunks are checked together, as the service checks the
    /// bulk they make up.
    /// </summary>
    public static string? ColumnsProblem(DdmsBulkColumns columns, JsonObject document, IReadOnlyCollection<string> labels)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(labels);
        if (columns == DdmsBulkColumns.Unchecked || document["data"] is not JsonObject data || data.Count == 0)
        {
            return null;
        }

        var curves = CurveColumns(labels);
        return columns switch
        {
            DdmsBulkColumns.CurveIds => CurveColumnsProblem(data, curves, widths: false),
            DdmsBulkColumns.CurveIdsAndWidths => CurveColumnsProblem(data, curves, widths: true),
            DdmsBulkColumns.TrajectoryStations => StationColumnsProblem(data, curves),
            _ => throw new ArgumentOutOfRangeException(nameof(columns), columns, "not a column rule"),
        };
    }

    /// <summary>
    /// The curves bulk columns belong to, with how many columns each has: a label <c>NAME[...]</c> is one column of the
    /// array curve <c>NAME</c>, any other non-empty label a curve of its own (the service's
    /// <c>_get_curve_name_and_column_count</c>).
    /// </summary>
    public static IReadOnlyDictionary<string, int> CurveColumns(IEnumerable<string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        var curves = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var label in labels)
        {
            if (ArrayColumn().Match(label) is { Success: true } match)
            {
                var name = match.Groups["name"].Value;
                curves[name] = curves.GetValueOrDefault(name) + 1;
            }
            else if (label.Length > 0)
            {
                curves[label] = 1;
            }
        }

        return curves;
    }

    private static string? CurveColumnsProblem(JsonObject data, IReadOnlyDictionary<string, int> curves, bool widths)
    {
        var declared = data[Curves] as JsonArray;
        if (!Given(data[Curves]))
        {
            return curves.Count == 0
                ? null
                : $"the bulk data has the column(s) {List(curves.Keys)} and data.Curves describes no curve; the Wellbore DDMS refuses bulk data whose columns are not curves of the record";
        }

        // A NumberOfColumns written as null counts as not given: the service drops null properties before it validates a
        // record, so holding such a record here could refuse one the service takes.
        var sizes = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var curve in (declared ?? []).OfType<JsonObject>())
        {
            if (Text(curve[CurveId]) is { } id)
            {
                sizes[id] = curve["NumberOfColumns"];
            }
        }

        var unmatched = curves.Keys.Where(name => !sizes.ContainsKey(name)).ToList();
        if (unmatched.Count > 0)
        {
            return $"the bulk column(s) {List(unmatched)} match no data.Curves[].CurveID of the record ({List(sizes.Keys)}); the Wellbore DDMS refuses bulk data whose columns are not curves of the record";
        }

        if (!widths)
        {
            return null;
        }

        var mismatched = sizes
            .Where(s => curves.TryGetValue(s.Key, out var count) && !Width(s.Value, count))
            .Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.Key} has {curves[s.Key]} column(s) in the bulk data and NumberOfColumns {(s.Value is null ? "1 (not given)" : s.Value.ToJsonString())}"))
            .ToList();
        return mismatched.Count == 0
            ? null
            : $"curve widths differ between the bulk data and the record: {string.Join("; ", mismatched)}. The Wellbore DDMS refuses bulk data whose curves have a different number of columns than data.Curves[].NumberOfColumns says";
    }

    private static string? StationColumnsProblem(JsonObject data, IReadOnlyDictionary<string, int> curves)
    {
        if (curves.Count == 0)
        {
            return null;
        }

        if (!data.ContainsKey(StationProperties))
        {
            return $"the bulk data has the column(s) {List(curves.Keys)} and the record has no data.{StationProperties}; the Wellbore DDMS refuses trajectory bulk data without it";
        }

        var names = Values(data[StationProperties], "Name").Select(Text).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var unmatched = curves.Keys.Where(name => !names.Contains(name)).ToList();
        return unmatched.Count == 0
            ? null
            : $"the bulk column(s) {List(unmatched)} match no data.{StationProperties}[].Name of the record ({List(names)}); the Wellbore DDMS refuses trajectory bulk data whose columns are not station properties of the record";
    }

    /// <summary>Whether a curve's declared <c>NumberOfColumns</c> (null when not given, which means 1) equals <paramref name="count"/>.</summary>
    private static bool Width(JsonNode? declared, int count)
    {
        if (declared is null)
        {
            return count == 1;
        }

        return declared is JsonValue value
            && value.GetValueKind() == JsonValueKind.Number
            && double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
            && width == count;
    }

    private static string? Duplicate(JsonObject data, string list, string property, string type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in Values(data[list], property))
        {
            // The service keeps the values it reads as given, and compares them as Python compares them: "1" is not 1.
            if (Given(value) && !seen.Add(value!.ToJsonString()))
            {
                return $"data.{list} has the {property} {Show(value)} more than once; the Wellbore DDMS requires every {property} of a {type} to be unique";
            }
        }

        return null;
    }

    private static string? ReferenceProblem(JsonObject data, string property, string type)
    {
        if (!Given(data[property]))
        {
            return null;
        }

        var reference = data[property]!;
        var curves = Values(data[Curves], CurveId).Where(Given).ToList();
        if (curves.Any(c => c!.ToJsonString() == reference.ToJsonString()))
        {
            return null;
        }

        var described = curves.Count == 0 ? "describes no curve" : "describes only " + string.Join(", ", curves.Select(c => Text(c) ?? c!.ToJsonString()));
        return $"data.{property} is {Show(reference)} but data.Curves {described}; the Wellbore DDMS refuses a {type} whose reference curve is not one of its curves";
    }

    private static string? Required(JsonObject data, string property)
        => Given(data[property])
            ? null
            : $"data.{property} is not given; the Wellbore DDMS requires it of every PPFGDataset";

    private static string? MissingCurveId(JsonObject data, string type)
    {
        var missing = (data[Curves] as JsonArray ?? [])
            .Select((curve, index) => (curve, index))
            .Where(c => !Given((c.curve as JsonObject)?[CurveId]))
            .Select(c => c.index.ToString(CultureInfo.InvariantCulture))
            .ToList();
        return missing.Count == 0
            ? null
            : $"data.Curves[{string.Join(", ", missing)}] has no CurveID; the Wellbore DDMS matches every bulk column of a {type} to a curve by its CurveID and refuses the bulk data of a record with a curve that has none";
    }

    /// <summary>The values of <paramref name="property"/> in the objects of a list; nothing when the list is not one.</summary>
    private static IEnumerable<JsonNode?> Values(JsonNode? list, string property)
        => (list as JsonArray ?? []).OfType<JsonObject>().Select(item => item[property]);

    /// <summary>Whether a value is given, as Python's truth test reads it.</summary>
    private static bool Given(JsonNode? node) => node switch
    {
        null => false,
        JsonArray array => array.Count > 0,
        JsonObject obj => obj.Count > 0,
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.String => value.TryGetValue<string>(out var text) && text.Length > 0,
            JsonValueKind.Number => !double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || number != 0,
            JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => false,
            _ => true,
        },
        _ => true,
    };

    private static string? Text(JsonNode? node)
        => node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;

    private static string Show(JsonNode? node) => Text(node) is { } text ? $"'{text}'" : node?.ToJsonString() ?? "null";

    private static string List(IEnumerable<string> names)
    {
        var all = names.ToList();
        return all.Count == 0 ? "none" : string.Join(", ", all.Take(20)) + (all.Count > 20 ? string.Create(CultureInfo.InvariantCulture, $" and {all.Count - 20} more") : string.Empty);
    }

    private static bool Is(string entityType, string known) => string.Equals(entityType, known, StringComparison.OrdinalIgnoreCase);

    // app/bulk_persistence/consistency_checks.py: ^(?P<name>.+)\[(?P<start>[^:]+):?(?P<stop>.*)\]$
    [GeneratedRegex(@"^(?<name>.+)\[(?<start>[^:]+):?(?<stop>.*)\]$", RegexOptions.CultureInvariant)]
    private static partial Regex ArrayColumn();
}
