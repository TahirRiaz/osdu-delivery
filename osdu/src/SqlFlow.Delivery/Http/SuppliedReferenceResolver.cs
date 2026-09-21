using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Http;

/// <summary>
/// The resolver a run uses: the central configuration the control plane supplied with the run answers first, and
/// everything it does not name falls through to the node's own resolver, which reads the process environment and the
/// key vaults the node reaches.
/// </summary>
/// <remarks>
/// <para>
/// A supplied value may itself be a <c>${env:NAME}</c> or <c>${keyvault:vault/secret}</c> reference, so the control
/// plane can point a whole estate at a secret without ever holding it: the substitution happens here and the result is
/// resolved by the node's own resolver. A value that is not a reference is used as it stands.
/// </para>
/// <para>
/// A reference substituted from the configuration is resolved once. A supplied value that names itself, directly or
/// through another property, is refused rather than followed, so a configuration mistake fails the run with a message
/// instead of looping.
/// </para>
/// </remarks>
public sealed partial class SuppliedReferenceResolver : ISecretResolver
{
    private readonly IReadOnlyDictionary<string, string> _supplied;
    private readonly ISecretResolver _node;

    public SuppliedReferenceResolver(IReadOnlyDictionary<string, string> supplied, ISecretResolver node)
    {
        ArgumentNullException.ThrowIfNull(supplied);
        ArgumentNullException.ThrowIfNull(node);
        _supplied = supplied;
        _node = node;
    }

    /// <summary>
    /// The resolver for a run: <paramref name="node"/> itself when the run carries no configuration, so a run that is
    /// given nothing costs nothing and behaves exactly as it did before there was a configuration to give.
    /// </summary>
    public static ISecretResolver For(IReadOnlyDictionary<string, string> supplied, ISecretResolver node)
    {
        ArgumentNullException.ThrowIfNull(supplied);
        ArgumentNullException.ThrowIfNull(node);
        return supplied.Count == 0 ? node : new SuppliedReferenceResolver(supplied, node);
    }

    public string Resolve(string value) => _node.Resolve(Substitute(value));

    public Task<string> ResolveAsync(string value, CancellationToken ct = default)
        => _node.ResolveAsync(Substitute(value), ct);

    /// <summary>
    /// <paramref name="value"/> with every <c>${env:NAME}</c> the configuration names replaced by what it holds. A
    /// reference the configuration does not name is left for the node's resolver, which is what makes a node's own
    /// environment the fallback rather than an error.
    /// </summary>
    private string Substitute(string value)
    {
        if (string.IsNullOrEmpty(value) || _supplied.Count == 0 || !value.Contains("${env:", StringComparison.Ordinal))
        {
            return value;
        }

        return EnvReference().Replace(value, match =>
        {
            var name = match.Groups["name"].Value;
            if (!_supplied.TryGetValue(name, out var supplied))
            {
                return match.Value;
            }

            // A property whose value is the very reference it fills would substitute forever. Hand the original back so
            // the node resolves it from its own environment, which is the only place such a value can mean anything.
            return supplied == match.Value ? match.Value : supplied;
        });
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\$\{env:(?<name>[A-Za-z_][A-Za-z0-9_]{0,63})\}")]
    private static partial System.Text.RegularExpressions.Regex EnvReference();
}

/// <summary>Where a reference a run resolved got its value, which a run records so a delivery can be explained.</summary>
public enum ReferenceOrigin
{
    /// <summary>The node's own environment, which is where every reference came from before there was a configuration.</summary>
    Node,

    /// <summary>The central configuration the control plane supplied with the run.</summary>
    ControlPlane,
}

/// <summary>One reference a run was given, and where it came from. Values are never carried: a listing names them only.</summary>
/// <param name="Name">The reference name, as a flow spells it in <c>${env:NAME}</c>.</param>
/// <param name="Origin">Where the value came from.</param>
public sealed record ReferenceSource(string Name, ReferenceOrigin Origin)
{
    /// <summary>
    /// What a run was given, by name: every property the control plane supplied, and every other reference the flow
    /// declares, which the node answered. Ordered by name so a run's listing reads the same way every time.
    /// </summary>
    public static IReadOnlyList<ReferenceSource> Of(IReadOnlyDictionary<string, string> supplied, IEnumerable<string> declared)
    {
        ArgumentNullException.ThrowIfNull(supplied);
        ArgumentNullException.ThrowIfNull(declared);
        var names = new SortedSet<string>(supplied.Keys, StringComparer.Ordinal);
        foreach (var name in declared.Where(DeliveryConfigNames.IsName))
        {
            names.Add(name);
        }

        return [.. names.Select(n => new ReferenceSource(n, supplied.ContainsKey(n) ? ReferenceOrigin.ControlPlane : ReferenceOrigin.Node))];
    }
}
