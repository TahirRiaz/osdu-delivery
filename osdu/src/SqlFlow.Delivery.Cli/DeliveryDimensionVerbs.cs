using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Cli.Hosting;
using SqlFlow.Core;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Cli;

/// <summary>
/// The <c>dimensions</c> verb: a dimension flow's dimensions where an operator already is, at a terminal or in a script,
/// without a control plane to reach (docs/dimension-plan.md, Stage 5). <c>list</c> shows each dimension and its last build,
/// <c>members</c> and <c>originals</c> page through what a dimension holds, <c>filter</c> writes the search that finds the
/// records of the members named, <c>history</c> lists the builds and <c>changes</c> the change log, and <c>export</c> writes
/// the whole as CSV or JSON Lines; the filter and the export are written by the same code as the API's. Building is a run
/// like any other: <c>sqlflow run &lt;flow.yaml&gt; --payload '{"dimensions":["name"]}'</c>.
/// </summary>
internal static class DeliveryDimensionVerbs
{
    /// <summary>Members, originals, builds or changes listed when the command line asks for no count.</summary>
    private const int DefaultMax = 50;

    /// <summary>The originals a member's line names beside it.</summary>
    private const int ShownOriginals = 3;

    public static async Task<int> DimensionsAsync(CliVerbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ct = context.CancellationToken;
        var engine = context.Services.GetRequiredService<EngineContext>();
        var verb = context.Arguments.Positional(1)?.ToLowerInvariant() ?? string.Empty;
        if (context.Arguments.Positional(2) is not { } flowPath)
        {
            return context.UsageError("name the dimension flow document whose dimensions these are.");
        }

        var ledger = engine.Ledger
            ?? throw new FlowValidationException(
                "Dimensions live in the module's database. Run 'sqlflow dimensions' with --db <conn-ref>, or set the catalog variable.");
        var flow = await CliPartitions.DimensionAsync(context, engine.Documents.LoadDimension(flowPath), ct).ConfigureAwait(false);
        if (verb == "list")
        {
            return await ListAsync(context, ledger, flow, ct).ConfigureAwait(false);
        }

        if (verb is not ("members" or "originals" or "filter" or "history" or "changes" or "export"))
        {
            return context.UsageError("say what to show: list, members, originals, filter, history, changes or export.");
        }

        var dimension = await DimensionAsync(context, ledger, flow, ct).ConfigureAwait(false);
        return verb switch
        {
            "members" => await MembersAsync(context, ledger, dimension, ct).ConfigureAwait(false),
            "originals" => await OriginalsAsync(context, ledger, dimension, ct).ConfigureAwait(false),
            "filter" => await FilterAsync(context, ledger, dimension, ct).ConfigureAwait(false),
            "history" => await HistoryAsync(context, ledger, dimension, ct).ConfigureAwait(false),
            "changes" => await ChangesAsync(context, ledger, dimension, ct).ConfigureAwait(false),
            _ => await ExportAsync(context, ledger, dimension, ct).ConfigureAwait(false),
        };
    }

    /// <summary>Each dimension the flow declares in its partition, with what it holds and its newest build.</summary>
    private static async Task<int> ListAsync(CliVerbContext context, ILedger ledger, DimensionFlowDefinition flow, CancellationToken ct)
    {
        var held = (await ledger.ListDimensionsAsync(null, flow.LedgerId, ct).ConfigureAwait(false))
            .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var latest = (await ledger.LatestDimensionRunsAsync(held.Values.Select(d => d.DimensionId).ToList(), ct).ConfigureAwait(false))
            .ToDictionary(r => r.DimensionId);
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["flow"] = flow.Name,
                ["ledger"] = flow.LedgerName,
                ["dimensions"] = new JsonArray(flow.Dimensions.Select(spec =>
                {
                    var dimension = held.GetValueOrDefault(spec.Name);
                    var newest = dimension is not null && latest.TryGetValue(dimension.DimensionId, out var run) ? run : null;
                    return (JsonNode)new JsonObject
                    {
                        ["dimension"] = spec.Name,
                        ["dimensionId"] = dimension?.DimensionId,
                        ["kind"] = spec.Kind,
                        ["path"] = spec.Path,
                        ["aggregateBy"] = dimension?.Field?.AggregateBy,
                        ["members"] = dimension?.Members,
                        ["originals"] = dimension?.Originals,
                        ["lastBuiltUtc"] = dimension?.LastBuiltUtc,
                        ["latestBuild"] = newest is null ? null : Described(newest),
                    };
                }).ToArray()),
            }));
            return 0;
        }

        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{flow.LedgerName}: {flow.Dimensions.Count} dimension(s)"));
        foreach (var spec in flow.Dimensions)
        {
            var dimension = held.GetValueOrDefault(spec.Name);
            if (dimension is null)
            {
                context.Out.WriteLine($"  {spec.Name}  ({spec.Kind} {spec.Path})  not built yet");
                continue;
            }

            var built = dimension.LastBuiltUtc is { } at ? "built " + Stamp(at) : "not built yet";
            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {spec.Name}  ({spec.Kind} {spec.Path})  {dimension.Members} member(s) from {dimension.Originals} original(s), {built}"));
            if (latest.TryGetValue(dimension.DimensionId, out var newest) && newest.DimensionRunId != dimension.LastRunId)
            {
                context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"      newest build {newest.DimensionRunId} {newest.Status} at {Stamp(newest.StartedUtc)}{(newest.Error is { Length: > 0 } error ? ": " + error : string.Empty)}"));
            }
        }

        if (held.Count == 0)
        {
            context.Out.WriteLine("  none built yet. Build them with: sqlflow run <flow.yaml> (a plan settles each field without reading: --operation plan)");
        }

        return 0;
    }

    /// <summary>A page of the dimension's members, in value order or with the most records first, each with its commonest originals.</summary>
    private static async Task<int> MembersAsync(CliVerbContext context, ILedger ledger, DimensionState dimension, CancellationToken ct)
    {
        var order = context.Arguments.GetOption("--order")?.Trim().ToLowerInvariant() switch
        {
            null or "value" => DimensionMemberOrder.Value,
            "records" => DimensionMemberOrder.Records,
            var other => throw new FlowValidationException($"--order '{other}' is not one of value, records."),
        };
        var max = Count(context.Arguments.GetOption("--max"), DefaultMax, "--max");
        var members = await ledger.ListDimensionMembersAsync(
            dimension.DimensionId, new DimensionMemberQuery(context.Arguments.GetOption("--search"), context.Arguments.HasFlag("--removed"), null, max, order), ct).ConfigureAwait(false);
        var top = (await ledger.TopMemberOriginalsAsync(dimension.DimensionId, members.Select(m => m.MemberId).ToList(), ShownOriginals, ct).ConfigureAwait(false))
            .GroupBy(v => v.MemberId ?? 0)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.Count).ThenBy(v => v.ValueId).Select(v => v.Original).ToList());
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["dimension"] = dimension.Name,
                ["members"] = new JsonArray(members.Select(m => (JsonNode)new JsonObject
                {
                    ["memberId"] = m.MemberId,
                    ["value"] = m.Value,
                    ["records"] = m.Records,
                    ["recordsExact"] = m.RecordsExact,
                    ["originals"] = m.Originals,
                    ["unfilterable"] = m.Unfilterable,
                    ["filter"] = m.Filter,
                    ["filterParts"] = m.FilterParts,
                    ["removedUtc"] = m.RemovedUtc,
                    ["top"] = new JsonArray((top.GetValueOrDefault(m.MemberId) ?? []).Select(o => (JsonNode)JsonValue.Create(o)!).ToArray()),
                }).ToArray()),
            }));
            return 0;
        }

        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{dimension.Name}: {members.Count} of {dimension.Members} member(s)"));
        foreach (var member in members)
        {
            var records = member.RecordsExact ? member.Records.ToString("N0", CultureInfo.InvariantCulture) : "~" + member.Records.ToString("N0", CultureInfo.InvariantCulture);
            var shown = top.GetValueOrDefault(member.MemberId) ?? [];
            var more = member.Originals > shown.Count ? string.Create(CultureInfo.InvariantCulture, $", {member.Originals - shown.Count} more") : string.Empty;
            var removed = member.RemovedUtc is { } gone ? "  (no build finds it since " + Stamp(gone) + ")" : string.Empty;
            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {records,12}  {member.Value}  <- {string.Join(", ", shown.Select(Quoted))}{more}{removed}"));
        }

        return 0;
    }

    /// <summary>A page of the dimension's originals: every one, a member's, or those under none, in arrival order or most records first.</summary>
    private static async Task<int> OriginalsAsync(CliVerbContext context, ILedger ledger, DimensionState dimension, CancellationToken ct)
    {
        var order = context.Arguments.GetOption("--order")?.Trim().ToLowerInvariant() switch
        {
            null or "arrival" => DimensionValueOrder.Arrival,
            "count" => DimensionValueOrder.Count,
            var other => throw new FlowValidationException($"--order '{other}' is not one of arrival, count."),
        };
        var leftOut = context.Arguments.HasFlag("--left-out");
        long? memberId = null;
        if (context.Arguments.GetOption("--member") is { } value)
        {
            if (leftOut)
            {
                return context.UsageError("an original under a member is not left out: give --member or --left-out, not both.");
            }

            memberId = (await MembersNamedAsync(ledger, dimension, [value], ct).ConfigureAwait(false))[0].MemberId;
        }

        var max = Count(context.Arguments.GetOption("--max"), DefaultMax, "--max");
        var originals = await ledger.ListDimensionValuesAsync(
            dimension.DimensionId,
            new DimensionValueQuery(context.Arguments.GetOption("--search"), memberId, leftOut, context.Arguments.HasFlag("--removed"), null, max, order),
            ct).ConfigureAwait(false);
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["dimension"] = dimension.Name,
                ["originals"] = new JsonArray(originals.Select(o => (JsonNode)new JsonObject
                {
                    ["valueId"] = o.ValueId,
                    ["original"] = o.Original,
                    ["member"] = o.MemberValue,
                    ["leftOut"] = o.LeftOut,
                    ["note"] = o.Note,
                    ["count"] = o.Count,
                    ["filterable"] = o.Filterable,
                    ["removedUtc"] = o.RemovedUtc,
                }).ToArray()),
            }));
            return 0;
        }

        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{dimension.Name}: {originals.Count} of {dimension.Originals} original(s)"));
        foreach (var original in originals)
        {
            var under = original.MemberValue is { } member ? "-> " + member : "under no member (" + original.LeftOut + ")";
            var unfilterable = original.Filterable ? string.Empty : "  (no query can carry it)";
            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {original.Count,12:N0}  {Quoted(original.Original)}  {under}{unfilterable}{(original.Note is { Length: > 0 } note ? "  " + note : string.Empty)}"));
        }

        return 0;
    }

    /// <summary>The search that finds the records of the members --member names, one query a line, ready to send.</summary>
    private static async Task<int> FilterAsync(CliVerbContext context, ILedger ledger, DimensionState dimension, CancellationToken ct)
    {
        var named = context.Arguments.GetOptions("--member");
        if (named.Count == 0)
        {
            return context.UsageError("name the members to filter by, by clean value: --member <value> (repeat it for more).");
        }

        var filter = await DimensionFilters.ForMembersAsync(ledger, dimension, [], named, ct).ConfigureAwait(false);
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["dimension"] = dimension.Name,
                ["kind"] = filter.Kind,
                ["query"] = filter.Query,
                ["aggregateBy"] = filter.AggregateBy,
                ["filters"] = new JsonArray(filter.Filters.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()),
                ["searches"] = new JsonArray(filter.Searches.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
                ["members"] = new JsonArray(filter.Members.Select(m => (JsonNode)JsonValue.Create(m.Value)!).ToArray()),
                ["originals"] = filter.Originals,
                ["unfilterable"] = filter.Unfilterable,
                ["removed"] = new JsonArray(filter.Removed.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
                ["missing"] = new JsonArray(filter.Missing.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()),
            }));
            return filter.Searches.Count == 0 ? 1 : 0;
        }

        // The searches go to the console alone, a line each, so a script can take them as they are; what the filter covers and
        // leaves out goes to the error stream.
        context.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{dimension.Name}: kind {filter.Kind}, {filter.Members.Count} member(s), {filter.Originals} original(s) in {filter.Searches.Count} search(es)"));
        if (filter.Unfilterable > 0)
        {
            context.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {filter.Unfilterable} original(s) no query can carry are left out: {string.Join(", ", filter.UnfilterableNamed.Take(5).Select(Quoted))}"));
        }

        if (filter.Removed.Count > 0)
        {
            context.Error.WriteLine("  no build finds these any more: " + string.Join(", ", filter.Removed.Select(Quoted)));
        }

        if (filter.Missing.Count > 0)
        {
            context.Error.WriteLine("  no member of the dimension: " + string.Join(", ", filter.Missing.Select(Quoted)));
        }

        foreach (var search in filter.Searches)
        {
            context.Out.WriteLine(search);
        }

        return filter.Searches.Count == 0 ? 1 : 0;
    }

    /// <summary>The dimension's builds, newest first: what each came to, how it read, and what it changed.</summary>
    private static async Task<int> HistoryAsync(CliVerbContext context, ILedger ledger, DimensionState dimension, CancellationToken ct)
    {
        var max = Count(context.Arguments.GetOption("--max"), DefaultMax, "--max");
        var runs = await ledger.ListDimensionRunsAsync(dimension.DimensionId, max, ct).ConfigureAwait(false);
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["dimension"] = dimension.Name,
                ["builds"] = new JsonArray(runs.Select(r => (JsonNode)Described(r)).ToArray()),
            }));
            return 0;
        }

        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{dimension.Name}: {runs.Count} build(s)"));
        foreach (var run in runs)
        {
            var changes = run.Changes;
            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {run.DimensionRunId,8}  {run.Status,-9}  {Stamp(run.StartedUtc)}  {run.Members} member(s) from {run.Originals} original(s), {run.LeftOut} under none; {changes.OriginalsAdded} arrived, {changes.OriginalsRemoved} left, {changes.OriginalsMoved} moved, {changes.OriginalsRestored} came back; {run.Read.Aggregations} aggregation(s), {run.Read.Splits} split(s), {run.Read.ScanPages} scan page(s)  by {run.Actor}"));
            if (run.Error is { Length: > 0 } error)
            {
                context.Out.WriteLine("            " + error);
            }

            foreach (var note in run.Read.Notes.Take(5))
            {
                context.Out.WriteLine("            " + note);
            }
        }

        return 0;
    }

    /// <summary>The change log, newest first: of every build, of the one --build names, of one member, or of one kind of change.</summary>
    private static async Task<int> ChangesAsync(CliVerbContext context, ILedger ledger, DimensionState dimension, CancellationToken ct)
    {
        long? build = null;
        if (context.Arguments.GetOption("--build") is { } asked)
        {
            build = long.TryParse(asked, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                ? id
                : throw new FlowValidationException($"--build '{asked}' is not a build number.");
        }

        var change = context.Arguments.GetOption("--change")?.Trim().ToLowerInvariant();
        if (change is not null && change is not (DimensionChangeKinds.Added or DimensionChangeKinds.Removed or DimensionChangeKinds.Moved or DimensionChangeKinds.Restored))
        {
            return context.UsageError($"--change '{change}' is not one of added, removed, moved, restored.");
        }

        long? memberId = null;
        if (context.Arguments.GetOption("--member") is { } value)
        {
            memberId = (await MembersNamedAsync(ledger, dimension, [value], ct).ConfigureAwait(false))[0].MemberId;
        }

        var max = Count(context.Arguments.GetOption("--max"), DefaultMax, "--max");
        var changes = await ledger.ListDimensionChangesAsync(dimension.DimensionId, new DimensionChangeQuery(build, null, memberId, change, null, max), ct).ConfigureAwait(false);
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["dimension"] = dimension.Name,
                ["changes"] = new JsonArray(changes.Select(c => (JsonNode)new JsonObject
                {
                    ["changeId"] = c.ChangeId,
                    ["build"] = c.DimensionRunId,
                    ["original"] = c.Original,
                    ["change"] = c.Change,
                    ["from"] = c.FromValue,
                    ["to"] = c.ToValue,
                    ["changedUtc"] = c.ChangedUtc,
                }).ToArray()),
            }));
            return 0;
        }

        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{dimension.Name}: {changes.Count} change(s)"));
        foreach (var c in changes)
        {
            var move = c.Change switch
            {
                DimensionChangeKinds.Moved => $"from {c.FromValue ?? "no member"} to {c.ToValue ?? "no member"}",
                DimensionChangeKinds.Removed => $"left {c.FromValue ?? "no member"}",
                _ => $"under {c.ToValue ?? "no member"}",
            };
            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {Stamp(c.ChangedUtc)}  build {c.DimensionRunId,-6}  {c.Change,-8}  {Quoted(c.Original)}  {move}"));
        }

        return 0;
    }

    /// <summary>The dimension's members or originals, whole, as CSV or JSON Lines, to --out or the console.</summary>
    private static async Task<int> ExportAsync(CliVerbContext context, ILedger ledger, DimensionState dimension, CancellationToken ct)
    {
        var set = DimensionExport.SetOf(context.Arguments.GetOption("--set"))
            ?? throw new FlowValidationException($"--set '{context.Arguments.GetOption("--set")}' is not one of members, originals.");
        var format = DimensionExport.FormatOf(context.Arguments.GetOption("--format"))
            ?? throw new FlowValidationException($"--format '{context.Arguments.GetOption("--format")}' is not one of csv, jsonl.");
        if (context.Arguments.GetOption("--out") is not { } outPath)
        {
            await DimensionExport.WriteAsync(ledger, dimension, set, format, context.Out, ct).ConfigureAwait(false);
            await context.Out.FlushAsync(ct).ConfigureAwait(false);
            return 0;
        }

        var full = Path.GetFullPath(outPath);
        if (Path.GetDirectoryName(full) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        // Written beside its final name and moved into place, so a failure part way never leaves half an export under it.
        var partial = full + ".partial";
        long rows;
        try
        {
            var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
            await using (file.ConfigureAwait(false))
            {
                rows = await DimensionExport.WriteAsync(ledger, dimension, set, format, file, ct).ConfigureAwait(false);
            }

            File.Move(partial, full, overwrite: true);
        }
        catch
        {
            File.Delete(partial);
            throw;
        }

        context.Error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Wrote {rows} {(set == DimensionExportSet.Members ? "member(s)" : "original(s)")} of {dimension.Name} to {full}"));
        return 0;
    }

    /// <summary>The dimension --dimension names, as the flow's ledger in its partition holds it.</summary>
    private static async Task<DimensionState> DimensionAsync(CliVerbContext context, ILedger ledger, DimensionFlowDefinition flow, CancellationToken ct)
    {
        if (context.Arguments.GetOption("--dimension") is not { } name || string.IsNullOrWhiteSpace(name))
        {
            throw new FlowValidationException(
                $"Name the dimension with --dimension <name>; {flow.Name} declares {string.Join(", ", flow.Dimensions.Select(d => d.Name))}.");
        }

        var spec = flow.Dimension(name)
            ?? throw new FlowValidationException($"{flow.Name} declares no dimension named '{name}'; it declares {string.Join(", ", flow.Dimensions.Select(d => d.Name))}.");
        return await ledger.FindDimensionAsync(flow.LedgerId, spec.Name, ct).ConfigureAwait(false)
            ?? throw new FlowValidationException(
                $"Dimension {spec.Name} has not been built in {flow.LedgerName} yet. Build it with: sqlflow run <flow.yaml> --payload '{{\"dimensions\":[\"{spec.Name}\"]}}'");
    }

    /// <summary>The members named by clean value, each one the dimension holds, in the order named.</summary>
    private static async Task<IReadOnlyList<DimensionMemberState>> MembersNamedAsync(ILedger ledger, DimensionState dimension, IReadOnlyList<string> values, CancellationToken ct)
    {
        var found = await ledger.GetDimensionMembersAsync(dimension.DimensionId, [], values, ct).ConfigureAwait(false);
        return values.Select(v => found.FirstOrDefault(m => string.Equals(m.Value, v, StringComparison.Ordinal))
                ?? throw new FlowValidationException($"Dimension {dimension.Name} has no member '{v}'. Members are named by their clean value, exactly."))
            .ToList();
    }

    private static JsonObject Described(DimensionRunState run) => new()
    {
        ["build"] = run.DimensionRunId,
        ["runId"] = run.RunId?.ToString("D"),
        ["status"] = run.Status,
        ["members"] = run.Members,
        ["originals"] = run.Originals,
        ["leftOut"] = run.LeftOut,
        ["unfilterable"] = run.Unfilterable,
        ["originalsAdded"] = run.Changes.OriginalsAdded,
        ["originalsRemoved"] = run.Changes.OriginalsRemoved,
        ["originalsMoved"] = run.Changes.OriginalsMoved,
        ["originalsRestored"] = run.Changes.OriginalsRestored,
        ["records"] = run.Read.Records,
        ["withValue"] = run.Read.WithValue,
        ["aggregations"] = run.Read.Aggregations,
        ["splits"] = run.Read.Splits,
        ["scanPages"] = run.Read.ScanPages,
        ["actor"] = run.Actor,
        ["startedUtc"] = run.StartedUtc,
        ["completedUtc"] = run.CompletedUtc,
        ["error"] = run.Error,
    };

    private static int Count(string? value, int fallback, string option)
    {
        if (value is null)
        {
            return fallback;
        }

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count is > 0 and <= OsduLedger.MaxDimensionPage
            ? count
            : throw new FlowValidationException(string.Create(CultureInfo.InvariantCulture, $"{option} '{value}' is not a count between 1 and {OsduLedger.MaxDimensionPage}."));
    }

    /// <summary>A value as a line shows it: quoted, so an empty value or one with spaces at its ends reads as it is.</summary>
    private static string Quoted(string value) => "\"" + value + "\"";

    private static string Stamp(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
