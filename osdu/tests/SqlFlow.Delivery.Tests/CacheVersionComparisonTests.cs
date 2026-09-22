using System.Globalization;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Snapshots;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Reading a cache across its versions: which cached records changed between two versions (and in which captured values),
/// which arrived and which left, per type, a page at a time, and what each version changed against the one captured before
/// it. The versions are written through the store, so the ranges the reads cover are the ones a refresh writes.
/// </summary>
public sealed class CacheVersionComparisonTests : IDisposable
{
    private const string Scope = "dev";
    private const string First = "20260901T100000Z";
    private const string Earlier = "20260910T100000Z";
    private const string Later = "20260911T100000Z";

    private static readonly CacheCapture Capture = new(null, "tests", "seeded");

    private readonly SqliteOsdu _catalog = new();

    public void Dispose() => _catalog.Dispose();

    [Fact]
    public async Task A_comparison_names_what_changed_what_arrived_and_what_left()
    {
        await SeedEstateAsync();

        await using var db = _catalog.CreateDbContext();
        var diff = await CacheVersions.CompareAsync(db, new CacheComparisonQuery(Scope,Earlier));

        Assert.NotNull(diff);
        Assert.Equal(Later, diff.ToVersion);
        Assert.Equal((1L, 1L, 1L), (diff.Changed, diff.Added, diff.Removed));
        Assert.Equal(3, diff.Total);

        // The unchanged wellbore is not a difference, so its type is not listed at all.
        var type = Assert.Single(diff.Types);
        Assert.Equal(new CacheComparisonTypeCount("UnitOfMeasure", 1, 1, 1), type);

        // Changed first, then added, then removed, each by type and id.
        Assert.Collection(
            diff.Items,
            changed =>
            {
                Assert.Equal(CacheItemChange.Changed, changed.Change);
                Assert.Equal(UomId("m"), changed.RecordId);
                Assert.Equal(["Name"], changed.ChangedFields);
                Assert.Contains("\"metre\"", changed.BeforeJson, StringComparison.Ordinal);
                Assert.Contains("\"Metre\"", changed.AfterJson, StringComparison.Ordinal);
            },
            added =>
            {
                Assert.Equal(CacheItemChange.Added, added.Change);
                Assert.Equal(UomId("km"), added.RecordId);
                Assert.Null(added.BeforeJson);
                Assert.NotNull(added.AfterJson);
                Assert.Empty(added.ChangedFields);
            },
            removed =>
            {
                // The foot left while the femtotesla stayed: ids differing only by case are two records.
                Assert.Equal(CacheItemChange.Removed, removed.Change);
                Assert.Equal(UomId("ft"), removed.RecordId);
                Assert.NotNull(removed.BeforeJson);
                Assert.Null(removed.AfterJson);
            });
    }

    [Fact]
    public async Task Pages_span_the_kinds_of_change_and_filters_narrow_the_items()
    {
        await SeedEstateAsync();
        await using var db = _catalog.CreateDbContext();

        var second = await CacheVersions.CompareAsync(db, new CacheComparisonQuery(Scope,Earlier) { Skip = 1, Take = 1 });
        Assert.NotNull(second);
        Assert.Equal(3, second.Total);
        Assert.Equal(UomId("km"), Assert.Single(second.Items).RecordId);

        var third = await CacheVersions.CompareAsync(db, new CacheComparisonQuery(Scope,Earlier) { Skip = 2, Take = 5 });
        Assert.Equal(UomId("ft"), Assert.Single(third!.Items).RecordId);

        // A change filter narrows the items, while the per-type counts keep describing every kind of change.
        var removed = await CacheVersions.CompareAsync(db, new CacheComparisonQuery(Scope,Earlier) { Change = CacheItemChange.Removed });
        Assert.Equal(1, removed!.Total);
        Assert.Equal(UomId("ft"), Assert.Single(removed.Items).RecordId);
        Assert.Equal((1L, 1L, 1L), (removed.Changed, removed.Added, removed.Removed));

        var wellbores = await CacheVersions.CompareAsync(db, new CacheComparisonQuery(Scope,Earlier) { Type = "Wellbore" });
        Assert.Equal(0, wellbores!.Total);
        Assert.Empty(wellbores.Types);

        // Search matches a value either side holds, not only the id.
        var kilo = await CacheVersions.CompareAsync(db, new CacheComparisonQuery(Scope,Earlier) { Search = "kilo" });
        Assert.Equal(UomId("km"), Assert.Single(kilo!.Items).RecordId);
    }

    [Fact]
    public async Task The_later_side_defaults_to_the_current_version_and_a_version_matches_itself()
    {
        await SeedEstateAsync();
        await using var db = _catalog.CreateDbContext();

        var named = await CacheVersions.CompareAsync(db, new CacheComparisonQuery(Scope,Earlier) { ToVersion = Later });
        var current = await CacheVersions.CompareAsync(db, new CacheComparisonQuery(Scope,Earlier));
        Assert.Equal(current!.Total, named!.Total);

        var same = await CacheVersions.CompareAsync(db, new CacheComparisonQuery(Scope,Later) { ToVersion = Later });
        Assert.Equal(0, same!.Total);
        Assert.Empty(same.Items);
    }

    [Fact]
    public async Task A_version_the_cache_does_not_hold_is_not_a_comparison()
    {
        await SeedEstateAsync();
        await using var db = _catalog.CreateDbContext();

        Assert.Null(await CacheVersions.CompareAsync(db, new CacheComparisonQuery(Scope,"19990101T000000Z")));
        Assert.Null(await CacheVersions.CompareAsync(db, new CacheComparisonQuery(Scope,Earlier) { ToVersion = "19990101T000000Z" }));

        // A version label names a version of one partition's cache: another partition's cache holding the same label is not this one.
        await SeedAsync("other-partition", Earlier, Uom("m", "metre"));
        Assert.Null(await CacheVersions.CompareAsync(db, new CacheComparisonQuery("other-partition", Earlier) { ToVersion = Later }));
    }

    [Fact]
    public async Task The_history_says_what_each_version_changed_against_the_one_captured_before_it()
    {
        await SeedAsync(Scope,First, Uom("m", "metre"), Wellbore("A", "NO 1/1-A"));
        await SeedEstateAsync();
        await using var db = _catalog.CreateDbContext();

        var history = await CacheVersions.HistoryAsync(db, Scope,type: null);
        Assert.Equal(new[] { Later, Earlier, First }, history.Select(h => h.Version.Version));
        Assert.True(history[0].Version.Current);

        Assert.Equal(Earlier, history[0].Before);
        Assert.Equal(new CacheChangeCounts(1, 1, 1), history[0].Changes);

        // Earlier kept the metre and the wellbore as First held them, and added the foot and the femtotesla.
        Assert.Equal(First, history[1].Before);
        Assert.Equal(new CacheChangeCounts(0, 2, 0), history[1].Changes);

        // The first version has nothing before it: everything it holds arrived with it.
        Assert.Null(history[2].Before);
        Assert.Equal(new CacheChangeCounts(0, 2, 0), history[2].Changes);

        // Narrowed to a type the later version did not touch, the same version changed nothing.
        var wellbores = await CacheVersions.HistoryAsync(db, Scope,"Wellbore");
        Assert.Equal(new CacheChangeCounts(0, 0, 0), wellbores[0].Changes);
    }

    [Fact]
    public async Task A_value_that_changed_and_changed_back_is_no_difference_between_the_ends()
    {
        await SeedAsync(Scope,First, Uom("m", "metre"));
        await SeedAsync(Scope,Earlier, Uom("m", "meter"));
        await SeedAsync(Scope,Later, Uom("m", "metre"));
        await using var db = _catalog.CreateDbContext();

        Assert.Equal(0, (await CacheVersions.CompareAsync(db, new CacheComparisonQuery(Scope,First) { ToVersion = Later }))!.Total);
        Assert.Equal(1, (await CacheVersions.CompareAsync(db, new CacheComparisonQuery(Scope,First) { ToVersion = Earlier }))!.Changed);
        Assert.Equal(new CacheChangeCounts(1, 0, 0), (await CacheVersions.HistoryAsync(db, Scope,type: null))[0].Changes);
    }

    [Fact]
    public async Task A_comparison_asked_for_no_items_answers_the_counts_alone()
    {
        await SeedEstateAsync();
        await using var db = _catalog.CreateDbContext();

        var counts = await CacheVersions.CompareAsync(db, new CacheComparisonQuery(Scope,Earlier) { Take = 0 });
        Assert.Equal(3, counts!.Total);
        Assert.Empty(counts.Items);
    }

    private async Task SeedEstateAsync()
    {
        await SeedAsync(Scope,Earlier, Uom("m", "metre"), Uom("ft", "foot"), Uom("fT", "femtotesla"), Wellbore("A", "NO 1/1-A"));
        await SeedAsync(Scope,Later, Uom("m", "Metre"), Uom("fT", "femtotesla"), Uom("km", "kilometre"), Wellbore("A", "NO 1/1-A"));
    }

    /// <summary>
    /// Merges one capture holding every type of <paramref name="items"/> into the partition's cache, captured at the instant
    /// <paramref name="version"/> names, so the version it writes carries that label.
    /// </summary>
    private async Task SeedAsync(string scope, string version, params Item[] items)
    {
        var captured = DateTimeOffset.ParseExact(version, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        var types = items.GroupBy(i => (i.Type, i.EntityType)).Select(g => new ReferenceType(g.Key.Type, g.Key.EntityType, g.Select(i => i.Record))).ToList();
        var write = await _catalog.Caches().MergeAsync(scope, "wells-osdu-00-reference-cache", types, Capture, captured);
        Assert.True(write.Written);
        Assert.Equal(version, write.Snapshot.Version);
    }

    private static string UomId(string code) => "test:reference-data--UnitOfMeasure:" + code;

    private static Item Uom(string code, string name)
        => new("UnitOfMeasure", "reference-data--UnitOfMeasure", ReferenceItem.FromText(UomId(code), new Dictionary<string, string> { ["Code"] = code, ["Name"] = name }));

    private static Item Wellbore(string key, string facility)
        => new("Wellbore", "master-data--Wellbore", ReferenceItem.FromText("test:master-data--Wellbore:" + key, new Dictionary<string, string> { ["FacilityName"] = facility }));

    private sealed record Item(string Type, string EntityType, ReferenceItem Record);
}
