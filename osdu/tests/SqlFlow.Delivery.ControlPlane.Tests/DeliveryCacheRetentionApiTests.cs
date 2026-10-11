using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The partition cache's retention as the control plane serves it (osdu/docs/reference/concepts/partition-cache.md,
/// Retention): a pruned version stays listed with when it was pruned and its counts, its records and a comparison with it
/// answer 410 Gone, and the cache listing says the retention each cache flow declares and the one the partition keeps.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryCacheRetentionApiTests
{
    private const string Flow = "cp-retention-cache";

    private static readonly DateTimeOffset Start = new(2026, 9, 1, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_pruned_version_is_listed_with_its_counts_and_its_records_are_gone()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);
        var scope = "cp-ret-" + Guid.NewGuid().ToString("N")[..8];
        var store = new OsduCacheStore(() => SampleEstate.Context(cs));

        try
        {
            var labels = new List<string>();
            for (var day = 0; day < 4; day++)
            {
                var write = await store.MergeAsync(
                    scope, Flow,
                    [new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure", [ReferenceItem.FromText(scope + ":reference-data--UnitOfMeasure:m", new Dictionary<string, string> { ["Name"] = "metre-" + day })])],
                    new CacheCapture(null, "tests", "seeded"), Start.AddDays(day));
                labels.Add(write.Snapshot.Version);
            }

            await using (var db = SampleEstate.Context(cs))
            {
                db.DeliveryCacheDefinitions.Add(new DeliveryCacheDefinition
                {
                    Id = Guid.NewGuid(),
                    RepoId = Guid.NewGuid(),
                    FlowName = Flow,
                    Scope = scope,
                    Endpoint = "https://osdu.example.test",
                    RelativePath = "cache/" + Flow + ".yaml",
                    Name = "UnitOfMeasure",
                    EntityType = "reference-data--UnitOfMeasure",
                    Kind = "osdu:wks:reference-data--UnitOfMeasure:1.0.0",
                    Query = "*",
                    FieldsJson = "[{\"path\":\"data.Name\",\"as\":\"Name\"}]",
                    RetentionDays = 2,
                    FirstSeenUtc = Start.UtcDateTime,
                    LastSeenUtc = Start.UtcDateTime,
                });
                await db.SaveChangesAsync();
            }

            // Ten days in, the current version and the one it replaced keep their records; the first two are pruned.
            var outcome = await store.ApplyRetentionAsync(scope, Flow, 2, Start.AddDays(10));
            Assert.True(
                outcome.Deferred is null,
                $"The retention waited ({outcome.Deferred}): an interrupted earlier run left an active interface row whose pin is not recorded in the test database. Running the module suite empties it.");
            Assert.Equal(labels.Take(2), outcome.Pruned);

            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            var versions = (await JsonAsync(client, token, $"/api/v1/delivery/cache/versions?scope={scope}")).EnumerateArray().ToList();
            Assert.Equal(4, versions.Count);
            foreach (var version in versions)
            {
                var pruned = labels.Take(2).Contains(version.GetProperty("version").GetString());
                Assert.Equal(pruned ? JsonValueKind.String : JsonValueKind.Null, version.GetProperty("prunedUtc").ValueKind);
            }

            // The history keeps every version's counts; the version after the last pruned one says its predecessor is gone.
            var history = (await JsonAsync(client, token, $"/api/v1/delivery/cache/history?scope={scope}")).EnumerateArray().ToList();
            Assert.Equal(4, history.Count);
            Assert.All(history.Where(h => h.GetProperty("before").ValueKind == JsonValueKind.String), h => Assert.Equal(1, h.GetProperty("changed").GetInt64()));
            Assert.Equal(
                [false, true, true, false],
                history.OrderBy(h => h.GetProperty("version").GetProperty("sequence").GetInt32()).Select(h => h.GetProperty("beforePruned").GetBoolean()));

            using (var items = await SendAsync(client, token, $"/api/v1/delivery/cache/items?scope={scope}&version={labels[0]}"))
            {
                Assert.Equal(HttpStatusCode.Gone, items.StatusCode);
                Assert.Contains($"Version {labels[0]} of the cache of partition '{scope}' was pruned", await items.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            using (var diff = await SendAsync(client, token, $"/api/v1/delivery/cache/diff?scope={scope}&from={labels[1]}"))
            {
                Assert.Equal(HttpStatusCode.Gone, diff.StatusCode);
            }

            // A version the cache never held is still not found, and the kept ones still read and compare.
            using (var unknown = await SendAsync(client, token, $"/api/v1/delivery/cache/items?scope={scope}&version=19990101T000000Z"))
            {
                Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            }

            var kept = await JsonAsync(client, token, $"/api/v1/delivery/cache/items?scope={scope}&version={labels[2]}");
            Assert.Equal(1, kept.GetProperty("total").GetInt32());
            var compared = await JsonAsync(client, token, $"/api/v1/delivery/cache/diff?scope={scope}&from={labels[2]}");
            Assert.Equal(1, compared.GetProperty("changed").GetInt64());

            // The cache listing says the retention the flow declares and the one the partition keeps.
            var cache = Assert.Single((await JsonAsync(client, token, "/api/v1/delivery/caches")).EnumerateArray(), c => c.GetProperty("scope").GetString() == scope);
            Assert.Equal(2, cache.GetProperty("retentionDays").GetInt32());
            Assert.Equal(2, Assert.Single(cache.GetProperty("flows").EnumerateArray()).GetProperty("retentionDays").GetInt32());
        }
        finally
        {
            await using var db = SampleEstate.Context(cs);
            await db.DeliveryCacheItems.Where(i => i.Scope == scope).ExecuteDeleteAsync();
            await db.DeliveryCacheMembers.Where(m => m.Scope == scope).ExecuteDeleteAsync();
            await db.DeliveryCacheVersions.Where(v => v.Scope == scope).ExecuteDeleteAsync();
            await db.DeliveryCacheDefinitions.Where(d => d.Scope == scope).ExecuteDeleteAsync();
        }
    }

    private static async Task<JsonElement> JsonAsync(HttpClient client, string token, string path)
    {
        using var response = await SendAsync(client, token, path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {path} answered {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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
