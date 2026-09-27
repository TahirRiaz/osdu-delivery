using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Validation;
using SqlFlow.Execution;
using SqlFlow.Orchestration;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// One delivery flow and one generic mapping delivered to two partitions (docs/partitions-design.md sections 3 and 4): the
/// sample WellLog flow naming dev, which keeps the ledger it had, and a partition of the test's own, over the sample estate,
/// the real WellLog schema, each partition's cache and a fake protocol. A run for a partition reads that partition's cache,
/// mints every id and reference in it, and keeps its records in that partition's ledger; the mapping, and the fixtures it
/// was captured with in dev, are the same for both.
/// </summary>
[Collection(SqlServerSuite.Name)]
public sealed partial class PartitionDeliveryTests : IDisposable
{
    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];

    /// <summary>The flow's name, its own so its ledgers hold nothing another test wrote.</summary>
    private string Name => "partitioned-welllog-" + _suffix;

    /// <summary>The second partition, its own so its cache holds nothing another test wrote.</summary>
    private string Other => "t" + _suffix;

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>The sample flow naming its partitions: dev keeping the ledger, and <see cref="Other"/>; no partition header.</summary>
    private FlowDefinition Partitioned()
    {
        var flow = Samples.InFolder(Samples.LocalFlow(_root), _root);
        var headers = flow.Target.Headers
            .Where(h => !h.Key.Equals(CacheScope.PartitionHeader, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(h => h.Key, h => h.Value, StringComparer.OrdinalIgnoreCase);
        return flow with
        {
            Name = Name,
            Partitions = [new DeclaredPartition(Samples.SamplePartition, KeepsLedger: true), new DeclaredPartition(Other)],
            Partition = null,
            Target = flow.Target with { Headers = headers },
        };
    }

    /// <summary>
    /// The caches of both partitions: dev's as the suites import it, and <see cref="Other"/>'s holding the same lookup tables
    /// and reference records under that partition's ids, as a capture of it would.
    /// </summary>
    private async Task<ICacheStore> CachesAsync()
    {
        var caches = _db.Caches();
        await Samples.ImportSampleCacheAsync(caches);
        await WellLogVersions.ImportPartitionCacheAsync(caches, Other, "partitioned-" + _suffix);
        return caches;
    }

    /// <summary>Every OSDU id or reference <paramref name="node"/> holds anywhere: each string written as partition:entity--Type:key.</summary>
    private static IEnumerable<string> Ids(JsonNode? node) => node switch
    {
        JsonObject o => o.SelectMany(p => Ids(p.Value)),
        JsonArray a => a.SelectMany(Ids),
        JsonValue v when v.TryGetValue<string>(out var text) && OsduId().IsMatch(text) => [text],
        _ => [],
    };

    [GeneratedRegex(@"^[\w\-\.]+:[\w\-\.]+--[\w\-\.]+:")]
    private static partial Regex OsduId();

    [Fact]
    public async Task A_fixture_renders_against_the_cache_of_the_partition_it_is_written_for_wherever_the_mapping_runs()
    {
        // The sample mapping's fixtures are written for dev (fixtureDefaults). Run for another partition, whose cache holds
        // the same reference data under its own ids, they still render against dev's cache, which holds theirs.
        var mapping = new MappingCatalog(Samples.Mappings, new DeliveryDocumentLoader()).Load(WellLogVersions.CurrentMapping);
        var schema = (await Samples.SampleTemplates.LoadAsync(mapping.Template))!;
        var searches = await RenderResolver.SearchesAsync(Samples.SampleTemplates, mapping);
        var caches = await CachesAsync();
        var other = (await caches.LoadAsync(Other, (await caches.CurrentVersionAsync(Other))!))!;
        var context = new RenderContext
        {
            MappingReference = mapping.Reference,
            CacheScope = Other,
            CacheVersion = other.Version,
            SchemaSnapshotVersion = schema.Version,
            Parameters = new Dictionary<string, string>(mapping.FixtureParameters, StringComparer.Ordinal) { [RenderContext.DataPartitionParameter] = Other },
            SystemProperties = SystemProperties.Pinned(other.SystemProperties),
        };

        Assert.Equal([Samples.SamplePartition], Preflight.FixturePartitions(mapping, Other));
        Assert.Empty(Preflight.FixturePartitions(mapping, Samples.SamplePartition));
        Assert.Empty(Preflight.FixturePartitions(mapping, scope: null));

        // Given no version of dev's cache, every fixture says it cannot render, and why; nothing else is wrong.
        var unrendered = Preflight.Check(mapping, schema, other, context, sourceColumns: null, searches);
        Assert.Equal(mapping.Fixtures.Count, unrendered.Count);
        Assert.All(unrendered, i => Assert.Contains($"is written for partition '{Samples.SamplePartition}'", i.Message, StringComparison.Ordinal));

        var fixtureCaches = await RenderResolver.FixtureCachesAsync(caches, mapping, Other);
        Assert.Equal([Samples.SamplePartition], fixtureCaches.Keys);
        Assert.Empty(Preflight.Check(mapping, schema, other, context, sourceColumns: null, searches, fixtureCaches: fixtureCaches));
    }

    [Fact]
    public async Task Each_partition_delivers_the_same_records_under_its_own_ids_into_its_own_ledger()
    {
        var tables = await SampleEstate.BuildAsync(_root, _clock.GetUtcNow().UtcDateTime.AddMinutes(-5), time: _clock);
        var protocol = new FakeProtocol();
        var engine = Samples.Engine(_db.Ledger(_clock), _clock, new FakeProtocolFactory(protocol), cache: await CachesAsync(), sources: tables);
        using var provider = new ServiceCollection().AddSingleton(engine).AddSingleton<PartitionLedgers>().BuildServiceProvider();
        var flow = Partitioned();

        Task<DocumentExecutionResult> RunAsync(string? partition)
        {
            var values = new Dictionary<string, string>(SampleEstate.Values, StringComparer.Ordinal);
            if (partition is not null)
            {
                values[PartitionNames.RunValue] = partition;
            }

            return new DeliveryExecutor(provider).ExecuteAsync(
                new DeliveryFlowDocument { Source = SourceDefinition.Of(flow) },
                flow.SourcePath!,
                new DocumentExecutionOptions { Parameters = new RunParameters { Values = values } },
                CancellationToken.None);
        }

        var toOther = await RunAsync(Other);
        Assert.True(toOther.Success, toOther.Error);
        var other = protocol.Deliveries.ToList();
        Assert.Equal(SampleEstate.Logs().Count, other.Count);

        var toDev = await RunAsync(Samples.SamplePartition);
        Assert.True(toDev.Success, toDev.Error);
        var dev = protocol.Deliveries.Skip(other.Count).ToList();
        Assert.Equal(SampleEstate.Logs().Count, dev.Count);

        // The same records, each delivered once to each partition, every id and reference it carries minted in that one.
        Assert.True(other.Select(d => d.Key).ToHashSet().SetEquals(dev.Select(d => d.Key)));
        foreach (var (sent, partition) in other.Select(d => (d, Other)).Concat(dev.Select(d => (d, Samples.SamplePartition))))
        {
            Assert.StartsWith(partition + ":work-product-component--WellLog:", sent.TargetId, StringComparison.Ordinal);
            var ids = Ids(sent.Document).ToList();
            Assert.NotEmpty(ids);
            Assert.All(ids, id => Assert.StartsWith(partition + ":", id, StringComparison.Ordinal));
        }

        // Dev keeps the ledger the flow kept before it named its partitions; the other partition keeps one of its own.
        await using var db = _db.CreateDbContext();
        Assert.Equal(dev.Count, await db.DeliveryRecords.CountAsync(r => r.FlowId == FlowId.Of(Name)));
        Assert.Equal(other.Count, await db.DeliveryRecords.CountAsync(r => r.FlowId == FlowId.Of(Name, Other)));
        Assert.True(await db.DeliveryRecords.Where(r => r.FlowId == FlowId.Of(Name, Other)).AllAsync(r => r.TargetId!.StartsWith(Other + ":")));

        // A run of a flow naming two partitions that names neither is refused, and delivers nothing.
        var unnamed = await RunAsync(partition: null);
        Assert.False(unnamed.Success);
        Assert.Contains("name the one this run or request targets", unnamed.Error, StringComparison.Ordinal);
        Assert.Equal(other.Count + dev.Count, protocol.Deliveries.Count);
    }
}
