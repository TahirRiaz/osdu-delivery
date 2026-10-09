using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Cli.Hosting;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Json;

namespace SqlFlow.Delivery.Cli;

/// <summary>
/// <c>sqlflow partition</c>: the partition registry (docs/partitions-design.md section 2.1), the OSDU partitions a flow that
/// names none serves and the default one a run that names none runs in, read and kept from a deployment.
/// </summary>
/// <remarks>
/// The rows a repository sync writes per partition (a registry-driven flow's interfaces and cache types) follow the
/// registry at each sync; the control plane's own upkeep makes every repository due at once, and from here the next sync
/// does it (<c>sqlflow db sync</c>, or Sync now on the Repositories page). A change is recorded under the account the
/// command runs as (<see cref="RunActors.LocalAccount"/>), as every command line change is.
/// </remarks>
public static class DeliveryPartitionVerbs
{
    public static Task<int> PartitionAsync(CliVerbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return RunAsync(ModuleCommand.Of(context));
    }

    /// <summary>The verb, over the command it runs for.</summary>
    internal static async Task<int> RunAsync(ModuleCommand context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ct = context.CancellationToken;
        var actor = RunActors.LocalAccount();
        var registry = context.Services.GetRequiredService<DeliveryPartitionRegistry>();
        if (!registry.Available)
        {
            throw new FlowValidationException(
                "The partition registry lives in the module's database. Run 'sqlflow partition' with --db <conn-ref>, or set the catalog variable.");
        }

        var name = context.Arguments.Positional(2);
        switch (context.Arguments.Positional(1)?.ToLowerInvariant() ?? string.Empty)
        {
            case "list":
            {
                var rows = await registry.ListAsync(ct).ConfigureAwait(false);
                if (context.Json)
                {
                    context.Out.WriteLine(CanonicalJson.Pretty(new JsonArray([.. rows.Select(Describe)])));
                    return 0;
                }

                if (rows.Count == 0)
                {
                    context.Out.WriteLine("no partition is registered; a flow that names no partitions has none to run in. Register one with 'sqlflow partition add <name>'.");
                    return 0;
                }

                foreach (var row in rows)
                {
                    var marker = row.IsDefault ? "  (default)" : string.Empty;
                    var described = row.Description is { } text ? $"  {text}" : string.Empty;
                    context.Out.WriteLine(string.Create(
                        CultureInfo.InvariantCulture, $"{row.Name}{marker}{described}  [registered by {row.CreatedBy} at {row.CreatedUtc:u}]"));
                }

                return 0;
            }

            case "add":
            {
                if (name is null)
                {
                    return context.UsageError("name the partition to register: its data-partition-id, as runs name it.");
                }

                var row = await registry
                    .AddAsync(name, context.Arguments.GetOption("--description"), context.Arguments.HasFlag("--default"), actor, DateTime.UtcNow, ct)
                    .ConfigureAwait(false);
                if (context.Json)
                {
                    context.Out.WriteLine(CanonicalJson.Pretty(Describe(row)));
                    return 0;
                }

                context.Out.WriteLine(row.IsDefault ? $"{row.Name} registered, and is the default" : $"{row.Name} registered");
                context.Out.WriteLine(SyncNote);
                return 0;
            }

            case "describe":
            {
                if (name is null)
                {
                    return context.UsageError("name the partition to describe.");
                }

                if (context.Arguments.GetOption("--description") is not { } description)
                {
                    return context.UsageError("give what the partition is for with --description <text>; an empty one clears it.");
                }

                var row = await registry.DescribeAsync(name, description, actor, DateTime.UtcNow, ct).ConfigureAwait(false);
                context.Out.WriteLine(context.Json ? CanonicalJson.Pretty(Describe(row)) : $"{row.Name} described");
                return 0;
            }

            case "default":
            {
                if (name is null)
                {
                    return context.UsageError("name the partition a run that names none should run in.");
                }

                var row = await registry.MakeDefaultAsync(name, actor, DateTime.UtcNow, ct).ConfigureAwait(false);
                context.Out.WriteLine(context.Json ? CanonicalJson.Pretty(Describe(row)) : $"{row.Name} is the default");
                return 0;
            }

            case "remove":
            {
                if (name is null)
                {
                    return context.UsageError("name the partition to remove.");
                }

                var removed = await registry.RemoveAsync(name, ct).ConfigureAwait(false);
                context.Out.WriteLine(removed
                    ? $"{name.Trim()} removed; its caches, ledgers and runs stay, and no registry-driven flow runs in it until it is registered again"
                    : $"{name.Trim()} was not registered");
                if (removed)
                {
                    context.Out.WriteLine(SyncNote);
                }

                return 0;
            }

            default:
                return context.UsageError(
                    "use 'partition list', 'partition add <name> [--description <text>] [--default]', 'partition describe <name> --description <text>', 'partition default <name>' or 'partition remove <name>'.");
        }
    }

    private const string SyncNote =
        "each repository describes its registry-driven flows in the registered partitions at its next sync ('sqlflow db sync', or Sync now on the Repositories page)";

    private static JsonObject Describe(DeliveryPartition row) => new()
    {
        ["name"] = row.Name,
        ["description"] = row.Description,
        ["isDefault"] = row.IsDefault,
        ["createdUtc"] = row.CreatedUtc.ToString("O", CultureInfo.InvariantCulture),
        ["createdBy"] = row.CreatedBy,
        ["updatedUtc"] = row.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture),
        ["updatedBy"] = row.UpdatedBy,
    };
}
