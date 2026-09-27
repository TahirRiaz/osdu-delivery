using SqlFlow.Core;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Model;

/// <summary>
/// One OSDU data partition a flow names under <c>partitions</c> (docs/partitions-design.md section 2): the partition a run
/// of the flow may target, by its <c>data-partition-id</c>. <see cref="KeepsLedger"/> marks the partition that keeps the
/// ledger a delivery flow kept before it named its partitions.
/// </summary>
public sealed record DeclaredPartition(string Name, bool KeepsLedger = false);

/// <summary>
/// The rules the partitions a flow names keep. A partition is named by its <c>data-partition-id</c>, written literally:
/// the name is the key a cache, a ledger and a run are kept under, so a reference a host resolves is never one.
/// </summary>
public static class PartitionNames
{
    /// <summary>The key a flow names its partitions under.</summary>
    public const string Key = "partitions";

    /// <summary>The run value a run names its target partition in, and the name a schedule's <c>values</c> sets it under.</summary>
    public const string RunValue = "partition";

    /// <summary>The most partitions one flow names.</summary>
    public const int MaxPerFlow = 64;

    /// <summary>
    /// <paramref name="declared"/> as a partition name, trimmed.
    /// </summary>
    /// <exception cref="FlowValidationException">The value is empty, a reference, or not a partition id.</exception>
    public static string Check(string? declared, string where)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(where);
        var name = declared?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            throw new FlowValidationException($"{where} is empty; name the partition by its data-partition-id.");
        }

        if (name.Contains("${", StringComparison.Ordinal))
        {
            throw new FlowValidationException(
                $"{where} is the reference '{Shown(name)}'. A partition is named literally, by its data-partition-id: the name is the key its cache, its ledger and its runs are kept under, and a key some host resolves is no key. Keep references for the endpoint and the credentials.");
        }

        if (!CacheScope.IsPartitionId(name))
        {
            throw new FlowValidationException(
                $"{where} '{Shown(name)}' is not a data-partition-id: letters, digits, underscore, hyphen and dot, at most {CacheScope.MaxLength} characters.");
        }

        return name;
    }

    /// <summary>Refuses a list that names no partition, names too many, or names one twice (ignoring case).</summary>
    /// <exception cref="FlowValidationException">The list breaks one of those rules.</exception>
    public static void CheckList(IReadOnlyList<string> names, string where)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentException.ThrowIfNullOrWhiteSpace(where);
        if (names.Count == 0)
        {
            throw new FlowValidationException($"{where} names no partition. Name at least one, or leave the key out for a flow that serves the partition its headers name.");
        }

        if (names.Count > MaxPerFlow)
        {
            throw new FlowValidationException($"{where} names {names.Count} partitions; a flow names at most {MaxPerFlow}.");
        }

        var twice = names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (twice.Count > 0)
        {
            throw new FlowValidationException($"{where} names {string.Join(", ", twice.Select(n => $"'{n}'"))} more than once (ignoring case); name each partition once.");
        }
    }

    /// <summary>The names of <paramref name="partitions"/>, as a message lists them.</summary>
    public static string Listed(IEnumerable<string> partitions) => string.Join(", ", partitions);

    /// <summary>
    /// A run's values with the partition it targets taken out (<see cref="RunValue"/>): the partition, or null when the run
    /// names none, and the values the flow's own parameters take. The partition never reaches the flow's parameters, so
    /// naming it moves neither a watermark nor a submission's scope. A flow written before partitions existed that declares
    /// a parameter of that name, and names no partitions, keeps the value as its own.
    /// </summary>
    public static (string? Partition, IReadOnlyDictionary<string, string> Values) SplitRunValues(
        IReadOnlyDictionary<string, string> values, bool keptAsParameter)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (keptAsParameter || !values.TryGetValue(RunValue, out var partition))
        {
            return (null, values);
        }

        var rest = values.Where(v => !string.Equals(v.Key, RunValue, StringComparison.Ordinal))
            .ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
        return (string.IsNullOrWhiteSpace(partition) ? null : partition.Trim(), rest);
    }

    private static string Shown(string value) => value.Length > 60 ? value[..60] + "..." : value;
}
