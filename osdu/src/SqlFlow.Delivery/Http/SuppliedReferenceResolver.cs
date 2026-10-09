using System.Diagnostics.CodeAnalysis;
using SqlFlow.Core.Secrets;

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
/// A reference substituted from the configuration is resolved once, by the node's resolver: properties do not chain, so
/// a supplied <c>${env:OTHER}</c> is read from the node's environment even when <c>OTHER</c> is a property too, and no
/// two properties naming each other can loop. A property whose value is its own reference
/// (<c>OSDU_URL = ${env:OSDU_URL}</c>) is handed back to the node's environment: set for a repository or a partition,
/// over a value the control plane sets for every one, that is how a scope says its nodes' own environment decides.
/// </para>
/// <para>
/// Which side answered each reference is what <see cref="ReferenceSource.Of"/> says, by the same rule
/// (<see cref="Answers"/>), and a run's trace names it for every reference the run resolves (the engine's run context
/// wraps this resolver to say so), by name and origin, never by value.
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

        // A reference the configuration does not answer (it does not name it, or names it as its own reference) is handed
        // back as it was, for the node to resolve from its own environment.
        return EnvReference().Replace(value, match => Answers(_supplied, match.Groups["name"].Value, out var supplied) ? supplied : match.Value);
    }

    /// <summary>
    /// Whether <paramref name="supplied"/> answers the reference <paramref name="name"/>, and with what: it does when it holds
    /// the name, unless what it holds is the very reference it fills (<see cref="DefersToNode"/>), which is the node's to
    /// answer. The one rule the substitution and <see cref="ReferenceSource.Of"/> apply, so where a run's trace says a value
    /// came from is where it came from.
    /// </summary>
    internal static bool Answers(IReadOnlyDictionary<string, string> supplied, string name, [NotNullWhen(true)] out string? value)
    {
        if (supplied.TryGetValue(name, out var held) && !DefersToNode(name, held))
        {
            value = held;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Whether the property <paramref name="name"/>, holding <paramref name="value"/>, is exactly its own reference
    /// (<c>${env:NAME}</c>), which leaves the reference to the node's own environment.
    /// </summary>
    internal static bool DefersToNode(string name, string value)
        => string.Equals(value, "${env:" + name + "}", StringComparison.Ordinal);

    /// <summary>
    /// The names of the <c>${env:NAME}</c> references in <paramref name="value"/>, in the order they appear, read exactly
    /// as the substitution reads them; none for a value without one.
    /// </summary>
    internal static IEnumerable<string> NamesIn(string value)
        => string.IsNullOrEmpty(value) || !value.Contains("${env:", StringComparison.Ordinal)
            ? []
            : EnvReference().Matches(value).Select(m => m.Groups["name"].Value);

    [System.Text.RegularExpressions.GeneratedRegex(@"\$\{env:(?<name>[A-Za-z_][A-Za-z0-9_]{0,63})\}")]
    private static partial System.Text.RegularExpressions.Regex EnvReference();
}

/// <summary>Where a reference a run resolves gets its value: the central configuration, or the node's own environment.</summary>
public enum ReferenceOrigin
{
    /// <summary>The node's own environment, which is where every reference came from before there was a configuration.</summary>
    Node,

    /// <summary>The central configuration the control plane supplied with the run.</summary>
    ControlPlane,
}

/// <summary>
/// One reference a run resolved, and where its value came from: what a run's trace says of each reference, the first time
/// the run resolves it. Values are never carried: it names the reference only.
/// </summary>
/// <param name="Name">The reference name, as a flow spells it in <c>${env:NAME}</c>.</param>
/// <param name="Origin">Where the value came from.</param>
/// <param name="Deferred">
/// True when the configuration names the reference as its own (<c>NAME = ${env:NAME}</c>), which leaves it to the node: the
/// origin is then the node although the run carries the name.
/// </param>
public sealed record ReferenceSource(string Name, ReferenceOrigin Origin, bool Deferred = false)
{
    /// <summary>
    /// Where the reference <paramref name="name"/> takes its value in a run given <paramref name="supplied"/>: the control
    /// plane when the configuration answers it, and the node otherwise, a property that is its own reference included, since
    /// <see cref="SuppliedReferenceResolver"/> hands that back to the node. The rule is the resolver's own
    /// (<see cref="SuppliedReferenceResolver.Answers"/>).
    /// </summary>
    public static ReferenceSource Of(string name, IReadOnlyDictionary<string, string> supplied)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(supplied);
        return SuppliedReferenceResolver.Answers(supplied, name, out _)
            ? new ReferenceSource(name, ReferenceOrigin.ControlPlane)
            : new ReferenceSource(name, ReferenceOrigin.Node, Deferred: supplied.ContainsKey(name));
    }
}
