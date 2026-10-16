using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Delivery.Json;

namespace SqlFlow.Delivery.Engine.Protocols.Dspdm;

/// <summary>How DSPDM stores an attribute, from the data type its metadata names (<c>DSPDMConstants.DataTypes</c>).</summary>
internal enum DspdmValueKind
{
    Text,
    Number,
    Boolean,
    Date,
    Timestamp,
    Time,
    Json,
    Binary,
    Other,
}

/// <summary>
/// An attribute's data type as DSPDM reads it (<c>DSPDMConstants.DataTypes</c>, <c>MetadataUtils.getDataTypeLengthFromString</c>
/// and <c>getDataTypeDecimalLengthFromString</c>): the kind of its values, whether they are whole numbers and in which range,
/// how many characters a text takes, and the precision and scale of a decimal.
/// </summary>
internal sealed record DspdmType(DspdmValueKind Kind, int? Length = null, int? Precision = null, int? Scale = null, decimal? Min = null, decimal? Max = null)
{
    /// <summary>Whether the attribute takes whole numbers only.</summary>
    public bool Whole => Min is not null;
}

/// <summary>
/// The values of DSPDM business object rows: what an attribute's data type takes, and the one form a value is compared in
/// when a row is found again by its key, which is the form DSPDM stores it in (osdu/specs/production-dspdm/INTEGRATION.md
/// section 3.1). DSPDM trims the text it saves (<c>DTOHelper.buildDynamicDTOFromMap</c>), rounds a decimal to its column's
/// scale (<c>DTOHelper.validateAndConvertDataType</c>), and reads a date or a time as <c>DateTimeUtils.parse</c> does: a
/// value in the ISO form with an offset is moved into the request's time zone, and any other form keeps the date and time it
/// was written with. With DSPDM's shipped settings (<c>use_utc_timezone_to_save</c> and <c>use_client_timezone_to_display</c>
/// false) that wall-clock time is what the database keeps, and what a read writes back, labelled with the request's zone.
/// </summary>
internal static partial class DspdmValues
{
    /// <summary>The attribute DSPDM stamps on every update of a row.</summary>
    public const string ChangedDate = "ROW_CHANGED_DATE";

    /// <summary>The attribute DSPDM stamps when it inserts a row.</summary>
    public const string CreatedDate = "ROW_CREATED_DATE";

    /// <summary>The four attributes DSPDM fills itself (<c>BusinessObjectAttributeDTO.isCreateAuditField</c> and <c>isUpdateAuditField</c>).</summary>
    public static readonly IReadOnlySet<string> Audit = new HashSet<string>(StringComparer.Ordinal)
    {
        "ROW_CREATED_BY", CreatedDate, "ROW_CHANGED_BY", ChangedDate,
    };

    /// <summary>The data type names DSPDM knows, PostgreSQL's and SQL Server's, longest first where one starts another.</summary>
    private static readonly (string Name, DspdmValueKind Kind)[] Names =
    [
        ("timestamp", DspdmValueKind.Timestamp),
        ("datetimeoffset", DspdmValueKind.Timestamp),
        ("datetime2", DspdmValueKind.Timestamp),
        ("datetime", DspdmValueKind.Timestamp),
        ("smalldatetime", DspdmValueKind.Timestamp),
        ("date", DspdmValueKind.Date),
        ("time", DspdmValueKind.Time),
        ("boolean", DspdmValueKind.Boolean),
        ("bit", DspdmValueKind.Boolean),
        ("smallint", DspdmValueKind.Number),
        ("tinyint", DspdmValueKind.Number),
        ("integer", DspdmValueKind.Number),
        ("int", DspdmValueKind.Number),
        ("bigint", DspdmValueKind.Number),
        ("decimal", DspdmValueKind.Number),
        ("numeric", DspdmValueKind.Number),
        ("real", DspdmValueKind.Number),
        ("float", DspdmValueKind.Number),
        ("double precision", DspdmValueKind.Number),
        ("character varying", DspdmValueKind.Text),
        ("character", DspdmValueKind.Text),
        ("nvarchar", DspdmValueKind.Text),
        ("varchar", DspdmValueKind.Text),
        ("nchar", DspdmValueKind.Text),
        ("char", DspdmValueKind.Text),
        ("ntext", DspdmValueKind.Text),
        ("text", DspdmValueKind.Text),
        ("jsonb", DspdmValueKind.Json),
        ("json", DspdmValueKind.Json),
        ("bytea", DspdmValueKind.Binary),
        ("varbinary", DspdmValueKind.Binary),
        ("binary", DspdmValueKind.Binary),
        ("image", DspdmValueKind.Binary),
    ];

    /// <summary>The kind of values a data type holds, and the limits DSPDM checks them against.</summary>
    public static DspdmType TypeOf(string? dataType)
    {
        var type = (dataType ?? string.Empty).Trim().ToLowerInvariant();
        var open = type.IndexOf('(', StringComparison.Ordinal);
        var name = (open >= 0 ? type[..open] : type).Trim();
        var arguments = open >= 0 && type.IndexOf(')', open) is var close and > 0
            ? type[(open + 1)..close].Split(',', StringSplitOptions.TrimEntries)
            : [];

        // An exact name wins; otherwise the longest name the type starts with (time with time zone, timestamp without time zone).
        var kind = Names.FirstOrDefault(n => n.Name == name).Name is not null
            ? Names.First(n => n.Name == name).Kind
            : Names.Where(n => name.StartsWith(n.Name + " ", StringComparison.Ordinal)).OrderByDescending(n => n.Name.Length).Select(n => (DspdmValueKind?)n.Kind).FirstOrDefault()
                ?? DspdmValueKind.Other;
        return kind switch
        {
            DspdmValueKind.Text => new DspdmType(kind, Length: arguments is [var length] && int.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null),
            DspdmValueKind.Number => name switch
            {
                "smallint" => new DspdmType(kind, Min: short.MinValue, Max: short.MaxValue),
                "tinyint" => new DspdmType(kind, Min: byte.MinValue, Max: byte.MaxValue),
                "integer" or "int" => new DspdmType(kind, Min: int.MinValue, Max: int.MaxValue),
                "bigint" => new DspdmType(kind, Min: long.MinValue, Max: long.MaxValue),
                "decimal" or "numeric" => new DspdmType(
                    kind,
                    Precision: arguments.Length > 0 && int.TryParse(arguments[0], NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p > 0 ? p : null,
                    Scale: arguments.Length > 1 && int.TryParse(arguments[1], NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : arguments.Length == 1 ? 0 : null),
                _ => new DspdmType(kind),
            },
            _ => new DspdmType(kind),
        };
    }

    /// <summary>
    /// Why a rendered value is not one an attribute of <paramref name="type"/> takes, or null when it is: a JSON document for a
    /// JSON column, and otherwise a single value DSPDM converts to the column's type and keeps within its limits. A null
    /// value is always taken: it leaves the attribute empty.
    /// </summary>
    public static string? Refusal(DspdmType type, JsonNode? value, TimeSpan zone)
    {
        ArgumentNullException.ThrowIfNull(type);
        var valueKind = value?.GetValueKind() ?? JsonValueKind.Null;
        if (valueKind == JsonValueKind.Null || type.Kind == DspdmValueKind.Json)
        {
            return null;
        }

        if (valueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            return "takes a single value, not a JSON " + (valueKind == JsonValueKind.Object ? "object" : "array");
        }

        switch (type.Kind)
        {
            case DspdmValueKind.Boolean:
                return Token(type, value, zone) is null ? "takes true or false (or Y, N, 1, 0 as text)" : null;
            case DspdmValueKind.Number:
                if (valueKind is JsonValueKind.True or JsonValueKind.False || Number(Text(value!)) is not { } number)
                {
                    return "takes a number";
                }

                if (type.Whole && (decimal.Truncate(number) != number || number < type.Min || number > type.Max))
                {
                    return string.Create(CultureInfo.InvariantCulture, $"takes whole numbers from {type.Min} to {type.Max}");
                }

                if (type.Precision is { } precision && IntegerDigits(number) > precision - (type.Scale ?? 0))
                {
                    return string.Create(CultureInfo.InvariantCulture, $"takes at most {precision - (type.Scale ?? 0)} digits before the decimal point");
                }

                return null;
            case DspdmValueKind.Text:
                return type.Length is { } length && Text(value!).Trim().Length > length
                    ? string.Create(CultureInfo.InvariantCulture, $"takes at most {length} characters")
                    : null;
            case DspdmValueKind.Date or DspdmValueKind.Timestamp or DspdmValueKind.Time:
                if (valueKind != JsonValueKind.String)
                {
                    return "takes a date or time written as text";
                }

                return Token(type, value, zone) is null
                    ? $"takes a {(type.Kind == DspdmValueKind.Time ? "time" : "date")} in one of the forms DSPDM reads, such as {(type.Kind == DspdmValueKind.Time ? "10:30:00" : "2024-05-01T10:30:00Z")}"
                    : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// The form a value of <paramref name="type"/> is compared in, or null when it is absent or cannot be read as that type:
    /// text trimmed, numbers rounded as DSPDM rounds them and without trailing zeros, dates as days, timestamps and times as
    /// the wall-clock time DSPDM keeps, to the millisecond.
    /// </summary>
    public static string? Token(DspdmType type, JsonNode? value, TimeSpan zone)
    {
        ArgumentNullException.ThrowIfNull(type);
        var valueKind = value?.GetValueKind() ?? JsonValueKind.Null;
        if (valueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (type.Kind == DspdmValueKind.Json)
        {
            return valueKind == JsonValueKind.String ? value!.GetValue<string>() : CanonicalJson.ToString(value);
        }

        if (valueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            return null;
        }

        var text = Text(value!);
        switch (type.Kind)
        {
            case DspdmValueKind.Number:
                if (valueKind is JsonValueKind.True or JsonValueKind.False || Number(text) is not { } number)
                {
                    return null;
                }

                if (type.Whole)
                {
                    return decimal.Truncate(number) == number ? Normal(number) : null;
                }

                // DSPDM rounds a decimal with more places than its column keeps, half away from zero (RoundingMode.HALF_UP).
                return Normal(type.Scale is { } scale && scale <= 28 ? decimal.Round(number, scale, MidpointRounding.AwayFromZero) : number);
            case DspdmValueKind.Boolean:
                return valueKind switch
                {
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.String => text.Trim().ToUpperInvariant() switch
                    {
                        "TRUE" or "1" or "Y" => "true",
                        "FALSE" or "0" or "N" => "false",
                        _ => null,
                    },
                    _ => null,
                };
            case DspdmValueKind.Date:
                return valueKind == JsonValueKind.String && WallClock(text, zone) is { } day
                    ? day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : null;
            case DspdmValueKind.Timestamp:
                return valueKind == JsonValueKind.String && WallClock(text, zone) is { } moment
                    ? moment.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture)
                    : null;
            case DspdmValueKind.Time:
                return valueKind == JsonValueKind.String && TimeOfDay(text, zone) is { } time
                    ? time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
                    : null;
            default:
                return text.Trim();
        }
    }

    /// <summary>
    /// The version a row read from DSPDM carries: when it last changed (<c>ROW_CHANGED_DATE</c>), or when it was inserted when it
    /// never changed, in milliseconds; null when it carries neither. DSPDM stamps both with the UTC time of the save
    /// (<c>DateTimeUtils.getCurrentTimestampUTC</c>) and, with its shipped settings, writes that time back unchanged under the
    /// request's zone label, so the version is the time as written, read as UTC: the moment of the save, whatever zone the
    /// flow names.
    /// </summary>
    public static long? VersionOf(JsonObject row)
    {
        ArgumentNullException.ThrowIfNull(row);
        foreach (var attribute in (string[])[ChangedDate, CreatedDate])
        {
            if (row[attribute] is JsonValue value && value.GetValueKind() == JsonValueKind.String && Written(value.GetValue<string>()) is { } written)
            {
                return new DateTimeOffset(written, TimeSpan.Zero).ToUnixTimeMilliseconds();
            }
        }

        return null;
    }

    /// <summary>A version as the moment it names, for messages.</summary>
    public static string Moment(long version)
        => DateTimeOffset.FromUnixTimeMilliseconds(version).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>A primary key value DSPDM answered with: a whole number, as JSON or as text.</summary>
    public static long? Key(JsonNode? value)
    {
        return value?.GetValueKind() switch
        {
            JsonValueKind.Number when decimal.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                && decimal.Truncate(number) == number && number is >= long.MinValue and <= long.MaxValue => (long)number,
            JsonValueKind.String when long.TryParse(value.GetValue<string>().Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    /// <summary>
    /// The date and time DSPDM keeps for <paramref name="text"/> (<c>DateTimeUtils.parse</c>): an ISO date and time with an offset
    /// (<c>ISO_ZONED_DATE_TIME</c>) moved into the request's zone, any other form it reads as written, a date alone at
    /// midnight; null when it is no date DSPDM reads.
    /// </summary>
    internal static DateTime? WallClock(string text, TimeSpan zone)
    {
        var trimmed = text.Trim();
        if (IsoZoned().Match(trimmed) is { Success: true } iso)
        {
            // java.time takes offsets up to 18 hours, to the second, which DateTimeOffset cannot hold, so the move is by ticks.
            var zulu = iso.Groups["z"].Value is "Z" or "z";
            var minutes = zulu ? 0 : Int(iso, "om");
            var seconds = zulu || !iso.Groups["os"].Success ? 0 : Int(iso, "os");
            var offset = zulu ? TimeSpan.Zero : new TimeSpan(Int(iso, "oh"), minutes, seconds);
            if (offset > TimeSpan.FromHours(18) || minutes > 59 || seconds > 59 || Build(iso, hour12: false) is not { } local)
            {
                return null;
            }

            var ticks = local.Ticks - (iso.Groups["sign"].Value == "-" ? -offset.Ticks : offset.Ticks) + zone.Ticks;
            return ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks ? null : new DateTime(ticks, DateTimeKind.Unspecified);
        }

        return Local().Match(trimmed) is { Success: true } written ? Build(written, hour12: written.Groups["ampm"].Success) : null;
    }

    /// <summary>
    /// The time of day DSPDM keeps for <paramref name="text"/>: a time alone as written, whatever offset follows it, or the time
    /// of the date and time <see cref="WallClock"/> reads.
    /// </summary>
    internal static TimeOnly? TimeOfDay(string text, TimeSpan zone)
    {
        var trimmed = text.Trim();
        if (TimeAlone().Match(trimmed) is { Success: true } time)
        {
            var hour = Int(time, "h");
            if (time.Groups["ampm"].Success)
            {
                if (hour is < 1 or > 12)
                {
                    return null;
                }

                hour = (hour % 12) + (time.Groups["ampm"].Value.StartsWith('P') || time.Groups["ampm"].Value.StartsWith('p') ? 12 : 0);
            }

            var minute = Int(time, "mi");
            var second = time.Groups["sec"].Success ? Int(time, "sec") : 0;
            return hour < 24 && minute < 60 && second < 60 ? new TimeOnly(hour, minute, second, Millis(time)) : null;
        }

        return WallClock(trimmed, zone) is { } moment ? TimeOnly.FromDateTime(moment) : null;
    }

    /// <summary>The date and time written in a value DSPDM wrote back, without the zone label that follows it.</summary>
    private static DateTime? Written(string text)
    {
        var trimmed = text.Trim();
        var match = IsoZoned().Match(trimmed);
        if (!match.Success)
        {
            match = Local().Match(trimmed);
        }

        return match.Success ? Build(match, hour12: match.Groups["ampm"].Success) : null;
    }

    private static DateTime? Build(Match match, bool hour12)
    {
        var year = Int(match, "y");
        var month = Int(match, "mo");
        var day = Int(match, "d");
        if (year < 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return null;
        }

        if (!match.Groups["h"].Success)
        {
            return new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Unspecified);
        }

        var hour = Int(match, "h");
        if (hour12)
        {
            if (hour is < 1 or > 12)
            {
                return null;
            }

            var pm = match.Groups["ampm"].Value.StartsWith('P') || match.Groups["ampm"].Value.StartsWith('p');
            hour = (hour % 12) + (pm ? 12 : 0);
        }

        var minute = Int(match, "mi");
        var second = match.Groups["sec"].Success ? Int(match, "sec") : 0;
        if (hour > 23 || minute > 59 || second > 59)
        {
            return null;
        }

        return new DateTime(year, month, day, hour, minute, second, Millis(match), DateTimeKind.Unspecified);
    }

    /// <summary>The milliseconds of a fraction of a second, the finer digits dropped as DSPDM's millisecond forms drop them.</summary>
    private static int Millis(Match match)
        => match.Groups["f"].Success ? int.Parse(match.Groups["f"].Value.PadRight(3, '0')[..3], NumberStyles.None, CultureInfo.InvariantCulture) : 0;

    private static int Int(Match match, string group) => int.Parse(match.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture);

    /// <summary>A scalar as the text DSPDM converts it from: a string as it is, a number or a flag as JSON writes it.</summary>
    private static string Text(JsonNode value)
        => value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString();

    private static decimal? Number(string text)
        => decimal.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;

    /// <summary>A number without trailing zeros (<c>BigDecimal.stripTrailingZeros</c>), written the invariant way.</summary>
    private static string Normal(decimal number) => (number / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);

    /// <summary>The digits a number has before its decimal point (DSPDM's <c>NumberUtils.getIntegerLength</c>), none for a number below one.</summary>
    private static int IntegerDigits(decimal number)
    {
        var whole = decimal.Truncate(Math.Abs(number));
        return whole == 0 ? 0 : whole.ToString(CultureInfo.InvariantCulture).Length;
    }

    /// <summary>
    /// <c>DateTimeFormatter.ISO_ZONED_DATE_TIME</c>: a date, <c>T</c>, a time to the minute or finer, and an offset (<c>Z</c> or
    /// <c>+hh:mm</c>), optionally a region in brackets.
    /// </summary>
    [GeneratedRegex(@"^(?<y>\d{4})-(?<mo>\d{2})-(?<d>\d{2})[Tt](?<h>\d{2}):(?<mi>\d{2})(?::(?<sec>\d{2})(?:\.(?<f>\d{1,9}))?)?(?<z>[Zz]|(?<sign>[+-])(?<oh>\d{2}):(?<om>\d{2})(?::(?<os>\d{2}))?)(?:\[[^\]]+\])?$", RegexOptions.CultureInvariant)]
    private static partial Regex IsoZoned();

    /// <summary>
    /// The other date and time forms DSPDM reads (<c>DateTimeUtils.PARSE_PATTERNS</c> and <c>DISPLAY_PATTERNS</c>): a date with
    /// dashes or slashes, optionally a time after <c>T</c> or a space, to the minute or finer, optionally an offset (which DSPDM
    /// does not apply), or a 12-hour time with AM or PM.
    /// </summary>
    [GeneratedRegex(@"^(?<y>\d{4})(?<sep>[-/])(?<mo>\d{2})\k<sep>(?<d>\d{2})(?:[Tt ](?<h>\d{2}):(?<mi>\d{2})(?::(?<sec>\d{2})(?:\.(?<f>\d{1,9}))?)?(?: ?(?:[Zz]|[+-]\d{2}(?::?\d{2})?))?(?: (?<ampm>[AaPp][Mm]))?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex Local();

    /// <summary>A time alone (<c>HH:mm:ss</c>, <c>HH:mm:ss.SSS</c>, with or without an offset, or <c>hh:mm a</c>).</summary>
    [GeneratedRegex(@"^(?<h>\d{2}):(?<mi>\d{2})(?::(?<sec>\d{2})(?:\.(?<f>\d{1,9}))?)?(?: ?(?:[Zz]|[+-]\d{2}(?::?\d{2})?))?(?: (?<ampm>[AaPp][Mm]))?$", RegexOptions.CultureInvariant)]
    private static partial Regex TimeAlone();
}
