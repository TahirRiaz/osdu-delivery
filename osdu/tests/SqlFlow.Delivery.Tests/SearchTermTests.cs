using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;
using SqlFlow.Delivery.SearchTerms;
using SqlFlow.Delivery.Snapshots;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The search terms a mapping gives (osdu/docs/search-terms.md), read from the sample estate's Recall well log mapping: every
/// column it reads, each route by which the column reaches the record, a value typed for a term put through the mapping as
/// a render puts it, and a condition on a term turned into the condition the explorer asks of the records.
/// </summary>
public sealed class SearchTermTests
{
    private const string WellLog = "work-product-component--WellLog";
    private const string WellboreKind = "osdu:wks:master-data--Wellbore:1.3.0";

    private static readonly MappingDefinition Mapping = new DeliveryDocumentLoader().LoadMapping(Path.Combine(Samples.Mappings, "WellLog@1.4.0.yaml"));

    private static readonly SchemaSnapshot Template = Samples.SampleTemplate(WellLogVersions.CurrentKind);

    private static readonly SchemaSnapshot Wellbore = Samples.SampleTemplate(WellboreKind);

    /// <summary>The partition's units, captured from OSDU; the unit spellings Recall writes are a table of the database.</summary>
    private static SearchCacheType? CacheType(string name) => name switch
    {
        "UnitOfMeasure" => new SearchCacheType(
            "UnitOfMeasure", SearchCacheType.OsduOrigin, "osdu:wks:reference-data--UnitOfMeasure:*",
            [new ReferenceFieldSpec("data.Code"), new ReferenceFieldSpec("data.Name"), new ReferenceFieldSpec("data.ID")]),
        "RecallUnits" => new SearchCacheType("RecallUnits", "table", null, []),
        _ => null,
    };

    private static readonly IReadOnlyList<CompiledSearchTerm> Terms = SearchTermCompiler.Compile([Mapping], CacheType);

    private static CompiledSearchTerm Term(string column, string? dataset = null)
        => Assert.Single(Terms, t => t.Key == SearchTermKey.Of("recall", WellLog, dataset, column));

    private static SearchRoute Route(string column, string target, SearchRouteKind kind, string? dataset = null)
        => Assert.Single(Term(column, dataset).Routes, r => r.Target == target && r.Kind == kind);

    [Fact]
    public void A_column_written_as_it_stands_is_a_copy_and_one_through_modifiers_is_steps_keeping_what_they_keep()
    {
        var run = Route("log_run", "osdu.data.LogRun", SearchRouteKind.Copy);
        Assert.Equal(("data.LogRun", (string?)null), (run.Path, run.Problem));
        Assert.Equal(["WellLog@1.4.0"], run.Mappings);

        var name = Route("log_source", "osdu.data.Name", SearchRouteKind.Steps);
        Assert.Equal(["trim"], name.Steps);
        Assert.Equal(SearchRouteKeeps.Text, name.Keeps);

        var domain = Route("index_type", "osdu.data.SamplingDomainTypeID", SearchRouteKind.Steps);
        Assert.Equal(["replace(DEPTH: Depth)", "ref"], domain.Steps);
        Assert.Equal((SearchRouteKeeps.Value, (string?)null), (domain.Keeps, domain.Problem));

        Assert.Equal(SearchRouteKeeps.Value, Route("depth_coding", "osdu.data.IsRegular", SearchRouteKind.Steps).Keeps);
        Assert.Equal(SearchRouteKeeps.Order, Route("update_date", "osdu.data.Curves[].DateStamp", SearchRouteKind.Steps, "curves").Keeps);
        Assert.Equal(SearchRouteKeeps.Text, Route("log_pass_type", "osdu.tags.LogStatus", SearchRouteKind.Steps).Keeps);
    }

    [Fact]
    public void A_column_a_search_finds_a_record_by_is_a_search_reading_the_records_id()
    {
        var wellbore = Assert.Single(Term("wellbore_uwi").Routes);

        Assert.Equal((SearchRouteKind.Search, "osdu.data.WellboreID", (string?)null), (wellbore.Kind, wellbore.Target, wellbore.Problem));
        Assert.Equal(
            new SearchRouteFind("Wellbore", "osdu:wks:master-data--Wellbore:*", [new SearchRouteLine("data.FacilityName"), new SearchRouteLine("data.NameAliases.AliasName")], "id"),
            wellbore.Find! with { Lines = wellbore.Find!.Lines.ToList() },
            new FindComparer());
    }

    [Fact]
    public void A_column_of_a_child_dataset_is_a_term_of_its_own_with_each_route_its_nodes_give()
    {
        var unit = Term("curve_unit", "curves");

        // The first and the last alternatives translate the spelling through a table of the database: one route, never
        // carried. The second finds the partition's unit by its ID, code or name: carried, by a search of the units. The
        // type captures a field called ID, so ID is that field, as the render reads it, and not the record id.
        var table = Assert.Single(unit.Routes, r => r.Kind == SearchRouteKind.Steps);
        Assert.Contains("cached table RecallUnits", table.Problem, StringComparison.Ordinal);
        var lookup = Assert.Single(unit.Routes, r => r.Kind == SearchRouteKind.Lookup);
        Assert.Equal((2, (string?)null, "osdu:wks:reference-data--UnitOfMeasure:*"), (lookup.Alternative, lookup.Problem, lookup.Find!.Kind));
        Assert.Equal(["data.ID", "data.Code", "data.Name"], lookup.Find.Lines.Select(l => l.Field));
        Assert.Equal("osdu.data.Curves[].CurveUnit", lookup.Target);

        // A mnemonic is written twice as it is, and built into three ids from the curve dictionary, which no search reads.
        var curve = Term("curve_id", "curves");
        Assert.Equal(2, curve.Routes.Count(r => r.Kind == SearchRouteKind.Copy));
        Assert.All(curve.Routes.Where(r => r.Kind == SearchRouteKind.Steps), r => Assert.Contains("cached table CurveDictionary", r.Problem, StringComparison.Ordinal));
    }

    [Fact]
    public void A_column_of_the_key_makes_the_records_id_with_the_others_the_record_holds()
    {
        var id = Assert.Single(Term("log_id").Routes);

        Assert.Equal((SearchRouteKind.Key, "id", (string?)null), (id.Kind, id.Path, id.Problem));
        Assert.Equal(("recall", false), (id.Key!.System, id.Key.FromKey));
        Assert.Equal(["source_project", "log_id"], id.Key.Columns);
        Assert.Equal("tags.SourceProject", id.Key.Others!["source_project"]);

        // The project is written as it stands too, which is the route it is searched by first.
        Assert.Equal(2, Term("source_project").Routes.Count);
    }

    [Fact]
    public void Who_may_read_a_record_and_its_legal_block_are_not_terms()
    {
        Assert.DoesNotContain(Terms.SelectMany(t => t.Routes), r => r.Path.StartsWith("acl.", StringComparison.Ordinal) || r.Path.StartsWith("legal.", StringComparison.Ordinal));
        Assert.All(Terms, t => Assert.Equal("recall", t.Key.System));
    }

    [Fact]
    public void A_lookup_through_a_type_no_cache_flow_declares_or_one_not_of_osdu_says_why_it_cannot_be_searched()
    {
        var terms = SearchTermCompiler.Compile([Mapping], _ => null);
        var lookup = Assert.Single(Assert.Single(terms, t => t.Key.Column == "curve_unit").Routes, r => r.Kind == SearchRouteKind.Lookup);

        Assert.Contains("declared by no cache flow", lookup.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void One_route_read_by_two_versions_of_a_mapping_is_listed_under_both()
    {
        var next = Mapping with { Version = "1.5.0" };
        var terms = SearchTermCompiler.Compile([next, Mapping], CacheType);
        var run = Assert.Single(Assert.Single(terms, t => t.Key.Column == "log_run").Routes);

        Assert.Equal(["WellLog@1.4.0", "WellLog@1.5.0"], run.Mappings);
    }

    [Fact]
    public void A_terms_identity_is_its_key_whatever_the_case_of_the_column_or_the_system()
    {
        var key = SearchTermKey.Of(" Recall ", WellLog, null, "Wellbore_UWI");

        Assert.Equal("recall/work-product-component--WellLog//wellbore_uwi", key.Text);
        Assert.Equal(SearchTermKey.Of("recall", WellLog, null, "wellbore_uwi").Id, key.Id);
        Assert.NotEqual(SearchTermKey.Of("recall", WellLog, "curves", "wellbore_uwi").Id, key.Id);
        Assert.Equal("curves.curve_unit", SearchTermKey.Of("recall", WellLog, "curves", "curve_unit").ColumnLabel);
    }

    [Theory]
    [InlineData("log_source", null, "osdu.data.Name", SearchRouteKind.Steps, "  STAT_COMP ", "STAT_COMP")]
    [InlineData("index_type", null, "osdu.data.SamplingDomainTypeID", SearchRouteKind.Steps, "DEPTH", "dev:reference-data--WellLogSamplingDomainType:Depth:")]
    [InlineData("depth_coding", null, "osdu.data.IsRegular", SearchRouteKind.Steps, "REGULAR", "true")]
    [InlineData("depth_coding", null, "osdu.data.IsRegular", SearchRouteKind.Steps, "IRREGULAR", "false")]
    [InlineData("log_pass", null, "osdu.data.LogActivity", SearchRouteKind.Steps, "MAIN,REPEAT", "MAIN")]
    [InlineData("index_min", null, "osdu.data.SamplingStart", SearchRouteKind.Copy, "1500.5", "1500.5")]
    [InlineData("business_value", "curves", "osdu.data.Curves[].LogCurveBusinessValueID", SearchRouteKind.Steps, "HIGH", "dev:reference-data--LogCurveBusinessValue:High:")]
    [InlineData("wellbore_uwi", null, "osdu.data.WellboreID", SearchRouteKind.Search, "NO 34/10-A-30", "NO 34/10-A-30")]
    [InlineData("log_id", null, "id", SearchRouteKind.Key, " 9982/1 ", "9982/1")]
    public void A_value_typed_for_a_term_is_put_through_the_mapping_as_the_render_puts_it(
        string column, string? dataset, string target, SearchRouteKind kind, string typed, string written)
    {
        var values = SearchTermValues.For(Mapping, Template, "dev");
        var key = SearchTermKey.Of("recall", WellLog, dataset, column);

        var carried = values.Translate(key, Route(column, target, kind, dataset), typed);

        Assert.Equal(SearchValue.Of(written), carried);
    }

    [Fact]
    public void A_value_the_render_would_hold_a_record_for_is_refused_with_why()
    {
        var values = SearchTermValues.For(Mapping, Template, "dev");

        var notANumber = values.Translate(SearchTermKey.Of("recall", WellLog, null, "index_min"), Route("index_min", "osdu.data.SamplingStart", SearchRouteKind.Copy), "deep");
        Assert.Null(notANumber.Value);
        Assert.Contains("deep", notANumber.Problem, StringComparison.Ordinal);

        var empty = values.Translate(SearchTermKey.Of("recall", WellLog, null, "log_source"), Route("log_source", "osdu.data.Name", SearchRouteKind.Steps), "   ");
        Assert.Null(empty.Value);

        var table = values.Translate(SearchTermKey.Of("recall", WellLog, "curves", "curve_unit"), Route("curve_unit", "osdu.data.Curves[].CurveUnit", SearchRouteKind.Steps, "curves"), "GAPI");
        Assert.Contains("RecallUnits", table.Problem, StringComparison.Ordinal);

        Assert.Throws<FlowValidationException>(() => SearchTermValues.For(Mapping, Template, "not a partition"));
    }

    [Fact]
    public void The_conditions_a_route_takes_follow_its_index_and_what_its_steps_keep()
    {
        IReadOnlyList<ExplorerCondition> Conditions(string column, string target, SearchRouteKind kind, string? dataset = null, SchemaSnapshot? found = null)
        {
            var route = Route(column, target, kind, dataset);
            return SearchTermResolver.Conditions(route, SearchTermResolver.Classify(route, Template, found));
        }

        Assert.Equal(
            [ExplorerCondition.Contains, ExplorerCondition.Is, ExplorerCondition.IsNot, ExplorerCondition.AnyOf, ExplorerCondition.NoneOf, ExplorerCondition.StartsWith, ExplorerCondition.Exists, ExplorerCondition.Missing],
            Conditions("log_source", "osdu.data.Name", SearchRouteKind.Steps));
        Assert.Equal(
            [ExplorerCondition.Is, ExplorerCondition.IsNot, ExplorerCondition.AnyOf, ExplorerCondition.NoneOf, ExplorerCondition.Exists, ExplorerCondition.Missing],
            Conditions("index_type", "osdu.data.SamplingDomainTypeID", SearchRouteKind.Steps));
        Assert.Equal(ExplorerCondition.Range, Conditions("index_min", "osdu.data.SamplingStart", SearchRouteKind.Copy)[0]);
        Assert.Equal([ExplorerCondition.Is, ExplorerCondition.Exists, ExplorerCondition.Missing], Conditions("depth_coding", "osdu.data.IsRegular", SearchRouteKind.Steps));

        // Through a search of wellbores matched on a name and a nested alias: words, but no start, which a nested list does not take.
        var wellbore = Conditions("wellbore_uwi", "osdu.data.WellboreID", SearchRouteKind.Search, found: Wellbore);
        Assert.Contains(ExplorerCondition.Contains, wellbore);
        Assert.DoesNotContain(ExplorerCondition.StartsWith, wellbore);

        // Inside the curves, a nested list, nothing asks whether a value is held.
        Assert.DoesNotContain(ExplorerCondition.Exists, Conditions("curve_description", "osdu.data.Curves[].CurveDescription", SearchRouteKind.Copy, "curves"));
        Assert.Equal([ExplorerCondition.Is, ExplorerCondition.IsNot, ExplorerCondition.AnyOf, ExplorerCondition.NoneOf], Conditions("log_id", "id", SearchRouteKind.Key));
    }

    [Fact]
    public void A_route_through_records_no_saved_template_describes_cannot_be_searched_and_says_so()
    {
        var route = Route("wellbore_uwi", "osdu.data.WellboreID", SearchRouteKind.Search);

        var fields = SearchTermResolver.Classify(route, Template, found: null);

        Assert.Null(fields.Target);
        Assert.Contains("no saved template of osdu:wks:master-data--Wellbore:*", fields.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_condition_on_a_term_is_the_condition_on_the_property_its_route_fills_with_the_values_carried()
    {
        var values = SearchTermValues.For(Mapping, Template, "dev");
        ExplorerFilter Resolve(string column, string target, SearchRouteKind kind, SearchTermCondition asked, SchemaSnapshot? found = null)
        {
            var route = Route(column, target, kind);
            return SearchTermResolver.Resolve(column, SearchTermKey.Of("recall", WellLog, null, column), route, SearchTermResolver.Classify(route, Template, found), asked, values);
        }

        var domain = Resolve("index_type", "osdu.data.SamplingDomainTypeID", SearchRouteKind.Steps, new SearchTermCondition(ExplorerCondition.AnyOf, Values: ["DEPTH", "TIME"]));
        Assert.Equal(("data.SamplingDomainTypeID", OsduFieldIndex.Text, (ExplorerVia?)null), (domain.Path, domain.Index, domain.Via));
        Assert.Equal(["dev:reference-data--WellLogSamplingDomainType:Depth:", "dev:reference-data--WellLogSamplingDomainType:TIME:"], domain.Values!);
        Assert.Equal("(data.SamplingDomainTypeID.keyword:(\"dev:reference-data--WellLogSamplingDomainType:Depth:\" OR \"dev:reference-data--WellLogSamplingDomainType:TIME:\"))", domain.Clause());

        var depth = Resolve("index_min", "osdu.data.SamplingStart", SearchRouteKind.Copy, new SearchTermCondition(ExplorerCondition.Range, "1000", To: "2000"));
        Assert.Equal("data.SamplingStart:[\"1000\" TO \"2000\"}", depth.Clause());

        var wellbore = Resolve("wellbore_uwi", "osdu.data.WellboreID", SearchRouteKind.Search, new SearchTermCondition(ExplorerCondition.Is, "NO 34/10-A-30"), Wellbore);
        Assert.Equal(("data.WellboreID", "NO 34/10-A-30"), (wellbore.Path, wellbore.Value));
        Assert.Equal(("wellbore_uwi", "osdu:wks:master-data--Wellbore:*", "id"), (wellbore.Via!.Term, wellbore.Via.Kind, wellbore.Via.Read!.Path));
        Assert.Equal(
            "(data.FacilityName.keyword:\"NO 34/10-A-30\") OR (nested(data.NameAliases, (AliasName.keyword:\"NO 34/10-A-30\")))",
            wellbore.Via.Matching(wellbore).Text);

        var log = Resolve("log_id", "id", SearchRouteKind.Key, new SearchTermCondition(ExplorerCondition.Is, "9982/1"));
        Assert.Equal(("id", OsduFieldIndex.Keyword, "log_id", "recall"), (log.Path, log.Index, log.Via!.Key!.Given, log.Via.Key.System));
        Assert.Equal(new ExplorerViaColumn { Column = "source_project", Path = "tags.SourceProject", Index = OsduFieldIndex.Keyword }, Assert.Single(log.Via.Key.Others));

        var exists = Resolve("log_run", "osdu.data.LogRun", SearchRouteKind.Copy, new SearchTermCondition(ExplorerCondition.Missing));
        Assert.Equal("NOT (_exists_:data.LogRun)", exists.Clause());
    }

    [Fact]
    public void A_condition_a_route_does_not_take_or_a_value_it_cannot_carry_is_refused_with_why()
    {
        var values = SearchTermValues.For(Mapping, Template, "dev");
        var domain = Route("index_type", "osdu.data.SamplingDomainTypeID", SearchRouteKind.Steps);
        var fields = SearchTermResolver.Classify(domain, Template, null);
        var key = SearchTermKey.Of("recall", WellLog, null, "index_type");

        var words = Assert.Throws<DeliveryException>(() => SearchTermResolver.Resolve("Domain", key, domain, fields, new SearchTermCondition(ExplorerCondition.Contains, "DEP"), values));
        Assert.Contains("not Contains", words.Message, StringComparison.Ordinal);

        var start = Route("index_min", "osdu.data.SamplingStart", SearchRouteKind.Copy);
        var deep = Assert.Throws<DeliveryException>(() => SearchTermResolver.Resolve(
            "Start", SearchTermKey.Of("recall", WellLog, null, "index_min"), start, SearchTermResolver.Classify(start, Template, null), new SearchTermCondition(ExplorerCondition.Is, "deep"), values));
        Assert.Contains("Start 'deep' cannot be searched for", deep.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_route_a_term_is_searched_by_is_the_one_picked_while_it_can_be_else_the_plainest()
    {
        var routes = Term("source_project").Routes;

        Assert.Equal(SearchRouteKind.Copy, SearchTermResolver.Preferred(routes, _ => true, picked: null)!.Kind);
        Assert.Equal(SearchRouteKind.Key, SearchTermResolver.Preferred(routes, _ => true, picked: "id|Key")!.Kind);
        Assert.Equal(SearchRouteKind.Copy, SearchTermResolver.Preferred(routes, r => r.Kind != SearchRouteKind.Key, picked: "id|Key")!.Kind);
        Assert.Null(SearchTermResolver.Preferred(routes, _ => false, picked: null));
    }

    /// <summary>Two finds alike: the same records, lines and read, the lines compared by value.</summary>
    private sealed class FindComparer : IEqualityComparer<SearchRouteFind>
    {
        public bool Equals(SearchRouteFind? x, SearchRouteFind? y)
            => x is not null && y is not null && x.Name == y.Name && x.Kind == y.Kind && x.Read == y.Read && x.Lines.SequenceEqual(y.Lines);

        public int GetHashCode(SearchRouteFind obj) => HashCode.Combine(obj.Name, obj.Kind, obj.Read);
    }
}
