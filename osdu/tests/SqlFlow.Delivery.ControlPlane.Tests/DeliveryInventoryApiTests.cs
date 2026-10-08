using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.ControlPlane.Api;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The inventories of inventory flows as the control plane serves them (docs/inventory-plan.md, Stage 5): the inventories of a
/// partition with what their last reconcile raised and their newest run, an inventory flow's inventories (declared, kept and no
/// longer declared), one inventory with its counts by finding, owners and last runs, its ids of one finding a page at a time,
/// its runs, a lookup of an OSDU id across the partition's inventories, and the CSV export. The inventory is built and
/// reconciled through the ledger as a run writes it, against a delivery ledger of the same partition, so what these tests hold
/// the API to is what a real build leaves; the tests never reach an OSDU.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryInventoryApiTests
{
    private const string WellLog = "osdu:wks:work-product-component--WellLog:*";
    private const string LogFile = "osdu:wks:dataset--File.Generic:*";
    private const string Estate = "delivery-sp@contoso.com";
    private const string Stranger = "someone@elsewhere.com";

    [Fact]
    public async Task An_inventory_flow_s_inventories_counts_records_runs_lookups_and_exports_are_served_in_its_partition()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var flowName = "api-inventory-" + suffix;
        var partition = "iv" + suffix;
        var repoId = FlowIdentity.FromName("repo/cp-inventories-" + suffix);
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var otherPipelineId = CatalogIdentity.Pipeline(repoId, flowName + "-ingestion");
        var yaml = $$"""
            flowType: inventory
            name: {{flowName}}
            description: Every well log and log file the partition holds.
            partitions: [{{partition}}]
            owners: [{{Estate}}]
            source:
              endpoint: http://localhost
            inventories:
              - name: WellLogs
                description: Every well log, whatever its kind's version.
                kind: "{{WellLog}}"
              - name: LogFiles
                kind: "{{LogFile}}"
            """;
        var flow = new DeliveryDocumentLoader().ParseInventory(yaml, "flows/" + flowName + ".yaml").ForRun(partition, RegisteredPartitions.None);
        var deliveryFlow = FlowId.Of("api-inventory-delivery-" + suffix);
        var deliveryName = "api-inventory-delivery-" + suffix;
        var now = new DateTime(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        string Id(string key) => $"{partition}:work-product-component--WellLog:{key}";

        await using (var db = CatalogDatabase.Create(cs))
        {
            db.Repos.Add(new CatalogRepo
            {
                Id = repoId, Name = "cp-inventories-" + suffix, RemoteUrl = "https://example/cp-inventories.git",
                RootPath = Path.GetTempPath(), FirstSeenUtc = now, LastSyncUtc = now,
            });
            db.Pipelines.Add(Pipeline(pipelineId, repoId, flowName, InventoryFlowDefinition.FlowTypeName, yaml, now));
            db.Pipelines.Add(Pipeline(otherPipelineId, repoId, flowName + "-ingestion", "ingestion", "flowType: ingestion", now));
            await db.SaveChangesAsync();
        }

        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        try
        {
            await ledger.RegisterLedgerAsync(new LedgerEntry
            {
                FlowId = flow.LedgerId, Partition = partition, Kind = LedgerKinds.Inventory, FlowName = flow.Name, LedgerName = flow.LedgerName,
            });
            await ledger.RegisterAsync(deliveryFlow, partition, deliveryName);

            // The delivery ledger delivered "tracked" at the version OSDU serves, and "drifted" at an older one than it serves.
            var trackedKey = await ClaimAsync(cs, ledger, deliveryFlow, Id("tracked"), "tracked", "delivered", 10);
            await ClaimAsync(cs, ledger, deliveryFlow, Id("drifted"), "drifted", "delivered", 10);

            // A build of WellLogs lists five ids: the two the ledger knows, two no ledger knows that the estate created, and one
            // another identity created. It reconciles with the owner the flow names and completes.
            var wellLogs = await ledger.RegisterInventoryAsync(flow.LedgerId, flow.Name, "WellLogs", WellLog, null, "search", "latest");
            var build = await ledger.StartInventoryRunAsync(flow.LedgerId, wellLogs.InventoryId, InventoryRunStatus.Build, Guid.NewGuid(), "inventory api tests", "search", now.AddHours(-2));
            await ledger.AppendInventoryScanAsync(flow.LedgerId, build,
            [
                Row(Id("tracked"), 10, Estate), Row(Id("drifted"), 12, Estate), Row(Id("ours-1"), 1, Estate), Row(Id("ours-2"), 1, Estate), Row(Id("theirs"), 1, Stranger),
            ]);
            var merge = await ledger.MergeInventoryAsync(flow.LedgerId, wellLogs.InventoryId, build, now.AddHours(-2));
            await ledger.ReconcileInventoryAsync(flow.LedgerId, wellLogs.InventoryId, [Estate], now.AddHours(-2));
            var counts = await ledger.InventoryCountsAsync(flow.LedgerId, wellLogs.InventoryId);
            var owners = JsonSerializer.Serialize(new { source = "declared", owners = new[] { new { identity = Estate, records = 2 } } });
            await ledger.CompleteInventoryRunAsync(
                flow.LedgerId, build,
                new InventoryRunState
                {
                    InventoryRunId = build, InventoryId = wellLogs.InventoryId, Operation = InventoryRunStatus.Build, Actor = "inventory api tests",
                    Status = InventoryRunStatus.Completed, StartedUtc = now.AddHours(-2), ReadMode = "search", Listed = merge.Listed, Added = merge.Added,
                    Pages = 1, Requests = 2, FindingsJson = JsonSerializer.Serialize(counts.ByFinding), OwnersJson = owners,
                },
                "declared", now.AddHours(-2).AddMinutes(1));

            // A newer build is under way: listings show it as the newest run, and the counts the last reconcile wrote beside it.
            var running = await ledger.StartInventoryRunAsync(flow.LedgerId, wellLogs.InventoryId, InventoryRunStatus.Build, Guid.NewGuid(), "inventory api tests", "search", now.AddMinutes(-5));

            // LogFiles is registered and never built; Retired is kept from a declaration the flow no longer makes.
            var logFiles = await ledger.RegisterInventoryAsync(flow.LedgerId, flow.Name, "LogFiles", LogFile, null, "search", "latest");
            var retired = await ledger.RegisterInventoryAsync(flow.LedgerId, flow.Name, "Retired", "osdu:wks:master-data--Wellbore:*", null, "search", "all");

            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);
            var root = "/api/v1/delivery/inventories";
            var inventoryPath = $"{root}/{partition}/{wellLogs.InventoryId.ToString(CultureInfo.InvariantCulture)}";

            // The partition's inventories, by flow and name; what holds no value is left out.
            var list = await JsonAsync(client, token, $"{root}?partition={partition}");
            Assert.Equal(partition, list.GetProperty("partition").GetString());
            var listed = list.GetProperty("inventories").EnumerateArray().ToList();
            Assert.Equal(["LogFiles", "Retired", "WellLogs"], listed.Select(i => i.GetProperty("name").GetString()));
            var summary = listed[2];
            Assert.Equal((wellLogs.InventoryId, flow.LedgerId, flowName), (summary.GetProperty("inventoryId").GetInt32(), summary.GetProperty("ledgerId").GetGuid(), summary.GetProperty("flowName").GetString()));
            Assert.Equal(3L, summary.GetProperty("raised").GetInt64());
            var reconciled = summary.GetProperty("reconciled").EnumerateArray().ToDictionary(c => c.GetProperty("finding").GetString()!, c => c.GetProperty("count").GetInt64());
            Assert.Equal(InventoryFindings.All.Count, reconciled.Count);
            Assert.Equal((2L, 1L, 1L, 1L, 0L), (reconciled["orphan"], reconciled["drifted"], reconciled["tracked"], reconciled["foreign"], reconciled["missing"]));
            Assert.Equal((running, "running", "build"), (summary.GetProperty("latest").GetProperty("inventoryRunId").GetInt64(), summary.GetProperty("latest").GetProperty("status").GetString(), summary.GetProperty("latest").GetProperty("operation").GetString()));
            Assert.Equal(build, summary.GetProperty("lastReconcileRunId").GetInt64());
            var never = listed[0];
            Assert.False(never.TryGetProperty("reconciled", out _));
            Assert.False(never.TryGetProperty("latest", out _));
            Assert.False(never.TryGetProperty("lastBuiltUtc", out _));
            Assert.False(never.TryGetProperty("query", out _));

            // The workbench's partition stands for one the request does not name; a name that is no partition is refused.
            using (var byHeader = await SendAsync(client, token, root, partition))
            {
                var text = await byHeader.Content.ReadAsStringAsync();
                Assert.True(byHeader.StatusCode == HttpStatusCode.OK, text);
                Assert.Equal(3, JsonDocument.Parse(text).RootElement.GetProperty("inventories").GetArrayLength());
            }

            await ProblemAsync(client, token, $"{root}?partition=no%20such!", HttpStatusCode.BadRequest, "is not a partition");
            Assert.Empty((await JsonAsync(client, token, $"{root}?partition=none{suffix}")).GetProperty("inventories").EnumerateArray());

            // One inventory: its counts read from its rows, every finding with its zeros, its owners and its last runs.
            var detail = await JsonAsync(client, token, inventoryPath);
            Assert.Equal((pipelineId, repoId, true), (detail.GetProperty("pipelineId").GetGuid(), detail.GetProperty("repoId").GetGuid(), detail.GetProperty("declared").GetBoolean()));
            Assert.Equal("Every well log, whatever its kind's version.", detail.GetProperty("description").GetString());
            Assert.Equal((5L, 3L), (detail.GetProperty("ids").GetInt64(), detail.GetProperty("raised").GetInt64()));
            var detailCounts = detail.GetProperty("counts").EnumerateArray().ToList();
            Assert.Equal(InventoryFindings.All, detailCounts.Select(c => c.GetProperty("finding").GetString()));
            Assert.True(detailCounts.Single(c => c.GetProperty("finding").GetString() == "orphan").GetProperty("raised").GetBoolean());
            Assert.False(detailCounts.Single(c => c.GetProperty("finding").GetString() == "foreign").GetProperty("raised").GetBoolean());
            Assert.Equal("declared", detail.GetProperty("owners").GetProperty("source").GetString());
            var owner = Assert.Single(detail.GetProperty("owners").GetProperty("identities").EnumerateArray().ToList());
            Assert.Equal((Estate, 2L), (owner.GetProperty("identity").GetString(), owner.GetProperty("records").GetInt64()));
            var lastBuild = detail.GetProperty("lastBuild");
            Assert.Equal((build, "completed", 5L, 5L), (lastBuild.GetProperty("inventoryRunId").GetInt64(), lastBuild.GetProperty("status").GetString(), lastBuild.GetProperty("listed").GetInt64(), lastBuild.GetProperty("added").GetInt64()));
            Assert.Equal(["orphan", "drifted", "foreign", "tracked"], lastBuild.GetProperty("findings").EnumerateArray().Select(c => c.GetProperty("finding").GetString()));
            Assert.Equal(build, detail.GetProperty("lastReconcile").GetProperty("inventoryRunId").GetInt64());
            Assert.Equal(running, detail.GetProperty("inventory").GetProperty("latest").GetProperty("inventoryRunId").GetInt64());
            await ProblemAsync(client, token, $"{root}/{partition}/2147480000", HttpStatusCode.NotFound, "No inventory 2147480000");
            await ProblemAsync(client, token, $"{root}/none{suffix}/{wellLogs.InventoryId}", HttpStatusCode.NotFound, "No inventory");

            // Its orphans a page at a time: the first page names where the next starts, the last names none.
            var first = await JsonAsync(client, token, $"{inventoryPath}/records?finding=orphan&limit=1");
            var firstItem = Assert.Single(first.GetProperty("items").EnumerateArray().ToList());
            Assert.Equal("orphan", firstItem.GetProperty("finding").GetString());
            var next = first.GetProperty("next").GetInt64();
            Assert.Equal(firstItem.GetProperty("inventoryRecordId").GetInt64(), next);
            var second = await JsonAsync(client, token, $"{inventoryPath}/records?finding=orphan&limit=1&after={next}");
            var secondItem = Assert.Single(second.GetProperty("items").EnumerateArray().ToList());
            Assert.True(secondItem.GetProperty("inventoryRecordId").GetInt64() > next);
            Assert.Equal(
                [Id("ours-1"), Id("ours-2")],
                new[] { firstItem.GetProperty("targetId").GetString()!, secondItem.GetProperty("targetId").GetString()! }.Order(StringComparer.Ordinal));
            Assert.False(second.TryGetProperty("next", out _));
            var all = await JsonAsync(client, token, $"{inventoryPath}/records?limit=1000");
            Assert.Equal(5, all.GetProperty("items").GetArrayLength());
            Assert.False(all.TryGetProperty("next", out _));

            // A tracked id carries what the ledger holds of it: the ledger by identity and name, the record, its status and version.
            var tracked = Assert.Single((await JsonAsync(client, token, $"{inventoryPath}/records?finding=tracked")).GetProperty("items").EnumerateArray().ToList());
            Assert.Equal((deliveryFlow, deliveryName, trackedKey.Value), (tracked.GetProperty("ledgerFlowId").GetGuid(), tracked.GetProperty("ledger").GetString(), tracked.GetProperty("deliveryKey").GetGuid()));
            Assert.Equal(("delivered", 10L, 10L), (tracked.GetProperty("ledgerStatus").GetString(), tracked.GetProperty("ledgerVersion").GetInt64(), tracked.GetProperty("version").GetInt64()));
            var drifted = Assert.Single((await JsonAsync(client, token, $"{inventoryPath}/records?finding=drifted")).GetProperty("items").EnumerateArray().ToList());
            Assert.Equal("the ledger holds version 10, OSDU serves version 12", drifted.GetProperty("detail").GetString());
            var foreign = Assert.Single((await JsonAsync(client, token, $"{inventoryPath}/records?finding=foreign")).GetProperty("items").EnumerateArray().ToList());
            Assert.False(foreign.TryGetProperty("ledgerFlowId", out _));
            Assert.Equal(Stranger, foreign.GetProperty("createUser").GetString());

            await ProblemAsync(client, token, $"{inventoryPath}/records?finding=lost", HttpStatusCode.BadRequest, "is not a finding");
            await ProblemAsync(client, token, $"{inventoryPath}/records?limit=0", HttpStatusCode.BadRequest, "limit");
            await ProblemAsync(client, token, $"{inventoryPath}/records?limit={DeliveryInventoryEndpoints.MaxPage + 1}", HttpStatusCode.BadRequest, "limit");
            await ProblemAsync(client, token, $"{inventoryPath}/records?after=-1", HttpStatusCode.BadRequest, "after");
            await ProblemAsync(client, token, $"{root}/{partition}/2147480000/records", HttpStatusCode.NotFound, "No inventory");

            // Its runs, newest first: the build under way, then the one that completed with what it found and the owners it used.
            var runs = (await JsonAsync(client, token, $"{inventoryPath}/runs")).EnumerateArray().ToList();
            Assert.Equal([running, build], runs.Select(r => r.GetProperty("inventoryRunId").GetInt64()));
            Assert.False(runs[0].TryGetProperty("findings", out _));
            Assert.Equal((3L, 2), (runs[1].GetProperty("raised").GetInt64(), runs[1].GetProperty("requests").GetInt32()));
            Assert.Equal("declared", runs[1].GetProperty("owners").GetProperty("source").GetString());
            Assert.Single((await JsonAsync(client, token, $"{inventoryPath}/runs?limit=1")).EnumerateArray());
            await ProblemAsync(client, token, $"{inventoryPath}/runs?limit={DeliveryInventoryEndpoints.MaxRuns + 1}", HttpStatusCode.BadRequest, "limit");

            // A lookup by OSDU id across the partition's inventories, in the partition named or the workbench's.
            var lookup = await JsonAsync(client, token, $"{root}/lookup?partition={partition}&id={Uri.EscapeDataString(Id("tracked"))}");
            var hit = Assert.Single(lookup.GetProperty("hits").EnumerateArray().ToList());
            Assert.Equal(("WellLogs", "tracked"), (hit.GetProperty("inventory").GetProperty("name").GetString(), hit.GetProperty("record").GetProperty("finding").GetString()));
            Assert.Equal(deliveryName, hit.GetProperty("record").GetProperty("ledger").GetString());
            using (var byHeader = await SendAsync(client, token, $"{root}/lookup?id={Uri.EscapeDataString("  " + Id("theirs") + " ")}", partition))
            {
                var text = await byHeader.Content.ReadAsStringAsync();
                Assert.True(byHeader.StatusCode == HttpStatusCode.OK, text);
                var looked = JsonDocument.Parse(text).RootElement;
                Assert.Equal(Id("theirs"), looked.GetProperty("targetId").GetString());
                Assert.Equal("foreign", Assert.Single(looked.GetProperty("hits").EnumerateArray().ToList()).GetProperty("record").GetProperty("finding").GetString());
            }

            Assert.Empty((await JsonAsync(client, token, $"{root}/lookup?partition={partition}&id=nowhere")).GetProperty("hits").EnumerateArray());
            await ProblemAsync(client, token, $"{root}/lookup?id=anything", HttpStatusCode.BadRequest, "Name the partition");
            await ProblemAsync(client, token, $"{root}/lookup?partition={partition}", HttpStatusCode.BadRequest, "Name the OSDU id");

            // The export: a finding's ids as CSV under a header row, or every id, named after the flow, partition and inventory.
            using (var csv = await SendAsync(client, token, $"{inventoryPath}/export?finding=orphan"))
            {
                var body = await csv.Content.ReadAsStringAsync();
                Assert.True(csv.StatusCode == HttpStatusCode.OK, body);
                Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
                var lines = body.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                Assert.StartsWith("inventory_record_id,id,kind,version,finding", lines[0], StringComparison.Ordinal);
                Assert.Equal(3, lines.Length);
                Assert.All(lines.Skip(1), line => Assert.Contains(",orphan,", line, StringComparison.Ordinal));
                var file = csv.Content.Headers.ContentDisposition?.FileNameStar ?? csv.Content.Headers.ContentDisposition?.FileName ?? string.Empty;
                Assert.Equal($"{flowName}-{partition}-WellLogs-orphan.csv", file.Trim('"'));
            }

            using (var csv = await SendAsync(client, token, $"{inventoryPath}/export"))
            {
                var lines = (await csv.Content.ReadAsStringAsync()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                Assert.Equal(6, lines.Length);
                Assert.Contains(lines, line => line.Contains(deliveryName, StringComparison.Ordinal) && line.Contains(trackedKey.Value.ToString("D"), StringComparison.Ordinal));
            }

            await ProblemAsync(client, token, $"{inventoryPath}/export?finding=lost", HttpStatusCode.BadRequest, "is not a finding");
            await ProblemAsync(client, token, $"{root}/{partition}/2147480000/export", HttpStatusCode.NotFound, "No inventory");

            // The flow's inventories in its partition: those it declares, in its order, then the one it no longer declares.
            var board = await JsonAsync(client, token, $"/api/v1/delivery/flows/{pipelineId}/inventories?partition={partition}");
            Assert.Equal((partition, true, flow.LedgerId), (board.GetProperty("partition").GetString(), board.GetProperty("readsPartition").GetBoolean(), board.GetProperty("ledgerId").GetGuid()));
            Assert.Equal(("search", Estate), (board.GetProperty("read").GetString(), Assert.Single(board.GetProperty("owners").EnumerateArray().ToList()).GetString()));
            Assert.False(board.TryGetProperty("problem", out _));
            var entries = board.GetProperty("inventories").EnumerateArray().ToList();
            Assert.Equal(["WellLogs", "LogFiles", "Retired"], entries.Select(e => e.GetProperty("name").GetString()));
            Assert.Equal([true, true, false], entries.Select(e => e.GetProperty("declared").GetBoolean()));
            Assert.Equal(wellLogs.InventoryId, entries[0].GetProperty("inventory").GetProperty("inventoryId").GetInt32());
            Assert.Equal(3L, entries[0].GetProperty("inventory").GetProperty("raised").GetInt64());
            Assert.Equal(logFiles.InventoryId, entries[1].GetProperty("inventory").GetProperty("inventoryId").GetInt32());
            Assert.Equal((retired.InventoryId, "all"), (entries[2].GetProperty("inventory").GetProperty("inventoryId").GetInt32(), entries[2].GetProperty("versions").GetString()));

            // In a partition the flow does not read, it says why, with what it declares; another kind and an unknown pipeline are refused.
            var elsewhere = await JsonAsync(client, token, $"/api/v1/delivery/flows/{pipelineId}/inventories?partition=other{suffix}");
            Assert.False(elsewhere.GetProperty("readsPartition").GetBoolean());
            Assert.Contains("does not read partition", elsewhere.GetProperty("problem").GetString(), StringComparison.Ordinal);
            Assert.All(elsewhere.GetProperty("inventories").EnumerateArray(), e => Assert.False(e.TryGetProperty("inventory", out _)));
            await ProblemAsync(client, token, $"/api/v1/delivery/flows/{otherPipelineId}/inventories", HttpStatusCode.Conflict, "not an inventory flow");
            await ProblemAsync(client, token, $"/api/v1/delivery/flows/{Guid.NewGuid()}/inventories", HttpStatusCode.NotFound, "No pipeline");
        }
        finally
        {
            await CleanUpAsync(cs, repoId, partition, flow.LedgerId, deliveryFlow);
        }
    }

    private static InventoryScanRow Row(string id, long version, string creator)
        => new(id, "osdu:wks:work-product-component--WellLog:1.4.0", version, creator, new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc), creator,
            new DateTime(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc));

    /// <summary>A record of the delivery ledger claiming <paramref name="targetId"/>, in <paramref name="status"/> at <paramref name="version"/>.</summary>
    private static async Task<DeliveryKey> ClaimAsync(string cs, OsduLedger ledger, Guid flowId, string targetId, string sourceKey, string status, long? version)
    {
        var key = DeliveryKey.Derive("inventory-api-tests", [targetId]);
        await ledger.UpsertPendingAsync(flowId, [new RecordState
        {
            DeliveryKey = key, FlowId = flowId, SourceKey = sourceKey, MappingName = "WellLog", TargetId = targetId, LastSubmissionId = Guid.NewGuid(),
            PendingDocumentRef = "0:0:10", PendingRenderContext = "{}", PendingMetadataHash = "mh", PendingMetadata = true,
        }]);
        await using var osdu = SampleEstate.Context(cs);
        await osdu.DeliveryRecords
            .Where(r => r.FlowId == flowId && r.DeliveryKey == key.Value)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, status).SetProperty(r => r.TargetVersion, version).SetProperty(r => r.ClaimedTargetId, targetId));
        return key;
    }

    private static async Task CleanUpAsync(string cs, Guid repoId, string partition, Guid inventoryFlow, Guid deliveryFlow)
    {
        await using (var osdu = SampleEstate.Context(cs))
        {
            var inventories = await osdu.DeliveryInventories.Where(i => i.FlowId == inventoryFlow).Select(i => i.InventoryId).ToListAsync();
            var runs = await osdu.DeliveryInventoryRuns.Where(r => inventories.Contains(r.InventoryId)).Select(r => r.InventoryRunId).ToListAsync();
            var records = osdu.DeliveryInventoryRecords.Where(r => inventories.Contains(r.InventoryId)).Select(r => r.InventoryRecordId);
            await osdu.DeliveryInventoryScans.Where(s => runs.Contains(s.InventoryRunId)).ExecuteDeleteAsync();
            await osdu.DeliveryInventoryVersions.Where(v => records.Contains(v.InventoryRecordId)).ExecuteDeleteAsync();
            await osdu.DeliveryInventoryRecords.Where(r => inventories.Contains(r.InventoryId)).ExecuteDeleteAsync();
            await osdu.DeliveryInventoryRuns.Where(r => inventories.Contains(r.InventoryId)).ExecuteDeleteAsync();
            await osdu.DeliveryInventories.Where(i => i.FlowId == inventoryFlow).ExecuteDeleteAsync();
            await osdu.DeliveryAttempts.Where(a => a.FlowId == deliveryFlow).ExecuteDeleteAsync();
            await osdu.DeliveryRecordEvents.Where(e => e.FlowId == deliveryFlow).ExecuteDeleteAsync();
            await osdu.DeliveryRecordIdentities.Where(i => i.FlowId == deliveryFlow).ExecuteDeleteAsync();
            await osdu.DeliveryRecords.Where(r => r.FlowId == deliveryFlow).ExecuteDeleteAsync();
            await osdu.DeliveryLedgers.Where(l => l.FlowId == inventoryFlow || l.FlowId == deliveryFlow).ExecuteDeleteAsync();
            await osdu.DeliveryLedgerPartitions.Where(p => p.Name == partition).ExecuteDeleteAsync();
        }

        await using (var db = CatalogDatabase.Create(cs))
        {
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    private static CatalogPipeline Pipeline(Guid id, Guid repoId, string name, string kind, string yaml, DateTime now) => new()
    {
        Id = id,
        RepoId = repoId,
        Name = name,
        Kind = kind,
        RelativePath = "flows/" + name + ".yaml",
        ContentHash = new string('0', 64),
        Yaml = yaml,
        DefinitionJson = string.Create(CultureInfo.InvariantCulture, $$"""{"name":"{{name}}","flowKind":"{{kind}}"}"""),
        Active = true,
        Wave = 0,
        FirstSeenUtc = now,
        LastSeenUtc = now,
    };

    private static ControlPlaneAppFactory Factory(string cs) => new ControlPlaneAppFactory()
        .WithCatalog(cs)
        .WithModules(new DeliveryControlPlaneModule())
        .WithSetting("ControlPlane:Worker:Enabled", "false")
        .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");

    private static async Task ProblemAsync(HttpClient client, string token, string path, HttpStatusCode status, string says)
    {
        using var response = await SendAsync(client, token, path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"GET {path} answered {(int)response.StatusCode}, not {(int)status}: {body}");
        Assert.Contains(says, body, StringComparison.Ordinal);
    }

    private static async Task<JsonElement> JsonAsync(HttpClient client, string token, string path)
    {
        using var response = await SendAsync(client, token, path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {path} answered {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>A GET as the workbench sends it: with the partition picked in its title bar, when one is.</summary>
    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, string path, string? workbenchPartition = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (workbenchPartition is not null)
        {
            request.Headers.Add(WorkbenchPartition.Header, workbenchPartition);
        }

        return await client.SendAsync(request);
    }

    private static async Task<string> TokenAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["read"]));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }
}
