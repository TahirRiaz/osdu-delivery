using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Engine.Snapshots;

/// <summary>
/// What a new cache version does to what has already been delivered. Every delivered manifest row points at the
/// set of cached values it was built from, so a refresh answers "which records must be updated in OSDU" by
/// comparing those values against what the new version holds: the comparison runs over the sets, which number in
/// the thousands, never over the records, which number in the billions. Each change becomes one tag naming the
/// cached record, the path, the value before and after, and how many delivered records it reaches. The type's
/// <c>onChange</c> setting decides whether the tag waits for approval or rolls out on its own.
/// </summary>
public sealed class CacheImpactAnalyzer
{
    private readonly ILedger _ledger;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public CacheImpactAnalyzer(ILedger ledger, TimeProvider time, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _ledger = ledger;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Compares one refreshed type against the version it replaces and tags what the difference touches. A cached
    /// value matters when a record wrote it into its document, when the cached record it used is gone, when the
    /// value it matched by no longer resolves, or when a path it read and found empty now gives a value; a value
    /// nothing read changes nothing.
    /// </summary>
    public async Task<CacheImpactResult> AnalyzeAsync(
        string scope, ReferenceType? previous, ReferenceType current, CacheChangeMode mode, string? fromVersion, string toVersion, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentException.ThrowIfNullOrWhiteSpace(toVersion);
        if (previous is null)
        {
            // Nothing was cached under this type before, so nothing was built from it.
            return new CacheImpactResult(current.Name, 0, 0, 0, 0);
        }

        var before = previous.Items.ToDictionary(i => i.Id, i => i, StringComparer.Ordinal);
        var after = current.Items.ToDictionary(i => i.Id, i => i, StringComparer.Ordinal);

        // Only the items that actually moved are worth asking the ledger about, and, for a lookup table, the keys it lists
        // now and did not before: a record that looked one of them up found no row, and was built without it. For a type
        // of OSDU records, the records it holds now and did not before: a record that wrote an id of one of them as an
        // unverified reference was built without finding it. And for a type of OSDU records, every value an item that
        // arrived or moved holds, before and after: a record that found no item by one of them, or read every item keyed by
        // one, may find another now.
        var moved = before.Keys.Where(id => !after.ContainsKey(id) || Differs(previous, before[id], current, after[id])).ToList();
        var added = after.Keys.Where(id => !before.ContainsKey(id)).ToList();
        var listed = current.IsLookup ? added.Select(CacheUsage.ListingKey).Distinct(StringComparer.Ordinal).ToList() : [];
        var found = current.IsLookup ? [] : added;
        var keyed = current.IsLookup ? [] : Keys(moved.Select(id => before[id]).Concat(moved.Where(after.ContainsKey).Concat(added).Select(id => after[id])));
        if (moved.Count == 0 && listed.Count == 0 && found.Count == 0)
        {
            return new CacheImpactResult(current.Name, 0, 0, 0, 0);
        }

        // A key a record found no row under is held folded, so it is asked for by the added keys alone, and a moved item
        // answers only for the rows a record read.
        var holders = new List<CacheSetUse>();
        if (moved.Count > 0)
        {
            holders.AddRange((await _ledger.FindCacheSetsAsync(scope, current.Name, moved, ct).ConfigureAwait(false))
                .Where(use => use.Kind is not (CacheUsageKind.Unlisted or CacheUsageKind.Unverified or CacheUsageKind.Listed)));
        }

        if (listed.Count > 0)
        {
            holders.AddRange((await _ledger.FindCacheSetsAsync(scope, current.Name, listed, ct).ConfigureAwait(false))
                .Where(use => use.Kind == CacheUsageKind.Unlisted));
        }

        if (found.Count > 0)
        {
            holders.AddRange((await _ledger.FindCacheSetsAsync(scope, current.Name, found, ct).ConfigureAwait(false))
                .Where(use => use.Kind == CacheUsageKind.Unverified));
        }

        if (keyed.Count > 0)
        {
            holders.AddRange((await _ledger.FindCacheSetsAsync(scope, current.Name, keyed, ct).ConfigureAwait(false))
                .Where(use => use.Kind is CacheUsageKind.Unlisted or CacheUsageKind.Listed));
        }

        var changedItems = moved.Count + listed.Count + found.Count;
        if (holders.Count == 0)
        {
            _logger.LogInformation(
                "Cache of partition {Scope} type {Type}: {Moved} item(s) changed in {Version}, none of them held by a set any delivered record was built from.",
                scope, current.Name, changedItems, toVersion);
            return new CacheImpactResult(current.Name, changedItems, 0, 0, 0);
        }

        // One tag per changed value, whatever the number of sets or records behind it: an operator decides about a
        // corrected unit once, not once per record. Each set is judged by the value it holds, because sets built against
        // different versions of the cache hold different values of the same path: a set already holding the new value is
        // not touched, and one still holding an older value is, whichever of them the lookup lists first. And a set is
        // judged only where this refresh moved what it reads: a cached record is asked about as a whole when any of its
        // values moved, so a path of it that reads in the new version exactly as in the version before changed nothing
        // here, whatever the set holds. That keeps a change an earlier refresh raised, and someone rejected, from being
        // raised again because another value of the same record moved, and a path that held nothing then and holds
        // nothing now from being told as a change.
        var tags = new List<UpdateTag>();
        long records = 0;
        var touched = holders
            .Select(use => (Use: use, Outcome: Describe(use, after.GetValueOrDefault(use.ItemId), current)))
            .Where(held => held.Outcome is not null && Moved(held.Use, previous, before, current, after))
            .GroupBy(held => (held.Use.ItemId, held.Use.Path, held.Use.Kind));
        foreach (var change in touched)
        {
            // The outcome depends on the item, the path and the kind alone, so every set in the group shares it.
            var sample = change.First().Use;
            var outcome = change.First().Outcome!.Value;
            var sets = change.Select(held => held.Use.SetId).Distinct().ToList();
            var affected = await _ledger.CountRecordsInSetsAsync(sets, ct).ConfigureAwait(false);
            if (affected == 0)
            {
                // The set exists but nothing is delivered under it any more; nothing to update.
                continue;
            }

            records += affected;
            tags.Add(new UpdateTag
            {
                Scope = scope,
                TypeName = sample.TypeName,
                ItemId = outcome.ItemId,
                Path = sample.Path,
                Change = outcome.Change,
                OldValue = OldValue(sample, change.Select(held => held.Use), previous, before),
                NewValue = outcome.NewValue,
                FromVersion = fromVersion,
                ToVersion = toVersion,
                Mode = ModeText(mode),
                SetIds = sets,
                AffectedRecords = affected,
            });
        }

        var written = await _ledger.TagUpdatesAsync(tags, _time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Cache of partition {Scope} type {Type}: {Moved} item(s) changed in {Version}; {Tags} change(s) reach {Records} delivered record(s) ({Mode}), {Written} new tag(s).",
            scope, current.Name, changedItems, toVersion, tags.Count, records, mode.ToString().ToLowerInvariant(), written);
        return new CacheImpactResult(current.Name, changedItems, tags.Count, records, written);
    }

    /// <summary>
    /// What the new version does to one cached value a set holds, and the cached record the change is told of, or null when
    /// it still reads the same.
    /// </summary>
    private static (string Change, string? NewValue, string ItemId)? Describe(CacheSetUse use, ReferenceItem? item, ReferenceType current)
    {
        if (use.Kind == CacheUsageKind.Listed)
        {
            // The rows a $findAll read under a key: the record changes when the rows the key finds now are not the ones it
            // was built from, a row listed or unlisted under it (the access group a data office adds for a field).
            var ids = CacheUsage.ListedIds(current.FindAll(use.Path, use.ItemId).Select(row => row.Id));
            return string.Equals(Hashing.ContentHash.Of(ids), use.ValueHash, StringComparison.Ordinal) ? null : ("relisted", ids, use.ItemId);
        }

        if (use.Kind == CacheUsageKind.Unlisted && !current.IsLookup)
        {
            // A value no record of the type answered to: one does now when the value finds exactly one, or several, which
            // holds the record and so changes it as surely.
            var answered = current.Find(use.Path, use.ValueText);
            if (answered.Item is { } record)
            {
                return ("listed", current.Value(record, use.Path)?.Text, record.Id);
            }

            return answered.IsCaseAmbiguous ? ("listed", null, answered.CaseVariants[0].Id) : null;
        }

        if (use.Kind == CacheUsageKind.Unlisted)
        {
            // A key no row was listed under: the table lists it now when a render would find a row for it. What that row
            // gives, if anything, is what the record would carry; several rows answering to it would hold the record,
            // which changes it as surely.
            if (current.Key is null)
            {
                return null;
            }

            var found = current.Find(current.Key, use.ValueText);
            if (found.Item is { } row)
            {
                return ("listed", current.Value(row, use.Path)?.Text, row.Id);
            }

            return found.IsCaseAmbiguous ? ("listed", null, found.CaseVariants[0].Id) : null;
        }

        if (use.Kind == CacheUsageKind.Unverified)
        {
            // An id written without its record: the type holds the record now, so the reference the record carries names
            // a record the partition holds, and the record is built again against it.
            return item is null ? null : ("found", use.ValueText, item.Id);
        }

        if (use.Kind == CacheUsageKind.Empty)
        {
            // The document was built without a value here: it changes only when the path now gives one. A cached record
            // that is gone is told by the match that found it, which every such read sits beside.
            return item is not null && current.Value(item, use.Path) is { } given ? ("changed", given.Text, use.ItemId) : null;
        }

        if (item is null)
        {
            return ("removed", null, use.ItemId);
        }

        // A render that wrote a record's id, or found the record by it, recorded that id: it reads the same as long as the
        // record is there, whatever the type caches under a field of its own called ID, which would otherwise be read here.
        if (ReferenceField.IsId(use.Path) && CachedReferences.Parse(use.ValueText) is { } named && string.Equals(named.Id, use.ItemId, StringComparison.Ordinal))
        {
            return null;
        }

        var value = current.Value(item, use.Path);
        if (value is null)
        {
            return (use.Kind == CacheUsageKind.Match ? "unmatched" : "removed", null, use.ItemId);
        }

        if (use.Kind == CacheUsageKind.Match)
        {
            // A match still holds as long as the value the source resolved by is still one of this item's.
            return value.Terms.Contains(use.ValueText, StringComparer.OrdinalIgnoreCase) ? null : ("unmatched", value.Text, use.ItemId);
        }

        return Hashing.ContentHash.Of(value.Text) == use.ValueHash || string.Equals(value.Text, use.ValueText, StringComparison.Ordinal)
            ? null
            : ("changed", value.Text, use.ItemId);
    }

    /// <summary>
    /// The value a change moved away from. For a written value, what the version being replaced held, so a tag reads from
    /// its <c>FromVersion</c> to its <c>ToVersion</c> even while some of its sets were built against an older version; a
    /// path that version did not hold falls back to the value of the most recently built set. For a match, the terms the
    /// sources resolved by that no longer resolve, and for keys a table now lists, the keys as the sources looked them up.
    /// For a path that gave no value, none.
    /// </summary>
    private static string? OldValue(CacheSetUse sample, IEnumerable<CacheSetUse> uses, ReferenceType previous, Dictionary<string, ReferenceItem> before)
    {
        if (sample.Kind == CacheUsageKind.Empty)
        {
            return null;
        }

        if (sample.Kind == CacheUsageKind.Listed)
        {
            // The rows the key found when the most recently built of the sets was.
            return uses.MaxBy(u => u.SetId)!.ValueText;
        }

        if (sample.Kind is CacheUsageKind.Match or CacheUsageKind.Unlisted or CacheUsageKind.Unverified)
        {
            return string.Join(", ", uses.Select(u => u.ValueText).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase));
        }

        return before.TryGetValue(sample.ItemId, out var item) && previous.Value(item, sample.Path) is { } held
            ? held.Text
            : uses.MaxBy(u => u.SetId)!.ValueText;
    }

    /// <summary>
    /// Whether the refresh moved what <paramref name="use"/> reads: the answer the version before gives it differs from the
    /// one the new version gives. The answer is what a render would take for the use from a version: for a value read out of
    /// a cached record, matched by, or read empty, whether the record is there and what the path holds; for a record written
    /// unverified, whether the version holds it; for a key or a value no record answered to, which record answers to it now
    /// and what it gives; for a <c>$findAll</c>, the rows the key finds.
    /// </summary>
    private static bool Moved(
        CacheSetUse use, ReferenceType previous, IReadOnlyDictionary<string, ReferenceItem> before, ReferenceType current, IReadOnlyDictionary<string, ReferenceItem> after)
        => Reading(use, previous, before) != Reading(use, current, after);

    /// <summary>What <paramref name="use"/> reads from one version of its type, as <see cref="Moved"/> compares it.</summary>
    private static Answer Reading(CacheSetUse use, ReferenceType type, IReadOnlyDictionary<string, ReferenceItem> items)
    {
        switch (use.Kind)
        {
            case CacheUsageKind.Listed:
                return new Answer(true, null, CacheUsage.ListedIds(type.FindAll(use.Path, use.ItemId).Select(row => row.Id)));

            case CacheUsageKind.Unlisted when !type.IsLookup:
                return Found(type, type.Find(use.Path, use.ValueText), use.Path);

            case CacheUsageKind.Unlisted:
                return type.Key is null ? default : Found(type, type.Find(type.Key, use.ValueText), use.Path);

            case CacheUsageKind.Unverified:
                return new Answer(items.ContainsKey(use.ItemId), null, null);

            default:
                return items.TryGetValue(use.ItemId, out var item) ? new Answer(true, item.Id, type.Value(item, use.Path)?.Text) : default;
        }

        static Answer Found(ReferenceType type, ReferenceMatch match, string path)
        {
            if (match.Item is { } item)
            {
                return new Answer(true, item.Id, type.Value(item, path)?.Text);
            }

            // Several records answering is an answer too: the render is held on it, and which ones answer can move.
            return match.IsCaseAmbiguous ? new Answer(true, null, string.Join('\n', match.CaseVariants.Select(v => v.Id))) : default;
        }
    }

    /// <summary>What a use reads from one version: whether anything answers it, which record, and the text it gives.</summary>
    private readonly record struct Answer(bool Holds, string? Id, string? Text);

    /// <summary>True when any cached path of the item reads differently in the new version.</summary>
    private static bool Differs(ReferenceType previous, ReferenceItem before, ReferenceType current, ReferenceItem after)
    {
        var paths = before.Fields.Keys.Union(after.Fields.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var oldValue = previous.Value(before, path)?.Text;
            var newValue = current.Value(after, path)?.Text;
            if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Every value the items hold under any of their fields, folded as a key is kept: what a record that found none of them
    /// by a value, or read every item keyed by one, recorded, so the sets to judge again are found by the keys alone. A value
    /// that is an OSDU reference is also asked for without its version separator and with it, as a render looks it up.
    /// </summary>
    private static List<string> Keys(IEnumerable<ReferenceItem> items)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            foreach (var value in item.Fields.Values)
            {
                foreach (var term in value.Terms)
                {
                    keys.Add(CacheUsage.ListingKey(term));
                    if (CachedReferences.Parse(term) is { } reference)
                    {
                        keys.Add(CacheUsage.ListingKey(reference.Id));
                        keys.Add(CacheUsage.ListingKey(reference.Id + ":"));
                    }
                }
            }
        }

        return [.. keys];
    }

    private static string ModeText(CacheChangeMode mode) => mode == CacheChangeMode.Auto ? "auto" : "approve";
}

/// <summary>What a refresh of one cached type did to what has already been delivered.</summary>
public sealed record CacheImpactResult(string TypeName, int ChangedItems, int Changes, long AffectedRecords, int NewTags);
