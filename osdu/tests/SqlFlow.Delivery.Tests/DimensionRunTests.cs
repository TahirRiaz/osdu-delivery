using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;
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

        // Few logs: the sources were read in one pass of the cursor. The dimension keeps what each source value stands for,
        // which a search picking it asks for.
        Assert.Contains(_platform.Calls, c => c.Uri.AbsolutePath.EndsWith("/query_with_cursor", StringComparison.Ordinal));
        var collected = Assert.Single(DimensionRunner.CollectedOf(wellbores.CollectedJson));
        Assert.Equal(("Source", "data.Source", "Not specified"), (collected.Name, collected.Path, collected.Missing));
        Assert.Equal(
            [("PETREL", "PETREL", 2L), ("RECALL", "RECALL", 3L), ("RECALL", "RECALL ", 1L)],
            (await ledger.ListDimensionCollectedTextsAsync(wellbores.DimensionId, "Source", null, 10)).Select(v => (v.Value, v.Text, v.Records)));

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
        // The export is the dimension's own table written out: each row under its number, with the key's number, in the
        // partition, and the key's filter at the end of the fixed columns.
        var table = new StringWriter();
        var rows = await DimensionExport.WriteAsync(ledger, wellbores, DimensionExportSet.Table, DimensionExportFormat.Csv, table, CancellationToken.None);
        var lines = table.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("id,partition,key_id,WellboreID,FacilityName,Country,Field,Source,records,filter", lines[0]);
        Assert.Equal(7, rows);
        string Filter(string key) => "\"" + keys[key].Filter!.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        string Row(int id, string key, string rest) => $"{id},dev,{keys[key].ValueId},dev:master-data--Wellbore:{key}:,{rest}," + Filter(key);
        Assert.Equal(
            [
                Row(1, "C", "DK C,Denmark,Tyra,PETREL,1"),
                Row(2, "C", "DK C,Denmark,Tyra,RECALL,1"),
                Row(3, "A", "NO A,Norway,Statfjord,PETREL,1"),
                Row(4, "A", "NO A,Norway,Statfjord,RECALL,2"),
                Row(5, "B", "NO B,Norway,Not specified,Not specified,1"),
                Row(6, "B", "NO B,Norway,Not specified,RECALL,1"),
                Row(7, "D", "Not specified,Not specified,Not specified,Not specified,1"),
            ],
            lines.Skip(1));
        var jsonl = new StringWriter();
        await DimensionExport.WriteAsync(ledger, wellbores, DimensionExportSet.Table, DimensionExportFormat.JsonLines, jsonl, CancellationToken.None);
        var firstRow = JsonNode.Parse(jsonl.ToString().Split("\n", StringSplitOptions.RemoveEmptyEntries)[0])!.AsObject();
        Assert.Equal(["id", "partition", "key_id", "WellboreID", "FacilityName", "Country", "Field", "Source", "records", "filter"], firstRow.Select(p => p.Key));
        Assert.Equal((1L, "dev", "dev:master-data--Wellbore:C:", "DK C", "PETREL", 1L), (
            firstRow["id"]!.GetValue<long>(), firstRow["partition"]!.GetValue<string>(), firstRow["WellboreID"]!.GetValue<string>(),
            firstRow["FacilityName"]!.GetValue<string>(), firstRow["Source"]!.GetValue<string>(), firstRow["records"]!.GetValue<long>()));

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

    /// <summary>The columns of a table in the module's schema, in order; none when it is not there.</summary>
    private async Task<IReadOnlyList<string>> ColumnsAsync(string table)
        => await SqlAsync($"SELECT c.[name] FROM sys.columns AS c WHERE c.[object_id] = OBJECT_ID(N'[osdu].[{table}]', N'U') ORDER BY c.[column_id];");

    private async Task<List<string>> SqlAsync(string sql)
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "(null)" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
        }

        return rows;
    }

    [Fact]
    public async Task A_dimension_is_one_table_whose_columns_follow_its_declaration_and_whose_rows_keep_their_numbers()
    {
        _platform.Add("dev:master-data--GeoPoliticalEntity:NO", "osdu:wks:master-data--GeoPoliticalEntity:1.0.0", new JsonObject { ["GeoPoliticalEntityName"] = "Norway" });
        _platform.Add("dev:master-data--GeoPoliticalEntity:DK", "osdu:wks:master-data--GeoPoliticalEntity:1.0.0", new JsonObject { ["GeoPoliticalEntityName"] = "Denmark" });
        foreach (var (name, country) in new[] { ("A", "NO"), ("B", "NO"), ("C", "DK") })
        {
            _platform.Add($"dev:master-data--Wellbore:{name}", Wellbore, new JsonObject
            {
                ["FacilityName"] = $"{country} {name}",
                ["FacilityID"] = $"uuid-{name}",
                ["GeoContexts"] = new JsonArray(new JsonObject { ["GeoPoliticalEntityID"] = $"dev:master-data--GeoPoliticalEntity:{country}:" }),
            });
        }

        foreach (var (log, wellbore, source) in new[] { ("1", "A", "RECALL"), ("2", "A", "PETREL"), ("3", "A", "RECALL"), ("4", "B", "RECALL"), ("5", "C", "PETREL") })
        {
            _platform.Add($"dev:work-product-component--WellLog:{log}", WellLog, new JsonObject { ["WellboreID"] = $"dev:master-data--Wellbore:{wellbore}:", ["Source"] = source });
        }

        const string Declared = """
            dimensions:
              - name: Wellbore
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                label: data.FacilityName
                attributes:
                  Country: [data.GeoContexts.GeoPoliticalEntityID, data.GeoPoliticalEntityName]

            """;
        const string Table = "dim_Wellbore";
        var (runner, ledger, flow) = await RunnerAsync(Head + Declared);

        // The first build makes the table: the dimension as one table, named after the dimension, keyed by an identity,
        // with a column for each attribute it declares. Any SQL client reads it and joins on its numbers.
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        var wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        Assert.Equal(Table, wellbores.TableName);
        Assert.Equal(["id", "partition", "key_id", "WellboreID", "FacilityName", "records", "filter", "Country"], await ColumnsAsync(Table));
        Assert.Equal(
            ["PK_" + Table + "|CLUSTERED|1|id|1", "IX_key|NONCLUSTERED|0|partition, key_id|0", "IX_value|NONCLUSTERED|0|partition, FacilityName, id|1"],
            await SqlAsync($"""
                SELECT i.[name], i.[type_desc], CAST(i.[is_primary_key] AS int),
                    STRING_AGG(c.[name], ', ') WITHIN GROUP (ORDER BY ic.[key_ordinal]), MAX(CAST(c.[is_identity] AS int))
                FROM sys.indexes AS i
                JOIN sys.index_columns AS ic ON ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
                JOIN sys.columns AS c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                WHERE i.[object_id] = OBJECT_ID(N'[osdu].[{Table}]') GROUP BY i.[name], i.[type_desc], i.[is_primary_key], i.[index_id] ORDER BY i.[index_id];
                """));
        var keys = (await ledger.ListDimensionValuesAsync(wellbores.DimensionId, new DimensionValueQuery(null, null, false, false, null, 10)))
            .ToDictionary(k => k.Original[^2..^1], StringComparer.Ordinal);
        Assert.Equal(
            [
                $"1|dev|{keys["C"].ValueId}|dev:master-data--Wellbore:C:|DK C|1|{keys["C"].Filter}|Denmark",
                $"2|dev|{keys["A"].ValueId}|dev:master-data--Wellbore:A:|NO A|3|{keys["A"].Filter}|Norway",
                $"3|dev|{keys["B"].ValueId}|dev:master-data--Wellbore:B:|NO B|1|{keys["B"].Filter}|Norway",
            ],
            await SqlAsync($"SELECT * FROM [osdu].[{Table}] ORDER BY [id];"));

        // A page of it, as the page and the CLI read it: searched, narrowed by an attribute, ordered by any column, counted.
        var page = await DimensionTable.ReadAsync(ledger, wellbores, new DimensionTableQuery(Limit: 2), CancellationToken.None);
        Assert.Equal(("osdu." + Table, 3L, true), (page.Table, page.Total, page.More));
        Assert.Equal(["Country"], page.Attributes);
        Assert.Equal([(1L, "DK C"), (2L, "NO A")], page.Rows.Select(r => (r.Id, r.Value)));
        var next = await DimensionTable.ReadAsync(ledger, wellbores, new DimensionTableQuery(Offset: 2, Limit: 2), CancellationToken.None);
        Assert.Equal((null, false, "NO B"), (next.Total, next.More, Assert.Single(next.Rows).Value));
        var byRecords = await DimensionTable.ReadAsync(ledger, wellbores, new DimensionTableQuery(OrderBy: "Records", Descending: true), CancellationToken.None);
        Assert.Equal([("NO A", 3L), ("DK C", 1L), ("NO B", 1L)], byRecords.Rows.Select(r => (r.Value, r.Records)));
        Assert.False(byRecords.More);
        var norway = await DimensionTable.ReadAsync(
            ledger, wellbores, new DimensionTableQuery(Attributes: [new DimensionAttributeMatch("country", ["Norway"])], OrderBy: "country"), CancellationToken.None);
        Assert.Equal(2L, norway.Total);
        Assert.Equal(["NO A", "NO B"], norway.Rows.Select(r => r.Value));
        Assert.Equal(("dev:master-data--Wellbore:A:", keys["A"].ValueId, keys["A"].Filter), (norway.Rows[0].Key, norway.Rows[0].KeyId, norway.Rows[0].Filter));
        Assert.Equal(["Norway"], norway.Rows[0].Attributes);
        Assert.Empty((await DimensionTable.ReadAsync(
            ledger, wellbores, new DimensionTableQuery(Attributes: [new DimensionAttributeMatch("Country", ["norway"])]), CancellationToken.None)).Rows);
        var searched = await DimensionTable.ReadAsync(ledger, wellbores, new DimensionTableQuery(Search: "denm"), CancellationToken.None);
        Assert.Equal("DK C", Assert.Single(searched.Rows).Value);
        Assert.Equal(1L, (await DimensionTable.ReadAsync(ledger, wellbores, new DimensionTableQuery(Search: "wellbore:b"), CancellationToken.None)).Total);
        Assert.Empty((await DimensionTable.ReadAsync(ledger, wellbores, new DimensionTableQuery(Search: "%"), CancellationToken.None)).Rows);
        var noColumn = await Assert.ThrowsAsync<DeliveryException>(() => DimensionTable.ReadAsync(ledger, wellbores, new DimensionTableQuery(OrderBy: "Basin"), CancellationToken.None));
        Assert.Contains("has no column 'Basin' to order by; it has FacilityName, WellboreID, records, id, Country", noColumn.Message, StringComparison.Ordinal);
        var noAttribute = await Assert.ThrowsAsync<DeliveryException>(() => DimensionTable.ReadAsync(
            ledger, wellbores, new DimensionTableQuery(Attributes: [new DimensionAttributeMatch("Basin", ["X"])]), CancellationToken.None));
        Assert.Contains("has no attribute column 'Basin'; it has Country", noAttribute.Message, StringComparison.Ordinal);

        // A build that finds the same thing writes no row of the table: each keeps its number, and nothing was touched.
        var before = await SqlAsync($"SELECT [id], %%physloc%% FROM [osdu].[{Table}] ORDER BY [id];");
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Equal(before, await SqlAsync($"SELECT [id], %%physloc%% FROM [osdu].[{Table}] ORDER BY [id];"));
        Assert.False(await DimensionTable.EnsureAsync(ledger, wellbores, CancellationToken.None));

        // The flow declares more: the table gains a column for each new attribute on the next build, with no migration.
        // The attribute it collects makes a row for each value a key holds, so the rows are written again.
        (runner, ledger, flow) = await RunnerAsync(Head + Declared + """
                  UUID: data.FacilityID
                  Source: { collect: data.Source }
            """);
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Equal(["id", "partition", "key_id", "WellboreID", "FacilityName", "records", "filter", "Country", "UUID", "Source"], await ColumnsAsync(Table));
        Assert.Equal(
            ["DK C|Denmark|uuid-C|PETREL|1", "NO A|Norway|uuid-A|PETREL|1", "NO A|Norway|uuid-A|RECALL|2", "NO B|Norway|uuid-B|RECALL|1"],
            await SqlAsync($"SELECT [FacilityName], [Country], [UUID], [Source], [records] FROM [osdu].[{Table}] WHERE [partition] = N'dev' ORDER BY [FacilityName], [Source];"));
        wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        var recall = await DimensionTable.ReadAsync(
            ledger, wellbores, new DimensionTableQuery(Attributes: [new DimensionAttributeMatch("Source", ["RECALL"])], OrderBy: "records", Descending: true), CancellationToken.None);
        Assert.Equal(["Country", "UUID", "Source"], recall.Attributes);
        Assert.Equal([("NO A", 2L), ("NO B", 1L)], recall.Rows.Select(r => (r.Value, r.Records)));

        // What changed is rewritten where it is: a log that moves to another source changes two rows and no number.
        var ids = await SqlAsync($"SELECT [id] FROM [osdu].[{Table}] WHERE [FacilityName] = N'NO B' OR [FacilityName] = N'DK C' ORDER BY [id];");
        _platform.Records.Single(r => r["id"]!.GetValue<string>() == "dev:work-product-component--WellLog:2")["data"]!["Source"] = "RECALL";
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Equal(
            ["DK C|PETREL|1", "NO A|RECALL|3", "NO B|RECALL|1"],
            await SqlAsync($"SELECT [FacilityName], [Source], [records] FROM [osdu].[{Table}] ORDER BY [FacilityName], [Source];"));
        Assert.Equal(ids, await SqlAsync($"SELECT [id] FROM [osdu].[{Table}] WHERE [FacilityName] = N'NO B' OR [FacilityName] = N'DK C' ORDER BY [id];"));

        // An attribute the flow stops declaring keeps its column, emptied, so a query naming it still runs; a column
        // somebody added to the table is theirs and is left as it is. The page shows what the dimension declares.
        await SqlAsync($"ALTER TABLE [osdu].[{Table}] ADD [Notes] nvarchar(100) NULL;");
        await SqlAsync($"UPDATE [osdu].[{Table}] SET [Notes] = N'mine';");
        (runner, ledger, flow) = await RunnerAsync(Head + Declared + """
                  Source: { collect: data.Source }
            """);
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Equal(["id", "partition", "key_id", "WellboreID", "FacilityName", "records", "filter", "Country", "UUID", "Source", "Notes"], await ColumnsAsync(Table));
        Assert.Equal(["DK C|(null)|PETREL|mine", "NO A|(null)|RECALL|mine", "NO B|(null)|RECALL|mine"], await SqlAsync($"SELECT [FacilityName], [UUID], [Source], [Notes] FROM [osdu].[{Table}] ORDER BY [FacilityName];"));
        wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        Assert.Equal(["Country", "Source"], (await DimensionTable.ShapeAsync(ledger, wellbores, CancellationToken.None)).Attributes);

        // A table the dimension wrote under an earlier name is left behind by no build: the next one writes the table
        // under its name and drops the other.
        await SqlAsync($"""
            EXEC sys.sp_rename N'osdu.{Table}', N'dim_wells_dimensions_Wellbore';
            EXEC sys.sp_rename N'osdu.PK_{Table}', N'PK_dim_wells_dimensions_Wellbore', N'OBJECT';
            UPDATE [osdu].[Dimension] SET [TableName] = N'dim_wells_dimensions_Wellbore';
            """);
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Empty(await ColumnsAsync("dim_wells_dimensions_Wellbore"));
        Assert.Equal(["id", "partition", "key_id", "WellboreID", "FacilityName", "records", "filter", "Country", "Source"], await ColumnsAsync(Table));
        Assert.Equal(["DK C|PETREL", "NO A|RECALL", "NO B|RECALL"], await SqlAsync($"SELECT [FacilityName], [Source] FROM [osdu].[{Table}] ORDER BY [FacilityName];"));
        wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        Assert.Equal(Table, wellbores.TableName);

        // A table dropped by hand is made again, with its rows, by whoever next reads it; so is the table of a dimension
        // built before dimensions had one, which names none.
        await SqlAsync($"DROP TABLE [osdu].[{Table}];");
        Assert.Equal(3, (await DimensionTable.ReadAsync(ledger, wellbores, new DimensionTableQuery(), CancellationToken.None)).Total);
        Assert.Equal(["id", "partition", "key_id", "WellboreID", "FacilityName", "records", "filter", "Country", "Source"], await ColumnsAsync(Table));
        await SqlAsync($"DROP TABLE [osdu].[{Table}]; UPDATE [osdu].[Dimension] SET [TableName] = NULL;");
        wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        Assert.Null(wellbores.TableName);
        var exported = new StringWriter();
        Assert.Equal(3, await DimensionExport.WriteAsync(ledger, wellbores, DimensionExportSet.Table, DimensionExportFormat.Csv, exported, CancellationToken.None));
        Assert.Equal(Table, (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!.TableName);

        // A key no build finds any more is no row of it.
        _platform.Records.RemoveAll(r => r["id"]!.GetValue<string>() == "dev:work-product-component--WellLog:5");
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Equal(["NO A", "NO B"], await SqlAsync($"SELECT [FacilityName] FROM [osdu].[{Table}] ORDER BY [FacilityName];"));

        // Removing the dimension drops its table with it.
        wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        var removed = await DimensionRemoval.RemoveAsync(ledger, wellbores, "admin@example.test", _clock, CancellationToken.None);
        Assert.Equal(Table, removed.TableDropped);
        Assert.Contains("dropped the table osdu." + Table, removed.Describe(), StringComparison.Ordinal);
        Assert.Empty(await ColumnsAsync(Table));
        Assert.Empty(await SqlAsync("SELECT [Name] FROM [osdu].[DimensionAttributeName];"));
    }

    [Fact]
    public async Task The_key_and_value_columns_are_renamed_where_they_are_when_the_dimension_names_them_otherwise()
    {
        foreach (var name in new[] { "A", "B" })
        {
            _platform.Add($"dev:master-data--Wellbore:{name}", Wellbore, new JsonObject { ["FacilityName"] = $"NO {name}" });
            _platform.Add($"dev:work-product-component--WellLog:{name}", WellLog, new JsonObject { ["WellboreID"] = $"dev:master-data--Wellbore:{name}:" });
        }

        const string Declared = """
            dimensions:
              - name: Wellbore
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                label: data.FacilityName

            """;
        const string Table = "dim_Wellbore";
        const string ValueIndex = $"""
            SELECT STRING_AGG(c.[name], ', ') WITHIN GROUP (ORDER BY ic.[key_ordinal])
            FROM sys.indexes AS i
            JOIN sys.index_columns AS ic ON ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
            JOIN sys.columns AS c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
            WHERE i.[object_id] = OBJECT_ID(N'[osdu].[{Table}]') AND i.[name] = N'IX_value';
            """;
        string Rows(string key, string value) => $"SELECT [id], [key_id], [{key}], [{value}], [records] FROM [osdu].[{Table}] ORDER BY [id];";
        string[] Columns(string key, string value, params string[] more) => ["id", "partition", "key_id", key, value, "records", "filter", .. more];

        // The columns are named after what the dimension reads: the property its path ends with, and its label's.
        var (runner, ledger, flow) = await RunnerAsync(Head + Declared);
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        var wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        Assert.Equal(("WellboreID", "FacilityName"), (wellbores.KeyColumn, wellbores.ValueColumn));
        var rows = await SqlAsync(Rows("WellboreID", "FacilityName"));
        Assert.Equal(2, rows.Count);

        // A table made before dimensions named their columns holds them as key and value, and is read under those names.
        await SqlAsync($"""
            EXEC sys.sp_rename N'[osdu].[{Table}].[WellboreID]', N'key', N'COLUMN';
            EXEC sys.sp_rename N'[osdu].[{Table}].[FacilityName]', N'value', N'COLUMN';
            UPDATE [osdu].[Dimension] SET [KeyColumn] = N'key', [ValueColumn] = N'value';
            """);
        wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        var before = await DimensionTable.ReadAsync(ledger, wellbores, new DimensionTableQuery(), CancellationToken.None);
        Assert.Equal(("key", "value", 2L), (before.KeyColumn, before.ValueColumn, before.Total));

        // Its next build renames the two where they are: every row keeps its number, and the index over the value follows.
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Equal(Columns("WellboreID", "FacilityName"), await ColumnsAsync(Table));
        Assert.Equal(rows, await SqlAsync(Rows("WellboreID", "FacilityName")));
        Assert.Equal(["partition, FacilityName, id"], await SqlAsync(ValueIndex));

        // No row names them, as for a table an earlier version made and a build then failed on: they are told by their names.
        await SqlAsync($"""
            EXEC sys.sp_rename N'[osdu].[{Table}].[WellboreID]', N'key', N'COLUMN';
            EXEC sys.sp_rename N'[osdu].[{Table}].[FacilityName]', N'value', N'COLUMN';
            UPDATE [osdu].[Dimension] SET [KeyColumn] = NULL, [ValueColumn] = NULL;
            """);
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Equal(Columns("WellboreID", "FacilityName"), await ColumnsAsync(Table));
        Assert.Equal(rows, await SqlAsync(Rows("WellboreID", "FacilityName")));

        // The document names them: a name that changes only in case is a rename too, and the rows stay.
        (runner, ledger, flow) = await RunnerAsync(Head + Declared + "    columns: { key: WellboreId, value: Wellbore }\n");
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Equal(Columns("WellboreId", "Wellbore"), await ColumnsAsync(Table));
        Assert.Equal(rows, await SqlAsync(Rows("WellboreId", "Wellbore")));
        Assert.Equal(["partition, Wellbore, id"], await SqlAsync(ValueIndex));
        wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        Assert.Equal(("WellboreId", "Wellbore"), (wellbores.KeyColumn, wellbores.ValueColumn));

        // A page says what the two columns are named, and is ordered by a column's name or by the words value and key.
        var byName = await DimensionTable.ReadAsync(ledger, wellbores, new DimensionTableQuery(OrderBy: "wellbore", Descending: true), CancellationToken.None);
        Assert.Equal(("WellboreId", "Wellbore"), (byName.KeyColumn, byName.ValueColumn));
        Assert.Equal(["NO B", "NO A"], byName.Rows.Select(r => r.Value));
        Assert.Equal(["NO A", "NO B"], (await DimensionTable.ReadAsync(ledger, wellbores, new DimensionTableQuery(OrderBy: "value"), CancellationToken.None)).Rows.Select(r => r.Value));
        Assert.Equal(
            ["dev:master-data--Wellbore:B:", "dev:master-data--Wellbore:A:"],
            (await DimensionTable.ReadAsync(ledger, wellbores, new DimensionTableQuery(OrderBy: "key", Descending: true), CancellationToken.None)).Rows.Select(r => r.Key));
        Assert.Equal("NO B", Assert.Single((await DimensionTable.ReadAsync(ledger, wellbores, new DimensionTableQuery(Search: "wellbore:b"), CancellationToken.None)).Rows).Value);
        var shape = await DimensionTable.ShapeAsync(ledger, wellbores, CancellationToken.None);
        Assert.Equal(("WellboreId", "Wellbore"), (shape.KeyColumn, shape.ValueColumn));
        var noColumn = await Assert.ThrowsAsync<DeliveryException>(() => DimensionTable.ReadAsync(ledger, wellbores, new DimensionTableQuery(OrderBy: "FacilityName"), CancellationToken.None));
        Assert.Contains("has no column 'FacilityName' to order by; it has Wellbore, WellboreId, records, id", noColumn.Message, StringComparison.Ordinal);

        // Each takes the other's name: neither name is free while the other column holds it, so both pass through another.
        (runner, ledger, flow) = await RunnerAsync(Head + Declared + "    columns: { key: Wellbore, value: WellboreId }\n");
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Equal(Columns("Wellbore", "WellboreId"), await ColumnsAsync(Table));
        Assert.Equal(rows, await SqlAsync(Rows("Wellbore", "WellboreId")));
        Assert.Equal(["partition, WellboreId, id"], await SqlAsync(ValueIndex));

        // A name another column of the table holds is not taken from it: the build fails saying so, and nothing is renamed.
        await SqlAsync($"ALTER TABLE [osdu].[{Table}] ADD [Name] nvarchar(10) NULL;");
        (runner, ledger, flow) = await RunnerAsync(Head + Declared + "    columns: { value: Name }\n");
        var taken = await Assert.ThrowsAsync<DimensionBuildsFailedException>(() => runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None));
        Assert.Contains(
            "The dimension's table osdu.dim_Wellbore has a column Name already (an attribute the dimension declared before, or a column added by hand), so its value column WellboreId cannot be renamed to it, and nothing was written. Drop that column, or name the value's column otherwise in the flow: columns: { value: <name> }.",
            taken.Message,
            StringComparison.Ordinal);
        Assert.Equal(Columns("Wellbore", "WellboreId", "Name"), await ColumnsAsync(Table));
        Assert.Equal(rows, await SqlAsync(Rows("Wellbore", "WellboreId")));

        // A reader that finds the table gone makes it again under the names it had: only a build reads the document.
        await SqlAsync($"DROP TABLE [osdu].[{Table}];");
        wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        Assert.Equal(2, (await DimensionTable.ReadAsync(ledger, wellbores, new DimensionTableQuery(), CancellationToken.None)).Total);
        Assert.Equal(Columns("Wellbore", "WellboreId"), await ColumnsAsync(Table));

        // And a dimension that never had a table takes the names it reads by.
        await SqlAsync($"DROP TABLE [osdu].[{Table}]; UPDATE [osdu].[Dimension] SET [TableName] = NULL, [KeyColumn] = NULL, [ValueColumn] = NULL;");
        wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        Assert.Equal(2, (await DimensionTable.ReadAsync(ledger, wellbores, new DimensionTableQuery(), CancellationToken.None)).Total);
        Assert.Equal(Columns("WellboreID", "FacilityName"), await ColumnsAsync(Table));
        wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        Assert.Equal((Table, "WellboreID", "FacilityName"), (wellbores.TableName, wellbores.KeyColumn, wellbores.ValueColumn));
    }

    [Fact]
    public async Task A_dimension_another_flow_declares_by_the_same_name_is_refused_and_the_first_ones_table_left_alone()
    {
        _platform.Add("dev:work-product-component--WellLog:1", WellLog, new JsonObject { ["Source"] = "RECALL" });
        const string Declared = """
            dimensions:
              - name: Source
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.Source
            """;
        var (runner, ledger, flow) = await RunnerAsync(Head + Declared);
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        // The dimension is named after the property it reads: its value's column keeps the name, and its key's takes Key.
        Assert.Equal(["id", "partition", "key_id", "SourceKey", "Source", "records", "filter"], await ColumnsAsync("dim_Source"));

        // A table is named after its dimension alone, so a dimension's name is unique among flows: another flow declaring
        // one of the same name would write the first one's table.
        var (other, _, _) = await RunnerAsync(Head.Replace("name: wells-dimensions", "name: log-dimensions", StringComparison.Ordinal) + Declared, "flows/other.yaml");
        var refused = await Assert.ThrowsAsync<DimensionBuildsFailedException>(() => other.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None));
        Assert.Contains(
            "Dimension Source of log-dimensions would write the table osdu.dim_Source, which dimension Source of wells-dimensions writes: a dimension's table is named after the dimension alone, so a dimension's name is unique among the flows of a database. Rename one of them.",
            refused.Message,
            StringComparison.Ordinal);
        var failed = (await ledger.ListDimensionsAsync(null, null)).Single(d => d.FlowName == "log-dimensions");
        Assert.Equal((DimensionRunStatus.Failed, (string?)null), ((await ledger.ListDimensionRunsAsync(failed.DimensionId, 1)).Single().Status, failed.TableName));

        // The first flow's table is untouched, and the refused dimension goes without taking it.
        var removed = await DimensionRemoval.RemoveAsync(ledger, failed, "admin@example.test", _clock, CancellationToken.None);
        Assert.Null(removed.TableDropped);
        Assert.Equal(["dev|RECALL|RECALL|1"], await SqlAsync("SELECT [partition], [SourceKey], [Source], [records] FROM [osdu].[dim_Source];"));
        Assert.Equal("dim_Source", (await ledger.FindDimensionAsync(flow.LedgerId, "Source"))!.TableName);
    }

    [Fact]
    public void A_table_is_named_after_its_dimension_alone_in_a_name_that_needs_no_quoting()
    {
        Assert.Equal("dim_Wellbore", DimensionTables.NameOf("Wellbore"));
        Assert.Equal("dim_Well_Type", DimensionTables.NameOf("Well-Type"));
        Assert.Equal(DimensionTables.NameOf("Well.Type"), DimensionTables.NameOf("Well-Type"));
        Assert.Equal("osdu.dim_Wellbore", DimensionTables.Shown(DimensionTables.NameOf("Wellbore")));
        Assert.Equal("[osdu].[dim_Wellbore]", DimensionTables.Qualified(DimensionTables.NameOf("Wellbore")));

        // The longest name a dimension may have still names a table SQL Server takes, with room for its key's name.
        Assert.Equal(104, DimensionTables.NameOf(new string('d', 100)).Length);

        var table = DimensionTables.Of("D", "Code", "Name", [new DimensionAttributeSpec("Country", ["data.A"]), new DimensionAttributeSpec("Source", [], "data.Source")]);
        Assert.Equal(("dim_D", "Code", "Name"), (table.Name, table.KeyColumn, table.ValueColumn));
        Assert.Equal([("Country", false), ("Source", true)], table.Columns.Select(c => (c.Name, c.Collected)));
        var tooMany = Assert.Throws<DeliveryException>(() => DimensionTables.Of(
            "D", "Code", "Name", Enumerable.Range(0, DimensionSpec.MaxAttributes + 1).Select(i => new DimensionAttributeSpec($"A{i}", ["data.A"])).ToList()));
        Assert.Contains("Dimension D declares 21 attributes, and a dimension's table holds 20", tooMany.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Few_values_over_many_logs_are_collected_a_read_per_value_and_count_as_one_pass_would()
    {
        // 3,600 logs of four wellbores, two sources and logs without one: three reads where one pass would take four pages.
        for (var log = 0; log < 3600; log++)
        {
            var data = new JsonObject { ["WellboreID"] = $"dev:master-data--Wellbore:W{log % 4}:" };
            if (log % 3 != 0)
            {
                data["Source"] = log % 3 == 1 ? "RECALL" : "PETREL";
            }

            _platform.Add($"dev:work-product-component--WellLog:{log}", WellLog, data);
        }

        var (runner, ledger, flow) = await RunnerAsync(Head + """
            dimensions:
              - name: Wellbore
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                unlabelled: Not specified
                attributes:
                  Source: { collect: data.Source }
            """);

        var outcome = await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        Assert.Equal((1, 0), (outcome.Built, outcome.Failed));
        Assert.DoesNotContain(_platform.Calls, c => c.Uri.AbsolutePath.EndsWith("/query_with_cursor", StringComparison.Ordinal));
        var wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        var keys = await ledger.ListDimensionValuesAsync(wellbores.DimensionId, new DimensionValueQuery(null, null, false, false, null, 10));
        Assert.Equal(4, keys.Count);
        Assert.All(keys, k => Assert.Equal(
            [("Not specified", (long?)300L), ("PETREL", 300L), ("RECALL", 300L)],
            k.Attributes.Select(a => (a.Value, a.Records)).OrderBy(a => a.Value, StringComparer.Ordinal)));
        Assert.Equal(
            [("PETREL", 1200L), ("RECALL", 1200L)],
            (await ledger.ListDimensionCollectedTextsAsync(wellbores.DimensionId, "Source", null, 10)).Select(v => (v.Value, v.Records)));
        OsduContracts.AssertConform(_platform.Calls, null, OsduContracts.Search);
    }

    [Fact]
    public async Task Many_values_are_collected_in_one_pass_and_a_search_says_when_none_of_them_is_more_than_it_can_exclude()
    {
        // 1,100 sources over three wellbores, one log each, and two logs without a source.
        for (var log = 0; log < 1102; log++)
        {
            var data = new JsonObject { ["WellboreID"] = $"dev:master-data--Wellbore:W{log % 3}:" };
            if (log < 1100)
            {
                data["Source"] = $"S{log:D4}";
            }

            _platform.Add($"dev:work-product-component--WellLog:{log}", WellLog, data);
        }

        var (runner, ledger, flow) = await RunnerAsync(Head + """
            dimensions:
              - name: Wellbore
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                unlabelled: Not specified
                attributes:
                  Source: { collect: data.Source }
            """);

        var outcome = await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        Assert.Equal((1, 0), (outcome.Built, outcome.Failed));
        var wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        Assert.Equal(1100, (await ledger.ListDimensionCollectedTextsAsync(wellbores.DimensionId, "Source", null, 2000)).Count);
        // W2 holds every third source, and the log of 1,100 without one.
        var w2 = (await ledger.ListDimensionValuesAsync(wellbores.DimensionId, new DimensionValueQuery("W2", null, false, false, null, 10))).Single();
        Assert.Equal(366, w2.Attributes.Count(a => a is { Name: "Source", Records: 1 } && a.Value != "Not specified"));
        Assert.Contains(w2.Attributes, a => a is { Name: "Source", Value: "Not specified", Records: 1 });
        Assert.Equal(
            [("S0001", 1, 1L)],
            (await ledger.ListDimensionAttributeValuesAsync(wellbores.DimensionId, new DimensionAttributeValueQuery("Source", "S0001", 10))).Select(v => (v.Value, v.Keys, v.Records)));

        // A source picks its log; Not specified would have to exclude every one of 1,100 sources, which no search holds.
        var one = await DimensionSearch.ComposeAsync(
            ledger, [new DimensionPick(wellbores, [], [], [new DimensionAttributeMatch("Source", ["S0001"])])], null, null, CancellationToken.None);
        Assert.Equal(["dev:work-product-component--WellLog:1"], _platform.Find(WellLog, one.Query));
        var none = await Assert.ThrowsAsync<DeliveryException>(() => DimensionSearch.ComposeAsync(
            ledger, [new DimensionPick(wellbores, [], [], [new DimensionAttributeMatch("Source", ["Not specified"])])], null, null, CancellationToken.None));
        Assert.Contains("more than the 1000 one search can exclude", none.Message, StringComparison.Ordinal);
        OsduContracts.AssertConform(_platform.Calls, null, OsduContracts.Search);
    }

    [Fact]
    public async Task A_pass_over_many_records_is_read_in_ranges_of_keys_side_by_side_and_counts_every_record_once()
    {
        // 9,000 logs over 300 wellbores and 600 sources: more sources than a read per value is made for, so the logs are
        // passed over, and enough of them for two ranges of keys, each read through a cursor of its own.
        var expected = new Dictionary<(string Key, string Source), long>();
        for (var log = 0; log < 9_000; log++)
        {
            var wellbore = $"dev:master-data--Wellbore:W{log % 300:D3}:";
            var source = $"S{(log * 7) % 600:D3}";
            _platform.Add($"dev:work-product-component--WellLog:{log}", WellLog, new JsonObject { ["WellboreID"] = wellbore, ["Source"] = source });
            expected[(wellbore, source)] = expected.GetValueOrDefault((wellbore, source)) + 1;
        }

        var (runner, ledger, flow) = await RunnerAsync(Head + """
            dimensions:
              - name: Wellbore
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                attributes:
                  Source: { collect: data.Source }
            """);

        var outcome = await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        Assert.Equal((1, 0), (outcome.Built, outcome.Failed));
        var wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        var keys = await ledger.ListDimensionValuesAsync(wellbores.DimensionId, new DimensionValueQuery(null, null, false, false, null, 1000));
        Assert.Equal(300, keys.Count);
        Assert.Equal(
            expected.OrderBy(e => e.Key.Key, StringComparer.Ordinal).ThenBy(e => e.Key.Source, StringComparer.Ordinal).Select(e => (e.Key.Key, e.Key.Source, (long?)e.Value)).ToList(),
            keys.SelectMany(k => k.Attributes.Select(a => (k.Original, a.Value, a.Records)))
                .OrderBy(e => e.Original, StringComparer.Ordinal).ThenBy(e => e.Value, StringComparer.Ordinal).ToList());
        Assert.Equal(9_000L, keys.Sum(k => k.Attributes.Sum(a => a.Records ?? 0)));

        // The pass opened two cursors, each over a range of the wellbores' ids and each returning the key and the source,
        // and between them they read every log once.
        var opened = _platform.Calls
            .Where(c => c.Uri.AbsolutePath.EndsWith("/query_with_cursor", StringComparison.Ordinal) && c.Body is not null)
            .Select(c => JsonNode.Parse(c.Body!)!.AsObject())
            .Where(body => body["cursor"] is null
                && body["returnedFields"]!.AsArray().Select(f => f!.GetValue<string>()).Intersect(["data.WellboreID", "data.Source"]).Count() == 2)
            .Select(body => body["query"]!.GetValue<string>())
            .ToList();
        Assert.Equal(2, opened.Count);
        Assert.All(opened, query => Assert.Contains("data.WellboreID.keyword:[", query, StringComparison.Ordinal));
        Assert.NotEqual(opened[0], opened[1]);
        OsduContracts.AssertConform(_platform.Calls, null, OsduContracts.Search);
    }

    [Fact]
    public async Task A_page_lost_during_the_pass_is_read_again_and_every_record_is_still_counted_once()
    {
        // The same pass over 9,000 logs in two ranges, with the answer to one range's second page lost after its cursor moved
        // on: asked again with that cursor, the service would answer with the page after it, and the thousand logs of the
        // lost page would be counted nowhere. The range is read again from its first page, and every log counts once.
        var expected = new Dictionary<(string Key, string Source), long>();
        for (var log = 0; log < 9_000; log++)
        {
            var wellbore = $"dev:master-data--Wellbore:W{log % 300:D3}:";
            var source = $"S{(log * 7) % 600:D3}";
            _platform.Add($"dev:work-product-component--WellLog:{log}", WellLog, new JsonObject { ["WellboreID"] = wellbore, ["Source"] = source });
            expected[(wellbore, source)] = expected.GetValueOrDefault((wellbore, source)) + 1;
        }

        var (runner, ledger, flow) = await RunnerAsync(Head + """
            dimensions:
              - name: Wellbore
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                attributes:
                  Source: { collect: data.Source }
            """);
        var lost = 0;
        _platform.Lose = body => IsPassPage(body, continuing: true) && Interlocked.CompareExchange(ref lost, 1, 0) == 0;

        var outcome = await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        Assert.Equal((1, 0), (outcome.Built, outcome.Failed));
        var wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        var keys = await ledger.ListDimensionValuesAsync(wellbores.DimensionId, new DimensionValueQuery(null, null, false, false, null, 1000));
        Assert.Equal(
            expected.OrderBy(e => e.Key.Key, StringComparer.Ordinal).ThenBy(e => e.Key.Source, StringComparer.Ordinal).Select(e => (e.Key.Key, e.Key.Source, (long?)e.Value)).ToList(),
            keys.SelectMany(k => k.Attributes.Select(a => (k.Original, a.Value, a.Records)))
                .OrderBy(e => e.Original, StringComparer.Ordinal).ThenBy(e => e.Value, StringComparer.Ordinal).ToList());
        Assert.Equal(9_000L, keys.Sum(k => k.Attributes.Sum(a => a.Records ?? 0)));

        // Two ranges and the range read again: three first pages, and the lost page's cursor was never asked again.
        var pages = _platform.Calls.Where(c => IsPassPage(c.Body, continuing: null)).Select(c => JsonNode.Parse(c.Body!)!.AsObject()).ToList();
        Assert.Equal(3, pages.Count(p => p["cursor"] is null));
        Assert.Equal(1, lost);
        OsduContracts.AssertConform(_platform.Calls, null, OsduContracts.Search);
    }

    [Fact]
    public async Task A_pass_that_cannot_be_read_whole_fails_the_build_and_the_dimension_keeps_the_build_it_had()
    {
        for (var log = 0; log < 9_000; log++)
        {
            _platform.Add(
                $"dev:work-product-component--WellLog:{log}", WellLog,
                new JsonObject { ["WellboreID"] = $"dev:master-data--Wellbore:W{log % 300:D3}:", ["Source"] = $"S{(log * 7) % 600:D3}" });
        }

        var (runner, ledger, flow) = await RunnerAsync(Head + """
            dimensions:
              - name: Wellbore
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                attributes:
                  Source: { collect: data.Source }
            """);
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        var built = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        var before = (await ledger.ListDimensionValuesAsync(built.DimensionId, new DimensionValueQuery(null, null, false, false, null, 1000)))
            .SelectMany(k => k.Attributes.Select(a => (k.Original, a.Value, a.Records))).ToList();

        // Every page after the first of the pass is lost: each range's read and its second read both fail.
        _platform.Lose = body => IsPassPage(body, continuing: true);
        var failed = await Assert.ThrowsAsync<DimensionBuildsFailedException>(() => runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None));

        Assert.Contains("could not be read whole in 2 reads", failed.Outcome.Dimensions[0].Error, StringComparison.Ordinal);
        var kept = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        Assert.Equal(built.Members, kept.Members);
        Assert.Equal(
            before,
            (await ledger.ListDimensionValuesAsync(kept.DimensionId, new DimensionValueQuery(null, null, false, false, null, 1000)))
                .SelectMany(k => k.Attributes.Select(a => (k.Original, a.Value, a.Records))).ToList());
        var runs = await ledger.ListDimensionRunsAsync(kept.DimensionId, 2);
        Assert.Equal([DimensionRunStatus.Failed, DimensionRunStatus.Completed], runs.Select(r => r.Status));
    }

    /// <summary>
    /// Whether a request body is a page of the pass over the logs (a cursor search returning the key and the source), and,
    /// when <paramref name="continuing"/> says, one asked with a cursor or without one.
    /// </summary>
    private static bool IsPassPage(string? body, bool? continuing)
    {
        if (string.IsNullOrEmpty(body) || JsonNode.Parse(body) is not JsonObject request || request["returnedFields"] is not JsonArray fields)
        {
            return false;
        }

        var returned = fields.Select(f => f!.GetValue<string>()).ToList();
        return returned.Contains("data.WellboreID") && returned.Contains("data.Source")
            && (continuing is null || continuing == (request["cursor"] is not null));
    }

    [Fact]
    public async Task A_build_asks_the_platform_several_things_at_a_time_and_never_more_than_the_flow_allows()
    {
        // Wellbores with a name and a country, and logs of many sources: keys read in ranges, labels a thousand ids a
        // search, and a pass over the logs, each of which the build asks side by side.
        _platform.Add("dev:master-data--GeoPoliticalEntity:NO", "osdu:wks:master-data--GeoPoliticalEntity:1.0.0", new JsonObject { ["GeoPoliticalEntityName"] = "Norway" });
        for (var wellbore = 0; wellbore < 1_200; wellbore++)
        {
            _platform.Add($"dev:master-data--Wellbore:W{wellbore:D4}", Wellbore, new JsonObject
            {
                ["FacilityName"] = $"NO {wellbore:D4}",
                ["GeoContexts"] = new JsonArray(new JsonObject { ["GeoPoliticalEntityID"] = "dev:master-data--GeoPoliticalEntity:NO:" }),
            });
        }

        for (var log = 0; log < 2_400; log++)
        {
            _platform.Add($"dev:work-product-component--WellLog:{log}", WellLog, new JsonObject
            {
                ["WellboreID"] = $"dev:master-data--Wellbore:W{log % 1_200:D4}:", ["Source"] = $"S{log % 600:D3}",
            });
        }

        _platform.Latency = TimeSpan.FromMilliseconds(3);
        var (runner, ledger, flow) = await RunnerAsync(Head.Replace("concurrency: 2", "concurrency: 6", StringComparison.Ordinal) + """
            dimensions:
              - name: Wellbore
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                label: data.FacilityName
                attributes:
                  Country: [data.GeoContexts.GeoPoliticalEntityID, data.GeoPoliticalEntityName]
                  Source: { collect: data.Source }
            """);

        var outcome = await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        Assert.Equal((1, 0), (outcome.Built, outcome.Failed));
        Assert.InRange(_platform.MostAtOnce, 3, 6);
        var wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        Assert.Equal((1_200L, 1_200L), (wellbores.Members, wellbores.Originals));
        var first = (await ledger.ListDimensionValuesAsync(wellbores.DimensionId, new DimensionValueQuery("W0000", null, false, false, null, 1))).Single();
        Assert.Equal("NO 0000", first.MemberValue);
        Assert.Equal([("Country", "Norway", null), ("Source", "S000", 2L)], first.Attributes.Select(a => (a.Name, a.Value, a.Records)));
        OsduContracts.AssertConform(_platform.Calls, null, OsduContracts.Search);
    }

    [Fact]
    public void A_pass_is_cut_into_ranges_of_keys_holding_about_as_many_records_each()
    {
        var field = OsduField.Text("data.WellboreID");
        var order = DimensionValueText.Order(field.Index);

        // Few records are one range, the whole of them, whatever the readers.
        Assert.Equal([DistinctSlice.Whole], DimensionCollector.Ranges(field, new Dictionary<string, long> { ["a"] = 1_000, ["b"] = 2_000 }, order, 8));

        // 100 keys of 1,000 records and 8 readers: 25 ranges (no range under 4,000 records), meeting end to end, open at both ends.
        var keys = Enumerable.Range(0, 100).ToDictionary(i => $"K{i:D3}", _ => 1_000L);
        var ranges = DimensionCollector.Ranges(field, keys, order, 8);
        Assert.Equal(25, ranges.Count);
        Assert.Null(ranges[0].From);
        Assert.Null(ranges[^1].To);
        Assert.All(Enumerable.Range(1, ranges.Count - 1), i => Assert.Equal(ranges[i - 1].To, ranges[i].From));
        Assert.All(keys.Keys, key => Assert.Single(ranges, r => r.Contains(key, order)));
        Assert.All(ranges, r => Assert.Equal(4_000L, keys.Where(k => r.Contains(k.Key, order)).Sum(k => k.Value)));

        // One key holding most of the records takes a range of its own; the rest still fall in exactly one.
        var skewed = Enumerable.Range(0, 20).ToDictionary(i => $"K{i:D3}", i => i == 10 ? 50_000L : 500L);
        var uneven = DimensionCollector.Ranges(field, skewed, order, 2);
        Assert.InRange(uneven.Count, 2, 8);
        Assert.All(skewed.Keys, key => Assert.Single(uneven, r => r.Contains(key, order)));

        // One reader still reads in ranges, in turn; fewer readers ask for fewer ranges.
        Assert.Equal(4, DimensionCollector.Ranges(field, keys, order, 1).Count);
    }

    [Fact]
    public async Task A_dimension_no_longer_declared_is_removed_with_everything_kept_of_it_and_nothing_else()
    {
        foreach (var (log, wellbore, source) in new[] { ("1", "A", "RECALL"), ("2", "A", "PETREL"), ("3", "B", "RECALL") })
        {
            _platform.Add($"dev:work-product-component--WellLog:{log}", WellLog, new JsonObject { ["WellboreID"] = $"dev:master-data--Wellbore:{wellbore}:", ["Source"] = source });
        }

        var (runner, ledger, flow) = await RunnerAsync(Head + """
            dimensions:
              - name: Wellbore
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                unlabelled: Not specified
                attributes:
                  Source: { collect: data.Source }
              - name: Source
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.Source
            """);

        // Two builds, the second finding a new wellbore, so the dimension has builds, keys, values, attributes, collected
        // texts and a change log to lose.
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        _platform.Add("dev:work-product-component--WellLog:4", WellLog, new JsonObject { ["WellboreID"] = "dev:master-data--Wellbore:C:", ["Source"] = "RECALL" });
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        var wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        var sources = (await ledger.FindDimensionAsync(flow.LedgerId, "Source"))!;
        string[] tables = ["DimensionRun", "DimensionMember", "DimensionValue", "DimensionAttribute", "DimensionCollectedText", "DimensionChange", "Dimension"];
        async Task<long[]> RowsAsync(int dimensionId)
        {
            await using var connection = new SqlConnection(_db.ConnectionString);
            await connection.OpenAsync();
            var counts = new long[tables.Length];
            for (var i = 0; i < tables.Length; i++)
            {
                await using var command = new SqlCommand($"SELECT COUNT_BIG(*) FROM [osdu].[{tables[i]}] WHERE [DimensionId] = @d", connection);
                command.Parameters.AddWithValue("@d", dimensionId);
                counts[i] = (long)(await command.ExecuteScalarAsync())!;
            }

            return counts;
        }

        var before = await RowsAsync(wellbores.DimensionId);
        var others = await RowsAsync(sources.DimensionId);
        Assert.Equal([2L, 3L, 3L, 4L, 2L, 1L, 1L], before);

        // A cache flow capturing the dimension keeps it: its refresh would read a dimension that is gone.
        await using (var db = _db.CreateDbContext())
        {
            db.DeliveryCacheDefinitions.Add(new DeliveryCacheDefinition
            {
                Id = Guid.NewGuid(), RepoId = Guid.NewGuid(), FlowName = "wells-cache", Scope = "dev", Origin = "dimension", SourceObject = $"{flow.Name}/Wellbore",
                RelativePath = "cache/wells-cache.yaml", Name = "Wellbores", EntityType = "lookup--Wellbores", FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var captured = await Assert.ThrowsAsync<DeliveryException>(() => DimensionRemoval.RemoveAsync(ledger, wellbores, "user:admin", _clock, CancellationToken.None));
        Assert.Contains("captured by type Wellbores of cache flow wells-cache in partition dev", captured.Message, StringComparison.Ordinal);
        Assert.Equal(before, await RowsAsync(wellbores.DimensionId));
        await using (var db = _db.CreateDbContext())
        {
            db.DeliveryCacheDefinitions.RemoveRange(db.DeliveryCacheDefinitions.Where(c => c.FlowName == "wells-cache"));
            await db.SaveChangesAsync();
        }

        // Removed: every row of it, and nothing of the other dimension.
        var removed = await DimensionRemoval.RemoveAsync(ledger, wellbores, "user:admin", _clock, CancellationToken.None);
        Assert.Equal(
            (wellbores.DimensionId, "Wellbore", before[1], before[2], before[0], before[5], before[3], before[4]),
            (removed.DimensionId, removed.Name, removed.Values, removed.Keys, removed.Builds, removed.Changes, removed.Attributes, removed.Texts));
        Assert.All(await RowsAsync(wellbores.DimensionId), count => Assert.Equal(0L, count));
        Assert.Equal(others, await RowsAsync(sources.DimensionId));
        Assert.Null(await ledger.GetDimensionAsync(wellbores.DimensionId));

        // The removal is an activity of the flow, with who removed it and what went; one that finds nothing to remove fails.
        var activity = Assert.Single(await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = flow.LedgerId, Kind = DimensionRemoval.ActivityKind }));
        Assert.Equal(("user:admin", "completed"), (activity.Actor, activity.Outcome));
        Assert.StartsWith("Removed dimension Wellbore of wells-dimensions in dev: 3 value(s), 3 key(s)", activity.Summary, StringComparison.Ordinal);
        var twice = await Assert.ThrowsAsync<DeliveryException>(() => DimensionRemoval.RemoveAsync(ledger, wellbores, "user:admin", _clock, CancellationToken.None));
        Assert.Contains("no longer in the ledger", twice.Message, StringComparison.Ordinal);
        Assert.Contains(await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = flow.LedgerId, Kind = DimensionRemoval.ActivityKind }), a => a.Outcome == "failed");
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
