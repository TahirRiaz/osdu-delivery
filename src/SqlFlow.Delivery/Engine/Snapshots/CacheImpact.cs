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
        ReferenceType? previous, ReferenceType current, CacheChangeMode mode, string? fromVersion, string toVersion, CancellationToken ct = default)
    {
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

        var holders = await _ledger.FindCacheSetsAsync(current.Name, moved, ct).ConfigureAwait(false);
        if (holders.Count == 0)
        {
            _logger.LogInformation(
                "Cache {Type}: {Moved} item(s) changed in {Version}, none of them held by a set any delivered record was built from.",
                current.Name, moved.Count, toVersion);
            return new CacheImpactResult(current.Name, moved.Count, 0, 0, 0);
        }

        // One tag per changed value, whatever the number of sets or records behind it: an operator decides about a
        // corrected unit once, not once per record.
        var tags = new List<UpdateTag>();
        long records = 0;
        foreach (var change in holders.GroupBy(h => (h.ItemId, h.Path, h.Kind)))
        {
            var sample = change.First();
            if (Describe(sample, after.GetValueOrDefault(sample.ItemId), current) is not { } outcome)
            {
                continue;
            }

            var sets = change.Select(h => h.SetId).Distinct().ToList();
            var affected = await _ledger.CountRecordsInSetsAsync(sets, ct).ConfigureAwait(false);
            if (affected == 0)
            {
                // The set exists but nothing is delivered under it any more; nothing to update.
                continue;
            }

            records += affected;
            tags.Add(new UpdateTag
            {
                TypeName = sample.TypeName,
                ItemId = sample.ItemId,
                Path = sample.Path,
                Change = outcome.Change,
                OldValue = sample.ValueText,
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
            "Cache {Type}: {Moved} item(s) changed in {Version}; {Tags} change(s) reach {Records} delivered record(s) ({Mode}), {Written} new tag(s).",
            current.Name, moved.Count, toVersion, tags.Count, records, mode.ToString().ToLowerInvariant(), written);
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
