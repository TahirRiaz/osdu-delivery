using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Model;
using SqlFlow.Execution;
using SqlFlow.Orchestration;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A cache flow that works in partitions, run through the cache executor against a real ingestion table on SQL Server
/// (docs/partitions-design.md section 6): a run naming every partition refreshes each partition's cache in turn, a run
/// naming one refreshes that one, a run naming none refreshes the registry's default, each partition resolves with its own
/// configuration, and a partition that fails leaves the others refreshed. A flow naming neither partitions nor a header
/// builds the partitions the registry holds. Every test works in a schema and a pair of partitions of its own.
/// </summary>
[Collection(SqlServerSuite.Name)]
public sealed class PartitionCacheRunTests : IAsyncLifetime, IDisposable
{
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];
    private readonly string _root = Samples.NewTempDirectory();
    private readonly OsduTestDatabase _db = new();

    private string Schema => "pk_" + _suffix;

    private string Variable => "SQLFLOW_PARTITION_DB_" + _suffix;

    private string Dev => "dev" + _suffix;

    private string Test => "test" + _suffix;

    private static string Database => new SqlConnectionStringBuilder(OsduTestServer.ConnectionString).InitialCatalog;

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable(Variable, OsduTestServer.ConnectionString);
        await ExecuteAsync($"CREATE SCHEMA [{Schema}];");
        await ExecuteAsync($"""
            CREATE TABLE [{Schema}].[CurveDictionary] (mnemonic nvarchar(100) NULL, curve_family nvarchar(200) NULL, DeletedDate_DW datetime2 NULL);
            INSERT INTO [{Schema}].[CurveDictionary] (mnemonic, curve_family) VALUES (N'GR', N'Gamma Ray'), (N'DT', N'Compressional Slowness');
            """);
    }

    public async Task DisposeAsync()
    {
        await ExecuteAsync($"DROP TABLE IF EXISTS [{Schema}].[CurveDictionary]; DROP SCHEMA IF EXISTS [{Schema}];");
        Environment.SetEnvironmentVariable(Variable, null);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(OsduTestServer.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The test's cache flow, naming both of its partitions, or when <paramref name="registry"/> is set, neither them nor a
    /// header, so it builds the partitions the registry holds; written beside the test's files.
    /// </summary>
    private (CacheDefinition Flow, string Path) Flow(bool registry = false)
    {
        var path = System.IO.Path.Combine(_root, "cache", "lookups.yaml");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""
            flowType: cache
            name: lookups-{{_suffix}}
            {{(registry ? string.Empty : $"partitions: [{Dev}, {Test}]")}}
            source:
              connection: ${env:{{Variable}}}
            types:
              - name: CurveDictionary
                table: {{Database}}.{{Schema}}.CurveDictionary
                key: mnemonic
                fields: [curve_family]
            """.ReplaceLineEndings("\n"));
        return (new DeliveryDocumentLoader().LoadCache(path), path);
    }

    /// <summary>The run values naming <paramref name="partition"/>.</summary>
    private static Dictionary<string, string> Naming(string partition)
        => new(StringComparer.Ordinal) { ["partition"] = partition };

    private async Task<DocumentExecutionResult> RunAsync(RunParameters parameters, IPartitionRegistry? partitions = null, bool registryFlow = false)
    {
        var engine = Samples.Engine(ledger: null, cache: _db.Caches(), partitions: partitions);
        using var provider = new ServiceCollection().AddSingleton(engine).BuildServiceProvider();
        var (flow, path) = Flow(registryFlow);
        return await new CacheExecutor(provider).ExecuteAsync(
            new CacheFlowDocument { Flow = flow }, path, new DocumentExecutionOptions { Parameters = parameters }, CancellationToken.None);
    }

    [Fact]
    public async Task A_run_naming_every_partition_builds_each_partition_s_cache_in_turn()
    {
        var result = await RunAsync(new RunParameters { Values = Naming(PartitionNames.Every) });

        Assert.True(result.Success, result.Error);
        var outcome = Assert.IsType<CachePartitionsOutcome>(result.Result);
        Assert.Equal([Dev, Test], outcome.Partitions.Select(p => p.Partition));
        Assert.All(outcome.Partitions, p => Assert.True(Assert.IsType<CacheRefreshOutcome>(p.Outcome).Written));

        foreach (var partition in new[] { Dev, Test })
        {
            var version = Assert.Single(await _db.Caches().ListVersionsAsync(partition));
            var cached = (await _db.Caches().LoadAsync(partition, version.Version))!.Type("CurveDictionary")!;
            Assert.Equal(["DT", "GR"], cached.Items.Select(i => i.Id).Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public async Task A_run_naming_a_partition_builds_that_partition_s_cache_alone()
    {
        var result = await RunAsync(new RunParameters { Values = new Dictionary<string, string>(StringComparer.Ordinal) { ["partition"] = Test } });

        Assert.True(result.Success, result.Error);
        Assert.Equal(Test, Assert.IsType<CacheRefreshOutcome>(result.Result).Scope);
        Assert.Empty(await _db.Caches().ListVersionsAsync(Dev));
        Assert.Single(await _db.Caches().ListVersionsAsync(Test));

        var unknown = await RunAsync(new RunParameters { Values = new Dictionary<string, string>(StringComparer.Ordinal) { ["partition"] = "prod" } });
        Assert.False(unknown.Success);
        Assert.Contains("builds no cache for partition 'prod'", unknown.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_partition_resolves_with_its_own_configuration_and_one_that_fails_leaves_the_others_refreshed()
    {
        // The value set for no partition names a database nothing answers on; the test partition's own value names the real
        // one. The test partition resolves with its own, and the dev partition, with the unreachable one, fails alone.
        var unreachable = "Server=tcp:127.0.0.1,1;Database=none;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=1";
        var payload = new DeliveryRunPayload
        {
            References = new Dictionary<string, string>(StringComparer.Ordinal) { [Variable] = unreachable },
            PartitionReferences = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                [Test] = new Dictionary<string, string>(StringComparer.Ordinal) { [Variable] = OsduTestServer.ConnectionString },
            },
        };

        var result = await RunAsync(new RunParameters { Payload = payload.ToJson(), Values = Naming(PartitionNames.Every) });

        Assert.False(result.Success);
        Assert.Contains($"partition '{Dev}'", result.Error, StringComparison.Ordinal);
        Assert.Contains($"'{Test}' completed", result.Error, StringComparison.Ordinal);
        var outcome = Assert.IsType<CachePartitionsOutcome>(result.Result);
        Assert.NotNull(outcome.Partitions.Single(p => p.Partition == Dev).Error);
        Assert.True(Assert.IsType<CacheRefreshOutcome>(outcome.Partitions.Single(p => p.Partition == Test).Outcome).Written);
        Assert.Empty(await _db.Caches().ListVersionsAsync(Dev));
        Assert.Single(await _db.Caches().ListVersionsAsync(Test));
    }

    [Fact]
    public async Task A_run_naming_no_partition_builds_the_registry_s_default_when_the_flow_names_it()
    {
        var result = await RunAsync(RunParameters.None, FixedPartitionRegistry.Of(Test, Dev, Test));

        Assert.True(result.Success, result.Error);
        Assert.Equal(Test, Assert.IsType<CacheRefreshOutcome>(result.Result).Scope);
        Assert.Empty(await _db.Caches().ListVersionsAsync(Dev));

        // With no default among the flow's partitions, a run has to name one.
        var unsettled = await RunAsync(RunParameters.None, FixedPartitionRegistry.Of("prod", "prod", Dev));
        Assert.False(unsettled.Success);
        Assert.Contains("the default partition 'prod' is not one of them", unsettled.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_flow_naming_no_partitions_builds_the_registered_ones_the_default_when_none_is_named()
    {
        var registry = FixedPartitionRegistry.Of(Dev, Dev, Test);

        var fallback = await RunAsync(RunParameters.None, registry, registryFlow: true);
        Assert.True(fallback.Success, fallback.Error);
        Assert.Equal(Dev, Assert.IsType<CacheRefreshOutcome>(fallback.Result).Scope);

        var every = await RunAsync(new RunParameters { Values = Naming(PartitionNames.Every) }, registry, registryFlow: true);
        Assert.True(every.Success, every.Error);
        Assert.Equal([Dev, Test], Assert.IsType<CachePartitionsOutcome>(every.Result).Partitions.Select(p => p.Partition));
        Assert.Single(await _db.Caches().ListVersionsAsync(Test));

        var unregistered = await RunAsync(new RunParameters { Values = Naming("prod" + _suffix) }, registry, registryFlow: true);
        Assert.False(unregistered.Success);
        Assert.Contains($"'prod{_suffix}' is not registered", unregistered.Error, StringComparison.Ordinal);

        var none = await RunAsync(RunParameters.None, FixedPartitionRegistry.Empty, registryFlow: true);
        Assert.False(none.Success);
        Assert.Contains("No partition is registered", none.Error, StringComparison.Ordinal);
    }
}
