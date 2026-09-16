using SqlFlow.Core;
using SqlFlow.Core.Lineage;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Lineage.Collection;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// The OSDU nodes the module's flows add to SQLFlow's lineage (docs/lineage-design.md). An OSDU type is a node per exact
/// kind in a data partition of a platform: system <see cref="TypeSystem"/>, the flow's endpoint as the instance, the
/// partition as the namespace, the entity type's group (<c>master-data</c>, <c>reference-data</c>, ...) as the group, and
/// the kind as the name, whose segments are separated by <see cref="KindSeparator"/> so a wildcard kind binds segment by
/// segment. A partition cache type is a node per cache type name in a partition: system <see cref="CacheSystem"/>, no
/// instance, because a partition has one cache whichever platform filled it. Every declaration is checked here against the
/// widths SQLFlow keeps, so a flow with an unusual value loses that one node with a warning instead of all its lineage.
/// </summary>
public static class OsduLineage
{
    /// <summary>The system OSDU types belong to; the graph captions their nodes "osdu type".</summary>
    public const string TypeSystem = "osdu-type";

    /// <summary>The system partition cache types belong to; the graph captions their nodes "osdu cache".</summary>
    public const string CacheSystem = "osdu-cache";

    /// <summary>The group every cache type of a partition is listed under.</summary>
    public const string CacheGroup = "cache";

    /// <summary>What separates the segments of a kind: a wildcard never matches across it.</summary>
    public const char KindSeparator = ':';

    /// <summary>
    /// The partition a flow's headers name, as the cache scopes it, or null with a warning when the headers name none or
    /// one that cannot identify a node. A reference stays its text: lineage never resolves one.
    /// </summary>
    public static string? Partition(IReadOnlyDictionary<string, string> headers, string flow, string headersPath, ICollection<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentException.ThrowIfNullOrWhiteSpace(flow);
        ArgumentNullException.ThrowIfNull(warnings);
        try
        {
            return CacheScope.Of(headers, headersPath);
        }
        catch (FlowValidationException ex)
        {
            warnings.Add($"{flow} shows no OSDU node in lineage: {SecretHygiene.RedactedMessage(ex.Message)}");
            return null;
        }
    }

    /// <summary>
    /// An OSDU type a flow reads or writes, or null with a warning when the kind or the platform cannot identify a node. A
    /// write names an exact kind; a read may carry wildcards.
    /// </summary>
    public static DeclaredDataset? Type(
        LineageRelation relation, string endpoint, string partition, string kind, string flow, string what, ICollection<string> warnings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flow);
        ArgumentNullException.ThrowIfNull(warnings);
        var trimmed = kind?.Trim() ?? string.Empty;
        if (!OsduKind.IsValid(trimmed) || (relation == LineageRelation.Writes && !OsduKind.IsExact(trimmed)))
        {
            warnings.Add($"{flow} shows no OSDU type for {what}: '{Shown(trimmed)}' is not {(relation == LineageRelation.Writes ? "an exact kind" : "a kind")}.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(endpoint) || endpoint.Trim().Length > DeclaredDataset.MaxInstanceLength)
        {
            warnings.Add($"{flow} shows no OSDU type for {what}: its endpoint is blank or longer than {DeclaredDataset.MaxInstanceLength} characters.");
            return null;
        }

        var dataset = new DeclaredDataset
        {
            Relation = relation,
            System = TypeSystem,
            Instance = endpoint.Trim(),
            Namespace = partition,
            Group = OsduKind.Group(trimmed),
            Name = trimmed,
            Separator = KindSeparator,
        };
        return Fits(dataset, flow, what, warnings) ? dataset : null;
    }

    /// <summary>A partition cache type a flow reads or writes, or null with a warning when its name cannot identify a node.</summary>
    public static DeclaredDataset? CacheType(LineageRelation relation, string partition, string name, string flow, ICollection<string> warnings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flow);
        ArgumentNullException.ThrowIfNull(warnings);
        var trimmed = name?.Trim() ?? string.Empty;
        var what = $"cache type '{Shown(trimmed)}'";
        if (trimmed.Length == 0 || trimmed.Contains('*', StringComparison.Ordinal) || trimmed.Contains('|', StringComparison.Ordinal)
            || trimmed.Any(char.IsControl))
        {
            warnings.Add($"{flow} shows no node for {what}: a cache type is named without wildcards, '|' or control characters.");
            return null;
        }

        var dataset = new DeclaredDataset
        {
            Relation = relation,
            System = CacheSystem,
            Namespace = partition,
            Group = CacheGroup,
            Name = trimmed,
        };
        return Fits(dataset, flow, what, warnings) ? dataset : null;
    }

    /// <summary>Whether the dataset's parts and node identity fit what SQLFlow keeps; warns when they do not.</summary>
    private static bool Fits(DeclaredDataset dataset, string flow, string what, ICollection<string> warnings)
    {
        var parts = new[] { dataset.Namespace, dataset.Group, dataset.Name };
        var key = NodeKey.For(ServerIdentity.Dataset(dataset.System, dataset.Instance), dataset.Namespace, dataset.Group, dataset.Name);
        if (parts.All(p => p.Length <= DeclaredDataset.MaxPartLength) && key.Length <= DeclaredDataset.MaxIdentityLength)
        {
            return true;
        }

        warnings.Add(
            $"{flow} shows no node for {what}: its partition, group or name is longer than {DeclaredDataset.MaxPartLength} characters, or its identity longer than {DeclaredDataset.MaxIdentityLength}.");
        return false;
    }

    /// <summary>A value as a warning quotes it: at most 80 characters.</summary>
    private static string Shown(string value) => value.Length > 80 ? value[..80] + "..." : value;
}
