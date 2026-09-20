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
/// narrows to a custody state. It is the ledger's indexed lookup, the same one the combined search reads, so a term is
/// required and a page past the candidate bound is empty rather than a scan.
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

            // Paging walks the same hits; a term is required, because a lookup with nothing to seek would be a scan.
            var pageTwo = await LookupAsync(client, token, $"search={marker}-&page=2&pageSize=2");
            Assert.Single(pageTwo.GetProperty("items").EnumerateArray().ToList());
            Assert.Equal(3, pageTwo.GetProperty("total").GetInt64());
            using var noTerm = await GetAsync(client, token, "/api/v1/delivery/records");
            Assert.Equal(HttpStatusCode.BadRequest, noTerm.StatusCode);
            using var blankTerm = await GetAsync(client, token, "/api/v1/delivery/records?search=%20");
            Assert.Equal(HttpStatusCode.BadRequest, blankTerm.StatusCode);
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
