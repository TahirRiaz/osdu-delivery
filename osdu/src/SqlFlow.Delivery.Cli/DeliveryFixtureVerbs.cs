using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Cli.Hosting;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Json;

namespace SqlFlow.Delivery.Cli;

/// <summary>
/// <c>sqlflow fixtures update &lt;flow.yaml&gt;</c>: writes what each fixture of the flow's mapping renders into its
/// <c>expected</c> block (osdu/docs/reference/cli/delivery.md). A fixture whose render would not be delivered, or whose
/// expected record is written in a form that cannot be edited in place, is left as written and named; the verb then ends
/// with an error, so a script never takes a partial update for a complete one.
/// </summary>
internal static class DeliveryFixtureVerbs
{
    public static async Task<int> FixturesAsync(CliVerbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var verb = context.Arguments.Positional(1)?.ToLowerInvariant() ?? string.Empty;
        if (verb != "update")
        {
            return context.UsageError("say what to do with the fixtures: update.");
        }

        if (context.Arguments.Positional(2) is not { } flowPath)
        {
            return context.UsageError("name the flow document whose mapping's fixtures to update.");
        }

        var ct = context.CancellationToken;
        var engine = context.Services.GetRequiredService<EngineContext>();
        var write = !context.Arguments.HasFlag("--dry-run");
        var source = engine.Documents.LoadSource(flowPath);
        var named = context.Arguments.GetOption("--interface");
        var flows = named is null ? source.Interfaces : [source.Interface(named)];

        // Interfaces that share a mapping file update it once.
        var updates = new List<FixtureUpdate>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var flow in flows)
        {
            var update = await FixtureUpdates.UpdateAsync(flow.Interface is null ? engine : engine.ForInterface(flow.Interface), flow, write, ct).ConfigureAwait(false);
            if (seen.Add(Path.GetFullPath(update.Path)))
            {
                updates.Add(update);
            }
        }

        var skipped = updates.Sum(u => u.Rewrite.Outcomes.Count(o => o.Kind == FixtureOutcomeKind.Skipped));
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonArray(updates.Select(u => (JsonNode)new JsonObject
            {
                ["mapping"] = u.Mapping,
                ["path"] = u.Path,
                ["written"] = u.Written,
                ["fixtures"] = new JsonArray(u.Rewrite.Outcomes.Select(o => (JsonNode)new JsonObject
                {
                    ["name"] = o.Name,
                    ["outcome"] = o.Kind.ToString().ToLowerInvariant(),
                    ["reason"] = o.Reason,
                }).ToArray()),
            }).ToArray())));
            return skipped == 0 ? 0 : 1;
        }

        foreach (var update in updates)
        {
            var action = update.Written ? "written" : update.Rewrite.Changed ? "not written (--dry-run)" : "nothing to write";
            context.Out.WriteLine($"{update.Mapping}  {update.Path}: {action}");
            foreach (var outcome in update.Rewrite.Outcomes)
            {
                var word = outcome.Kind switch
                {
                    FixtureOutcomeKind.Updated => update.Written ? "updated  " : "differs  ",
                    FixtureOutcomeKind.Unchanged => "unchanged",
                    _ => "skipped  ",
                };
                context.Out.WriteLine(outcome.Reason is null ? $"  {word} {outcome.Name}" : $"  {word} {outcome.Name}: {outcome.Reason}");
            }
        }

        if (skipped > 0)
        {
            context.Error.WriteLine($"ERROR  {skipped} fixture(s) were left as written; the reasons are above.");
            return 1;
        }

        return 0;
    }
}
