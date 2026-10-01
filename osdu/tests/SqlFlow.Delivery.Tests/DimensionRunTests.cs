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
                unlabelled: Not specified
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

        // A key whose record the search does not hold, or whose record holds no name, is valued by the code its id ends with,
        // and the build says why.
        Assert.Equal((null, "Z"), (keys["dev:master-data--Wellbore:Z:"].Label, keys["dev:master-data--Wellbore:Z:"].MemberValue));
        Assert.Equal((null, "dev:master-data--Wellbore:C"), (keys["dev:master-data--Wellbore:C:"].Label, keys["dev:master-data--Wellbore:C:"].LabelFrom));
        var run = (await ledger.ListDimensionRunsAsync(wellbores.DimensionId, 1)).Single();
        Assert.Equal((2L, 2L, 1), (run.Read.Labelled, run.Read.Unlabelled, run.Read.LabelQueries));
        Assert.Contains(run.Read.Notes, n => n.Contains("the search holds no record they name", StringComparison.Ordinal));
        Assert.Contains(run.Read.Notes, n => n.Contains("holds nothing at the path it is read from", StringComparison.Ordinal));

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

        // The keys whose country is not read are one value a drop-down lists, as the dimension names it, and its filter
        // finds exactly their logs.
        var unspecified = (await MembersAsync(ledger, countries.DimensionId)).Single(m => m.Value == "Not specified");
        Assert.Equal(2, unspecified.Originals);
        Assert.Equal(["dev:work-product-component--WellLog:4", "dev:work-product-component--WellLog:5"], _platform.Find(WellLog, unspecified.Filter!));

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
    public async Task A_key_s_attributes_are_read_through_the_records_it_names_kept_and_looked_up_and_a_search_is_picked_by_them()
    {
        // Wellbores whose ids escape a slash, each in a region and a country, one of them in a field; logs of each. A country
        // is the political entity whose own type is Country, as PetroDB tells it, whatever order a wellbore lists them in.
        const string Norway = "dev:master-data--GeoPoliticalEntity:NO";
        const string Denmark = "dev:master-data--GeoPoliticalEntity:DK";
        const string NorthSea = "dev:master-data--GeoPoliticalEntity:NorthSea";
        JsonObject Entity(string name, string type) => new()
        {
            ["GeoPoliticalEntityName"] = name, ["GeoPoliticalEntityTypeID"] = $"dev:reference-data--GeoPoliticalEntityType:{type}:",
        };
        _platform.Add(Norway, "osdu:wks:master-data--GeoPoliticalEntity:1.0.0", Entity("Norway", "Country"));
        _platform.Add(Denmark, "osdu:wks:master-data--GeoPoliticalEntity:1.0.0", Entity("Denmark", "Country"));
        _platform.Add(NorthSea, "osdu:wks:master-data--GeoPoliticalEntity:1.0.0", Entity("North Sea", "Region"));
        _platform.Add("dev:master-data--Field:STATFJORD", "osdu:wks:master-data--Field:1.0.0", new JsonObject { ["FieldName"] = "Statfjord" });
        JsonObject Context(string entity, string type) => new() { ["GeoPoliticalEntityID"] = entity + ":", ["GeoTypeID"] = $"dev:reference-data--GeoPoliticalEntityType:{type}:" };
        _platform.Add("dev:master-data--Wellbore:15%2F9-A", Wellbore, new JsonObject
        {
            ["FacilityName"] = "NO 15/9-A",
            ["GeoContexts"] = new JsonArray(Context(NorthSea, "Region"), Context(Norway, "Country"), new JsonObject { ["FieldID"] = "dev:master-data--Field:STATFJORD:" }),
        });
        _platform.Add("dev:master-data--Wellbore:15%2F9-B", Wellbore, new JsonObject
        {
            ["FacilityName"] = "NO 15/9-B",
            // Contexts that do not say their type: the entities' own types tell the country.
            ["GeoContexts"] = new JsonArray(new JsonObject { ["GeoPoliticalEntityID"] = NorthSea + ":" }, new JsonObject { ["GeoPoliticalEntityID"] = Norway + ":" }),
        });
        _platform.Add("dev:master-data--Wellbore:5504%2F7-1", Wellbore, new JsonObject
        {
            ["FacilityName"] = "DK 5504/7-1",
            ["GeoContexts"] = new JsonArray(Context(Denmark, "Country")),
        });
        foreach (var (log, wellbore) in new[] { ("1", "15%2F9-A"), ("2", "15%2F9-A"), ("3", "15%2F9-B"), ("4", "5504%2F7-1") })
        {
            _platform.Add($"dev:work-product-component--WellLog:{log}", WellLog, new JsonObject { ["WellboreID"] = $"dev:master-data--Wellbore:{wellbore}:" });
        }

        var (runner, ledger, flow) = await RunnerAsync(Head + """
            dimensions:
              - name: Wellbore
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                label: data.FacilityName
                attributes:
                  Country: [data.GeoContexts.GeoPoliticalEntityID, 'data[GeoPoliticalEntityTypeID*=GeoPoliticalEntityType:Country:].GeoPoliticalEntityName']
                  Field: [data.GeoContexts.FieldID, data.FieldName]
                  Region: ['data.GeoContexts[GeoTypeID$=:Region:].GeoPoliticalEntityID', data.GeoPoliticalEntityName]
              - name: WellboreCode
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
            """);

        var outcome = await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        Assert.Equal((2, 0), (outcome.Built, outcome.Failed));
        var wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        var keys = (await ledger.ListDimensionValuesAsync(wellbores.DimensionId, new DimensionValueQuery(null, null, false, false, null, 10)))
            .ToDictionary(k => k.Original, StringComparer.Ordinal);

        // Each key keeps its attributes: the country the entity whose own type is Country, not the region before it; the
        // region read through the context whose type ends so.
        var a = keys["dev:master-data--Wellbore:15%2F9-A:"];
        Assert.Equal(
            [("Country", "Norway", (string?)Norway), ("Field", "Statfjord", "dev:master-data--Field:STATFJORD"), ("Region", "North Sea", NorthSea)],
            a.Attributes.Select(x => (x.Name, x.Value, x.From)));
        Assert.Equal([("Country", "Norway")], keys["dev:master-data--Wellbore:15%2F9-B:"].Attributes.Select(x => (x.Name, x.Value)));
        Assert.Equal([("Country", "Denmark")], keys["dev:master-data--Wellbore:5504%2F7-1:"].Attributes.Select(x => (x.Name, x.Value)));
        var run = (await ledger.ListDimensionRunsAsync(wellbores.DimensionId, 1)).Single();
        Assert.Contains(run.Read.Notes, n => n.Contains("key(s) have no Field", StringComparison.Ordinal));

        // A key naming a record with no label is valued by the code its id ends with, its escapes decoded.
        var codes = (await ledger.FindDimensionAsync(flow.LedgerId, "WellboreCode"))!;
        Assert.Equal(["15/9-A", "15/9-B", "5504/7-1"], (await MembersAsync(ledger, codes.DimensionId)).Select(m => m.Value));

        // Values and keys are looked up by their attributes, and an attribute lists its values with their keys and records.
        var norwegian = await ledger.ListDimensionMembersAsync(
            wellbores.DimensionId, new DimensionMemberQuery(null, false, null, 10, DimensionMemberOrder.Value, [new DimensionAttributeMatch("Country", ["Norway"])]));
        Assert.Equal(["NO 15/9-A", "NO 15/9-B"], norwegian.Select(m => m.Value));
        Assert.Contains(norwegian[0].Attributes, x => x is { Name: "Country", Value: "Norway", Keys: 1 });
        var both = await ledger.ListDimensionValuesAsync(
            wellbores.DimensionId,
            new DimensionValueQuery(null, null, false, false, null, 10, DimensionValueOrder.Arrival,
                [new DimensionAttributeMatch("Country", ["Norway", "Denmark"]), new DimensionAttributeMatch("Field", ["Statfjord"])]));
        Assert.Equal(["dev:master-data--Wellbore:15%2F9-A:"], both.Select(k => k.Original));
        Assert.Equal(
            [("Norway", 2, 3L), ("Denmark", 1, 1L)],
            (await ledger.ListDimensionAttributeValuesAsync(wellbores.DimensionId, new DimensionAttributeValueQuery("Country", null, 10))).Select(v => (v.Value, v.Keys, v.Records)));

        // A search picked by an attribute finds the logs of every wellbore holding it.
        var inNorway = await DimensionSearch.ComposeAsync(
            ledger, [new DimensionPick(wellbores, [], [], [new DimensionAttributeMatch("country", ["Norway"])])], null, null, CancellationToken.None);
        Assert.Equal(
            ["dev:work-product-component--WellLog:1", "dev:work-product-component--WellLog:2", "dev:work-product-component--WellLog:3"],
            _platform.Find(WellLog, inNorway.Query));
        Assert.Equal(["NO 15/9-A", "NO 15/9-B"], Assert.Single(inNorway.Parts).Values.Select(v => v.Value).Order(StringComparer.Ordinal));
        var unknown = await Assert.ThrowsAsync<DeliveryException>(() => DimensionSearch.ComposeAsync(
            ledger, [new DimensionPick(wellbores, [], [], [new DimensionAttributeMatch("Basin", ["X"])])], null, null, CancellationToken.None));
        Assert.Contains("reads no attribute 'Basin'; it reads Country, Field, Region", unknown.Message, StringComparison.Ordinal);

        // A second build reads the attributes again: a country renamed is rewritten, a field no longer named is dropped.
        _platform.Records.Single(r => r["id"]!.GetValue<string>() == Norway)["data"]!["GeoPoliticalEntityName"] = "Kingdom of Norway";
        var first = _platform.Records.Single(r => r["id"]!.GetValue<string>() == "dev:master-data--Wellbore:15%2F9-A")["data"]!.AsObject();
        first["GeoContexts"] = new JsonArray(Context(NorthSea, "Region"), Context(Norway, "Country"));
        await runner.BuildAsync(["Wellbore"], Guid.NewGuid(), "tests", CancellationToken.None);
        var again = (await ledger.ListDimensionValuesAsync(wellbores.DimensionId, new DimensionValueQuery(null, null, false, false, null, 10)))
            .Single(k => k.Original == "dev:master-data--Wellbore:15%2F9-A:");
        Assert.Equal([("Country", "Kingdom of Norway"), ("Region", "North Sea")], again.Attributes.Select(x => (x.Name, x.Value)));
        var second = (await ledger.ListDimensionRunsAsync(wellbores.DimensionId, 1)).Single();
        Assert.Contains(second.Read.Notes, n => n.Contains("attribute value(s) of keys were added, rewritten or dropped", StringComparison.Ordinal));
        OsduContracts.AssertConform(_platform.Calls, null, OsduContracts.Search);
    }

    [Fact]
    public async Task A_wellbore_collects_its_logs_sources_and_its_table_feeds_cascading_selects()
    {
        // Two countries, two fields, and logs from two sources, one spelled with a trailing space; a log without a source,
        // and a wellbore no record describes.
        _platform.Add("dev:master-data--GeoPoliticalEntity:NO", "osdu:wks:master-data--GeoPoliticalEntity:1.0.0", new JsonObject { ["GeoPoliticalEntityName"] = "Norway" });
        _platform.Add("dev:master-data--GeoPoliticalEntity:DK", "osdu:wks:master-data--GeoPoliticalEntity:1.0.0", new JsonObject { ["GeoPoliticalEntityName"] = "Denmark" });
        _platform.Add("dev:master-data--Field:STATFJORD", "osdu:wks:master-data--Field:1.0.0", new JsonObject { ["FieldName"] = "Statfjord" });
        _platform.Add("dev:master-data--Field:TYRA", "osdu:wks:master-data--Field:1.0.0", new JsonObject { ["FieldName"] = "Tyra" });
        JsonObject Described(string name, string country, string? field)
        {
            var contexts = new JsonArray(new JsonObject { ["GeoPoliticalEntityID"] = $"dev:master-data--GeoPoliticalEntity:{country}:" });
            if (field is not null)
            {
                contexts.Add(new JsonObject { ["FieldID"] = $"dev:master-data--Field:{field}:" });
            }

            return new JsonObject { ["FacilityName"] = name, ["GeoContexts"] = contexts };
        }

        _platform.Add("dev:master-data--Wellbore:A", Wellbore, Described("NO A", "NO", "STATFJORD"));
        _platform.Add("dev:master-data--Wellbore:B", Wellbore, Described("NO B", "NO", null));
        _platform.Add("dev:master-data--Wellbore:C", Wellbore, Described("DK C", "DK", "TYRA"));
        foreach (var (log, wellbore, source) in new[]
        {
            ("1", "A", "RECALL"), ("2", "A", "RECALL"), ("3", "A", "PETREL"), ("4", "B", "RECALL"), ("5", "B", null), ("6", "C", "PETREL"),
            ("7", "D", null), ("8", "C", "RECALL "),
        })
        {
            var data = new JsonObject { ["WellboreID"] = $"dev:master-data--Wellbore:{wellbore}:" };
            if (source is not null)
            {
                data["Source"] = source;
            }

            _platform.Add($"dev:work-product-component--WellLog:{log}", WellLog, data);
        }

        var (runner, ledger, flow) = await RunnerAsync(Head + """
            dimensions:
              - name: Wellbore
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                label: data.FacilityName
                unlabelled: Not specified
                attributes:
                  Country: [data.GeoContexts.GeoPoliticalEntityID, data.GeoPoliticalEntityName]
                  Field: [data.GeoContexts.FieldID, data.FieldName]
                  Source: { collect: data.Source }
            """);

        var outcome = await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        Assert.Equal((1, 0), (outcome.Built, outcome.Failed));
        var wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        var keys = (await ledger.ListDimensionValuesAsync(wellbores.DimensionId, new DimensionValueQuery(null, null, false, false, null, 10)))
            .ToDictionary(k => k.Original[(k.Original.LastIndexOf(':', k.Original.Length - 2) + 1)..^1], StringComparer.Ordinal);

        // Each wellbore collects the sources of its logs with how many hold each; a source spelled with a trailing space is
        // the same value; the logs holding none are Not specified, as an attribute a wellbore does not hold is.
        IEnumerable<(string, string, long?)> Of(string key) => keys[key].Attributes.Select(x => (x.Name, x.Value, x.Records));
        Assert.Equal([("Country", "Norway", null), ("Field", "Statfjord", null), ("Source", "RECALL", 2L), ("Source", "PETREL", 1L)], Of("A"));
        Assert.Equal([("Country", "Norway", null), ("Field", "Not specified", null), ("Source", "Not specified", 1L), ("Source", "RECALL", 1L)], Of("B"));
        Assert.Equal([("Country", "Denmark", null), ("Field", "Tyra", null), ("Source", "PETREL", 1L), ("Source", "RECALL", 1L)], Of("C"));
        Assert.Equal([("Country", "Not specified", null), ("Field", "Not specified", null), ("Source", "Not specified", 1L)], Of("D"));
        Assert.Equal("Not specified", keys["D"].MemberValue);

        // The dimension keeps what each source value stands for, which a search picking it asks for.
        var collected = Assert.Single(DimensionRunner.CollectedOf(wellbores.CollectedJson));
        Assert.Equal(("Source", "data.Source", "Not specified"), (collected.Name, collected.Path, collected.Missing));
        Assert.Equal(
            [("PETREL", "PETREL", 2L), ("RECALL", "RECALL|RECALL ", 4L)],
            collected.Values.Select(v => (v.Value, string.Join('|', v.Texts), v.Records)));

        // Each list of a cascade lists what the other picks leave, its own pick aside.
        async Task<IEnumerable<(string, int, long)>> ListAsync(string name, IReadOnlyList<DimensionAttributeMatch>? picks = null, IReadOnlyCollection<long>? values = null)
            => (await ledger.ListDimensionAttributeValuesAsync(wellbores.DimensionId, new DimensionAttributeValueQuery(name, null, 10, picks, values)))
                .Select(v => (v.Value, v.Keys, v.Records));
        DimensionAttributeMatch Pick(string name, params string[] values) => new(name, values);
        Assert.Equal([("Statfjord", 1, 3L), ("Not specified", 1, 2L)], await ListAsync("Field", [Pick("Country", "Norway")]));
        Assert.Equal([("RECALL", 2, 3L), ("Not specified", 1, 1L), ("PETREL", 1, 1L)], await ListAsync("Source", [Pick("Country", "Norway")]));
        Assert.Equal([("Norway", 1, 3L), ("Denmark", 1, 2L)], await ListAsync("Country", [Pick("Source", "PETREL")]));
        Assert.Equal(await ListAsync("Country", [Pick("Source", "PETREL")]), await ListAsync("Country", [Pick("Country", "Norway"), Pick("Source", "PETREL")]));
        var a = (await ledger.GetDimensionMembersAsync(wellbores.DimensionId, [], ["NO A"])).Single();
        Assert.Equal([("RECALL", 1, 2L), ("PETREL", 1, 1L)], await ListAsync("Source", null, [a.MemberId]));

        // The table: a row per wellbore and source, each with its country, field and records, what cascading selects read.
        var table = new StringWriter();
        var rows = await DimensionExport.WriteAsync(ledger, wellbores, DimensionExportSet.Table, DimensionExportFormat.Csv, table, CancellationToken.None);
        var lines = table.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("key,value,Country,Field,Source,records", lines[0]);
        Assert.Equal(7, rows);
        Assert.Equal(
            [
                "dev:master-data--Wellbore:A:,NO A,Norway,Statfjord,PETREL,1",
                "dev:master-data--Wellbore:A:,NO A,Norway,Statfjord,RECALL,2",
                "dev:master-data--Wellbore:B:,NO B,Norway,Not specified,Not specified,1",
                "dev:master-data--Wellbore:B:,NO B,Norway,Not specified,RECALL,1",
                "dev:master-data--Wellbore:C:,DK C,Denmark,Tyra,PETREL,1",
                "dev:master-data--Wellbore:C:,DK C,Denmark,Tyra,RECALL,1",
                "dev:master-data--Wellbore:D:,Not specified,Not specified,Not specified,Not specified,1",
            ],
            lines.Skip(1).Order(StringComparer.Ordinal));

        // A search picking a source finds the logs holding it, however it is spelled, not every log of the wellbores holding
        // it; Not specified finds the logs holding none; and a source picked with a country, the country's logs holding it.
        async Task<IReadOnlyList<string>> FindAsync(params DimensionAttributeMatch[] picks)
        {
            var set = await DimensionSearch.ComposeAsync(ledger, [new DimensionPick(wellbores, [], [], picks)], null, null, CancellationToken.None);
            return _platform.Find(WellLog, set.Query).Select(id => id[(id.LastIndexOf(':') + 1)..]).ToList();
        }

        Assert.Equal(["1", "2", "4", "8"], await FindAsync(Pick("Source", "RECALL")));
        Assert.Equal(["5", "7"], await FindAsync(Pick("Source", "Not specified")));
        Assert.Equal(["3", "5", "6", "7"], await FindAsync(Pick("Source", "PETREL", "Not specified")));
        Assert.Equal(["1", "2", "4"], await FindAsync(Pick("Country", "Norway"), Pick("Source", "RECALL")));
        var some = await DimensionSearch.ComposeAsync(
            ledger, [new DimensionPick(wellbores, [], [], [Pick("Source", "RECALL", "NOPE")])], null, null, CancellationToken.None);
        Assert.Equal(["Wellbore.Source: NOPE"], some.Missing);
        var none = await Assert.ThrowsAsync<DeliveryException>(() => FindAsync(Pick("Source", "NOPE")));
        Assert.Contains("No value picked of attribute Source of dimension Wellbore is one it collects", none.Message, StringComparison.Ordinal);

        // A second build collects again: a wellbore whose last PETREL log became RECALL holds RECALL alone.
        _platform.Records.Single(r => r["id"]!.GetValue<string>() == "dev:work-product-component--WellLog:3")["data"]!["Source"] = "RECALL";
        await runner.BuildAsync(["Wellbore"], Guid.NewGuid(), "tests", CancellationToken.None);
        var again = (await ledger.ListDimensionValuesAsync(wellbores.DimensionId, new DimensionValueQuery(null, null, false, false, null, 10)))
            .Single(k => k.Original == "dev:master-data--Wellbore:A:");
        Assert.Equal([("Source", "RECALL", (long?)3L)], again.Attributes.Where(x => x.Name == "Source").Select(x => (x.Name, x.Value, x.Records)));
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
