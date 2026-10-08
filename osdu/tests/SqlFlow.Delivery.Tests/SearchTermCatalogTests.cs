using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.SearchTerms;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The search terms in the module's database (osdu/docs/search-terms.md): the repository sync extracts them from the
/// mappings the active delivery flows pin, after the mappings, cache declarations and interfaces it reads them from; what a
/// person makes of a term (a name, leaving it out, the route it is searched through, a note) holds across every sync, and
/// stays, named by its key, when no mapping gives the term any longer; and a condition on a term becomes the condition the
/// explorer asks, through the term's route.
/// </summary>
[Collection(SqlServerSuite.Name)]
public sealed class SearchTermCatalogTests : IDisposable
{
    private const string WellLog = "work-product-component--WellLog";

    private readonly OsduTestDatabase _module = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqlflow-search-terms-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _repo = Guid.NewGuid();
    private readonly TestClock _clock = new();

    public SearchTermCatalogTests()
    {
        // The sample estate as a repository holds it: the flows, the mapping they pin and the cache flows.
        CopyDirectory(Samples.Source, _root);
    }

    public void Dispose()
    {
        _module.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private async Task<(IReadOnlyList<string> Warnings, SqlFlow.Catalog.CatalogSyncExtensionResult Result)> SyncAsync()
    {
        var warnings = new List<string>();
        await using var db = _module.CreateDbContext();
        var result = await new DeliveryCatalogSync(new DeliveryDocumentLoader()).ReconcileAsync(db, _repo, _root, _clock.GetUtcNow().UtcDateTime, warnings, CancellationToken.None);
        return (warnings, result);
    }

    /// <summary>Saves the templates the sample mapping pins and searches by, as the Templates page saves them.</summary>
    private async Task SaveTemplatesAsync()
    {
        var store = _module.Templates(_clock);
        await store.SaveAsync(Samples.SampleTemplate(WellLogVersions.CurrentKind), "tests", "tests");
        await store.SaveAsync(Samples.SampleTemplate("osdu:wks:master-data--Wellbore:1.3.0"), "tests", "tests");
    }

    private async Task<T> WithDirectoryAsync<T>(Func<SearchTermDirectory, Task<T>> read)
    {
        await using var db = _module.CreateDbContext();
        return await read(new SearchTermDirectory(db, _module.Templates(_clock), new DeliveryDocumentLoader(), _clock));
    }

    private Task<IReadOnlyList<SearchTermView>> TermsAsync(bool orphans = false) => WithDirectoryAsync(d => d.ListAsync(WellLog, orphans, CancellationToken.None));

    private static Guid IdOf(string column, string? dataset = null) => SearchTermKey.Of("recall", WellLog, dataset, column).Id;

    [Fact]
    public async Task The_sync_extracts_the_terms_of_the_mappings_the_active_delivery_flows_pin()
    {
        var (warnings, _) = await SyncAsync();
        Assert.DoesNotContain(warnings, w => w.Contains("search term", StringComparison.OrdinalIgnoreCase));

        await using var db = _module.CreateDbContext();
        var rows = await db.DeliverySearchTerms.AsNoTracking().Where(t => t.RepoId == _repo).ToListAsync();
        var wellbore = Assert.Single(rows, r => r.Column == "wellbore_uwi");
        Assert.Equal((IdOf("wellbore_uwi"), "recall/work-product-component--WellLog//wellbore_uwi", WellLog), (wellbore.TermId, wellbore.TermKey, wellbore.EntityType));
        Assert.Contains("recall-welllog-03-header-delivery", wellbore.FlowsJson, StringComparison.Ordinal);
        Assert.Equal(SearchRouteKind.Search, Assert.Single(SearchRoute.FromJson(wellbore.RoutesJson)).Kind);
        Assert.Contains(rows, r => r.Dataset == "curves" && r.Column == "curve_unit");

        // A sync of the same repository changes nothing.
        var again = (await SyncAsync()).Result;
        await using var after = _module.CreateDbContext();
        Assert.Equal(rows.Count, await after.DeliverySearchTerms.CountAsync(t => t.RepoId == _repo));
        Assert.Equal(0, again.Added);
    }

    [Fact]
    public async Task A_term_is_listed_by_its_columns_name_with_the_route_it_is_searched_through_classified_by_the_saved_templates()
    {
        await SyncAsync();

        // No template saved: nothing of the record's content can be compared, and each term says why.
        var unsaved = Assert.Single(await TermsAsync(), t => t.Column == "log_run");
        Assert.Null(unsaved.Route);
        Assert.Contains("is not saved; save it on the Templates page", unsaved.Problem, StringComparison.Ordinal);

        await SaveTemplatesAsync();
        var terms = await TermsAsync();
        var run = Assert.Single(terms, t => t.Column == "log_run");
        Assert.Equal(("log_run", "osdu.data.LogRun|Copy", (string?)null, false), (run.Name, run.Route, run.Problem, run.Renamed));
        Assert.Equal("text", run.Routes[0].Index);
        Assert.Equal(new SearchTermSuggest(null, "data.LogRun", "text", null), run.Suggest);

        // Through the wellbores a search finds, which the Wellbore template says how to match: its values are their names.
        var wellbore = Assert.Single(terms, t => t.Column == "wellbore_uwi");
        Assert.Equal(new SearchTermSuggest("osdu:wks:master-data--Wellbore:*", "data.FacilityName", "text", null), wellbore.Suggest);
        Assert.Contains("is", wellbore.Routes[0].Conditions);

        // A route through a table of the database is listed and never chosen; one through the partition's units waits for a
        // saved template of them, which says how a unit's code is indexed.
        var unit = Assert.Single(terms, t => t.ColumnLabel == "curves.curve_unit");
        Assert.Null(unit.Route);
        Assert.Contains(unit.Routes, r => r.Kind == "steps" && r.Problem!.Contains("RecallUnits", StringComparison.Ordinal));
        Assert.Contains(unit.Routes, r => r.Kind == "lookup" && r.Problem!.Contains("no saved template of osdu:wks:reference-data--UnitOfMeasure:*", StringComparison.Ordinal));

        var types = await WithDirectoryAsync(d => d.EntityTypesAsync(CancellationToken.None));
        Assert.Equal(WellLog, Assert.Single(types).EntityType);
    }

    [Fact]
    public async Task What_a_person_makes_of_a_term_holds_across_syncs_and_names_it_once_no_mapping_gives_it()
    {
        await SyncAsync();
        await SaveTemplatesAsync();

        var renamed = await WithDirectoryAsync(d => d.RefineAsync(IdOf("wellbore_uwi"), new SearchTermRefinementRequest("Wellbore name", false, null, "The UWI Recall files the log under."), "tester", CancellationToken.None));
        Assert.Equal(("Wellbore name", true, "The UWI Recall files the log under.", "tester"), (renamed.Name, renamed.Renamed, renamed.Note, renamed.UpdatedBy));
        await WithDirectoryAsync(d => d.RefineAsync(IdOf("log_run"), new SearchTermRefinementRequest("Run", true, null, null), "tester", CancellationToken.None));

        // A sync writes the terms again and never what was made of them.
        await SyncAsync();
        var terms = await TermsAsync();
        Assert.Equal("Wellbore name", Assert.Single(terms, t => t.Column == "wellbore_uwi").Name);
        Assert.True(Assert.Single(terms, t => t.Column == "log_run").Excluded);

        // The mapping stops reading log_run: the term goes, and its refinement stays, named by its key, while asked for.
        var mapping = Path.Combine(_root, "mappings", "WellLog@1.4.0.yaml");
        File.WriteAllText(mapping, File.ReadAllText(mapping).Replace("    LogRun: { $from: log_run }\n", string.Empty, StringComparison.Ordinal).Replace("    LogRun: { $from: log_run }\r\n", string.Empty, StringComparison.Ordinal));
        await SyncAsync();
        Assert.DoesNotContain(await TermsAsync(), t => t.Column == "log_run");
        var orphan = Assert.Single(await TermsAsync(orphans: true), t => t.Column == "log_run");
        Assert.Equal((true, "Run", true), (orphan.Orphan, orphan.Name, orphan.Excluded));
        await Assert.ThrowsAsync<DeliveryException>(() => WithDirectoryAsync(d => d.RefineAsync(IdOf("log_run"), new SearchTermRefinementRequest("Again", false, null, null), "tester", CancellationToken.None)));

        // Removing the refinement removes the orphan.
        Assert.True(await WithDirectoryAsync(d => d.ResetAsync(IdOf("log_run"), CancellationToken.None)));
        Assert.DoesNotContain(await TermsAsync(orphans: true), t => t.Column == "log_run");
        Assert.False(await WithDirectoryAsync(d => d.ResetAsync(IdOf("log_run"), CancellationToken.None)));
    }

    [Fact]
    public async Task A_name_belongs_to_one_term_of_an_entity_type_and_a_route_picked_must_be_one_the_term_is_searched_through()
    {
        await SyncAsync();
        await SaveTemplatesAsync();
        await WithDirectoryAsync(d => d.RefineAsync(IdOf("log_run"), new SearchTermRefinementRequest("Run", false, null, null), "tester", CancellationToken.None));

        var taken = await Assert.ThrowsAsync<DeliveryException>(() => WithDirectoryAsync(d => d.RefineAsync(IdOf("log_version"), new SearchTermRefinementRequest("run", false, null, null), "tester", CancellationToken.None)));
        Assert.Contains("is the search term log_run's already", taken.Message, StringComparison.Ordinal);

        var table = await Assert.ThrowsAsync<DeliveryException>(() => WithDirectoryAsync(d => d.RefineAsync(
            IdOf("curve_unit", "curves"), new SearchTermRefinementRequest(null, false, "osdu.data.Curves[].CurveUnit|Steps", null), "tester", CancellationToken.None)));
        Assert.Contains("cannot be searched through osdu.data.Curves[].CurveUnit", table.Message, StringComparison.Ordinal);

        // The project makes the id only with the log id, which the record does not hold: that route is listed, never searched.
        var key = await Assert.ThrowsAsync<DeliveryException>(() => WithDirectoryAsync(d => d.RefineAsync(
            IdOf("source_project"), new SearchTermRefinementRequest(null, false, "id|Key", null), "tester", CancellationToken.None)));
        Assert.Contains("does not hold log_id", key.Message, StringComparison.Ordinal);

        // A curve's mnemonic is written as its id and as its mnemonic: either is searched through, as picked.
        Assert.Equal("osdu.data.Curves[].CurveID|Copy", Assert.Single(await TermsAsync(), t => t.ColumnLabel == "curves.curve_id").Route);
        var picked = await WithDirectoryAsync(d => d.RefineAsync(
            IdOf("curve_id", "curves"), new SearchTermRefinementRequest(null, false, "osdu.data.Curves[].Mnemonic|Copy", null), "tester", CancellationToken.None));
        Assert.Equal(("osdu.data.Curves[].Mnemonic|Copy", "osdu.data.Curves[].Mnemonic|Copy"), (picked.PickedRoute, picked.Route));

        // A refinement that keeps nothing of its own is removed: the column's own name given back is no name.
        var plain = await WithDirectoryAsync(d => d.RefineAsync(IdOf("log_run"), new SearchTermRefinementRequest("log_run", false, null, " "), "tester", CancellationToken.None));
        Assert.Equal((false, (string?)null), (plain.Renamed, plain.UpdatedBy));

        await Assert.ThrowsAsync<DeliveryException>(() => WithDirectoryAsync(d => d.RefineAsync(IdOf("log_run"), new SearchTermRefinementRequest(new string('n', 101), false, null, null), "tester", CancellationToken.None)));
    }

    [Fact]
    public async Task A_condition_on_a_term_resolves_through_its_route_and_one_left_out_is_refused()
    {
        await SyncAsync();
        await SaveTemplatesAsync();
        await WithDirectoryAsync(d => d.RefineAsync(IdOf("wellbore_uwi"), new SearchTermRefinementRequest("Wellbore name", false, null, null), "tester", CancellationToken.None));

        var wellbore = await WithDirectoryAsync(d => d.ResolveAsync(IdOf("wellbore_uwi"), new SearchTermCondition(ExplorerCondition.Is, "NO 34/10-A-30"), "dev", CancellationToken.None));
        Assert.Equal(("data.WellboreID", "Wellbore name", "osdu:wks:master-data--Wellbore:*"), (wellbore.Path, wellbore.Via!.Term, wellbore.Via.Kind));

        var domain = await WithDirectoryAsync(d => d.ResolveAsync(IdOf("index_type"), new SearchTermCondition(ExplorerCondition.Is, "DEPTH"), "test", CancellationToken.None));
        Assert.Equal("data.SamplingDomainTypeID.keyword:\"test:reference-data--WellLogSamplingDomainType:Depth:\"", domain.Clause());

        await WithDirectoryAsync(d => d.RefineAsync(IdOf("index_type"), new SearchTermRefinementRequest(null, true, null, null), "tester", CancellationToken.None));
        var excluded = await Assert.ThrowsAsync<DeliveryException>(() => WithDirectoryAsync(d => d.ResolveAsync(IdOf("index_type"), new SearchTermCondition(ExplorerCondition.Is, "DEPTH"), "dev", CancellationToken.None)));
        Assert.Contains("is left out of the search", excluded.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<DeliveryException>(() => WithDirectoryAsync(d => d.ResolveAsync(Guid.NewGuid(), new SearchTermCondition(ExplorerCondition.Is, "x"), "dev", CancellationToken.None)));
    }

    [Fact]
    public async Task The_terms_are_written_again_from_the_module_rows_alone_as_the_control_plane_does_when_it_starts()
    {
        await SyncAsync();
        await using (var db = _module.CreateDbContext())
        {
            await db.DeliverySearchTerms.Where(t => t.RepoId == _repo).ExecuteDeleteAsync();
        }

        var warnings = new List<string>();
        await using var context = _module.CreateDbContext();
        var counts = await DeliverySearchTermCatalog.ReconcileAsync(context, _repo, new DeliveryDocumentLoader(), _clock.GetUtcNow().UtcDateTime, warnings, CancellationToken.None);

        Assert.True(counts.Added > 10);
        Assert.Empty(warnings);
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(from, to, StringComparison.Ordinal));
        }

        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(from, to, StringComparison.Ordinal), overwrite: true);
        }
    }
}
