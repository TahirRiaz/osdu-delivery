using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.ControlPlane.Background;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Tests;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The central configuration reaches a run of every kind the module adds (docs/partitions-design.md section 5): the kinds
/// the dispatcher configures are exactly the kinds the module registers, so a kind added later cannot be left to resolve its
/// references on the node alone, and an inventory run, a removal run among them, is queued carrying the configuration as a
/// delivery run is.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class ConfiguredRunDispatcherKindTests
{
    /// <summary>The dispatcher the configured one decorates, keeping what it was asked to queue.</summary>
    private sealed class RecordingDispatcher : IRunDispatcher
    {
        public List<RunEnqueueRequest> Runs { get; } = [];

        public List<RunGroupEnqueueRequest> Groups { get; } = [];

        public Task<Guid> EnqueueAsync(CatalogDbContext catalog, RunEnqueueRequest request, CancellationToken ct = default)
        {
            Runs.Add(request);
            return Task.FromResult(Guid.NewGuid());
        }

        public Task<RunGroupEnqueueResult> EnqueueGroupAsync(CatalogDbContext catalog, RunGroupEnqueueRequest request, CancellationToken ct = default)
        {
            Groups.Add(request);
            return Task.FromResult(new RunGroupEnqueueResult(Guid.NewGuid(), [], []));
        }

        public Task<Guid> EnqueueComputeTaskAsync(CatalogDbContext catalog, ComputeTaskEnqueueRequest request, CancellationToken ct = default)
            => throw new InvalidOperationException("These tests queue runs and groups only.");

        public Task<CancelOutcome> CancelAsync(CatalogDbContext catalog, Guid runId, CancellationToken ct = default)
            => throw new InvalidOperationException("These tests cancel nothing.");

        public Task<GroupCancelResult> CancelGroupAsync(CatalogDbContext catalog, Guid groupId, CancellationToken ct = default)
            => throw new InvalidOperationException("These tests cancel nothing.");

        public Task<CancelOutcome> CancelComputeTaskAsync(CatalogDbContext catalog, Guid taskId, CancellationToken ct = default)
            => throw new InvalidOperationException("These tests cancel nothing.");
    }

    [Fact]
    public void Every_flow_kind_the_module_registers_is_queued_with_the_central_configuration()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDeliveryKind();
        using var provider = services.BuildServiceProvider();
        var registered = provider.GetServices<IFlowDocumentKind>().Select(k => k.FlowType).ToList();

        Assert.Contains(InventoryFlowDefinition.FlowTypeName, registered);
        Assert.Equal(registered.Order(StringComparer.Ordinal), ConfiguredRunDispatcher.ConfiguredKinds.Order(StringComparer.Ordinal));
        Assert.Equal(DeliveryDocumentLoader.FlowTypes, ConfiguredRunDispatcher.ConfiguredKinds);
        Assert.All(registered, kind => Assert.True(ConfiguredRunDispatcher.Configures(kind), kind));
        Assert.True(ConfiguredRunDispatcher.Configures("Inventory"));

        // SQLFlow's own kinds read nothing of the module's configuration, and are queued as they were.
        Assert.False(ConfiguredRunDispatcher.Configures("pre"));
        Assert.False(ConfiguredRunDispatcher.Configures("ing"));
        Assert.False(ConfiguredRunDispatcher.Configures(null));
    }

    [Fact]
    public async Task An_inventory_run_and_a_removal_run_are_queued_carrying_the_central_configuration()
    {
        var cs = OsduTestServer.Require();
        await SampleEstate.MigrateModuleAsync(cs);
        var repoId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using (var seed = SampleEstate.Context(cs))
        {
            seed.DeliveryConfigProperties.AddRange(
                new DeliveryConfigProperty { Id = Guid.NewGuid(), RepoId = repoId, Name = "OSDU_URL", Value = "https://osdu.example.test", UpdatedUtc = now, UpdatedBy = "tests" },
                new DeliveryConfigProperty { Id = Guid.NewGuid(), RepoId = repoId, Partition = "test", Name = "OSDU_URL", Value = "https://test.osdu.example.test", UpdatedUtc = now, UpdatedBy = "tests" });
            await seed.SaveChangesAsync();
        }

        try
        {
            var inner = new RecordingDispatcher();
            var dispatcher = new ConfiguredRunDispatcher(inner, new DeliveryConfigStore(() => SampleEstate.Context(cs)), NullLogger<ConfiguredRunDispatcher>.Instance);

            // A build run of an inventory flow, as a schedule or the trigger dialog queues it.
            await dispatcher.EnqueueAsync(null!, new RunEnqueueRequest(repoId, "inventories", InventoryFlowDefinition.FlowTypeName));
            var built = DeliveryRunPayload.Parse(inner.Runs[^1].Parameters!.Payload);
            Assert.Equal("https://osdu.example.test", built.References["OSDU_URL"]);
            Assert.Equal("https://test.osdu.example.test", built.ReferencesFor("test")["OSDU_URL"]);

            // A removal run, as the inventory page queues it: what it removes travels untouched beside the configuration.
            var removal = new RunParameters
            {
                Operation = DeliveryOperations.Remove,
                Payload = new DeliveryRunPayload
                {
                    Inventories = ["WellLogs"],
                    Removal = new InventoryRemovalRequest { Finding = "orphan", Scope = "record", Expected = 1, Ids = ["test:master-data--Wellbore:WB-0001"] },
                    Confirm = "test",
                }.ToJson(),
            };
            await dispatcher.EnqueueAsync(null!, new RunEnqueueRequest(repoId, "inventories", InventoryFlowDefinition.FlowTypeName, Parameters: removal));
            var queued = inner.Runs[^1].Parameters!;
            Assert.Equal(DeliveryOperations.Remove, queued.Operation);
            var removing = DeliveryRunPayload.Parse(queued.Payload);
            Assert.Equal("https://test.osdu.example.test", removing.ReferencesFor("test")["OSDU_URL"]);
            Assert.Equal(["WellLogs"], removing.Inventories);
            Assert.Equal(("test", "orphan", "record", 1L), (removing.Confirm, removing.Removal!.Finding, removing.Removal.Scope, removing.Removal.Expected));
            Assert.Equal(["test:master-data--Wellbore:WB-0001"], removing.Removal.Ids);

            // An inventory flow in a group (a schedule firing the estate in order) is given it too.
            await dispatcher.EnqueueGroupAsync(null!, new RunGroupEnqueueRequest(
                repoId, RunGroupModes.Node, "welllogs",
                [new RunScopeMember("welllogs", FlowDefinition.FlowTypeName, 0, "b"), new RunScopeMember("inventories", InventoryFlowDefinition.FlowTypeName, 1, "b")]));
            var members = inner.Groups.Single().MemberParameters!;
            Assert.Equal("https://osdu.example.test", DeliveryRunPayload.Parse(members["inventories"].Payload).References["OSDU_URL"]);
            Assert.Equal("https://osdu.example.test", DeliveryRunPayload.Parse(members["welllogs"].Payload).References["OSDU_URL"]);
        }
        finally
        {
            await using var osdu = SampleEstate.Context(cs);
            await osdu.DeliveryConfigProperties.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
        }
    }
}
