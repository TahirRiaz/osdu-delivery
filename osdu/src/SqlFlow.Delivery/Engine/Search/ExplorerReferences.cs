using System.Globalization;
using System.Text.RegularExpressions;

namespace SqlFlow.Delivery.Engine.Search;

/// <summary>A place a kind's records name the type asked about.</summary>
/// <param name="At">The property path, as a check names it (<c>data.VerticalMeasurements[].VerticalMeasurementUnitOfMeasureID</c>).</param>
/// <param name="ByPattern">True when the schema marks no <c>x-osdu-relationship</c> there, and the type is read from the pattern an id there must match.</param>
public sealed record ExplorerReferencePlace(string At, bool ByPattern);

/// <summary>One kind whose schema names the type asked about, with where its records name it.</summary>
/// <param name="Kind">The kind, <c>authority:source:entityType:version</c>.</param>
/// <param name="Version">Its version.</param>
/// <param name="Status">Its schema's status: <c>PUBLISHED</c>, <c>DEVELOPMENT</c> or <c>OBSOLETE</c>.</param>
/// <param name="Places">The places its records name the type; at most <see cref="ExplorerReferences.MaxPlaces"/>.</param>
/// <param name="MorePlaces">How many places past those listed.</param>
/// <param name="Partial">Whether its schema holds a reference that was not followed, behind which another place may be.</param>
public sealed record ExplorerReferringKind(string Kind, string Version, string Status, IReadOnlyList<ExplorerReferencePlace> Places, int MorePlaces, bool Partial);

/// <summary>A kind of a referring type whose schema names the type asked about nowhere.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Version">Its version.</param>
/// <param name="Status">Its schema's status.</param>
/// <param name="Partial">Whether its schema holds a reference that was not followed, so the type may be named behind it.</param>
public sealed record ExplorerKindVersion(string Kind, string Version, string Status, bool Partial);

/// <summary>A type with a kind whose records name the type asked about.</summary>
/// <param name="EntityType">The type, <c>group--Type</c>.</param>
/// <param name="Kinds">Each kind of it that does, by authority and source, the oldest version first.</param>
/// <param name="Without">Each kind of it read that does not, the same order.</param>
public sealed record ExplorerReferringType(string EntityType, IReadOnlyList<ExplorerReferringKind> Kinds, IReadOnlyList<ExplorerKindVersion> Without);

/// <summary>Where the pass reading the partition's schemas stands.</summary>
/// <param name="StartedUtc">When it started.</param>
/// <param name="Listing">True while it lists the schemas, before it reads any.</param>
/// <param name="Listed">How many schemas it listed.</param>
/// <param name="ToRead">How many kinds it reads.</param>
/// <param name="Read">How many it has read.</param>
/// <param name="Failed">How many it could not read.</param>
public sealed record ExplorerReferencesProgress(DateTimeOffset StartedUtc, bool Listing, int Listed, int ToRead, int Read, int Failed);

/// <summary>
/// The types whose records name records of a type (osdu/docs/explorer.md, Referenced by): every kind the partition's Schema
/// service holds whose schema declares a place naming the type (<c>x-osdu-relationship</c>), with the versions that do and
/// those that do not, and where. A reference names a type, never a version of it, so it holds for every version of the type
/// asked about.
/// </summary>
public sealed partial record ExplorerReferences
{
    /// <summary>The most places of one kind listed.</summary>
    public const int MaxPlaces = 50;

    /// <summary>The longest type or kind asked about.</summary>
    public const int MaxTypeLength = 256;

    /// <summary>The partition's schemas are being read; what was read before, if anything, answers meanwhile.</summary>
    public const string Reading = "reading";

    /// <summary>The answer is the last complete reading of the partition's schemas.</summary>
    public const string Ready = "ready";

    /// <summary>The last pass failed (<see cref="Problem"/>); what was read before, if anything, answers.</summary>
    public const string Failed = "failed";

    /// <summary>The type asked about, <c>group--Type</c>.</summary>
    public required string EntityType { get; init; }

    /// <summary><see cref="Reading"/>, <see cref="Ready"/> or <see cref="Failed"/>.</summary>
    public required string State { get; init; }

    /// <summary>When the reading answered from was made; null before the first one is.</summary>
    public DateTimeOffset? ReadUtc { get; init; }

    /// <summary>How many schemas the service listed in that reading.</summary>
    public int Listed { get; init; }

    /// <summary>How many kinds that reading holds.</summary>
    public int Kinds { get; init; }

    /// <summary>Where the pass under way stands; null when none is.</summary>
    public ExplorerReferencesProgress? Progress { get; init; }

    /// <summary>Why the last pass failed; null unless <see cref="State"/> is <see cref="Failed"/>.</summary>
    public string? Problem { get; init; }

    /// <summary>The types with a kind whose records name the type, by group (master data first) and name.</summary>
    public IReadOnlyList<ExplorerReferringType> Types { get; init; } = [];

    /// <summary>The types with a kind whose records name any record of the type's group (<c>reference-data</c>), which may be one of the type, and not the type itself.</summary>
    public IReadOnlyList<ExplorerReferringType> AnyOfGroup { get; init; } = [];

    /// <summary>The kinds whose schema could not be read, each with why; at most <see cref="PartitionSchemaIndex.MaxUnreadKept"/>.</summary>
    public IReadOnlyList<SchemaKindUnread> Unread { get; init; } = [];

    /// <summary>How many kinds could not be read in all.</summary>
    public int UnreadCount { get; init; }

    /// <summary>What leaves the answer short: listings refused, references not followed.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>
    /// The type <paramref name="asked"/> names: a type (<c>reference-data--UnitOfMeasure</c>), or a kind naming one
    /// (<c>osdu:wks:reference-data--UnitOfMeasure:1.0.0</c>, whose version does not matter); null when it names none.
    /// </summary>
    public static string? EntityTypeOf(string? asked)
    {
        var text = asked?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > MaxTypeLength)
        {
            return null;
        }

        if (text.Contains(':', StringComparison.Ordinal))
        {
            var parts = text.Split(':');
            text = parts.Length == 4 ? parts[2] : string.Empty;
        }

        return EntityTypePattern().IsMatch(text) ? text : null;
    }

    /// <summary>Why <paramref name="asked"/> names no type.</summary>
    public static string TypeProblem(string? asked)
    {
        var shown = asked is null ? string.Empty : asked.Length <= 80 ? asked : asked[..80] + "...";
        return $"'{shown}' is not a type: group--Type (reference-data--UnitOfMeasure), or a kind naming one (osdu:wks:reference-data--UnitOfMeasure:1.0.0).";
    }

    /// <summary>The answer for <paramref name="entityType"/> from what the index holds now.</summary>
    public static ExplorerReferences Of(string entityType, SchemaIndexView view)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentNullException.ThrowIfNull(view);
        var last = view.Last;
        var failed = view.Progress is null && view.Problem is not null && (last is null || view.ProblemUtc > last.ReadUtc);
        var state = view.Progress is not null || (last is null && !failed) ? Reading : failed ? Failed : Ready;
        var progress = view.Progress is { } p ? new ExplorerReferencesProgress(p.StartedUtc, p.Listing, p.Listed, p.ToRead, p.Read, p.Failed) : null;
        if (last is null)
        {
            return new ExplorerReferences { EntityType = entityType, State = state, Progress = progress, Problem = failed ? view.Problem : null };
        }

        var group = entityType[..entityType.IndexOf("--", StringComparison.Ordinal)];
        var named = new Dictionary<string, List<SchemaKindFacts>>(StringComparer.Ordinal);
        var anyOfGroup = new Dictionary<string, List<SchemaKindFacts>>(StringComparer.Ordinal);
        var byType = new Dictionary<string, List<SchemaKindFacts>>(StringComparer.Ordinal);
        foreach (var facts in last.Kinds.Values)
        {
            var type = facts.Listing.EntityType;
            Add(byType, type, facts);
            if (facts.Relationships.Any(r => r.Names.Contains(entityType, StringComparer.Ordinal)))
            {
                Add(named, type, facts);
            }

            if (facts.Relationships.Any(r => r.Names.Contains(group, StringComparer.Ordinal) && !r.Names.Contains(entityType, StringComparer.Ordinal)))
            {
                Add(anyOfGroup, type, facts);
            }
        }

        var notes = new List<string>(last.Notes);
        var partial = last.Kinds.Values.Count(k => k.Partial);
        if (partial > 0)
        {
            notes.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{partial} kind{(partial == 1 ? " holds a reference" : "s hold references")} to another schema that could not be followed (a schema the service does not hold, or a form the reader does not follow); a place behind one is not seen. Each kind listed here that holds one is marked."));
        }

        return new ExplorerReferences
        {
            EntityType = entityType,
            State = state,
            ReadUtc = last.ReadUtc,
            Listed = last.Listed,
            Kinds = last.Kinds.Count,
            Progress = progress,
            Problem = failed ? view.Problem : null,
            Types = Referring(named, byType, r => r.Names.Contains(entityType, StringComparer.Ordinal)),
            AnyOfGroup = Referring(anyOfGroup, byType, r => r.Names.Contains(group, StringComparer.Ordinal) && !r.Names.Contains(entityType, StringComparer.Ordinal)),
            Unread = last.Unread,
            UnreadCount = last.UnreadCount,
            Notes = notes,
        };
    }

    private static void Add(Dictionary<string, List<SchemaKindFacts>> into, string type, SchemaKindFacts facts)
    {
        if (!into.TryGetValue(type, out var held))
        {
            held = [];
            into[type] = held;
        }

        held.Add(facts);
    }

    /// <summary>Each type with a kind holding a place <paramref name="names"/> picks, its kinds that do and those that do not, in the order a reader looks for them.</summary>
    private static List<ExplorerReferringType> Referring(
        Dictionary<string, List<SchemaKindFacts>> referring, Dictionary<string, List<SchemaKindFacts>> byType, Func<SchemaRelationship, bool> names)
        => referring.Keys
            .OrderBy(GroupRank)
            .ThenBy(t => t, StringComparer.OrdinalIgnoreCase)
            .Select(type =>
            {
                var kinds = referring[type].Select(k => k.Listing.Id).ToHashSet(StringComparer.Ordinal);
                return new ExplorerReferringType(
                    type,
                    Ordered(referring[type]).Select(k =>
                    {
                        var places = k.Relationships.Where(names).Select(r => new ExplorerReferencePlace(r.At, r.ByPattern)).ToList();
                        return new ExplorerReferringKind(
                            k.Listing.Id, k.Listing.Version, k.Listing.Status, places.Take(MaxPlaces).ToList(), Math.Max(0, places.Count - MaxPlaces), k.Partial);
                    }).ToList(),
                    Ordered(byType[type].Where(k => !kinds.Contains(k.Listing.Id)))
                        .Select(k => new ExplorerKindVersion(k.Listing.Id, k.Listing.Version, k.Listing.Status, k.Partial))
                        .ToList());
            })
            .ToList();

    /// <summary>Kinds by authority and source, then the oldest version first.</summary>
    private static IEnumerable<SchemaKindFacts> Ordered(IEnumerable<SchemaKindFacts> kinds)
        => kinds
            .OrderBy(k => k.Listing.Authority, StringComparer.OrdinalIgnoreCase)
            .ThenBy(k => k.Listing.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(k => VersionKey(k.Listing.Version))
            .ThenBy(k => k.Listing.Id, StringComparer.Ordinal);

    /// <summary>A version as a key that sorts as numbers do: 1.10.0 after 1.9.0.</summary>
    private static string VersionKey(string version)
        => string.Join('.', version.Split('.').Select(part => long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n.ToString("D12", CultureInfo.InvariantCulture) : part));

    /// <summary>Where a type's group stands in the answer: the records a record belongs to first, the codes that describe it after.</summary>
    private static int GroupRank(string entityType)
    {
        var at = entityType.IndexOf("--", StringComparison.Ordinal);
        return (at < 0 ? entityType : entityType[..at]) switch
        {
            "master-data" => 0,
            "work-product-component" => 1,
            "work-product" => 2,
            "dataset" => 3,
            "reference-data" => 4,
            _ => 5,
        };
    }

    /// <summary>A type: a group and a type within it (openapi schema_service, <c>SchemaIdentity.entityType</c>).</summary>
    [GeneratedRegex(@"^[\w\-\.]+--[\w\-\.]+$", RegexOptions.CultureInvariant)]
    private static partial Regex EntityTypePattern();
}
