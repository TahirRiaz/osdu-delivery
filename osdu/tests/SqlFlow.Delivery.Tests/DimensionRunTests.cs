using System.Text.Json.Nodes;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A dimension flow's build end to end (docs/dimension-plan.md): the field settled from the saved templates or the record's
/// own mapping, every distinct value read from a platform whose aggregation returns few groups at a time, cleaned into
/// members with their filters, and kept in the ledger on SQL Server; a dimension that cannot be built failing alone; and the
/// plan. Every request the build makes is held to the pinned search contract.
/// </summary>
[Collection(SqlServerSuite.Name)]
public sealed class DimensionRunTests : IDisposable
{
    private const string WellLog = Samples.WellLogKind;

    private const string Wellbore = Samples.WellboreKind;

    private const string Head = """
        flowType: dimension
        name: wells-dimensions
        partitions: [dev]
        source:
          endpoint: http://localhost
          aggregationSize: 50
        reliability: { concurrency: 2, retry: { attempts: 1, baseDelayMs: 1, maxDelayMs: 1 } }

        """;

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly FakeDimensionPlatform _platform = new() { AggregationSize = 50 };

    public void Dispose()
    {
        _db.Dispose();
        _platform.Dispose();
    }

    private async Task<(DimensionRunner Runner, OsduLedger Ledger, DimensionFlowDefinition Flow)> RunnerAsync(string yaml, string source = "flows/dims.yaml")
    {
        var ledger = _db.Ledger(_clock);
        var templates = _db.Templates(_clock);
        await Samples.ImportSampleTemplatesAsync(templates);
        var engine = Samples.Engine(ledger, _clock, templates: templates);
        var flow = new DeliveryDocumentLoader().ParseDimension(yaml, source).ForPartition("dev");
        return (new DimensionRunner(engine, flow, new Dictionary<string, string>(), Samples.Logger<DimensionRunner>(), _platform, allowLoopback: true), ledger, flow);
    }

    /// <summary>Well logs, each holding curves whose mnemonics are spelled several ways.</summary>
    private void Logs(int logs, int curvesPerLog, int mnemonics)
    {
        var seen = new Dictionary<int, int>();
        for (var log = 0; log < logs; log++)
        {
            var curves = new JsonArray();
            for (var curve = 0; curve < curvesPerLog; curve++)
            {
                var n = ((log * curvesPerLog) + curve) % mnemonics;
                // Each mnemonic spelled in upper case, and every third one in lower case every other time it occurs.
                var occurrence = seen[n] = seen.GetValueOrDefault(n) + 1;
                var spelled = n % 3 == 0 && occurrence % 2 == 0 ? $"m{n:D4}" : $"M{n:D4}";
                curves.Add(new JsonObject { ["Mnemonic"] = spelled });
            }

            _platform.Add($"dev:work-product-component--WellLog:{log}", WellLog, new JsonObject { ["Curves"] = curves });
        }
    }

    private static async Task<IReadOnlyList<DimensionMemberState>> MembersAsync(OsduLedger ledger, int dimensionId)
    {
        var members = new List<DimensionMemberState>();
        DimensionMemberCursor? after = null;
        while (true)
        {
            var page = await ledger.ListDimensionMembersAsync(dimensionId, new DimensionMemberQuery(null, false, after, OsduLedger.MaxDimensionPage));
            members.AddRange(page);
            if (page.Count < OsduLedger.MaxDimensionPage)
            {
                return members;
            }

            after = new DimensionMemberCursor(page[^1].Value, page[^1].Records);
        }
    }

    [Fact]
    public async Task Every_value_of_a_nested_array_is_read_past_the_aggregation_limit_cleaned_and_kept_with_its_filter()
    {
        // More logs than one cursor page, so the first cut-off range is split rather than scanned.
        Logs(logs: 3_000, curvesPerLog: 3, mnemonics: 1_500);
        var (runner, ledger, flow) = await RunnerAsync(Head + $$"""
            dimensions:
              - name: CurveMnemonic
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.Curves.Mnemonic
                clean: [trim, upper]
            """);

        var outcome = await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        var spellings = _platform.Records
            .SelectMany(r => r["data"]!["Curves"]!.AsArray().Select(c => c!["Mnemonic"]!.GetValue<string>()))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var built = Assert.Single(outcome.Dimensions);
        Assert.Equal(DimensionRunStatus.Completed, built.Status);
        Assert.Equal("nested(data.Curves, Mnemonic.keyword)", built.AggregateBy);
        Assert.Equal(1_500, built.Values);
        Assert.Equal(spellings.Count, built.Keys);
        Assert.True(spellings.Count > 1_500, "some mnemonics are spelled more than one way");
        Assert.Equal((1, 0), (outcome.Built, outcome.Failed));

        var dimension = (await ledger.FindDimensionAsync(flow.LedgerId, "CurveMnemonic"))!;
        var members = await MembersAsync(ledger, dimension.DimensionId);
        Assert.Equal(1_500, members.Count);
        var twice = spellings.Where(s => s[0] == 'm').Select(s => "M" + s[1..]).First(upper => spellings.Contains(upper));
        var member = members.Single(m => m.Value == twice);
        Assert.Equal(2, member.Originals);
        Assert.Equal(1, member.FilterParts);
        Assert.Equal(
            $"(nested(data.Curves, (Mnemonic.keyword:\"{twice}\"))) OR (nested(data.Curves, (Mnemonic.keyword:\"m{twice[1..]}\")))",
            member.Filter);
        Assert.False(member.RecordsExact);

        var run = (await ledger.ListDimensionRunsAsync(dimension.DimensionId, 1)).Single();
        Assert.True(run.Read.Splits > 0, "the values could only be read past the limit by splitting");
        Assert.Contains(WellLog, run.Read.Templates, StringComparison.Ordinal);
        OsduContracts.AssertConform(_platform.Calls, null, OsduContracts.Search);
    }

    [Fact]
    public async Task A_second_build_keeps_what_did_not_change_and_logs_what_did()
    {
        Logs(logs: 20, curvesPerLog: 2, mnemonics: 10);
        var yaml = Head + """
            dimensions:
              - name: CurveMnemonic
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.Curves.Mnemonic
            """;
        var (runner, ledger, flow) = await RunnerAsync(yaml);
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        _platform.Records.RemoveAll(r => r["data"]!["Curves"]!.AsArray().Any(c => c!["Mnemonic"]!.GetValue<string>() is "M0000" or "m0000"));
        _platform.Add("dev:work-product-component--WellLog:new", WellLog, new JsonObject { ["Curves"] = new JsonArray(new JsonObject { ["Mnemonic"] = "NEW" }) });
        var second = await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        var changes = Assert.Single(second.Dimensions).Changes;
        Assert.Equal(1, changes.KeysAdded);
        Assert.True(changes.KeysRemoved >= 1);
        var dimension = (await ledger.FindDimensionAsync(flow.LedgerId, "CurveMnemonic"))!;
        Assert.Contains(await ledger.ListDimensionChangesAsync(dimension.DimensionId, new DimensionChangeQuery(null, null, null, null, null, 100)), c => c.Original == "NEW" && c.Change == DimensionChangeKinds.Added);
    }

    [Fact]
    public async Task A_key_naming_a_record_is_valued_by_the_label_read_there_and_every_key_and_value_carries_the_search_finding_it()
    {
        // Two wellbores in Norway, one of them in Statfjord, and one with no name; logs of each, and a log of a wellbore the
        // partition does not hold.
        const string Norway = "dev:master-data--GeoPoliticalEntity:Norway";
        _platform.Add(Norway, "osdu:wks:master-data--GeoPoliticalEntity:1.0.0", new JsonObject { ["GeoPoliticalEntityName"] = "Norway" });
        _platform.Add("dev:master-data--Wellbore:A", Wellbore, new JsonObject
        {
            ["FacilityName"] = "15/9-A",
            ["GeoContexts"] = new JsonArray(
                new JsonObject { ["FieldID"] = "dev:master-data--Field:STATFJORD:" },
                new JsonObject { ["GeoPoliticalEntityID"] = Norway + ":" }),
        });
        _platform.Add("dev:master-data--Wellbore:B", Wellbore, new JsonObject
        {
            ["FacilityName"] = "15/9-B",
            ["GeoContexts"] = new JsonArray(new JsonObject { ["GeoPoliticalEntityID"] = Norway + ":" }),
        });
        _platform.Add("dev:master-data--Wellbore:C", Wellbore, new JsonObject { ["FacilityName"] = "  " });
        foreach (var (log, wellbore) in new[] { ("1", "A"), ("2", "A"), ("3", "B"), ("4", "C"), ("5", "Z") })
        {
            _platform.Add($"dev:work-product-component--WellLog:{log}", WellLog, new JsonObject { ["WellboreID"] = $"dev:master-data--Wellbore:{wellbore}:" });
        }

        var (runner, ledger, flow) = await RunnerAsync(Head + """
            dimensions:
              - name: Wellbore
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                label: data.FacilityName
              - name: Country
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                label: [data.GeoContexts.GeoPoliticalEntityID, data.GeoPoliticalEntityName]
            """);

        var outcome = await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        Assert.Equal((2, 0), (outcome.Built, outcome.Failed));
        Assert.Equal([2L, 2L], outcome.Dimensions.Select(d => d.Labelled));
        var wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        var keys = (await ledger.ListDimensionValuesAsync(wellbores.DimensionId, new DimensionValueQuery(null, null, false, false, null, 10)))
            .ToDictionary(k => k.Original, StringComparer.Ordinal);

        // A key is the reference exactly as the index holds it; its value the name the wellbore holds.
        var a = keys["dev:master-data--Wellbore:A:"];
        Assert.Equal(("15/9-A", "dev:master-data--Wellbore:A", "15/9-A"), (a.Label, a.LabelFrom, a.MemberValue));

        // A key whose record the search does not hold, or whose record holds no name, is its own value, and the build says why.
        Assert.Equal((null, "dev:master-data--Wellbore:Z:"), (keys["dev:master-data--Wellbore:Z:"].Label, keys["dev:master-data--Wellbore:Z:"].MemberValue));
        Assert.Equal((null, "dev:master-data--Wellbore:C"), (keys["dev:master-data--Wellbore:C:"].Label, keys["dev:master-data--Wellbore:C:"].LabelFrom));
        var run = (await ledger.ListDimensionRunsAsync(wellbores.DimensionId, 1)).Single();
        Assert.Equal((2L, 2L, 1), (run.Read.Labelled, run.Read.Unlabelled, run.Read.LabelQueries));
        Assert.Contains(run.Read.Notes, n => n.Contains("the search holds no record they name", StringComparison.Ordinal));
        Assert.Contains(run.Read.Notes, n => n.Contains("holds nothing at the label's path", StringComparison.Ordinal));

        // Each key's filter finds exactly the records holding it, and so does its value's.
        Assert.Equal(["dev:work-product-component--WellLog:1", "dev:work-product-component--WellLog:2"], _platform.Find(WellLog, a.Filter!));
        var named = (await MembersAsync(ledger, wellbores.DimensionId)).Single(m => m.Value == "15/9-A");
        Assert.Equal(["dev:work-product-component--WellLog:1", "dev:work-product-component--WellLog:2"], _platform.Find(WellLog, named.Filter!));

        // A label read through two records: the wellbore's country reference, then the country's name.
        var countries = (await ledger.FindDimensionAsync(flow.LedgerId, "Country"))!;
        var norway = (await MembersAsync(ledger, countries.DimensionId)).Single(m => m.Value == "Norway");
        Assert.Equal((2, 3L), (norway.Originals, norway.Records));
        var country = (await ledger.ListDimensionRunsAsync(countries.DimensionId, 1)).Single();
        Assert.Equal((2L, 2L, 2), (country.Read.Labelled, country.Read.Unlabelled, country.Read.LabelQueries));
        var ofNorway = await ledger.ListDimensionValuesAsync(countries.DimensionId, new DimensionValueQuery(null, norway.MemberId, false, false, null, 10));
        Assert.All(ofNorway, k => Assert.Equal(Norway, k.LabelFrom));

        // The search across both: logs of a Norwegian wellbore named 15/9-A; across one, every log of Norway.
        var picked = await DimensionSearch.ComposeAsync(
            ledger, [new DimensionPick(countries, [], ["Norway"]), new DimensionPick(wellbores, [], ["15/9-A"])], null, null, CancellationToken.None);
        Assert.Equal(WellLog.Replace("1.4.0", "*", StringComparison.Ordinal), picked.Kind);
        Assert.Equal(["dev:work-product-component--WellLog:1", "dev:work-product-component--WellLog:2"], _platform.Find(WellLog, picked.Query));
        Assert.Equal(3, picked.Clauses);
        var all = await DimensionSearch.ComposeAsync(ledger, [new DimensionPick(countries, [], ["Norway"])], null, null, CancellationToken.None);
        Assert.Equal(3, _platform.Find(WellLog, all.Query).Count);

        // Narrowed by a query of its own, the search still finds only what every part allows.
        var within = await DimensionSearch.ComposeAsync(
            ledger, [new DimensionPick(countries, [], ["Norway"])], null, "id:\"dev:work-product-component--WellLog:3\"", CancellationToken.None);
        Assert.Equal(["dev:work-product-component--WellLog:3"], _platform.Find(WellLog, within.Query));
        OsduContracts.AssertConform(_platform.Calls, null, OsduContracts.Search);
    }

    [Fact]
    public async Task A_property_of_the_record_itself_is_read_for_every_kind_and_counted_exactly_when_asked()
    {
        _platform.Add("dev:master-data--Wellbore:1", Wellbore, new JsonObject(), legalTags: ["dev-a", "dev-b"]);
        _platform.Add("dev:master-data--Wellbore:2", Wellbore, new JsonObject(), legalTags: ["dev-a"]);
        _platform.Add("dev:master-data--Wellbore:3", Wellbore, new JsonObject(), legalTags: ["DEV-A", "dev-b"]);
        var (runner, ledger, flow) = await RunnerAsync(Head + """
            dimensions:
              - name: LegalTag
                kind: "osdu:wks:master-data--Wellbore:*"
                path: legal.legaltags
                countRecords: true
                clean: [lower]
            """);

        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        var dimension = (await ledger.FindDimensionAsync(flow.LedgerId, "LegalTag"))!;
        var members = await MembersAsync(ledger, dimension.DimensionId);
        var a = members.Single(m => m.Value == "dev-a");
        Assert.Equal((3L, true, 2), (a.Records, a.RecordsExact, a.Originals));
        var b = members.Single(m => m.Value == "dev-b");
        Assert.Equal((2L, true), (b.Records, b.RecordsExact));
        Assert.Equal("legal.legaltags:(\"DEV-A\" OR \"dev-a\")", a.Filter);
        Assert.Equal(new DimensionFieldState("keyword", null, "legal.legaltags", true), dimension.Field);
        OsduContracts.AssertConform(_platform.Calls, null, OsduContracts.Search);
    }

    [Fact]
    public async Task A_dimension_that_cannot_be_built_fails_alone_and_says_why()
    {
        _platform.Add("dev:master-data--Wellbore:1", Wellbore, new JsonObject { ["FacilityName"] = "A" });
        _platform.Add("dev:master-data--Field:1", "osdu:wks:master-data--Field:1.0.0", new JsonObject { ["FieldName"] = "Troll" });
        var (runner, ledger, flow) = await RunnerAsync(Head + """
            dimensions:
              - name: WellboreName
                kind: "osdu:wks:master-data--Wellbore:*"
                path: data.FacilityName
              - name: FieldName
                kind: "osdu:wks:master-data--Field:*"
                path: data.FieldName
            """);

        var failed = await Assert.ThrowsAsync<DimensionBuildsFailedException>(() => runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None));

        Assert.Equal((1, 1), (failed.Outcome.Built, failed.Outcome.Failed));
        var field = failed.Outcome.Dimensions.Single(d => d.Dimension == "FieldName");
        Assert.Contains("osdu:wks:master-data--Field:1.0.0 (1 record(s))", field.Error, StringComparison.Ordinal);
        Assert.Contains("sqlflow template capture", field.Error, StringComparison.Ordinal);
        var names = (await ledger.FindDimensionAsync(flow.LedgerId, "WellboreName"))!;
        Assert.Equal(1, names.Members);
        var fieldName = (await ledger.FindDimensionAsync(flow.LedgerId, "FieldName"))!;
        Assert.Null(fieldName.LastRunId);
        Assert.Equal(DimensionRunStatus.Failed, (await ledger.ListDimensionRunsAsync(fieldName.DimensionId, 1)).Single().Status);
    }

    [Fact]
    public async Task A_kind_the_partition_holds_no_record_of_builds_a_dimension_with_no_value_and_says_so()
    {
        var (runner, ledger, flow) = await RunnerAsync(Head + """
            dimensions:
              - name: WellboreName
                kind: "osdu:wks:master-data--Wellbore:*"
                path: data.FacilityName
            """);

        var outcome = await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        var built = Assert.Single(outcome.Dimensions);
        Assert.Equal((DimensionRunStatus.Completed, 0L), (built.Status, built.Values));
        Assert.Contains(built.Notes, n => n.Contains("No record of kind", StringComparison.Ordinal));
        Assert.Null((await ledger.FindDimensionAsync(flow.LedgerId, "WellboreName"))!.Field);
    }

    [Fact]
    public async Task Values_are_mapped_through_a_dictionary_found_beside_the_flow()
    {
        var root = Samples.NewTempDirectory();
        Directory.CreateDirectory(Path.Combine(root, "dictionaries"));
        Directory.CreateDirectory(Path.Combine(root, "flows"));
        await File.WriteAllTextAsync(Path.Combine(root, "dictionaries", "Operators.yaml"), """
            documentType: dictionary
            name: Operators
            entries:
              Statoil: Equinor ASA
              Equinor: Equinor ASA
              Retired: ~
            """);
        foreach (var (id, name) in new[] { ("1", "Statoil"), ("2", "EQUINOR"), ("3", "Aker BP"), ("4", "Retired"), ("5", "  ") })
        {
            _platform.Add($"dev:master-data--Wellbore:{id}", Wellbore, new JsonObject { ["FacilityName"] = name });
        }

        var (runner, ledger, flow) = await RunnerAsync(Head + """
            dimensions:
              - name: Operator
                kind: "osdu:wks:master-data--Wellbore:*"
                path: data.FacilityName
                clean:
                  - trim
                  - map: { dictionary: Operators }
            """, Path.Combine(root, "flows", "dims.yaml"));

        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        var dimension = (await ledger.FindDimensionAsync(flow.LedgerId, "Operator"))!;
        Assert.Equal(["Aker BP", "Equinor ASA"], (await MembersAsync(ledger, dimension.DimensionId)).Select(m => m.Value));
        var left = await ledger.ListDimensionValuesAsync(dimension.DimensionId, new DimensionValueQuery(null, null, LeftOutOnly: true, false, null, 10));
        Assert.Equal(
            [("  ", DimensionLeftOut.Empty), ("Retired", DimensionLeftOut.Dropped)],
            left.Select(v => (v.Original, v.LeftOut!)).OrderBy(v => v.Original, StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_number_the_service_names_no_keys_for_is_read_by_scanning()
    {
        _platform.NumberKeysMissing = true;
        _platform.Add("dev:master-data--Wellbore:1", Wellbore, new JsonObject()).AsObject()["version"] = 11;
        _platform.Add("dev:master-data--Wellbore:2", Wellbore, new JsonObject()).AsObject()["version"] = 12;
        _platform.Add("dev:master-data--Wellbore:3", Wellbore, new JsonObject()).AsObject()["version"] = 12;
        var (runner, ledger, flow) = await RunnerAsync(Head + """
            dimensions:
              - name: Version
                kind: "osdu:wks:master-data--Wellbore:*"
                path: version
            """);

        var outcome = await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        Assert.Contains(outcome.Dimensions[0].Notes, n => n.Contains("scanning", StringComparison.Ordinal));
        var dimension = (await ledger.FindDimensionAsync(flow.LedgerId, "Version"))!;
        var members = await MembersAsync(ledger, dimension.DimensionId);
        Assert.Equal([("11", 1L), ("12", 2L)], members.Select(m => (m.Value, m.Records)));
        Assert.Equal("version:\"12\"", members[1].Filter);
    }

    [Fact]
    public async Task A_run_builds_only_the_dimensions_it_names()
    {
        _platform.Add("dev:master-data--Wellbore:1", Wellbore, new JsonObject { ["FacilityName"] = "A" });
        var (runner, ledger, flow) = await RunnerAsync(Head + """
            dimensions:
              - { name: Names, kind: "osdu:wks:master-data--Wellbore:*", path: data.FacilityName }
              - { name: Kinds, kind: "osdu:wks:*:*", path: kind }
            """);

        var outcome = await runner.BuildAsync(["kinds"], Guid.NewGuid(), "tests", CancellationToken.None);

        Assert.Equal(["Kinds"], outcome.Dimensions.Select(d => d.Dimension));
        Assert.Null(await ledger.FindDimensionAsync(flow.LedgerId, "Names"));
    }

    [Fact]
    public async Task A_plan_settles_each_field_and_counts_each_dimensions_records_and_keeps_nothing()
    {
        Logs(logs: 5, curvesPerLog: 2, mnemonics: 4);
        var (runner, ledger, flow) = await RunnerAsync(Head + """
            dimensions:
              - { name: CurveMnemonic, kind: "osdu:wks:work-product-component--WellLog:*", path: data.Curves.Mnemonic }
              - { name: FieldName, kind: "osdu:wks:master-data--Field:*", path: data.FieldName }
            """);

        var plan = await runner.PlanAsync([], CancellationToken.None);

        var curves = plan.Dimensions.Single(d => d.Dimension == "CurveMnemonic");
        Assert.Equal((5L, "nested(data.Curves, Mnemonic.keyword)", true), (curves.Records!.Value, curves.AggregateBy, curves.Repeats!.Value));
        Assert.Equal(WellLog, Assert.Single(curves.Kinds).Kind);
        Assert.Empty(curves.Problems);
        var field = plan.Dimensions.Single(d => d.Dimension == "FieldName");
        Assert.Equal(0, field.Records);
        Assert.NotEmpty(field.Problems);
        Assert.Empty(await ledger.ListDimensionsAsync("dev", null));
    }

    [Fact]
    public async Task A_platform_that_fails_part_way_fails_the_dimension_and_writes_none_of_it()
    {
        Logs(logs: 100, curvesPerLog: 4, mnemonics: 300);
        var (runner, ledger, flow) = await RunnerAsync(Head + """
            dimensions:
              - { name: CurveMnemonic, kind: "osdu:wks:work-product-component--WellLog:*", path: data.Curves.Mnemonic }
            """);
        var calls = 0;
        _platform.Fail = _ => ++calls > 4 ? System.Net.HttpStatusCode.ServiceUnavailable : null;

        var failed = await Assert.ThrowsAsync<DimensionBuildsFailedException>(() => runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None));

        Assert.Contains("503", failed.Outcome.Dimensions[0].Error, StringComparison.Ordinal);
        var dimension = (await ledger.FindDimensionAsync(flow.LedgerId, "CurveMnemonic"))!;
        Assert.Equal(0, dimension.Members);
        var run = (await ledger.ListDimensionRunsAsync(dimension.DimensionId, 1)).Single();
        Assert.Equal(DimensionRunStatus.Failed, run.Status);
        Assert.NotNull(run.CompletedUtc);
    }
}
