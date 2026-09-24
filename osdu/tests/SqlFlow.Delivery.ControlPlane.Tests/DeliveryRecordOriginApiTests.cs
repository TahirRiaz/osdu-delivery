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
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// What a record says about where it came from, as the API serves it (docs/stage4-design.md section 4.3). Traceability is
/// the product: a delivered record names the ingestion file and row its document was built from, a record with work
/// waiting names the file and row that work will be built from, and a past attempt keeps the origin it sent, so the trail
/// survives the record moving on to a newer row. The ledger has carried all of it since the ingestion tables replaced
/// drops; these tests hold the served contract to it, because a field the DTO omits is invisible to every reader.
/// </summary>
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryRecordOriginApiTests
{
    private const string FlowName = "wells-wellbore-03-header-delivery";

    private const string OriginFile = "wellbore_20260901.csv";

    private const string PendingFile = "wellbore_20260902.csv";

    [Fact]
    public async Task A_record_and_its_attempts_carry_the_ingestion_file_and_row_they_came_from()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var flowId = FlowId.Of(FlowName);
        var key = new DeliveryKey(Guid.NewGuid());
        var delivered = new DateTime(2026, 9, 1, 6, 30, 0, DateTimeKind.Utc);
        var pending = new DateTime(2026, 9, 2, 7, 15, 0, DateTimeKind.Utc);
        var ledger = new OsduLedger(() => SampleEstate.Context(cs));

        try
        {
            // A record whose current document came from one file and row, with newer work waiting from another: the two
            // origins are what tell an operator which file a delivered record is from and which file will replace it.
            await ledger.UpsertPendingAsync(
                flowId,
            [
                new RecordState
                {
                    DeliveryKey = key,
                    FlowId = flowId,
                    SourceKey = "WB-ORIGIN-1",
                    Label = "WB-ORIGIN-1",
                    MappingName = SampleEstate.WellboreMapping,
                    Status = RecordStatus.Pending,
                    SourceKeyJson = """{"facility_name":"WB-ORIGIN-1"}""",
                    SourceFileName = OriginFile,
                    SourceRowNumber = 7,
                    SourceUpdatedUtc = delivered,
                    PendingSourceFileName = PendingFile,
                    PendingSourceRowNumber = 11,
                    PendingSourceUpdatedUtc = pending,
                    PendingDocumentRef = "1:0:10",
                    PendingMetadata = true,
                },
            ]);

            // One try, which keeps the origin it sent even after the record moves on: appended as a worker appends it, and
            // applied as its lease applies it.
            var lease = "origin-test/" + Guid.NewGuid().ToString("N");
            await ledger.AppendAsync(flowId, lease, new LeaseAppend([], [new RecordCompletion
            {
                DeliveryKey = key,
                Status = RecordStatus.Delivered,
                Promote = true,
                TargetId = "dev:master-data--Wellbore:WB-ORIGIN-1",
                TargetVersion = 1,
                Attempt = new AttemptRecord
                {
                    DeliveryKey = key,
                    Worker = "origin-test",
                    StartedUtc = delivered,
                    CompletedUtc = delivered.AddSeconds(2),
                    Outcome = AttemptOutcome.Delivered,
                    Phase = "metadata",
                    SourceFileName = OriginFile,
                    SourceRowNumber = 7,
                    SourceUpdatedUtc = delivered,
                },
            }]));
            await ledger.CheckpointLeaseAsync(lease, delivered.AddSeconds(2));

            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            using var recordResponse = await GetAsync(client, token, $"/api/v1/delivery/records/{flowId:D}/{key.Value:D}");
            Assert.Equal(HttpStatusCode.OK, recordResponse.StatusCode);

            // A record is its flow's: the same key under another flow is a record that flow does not hold.
            using var elsewhere = await GetAsync(client, token, $"/api/v1/delivery/records/{FlowId.Of(FlowName + "-elsewhere"):D}/{key.Value:D}");
            Assert.Equal(HttpStatusCode.NotFound, elsewhere.StatusCode);
            using var elsewhereAttempts = await GetAsync(client, token, $"/api/v1/delivery/records/{FlowId.Of(FlowName + "-elsewhere"):D}/{key.Value:D}/attempts");
            Assert.Equal(HttpStatusCode.NotFound, elsewhereAttempts.StatusCode);
            using var recordJson = JsonDocument.Parse(await recordResponse.Content.ReadAsStringAsync());
            var record = recordJson.RootElement.GetProperty("record");

            // A delivered completion promotes the waiting origin to the current one: what the record now holds came from
            // the newer file and row, which is why an operator reading the record sees the file it was last built from.
            Assert.Equal(PendingFile, record.GetProperty("sourceFileName").GetString());
            Assert.Equal(11, record.GetProperty("sourceRowNumber").GetInt64());
            Assert.Equal(pending, record.GetProperty("sourceUpdatedUtc").GetDateTime());
            Assert.Contains("WB-ORIGIN-1", record.GetProperty("sourceKeyJson").GetString()!, StringComparison.Ordinal);

            // The pending document is settled by the promotion, so nothing is left waiting.
            Assert.False(record.GetProperty("hasPendingDocument").GetBoolean());
            Assert.False(record.GetProperty("pendingMetadata").GetBoolean());

            // The attempt keeps the origin it actually sent, which is what makes a past try reconstructible from the
            // ledger alone after the record has moved on to a newer row.
            using var attemptsResponse = await GetAsync(client, token, $"/api/v1/delivery/records/{flowId:D}/{key.Value:D}/attempts");
            Assert.Equal(HttpStatusCode.OK, attemptsResponse.StatusCode);
            using var attemptsJson = JsonDocument.Parse(await attemptsResponse.Content.ReadAsStringAsync());
            var attempt = Assert.Single(attemptsJson.RootElement.EnumerateArray().ToList());

            Assert.Equal(OriginFile, attempt.GetProperty("sourceFileName").GetString());
            Assert.Equal(7, attempt.GetProperty("sourceRowNumber").GetInt64());
            Assert.Equal(delivered, attempt.GetProperty("sourceUpdatedUtc").GetDateTime());
            Assert.NotEqual(
                record.GetProperty("sourceFileName").GetString(),
                attempt.GetProperty("sourceFileName").GetString());
        }
        finally
        {
            await using var osdu = SampleEstate.Context(cs);
            await osdu.DeliveryAttempts.Where(a => a.FlowId == flowId && a.DeliveryKey == key.Value).ExecuteDeleteAsync();
            await osdu.DeliveryRecords.Where(r => r.FlowId == flowId && r.DeliveryKey == key.Value).ExecuteDeleteAsync();
        }
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
