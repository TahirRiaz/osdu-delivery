using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Cli.Hosting;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Cli;

/// <summary>
/// The partition a command works in, settled as a run settles it (docs/partitions-design.md section 3): the one
/// <c>--partition</c> names, or when it names none, the registry's default among the flow's partitions, else the flow's
/// only one. The registry is read only when the flow leaves its partitions to it, or names several and none is named.
/// </summary>
internal static class CliPartitions
{
    /// <summary>The partition <c>--partition</c> names, or null.</summary>
    public static string? Requested(CliVerbContext context) => context.Arguments.GetOption("--partition");

    /// <summary><paramref name="source"/> bound as a run of it would be, for a command that does what a run does.</summary>
    public static async Task<SourceDefinition> ResolveAsync(CliVerbContext context, SourceDefinition source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        var requested = Requested(context);
        return source.Resolve(requested, await RegistryAsync(context, source.NeedsRegistry(requested), ct).ConfigureAwait(false));
    }

    /// <summary>
    /// <paramref name="source"/> bound for a command that sends nothing (reading a ledger, rendering fixtures): to the
    /// partition named as it is, since a partition taken out of the registry keeps its ledger and its records stay readable,
    /// and as a run would bind it when none is named.
    /// </summary>
    public static async Task<SourceDefinition> AsNamedAsync(CliVerbContext context, SourceDefinition source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        return Requested(context) is { } named && !string.IsNullOrWhiteSpace(named)
            ? source.ForPartition(named)
            : await ResolveAsync(context, source, ct).ConfigureAwait(false);
    }

    /// <summary>The partitions of <paramref name="cache"/> a command works in: <c>--partition</c> settled as a refresh settles it.</summary>
    public static async Task<IReadOnlyList<CacheDefinition>> ForRunAsync(CliVerbContext context, CacheDefinition cache, string? requested, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(cache);
        return cache.ForRun(requested, await RegistryAsync(context, cache.NeedsRegistry(requested), ct).ConfigureAwait(false));
    }

    private static async Task<RegisteredPartitions> RegistryAsync(CliVerbContext context, bool needed, CancellationToken ct)
        => needed
            ? await context.Services.GetRequiredService<IPartitionRegistry>().ReadAsync(ct).ConfigureAwait(false)
            : RegisteredPartitions.None;
}
