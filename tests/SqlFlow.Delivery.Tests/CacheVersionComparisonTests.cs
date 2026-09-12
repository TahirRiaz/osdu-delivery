using System.Globalization;
using SqlFlow.Catalog;
using SqlFlow.Delivery.Catalog;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Comparing two snapshot versions of the OSDU cache over the items the catalog carries: which cached records changed
/// (and in which captured values), which arrived and which left, per type, a page at a time, and what could not be
/// compared because a repository lacks one of the versions or the catalog no longer carries its records.
/// </summary>
public sealed class CacheVersionComparisonTests : IDisposable
{
    private const string Earlier = "20260910T100000Z";
    private const string Later = "20260911T100000Z";

    private static readonly Guid Repo = Guid.Parse("7d0c2a51-2b7e-4c61-9a52-0f1b1e6a4c01");
    private static readonly Guid OtherRepo = Guid.Parse("7d0c2a51-2b7e-4c61-9a52-0f1b1e6a4c02");

    private readonly SqliteCatalog _catalog = new();

    public void Dispose() => _catalog.Dispose();

    [Fact]
    public async Task A_comparison_names_what_changed_what_arrived_and_what_left()
    {
        await SeedEstateAsync();

        await using var db = _catalog.CreateDbContext();
        var diff = await CacheVersions.CompareAsync(db, new CacheComparisonQuery(Earlier));

        Assert.NotNull(diff);
        Assert.Empty(diff.Gaps);
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

        var second = await CacheVersions.CompareAsync(db, new CacheComparisonQuery(Earlier) { Skip = 1, Take = 1 });
        Assert.NotNull(second);
        Assert.Equal(3, second.Total);
        Assert.Equal(UomId("km"), Assert.Single(second.Items).RecordId);

        var third = await CacheVersions.CompareAsync(db, new CacheComparisonQuery(Earlier) { Skip = 2, Take = 5 });
        Assert.Equal(UomId("ft"), Assert.Single(third!.Items).RecordId);

        // A change filter narrows the items, while the per-type counts keep describing every kind of change.
        var removed = await CacheVersions.CompareAsync(db, new CacheComparisonQuery(Earlier) { Change = CacheItemChange.Removed });
        Assert.Equal(1, removed!.Total);
        Assert.Equal(UomId("ft"), Assert.Single(removed.Items).RecordId);
        Assert.Equal((1L, 1L, 1L), (removed.Changed, removed.Added, removed.Removed));

        var wellbores = await CacheVersions.CompareAsync(db, new CacheComparisonQuery(Earlier) { Type = "Wellbore" });
        Assert.Equal(0, wellbores!.Total);
        Assert.Empty(wellbores.Types);

        // Search matches a value either side holds, not only the id.
        var kilo = await CacheVersions.CompareAsync(db, new CacheComparisonQuery(Earlier) { Search = "kilo" });
        Assert.Equal(UomId("km"), Assert.Single(kilo!.Items).RecordId);
    }

    [Fact]
    public async Task The_later_side_defaults_to_the_current_version_and_a_version_matches_itself()
    {
        await SeedEstateAsync();
        await using var db = _catalog.CreateDbContext();

        var named = await CacheVersions.CompareAsync(db, new CacheComparisonQuery(Earlier) { ToVersion = Later });
        var current = await CacheVersions.CompareAsync(db, new CacheComparisonQuery(Earlier));
        Assert.Equal(current!.Total, named!.Total);

        var same = await CacheVersions.CompareAsync(db, new CacheComparisonQuery(Later) { ToVersion = Later });
        Assert.Equal(0, same!.Total);
        Assert.Empty(same.Items);
        Assert.Empty(same.Gaps);
    }

    [Fact]
    public async Task A_version_no_repository_holds_is_not_a_comparison()
    {
        await SeedEstateAsync();
        await using var db = _catalog.CreateDbContext();

        Assert.Null(await CacheVersions.CompareAsync(db, new CacheComparisonQuery("19990101T000000Z")));
        Assert.Null(await CacheVersions.CompareAsync(db, new CacheComparisonQuery(Earlier) { ToVersion = "19990101T000000Z" }));
    }

    [Fact]
    public async Task A_version_whose_records_are_not_carried_is_reported_rather_than_read_as_empty()
    {
        await SeedEstateAsync();
        await SeedAsync(Repo, "20260901T100000Z", current: false);
        await using var db = _catalog.CreateDbContext();

        // Compared as held, an aged-out version would read as "every record was added".
        var diff = await CacheVersions.CompareAsync(db, new CacheComparisonQuery("20260901T100000Z"));
        Assert.NotNull(diff);
        Assert.Equal(0, diff.Total);
        var gap = Assert.Single(diff.Gaps);
        Assert.Equal(Repo, gap.RepoId);
        Assert.Contains("no longer carries", gap.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_repository_lacking_one_side_is_a_gap_and_the_others_are_still_compared()
    {
        await SeedEstateAsync();
        await SeedAsync(OtherRepo, "20260912T100000Z", current: true, Uom("m", "metre"));
        await using var db = _catalog.CreateDbContext();

        var diff = await CacheVersions.CompareAsync(db, new CacheComparisonQuery(Earlier));
        Assert.NotNull(diff);
        Assert.Equal(3, diff.Total);
        Assert.All(diff.Items, item => Assert.Equal(Repo, item.RepoId));
        var gap = Assert.Single(diff.Gaps);
        Assert.Equal(OtherRepo, gap.RepoId);
        Assert.Contains(Earlier, gap.Reason, StringComparison.Ordinal);

        // Scoped to the repository that holds both, there is nothing to report.
        var scoped = await CacheVersions.CompareAsync(db, new CacheComparisonQuery(Earlier) { RepoId = Repo });
        Assert.Empty(scoped!.Gaps);
    }

    [Fact]
    public async Task The_history_says_what_each_version_changed_against_the_one_before_it()
    {
        await SeedEstateAsync();
        await SeedAsync(Repo, "20260901T100000Z", current: false);
        await using var db = _catalog.CreateDbContext();

        var history = await CacheVersions.HistoryAsync(db, Repo, type: null);
        Assert.Equal(new[] { Later, Earlier, "20260901T100000Z" }, history.Select(h => h.Version.Version));
        Assert.Equal(Earlier, history[0].Previous!.Version);
        Assert.Equal(new CacheChangeCounts(1, 1, 1), history[0].Changes);

        // The version before Earlier is not carried, so nothing is claimed about what Earlier changed.
        Assert.Equal("20260901T100000Z", history[1].Previous!.Version);
        Assert.Null(history[1].Changes);
        Assert.False(history[2].Version.Carried);
        Assert.Null(history[2].Previous);
        Assert.Null(history[2].Changes);

        // Narrowed to a type the later version did not touch, the same version changed nothing.
        var wellbores = await CacheVersions.HistoryAsync(db, Repo, "Wellbore");
        Assert.Equal(new CacheChangeCounts(0, 0, 0), wellbores[0].Changes);
    }

    [Fact]
    public async Task A_comparison_asked_for_no_items_answers_the_counts_alone()
    {
        await SeedEstateAsync();
        await using var db = _catalog.CreateDbContext();

        var counts = await CacheVersions.CompareAsync(db, new CacheComparisonQuery(Earlier) { Take = 0 });
        Assert.Equal(3, counts!.Total);
        Assert.Empty(counts.Items);
    }

    private async Task SeedEstateAsync()
    {
        await SeedAsync(Repo, Earlier, current: false,
            Uom("m", "metre"), Uom("ft", "foot"), Uom("fT", "femtotesla"), Wellbore("A", "NO 1/1-A"));
        await SeedAsync(Repo, Later, current: true,
            Uom("m", "Metre"), Uom("fT", "femtotesla"), Uom("km", "kilometre"), Wellbore("A", "NO 1/1-A"));
    }

    private async Task SeedAsync(Guid repoId, string version, bool current, params Item[] items)
    {
        await using var db = _catalog.CreateDbContext();
        var now = DateTime.UtcNow;
        var snapshot = new DeliverySnapshot
        {
            Id = Guid.NewGuid(),
            RepoId = repoId,
            Kind = "references",
            Name = version,
            Version = version,
            CapturedUtc = DateTime.ParseExact(version, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
            Current = current,
            RelativePath = $"snapshots/references/{version}/manifest.json",
            FirstSeenUtc = now,
            LastSeenUtc = now,
        };
        db.DeliverySnapshots.Add(snapshot);
        db.DeliverySnapshotItems.AddRange(items.Select(i => new DeliverySnapshotItem
        {
            SnapshotId = snapshot.Id,
            RepoId = repoId,
            TypeName = i.TypeName,
            EntityType = i.EntityType,
            RecordId = i.RecordId,
            FieldsJson = i.FieldsJson,
            Terms = i.Terms,
        }));
        await db.SaveChangesAsync();
    }

    private static string UomId(string code) => "test:reference-data--UnitOfMeasure:" + code;

    private static Item Uom(string code, string name)
        => new("UnitOfMeasure", "reference-data--UnitOfMeasure", UomId(code), $$"""{"Code":"{{code}}","Name":"{{name}}"}""", code + "\n" + name);

    private static Item Wellbore(string key, string facility)
        => new("Wellbore", "master-data--Wellbore", "test:master-data--Wellbore:" + key, $$"""{"FacilityName":"{{facility}}"}""", facility);

    private sealed record Item(string TypeName, string EntityType, string RecordId, string FieldsJson, string Terms);
}
