using System.Text;
using SqlFlow.Acquire.Engine;
using SqlFlow.Acquire.Landing;
using SqlFlow.Acquire.Runtime;
using SqlFlow.Core.Acquire;
using SqlFlow.Core.Runs;
using Xunit;

namespace SqlFlow.Acquire.Tests;

/// <summary>Proves the multi-item acquire shape: one shared source drives several landing items, each overlaying its
/// own transport options (the S3 prefix) onto the source's, and each landing into its own target. This is what folds
/// four near-identical single-landing acq files (same bucket/credentials, different prefix and target) into one flow.</summary>
public sealed class ItemsTests
{
    /// <summary>A minimal listing transport that records the effective prefix it was handed and lands one payload,
    /// so a test can assert both the per-item option overlay and the per-item landing target.</summary>
    private sealed class FakeListTransport : IAcquireTransport
    {
        public List<string?> SeenPrefixes { get; } = [];
        public List<string?> SeenRegions { get; } = [];

        public bool CanHandle(AcquireTransport transport) => transport == AcquireTransport.S3;

        public async Task FetchAsync(AcquireFetch fetch, CancellationToken ct)
        {
            var prefix = fetch.Source.Options.TryGetValue("prefix", out var p) ? p : null;
            var region = fetch.Source.Options.TryGetValue("region", out var r) ? r : null;
            SeenPrefixes.Add(prefix);
            SeenRegions.Add(region);
            fetch.Pages++;
            await fetch.Landing.LandAsync(
                new LandedItem(Encoding.UTF8.GetBytes($"data-{prefix}"), "text/csv", prefix ?? "file", RecordCount: -1, Headers: null),
                fetch.Vars.Clone().WithString("filename", prefix ?? "file"), ct).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task Multi_item_flow_overlays_each_prefix_and_lands_to_its_own_target()
    {
        var root = Path.Combine(Path.GetTempPath(), "sqlflow-acquire-tests", Guid.NewGuid().ToString("N"));
        var ordersDir = Path.Combine(root, "orders");
        var paymentsDir = Path.Combine(root, "payments");

        var transport = new FakeListTransport();
        var store = new CompositeRawLandingStore([new LocalRawLandingStore()]);
        var secrets = new FakeSecrets();
        var engine = new AcquireEngine(store, new AuthResolver(secrets), secrets, new IAcquireTransport[] { transport }, TimeProvider.System);

        var flow = new AcquireFlow
        {
            Name = "Multi_Flow",
            Source = new AcquireSource
            {
                Transport = AcquireTransport.S3,
                BaseUrl = "s3://bucket",
                // The shared option: authored once, visible to every item.
                Options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["region"] = "eu-west-1" },
            },
            Items =
            [
                new AcquireItem
                {
                    Options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["prefix"] = "export_orders" },
                    Landing = new AcquireLanding { Target = ordersDir, PathTemplate = "{filename}" },
                },
                new AcquireItem
                {
                    Options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["prefix"] = "export_payments" },
                    Landing = new AcquireLanding { Target = paymentsDir, PathTemplate = "{filename}" },
                },
            ],
        };

        var result = await engine.RunAsync(flow, Guid.NewGuid(), NullRunEventSink.Instance, null, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.FilesWritten);
        // Each item overlaid its own prefix; the shared region reached both.
        Assert.Equal(["export_orders", "export_payments"], transport.SeenPrefixes);
        Assert.Equal(["eu-west-1", "eu-west-1"], transport.SeenRegions);
        // Each item landed into its own target, not a shared one.
        Assert.Single(TestEngine.LandedFiles(ordersDir));
        Assert.Single(TestEngine.LandedFiles(paymentsDir));
    }
}
