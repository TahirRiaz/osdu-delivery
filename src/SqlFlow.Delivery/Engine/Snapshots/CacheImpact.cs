using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Ledger;
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
    /// value matters when a record wrote it into its document, when the cached record it used is gone, or when the
    /// value it matched by no longer resolves; a value nothing read changes nothing.
    /// </summary>
    public async Task<CacheImpactResult> AnalyzeAsync(
        string cacheName, ReferenceType? previous, ReferenceType current, CacheChangeMode mode, string? fromVersion, string toVersion, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheName);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentException.ThrowIfNullOrWhiteSpace(toVersion);
        if (previous is null)
        {
            // Nothing was cached under this type before, so nothing was built from it.
            return new CacheImpactResult(current.Name, 0, 0, 0, 0);
        }

        var before = previous.Items.ToDictionary(i => i.Id, i => i, StringComparer.Ordinal);
        var after = current.Items.ToDictionary(i => i.Id, i => i, StringComparer.Ordinal);

        // Only the items that actually moved are worth asking the ledger about.
        var moved = before.Keys.Where(id => !after.ContainsKey(id) || Differs(previous, before[id], current, after[id])).ToList();
        if (moved.Count == 0)
        {
            return new CacheImpactResult(current.Name, 0, 0, 0, 0);
        }

        var holders = await _ledger.FindCacheSetsAsync(cacheName, current.Name, moved, ct).ConfigureAwait(false);
        if (holders.Count == 0)
        {
            _logger.LogInformation(
                "Cache {Cache} type {Type}: {Moved} item(s) changed in {Version}, none of them held by a set any delivered record was built from.",
                cacheName, current.Name, moved.Count, toVersion);
            return new CacheImpactResult(current.Name, moved.Count, 0, 0, 0);
        }

        // One tag per changed value, whatever the number of sets or records behind it: an operator decides about a
        // corrected unit once, not once per record. Each set is judged by the value it holds, because sets built against
        // different versions of the cache hold different values of the same path: a set already holding the new value is
        // not touched, and one still holding an older value is, whichever of them the lookup lists first.
        var tags = new List<UpdateTag>();
        long records = 0;
        var touched = holders
            .Select(use => (Use: use, Outcome: Describe(use, after.GetValueOrDefault(use.ItemId), current)))
            .Where(held => held.Outcome is not null)
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
                CacheName = cacheName,
                TypeName = sample.TypeName,
                ItemId = sample.ItemId,
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
            "Cache {Cache} type {Type}: {Moved} item(s) changed in {Version}; {Tags} change(s) reach {Records} delivered record(s) ({Mode}), {Written} new tag(s).",
            cacheName, current.Name, moved.Count, toVersion, tags.Count, records, mode.ToString().ToLowerInvariant(), written);
        return new CacheImpactResult(current.Name, moved.Count, tags.Count, records, written);
    }

    /// <summary>What the new version does to one cached value a set holds, or null when it still reads the same.</summary>
    private static (string Change, string? NewValue)? Describe(CacheSetUse use, ReferenceItem? item, ReferenceType current)
    {
        if (item is null)
        {
            return ("removed", null);
        }

        var value = current.Value(item, use.Path);
        if (value is null)
        {
            return (use.Kind == CacheUsageKind.Match ? "unmatched" : "removed", null);
        }

        if (use.Kind == CacheUsageKind.Match)
        {
            // A match still holds as long as the value the source resolved by is still one of this item's.
            return value.Terms.Contains(use.ValueText, StringComparer.OrdinalIgnoreCase) ? null : ("unmatched", value.Text);
        }

        return Hashing.ContentHash.Of(value.Text) == use.ValueHash || string.Equals(value.Text, use.ValueText, StringComparison.Ordinal)
            ? null
            : ("changed", value.Text);
    }

    /// <summary>
    /// The value a change moved away from. For a written value, what the version being replaced held, so a tag reads from
    /// its <c>FromVersion</c> to its <c>ToVersion</c> even while some of its sets were built against an older version; a
    /// path that version did not hold falls back to the value of the most recently built set. For a match, the terms the
    /// sources resolved by that no longer resolve.
    /// </summary>
    private static string OldValue(CacheSetUse sample, IEnumerable<CacheSetUse> uses, ReferenceType previous, Dictionary<string, ReferenceItem> before)
    {
        if (sample.Kind == CacheUsageKind.Match)
        {
            return string.Join(", ", uses.Select(u => u.ValueText).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase));
        }

        return before.TryGetValue(sample.ItemId, out var item) && previous.Value(item, sample.Path) is { } held
            ? held.Text
            : uses.MaxBy(u => u.SetId)!.ValueText;
    }

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

    private static string ModeText(CacheChangeMode mode) => mode == CacheChangeMode.Auto ? "auto" : "approve";
}

/// <summary>What a refresh of one cached type did to what has already been delivered.</summary>
public sealed record CacheImpactResult(string TypeName, int ChangedItems, int Changes, long AffectedRecords, int NewTags);
