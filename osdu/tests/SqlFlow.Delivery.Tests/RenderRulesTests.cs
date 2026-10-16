using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Model;
using SqlFlow.Execution;
using SqlFlow.Orchestration;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// An ordinary scheduled run after the mapping it renders with was edited in place: the rows did not change, yet every record
/// has to be rendered again under the new rules, and only what renders differently is sent. An edit that cannot change a
/// record (a comment) leaves the run incremental.
/// </summary>
[Collection(SqlServerSuite.Name)]
public sealed class RenderRulesTests : IDisposable
{
    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void A_mapping_fingerprint_moves_with_what_renders_and_not_with_comments()
    {
        var original = File.ReadAllText(Path.Combine(Samples.Mappings, "WellLog@1.4.0.yaml"));
        var fingerprint = MappingFingerprint.Of(original, "original");

        Assert.Equal(fingerprint, MappingFingerprint.Of("# A note.\n" + original.Replace("\r\n", "\n", StringComparison.Ordinal), "commented"));
        Assert.NotEqual(fingerprint, MappingFingerprint.Of(original.Replace("DeliveredBy: osdu-delivery", "DeliveredBy: someone-else", StringComparison.Ordinal), "tag"));
        Assert.Equal(fingerprint, new DeliveryDocumentLoader().ParseMapping(original).Fingerprint);
    }

    [Fact]
    public async Task A_scheduled_run_after_a_mapping_edit_renders_every_record_again_and_sends_only_what_changed()
    {
        var mappings = Path.Combine(_root, "mappings");
        Directory.CreateDirectory(mappings);
        var file = Path.Combine(mappings, "WellLog@1.4.0.yaml");
        var original = await File.ReadAllTextAsync(Path.Combine(Samples.Mappings, "WellLog@1.4.0.yaml"));
        await File.WriteAllTextAsync(file, original);

        // Rows written an hour ago, well before the overlap below the watermark, so a run after the first reads none of them.
        var tables = await SampleEstate.BuildAsync(_root, _clock.GetUtcNow().UtcDateTime.AddHours(-1), time: _clock);
        var caches = _db.Caches();
        await Samples.ImportSampleCacheAsync(caches);
        var protocol = new FakeProtocol();
        var engine = Samples.Engine(_db.Ledger(_clock), _clock, new FakeProtocolFactory(protocol), cache: caches, sources: tables);
        using var provider = new ServiceCollection().AddSingleton(engine).AddSingleton<PartitionLedgers>().BuildServiceProvider();
        var flow = Samples.InFolder(Samples.LocalFlow(_root), _root);
        flow = flow with { Name = "rules-welllog-" + _suffix, Render = flow.Render with { MappingsDirectory = mappings } };
        var logs = SampleEstate.Logs().Count;

        async Task<DeliverOutcome> RunAsync()
        {
            var result = await new DeliveryExecutor(provider).ExecuteAsync(
                new DeliveryFlowDocument { Source = SourceDefinition.Of(flow) },
                flow.SourcePath!,
                new DocumentExecutionOptions
                {
                    Parameters = new RunParameters { Operation = DeliveryOperations.Deliver, Values = new Dictionary<string, string>(SampleEstate.Values, StringComparer.Ordinal) },
                },
                CancellationToken.None);
            Assert.True(result.Success, result.Error);
            return Assert.IsType<DeliverOutcome>(result.Result);
        }

        Assert.Equal(logs, (await RunAsync()).Delivered);
        var quiet = await RunAsync();
        Assert.Equal((0, 0L), (quiet.Delivered, quiet.RecordCount));

        // A comment says nothing about a record: the run stays incremental and reads nothing.
        await File.WriteAllTextAsync(file, "# Reviewed.\n" + original);
        var commented = await RunAsync();
        Assert.Equal((0, 0L), (commented.Delivered, commented.RecordCount));

        // A parameter's description moves the rules and renders the same records: every row is read again, nothing is sent.
        var described = original.Replace(
            "description: The legal tag every record this mapping renders carries (legal.legaltags).",
            "description: The legal tag on every record this mapping renders (legal.legaltags).",
            StringComparison.Ordinal);
        Assert.NotEqual(original, described);
        await File.WriteAllTextAsync(file, described);
        var reread = await RunAsync();
        Assert.Equal((0, (long)logs), (reread.Delivered, reread.RecordCount));
        Assert.Equal(0, (await RunAsync()).RecordCount);

        // A value every record carries: the next ordinary run sends every record again, and the one after it nothing.
        await File.WriteAllTextAsync(file, described.Replace("DeliveredBy: osdu-delivery", "DeliveredBy: osdu-delivery-rules", StringComparison.Ordinal));
        protocol.Deliveries.Clear();
        var edited = await RunAsync();
        Assert.Equal(logs, edited.Delivered);
        Assert.Equal(logs, protocol.Deliveries.Count);
        Assert.All(protocol.Deliveries, d => Assert.Equal("osdu-delivery-rules", d.Document!["tags"]!["DeliveredBy"]!.GetValue<string>()));
        var after = await RunAsync();
        Assert.Equal((0, 0L), (after.Delivered, after.RecordCount));
    }
}
