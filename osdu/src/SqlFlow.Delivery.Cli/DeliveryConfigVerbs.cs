using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Cli.Hosting;
using SqlFlow.Core;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Json;

namespace SqlFlow.Delivery.Cli;

/// <summary>
/// <c>sqlflow config</c>: the central configuration the control plane supplies to the runs it queues, read and written
/// from a deployment so an estate names where it delivers in one place rather than on every node.
/// </summary>
/// <remarks>
/// A property holds a non-secret value, or a <c>${env:...}</c> or <c>${keyvault:...}</c> reference the node resolves, so
/// a deployment can point a whole estate at a secret without this command, this database or a run payload ever holding
/// one.
/// </remarks>
public static class DeliveryConfigVerbs
{
    public static async Task<int> ConfigAsync(CliVerbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ct = context.CancellationToken;
        var store = context.Services.GetRequiredService<DeliveryConfigStore>();
        if (!store.Available)
        {
            throw new FlowValidationException(
                "The central configuration lives in the module's database. Run 'sqlflow config' with --db <conn-ref>, or set the catalog variable.");
        }

        var repo = Repo(context);
        switch (context.Arguments.Positional(1)?.ToLowerInvariant() ?? string.Empty)
        {
            case "list":
            {
                var rows = repo is null
                    ? await store.ListAllAsync(ct).ConfigureAwait(false)
                    : await store.ListAsync(repo, ct).ConfigureAwait(false);
                if (context.Json)
                {
                    context.Out.WriteLine(CanonicalJson.Pretty(new JsonArray([.. rows.Select(Describe)])));
                    return 0;
                }

                if (rows.Count == 0)
                {
                    context.Out.WriteLine("no configuration property is set; every reference a flow names is resolved on the node that runs it");
                    return 0;
                }

                foreach (var row in rows)
                {
                    var scope = row.RepoId is { } id ? id.ToString("D") : "(all repositories)";
                    context.Out.WriteLine($"{row.Name} = {row.Value}  [{scope}]  set by {row.UpdatedBy} at {row.UpdatedUtc:u}");
                }

                return 0;
            }

            case "effective":
            {
                if (repo is not { } id)
                {
                    return context.UsageError("name the repository with --repo <id>: what a run is given depends on which estate it belongs to.");
                }

                var effective = await store.EffectiveAsync(id, ct).ConfigureAwait(false);
                if (context.Json)
                {
                    var json = new JsonObject();
                    foreach (var (name, value) in effective.OrderBy(e => e.Key, StringComparer.Ordinal))
                    {
                        json[name] = value;
                    }

                    context.Out.WriteLine(CanonicalJson.Pretty(json));
                    return 0;
                }

                foreach (var (name, value) in effective.OrderBy(e => e.Key, StringComparer.Ordinal))
                {
                    context.Out.WriteLine($"{name} = {value}");
                }

                return 0;
            }

            case "set":
            {
                if (context.Arguments.Positional(2) is not { } name)
                {
                    return context.UsageError("name the property to set.");
                }

                if (context.Arguments.GetOption("--value") is not { } value)
                {
                    return context.UsageError("give the value with --value <value>.");
                }

                var row = await store
                    .SetAsync(repo, name, value, context.Arguments.GetOption("--description"), "cli", DateTime.UtcNow, ct)
                    .ConfigureAwait(false);
                context.Out.WriteLine(context.Json ? CanonicalJson.Pretty(Describe(row)) : $"{row.Name} set");
                return 0;
            }

            case "remove":
            {
                if (context.Arguments.Positional(2) is not { } name)
                {
                    return context.UsageError("name the property to remove.");
                }

                var removed = await store.RemoveAsync(repo, name, ct).ConfigureAwait(false);
                context.Out.WriteLine(removed ? $"{name} removed" : $"{name} was not set");
                return 0;
            }

            default:
                return context.UsageError("use 'config list', 'config effective --repo <id>', 'config set <name> --value <value>' or 'config remove <name>'.");
        }
    }

    /// <summary>The repository the command works on, or null for the control plane's own properties.</summary>
    private static Guid? Repo(CliVerbContext context)
    {
        if (context.Arguments.GetOption("--repo") is not { } text)
        {
            return null;
        }

        return Guid.TryParse(text, out var id)
            ? id
            : throw new FlowValidationException($"--repo '{text}' is not a repository id.");
    }

    private static JsonObject Describe(DeliveryConfigProperty row) => new()
    {
        ["name"] = row.Name,
        ["value"] = row.Value,
        ["repoId"] = row.RepoId?.ToString("D"),
        ["description"] = row.Description,
        ["updatedUtc"] = row.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture),
        ["updatedBy"] = row.UpdatedBy,
    };
}
