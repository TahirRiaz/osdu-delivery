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
/// The reads the explorer's dimension builder makes of OSDU beyond the explorer's own
/// (osdu/docs/reference/concepts/explorer.md, Building a dimension), against a stand-in of the search service as a
/// build reads it: the commonest keys of a path in one search, the query narrowing them, a query the service refuses
/// answered with its words; and one key made into its row exactly as a build makes it, through the build's own labeler,
/// collector display, cleaner and attribute assembly. Then the same through the explorer's operation, as the control
/// plane runs it.
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

    private static DimensionKeysRequest Request(string? query = null) => new()
    {
        Kind = WellLog,
        Query = query,
        Path = "data.WellboreID",
        KeyField = DimensionFieldWire.Of(WellboreId),
    };

    [Fact]
    public async Task The_commonest_keys_are_listed_most_records_first_with_the_records_counted_in_one_search()
    {
        var platform = Estate();
        var keys = await Sampler(platform).KeysAsync(Request(), CancellationToken.None);

        Assert.Equal([(W1 + ":", 3L), (W2 + ":", 1L), ("unknown-well", 1L)], keys.Keys.Select(k => (k.Key, k.Count)));
        Assert.Equal(5, keys.Total);
        Assert.False(keys.MoreKeys);
        Assert.Equal("data.WellboreID", keys.KeyField!.Path);
        Assert.False(keys.KeyFieldGuessed);
        Assert.Null(keys.Refusal);

        // One search: a page of one record, grouped by the key's keyword, which counts the records as it groups them.
        var asked = JsonNode.Parse(Assert.Single(platform.Calls).Body!)!;
        Assert.Equal("data.WellboreID.keyword", asked["aggregateBy"]!.GetValue<string>());
        Assert.Equal(1, asked["limit"]!.GetValue<int>());
        Assert.True(asked["trackTotalCount"]!.GetValue<bool>());
        OsduContracts.AssertConform(platform.Calls, null, OsduContracts.Search);
    }

    [Fact]
    public async Task More_keys_than_the_examples_offer_are_said_to_be_more()
    {
        var platform = new FakeDimensionPlatform();
        for (var i = 0; i < DimensionSampler.ExampleKeys + 5; i++)
        {
            platform.Add($"dev:work-product-component--WellLog:L{i}", WellLog, new JsonObject { ["WellboreID"] = $"dev:master-data--Wellbore:W{i:D2}:" });
        }

        var keys = await Sampler(platform).KeysAsync(Request(), CancellationToken.None);

        Assert.Equal(DimensionSampler.ExampleKeys, keys.Keys.Count);
        Assert.True(keys.MoreKeys);
        Assert.Equal(DimensionSampler.ExampleKeys + 5, keys.Total);
    }

    [Fact]
    public async Task The_query_narrows_the_keys_to_those_its_records_hold()
    {
        var keys = await Sampler(Estate()).KeysAsync(Request("data.Source.keyword:\"Recall\""), CancellationToken.None);

        Assert.Equal([(W1 + ":", 2L), ("unknown-well", 1L)], keys.Keys.Select(k => (k.Key, k.Count)));
        Assert.Equal(3, keys.Total);
    }

    [Fact]
    public async Task A_guessed_key_field_is_said_to_be_a_guess()
    {
        var keys = await Sampler(Estate()).KeysAsync(Request() with { KeyFieldGuessed = true }, CancellationToken.None);

        Assert.True(keys.KeyFieldGuessed);
        Assert.Contains(keys.Notes, n => n.Contains("is read here as text", StringComparison.Ordinal));
        Assert.NotEmpty(keys.Keys);
    }

    [Fact]
    public async Task Without_knowing_how_the_key_is_indexed_the_records_are_counted_and_no_key_is_grouped()
    {
        var platform = Estate();
        var keys = await Sampler(platform).KeysAsync(Request() with { KeyField = null }, CancellationToken.None);

        Assert.Empty(keys.Keys);
        Assert.Equal(5, keys.Total);
        Assert.Contains(keys.Notes, n => n.Contains("is indexed is not known", StringComparison.Ordinal));
        Assert.Null(JsonNode.Parse(Assert.Single(platform.Calls).Body!)!["aggregateBy"]);
    }

    [Fact]
    public async Task A_query_the_service_refuses_is_answered_with_its_words()
    {
        var platform = Estate();
        platform.Fail = _ => HttpStatusCode.BadRequest;

        var keys = await Sampler(platform).KeysAsync(Request(), CancellationToken.None);

        Assert.NotNull(keys.Refusal);
        Assert.Empty(keys.Keys);
        Assert.Equal(0, keys.Total);
        Assert.Single(platform.Calls);
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
        Assert.Equal("it names no OSDU record", unnamed.Problem);
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
    public async Task The_operation_reads_the_keys_through_the_flows_connection_with_the_partition_filled_into_the_query()
    {
        var platform = Estate();
        var operation = new ExploreOperation(Samples.Engine(ledger: null), platform, allowLoopback: true);
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        LongArgument.Put(arguments, ExploreOperation.KeysArgument, JsonSerializer.Serialize(Request("NOT data.Source.keyword:\"{partition}\""), ExploreOperation.BuilderJson));

        var answered = JsonNode.Parse(await operation.ExecuteAsync(Task(await FlowFileAsync(), ExploreOperation.DimensionKeysAction, arguments), CancellationToken.None))!;

        Assert.Equal(("dev", "explorer-route"), (answered["partition"]!.GetValue<string>(), answered["connection"]!.GetValue<string>()));
        var keys = answered["answer"]!.Deserialize<DimensionKeys>(ExploreOperation.BuilderJson)!;
        Assert.Equal(W1 + ":", keys.Keys[0].Key);
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

    /// <summary>A keys request the operation refuses, as the control plane refuses it before it is sent.</summary>
    public static TheoryData<string, string> RefusedKeys => new()
    {
        { """{"kind":"osdu:wks:WellLog","path":"data.WellboreID"}""", "osdu:wks:WellLog" },
        { """{"kind":"osdu:wks:work-product-component--WellLog:1.4.0","path":"data..WellboreID"}""", "is not a property path a key is read at" },
        { """{"kind":"osdu:wks:work-product-component--WellLog:1.4.0","path":"data[Type=x].WellboreID"}""", "is not a property path a key is read at" },
        { """{"kind":"osdu:wks:work-product-component--WellLog:1.4.0","path":" "}""", "is not a property path a key is read at" },
        { """{"kind":"osdu:wks:work-product-component--WellLog:1.4.0","path":""", "is not what the dimension builder sends" },
        { """{"path":"data.X"}""", "is not what the dimension builder sends" },
    };

    [Theory]
    [MemberData(nameof(RefusedKeys))]
    public void The_operation_refuses_keys_it_cannot_read(string json, string why)
    {
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        LongArgument.Put(arguments, ExploreOperation.KeysArgument, json);
        var payload = new ComputeTaskPayload { Operation = ExploreOperation.OperationName, SourceRef = "explorer-route", Arguments = arguments };

        var refused = Assert.ThrowsAny<SqlFlowException>(() => ExploreOperation.KeysOf(payload, "dev"));

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
