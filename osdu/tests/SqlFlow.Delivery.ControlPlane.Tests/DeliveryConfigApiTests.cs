using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.ControlPlane.Api;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The central configuration through the API, checked by the store the command line is checked by
/// (<see cref="SqlFlow.Delivery.Catalog.DeliveryConfigStore"/>): a value trimmed and recorded under the caller, a description
/// longer than its column refused before the save, a repository the catalog does not hold refused as not found, a listing
/// narrowed to a partition, and what was set for a repository since removed from the catalog still removable.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryConfigApiTests
{
    [Fact]
    public async Task A_property_is_checked_by_the_store_the_command_line_uses_and_a_listing_narrows_to_a_partition()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var name = "CFG_API_" + suffix.ToUpperInvariant();
        var partition = "cfg-" + suffix;
        var subject = "config-admin-" + suffix;
        var repoId = Guid.NewGuid();
        var unregistered = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using (var db = CatalogDatabase.Create(cs))
        {
            db.Repos.Add(new CatalogRepo { Id = repoId, Name = "cp-config-" + suffix, FirstSeenUtc = now, LastSyncUtc = now });
            await db.SaveChangesAsync();
        }

        try
        {
            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");
            using var client = factory.CreateClient();
            var admin = await TokenAsync(client, subject, ["admin"]);

            // Set for the repository in the partition: the value and the description are trimmed, the caller recorded.
            using (var set = await SendAsync(client, admin, HttpMethod.Put, $"/api/v1/delivery/config/{name}?repoId={repoId:D}&partition={partition}", new DeliveryConfigSetRequest("  https://test.osdu.example.com  ", "  the test platform  ")))
            {
                var body = await set.Content.ReadAsStringAsync();
                Assert.True(set.StatusCode == HttpStatusCode.OK, body);
                var row = JsonDocument.Parse(body).RootElement;
                Assert.Equal("https://test.osdu.example.com", row.GetProperty("value").GetString());
                Assert.Equal("the test platform", row.GetProperty("description").GetString());
                Assert.Equal("user:" + subject, row.GetProperty("updatedBy").GetString());
            }

            // The control plane's own value in the partition, and one for no partition, which a narrowed listing leaves out.
            using (var set = await SendAsync(client, admin, HttpMethod.Put, $"/api/v1/delivery/config/{name}?partition={partition}", new DeliveryConfigSetRequest("https://cp-test.osdu.example.com", null)))
            {
                Assert.Equal(HttpStatusCode.OK, set.StatusCode);
            }

            using (var set = await SendAsync(client, admin, HttpMethod.Put, $"/api/v1/delivery/config/{name}", new DeliveryConfigSetRequest("https://osdu.example.com", null)))
            {
                Assert.Equal(HttpStatusCode.OK, set.StatusCode);
            }

            var listed = await JsonAsync(client, admin, $"/api/v1/delivery/config?partition={partition}");
            Assert.Equal(
                ["https://cp-test.osdu.example.com", "https://test.osdu.example.com"],
                listed.EnumerateArray().Select(p => p.GetProperty("value").GetString()).Order(StringComparer.Ordinal));
            Assert.All(listed.EnumerateArray(), p => Assert.Equal(partition, p.GetProperty("partition").GetString()));
            var repository = await JsonAsync(client, admin, $"/api/v1/delivery/config?repoId={repoId:D}");
            Assert.Equal(["https://test.osdu.example.com"], repository.EnumerateArray().Select(p => p.GetProperty("value").GetString()));

            // A description longer than its column, and a value of nothing, are the store's refusals, said as such.
            var description = new string('d', DeliveryConfigNames.MaxDescriptionLength + 1);
            var tooLong = await ProblemAsync(client, admin, HttpMethod.Put, $"/api/v1/delivery/config/{name}", new DeliveryConfigSetRequest("https://osdu.example.com", description));
            Assert.Equal(HttpStatusCode.BadRequest, tooLong.Status);
            Assert.Contains($"is {DeliveryConfigNames.MaxDescriptionLength + 1} characters; a property's description is at most {DeliveryConfigNames.MaxDescriptionLength}", tooLong.Detail, StringComparison.Ordinal);
            var blank = await ProblemAsync(client, admin, HttpMethod.Put, $"/api/v1/delivery/config/{name}", new DeliveryConfigSetRequest("   ", null));
            Assert.Equal(HttpStatusCode.BadRequest, blank.Status);
            Assert.Contains("needs a value", blank.Detail, StringComparison.Ordinal);

            // A repository the catalog does not hold is not found, and nothing is set for it.
            var missing = await ProblemAsync(client, admin, HttpMethod.Put, $"/api/v1/delivery/config/{name}?repoId={unregistered:D}", new DeliveryConfigSetRequest("https://osdu.example.com", null));
            Assert.Equal(HttpStatusCode.NotFound, missing.Status);
            Assert.Contains($"No repository {unregistered:D} is registered", missing.Detail, StringComparison.Ordinal);

            // What was set for a repository the catalog no longer holds is listed and removed like any other property.
            await using (var osdu = SampleEstate.Context(cs))
            {
                osdu.DeliveryConfigProperties.Add(new DeliveryConfigProperty
                {
                    Id = Guid.NewGuid(), RepoId = unregistered, Name = name, Value = "https://left.example.com", UpdatedBy = "user:" + subject, UpdatedUtc = now,
                });
                await osdu.SaveChangesAsync();
            }

            var left = await JsonAsync(client, admin, $"/api/v1/delivery/config?repoId={unregistered:D}");
            Assert.Equal("https://left.example.com", Assert.Single(left.EnumerateArray()).GetProperty("value").GetString());
            using (var removed = await SendAsync(client, admin, HttpMethod.Delete, $"/api/v1/delivery/config/{name}?repoId={unregistered:D}"))
            {
                Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
            }

            using (var again = await SendAsync(client, admin, HttpMethod.Delete, $"/api/v1/delivery/config/{name}?repoId={unregistered:D}"))
            {
                Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
            }

            // A partition written as a reference names none, for a listing as for the effective view.
            var reference = await ProblemAsync(client, admin, HttpMethod.Get, $"/api/v1/delivery/config?partition={Uri.EscapeDataString("${env:PART}")}", null);
            Assert.Equal(HttpStatusCode.BadRequest, reference.Status);
            Assert.Contains("is not a data-partition-id", reference.Detail, StringComparison.Ordinal);
        }
        finally
        {
            await using (var osdu = SampleEstate.Context(cs))
            {
                await osdu.DeliveryConfigProperties.Where(p => p.Name == name).ExecuteDeleteAsync();
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
            }
        }
    }

    private static async Task<(HttpStatusCode Status, string Detail)> ProblemAsync(HttpClient client, string token, HttpMethod method, string path, object? body)
    {
        using var response = await SendAsync(client, token, method, path, body);
        var text = await response.Content.ReadAsStringAsync();
        var detail = JsonDocument.Parse(text).RootElement.TryGetProperty("detail", out var said) ? said.GetString() ?? string.Empty : string.Empty;
        return (response.StatusCode, detail);
    }

    private static async Task<JsonElement> JsonAsync(HttpClient client, string token, string path)
    {
        using var response = await SendAsync(client, token, HttpMethod.Get, path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {path} answered {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }

    private static async Task<string> TokenAsync(HttpClient client, string subject, IReadOnlyList<string> scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, subject, scopes.ToArray()));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }
}
