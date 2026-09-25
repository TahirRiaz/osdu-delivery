using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Model;

/// <summary>
/// The mapping parameters the OSDU flow kind owns: where a record is delivered and under whose access and legal terms.
/// Every OSDU record carries a partition in its id, an owner and a viewer group in <c>acl</c>, and a legal tag in
/// <c>legal</c>, so these four are properties of the kind rather than of any one mapping, and a flow does not repeat
/// them.
/// </summary>
/// <remarks>
/// <para>
/// A mapping declares the ones it fills and writes them with <c>{$param.name}</c>. A flow may still supply a value under
/// <c>render.parameters</c>, and what it supplies wins. A parameter the flow leaves out defaults to the reference named
/// here, which resolves like every other reference the flow declares: from the central configuration the control plane
/// supplied with the run, and from the node's own environment where the configuration is silent. Both routes end in one
/// resolution, so a document that spells the reference and one that lets the kind supply it deliver the same record.
/// </para>
/// </remarks>
public static class DeliveryDestination
{
    /// <summary>The partition every record id is minted in, and whose cache the render reads.</summary>
    public const string DataPartitionParameter = RenderContext.DataPartitionParameter;

    /// <summary>The entitlements group that owns every record delivered (<c>acl.owners</c>).</summary>
    public const string AclOwnerParameter = "aclOwner";

    /// <summary>The entitlements group that may read every record delivered (<c>acl.viewers</c>).</summary>
    public const string AclViewerParameter = "aclViewer";

    /// <summary>The legal tag every record delivered carries (<c>legal.legaltags</c>).</summary>
    public const string LegalTagParameter = "legalTag";

    /// <summary>The reference each parameter takes when the flow supplies no value, in the order they are listed.</summary>
    private static readonly (string Parameter, string Reference)[] Defaults =
    [
        (DataPartitionParameter, "${env:OSDU_DATA_PARTITION}"),
        (AclOwnerParameter, "${env:OSDU_ACL_OWNER}"),
        (AclViewerParameter, "${env:OSDU_ACL_VIEWER}"),
        (LegalTagParameter, "${env:OSDU_LEGAL_TAG}"),
    ];

    /// <summary>The parameters the kind owns, in the order a listing shows them.</summary>
    public static IReadOnlyList<string> Parameters { get; } = [.. Defaults.Select(d => d.Parameter)];

    /// <summary>The environment references these parameters default to, by parameter name.</summary>
    public static IReadOnlyDictionary<string, string> References { get; } =
        Defaults.ToDictionary(d => d.Parameter, d => d.Reference, StringComparer.Ordinal);

    /// <summary>Whether <paramref name="parameter"/> is one the kind supplies when a flow does not.</summary>
    public static bool Owns(string parameter) => References.ContainsKey(parameter);

    /// <summary>
    /// The reference <paramref name="parameter"/> takes when the flow supplies no value, or null for a parameter the kind
    /// does not own, which only the flow can fill.
    /// </summary>
    public static string? ReferenceFor(string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameter);
        return References.GetValueOrDefault(parameter);
    }

    /// <summary>
    /// What a flow renders <paramref name="parameters"/> with, as written and before any reference in it is resolved:
    /// every value the flow supplies under <c>render.parameters</c>, and for each of <paramref name="parameters"/> the kind
    /// owns that the flow leaves out, the reference the kind names for it. A value the flow supplies always wins.
    /// </summary>
    /// <param name="supplied">The flow's <c>render.parameters</c>.</param>
    /// <param name="parameters">The parameters asked for: a mapping's declared ones, or every one the kind owns.</param>
    public static IReadOnlyDictionary<string, string> Supplied(IReadOnlyDictionary<string, string> supplied, IEnumerable<string> parameters)
    {
        ArgumentNullException.ThrowIfNull(supplied);
        ArgumentNullException.ThrowIfNull(parameters);
        var result = new Dictionary<string, string>(supplied, StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            if (!result.ContainsKey(parameter) && References.GetValueOrDefault(parameter) is { } reference)
            {
                result[parameter] = reference;
            }
        }

        return result;
    }
}
