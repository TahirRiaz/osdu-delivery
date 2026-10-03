using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core;
using SqlFlow.Core.Compute;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The reads the explorer's dimension builder makes of OSDU (osdu/docs/explorer.md, Building a dimension), against a stand-in
/// of the search service as a build reads it: the records of a kind one at a time, the commonest keys of a path, the records
/// a key names and those they name in turn, each followed as a build follows it, long records cut where a page would drown,
/// a query the service refuses answered with its words; and one key made into its row exactly as a build makes it, through
/// the build's own labeler, collector display, cleaner and attribute assembly. Then the same through the explorer's operation,
/// as the control plane runs it.
/// </summary>
public class DimensionSamplerTests
{
    private const string WellLog = "osdu:wks:work-product-component--WellLog:1.4.0";
    private const string WellboreKind = "osdu:wks:master-data--Wellbore:1.3.0";
    private const string W1 = "dev:master-data--Wellbore:W1";
    private const string W2 = "dev:master-data--Wellbore:W2";
    private const string Norway = "dev:master-data--GeoPoliticalEntity:Norway";
    private const string Rogaland = "dev:master-data--GeoPoliticalEntity:Rogaland";
    private const string Sverdrup = "dev:master-data--Field:JohanSverdrup";
    private const string CountryName = "data[GeoPoliticalEntityTypeID*=GeoPoliticalEntityType:Country:].GeoPoliticalEntityName";

    private static readonly OsduField WellboreId = OsduField.Text("data.WellboreID");
    private static readonly OsduField Source = OsduField.Text("data.Source");

    /// <summary>
    /// Wellbore W1, of field Johan Sverdrup, in the county of Rogaland and the country of Norway (the county named first, so
    /// only a filter reads the country), with three logs, two from Recall and one from PetroDB; W2, nameless, with one log
    /// holding no source; and a log naming no record at all.
    /// </summary>
    private static FakeDimensionPlatform Estate()
    {
        var platform = new FakeDimensionPlatform();
        platform.Add(Norway, "osdu:wks:master-data--GeoPoliticalEntity:1.0.0", new JsonObject
        {
            ["GeoPoliticalEntityName"] = "Norway", ["GeoPoliticalEntityTypeID"] = "dev:reference-data--GeoPoliticalEntityType:Country:",
        });
        platform.Add(Rogaland, "osdu:wks:master-data--GeoPoliticalEntity:1.0.0", new JsonObject
        {
            ["GeoPoliticalEntityName"] = "Rogaland", ["GeoPoliticalEntityTypeID"] = "dev:reference-data--GeoPoliticalEntityType:County:",
        });
        platform.Add(Sverdrup, "osdu:wks:master-data--Field:1.0.0", new JsonObject { ["FieldName"] = "JOHAN SVERDRUP" });
        platform.Add(W1, WellboreKind, new JsonObject
        {
            ["FacilityName"] = "  NO 16/2-9 S ",
            ["FacilityID"] = "ad215042",
            ["GeoContexts"] = new JsonArray(
                new JsonObject { ["FieldID"] = Sverdrup + ":" },
                new JsonObject { ["GeoPoliticalEntityID"] = Rogaland + ":" },
                new JsonObject { ["GeoPoliticalEntityID"] = Norway + ":" }),
        });
        platform.Add(W2, WellboreKind, new JsonObject { ["FacilityID"] = "bc771003" });
        platform.Add("dev:work-product-component--WellLog:L1", WellLog, new JsonObject { ["WellboreID"] = W1 + ":", ["Source"] = "Recall" });
        platform.Add("dev:work-product-component--WellLog:L2", WellLog, new JsonObject { ["WellboreID"] = W1 + ":", ["Source"] = "Recall" });
        platform.Add("dev:work-product-component--WellLog:L3", WellLog, new JsonObject { ["WellboreID"] = W1 + ":", ["Source"] = "dev:reference-data--Source:Petro%20DB:" });
        platform.Add("dev:work-product-component--WellLog:L4", WellLog, new JsonObject { ["WellboreID"] = W2 + ":" });
        platform.Add("dev:work-product-component--WellLog:L5", WellLog, new JsonObject { ["WellboreID"] = "unknown-well", ["Source"] = "Recall" });
        return platform;
    }

    private static DimensionSampler Sampler(HttpMessageHandler platform)
    {
        var runtime = new HttpRuntime(
            new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
            new SecretResolver([new EnvSecretProvider()]), new TestClock(), platform, allowLoopback: true);
        var client = new OsduHttpClient(
            runtime, FakeDimensionPlatform.Endpoint, new TargetAuth { Type = TargetAuthType.None },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
        return new DimensionSampler(new OsduSearch(client, RecordExplorer.QueryPath, RetrievalSource.DefaultSearchPath, NullLogger.Instance), NullLogger.Instance);
    }

    private static DimensionSampleRequest Request(string? key = null, int at = 0, params IReadOnlyList<string>[] trails) => new()
    {
        Kind = WellLog,
        Path = "data.WellboreID",
        KeyField = DimensionFieldWire.Of(WellboreId),
        Key = key,
        At = at,
        Trails = trails,
    };

    [Fact]
    public async Task Before_a_key_is_picked_the_records_of_the_kind_are_shown_one_at_a_time_whole()
    {
        var platform = Estate();
        var sampler = Sampler(platform);

        var first = await sampler.SampleAsync(new DimensionSampleRequest { Kind = WellLog }, CancellationToken.None);
        var third = await sampler.SampleAsync(new DimensionSampleRequest { Kind = WellLog, At = 2 }, CancellationToken.None);

        Assert.Equal(5, first.Total);
        Assert.Equal("dev:work-product-component--WellLog:L1", first.Record!.Id);
        Assert.Equal(WellLog, first.Record.Kind);
        Assert.Equal("dev:work-product-component--WellLog:L3", third.Record!.Id);
        Assert.Equal(2, third.At);

        // A whole record, as the search holds it, and nothing of keys or trails before a key is picked.
        Assert.Equal("Recall", first.Record.Record["data"]!["Source"]!.GetValue<string>());
        Assert.Null(first.Keys);
        Assert.Empty(first.Trails);
        Assert.Null(first.Refusal);
        var asked = JsonNode.Parse(platform.Calls[0].Body!)!;
        Assert.Null(asked["returnedFields"]);
        Assert.Equal(1, asked["limit"]!.GetValue<int>());
        OsduContracts.AssertConform(platform.Calls, null, OsduContracts.Search);
    }

    [Fact]
    public async Task With_a_key_path_the_commonest_keys_are_listed_most_records_first()
    {
        var sample = await Sampler(Estate()).SampleAsync(Request(), CancellationToken.None);

        Assert.Equal([(W1 + ":", 3L), (W2 + ":", 1L), ("unknown-well", 1L)], sample.Keys!.Select(k => (k.Key, k.Count)));
        Assert.False(sample.MoreKeys);
        Assert.Equal("data.WellboreID", sample.KeyField!.Path);
        Assert.False(sample.KeyFieldGuessed);
    }

    [Fact]
    public async Task More_keys_than_a_page_offers_are_said_to_be_more()
    {
        var platform = new FakeDimensionPlatform();
        for (var i = 0; i < DimensionSampler.ExampleKeys + 5; i++)
        {
            platform.Add($"dev:work-product-component--WellLog:L{i}", WellLog, new JsonObject { ["WellboreID"] = $"dev:master-data--Wellbore:W{i:D2}:" });
        }

        var sample = await Sampler(platform).SampleAsync(Request(), CancellationToken.None);

        Assert.Equal(DimensionSampler.ExampleKeys, sample.Keys!.Count);
        Assert.True(sample.MoreKeys);
    }

    [Fact]
    public async Task With_a_key_the_record_shown_is_one_holding_it_and_the_total_is_of_those()
    {
        var sample = await Sampler(Estate()).SampleAsync(Request(W2 + ":"), CancellationToken.None);

        Assert.Equal(1, sample.Total);
        Assert.Equal("dev:work-product-component--WellLog:L4", sample.Record!.Id);
    }

    [Fact]
    public async Task A_guessed_key_field_is_said_to_be_a_guess()
    {
        var sample = await Sampler(Estate()).SampleAsync(Request() with { KeyFieldGuessed = true }, CancellationToken.None);

        Assert.True(sample.KeyFieldGuessed);
        Assert.Contains(sample.Notes, n => n.Contains("is read here as text", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_trail_reads_the_record_the_key_names_and_follows_its_references_as_a_build_does()
    {
        var platform = Estate();
        var sample = await Sampler(platform).SampleAsync(
            Request(W1 + ":", 0, [], ["data.GeoContexts.GeoPoliticalEntityID"], ["data.GeoContexts.FieldID"], ["data.GeoContexts[FieldID*=Sverdrup].FieldID"]),
            CancellationToken.None);

        Assert.Equal(4, sample.Trails.Count);
        var own = sample.Trails[0];
        Assert.Equal([W1], own.Reached);
        Assert.Equal(W1, Assert.Single(own.Records).Id);
        Assert.Null(own.Problem);

        // Every political entity the wellbore names, in the order it names them, each read whole.
        var entities = sample.Trails[1];
        Assert.Equal([Rogaland, Norway], entities.Reached);
        Assert.Equal([Rogaland, Norway], entities.Records.Select(r => r.Id));
        Assert.Equal("Norway", entities.Records[1].Record["data"]!["GeoPoliticalEntityName"]!.GetValue<string>());

        Assert.Equal(Sverdrup, Assert.Single(sample.Trails[2].Records).Id);
        Assert.Equal(Sverdrup, Assert.Single(sample.Trails[3].Records).Id);

        // Each record was read once, whichever trails reached it: one search per entity type and step.
        var idSearches = platform.Calls.Select(c => JsonNode.Parse(c.Body!)!).Where(b => b["query"]?.GetValue<string>()?.StartsWith("id:", StringComparison.Ordinal) == true).ToList();
        Assert.Equal(3, idSearches.Count);
        Assert.Equal(["*:*:master-data--Wellbore:*", "*:*:master-data--GeoPoliticalEntity:*", "*:*:master-data--Field:*"], idSearches.Select(b => b["kind"]!.GetValue<string>()));
        OsduContracts.AssertConform(platform.Calls, null, OsduContracts.Search);
    }

    [Fact]
    public async Task A_trail_says_why_it_stops_short()
    {
        var sampler = Sampler(Estate());

        var noRecord = await sampler.SampleAsync(Request("unknown-well", 0, new List<string>()), CancellationToken.None);
        Assert.Contains("names no OSDU record", Assert.Single(noRecord.Trails).Problem, StringComparison.Ordinal);

        var missing = await sampler.SampleAsync(Request("dev:master-data--Wellbore:W9:", 0, [], ["data.GeoContexts.FieldID"]), CancellationToken.None);
        Assert.Equal("The search holds no record dev:master-data--Wellbore:W9.", missing.Trails[0].Problem);
        Assert.Equal("The search holds no record dev:master-data--Wellbore:W9.", missing.Trails[1].Problem);
        Assert.Empty(missing.Trails[1].Records);

        var noReference = await sampler.SampleAsync(Request(W2 + ":", 0, ["data.GeoContexts.FieldID"]), CancellationToken.None);
        Assert.Equal("The record reached holds no record id at data.GeoContexts.FieldID.", noReference.Trails[0].Problem);
        Assert.Empty(noReference.Trails[0].Records);
    }

    [Fact]
    public async Task A_step_follows_at_most_as_many_references_as_a_build_does()
    {
        var platform = new FakeDimensionPlatform();
        var names = new JsonArray();
        for (var i = 0; i < DimensionLabeler.MaxReferencesPerStep + 6; i++)
        {
            var id = $"dev:master-data--Field:F{i:D2}";
            platform.Add(id, "osdu:wks:master-data--Field:1.0.0", new JsonObject { ["FieldName"] = $"Field {i}" });
            names.Add(new JsonObject { ["FieldID"] = id + ":" });
        }

        platform.Add(W1, WellboreKind, new JsonObject { ["GeoContexts"] = names });
        var sample = await Sampler(platform).SampleAsync(Request(W1 + ":", 0, ["data.GeoContexts.FieldID"]), CancellationToken.None);

        Assert.Equal(DimensionLabeler.MaxReferencesPerStep, sample.Trails[0].Reached.Count);
        Assert.Equal(DimensionLabeler.MaxReferencesPerStep, sample.Trails[0].Records.Count);
    }

    [Fact]
    public void A_long_array_and_a_long_text_are_cut_where_a_page_would_drown_and_say_where()
    {
        var curves = new JsonArray(Enumerable.Range(0, 120).Select(i => (JsonNode?)new JsonObject { ["Mnemonic"] = $"C{i}" }).ToArray());
        var record = new JsonObject
        {
            ["id"] = "dev:work-product-component--WellLog:L1",
            ["kind"] = WellLog,
            ["data"] = new JsonObject { ["Curves"] = curves, ["Notes"] = new string('x', 5_000), ["Tags"] = new JsonArray("a", new string('y', 3_000)) },
        };

        var shown = DimensionSampler.Shown(record);

        Assert.Equal(DimensionSampler.ShownItems, shown.Record["data"]!["Curves"]!.AsArray().Count);
        Assert.Equal(DimensionSampler.ShownText, shown.Record["data"]!["Notes"]!.GetValue<string>().Length);
        Assert.Contains(new DimensionSampleCut("data.Curves", 120, DimensionSampler.ShownItems), shown.Cut);
        Assert.Contains(new DimensionSampleCut("data.Notes", 5_000, DimensionSampler.ShownText), shown.Cut);
        Assert.Contains(new DimensionSampleCut("data.Tags.1", 3_000, DimensionSampler.ShownText), shown.Cut);

        // The record itself is left as it was read.
        Assert.Equal(120, record["data"]!["Curves"]!.AsArray().Count);
    }

    [Fact]
    public async Task A_query_the_service_refuses_is_answered_with_its_words_and_nothing_else_is_read()
    {
        var platform = Estate();
        platform.Fail = _ => HttpStatusCode.BadRequest;

        var sample = await Sampler(platform).SampleAsync(Request(W1 + ":", 0, new List<string>()), CancellationToken.None);

        Assert.NotNull(sample.Refusal);
        Assert.Null(sample.Record);
        Assert.Empty(sample.Trails);
        Assert.Single(platform.Calls);
    }

    [Fact]
    public async Task A_sample_shows_only_the_first_records()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Sampler(Estate()).SampleAsync(Request(at: DimensionSampler.ExampleRecords), CancellationToken.None));
    }

    private static DimensionSpec Spec(string? unlabelled = "Not specified", IReadOnlyList<CleanStep>? clean = null, IReadOnlyList<string>? label = null) => new()
    {
        Name = "Wellbore",
        Kind = WellLog,
        Path = "data.WellboreID",
        Label = label ?? ["data.FacilityName"],
        Unlabelled = unlabelled,
        Attributes =
        [
            new DimensionAttributeSpec("UUID", ["data.FacilityID"]),
            new DimensionAttributeSpec("Field", ["data.GeoContexts.FieldID", "data.FieldName"]),
            new DimensionAttributeSpec("Country", ["data.GeoContexts.GeoPoliticalEntityID", CountryName]),
            new DimensionAttributeSpec("Source", [], "data.Source"),
        ],
        Clean = clean ?? [],
    };

    private static Dictionary<string, OsduField> Collected() => new(StringComparer.Ordinal) { ["Source"] = Source };

    [Fact]
    public async Task An_example_is_the_row_a_build_makes_of_the_key()
    {
        var spec = Spec(clean: [new CleanStep { Kind = CleanStepKind.Upper }]);
        var example = await Sampler(Estate()).ExampleAsync(
            spec, W1 + ":", null, WellboreId, Collected(), DimensionCleaner.Build(spec.Clean, _ => throw new InvalidOperationException()), CancellationToken.None);

        // The label as read, trimmed as a build reads it, from the record the key names; the value, the label cleaned.
        Assert.Equal(("NO 16/2-9 S", W1), (example.Label, example.LabelFrom));
        Assert.Equal("NO 16/2-9 S", example.Value);
        Assert.Null(example.LeftOut);

        // Each attribute as a build reads it: the country through the entity whose type is Country, not the county named first.
        var attributes = example.Attributes.ToLookup(a => a.Name);
        Assert.Equal("ad215042", Assert.Single(attributes["UUID"]).Value);
        Assert.Equal(("JOHAN SVERDRUP", Sverdrup), (Assert.Single(attributes["Field"]).Value, attributes["Field"].Single().From));
        Assert.Equal(("Norway", Norway), (Assert.Single(attributes["Country"]).Value, attributes["Country"].Single().From));

        // The values its logs collect, each shown as a build shows it (a reference by its decoded code), with their records.
        Assert.Equal([("Recall", 2L), ("Petro DB", 1L)], attributes["Source"].Select(a => (a.Value, a.Records ?? 0)));

        // The records holding the key, and the filter finding them, as a build writes it.
        Assert.Equal(3, example.Records);
        Assert.Equal(DimensionFilters.Of(WellboreId, [W1 + ":"])[0], example.Filter);
    }

    [Fact]
    public async Task What_is_not_read_takes_the_dimensions_value_for_it_and_records_holding_no_collected_value_are_counted()
    {
        var spec = Spec();
        var example = await Sampler(Estate()).ExampleAsync(spec, W2 + ":", null, WellboreId, Collected(), DimensionCleaner.Identity, CancellationToken.None);

        Assert.Null(example.Label);
        Assert.Contains("holds nothing at data.FacilityName", example.Problem, StringComparison.Ordinal);
        Assert.Equal("Not specified", example.Value);
        var attributes = example.Attributes.ToLookup(a => a.Name);
        Assert.Equal("bc771003", Assert.Single(attributes["UUID"]).Value);
        Assert.Equal("Not specified", Assert.Single(attributes["Field"]).Value);
        Assert.Equal("Not specified", Assert.Single(attributes["Country"]).Value);
        Assert.Equal([("Not specified", 1L)], attributes["Source"].Select(a => (a.Value, a.Records ?? 0)));
    }

    [Fact]
    public async Task A_key_naming_no_record_is_valued_by_its_own_text_and_one_without_a_label_by_its_code()
    {
        var sampler = Sampler(Estate());
        var unnamed = await sampler.ExampleAsync(Spec(unlabelled: null), "unknown-well", null, WellboreId, Collected(), DimensionCleaner.Identity, CancellationToken.None);
        Assert.Equal(DimensionLabeler.NamesNoRecord, unnamed.Problem);
        Assert.Equal("unknown-well", unnamed.Value);
        Assert.Equal([("Recall", 1L)], unnamed.Attributes.Where(a => a.Name == "Source").Select(a => (a.Value, a.Records ?? 0)));

        var coded = await sampler.ExampleAsync(Spec(unlabelled: null, label: []), "dev:master-data--Wellbore:W%2F2:", null, WellboreId, Collected(), DimensionCleaner.Identity, CancellationToken.None);
        Assert.Null(coded.Label);
        Assert.Equal("W/2", coded.Value);
    }

    [Fact]
    public async Task Cleaning_that_leaves_nothing_leaves_the_key_out_of_every_value_and_says_why()
    {
        var spec = Spec(clean: [new CleanStep { Kind = CleanStepKind.Replace, Pattern = ".*", With = string.Empty }]);
        var example = await Sampler(Estate()).ExampleAsync(
            spec, W1 + ":", null, WellboreId, Collected(), DimensionCleaner.Build(spec.Clean, _ => throw new InvalidOperationException()), CancellationToken.None);

        Assert.Null(example.Value);
        Assert.Equal("cleaning left nothing", example.LeftOut);
    }

    [Fact]
    public async Task Without_knowing_how_the_key_is_indexed_its_records_and_collected_values_are_not_read_and_it_says_so()
    {
        var platform = Estate();
        var example = await Sampler(platform).ExampleAsync(Spec(), W1 + ":", null, null, Collected(), DimensionCleaner.Identity, CancellationToken.None);

        Assert.Equal("NO 16/2-9 S", example.Label);
        Assert.Null(example.Records);
        Assert.Null(example.Filter);
        Assert.DoesNotContain(example.Attributes, a => a.Name == "Source");
        Assert.Contains(example.Notes, n => n.Contains("How the key is indexed is not known", StringComparison.Ordinal));
        Assert.DoesNotContain(platform.Calls, c => c.Body!.Contains("aggregateBy", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_query_narrows_the_records_counted_and_collected()
    {
        var example = await Sampler(Estate()).ExampleAsync(
            Spec(), W1 + ":", "data.Source.keyword:\"Recall\"", WellboreId, Collected(), DimensionCleaner.Identity, CancellationToken.None);

        Assert.Equal(2, example.Records);
        Assert.Equal([("Recall", 2L)], example.Attributes.Where(a => a.Name == "Source").Select(a => (a.Value, a.Records ?? 0)));
    }

    // ---- Through the explorer's operation, as the control plane runs it ----

    private static async Task<string> FlowFileAsync()
    {
        var root = Samples.NewTempDirectory();
        var flowFile = Path.Combine(root, "explorer-route.yaml");
        await File.WriteAllTextAsync(flowFile, """
            flowType: delivery
            name: explorer-route
            partitions:
              - dev
            source:
              connection: ${env:OSDU_DATA_DB}
              record: { object: OsduData.ing.Record, key: [record_id] }
              work: work
            render:
              mapping: Targeting@1.0.0
            target:
              endpoint: http://localhost
              protocol: storage
            """);
        return flowFile;
    }

    private static ComputeTaskPayload Task(string flowFile, string action, Dictionary<string, string> arguments)
    {
        arguments["flowFile"] = flowFile;
        arguments[ExploreOperation.ActionArgument] = action;
        arguments[DeliveryOperation.PartitionArgument] = "dev";
        var payload = new ComputeTaskPayload { Operation = ExploreOperation.OperationName, SourceRef = "explorer-route", Arguments = arguments };
        payload.Validate([ExploreOperation.OperationName]);
        return payload;
    }

    [Fact]
    public async Task The_operation_samples_through_the_flows_connection_with_the_partition_filled_into_the_query()
    {
        var platform = Estate();
        var operation = new ExploreOperation(Samples.Engine(ledger: null), platform, allowLoopback: true);
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        var request = Request(W1 + ":", 0, [], ["data.GeoContexts.GeoPoliticalEntityID"]) with { Query = "NOT data.Source.keyword:\"{partition}\"" };
        LongArgument.Put(arguments, ExploreOperation.SampleArgument, JsonSerializer.Serialize(request, ExploreOperation.BuilderJson));

        var answered = JsonNode.Parse(await operation.ExecuteAsync(Task(await FlowFileAsync(), ExploreOperation.DimensionSampleAction, arguments), CancellationToken.None))!;

        Assert.Equal(("dev", "explorer-route"), (answered["partition"]!.GetValue<string>(), answered["connection"]!.GetValue<string>()));
        var answer = answered["answer"]!;
        Assert.Equal(W1, answer["trails"]![0]!["records"]![0]!["id"]!.GetValue<string>());
        Assert.Equal(2, answer["trails"]![1]!["records"]!.AsArray().Count);
        Assert.All(platform.Calls, c => Assert.Equal("dev", c.Headers["data-partition-id"]));
        Assert.Contains(platform.Calls, c => c.Body!.Contains("NOT data.Source.keyword:\\\"dev\\\"", StringComparison.Ordinal));
        Assert.DoesNotContain(platform.Calls, c => c.Body!.Contains("{partition}", StringComparison.Ordinal));
        OsduContracts.AssertConform(platform.Calls, null, OsduContracts.Search);
    }

    [Fact]
    public async Task The_operation_makes_an_example_of_the_item_exactly_as_the_builder_wrote_it()
    {
        var platform = Estate();
        var operation = new ExploreOperation(Samples.Engine(ledger: null), platform, allowLoopback: true);
        var draft = new DimensionDraft
        {
            Name = "Wellbore",
            Kind = WellLog,
            Path = "data.WellboreID",
            Label = ["data.FacilityName"],
            Attributes = [new DimensionDraftAttributeSpec("Country", ["data.GeoContexts.GeoPoliticalEntityID", CountryName], null), new DimensionDraftAttributeSpec("Source", null, "data.Source")],
            Clean = [new DimensionDraftCleanStep("lower", null, null)],
        };
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal) { [ExploreOperation.KeyArgument] = W1 + ":" };
        LongArgument.Put(arguments, ExploreOperation.ItemArgument, DimensionBuilder.ToYaml(draft));
        LongArgument.Put(arguments, ExploreOperation.FieldsArgument, JsonSerializer.Serialize(
            new DimensionExampleFields(DimensionFieldWire.Of(WellboreId), new Dictionary<string, DimensionFieldWire> { ["Source"] = DimensionFieldWire.Of(Source) }),
            ExploreOperation.BuilderJson));

        var answered = JsonNode.Parse(await operation.ExecuteAsync(Task(await FlowFileAsync(), ExploreOperation.DimensionExampleAction, arguments), CancellationToken.None))!;
        var example = answered["answer"]!.Deserialize<DimensionExample>(ExploreOperation.BuilderJson)!;

        Assert.Equal(("NO 16/2-9 S", "no 16/2-9 s"), (example.Label, example.Value));
        Assert.Equal("Norway", example.Attributes.Single(a => a.Name == "Country").Value);
        Assert.Equal(["Recall", "Petro DB"], example.Attributes.Where(a => a.Name == "Source").Select(a => a.Value));
        Assert.Equal(3, example.Records);
    }

    [Fact]
    public async Task The_operation_refuses_a_map_step_it_has_no_dictionary_for()
    {
        var operation = new ExploreOperation(Samples.Engine(ledger: null), Estate(), allowLoopback: true);
        var item = DimensionBuilder.ToYaml(new DimensionDraft { Name = "Source", Kind = WellLog, Path = "data.Source" })
            + "    clean:\n      - map: CurveAliases\n";
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal) { [ExploreOperation.KeyArgument] = "Recall" };
        LongArgument.Put(arguments, ExploreOperation.ItemArgument, item);

        var payload = Task(await FlowFileAsync(), ExploreOperation.DimensionExampleAction, arguments);
        var refused = await Assert.ThrowsAsync<SqlFlowException>(() => operation.ExecuteAsync(payload, CancellationToken.None));

        Assert.Contains("reads the dictionary CurveAliases", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A sample the operation refuses, as the control plane refuses it before it is sent.</summary>
    public static TheoryData<string, string> RefusedSamples => new()
    {
        { """{"kind":"osdu:wks:WellLog"}""", "osdu:wks:WellLog" },
        { """{"kind":"osdu:wks:work-product-component--WellLog:1.4.0","at":25}""", "first 25 records" },
        { """{"kind":"osdu:wks:work-product-component--WellLog:1.4.0","trails":[["a","b","c"]]}""", "at most 2 paths" },
        { """{"kind":"osdu:wks:work-product-component--WellLog:1.4.0","trails":[["data[Type=]x"]]}""", "is not a path a trail can follow" },
        { """{"kind":"osdu:wks:work-product-component--WellLog:1.4.0","trails":""", "is not what the dimension builder sends" },
        { """{"path":"data.X"}""", "is not what the dimension builder sends" },
    };

    [Theory]
    [MemberData(nameof(RefusedSamples))]
    public void The_operation_refuses_a_sample_it_cannot_read(string json, string why)
    {
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        LongArgument.Put(arguments, ExploreOperation.SampleArgument, json);
        var payload = new ComputeTaskPayload { Operation = ExploreOperation.OperationName, SourceRef = "explorer-route", Arguments = arguments };

        var refused = Assert.ThrowsAny<SqlFlowException>(() => ExploreOperation.SampleOf(payload, "dev"));

        Assert.Contains(why, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_argument_is_carried_in_pieces_and_read_back_whole()
    {
        foreach (var length in new[] { 0, 1, LongArgument.PieceLength, LongArgument.PieceLength + 1, LongArgument.MaxLength })
        {
            var text = string.Create(length, length, (span, _) =>
            {
                for (var i = 0; i < span.Length; i++)
                {
                    span[i] = (char)('a' + (i % 26));
                }
            });
            var arguments = new Dictionary<string, string>(StringComparer.Ordinal) { ["action"] = "x" };
            LongArgument.Put(arguments, "item", text);
            var payload = new ComputeTaskPayload { Operation = ExploreOperation.OperationName, SourceRef = "flow", Arguments = arguments };

            payload.Validate([ExploreOperation.OperationName]);
            Assert.Equal(text, LongArgument.Read(payload, "item"));
            Assert.Equal(Math.Max(1, (length + LongArgument.PieceLength - 1) / LongArgument.PieceLength), arguments.Count - 1);
        }

        Assert.Throws<SqlFlowException>(() => LongArgument.Put(new Dictionary<string, string>(), "item", new string('x', LongArgument.MaxLength + 1)));
        var none = new ComputeTaskPayload { Operation = ExploreOperation.OperationName, SourceRef = "flow", Arguments = new Dictionary<string, string>() };
        Assert.Null(LongArgument.Read(none, "item"));
        Assert.Throws<SqlFlowException>(() => LongArgument.Require(none, "item"));
    }
}
