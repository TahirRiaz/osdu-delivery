using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Whether the records a record refers to exist (<see cref="ReferenceResolver"/>): the ledger first, then OSDU's storage
/// service when the flow asks for it, which then decides every id the ledger does not hold, else the cache for the entity
/// types it captures. Each source is asked once per call, an id is looked up without its version, and a source that cannot
/// be read is never taken for an answer.
/// </summary>
public sealed class ReferenceResolverTests
{
    private const string Well = "dev:master-data--Well:W-1:";
    private const string Unit = "dev:reference-data--UnitOfMeasure:m:";
    private const string Crs = "dev:reference-data--CoordinateReferenceSystem:Projected:EPSG::23031:";

    private static readonly ReferenceSnapshot Cache = new("v7", DateTimeOffset.UnixEpoch,
    [
        new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure", [Item("dev:reference-data--UnitOfMeasure:m"), Item("dev:reference-data--UnitOfMeasure:ft")]),
        new ReferenceType("Crs", "reference-data--CoordinateReferenceSystem", [Item("dev:reference-data--CoordinateReferenceSystem:Projected:EPSG::23031")]),
    ]);

    private static ReferenceItem Item(string id) => ReferenceItem.FromText(id, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private static FoundReference Ref(string id) => new(id, id.Split(':')[1], "data.X", "data.X");

    /// <summary>A source answering from a set, counting how often it is asked and what.</summary>
    private sealed class Source(params string[] holds)
    {
        public List<IReadOnlyCollection<string>> Asked { get; } = [];

        public Task<IReadOnlySet<string>> Answer(IReadOnlyCollection<string> ids, CancellationToken ct)
        {
            Asked.Add(ids.ToList());
            return Task.FromResult<IReadOnlySet<string>>(ids.Where(holds.Contains).ToHashSet(StringComparer.Ordinal));
        }
    }

    [Fact]
    public async Task The_ledger_answers_first_and_storage_is_asked_only_for_what_the_ledger_does_not_hold()
    {
        var ledger = new Source("dev:master-data--Well:W-1");
        var osdu = new Source("dev:reference-data--UnitOfMeasure:m");
        var resolver = new ReferenceResolver(Cache, null, ledger.Answer, osdu.Answer);

        var answers = await resolver.ResolveAsync([Ref(Well), Ref(Unit), Ref("dev:master-data--Field:Gone:")]);

        Assert.Equal(ReferenceState.InLedger, answers["dev:master-data--Well:W-1"].State);
        Assert.Equal(ReferenceState.InOsdu, answers["dev:reference-data--UnitOfMeasure:m"].State);
        var gone = answers["dev:master-data--Field:Gone"];
        Assert.Equal(ReferenceState.Missing, gone.State);
        Assert.Contains("storage service", gone.Detail, StringComparison.Ordinal);
        Assert.Single(ledger.Asked);
        Assert.Equal(["dev:reference-data--UnitOfMeasure:m", "dev:master-data--Field:Gone"], Assert.Single(osdu.Asked));
    }

    [Fact]
    public async Task Storage_asked_decides_for_every_id_the_ledger_does_not_hold_whatever_the_cache_holds()
    {
        // The cache holds the unit; storage, asked, says the partition no longer does, and storage decides.
        var resolver = new ReferenceResolver(Cache, null, new Source().Answer, new Source().Answer);

        var answers = await resolver.ResolveAsync([Ref(Unit)]);

        Assert.Equal(ReferenceState.Missing, answers["dev:reference-data--UnitOfMeasure:m"].State);
        Assert.True(resolver.AsksOsdu);
    }

    [Fact]
    public async Task Without_storage_the_cache_answers_for_the_entity_types_it_captures_and_says_nothing_of_the_rest()
    {
        var resolver = new ReferenceResolver(Cache, "version v7 of the cache of partition 'dev'", new Source().Answer, osdu: null);

        var answers = await resolver.ResolveAsync([Ref(Unit), Ref("dev:reference-data--UnitOfMeasure:furlong:"), Ref(Well), Ref(Crs)]);

        Assert.Equal(ReferenceState.InCache, answers["dev:reference-data--UnitOfMeasure:m"].State);
        var furlong = answers["dev:reference-data--UnitOfMeasure:furlong"];
        Assert.Equal(ReferenceState.Missing, furlong.State);
        Assert.Equal("version v7 of the cache of partition 'dev' holds no such reference-data--UnitOfMeasure record in UnitOfMeasure", furlong.Detail);

        // A type the cache does not capture says nothing about whether a record of it exists.
        Assert.Equal(ReferenceState.NotChecked, answers["dev:master-data--Well:W-1"].State);

        // A code with colons of its own is found by the id without its trailing colon.
        Assert.Equal(ReferenceState.InCache, answers["dev:reference-data--CoordinateReferenceSystem:Projected:EPSG::23031"].State);
        Assert.False(resolver.AsksOsdu);
    }

    [Theory]
    [InlineData("dev:master-data--Well:W-1:")]
    [InlineData("dev:master-data--Well:W-1:1700000000000")]
    [InlineData("dev:master-data--Well:W-1")]
    [InlineData("  dev:master-data--Well:W-1:  ")]
    public async Task An_id_is_looked_up_without_its_version_so_every_way_of_writing_it_is_one_question(string written)
    {
        var ledger = new Source("dev:master-data--Well:W-1");
        var answers = await new ReferenceResolver(null, null, ledger.Answer, null).ResolveAsync([Ref(written), Ref(Well)]);

        Assert.Equal("dev:master-data--Well:W-1", ReferenceResolver.Key(written));
        Assert.Equal(ReferenceState.InLedger, Assert.Single(answers).Value.State);
        Assert.Equal(["dev:master-data--Well:W-1"], Assert.Single(ledger.Asked));
    }

    [Fact]
    public async Task Nothing_to_resolve_asks_nothing()
    {
        var ledger = new Source();
        var osdu = new Source();

        Assert.Empty(await new ReferenceResolver(Cache, null, ledger.Answer, osdu.Answer).ResolveAsync([]));
        Assert.Empty(ledger.Asked);
        Assert.Empty(osdu.Asked);
    }

    [Fact]
    public async Task Ids_the_ledger_holds_all_of_ask_storage_nothing()
    {
        var osdu = new Source();
        var answers = await new ReferenceResolver(null, null, new Source("dev:master-data--Well:W-1").Answer, osdu.Answer).ResolveAsync([Ref(Well)]);

        Assert.Equal(ReferenceState.InLedger, Assert.Single(answers).Value.State);
        Assert.Empty(osdu.Asked);
    }

    [Fact]
    public async Task A_source_that_cannot_be_read_fails_the_lookup_rather_than_answering_for_it()
    {
        var resolver = new ReferenceResolver(Cache, null, new Source().Answer, (_, _) => throw new HttpRequestException("storage is down"));
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => resolver.ResolveAsync([Ref(Unit)]));
        Assert.Equal("storage is down", failure.Message);

        var ledgerDown = new ReferenceResolver(Cache, null, (_, _) => throw new InvalidOperationException("ledger is down"), null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ledgerDown.ResolveAsync([Ref(Unit)]));
    }

    [Fact]
    public async Task A_resolver_that_asks_nothing_says_nothing_of_any_id()
    {
        var answers = await ReferenceResolver.None.ResolveAsync([Ref(Unit), Ref(Well)]);
        Assert.All(answers.Values, a => Assert.Equal(ReferenceState.NotChecked, a.State));
    }
}
