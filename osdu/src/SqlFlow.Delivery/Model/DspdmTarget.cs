using System.Globalization;
using System.Text.RegularExpressions;

namespace SqlFlow.Delivery.Model;

/// <summary>
/// The Production DDMS core service (DSPDM) behind the target (<c>target.dspdm</c>): where it is under the endpoint, the
/// time zone every request names, and the business objects the flow's kinds are rows of
/// (osdu/specs/production-dspdm/INTEGRATION.md). DSPDM keeps business object rows in its own database, not OSDU records,
/// so the dspdm route finds each row again by a unique key of its business object and keeps the row's primary key.
/// </summary>
public sealed record DspdmTarget
{
    /// <summary>The time zone a flow's requests name when it names none: the one the engine writes date and time values in.</summary>
    public const string DefaultTimeZone = "GMT+00:00";

    /// <summary>Where DSPDM sits under the endpoint (<c>/api/dspdm/v1</c> behind the GC gateway); null when the endpoint is DSPDM.</summary>
    public string? Root { get; init; }

    /// <summary>
    /// The time zone every request names (<c>GMT+hh:mm</c> or <c>GMT-hh:mm</c>, from GMT-12:00 to GMT+14:00, the only form
    /// DSPDM's save takes): the zone a date value without an offset is read in, and the zone dates come back in.
    /// </summary>
    public string Timezone { get; init; } = DefaultTimeZone;

    /// <summary>
    /// The business objects the flow's kinds are rows of, by the entity type a kind names. A kind the flow does not list is
    /// a row of the business object named like its entity type (upper case, a space for each underscore), found again by
    /// that business object's one unique constraint.
    /// </summary>
    public IReadOnlyDictionary<string, DspdmBusinessObject> BusinessObjects { get; init; } = new Dictionary<string, DspdmBusinessObject>(StringComparer.Ordinal);

    /// <summary>
    /// What the route does with a row it finds by a record's unique key when the record did not write that row: holds the
    /// record (the default), or updates the row and keeps it as the record's.
    /// </summary>
    public DspdmExistingRows ExistingRows { get; init; } = DspdmExistingRows.Hold;

    /// <summary>What the flow says about the business object a kind of <paramref name="entity"/> is a row of.</summary>
    public DspdmBusinessObject For(string entity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        return BusinessObjects.TryGetValue(entity, out var declared) ? declared : new DspdmBusinessObject();
    }
}

/// <summary>
/// What the dspdm route does with a row that holds a record's unique key and that the record did not write (another
/// system's row, a row written before the flow existed, or the row of another record whose key renders the same).
/// </summary>
public enum DspdmExistingRows
{
    /// <summary>The record is held, naming the row, so nothing another system wrote is changed unasked.</summary>
    Hold,

    /// <summary>The row is updated with the record's values and kept as the record's row, to take over rows loaded before.</summary>
    Update,
}

/// <summary>One business object a flow's kind is a row of (<c>target.dspdm.businessObjects.&lt;entity&gt;</c>).</summary>
public sealed record DspdmBusinessObject
{
    /// <summary>The business object's name in DSPDM (<c>WELL TEST</c>); null takes the entity type's (<see cref="DspdmKinds.DefaultName"/>).</summary>
    public string? Name { get; init; }

    /// <summary>
    /// The attributes a row is found again by, one of the business object's unique constraints; empty takes its one
    /// unique constraint.
    /// </summary>
    public IReadOnlyList<string> Key { get; init; } = [];
}

/// <summary>
/// The kinds of DSPDM business object templates: a template whose kind has the source <c>dspdm</c>
/// (<c>{authority}:dspdm:{entity}:{version}</c>, <c>acme:dspdm:well_test:1.0.0</c>) describes the rows of a business object,
/// its entity type being the business object's entity (its table), and a mapping of it renders rows for the dspdm route.
/// </summary>
public static partial class DspdmKinds
{
    /// <summary>The source segment that marks a DSPDM business object template.</summary>
    public const string Source = "dspdm";

    /// <summary>
    /// The top-level property a rendered row lists the attributes its mapping fills in, upper case and sorted, so that an update
    /// clears an attribute whose value the source no longer gives, and leaves the attributes the mapping does not fill alone.
    /// </summary>
    public const string OwnedProperty = "attributes";

    /// <summary>Whether <paramref name="kind"/> names a DSPDM business object template.</summary>
    public static bool Is(string? kind)
        => kind is not null && kind.Split(':') is { Length: 4 } parts && string.Equals(parts[1], Source, StringComparison.Ordinal);

    /// <summary>The entity type a DSPDM kind names, or null when it is not one.</summary>
    public static string? EntityOf(string kind) => Is(kind) ? kind.Split(':')[2] : null;

    /// <summary>The business object an entity type names by default: upper case, a space for each underscore (<c>well_test</c> is <c>WELL TEST</c>).</summary>
    public static string DefaultName(string entity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        return entity.Replace('_', ' ').ToUpperInvariant();
    }

    /// <summary>
    /// Whether <paramref name="timezone"/> is a time zone DSPDM's save takes: <c>GMT</c>, a sign and <c>hh:mm</c>, from
    /// GMT-12:00 to GMT+14:00 (osdu/specs/production-dspdm/INTEGRATION.md section 3.1, <c>BaseController.isValidGmtOffset</c>).
    /// </summary>
    public static bool IsTimezone(string? timezone)
    {
        if (timezone is null || GmtOffset().Match(timezone) is not { Success: true } match)
        {
            return false;
        }

        var hours = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
        var minutes = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        if (hours > 14 || minutes >= 60)
        {
            return false;
        }

        return match.Groups["sign"].Value == "+"
            ? hours < 14 || minutes == 0
            : hours < 12 || (hours == 12 && minutes == 0);
    }

    /// <summary>The offset a valid <paramref name="timezone"/> names.</summary>
    public static TimeSpan OffsetOf(string timezone)
    {
        if (!IsTimezone(timezone))
        {
            throw new ArgumentException($"'{timezone}' is not a time zone of the form GMT+hh:mm.", nameof(timezone));
        }

        var match = GmtOffset().Match(timezone);
        var offset = new TimeSpan(
            int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture),
            0);
        return match.Groups["sign"].Value == "-" ? offset.Negate() : offset;
    }

    /// <summary>Whether <paramref name="name"/> can name a business object or an attribute: DSPDM refuses names that start with a digit.</summary>
    public static bool IsName(string? name)
        => !string.IsNullOrWhiteSpace(name) && name.Trim() == name && !char.IsAsciiDigit(name[0]) && name.Length <= 50 && !name.Any(char.IsControl);

    [GeneratedRegex(@"^GMT(?<sign>[+-])(?<h>\d{2}):(?<m>\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex GmtOffset();
}
