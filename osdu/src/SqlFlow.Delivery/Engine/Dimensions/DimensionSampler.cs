using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;

namespace SqlFlow.Delivery.Engine.Dimensions;

/// <summary>How the platform indexes a path, as the builder carries it between the control plane and the read: the path, its index's type, and the nested array it sits in.</summary>
/// <param name="Path">The path from the record's root.</param>
/// <param name="Index">text, keyword, number, boolean or date.</param>
/// <param name="NestedPath">The nested array the path sits in; null for none.</param>
public sealed record DimensionFieldWire(string Path, string Index, string? NestedPath)
{
    /// <summary>The field, or null for a wire whose index is none the platform keeps.</summary>
    public OsduField? Field() => Index switch
    {
        "text" => OsduField.Text(Path, NestedPath),
        "keyword" => OsduField.Keyword(Path, NestedPath),
        "number" => OsduField.Number(Path, NestedPath),
        "boolean" => OsduField.Boolean(Path, NestedPath),
        "date" => OsduField.Date(Path, NestedPath),
        _ => null,
    };

    public static DimensionFieldWire Of(OsduField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return new DimensionFieldWire(field.Path, DimensionRunner.Index(field.Index), field.NestedPath);
    }
}

/// <summary>
/// What the builder asks to show a person the records a dimension reads: the kind and the query narrowing it, the key's
/// path and how it is indexed once one is picked, the example key, which record of the kind (or of those holding the key) to
/// show, and the trails to follow from the record the key names, each the paths that name the records read next.
/// </summary>
public sealed record DimensionSampleRequest
{
    public required string Kind { get; init; }

    /// <summary>The query narrowing the records, its tokens filled; null for every record of the kind.</summary>
    public string? Query { get; init; }

    public string? Path { get; init; }

    public DimensionFieldWire? KeyField { get; init; }

    /// <summary>
    /// Whether <see cref="KeyField"/> is a guess, made because no saved template says how the path is indexed: text, which most
    /// properties of data are. A guess is said in the sample's notes, since a build refuses such a path until a template is saved.
    /// </summary>
    public bool KeyFieldGuessed { get; init; }

    public string? Key { get; init; }

    /// <summary>Which record to show, from 0, of the kind's records or of those holding the key.</summary>
    public int At { get; init; }

    public IReadOnlyList<IReadOnlyList<string>> Trails { get; init; } = [];
}

/// <summary>A part of a record left out of what a page is shown: the location of an array or a text, how much it holds, and how much is shown.</summary>
/// <param name="Path">The location, its segments joined by dots and an array's items by their place (<c>data.Curves</c>, <c>data.Curves.3.Name</c>).</param>
/// <param name="Held">The items the array holds, or the characters the text holds.</param>
/// <param name="Shown">The items, or characters, shown.</param>
public sealed record DimensionSampleCut(string Path, int Held, int Shown);

/// <summary>A record as the search holds it, which is what a build reads: its id, its kind, the record itself (cut where it is long), and where it was cut.</summary>
public sealed record DimensionSampleRecord(string Id, string? Kind, JsonObject Record, IReadOnlyList<DimensionSampleCut> Cut);

/// <summary>A key of the dimension with how many of its records hold it, as the search counts them.</summary>
public sealed record DimensionSampleKey(string Key, long Count);

/// <summary>
/// The records a trail reaches from the record the key names: the paths followed, the ids the last of them named (at most as
/// many as a build follows), the records the search holds of them, and why the trail stopped short, when it did.
/// </summary>
public sealed record DimensionSampleTrail(IReadOnlyList<string> Steps, IReadOnlyList<string> Reached, IReadOnlyList<DimensionSampleRecord> Records, string? Problem);

/// <summary>
/// What the builder shows of the records a dimension reads: how many records the kind (and the key) has, the record shown and
/// its place, the commonest keys, whether there are more, how the key was read (and whether that was a guess), the records
/// each trail reaches, what the reads had to say, and the search service's own words when it refused the query.
/// </summary>
public sealed record DimensionSample(
    long Total, int At, DimensionSampleRecord? Record, IReadOnlyList<DimensionSampleKey>? Keys, bool MoreKeys, DimensionFieldWire? KeyField,
    bool KeyFieldGuessed, IReadOnlyList<DimensionSampleTrail> Trails, IReadOnlyList<string> Notes, string? Refusal);

/// <summary>
/// One key of a draft as a build would make it: the key, its label and the record it came from, why it has none, its value
/// (or why it is left out of every value), its attributes, the records holding it and the filter finding them, and what
/// the reads had to say.
/// </summary>
public sealed record DimensionExample(
    string Key, string? Label, string? LabelFrom, string? Problem, string? Value, string? LeftOut, string? Note,
    IReadOnlyList<DimensionAttributeState> Attributes, long? Records, string? Filter, IReadOnlyList<string> Notes);

/// <summary>
/// The reads the explorer's dimension builder makes of OSDU (osdu/docs/explorer.md, Building a dimension): the records of a
/// kind as the search holds them, which is what a build reads; the commonest keys of a path; the records a key names and
/// those they name in turn, so a person can pick what to read from them; and one key made into its row exactly as a build
/// makes it, through the build's own labeler, collector display, cleaner and attribute assembly. Nothing is written.
/// </summary>
public sealed class DimensionSampler
{
    /// <summary>The keys offered as examples: the commonest, as a dimension's own pages offer theirs.</summary>
    public const int ExampleKeys = 25;

    /// <summary>The records of a kind a person steps through before a key is picked.</summary>
    public const int ExampleRecords = 25;

    /// <summary>The trails one sample follows: every chain prefix of a dimension and a few a person opened besides.</summary>
    public const int MaxTrails = 16;

    /// <summary>The paths one trail follows: the records a chain reads all but its last path in.</summary>
    public const int MaxTrailSteps = DimensionSpec.MaxLabelSteps - 1;

    /// <summary>The items of an array a record is shown with.</summary>
    public const int ShownItems = 50;

    /// <summary>The characters of a text a record is shown with.</summary>
    public const int ShownText = 2_000;

    /// <summary>The records the trails of one sample show at most, so an answer stays a page's size.</summary>
    public const int MaxTrailRecords = 80;

    private readonly OsduSearch _search;
    private readonly ILogger _log;

    public DimensionSampler(OsduSearch search, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(log);
        _search = search;
        _log = log;
    }

    /// <summary>The records, keys and trails <paramref name="request"/> asks for.</summary>
    public async Task<DimensionSample> SampleAsync(DimensionSampleRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.At is < 0 or >= ExampleRecords)
        {
            throw new ArgumentOutOfRangeException(nameof(request), $"A sample shows one of the first {ExampleRecords} records, from 0.");
        }

        var notes = new List<string>();
        var keyField = request.KeyField?.Field();
        var key = string.IsNullOrEmpty(request.Key) ? null : request.Key;
        if (keyField is not null && request.KeyFieldGuessed)
        {
            notes.Add($"No saved template of {request.Kind} says how {keyField.Path} is indexed, so it is read here as text, which most properties are. A build reads it only once the template is saved.");
        }
        string? keyFilter = null;
        if (key is not null && keyField is not null)
        {
            if (DimensionFilters.Filterable(keyField, key))
            {
                keyFilter = DimensionFilters.Of(keyField, [key])[0];
            }
            else
            {
                notes.Add("The key holds what no query can carry, so the record shown is one of the kind, not one known to hold the key.");
            }
        }

        var page = await _search.PageAsync(
            new OsduSearchQuery { Kind = request.Kind, Query = keyFilter is null ? request.Query : DimensionFilters.Within(request.Query, keyFilter) },
            request.At, 1, null, ct).ConfigureAwait(false);
        if (page.Refusal is { } refusal)
        {
            return new DimensionSample(0, request.At, null, null, false, request.KeyField, request.KeyFieldGuessed, [], notes, refusal);
        }

        var record = page.Hits.Count > 0 ? Shown(page.Hits[0]) : null;
        IReadOnlyList<DimensionSampleKey>? keys = null;
        var moreKeys = false;
        if (request.Path is not null && keyField is not null)
        {
            try
            {
                var (_, buckets) = await _search.AggregateAsync(new OsduSearchQuery { Kind = request.Kind, Query = request.Query }, keyField.AggregateBy, ct).ConfigureAwait(false);
                var held = buckets.Where(b => !string.IsNullOrEmpty(b.Key)).ToList();
                keys = held.Take(ExampleKeys).Select(b => new DimensionSampleKey(b.Key!, b.Count)).ToList();
                moreKeys = held.Count > ExampleKeys;
            }
            catch (OsduStatusException ex)
            {
                notes.Add($"The search service would not group the records by {request.Path} ({keyField.AggregateBy}): {ex.Message}");
            }
        }

        var trails = request.Trails.Count == 0 || key is null
            ? []
            : await TrailsAsync(key, request.Trails, notes, ct).ConfigureAwait(false);
        return new DimensionSample(page.Total, request.At, record, keys, moreKeys, request.KeyField, request.KeyFieldGuessed, trails, notes, null);
    }

    /// <summary>
    /// <paramref name="key"/> made into its row as a build makes it, for <paramref name="dimension"/> read with
    /// <paramref name="query"/> (its tokens filled): its label and attributes read through the record it names by the build's
    /// labeler, the values it collects from the records holding it (<paramref name="collected"/>, each attribute's field) shown
    /// as a build shows them, its value cleaned by <paramref name="cleaner"/> from what a build cleans it from, and the records
    /// holding it with the filter finding them, when <paramref name="keyField"/> says how the key is indexed.
    /// </summary>
    public async Task<DimensionExample> ExampleAsync(
        DimensionSpec dimension, string key, string? query, OsduField? keyField, IReadOnlyDictionary<string, OsduField> collected, DimensionCleaner cleaner,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dimension);
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(collected);
        ArgumentNullException.ThrowIfNull(cleaner);
        var notes = new List<string>();
        var labeler = new DimensionLabeler(_search, _log, concurrency: 4);
        var labels = await labeler.ReadAsync([key], dimension.Label, dimension.Attributes, ct).ConfigureAwait(false);
        notes.AddRange(labels.Notes);
        var labelled = labels.Labels.GetValueOrDefault(key);

        string? filter = null;
        long? records = null;
        var collects = new List<DimensionAttributeState>();
        if (keyField is null)
        {
            notes.Add("How the key is indexed is not known, so the records holding it are not counted and its collected values are not read.");
        }
        else if (!DimensionFilters.Filterable(keyField, key))
        {
            notes.Add("The key holds what no query can carry, so no filter finds its records: a build keeps it, marked unfilterable.");
        }
        else
        {
            filter = DimensionFilters.Of(keyField, [key])[0];
            var holding = DimensionFilters.Within(query, filter);
            records = await _search.CountAsync(new OsduSearchQuery { Kind = dimension.Kind, Query = holding }, ct).ConfigureAwait(false);
            foreach (var attribute in dimension.Attributes.Where(a => a.IsCollected))
            {
                if (!collected.TryGetValue(attribute.Name, out var field))
                {
                    notes.Add($"How {attribute.Collect} is indexed is not known, so the values {attribute.Name} collects are not read.");
                    continue;
                }

                collects.AddRange(await CollectAsync(dimension, attribute, holding, field, records ?? 0, notes, ct).ConfigureAwait(false));
            }
        }

        var attributes = DimensionRunner.KeyAttributes(
            dimension, [key], labels, new Dictionary<string, List<DimensionAttributeState>>(StringComparer.Ordinal) { [key] = collects });
        var cleaned = cleaner.Clean(DimensionRunner.ValueSourceOf(dimension, key, labelled));
        return new DimensionExample(
            key, labelled?.Label, labelled?.From, labelled?.Problem,
            cleaned.Outcome == CleanOutcome.Member ? cleaned.Value : null,
            cleaned.Outcome == CleanOutcome.Member ? null : cleaned.Note,
            cleaned.Outcome == CleanOutcome.Member ? cleaned.Note : null,
            attributes.GetValueOrDefault(key) ?? [], records, filter, notes);
    }

    /// <summary>
    /// The values <paramref name="attribute"/> collects from the records holding the key (<paramref name="holding"/>), each shown
    /// as a build shows it with how many of those records hold it; and, for a dimension naming the value of what is not
    /// read, that value for the records holding none of them, as a build counts them.
    /// </summary>
    private async Task<List<DimensionAttributeState>> CollectAsync(
        DimensionSpec dimension, DimensionAttributeSpec attribute, string holding, OsduField field, long records, List<string> notes, CancellationToken ct)
    {
        IReadOnlyList<OsduSearchBucket> buckets;
        try
        {
            (_, buckets) = await _search.AggregateAsync(new OsduSearchQuery { Kind = dimension.Kind, Query = holding }, field.AggregateBy, ct).ConfigureAwait(false);
        }
        catch (OsduStatusException ex)
        {
            notes.Add($"The search service would not group the key's records by {attribute.Collect}: {ex.Message}");
            return [];
        }

        // Each text as a build shows it; texts shown alike are one value, their records added up, as a build keeps them.
        var texts = new List<string>();
        var values = new Dictionary<string, DimensionAttributeState>(StringComparer.Ordinal);
        foreach (var bucket in buckets)
        {
            if (string.IsNullOrEmpty(bucket.Key) || !DimensionFilters.Filterable(field, bucket.Key))
            {
                continue;
            }

            texts.Add(bucket.Key);
            if (DimensionCollector.ShownAs(bucket.Key) is { } shown)
            {
                values[shown] = values.TryGetValue(shown, out var held)
                    ? held with { Records = held.Records + bucket.Count }
                    : new DimensionAttributeState(attribute.Name, shown, bucket.Key, bucket.Count);
            }
        }

        if (dimension.Unlabelled is { } none && records > 0)
        {
            // The records holding none of the texts take the dimension's value for what is not read, as a build counts them;
            // that is told only when the search named every text, which one answer does up to the platform's group size.
            if (buckets.Count < DimensionSource.DefaultAggregationSize)
            {
                var without = texts.Count == 0
                    ? records
                    : await _search.CountAsync(
                        new OsduSearchQuery { Kind = dimension.Kind, Query = DimensionFilters.Within(holding, DimensionFilters.NoneOf(field, texts)) }, ct).ConfigureAwait(false);
                if (without > 0)
                {
                    values[none] = values.TryGetValue(none, out var held)
                        ? held with { Records = held.Records + without }
                        : new DimensionAttributeState(attribute.Name, none, null, without);
                }
            }
            else
            {
                notes.Add(string.Create(CultureInfo.InvariantCulture,
                    $"The key's records hold {buckets.Count} or more values of {attribute.Collect}, so those holding none of them are not counted here."));
            }
        }

        return values.Values.OrderByDescending(v => v.Records).ThenBy(v => v.Value, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The records each trail reaches from the record <paramref name="key"/> names: each path of a trail read in the records the
    /// one before reached, the references it holds followed as a build follows them, and the records of the last read whole.
    /// Records are read once, whichever trails reach them.
    /// </summary>
    private async Task<List<DimensionSampleTrail>> TrailsAsync(string key, IReadOnlyList<IReadOnlyList<string>> trails, List<string> notes, CancellationToken ct)
    {
        var found = new List<DimensionSampleTrail>(trails.Count);
        if (DimensionLabeler.StartOf(key) is not { } start)
        {
            found.AddRange(trails.Select(t => new DimensionSampleTrail(t, [], [], $"The key {Clipped(key)} names no OSDU record, so no record is read through it: only a key holding a record id has a value or attributes read through it.")));
            return found;
        }

        var read = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var shown = 0;
        foreach (var trail in trails)
        {
            IReadOnlyList<string> ids = [start];
            string? problem = null;
            foreach (var step in trail)
            {
                var path = DimensionPath.Parse(step).Path
                    ?? throw new ArgumentException($"'{step}' is not a path a trail can follow: {DimensionPath.Parse(step).Problem}.", nameof(trails));
                await ReadAsync(ids, read, ct).ConfigureAwait(false);
                var held = ids.Where(read.ContainsKey).ToList();
                if (held.Count == 0)
                {
                    problem = $"The search holds no record {ids[0]}.";
                    ids = [];
                    break;
                }

                var next = DimensionLabeler.ReferencesAt(path, held.Select(id => read[id]));
                if (next.Count == 0)
                {
                    problem = held.Count == 1
                        ? $"The record reached holds no record id at {step}."
                        : string.Create(CultureInfo.InvariantCulture, $"None of the {held.Count} records reached holds a record id at {step}.");
                    ids = [];
                    break;
                }

                ids = next;
            }

            var records = new List<DimensionSampleRecord>();
            if (ids.Count > 0)
            {
                await ReadAsync(ids, read, ct).ConfigureAwait(false);
                foreach (var id in ids.Where(read.ContainsKey))
                {
                    if (shown >= MaxTrailRecords)
                    {
                        notes.Add(string.Create(CultureInfo.InvariantCulture, $"The trails reach more records than one page shows; the first {MaxTrailRecords} are shown."));
                        break;
                    }

                    records.Add(Shown(read[id]));
                    shown++;
                }

                if (records.Count == 0 && problem is null)
                {
                    problem = ids.Count == 1 ? $"The search holds no record {ids[0]}." : $"The search holds none of the {ids.Count} records reached.";
                }
            }

            found.Add(new DimensionSampleTrail(trail, ids, records, problem));
        }

        return found;
    }

    /// <summary>The records of <paramref name="ids"/> not read yet, read whole into <paramref name="read"/>: one search per entity type, as a build searches them.</summary>
    private async Task ReadAsync(IReadOnlyList<string> ids, Dictionary<string, JsonObject> read, CancellationToken ct)
    {
        foreach (var group in ids.Where(id => !read.ContainsKey(id)).Distinct(StringComparer.Ordinal).GroupBy(DimensionLabeler.EntityTypeOf, StringComparer.Ordinal))
        {
            foreach (var chunk in group.Chunk(DimensionLabeler.IdsPerQuery))
            {
                var (_, hits) = await _search.FirstAsync(
                    new OsduSearchQuery { Kind = DimensionLabeler.KindOfType(group.Key), Query = OsduQuery.AnyOf(DimensionLabeler.Id, chunk).Text },
                    chunk.Length, ct).ConfigureAwait(false);
                foreach (var hit in hits)
                {
                    if (hit["id"] is JsonValue id && id.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text))
                    {
                        read[text] = hit;
                    }
                }
            }
        }
    }

    /// <summary>A record as a page is shown it: a copy with every array cut at <see cref="ShownItems"/> items and every text at <see cref="ShownText"/> characters, saying where.</summary>
    internal static DimensionSampleRecord Shown(JsonObject record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var copy = (JsonObject)record.DeepClone();
        var cut = new List<DimensionSampleCut>();
        Cut(copy, string.Empty, cut);
        var id = copy["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var text) ? text : string.Empty;
        var kind = copy["kind"] is JsonValue kindValue && kindValue.TryGetValue<string>(out var kindText) ? kindText : null;
        return new DimensionSampleRecord(id, kind, copy, cut);
    }

    private static void Cut(JsonNode? node, string at, List<DimensionSampleCut> cut)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    var child = obj[name];
                    var path = at.Length == 0 ? name : $"{at}.{name}";
                    if (child is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > ShownText)
                    {
                        obj[name] = text[..ShownText];
                        cut.Add(new DimensionSampleCut(path, text.Length, ShownText));
                        continue;
                    }

                    Cut(child, path, cut);
                }

                break;
            case JsonArray array:
                if (array.Count > ShownItems)
                {
                    cut.Add(new DimensionSampleCut(at, array.Count, ShownItems));
                    while (array.Count > ShownItems)
                    {
                        array.RemoveAt(array.Count - 1);
                    }
                }

                for (var i = 0; i < array.Count; i++)
                {
                    var path = string.Create(CultureInfo.InvariantCulture, $"{at}.{i}");
                    if (array[i] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > ShownText)
                    {
                        array[i] = text[..ShownText];
                        cut.Add(new DimensionSampleCut(path, text.Length, ShownText));
                        continue;
                    }

                    Cut(array[i], path, cut);
                }

                break;
        }
    }

    private static string Clipped(string value) => value.Length > 80 ? value[..80] + "..." : value;
}
