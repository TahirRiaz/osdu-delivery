using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Snapshots;
using Xunit;
using static SqlFlow.Delivery.Tests.AccessListTests;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What a refresh of the wellbores or of the access group map does to well logs already delivered with the viewers they
/// gave: a group the data office lists for a field later, a wellbore loaded after its logs, a wellbore given its field, and a
/// country group that comes to name a field each reach the records built without them, and a change nothing read reaches
/// none. Every record is judged from the dependency set its render left behind, never by rendering the estate again.
/// </summary>
[Collection(SqlServerSuite.Name)]
public sealed class AccessListCacheChangeTests : IAsyncLifetime, IDisposable
{
    private const string Scope = "dev";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly Guid _flow = FlowId.Of("access-list-flow");
    private readonly OsduLedger _ledger;

    public AccessListCacheChangeTests() => _ledger = _db.Ledger(_clock);

    public Task InitializeAsync() => _ledger.RegisterAsync(_flow);

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose() => _db.Dispose();

    /// <summary>Renders a log of the wellbore named <paramref name="uwi"/> and delivers <paramref name="count"/> records built from what it read.</summary>
    private async Task<IReadOnlyList<CacheUsage>> DeliveredAsync(string uwi, int count, bool optionalWellbore = false, ReferenceSnapshot? cache = null)
    {
        var mapping = optionalWellbore ? Mapping(data: "WellboreID: { $lookup: wellbore.id, $required: false }") : Mapping();
        var usages = Renderer(mapping, cache).Render(Record(uwi)).CacheUsages;
        var submission = Guid.NewGuid();
        var now = _clock.GetUtcNow().UtcDateTime;
        await _ledger.RegisterSubmissionAsync(new SubmissionState
        {
            SubmissionId = submission,
            FlowId = _flow,
            FlowName = "access-list-flow",
            MappingReference = "Thing@1.0.0",
            RenderContext = "{}",
            RecordCount = count,
            Status = SubmissionStatus.Planned,
            ReceivedUtc = now,
        });

        var setId = await _ledger.EnsureCacheSetAsync(Scope, usages);
        var records = Enumerable.Range(0, count).Select(i => new RecordState
        {
            DeliveryKey = DeliveryKey.Derive("test", [$"{uwi}-{i}"]),
            FlowId = _flow,
            SourceKey = $"{uwi}-{i}",
            MappingName = "Thing",
            TargetId = $"dev:work-product-component--WellLog:{uwi.Replace(' ', '-').Replace('/', '-')}-{i}",
            LastSubmissionId = submission,
            PendingDocumentRef = "0:0:10",
            PendingRenderContext = "{}",
            PendingMetadataHash = "mh",
            PendingMetadata = true,
            CacheSetId = setId,
        }).ToList();
        await _ledger.UpsertPendingAsync(_flow, records);
        return usages;
    }

    private Task<CacheImpactResult> AnalyzeAsync(ReferenceType previous, ReferenceType current)
        => new CacheImpactAnalyzer(_ledger, _clock, NullLogger.Instance).AnalyzeAsync(Scope, previous, current, CacheChangeMode.Auto, "v1", "v2");

    private async Task<UpdateTag> OnlyTagAsync() => Assert.Single(await _ledger.ListTagsAsync("approved", 10, 0));

    [Fact]
    public async Task A_group_the_data_office_lists_for_a_field_later_reaches_every_log_built_without_it()
    {
        await DeliveredAsync("NO 16/3-A-1", 4);

        var impact = await AnalyzeAsync(Groups(), Groups(Item("dev:data-governance--AccessGroupMap:linge-extra",
            ("FieldIDList", $"[\"{MartinLinge}\"]"), ("FieldList", "[\"MARTIN LINGE\"]"),
            ("EntitlementGroupEmail", "\"data.office.martin.linge.partners.viewers@x\""))));

        Assert.Equal(4, impact.AffectedRecords);
        var tag = await OnlyTagAsync();
        Assert.Equal("relisted", tag.Change);
        Assert.Equal("FieldIDList", tag.Path);
        Assert.Equal(CacheUsage.ListingKey(MartinLinge), tag.ItemId);
        Assert.Equal(string.Empty, tag.OldValue ?? string.Empty);
        Assert.Equal("dev:data-governance--AccessGroupMap:linge-extra", tag.NewValue);
        Assert.Contains("AccessGroupMap rows whose FieldIDList holds", tag.Describe(), StringComparison.Ordinal);
        Assert.Equal(4, tag.AffectedRecords);
    }

    [Fact]
    public async Task A_group_listed_for_a_field_no_log_lies_in_reaches_none_of_them()
    {
        await DeliveredAsync("NO 16/3-A-1", 2);

        var impact = await AnalyzeAsync(Groups(), Groups(Item("dev:data-governance--AccessGroupMap:elsewhere",
            ("FieldIDList", "[\"dev:master-data--Field:ELSEWHERE:\"]"), ("FieldList", "[\"ELSEWHERE\"]"),
            ("EntitlementGroupEmail", "\"data.office.elsewhere.viewers@x\""))));

        Assert.Equal(0, impact.AffectedRecords);
        Assert.Empty(await _ledger.ListTagsAsync(null, 10, 0));
    }

    [Fact]
    public async Task A_country_group_that_comes_to_name_a_field_stops_being_a_country_group_of_the_logs_it_reached()
    {
        await DeliveredAsync("NO 1/1-X", 3);

        var narrowed = new ReferenceType("AccessGroupMap", "data-governance--AccessGroupMap", Groups().Items.Select(item => item.Id.EndsWith(":no-contractors", StringComparison.Ordinal)
            ? Item(item.Id, ("GeoPoliticalEntityID", $"\"{Norway}\""), ("FieldList", "[\"TROLL\"]"), ("EntitlementGroupEmail", "\"data.office.norway.contractors.viewers@x\""))
            : item));
        var impact = await AnalyzeAsync(Groups(), narrowed);

        Assert.Equal(3, impact.AffectedRecords);
        var tag = await OnlyTagAsync();
        Assert.Equal("dev:data-governance--AccessGroupMap:no-contractors", tag.ItemId);
        Assert.Equal("FieldList", tag.Path);
        Assert.Equal("TROLL", tag.NewValue);
    }

    [Fact]
    public async Task A_group_s_changed_address_reaches_the_logs_that_carry_it()
    {
        await DeliveredAsync("NO 16/3-A-1", 2);

        var renamed = new ReferenceType("AccessGroupMap", "data-governance--AccessGroupMap", Groups().Items.Select(item => item.Id.EndsWith(":no-permanent", StringComparison.Ordinal)
            ? Item(item.Id, ("GeoPoliticalEntityID", $"\"{Norway}\""), ("EntitlementGroupEmail", "\"data.office.norway.employees.viewers@x\""))
            : item));
        await AnalyzeAsync(Groups(), renamed);

        var tag = await OnlyTagAsync();
        Assert.Equal("changed", tag.Change);
        Assert.Equal("EntitlementGroupEmail", tag.Path);
        Assert.Equal("data.office.norway.viewers@x", tag.OldValue);
        Assert.Equal("data.office.norway.employees.viewers@x", tag.NewValue);
    }

    [Fact]
    public async Task A_wellbore_loaded_after_its_logs_reaches_them_by_the_name_they_found_no_wellbore_by()
    {
        await DeliveredAsync("NO 99/9-Z-9", 2, optionalWellbore: true);

        var impact = await AnalyzeAsync(Wellbores(), Wellbores(Item("dev:master-data--Wellbore:late",
            ("FacilityName", "\"NO 99/9-Z-9\""),
            ("GeoContexts.FieldID", $"[\"{MartinLinge}\"]"))));

        Assert.Equal(2, impact.AffectedRecords);
        var tags = await _ledger.ListTagsAsync("approved", 10, 0);
        var tag = Assert.Single(tags, t => t.Path == "FacilityName");
        Assert.Equal("listed", tag.Change);
        Assert.Equal("dev:master-data--Wellbore:late", tag.ItemId);
        Assert.Equal("NO 99/9-Z-9", tag.NewValue);
        Assert.Contains("Wellbore now lists 'NO 99/9-Z-9' as 'dev:master-data--Wellbore:late'", tag.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wellbore_given_its_field_reaches_the_logs_built_without_the_field_s_groups()
    {
        await DeliveredAsync("NO 1/1-X", 2);

        var placed = new ReferenceType("Wellbore", "master-data--Wellbore", Wellbores().Items.Select(item => item.Id.EndsWith(":nofield", StringComparison.Ordinal)
            ? Item(item.Id, ("FacilityName", "\"NO 1/1-X\""), ("GeoContexts.FieldID", $"[\"{MartinLinge}\"]"), ("GeoContexts.GeoPoliticalEntityID", $"[\"{Norway}\"]"))
            : item));
        var impact = await AnalyzeAsync(Wellbores(), placed);

        Assert.Equal(2, impact.AffectedRecords);
        var tag = await OnlyTagAsync();
        Assert.Equal("changed", tag.Change);
        Assert.Equal("GeoContexts.FieldID", tag.Path);
        Assert.Null(tag.OldValue);
        Assert.Contains(MartinLinge, tag.NewValue, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_gaps_name_what_logs_were_built_without_and_how_many_most_records_first()
    {
        // Three logs of a field no group lists yet, two of a wellbore the cache does not hold, one complete.
        await DeliveredAsync("NO 2/2-N", 3, cache: Cache(Wellbores(Item("dev:master-data--Wellbore:new1",
            ("FacilityName", "\"NO 2/2-N\""),
            ("GeoContexts.FieldID", "[\"dev:master-data--Field:NEW:\"]")))));
        await DeliveredAsync("NO 99/9-Z-9", 2, optionalWellbore: true);
        await DeliveredAsync("NO 16/3-A-1", 1);

        var gaps = await _ledger.ListCacheGapsAsync(Scope, typeName: null, empty: false, take: 50);

        var field = Assert.Single(gaps, g => g.TypeName == "AccessGroupMap" && g.Path == "FieldIDList" && g.Key == CacheUsage.ListingKey("dev:master-data--Field:NEW:"));
        Assert.Equal(CacheUsageKind.Listed, field.Kind);
        Assert.Equal(3, field.Records);
        var wellbore = Assert.Single(gaps, g => g.TypeName == "Wellbore" && g.Path == "FacilityName");
        Assert.Equal(CacheUsageKind.Unlisted, wellbore.Kind);
        Assert.Equal("NO 99/9-Z-9", wellbore.Value);
        Assert.Equal(2, wellbore.Records);
        Assert.Equal(gaps.Max(g => g.Records), gaps[0].Records);

        // A complete log leaves no gap, and a key that found its rows is none either, in whichever form it found them: the
        // field's groups name it without the version separator and the country's with it.
        Assert.DoesNotContain(gaps, g => g.Key == CacheUsage.ListingKey(MartinLinge.TrimEnd(':')) || g.Key == CacheUsage.ListingKey(MartinLinge));
        Assert.DoesNotContain(gaps, g => g.Key == CacheUsage.ListingKey(Norway.TrimEnd(':')) || g.Key == CacheUsage.ListingKey(Norway));

        // A key no form of which found a row is one gap, not one per form.
        Assert.DoesNotContain(gaps, g => g.Key == CacheUsage.ListingKey("dev:master-data--Field:NEW"));

        // One type's gaps, and the paths read empty beside them: the wellbore the new field's log lies in has no country.
        Assert.All(await _ledger.ListCacheGapsAsync(Scope, "Wellbore", empty: false, take: 50), g => Assert.Equal("Wellbore", g.TypeName));
        var withEmpty = await _ledger.ListCacheGapsAsync(Scope, "Wellbore", empty: true, take: 50);
        Assert.Contains(withEmpty, g => g.Kind == CacheUsageKind.Empty && g.Path == "GeoContexts.GeoPoliticalEntityID" && g.Key == "dev:master-data--Wellbore:new1");
    }

    [Fact]
    public async Task A_listing_survives_the_ledger_as_the_render_wrote_it()
    {
        var usages = await DeliveredAsync("NO 16/3-A-1", 1);
        var set = await _ledger.EnsureCacheSetAsync(Scope, usages);
        var entries = await _ledger.ListCacheSetAsync(set);

        // The country is asked for as the wellbore writes it and without its version separator: two keys, one listing each.
        var listings = entries.Where(e => e.Kind == CacheUsageKind.Listed && e.Path == "GeoPoliticalEntityID").ToList();
        Assert.Equal([CacheUsage.ListingKey(Norway.TrimEnd(':')), CacheUsage.ListingKey(Norway)], listings.Select(e => e.ItemId).Order(StringComparer.Ordinal));
        var listing = Assert.Single(listings, e => e.ItemId == CacheUsage.ListingKey(Norway));
        Assert.Equal(
            "dev:data-governance--AccessGroupMap:martin-linge, dev:data-governance--AccessGroupMap:no-contractors, dev:data-governance--AccessGroupMap:no-permanent",
            listing.ValueText);
    }
}
