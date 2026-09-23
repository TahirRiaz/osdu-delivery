using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The Records page's lookup, as the API serves it: an operator holding a source key, an OSDU id, a delivery key or the
/// name of the file a record came from finds the record without knowing which flow delivered it, across every flow, and
/// narrows to a custody state. With nothing to look for, the same route lists what the delivery system last took in or
/// sent, newest first. Both are indexed reads, so a page past the candidate bound is empty rather than a scan.
/// </summary>
public sealed class DeliveryRecordLookupApiTests
{
    private const string FlowName = "wells-wellbore-03-header-delivery";

    [SkippableFact]
    public async Task A_record_is_found_across_flows_by_what_an_operator_holds_and_narrowed_by_state()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var flowId = FlowId.Of(FlowName);
        var otherFlow = FlowId.Of(FlowName + "-lookup-other");
        var marker = "LK" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var delivered = new DeliveryKey(Guid.NewGuid());
        var held = new DeliveryKey(Guid.NewGuid());
        var elsewhere = new DeliveryKey(Guid.NewGuid());
        var when = new DateTime(2026, 9, 3, 8, 0, 0, DateTimeKind.Utc);
        var ledger = new OsduLedger(() => SampleEstate.Context(cs));

        try
        {
            await ledger.UpsertPendingAsync(flowId,
            [
                Record(flowId, delivered, marker + "-A", $"dev:master-data--Wellbore:{marker}-A", marker + "_wellbores.csv", 3),
                Record(flowId, held, marker + "-B", $"dev:master-data--Wellbore:{marker}-B", marker + "_wellbores.csv", 4),
            ]);
            await ledger.UpsertPendingAsync(otherFlow,
            [
                Record(otherFlow, elsewhere, marker + "-C", $"dev:master-data--Well:{marker}-C", marker + "_wells.csv", 1),
            ]);

            var lease = "lookup-test/" + Guid.NewGuid().ToString("N");
            await ledger.AppendAsync(flowId, lease, new LeaseAppend([],
            [
                new RecordCompletion
                {
                    DeliveryKey = delivered,
                    Status = RecordStatus.Delivered,
                    Promote = true,
                    TargetId = $"dev:master-data--Wellbore:{marker}-A",
                    TargetVersion = 1,
                    Attempt = new AttemptRecord
                    {
                        DeliveryKey = delivered, Worker = "lookup-test", StartedUtc = when, CompletedUtc = when.AddSeconds(1),
                        Outcome = AttemptOutcome.Delivered, Phase = "metadata",
                    },
                },
                new RecordCompletion
                {
                    DeliveryKey = held,
                    Status = RecordStatus.Held,
                    Error = "held by the test",
                    Attempt = new AttemptRecord
                    {
                        DeliveryKey = held, Worker = "lookup-test", StartedUtc = when, CompletedUtc = when.AddSeconds(1),
                        Outcome = AttemptOutcome.Held, Phase = "metadata", Error = "held by the test",
                    },
                },
            ]));
            await ledger.CheckpointLeaseAsync(lease, when.AddSeconds(2));

            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            // A source key prefix finds the records of every flow, newest first, each naming its flow's ledger.
            var byKey = await LookupAsync(client, token, $"search={marker}-");
            Assert.Equal(3, byKey.GetProperty("total").GetInt64());
            Assert.False(byKey.GetProperty("totalCapped").GetBoolean());
            var hits = byKey.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(3, hits.Count);
            Assert.Contains(hits, h => h.GetProperty("deliveryKey").GetGuid() == elsewhere.Value && h.GetProperty("flowId").GetGuid() == otherFlow);
            var hitA = Assert.Single(hits, h => h.GetProperty("deliveryKey").GetGuid() == delivered.Value);
            Assert.Equal(flowId, hitA.GetProperty("flowId").GetGuid());
            Assert.Equal("delivered", hitA.GetProperty("status").GetString());
            Assert.Equal($"dev:master-data--Wellbore:{marker}-A", hitA.GetProperty("targetId").GetString());

            // The OSDU id, the ingestion file name and the delivery key itself find a record too. A file finds every
            // record built from it, landed or not: an operator asking "what came out of this file" means all of them,
            // and the held one is exactly what they are looking for.
            Assert.Equal(1, (await LookupAsync(client, token, $"search=dev:master-data--Well:{marker}")).GetProperty("total").GetInt64());
            var byFile = await LookupAsync(client, token, $"search={marker}_wellbores");
            Assert.Equal(2, byFile.GetProperty("total").GetInt64());
            Assert.Contains(byFile.GetProperty("items").EnumerateArray(), h => h.GetProperty("deliveryKey").GetGuid() == delivered.Value);
            Assert.Contains(byFile.GetProperty("items").EnumerateArray(), h => h.GetProperty("deliveryKey").GetGuid() == held.Value);
            var byDeliveryKey = await LookupAsync(client, token, $"search={held.Value:D}");
            Assert.Equal(held.Value, Assert.Single(byDeliveryKey.GetProperty("items").EnumerateArray().ToList()).GetProperty("deliveryKey").GetGuid());

            // A state narrows the same lookup, and one the ledger does not know is refused.
            var heldOnly = await LookupAsync(client, token, $"search={marker}-&status=held");
            Assert.Equal(held.Value, Assert.Single(heldOnly.GetProperty("items").EnumerateArray().ToList()).GetProperty("deliveryKey").GetGuid());
            Assert.Equal(0, (await LookupAsync(client, token, $"search={marker}-&status=failed")).GetProperty("total").GetInt64());
            using var badStatus = await GetAsync(client, token, $"/api/v1/delivery/records?search={marker}&status=nonsense");
            Assert.Equal(HttpStatusCode.BadRequest, badStatus.StatusCode);

            // Paging walks the same hits.
            var pageTwo = await LookupAsync(client, token, $"search={marker}-&page=2&pageSize=2");
            Assert.Single(pageTwo.GetProperty("items").EnumerateArray().ToList());
            Assert.Equal(3, pageTwo.GetProperty("total").GetInt64());
        }
        finally
        {
            await using var osdu = SampleEstate.Context(cs);
            var keys = new[] { delivered.Value, held.Value, elsewhere.Value };
            await osdu.DeliveryRecordIdentities.Where(i => keys.Contains(i.DeliveryKey)).ExecuteDeleteAsync();
            await osdu.DeliveryAttempts.Where(a => keys.Contains(a.DeliveryKey)).ExecuteDeleteAsync();
            await osdu.DeliveryRecords.Where(r => keys.Contains(r.DeliveryKey)).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task With_nothing_to_look_for_the_records_the_system_last_took_in_are_listed_newest_first()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var flowId = FlowId.Of(FlowName);
        var marker = "RC" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var keys = new[] { new DeliveryKey(Guid.NewGuid()), new DeliveryKey(Guid.NewGuid()), new DeliveryKey(Guid.NewGuid()) };
        var ledger = new OsduLedger(() => SampleEstate.Context(cs));

        try
        {
            // Staged now, so these are the records the ledger last touched: the listing has to show them first.
            await ledger.UpsertPendingAsync(flowId, keys
                .Select((key, i) => Record(flowId, key, $"{marker}-{i}", $"dev:master-data--Wellbore:{marker}-{i}", marker + "_wellbores.csv", i))
                .ToList());

            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            var latest = await LookupAsync(client, token, "pageSize=200");
            var items = latest.GetProperty("items").EnumerateArray().ToList();
            Assert.NotEmpty(items);
            foreach (var key in keys)
            {
                Assert.Contains(items, h => h.GetProperty("deliveryKey").GetGuid() == key.Value);
            }

            // Newest first, and nothing was typed, so no row claims a value matched it.
            var times = items.Select(h => h.GetProperty("updatedUtc").GetDateTime()).ToList();
            Assert.Equal(times.OrderByDescending(t => t).ToList(), times);
            Assert.All(items, h => Assert.False(h.TryGetProperty("matched", out var matched) && matched.ValueKind != JsonValueKind.Null));

            // A blank term is nothing typed, not a term that matches nothing.
            var blank = await LookupAsync(client, token, "search=%20&pageSize=200");
            Assert.Equal(items.Count, blank.GetProperty("items").EnumerateArray().Count());

            // The same listing narrowed to one custody state, and a page past the recency bound is empty rather than a scan.
            var pending = await LookupAsync(client, token, "status=pending&pageSize=200");
            Assert.All(pending.GetProperty("items").EnumerateArray(), h => Assert.Equal("pending", h.GetProperty("status").GetString()));
            Assert.Contains(pending.GetProperty("items").EnumerateArray(), h => h.GetProperty("deliveryKey").GetGuid() == keys[0].Value);
            var past = await LookupAsync(client, token, $"page={(RecordListing.LookupCandidateLimit / 50) + 1}&pageSize=50");
            Assert.Empty(past.GetProperty("items").EnumerateArray());
        }
        finally
        {
            await using var osdu = SampleEstate.Context(cs);
            var ids = keys.Select(k => k.Value).ToArray();
            await osdu.DeliveryRecordIdentities.Where(i => ids.Contains(i.DeliveryKey)).ExecuteDeleteAsync();
            await osdu.DeliveryRecords.Where(r => ids.Contains(r.DeliveryKey)).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task The_listing_and_the_lookup_narrow_to_one_flow_the_page_offers()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var marker = "FL" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var single = $"{marker}-wells";
        var source = $"{marker}-logs";
        var singleFlow = FlowId.Of(single);
        var headerFlow = FlowId.Of($"{source}/header");
        var unsynced = FlowId.Of($"{marker}-gone");
        var repoId = Guid.NewGuid();
        var singlePipeline = Guid.NewGuid();
        var sourcePipeline = Guid.NewGuid();
        var keys = new[] { new DeliveryKey(Guid.NewGuid()), new DeliveryKey(Guid.NewGuid()), new DeliveryKey(Guid.NewGuid()) };
        var ledger = new OsduLedger(() => SampleEstate.Context(cs));

        try
        {
            await ledger.UpsertPendingAsync(singleFlow, [Record(singleFlow, keys[0], $"{marker}-W1", $"dev:master-data--Well:{marker}-W1", marker + "_wells.csv", 1)]);
            await ledger.UpsertPendingAsync(headerFlow,
            [
                Record(headerFlow, keys[1], $"{marker}-H1", $"dev:work-product-component--WellLog:{marker}-H1", marker + "_logs.csv", 1),
                Record(headerFlow, keys[2], $"{marker}-H2", $"dev:work-product-component--WellLog:{marker}-H2", marker + "_logs.csv", 2),
            ]);

            // What the repository sync records: a flow in the single form, a source of two interfaces, and a ledger
            // whose pipeline the catalog no longer holds.
            await using (var osdu = SampleEstate.Context(cs))
            {
                osdu.DeliveryInterfaces.AddRange(
                    Interface(repoId, single, "", singleFlow),
                    Interface(repoId, source, "header", headerFlow),
                    Interface(repoId, source, "curves", FlowId.Of($"{source}/curves")),
                    Interface(repoId, $"{marker}-gone", "", unsynced));
                await osdu.SaveChangesAsync();
            }

            await using (var catalog = new CatalogDbContext(CatalogDatabase.BuildOptions(cs)))
            {
                catalog.Pipelines.AddRange(Pipeline(singlePipeline, repoId, single), Pipeline(sourcePipeline, repoId, source));
                await catalog.SaveChangesAsync();
            }

            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            // The choices: one per ledger identity, named by pipeline and interface as a hit is, and none for a ledger
            // no synced pipeline holds.
            using var flowsResponse = await GetAsync(client, token, "/api/v1/delivery/records/flows");
            Assert.Equal(HttpStatusCode.OK, flowsResponse.StatusCode);
            using var flowsJson = JsonDocument.Parse(await flowsResponse.Content.ReadAsStringAsync());
            var ours = flowsJson.RootElement.EnumerateArray().Where(f => f.GetProperty("flowName").GetString()!.StartsWith(marker, StringComparison.Ordinal)).ToList();
            Assert.Equal(
                [(source, "curves", FlowId.Of($"{source}/curves")), (source, "header", headerFlow), (single, (string?)null, singleFlow)],
                ours.Select(f => (f.GetProperty("flowName").GetString()!, f.GetProperty("interface").GetString(), f.GetProperty("flowId").GetGuid())).ToList());
            Assert.Equal([sourcePipeline, sourcePipeline, singlePipeline], ours.Select(f => f.GetProperty("pipelineId").GetGuid()).ToList());
            Assert.DoesNotContain(flowsJson.RootElement.EnumerateArray(), f => f.GetProperty("flowId").GetGuid() == unsynced);

            // The recency listing of one flow is that flow's records alone.
            var header = await LookupAsync(client, token, $"flowId={headerFlow:D}&pageSize=200");
            Assert.Equal(2, header.GetProperty("total").GetInt64());
            Assert.All(header.GetProperty("items").EnumerateArray(), h => Assert.Equal(headerFlow, h.GetProperty("flowId").GetGuid()));
            Assert.All(header.GetProperty("items").EnumerateArray(), h => Assert.Equal("header", h.GetProperty("interface").GetString()));

            // A term matching both flows' records finds the chosen flow's only, and a status narrows it further.
            Assert.Equal(3, (await LookupAsync(client, token, $"search={marker}-")).GetProperty("total").GetInt64());
            var wells = await LookupAsync(client, token, $"search={marker}-&flowId={singleFlow:D}");
            Assert.Equal(keys[0].Value, Assert.Single(wells.GetProperty("items").EnumerateArray().ToList()).GetProperty("deliveryKey").GetGuid());
            Assert.Equal(0, (await LookupAsync(client, token, $"search={marker}-&flowId={singleFlow:D}&status=held")).GetProperty("total").GetInt64());
            Assert.Equal(0, (await LookupAsync(client, token, $"search={marker}-W&flowId={headerFlow:D}")).GetProperty("total").GetInt64());

            using var badFlow = await GetAsync(client, token, "/api/v1/delivery/records?flowId=not-a-flow");
            Assert.Equal(HttpStatusCode.BadRequest, badFlow.StatusCode);
        }
        finally
        {
            await using var osdu = SampleEstate.Context(cs);
            var ids = keys.Select(k => k.Value).ToArray();
            await osdu.DeliveryInterfaces.Where(i => i.RepoId == repoId).ExecuteDeleteAsync();
            await osdu.DeliveryRecordIdentities.Where(i => ids.Contains(i.DeliveryKey)).ExecuteDeleteAsync();
            await osdu.DeliveryRecords.Where(r => ids.Contains(r.DeliveryKey)).ExecuteDeleteAsync();
            await using var catalog = new CatalogDbContext(CatalogDatabase.BuildOptions(cs));
            await catalog.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
        }
    }

    private static DeliveryInterface Interface(Guid repoId, string flowName, string name, Guid ledgerFlowId) => new()
    {
        Id = Guid.NewGuid(),
        RepoId = repoId,
        FlowName = flowName,
        Interface = name,
        LedgerFlowId = ledgerFlowId,
        LedgerName = name.Length == 0 ? flowName : $"{flowName}/{name}",
        Route = "storage",
        MappingReference = SampleEstate.WellboreMapping,
        RelativePath = $"flows/{flowName}.yaml",
        FirstSeenUtc = DateTime.UtcNow,
        LastSeenUtc = DateTime.UtcNow,
        Active = true,
    };

    private static CatalogPipeline Pipeline(Guid id, Guid repoId, string name) => new()
    {
        Id = id,
        RepoId = repoId,
        Name = name,
        Kind = FlowDefinition.FlowTypeName,
        RelativePath = $"flows/{name}.yaml",
        Active = true,
    };

    private static RecordState Record(Guid flowId, DeliveryKey key, string sourceKey, string targetId, string file, long row) => new()
    {
        DeliveryKey = key,
        FlowId = flowId,
        SourceKey = sourceKey,
        Label = sourceKey,
        MappingName = SampleEstate.WellboreMapping,
        Status = RecordStatus.Pending,
        TargetId = targetId,
        SourceKeyJson = $$"""{"facility_name":"{{sourceKey}}"}""",
        SourceFileName = file,
        SourceRowNumber = row,
        PendingSourceFileName = file,
        PendingSourceRowNumber = row,
        PendingDocumentRef = "1:0:10",
        PendingMetadata = true,
    };

    private static async Task<JsonElement> LookupAsync(HttpClient client, string token, string query)
    {
        using var response = await GetAsync(client, token, "/api/v1/delivery/records?" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private static ControlPlaneAppFactory Factory(string cs)
        => new ControlPlaneAppFactory()
            .WithCatalog(cs)
            .WithModules(new DeliveryControlPlaneModule())
            .WithSetting("ControlPlane:Worker:Enabled", "false")
            .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");

    private static async Task<string> TokenAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["read", "operate"]));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }
}
