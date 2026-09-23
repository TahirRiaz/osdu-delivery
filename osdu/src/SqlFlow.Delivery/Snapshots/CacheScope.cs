using System.Text.RegularExpressions;
using SqlFlow.Core;

namespace SqlFlow.Delivery.Snapshots;

/// <summary>
/// The partition a cache belongs to (design.md section 6.2). A catalog keeps one cache per OSDU data partition: every cache
/// flow that searches a partition writes into it, and every delivery flow that delivers to the partition reads from it, so a
/// record any flow captured is there for all of them and is stored once. The scope is the partition the flows reach: their
/// <c>data-partition-id</c> header with its references resolved, trimmed.
/// </summary>
public static partial class CacheScope
{
    /// <summary>The widest partition name the catalog keeps a cache under.</summary>
    public const int MaxLength = 200;

    /// <summary>The header every OSDU request carries its partition in, and the one a cache is scoped by.</summary>
    public const string PartitionHeader = "data-partition-id";

    /// <summary>The cache scope of a flow's headers: the partition its requests carry.</summary>
    /// <exception cref="FlowValidationException">The headers declare no partition, or one that cannot name a cache.</exception>
    public static string Of(IReadOnlyDictionary<string, string> headers, string where)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentException.ThrowIfNullOrWhiteSpace(where);
        var partition = headers.FirstOrDefault(h => h.Key.Equals(PartitionHeader, StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrWhiteSpace(partition))
        {
            throw new FlowValidationException(
                $"{where}: the headers declare no '{PartitionHeader}', so there is no partition whose cache the flow uses.");
        }

        return Normalize(partition, where);
    }

    /// <summary>A declared partition as a cache scope: trimmed, and either an OSDU id segment or a secret reference.</summary>
    /// <exception cref="FlowValidationException">The value is neither.</exception>
    public static string Normalize(string partition, string where)
    {
        ArgumentNullException.ThrowIfNull(partition);
        var scope = partition.Trim();
        if (scope.Length == 0 || scope.Length > MaxLength || !(Segment().IsMatch(scope) || Reference().IsMatch(scope)))
        {
            var shown = scope.Length > 60 ? scope[..60] + "..." : scope;
            throw new FlowValidationException(
                $"{where}: {PartitionHeader} '{shown}' is neither a partition id (letters, digits, underscore, hyphen and dot, at most {MaxLength} characters) nor a ${{env:...}} or ${{keyvault:...}} reference, so it names no cache.");
        }

        return scope;
    }

    [GeneratedRegex(@"^[\w\-\.]+$")]
    private static partial Regex Segment();

    [GeneratedRegex(@"^\$\{(env|keyvault):[^}]+\}$")]
    private static partial Regex Reference();
}
