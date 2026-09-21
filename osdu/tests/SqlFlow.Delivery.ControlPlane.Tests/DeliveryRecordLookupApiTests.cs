using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Delivery.ControlPlane;
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
                Record(flowId, delivered, marker + "-A", $"opendes:master-data--Wellbore:{marker}-A", marker + "_wellbores.csv", 3),
                Record(flowId, held, marker + "-B", $"opendes:master-data--Wellbore:{marker}-B", marker + "_wellbores.csv", 4),
            ]);
            await ledger.UpsertPendingAsync(otherFlow,
            [
                Record(otherFlow, elsewhere, marker + "-C", $"opendes:master-data--Well:{marker}-C", marker + "_wells.csv", 1),
            ]);

            var lease = "lookup-test/" + Guid.NewGuid().ToString("N");
            await ledger.AppendAsync(flowId, lease, new LeaseAppend([],
            [
                new RecordCompletion
                {
                    DeliveryKey = delivered,
                    Status = RecordStatus.Delivered,
                    Promote = true,
                    TargetId = $"opendes:master-data--Wellbore:{marker}-A",
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
            Assert.Equal($"opendes:master-data--Wellbore:{marker}-A", hitA.GetProperty("targetId").GetString());

            // The OSDU id, the ingestion file name and the delivery key itself find a record too. A file finds every
            // record built from it, landed or not: an operator asking "what came out of this file" means all of them,
            // and the held one is exactly what they are looking for.
            Assert.Equal(1, (await LookupAsync(client, token, $"search=opendes:master-data--Well:{marker}")).GetProperty("total").GetInt64());
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
                .Select((key, i) => Record(flowId, key, $"{marker}-{i}", $"opendes:master-data--Wellbore:{marker}-{i}", marker + "_wellbores.csv", i))
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
