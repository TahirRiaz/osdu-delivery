using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Search;

namespace SqlFlow.Delivery.Engine.Dimensions;

/// <summary>A key's label as the labeler read it: the text, the record it was read from, or why there is none.</summary>
/// <param name="Label">The text the last step's path holds in the record reached; null when none was read.</param>
/// <param name="From">The id of the record the label was read from; null when none was reached.</param>
/// <param name="Problem">Why the key has no label; null when it has one.</param>
public sealed record KeyLabel(string? Label, string? From, string? Problem);

/// <summary>What labelling a dimension's keys came to.</summary>
/// <param name="Labels">Each key's label, by key.</param>
/// <param name="Labelled">Keys that have a label.</param>
/// <param name="Unlabelled">Keys that have none.</param>
/// <param name="Queries">Searches asked.</param>
/// <param name="Notes">What labelling had to say, a line each.</param>
public sealed record KeyLabels(IReadOnlyDictionary<string, KeyLabel> Labels, long Labelled, long Unlabelled, int Queries, IReadOnlyList<string> Notes)
{
    public static KeyLabels None { get; } = new(new Dictionary<string, KeyLabel>(StringComparer.Ordinal), 0, 0, 0, []);
}

/// <summary>
/// Reads the human-friendly label of each key of a dimension (docs/dimension-plan.md, Keys and values): a key naming an OSDU
/// record (<c>dev:master-data--Wellbore:NO-15-9-19-A:</c>) is followed to the record it names, and the label's path is read
/// there (<c>data.FacilityName</c>); a label of several steps follows each step's reference to the next record
/// (<c>data.GeoContexts.GeoPoliticalEntityID</c>, then <c>data.GeoPoliticalEntityName</c>). Records are found by id through
/// the search service, <see cref="IdsPerQuery"/> ids a search, in the kind of their entity type, so the labels of a hundred
/// thousand keys take a few hundred searches. A key that names no record, a record the search does not hold, and one whose
/// path holds nothing, have no label, and the result says how many and why.
/// </summary>
public sealed class DimensionLabeler
{
    /// <summary>The ids one search finds: each is a clause, and the service allows 1024 in a query.</summary>
    public const int IdsPerQuery = 500;

    /// <summary>The longest label kept; a longer one is cut.</summary>
    public const int MaxLabelLength = 1024;

    /// <summary>The keys a note names as examples.</summary>
    private const int Examples = 3;

    private static readonly OsduField Id = OsduField.Keyword("id");

    private readonly OsduSearch _search;
    private readonly ILogger _log;

    public DimensionLabeler(OsduSearch search, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(log);
        _search = search;
        _log = log;
    }

    /// <summary>The label of each of <paramref name="keys"/>, read through <paramref name="steps"/>.</summary>
    public async Task<KeyLabels> LabelAsync(IReadOnlyCollection<string> keys, IReadOnlyList<string> steps, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0 || keys.Count == 0)
        {
            return KeyLabels.None;
        }

        // Where each key has got to: the reference the next step reads, or why it stopped.
        var reached = new Dictionary<string, (string? Reference, string? Problem)>(keys.Count, StringComparer.Ordinal);
        foreach (var key in keys)
        {
            reached[key] = TargetId.IsRecordReference(key.Trim())
                ? (key.Trim(), null)
                : (null, "it names no OSDU record");
        }

        var queries = 0;
        var labels = new Dictionary<string, KeyLabel>(keys.Count, StringComparer.Ordinal);
        for (var step = 0; step < steps.Count; step++)
        {
            var last = step == steps.Count - 1;
            var path = steps[step];
            var references = reached.Values.Where(r => r.Reference is not null).Select(r => TargetId.WithoutVersion(r.Reference!)).Distinct(StringComparer.Ordinal).ToList();
            var (records, asked) = await ReadAsync(references, path, ct).ConfigureAwait(false);
            queries += asked;
            foreach (var key in reached.Keys.ToList())
            {
                var (reference, problem) = reached[key];
                if (reference is null)
                {
                    labels[key] = new KeyLabel(null, null, problem);
                    reached.Remove(key);
                    continue;
                }

                var id = TargetId.WithoutVersion(reference);
                if (!records.TryGetValue(id, out var values))
                {
                    labels[key] = new KeyLabel(null, null, $"the search holds no record {id}");
                    reached.Remove(key);
                    continue;
                }

                if (last)
                {
                    var text = values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
                    labels[key] = text is null
                        ? new KeyLabel(null, id, $"record {id} holds nothing at {path}")
                        : new KeyLabel(text.Length > MaxLabelLength ? text[..MaxLabelLength] : text, id, null);
                    reached.Remove(key);
                    continue;
                }

                var next = values.Select(v => v.Trim()).FirstOrDefault(TargetId.IsRecordReference);
                reached[key] = next is null ? (null, $"record {id} holds no record reference at {path}") : (next, null);
            }
        }

        var unlabelled = labels.Where(l => l.Value.Label is null).ToList();
        var notes = new List<string>();
        foreach (var why in unlabelled.GroupBy(l => Reason(l.Value.Problem!)).OrderByDescending(g => g.Count()))
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"{why.Count()} key(s) have no label, since {why.Key}, so each is its own value: {string.Join(", ", why.Take(Examples).Select(l => $"'{Shown(l.Key)}'"))}{(why.Count() > Examples ? ", ..." : string.Empty)}."));
        }

        _log.LogInformation(
            "labels: {Labelled} of {Keys} key(s) labelled through {Steps} in {Queries} search(es)",
            keys.Count - unlabelled.Count, keys.Count, string.Join(" > ", steps), queries);
        return new KeyLabels(labels, keys.Count - unlabelled.Count, unlabelled.Count, queries, notes);
    }

    /// <summary>
    /// The values <paramref name="path"/> holds in each record of <paramref name="ids"/> the search finds, by id: the ids
    /// grouped by the entity type their id names and searched in that type's kind, a chunk at a time.
    /// </summary>
    private async Task<(Dictionary<string, IReadOnlyList<string>> Records, int Queries)> ReadAsync(IReadOnlyList<string> ids, string path, CancellationToken ct)
    {
        var records = new Dictionary<string, IReadOnlyList<string>>(ids.Count, StringComparer.Ordinal);
        var queries = 0;
        var segments = path.Split('.');
        foreach (var group in ids.GroupBy(EntityTypeOf, StringComparer.Ordinal))
        {
            foreach (var chunk in group.Chunk(IdsPerQuery))
            {
                ct.ThrowIfCancellationRequested();
                var query = new OsduSearchQuery
                {
                    Kind = $"*:*:{group.Key}:*",
                    Query = OsduQuery.AnyOf(Id, chunk).Text,
                    ReturnedFields = ["id", path],
                };
                var (_, hits) = await _search.FirstAsync(query, OsduSearch.MaxPage, ct).ConfigureAwait(false);
                queries++;
                foreach (var hit in hits)
                {
                    if (hit["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var id) && !string.IsNullOrEmpty(id))
                    {
                        var values = new List<string>();
                        Collect(hit, segments, 0, values);
                        records[id] = values;
                    }
                }
            }
        }

        return (records, queries);
    }

    /// <summary>Every scalar the path reaches in <paramref name="node"/>, an array on the way stepped into, as text.</summary>
    internal static void Collect(JsonNode? node, string[] path, int at, List<string> into)
    {
        switch (node)
        {
            case null:
                return;
            case JsonArray array:
                foreach (var item in array)
                {
                    Collect(item, path, at, into);
                }

                return;
            case JsonObject obj when at < path.Length:
                Collect(obj.TryGetPropertyValue(path[at], out var child) ? child : null, path, at + 1, into);
                return;
            case JsonValue value when at == path.Length:
                var text = value.TryGetValue<string>(out var s) ? s : value.ToJsonString();
                if (!string.IsNullOrEmpty(text))
                {
                    into.Add(text);
                }

                return;
            default:
                return;
        }
    }

    /// <summary>The entity type an id names: its second segment (<c>master-data--Wellbore</c>).</summary>
    private static string EntityTypeOf(string id) => id.Split(':')[1];

    /// <summary>A problem without the record it names, so keys stopped for the same reason are counted together.</summary>
    private static string Reason(string problem)
        => problem.StartsWith("the search holds no record", StringComparison.Ordinal) ? "the search holds no record they name"
            : problem.StartsWith("record ", StringComparison.Ordinal) && problem.Contains(" holds nothing at ", StringComparison.Ordinal)
                ? "the record they name holds nothing at the label's path"
            : problem.StartsWith("record ", StringComparison.Ordinal) ? "the record they name holds no reference where the label reads one"
            : problem;

    private static string Shown(string value) => value.Length > 60 ? value[..60] + "..." : value;
}
