using SqlFlow.Core;
using SqlFlow.Core.Lineage;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Snapshots;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A cache type holding a dimension's members (docs/dimension-plan.md, Stage 6): declared by a cache flow with the dimension
/// and the dimension flow building it, captured from the partition's ledger as the dimension's last build left it, one lookup
/// row per member keyed by its clean value with its originals and records, so a mapping finds the member of any original.
/// </summary>
[Collection(SqlServerSuite.Name)]
public sealed class DimensionCacheTests : IDisposable
{
    private const string DimensionFlowName = "welllog-dimensions";

    private static readonly Guid DimensionLedger = FlowId.Of(DimensionFlowName, "dev");

    /// <summary>A cache flow of partition dev whose types are <paramref name="types"/>, each a type's lines in flow style.</summary>
    private static string Flow(params string[] types)
        => "flowType: cache\nname: curve-lookups\nsource:\n  headers: { data-partition-id: dev }\ntypes:\n"
           + string.Concat(types.Select(type => $"  - {type}\n"));

    private const string Curves = "{ dimension: CurveMnemonic, dimensionFlow: welllog-dimensions }";

    private static readonly string Lookups = Flow(
        Curves, "{ name: Mnemonics, dimension: CurveMnemonic, dimensionFlow: welllog-dimensions, onChange: approve }");

    private readonly string _root = Samples.NewTempDirectory();
    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>Writes a completed build of CurveMnemonic with <paramref name="originals"/> (original, member, count), as a build writes it.</summary>
    private async Task BuildAsync(OsduLedger ledger, params (string Original, string Member, long Count)[] originals)
    {
        await ledger.RegisterLedgerAsync(new LedgerEntry
        {
            FlowId = DimensionLedger, Partition = "dev", Kind = LedgerKinds.Dimension, FlowName = DimensionFlowName, LedgerName = DimensionFlowName + "@dev",
        });
        var (dimension, run) = await ledger.StartDimensionRunAsync(
            new DimensionDeclaration
            {
                FlowId = DimensionLedger, FlowName = DimensionFlowName, Name = "CurveMnemonic", Kind = "osdu:wks:work-product-component--WellLog:*",
                Path = "data.Curves.Mnemonic", CleanJson = """[{"kind":"upper"}]""", DefinitionHash = "0123456789abcdef",
            },
            Guid.NewGuid(), "tests", _clock.GetUtcNow().UtcDateTime);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await ledger.WriteDimensionAsync(new DimensionWrite
        {
            DimensionRunId = run.DimensionRunId,
            DimensionId = dimension.DimensionId,
            FlowId = DimensionLedger,
            Field = new DimensionFieldState("text", "data.Curves", "nested(data.Curves, Mnemonic.keyword)", true),
            Originals = originals.Select(o => new DimensionOriginalWrite(o.Original, o.Member, null, null, o.Count, true)).ToList(),
            Members = originals.GroupBy(o => o.Member, StringComparer.Ordinal)
                .Select(g => new DimensionMemberWrite(g.Key, g.Sum(o => o.Count), false, g.Count(), 0, $"filter of {g.Key}", 1)).ToList(),
            Read = DimensionReadCounts.None,
            CompletedUtc = _clock.GetUtcNow().UtcDateTime,
        });
    }

    private string WriteFlow(string yaml)
    {
        var path = Path.Combine(_root, "cache", "curve-lookups.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, yaml);
        return path;
    }

    [Fact]
    public void A_cache_flow_holds_a_dimension_as_a_lookup_table_of_fixed_columns_and_is_ordered_after_the_dimension_flow()
    {
        var loader = new DeliveryDocumentLoader();
        var flow = loader.ParseCache(Lookups, "cache/curve-lookups.yaml");

        var curves = flow.Types[0];
        Assert.Equal(("CurveMnemonic", CacheOrigin.Dimension, "lookup--CurveMnemonic"), (curves.Name, curves.Origin, curves.EntityType));
        Assert.Equal(("CurveMnemonic", DimensionFlowName), (curves.Dimension, curves.DimensionFlow));
        Assert.Equal(DimensionColumns.Value, curves.Key);
        Assert.Equal(["originals", "records"], curves.Fields.Select(f => f.Name));
        Assert.Equal("dimension CurveMnemonic of welllog-dimensions", curves.Describe());
        Assert.Equal(("Mnemonics", CacheChangeMode.Approve), (flow.Types[1].Name, flow.Types[1].OnChange));
        Assert.False(new CacheFlowDocument { Flow = flow }.RequiresRepoTree);

        // Its lineage reads the dimension in the partition, which the dimension flow's build writes.
        var lineage = CacheLineage.Describe(flow);
        var read = Assert.Single(lineage.Datasets, d => d.System == OsduLineage.DimensionSystem);
        Assert.Equal((LineageRelation.Reads, "dev", DimensionFlowName, "CurveMnemonic"), (read.Relation, read.Namespace, read.Group, read.Name));

        string Refused(string yaml) => Assert.Throws<FlowValidationException>(() => loader.ParseCache(yaml, "cache/curve-lookups.yaml")).Message;
        Assert.Contains("names both", Refused(Flow("{ dimension: CurveMnemonic }")), StringComparison.Ordinal);
        Assert.Contains("names both", Refused(Flow("{ name: Curves, dimensionFlow: welllog-dimensions }")), StringComparison.Ordinal);
        Assert.Contains("names more than one origin", Refused(Flow("{ dimension: CurveMnemonic, dimensionFlow: welllog-dimensions, dictionary: CurveAliases }")), StringComparison.Ordinal);
        Assert.Contains("which takes no 'fields'", Refused(Flow("{ dimension: CurveMnemonic, dimensionFlow: welllog-dimensions, fields: [value] }")), StringComparison.Ordinal);
        Assert.Contains("which takes no 'key'", Refused(Flow("{ dimension: CurveMnemonic, dimensionFlow: welllog-dimensions, key: code }")), StringComparison.Ordinal);
        Assert.Contains("is not a dimension's name", Refused(Flow("{ dimension: \"Curve Mnemonic\", dimensionFlow: welllog-dimensions }")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refresh_captures_a_dimension_s_members_so_a_mapping_finds_the_member_of_any_original()
    {
        var ledger = _db.Ledger(_clock);
        var store = _db.Caches();
        var engine = Samples.Engine(ledger, _clock, cache: store);
        var flow = new DeliveryDocumentLoader().LoadCache(WriteFlow(Flow(Curves)));
        var refresher = new CacheRefresher(engine, Samples.Logger<CacheRefresher>());

        // Before the dimension flow has built the dimension, there is nothing to hold, and the refresh says what to build.
        var early = await Assert.ThrowsAsync<DeliveryException>(() => refresher.RefreshAsync(flow, new Dictionary<string, string>(), Guid.NewGuid(), "tests", CancellationToken.None));
        Assert.Contains("has no completed build in partition 'dev'", early.Message, StringComparison.Ordinal);

        await BuildAsync(ledger, ("GR", "GR", 5), ("gr", "GR", 3), ("Gamma Ray", "GR", 1), ("DT", "DT", 4));
        var refreshed = await refresher.RefreshAsync(flow, new Dictionary<string, string>(), Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.True(refreshed.Written);
        var type = Assert.Single(refreshed.Types);
        Assert.Equal(("dimension", 2, "dimension CurveMnemonic of welllog-dimensions"), (type.Origin, type.Items, type.Source));
        // A lookup table lists the names its rows hold, as the version keeps them.
        Assert.Equal(["originals", "records", "value"], type.Fields.Order(StringComparer.Ordinal));

        // A mapping reading `originals to value` cleans any spelling the build found, as the dimension cleaned it.
        var snapshot = (await store.LoadAsync("dev", refreshed.Version))!;
        var members = snapshot.Type("CurveMnemonic")!;
        Assert.Equal("lookup--CurveMnemonic", members.EntityType);
        var gamma = members.Match(DimensionColumns.Originals, "gamma ray")!;
        Assert.Equal("GR", members.Value(gamma, DimensionColumns.Value)!.Text);
        Assert.Equal("9", members.Value(gamma, DimensionColumns.Records)!.Text);
        var originals = members.Value(gamma, DimensionColumns.Originals)!;
        Assert.True(originals.IsSet);
        Assert.Equal(3, originals.Count);
        Assert.Equal("GR", members.Match(DimensionColumns.Originals, "gr")!.Id);
        Assert.Equal("DT", members.Match(DimensionColumns.Value, "DT")!.Id);

        // A plan counts the members and writes nothing; an unchanged dimension writes no version.
        Assert.Equal(2, Assert.Single((await refresher.PlanAsync(flow, new Dictionary<string, string>(), CancellationToken.None)).Types).Records);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False((await refresher.RefreshAsync(flow, new Dictionary<string, string>(), Guid.NewGuid(), "tests", CancellationToken.None)).Written);
    }
}
