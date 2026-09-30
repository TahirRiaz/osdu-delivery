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
/// <c>values</c> and <c>keys</c> page through what a dimension holds (the human-friendly values, and the keys exactly as the
/// index holds them), <c>filter</c> writes the search that finds the records of the values named, <c>search</c> composes the
/// search across the flow's dimensions from the values picked in each, <c>history</c> lists the builds and <c>changes</c> the
/// change log, and <c>export</c> writes the whole as CSV or JSON Lines; the filter, the search and the export are written by
/// the same code as the API's. Building is a run like any other: <c>sqlflow run &lt;flow.yaml&gt; --payload '{"dimensions":["name"]}'</c>.
/// </summary>
internal static class DeliveryDimensionVerbs
{
    /// <summary>Values, keys, builds or changes listed when the command line asks for no count.</summary>
    private const int DefaultMax = 50;

    /// <summary>The keys a value's line names beside it.</summary>
    private const int ShownKeys = 3;

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

        if (verb == "search")
        {
            return await SearchAsync(context, ledger, flow, ct).ConfigureAwait(false);
        }

        if (verb is not ("values" or "keys" or "filter" or "history" or "changes" or "export"))
        {
            return context.UsageError("say what to show: list, values, keys, filter, search, history, changes or export.");
        }

        var dimension = await DimensionAsync(context, ledger, flow, ct).ConfigureAwait(false);
        return verb switch
        {
            "values" => await ValuesAsync(context, ledger, dimension, ct).ConfigureAwait(false),
            "keys" => await KeysAsync(context, ledger, dimension, ct).ConfigureAwait(false),
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
                        ["label"] = spec.Label.Count == 0 ? null : new JsonArray(spec.Label.Select(l => (JsonNode)JsonValue.Create(l)!).ToArray()),
                        ["aggregateBy"] = dimension?.Field?.AggregateBy,
                        ["values"] = dimension?.Members,
                        ["keys"] = dimension?.Originals,
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
            var label = spec.Label.Count == 0 ? string.Empty : " labelled by " + string.Join(" > ", spec.Label);
            if (dimension is null)
            {
                context.Out.WriteLine($"  {spec.Name}  ({spec.Kind} {spec.Path}{label})  not built yet");
                continue;
            }

            var built = dimension.LastBuiltUtc is { } at ? "built " + Stamp(at) : "not built yet";
            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {spec.Name}  ({spec.Kind} {spec.Path}{label})  {dimension.Members} value(s) from {dimension.Originals} key(s), {built}"));
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

    /// <summary>A page of the dimension's values, in value order or with the most records first, each with its commonest keys.</summary>
    private static async Task<int> ValuesAsync(CliVerbContext context, ILedger ledger, DimensionState dimension, CancellationToken ct)
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
        var top = (await ledger.TopMemberOriginalsAsync(dimension.DimensionId, members.Select(m => m.MemberId).ToList(), ShownKeys, ct).ConfigureAwait(false))
            .GroupBy(v => v.MemberId ?? 0)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.Count).ThenBy(v => v.ValueId).Select(v => v.Original).ToList());
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["dimension"] = dimension.Name,
                ["values"] = new JsonArray(members.Select(m => (JsonNode)new JsonObject
                {
                    ["valueId"] = m.MemberId,
                    ["value"] = m.Value,
                    ["records"] = m.Records,
                    ["recordsExact"] = m.RecordsExact,
                    ["keys"] = m.Originals,
                    ["unfilterable"] = m.Unfilterable,
                    ["filter"] = m.Filter,
                    ["filterParts"] = m.FilterParts,
                    ["removedUtc"] = m.RemovedUtc,
                    ["top"] = new JsonArray((top.GetValueOrDefault(m.MemberId) ?? []).Select(o => (JsonNode)JsonValue.Create(o)!).ToArray()),
                }).ToArray()),
            }));
            return 0;
        }

        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{dimension.Name}: {members.Count} of {dimension.Members} value(s)"));
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

    /// <summary>
    /// A page of the dimension's keys, each exactly as the index holds it with its label and filter: every one, a value's, or
    /// those of no value, in arrival order or most records first.
    /// </summary>
    private static async Task<int> KeysAsync(CliVerbContext context, ILedger ledger, DimensionState dimension, CancellationToken ct)
    {
        var order = context.Arguments.GetOption("--order")?.Trim().ToLowerInvariant() switch
        {
            null or "arrival" => DimensionValueOrder.Arrival,
            "count" => DimensionValueOrder.Count,
            var other => throw new FlowValidationException($"--order '{other}' is not one of arrival, count."),
        };
        var leftOut = context.Arguments.HasFlag("--left-out");
        long? memberId = null;
        if (context.Arguments.GetOption("--value") is { } named)
        {
            if (leftOut)
            {
                return context.UsageError("a key of a value is not left out: give --value or --left-out, not both.");
            }

            memberId = (await ValuesNamedAsync(ledger, dimension, [named], ct).ConfigureAwait(false))[0].MemberId;
        }

        var max = Count(context.Arguments.GetOption("--max"), DefaultMax, "--max");
        var keys = await ledger.ListDimensionValuesAsync(
            dimension.DimensionId,
            new DimensionValueQuery(context.Arguments.GetOption("--search"), memberId, leftOut, context.Arguments.HasFlag("--removed"), null, max, order),
            ct).ConfigureAwait(false);
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["dimension"] = dimension.Name,
                ["keys"] = new JsonArray(keys.Select(k => (JsonNode)new JsonObject
                {
                    ["keyId"] = k.ValueId,
                    ["key"] = k.Original,
                    ["label"] = k.Label,
                    ["labelFrom"] = k.LabelFrom,
                    ["value"] = k.MemberValue,
                    ["leftOut"] = k.LeftOut,
                    ["note"] = k.Note,
                    ["count"] = k.Count,
                    ["filterable"] = k.Filterable,
                    ["filter"] = k.Filter,
                    ["removedUtc"] = k.RemovedUtc,
                }).ToArray()),
            }));
            return 0;
        }

        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{dimension.Name}: {keys.Count} of {dimension.Originals} key(s)"));
        foreach (var key in keys)
        {
            var of = key.MemberValue is { } member ? "-> " + member : "of no value (" + key.LeftOut + ")";
            var label = key.Label is { } read && !string.Equals(read, key.MemberValue, StringComparison.Ordinal) ? "  label " + Quoted(read) : string.Empty;
            var unfilterable = key.Filterable ? string.Empty : "  (no query can carry it)";
            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {key.Count,12:N0}  {Quoted(key.Original)}  {of}{label}{unfilterable}{(key.Note is { Length: > 0 } note ? "  " + note : string.Empty)}"));
            if (key.Filter is { } filter)
            {
                context.Out.WriteLine("                search " + filter);
            }
        }

        return 0;
    }

    /// <summary>The search that finds the records of the values --value names, one query a line, ready to send.</summary>
    private static async Task<int> FilterAsync(CliVerbContext context, ILedger ledger, DimensionState dimension, CancellationToken ct)
    {
        var named = context.Arguments.GetOptions("--value");
        if (named.Count == 0)
        {
            return context.UsageError("name the values to filter by, exactly as the dimension holds them: --value <value> (repeat it for more).");
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
                ["values"] = new JsonArray(filter.Members.Select(m => (JsonNode)JsonValue.Create(m.Value)!).ToArray()),
                ["keys"] = filter.Originals,
                ["unfilterable"] = filter.Unfilterable,
                ["removed"] = new JsonArray(filter.Removed.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
                ["missing"] = new JsonArray(filter.Missing.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()),
            }));
            return filter.Searches.Count == 0 ? 1 : 0;
        }

        // The searches go to the console alone, a line each, so a script can take them as they are; what the filter covers and
        // leaves out goes to the error stream.
        context.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{dimension.Name}: kind {filter.Kind}, {filter.Members.Count} value(s), {filter.Originals} key(s) in {filter.Searches.Count} search(es)"));
        if (filter.Unfilterable > 0)
        {
            context.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {filter.Unfilterable} key(s) no query can carry are left out: {string.Join(", ", filter.UnfilterableNamed.Take(5).Select(Quoted))}"));
        }

        if (filter.Removed.Count > 0)
        {
            context.Error.WriteLine("  no build finds these any more: " + string.Join(", ", filter.Removed.Select(Quoted)));
        }

        if (filter.Missing.Count > 0)
        {
            context.Error.WriteLine("  no value of the dimension: " + string.Join(", ", filter.Missing.Select(Quoted)));
        }

        foreach (var search in filter.Searches)
        {
            context.Out.WriteLine(search);
        }

        return filter.Searches.Count == 0 ? 1 : 0;
    }

    /// <summary>
    /// The search that finds the records holding one of the values picked in each dimension --pick names: OR within a
    /// dimension, AND across them, in the kind every dimension reads or the one --kind names, narrowed by --within. The query
    /// goes to the console alone, so a script takes it as it is; with --json, the whole composition with the request to send.
    /// </summary>
    private static async Task<int> SearchAsync(CliVerbContext context, ILedger ledger, DimensionFlowDefinition flow, CancellationToken ct)
    {
        var given = context.Arguments.GetOptions("--pick");
        if (given.Count == 0)
        {
            return context.UsageError("pick the values to search for: --pick <dimension>=<value> (repeat it for more values and dimensions).");
        }

        // A pick is split at its first '=': a dimension's name holds none, and a value may.
        var byDimension = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pick in given)
        {
            var at = pick.IndexOf('=', StringComparison.Ordinal);
            if (at <= 0)
            {
                return context.UsageError($"--pick '{pick}' is not <dimension>=<value>.");
            }

            var name = pick[..at].Trim();
            (byDimension.TryGetValue(name, out var values) ? values : byDimension[name] = []).Add(pick[(at + 1)..]);
        }

        var picks = new List<DimensionPick>(byDimension.Count);
        foreach (var (name, values) in byDimension)
        {
            var spec = flow.Dimension(name)
                ?? throw new FlowValidationException($"{flow.Name} declares no dimension named '{name}'; it declares {string.Join(", ", flow.Dimensions.Select(d => d.Name))}.");
            var dimension = await ledger.FindDimensionAsync(flow.LedgerId, spec.Name, ct).ConfigureAwait(false)
                ?? throw new FlowValidationException(
                    $"Dimension {spec.Name} has not been built in {flow.LedgerName} yet. Build it with: sqlflow run <flow.yaml> --payload '{{\"dimensions\":[\"{spec.Name}\"]}}'");
            picks.Add(new DimensionPick(dimension, [], values.Distinct(StringComparer.Ordinal).ToList()));
        }

        var set = await DimensionSearch.ComposeAsync(ledger, picks, context.Arguments.GetOption("--kind"), context.Arguments.GetOption("--within"), ct).ConfigureAwait(false);
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["kind"] = set.Kind,
                ["query"] = set.Query,
                ["request"] = new JsonObject { ["kind"] = set.Kind, ["query"] = set.Query, ["limit"] = 1000 },
                ["clauses"] = set.Clauses,
                ["parts"] = new JsonArray(set.Parts.Select(p => (JsonNode)new JsonObject
                {
                    ["dimension"] = p.Dimension,
                    ["aggregateBy"] = p.AggregateBy,
                    ["values"] = new JsonArray(p.Values.Select(v => (JsonNode)JsonValue.Create(v.Value)!).ToArray()),
                    ["keys"] = p.Keys,
                    ["unfilterable"] = p.Unfilterable,
                    ["filter"] = p.Filter,
                    ["query"] = p.Query,
                }).ToArray()),
                ["removed"] = new JsonArray(set.Removed.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
                ["missing"] = new JsonArray(set.Missing.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()),
                ["notes"] = new JsonArray(set.Notes.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
            }));
            return 0;
        }

        context.Error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"kind {set.Kind}, {set.Parts.Count} dimension(s), {set.Clauses} clause(s)"));
        foreach (var part in set.Parts)
        {
            context.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {part.Dimension}: {string.Join(", ", part.Values.Select(v => Quoted(v.Value)))} ({part.Keys} key(s) of {part.AggregateBy})"));
        }

        foreach (var line in set.Removed.Select(r => "  no build finds this any more: " + r)
                     .Concat(set.Missing.Select(m => "  no value of the dimension: " + m))
                     .Concat(set.Notes.Select(n => "  " + n)))
        {
            context.Error.WriteLine(line);
        }

        context.Out.WriteLine(set.Query);
        return 0;
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
                $"  {run.DimensionRunId,8}  {run.Status,-9}  {Stamp(run.StartedUtc)}  {run.Members} value(s) from {run.Originals} key(s), {run.LeftOut} of none; {changes.OriginalsAdded} arrived, {changes.OriginalsRemoved} left, {changes.OriginalsMoved} moved, {changes.OriginalsRestored} came back; {run.Read.Aggregations} aggregation(s), {run.Read.Splits} split(s), {run.Read.ScanPages} scan page(s), {run.Read.Labelled} labelled in {run.Read.LabelQueries} search(es)  by {run.Actor}"));
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

    /// <summary>The change log, newest first: of every build, of the one --build names, of one value, or of one kind of change.</summary>
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
        if (context.Arguments.GetOption("--value") is { } value)
        {
            memberId = (await ValuesNamedAsync(ledger, dimension, [value], ct).ConfigureAwait(false))[0].MemberId;
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
                    ["key"] = c.Original,
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
                DimensionChangeKinds.Moved => $"from {c.FromValue ?? "no value"} to {c.ToValue ?? "no value"}",
                DimensionChangeKinds.Removed => $"left {c.FromValue ?? "no value"}",
                _ => $"to {c.ToValue ?? "no value"}",
            };
            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {Stamp(c.ChangedUtc)}  build {c.DimensionRunId,-6}  {c.Change,-8}  {Quoted(c.Original)}  {move}"));
        }

        return 0;
    }

    /// <summary>The dimension's values or keys, whole, as CSV or JSON Lines, to --out or the console.</summary>
    private static async Task<int> ExportAsync(CliVerbContext context, ILedger ledger, DimensionState dimension, CancellationToken ct)
    {
        var set = DimensionExport.SetOf(context.Arguments.GetOption("--set"))
            ?? throw new FlowValidationException($"--set '{context.Arguments.GetOption("--set")}' is not one of values, keys.");
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

        context.Error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Wrote {rows} {(set == DimensionExportSet.Values ? "value(s)" : "key(s)")} of {dimension.Name} to {full}"));
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

    /// <summary>The values named, each one the dimension holds, in the order named.</summary>
    private static async Task<IReadOnlyList<DimensionMemberState>> ValuesNamedAsync(ILedger ledger, DimensionState dimension, IReadOnlyList<string> values, CancellationToken ct)
    {
        var found = await ledger.GetDimensionMembersAsync(dimension.DimensionId, [], values, ct).ConfigureAwait(false);
        return values.Select(v => found.FirstOrDefault(m => string.Equals(m.Value, v, StringComparison.Ordinal))
                ?? throw new FlowValidationException($"Dimension {dimension.Name} has no value '{v}'. Values are named exactly as the dimension holds them."))
            .ToList();
    }

    private static JsonObject Described(DimensionRunState run) => new()
    {
        ["build"] = run.DimensionRunId,
        ["runId"] = run.RunId?.ToString("D"),
        ["status"] = run.Status,
        ["values"] = run.Members,
        ["keys"] = run.Originals,
        ["leftOut"] = run.LeftOut,
        ["unfilterable"] = run.Unfilterable,
        ["keysAdded"] = run.Changes.OriginalsAdded,
        ["keysRemoved"] = run.Changes.OriginalsRemoved,
        ["keysMoved"] = run.Changes.OriginalsMoved,
        ["keysRestored"] = run.Changes.OriginalsRestored,
        ["labelled"] = run.Read.Labelled,
        ["unlabelled"] = run.Read.Unlabelled,
        ["labelQueries"] = run.Read.LabelQueries,
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
