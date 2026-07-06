using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The schema-browse and object-dossier read API end to end against the real shadow catalog: the test seeds a
/// handful of catalog objects (with a script and a column dictionary) under one isolated server identity, then
/// asserts that <c>/lineage/schemas</c> reports the (server, database, schema) groupings with counts, that
/// <c>/lineage/objects</c> filters by database and schema, and that <c>/lineage/objects/dossier</c> returns an
/// object's script and columns. This is the "answer questions about the schema" path a model uses. Every
/// seeded row is removed in a finally so repeated runs stay isolated.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LineageSchemaApiTests
{
    [SkippableFact]
    public async Task SchemaBrowse_ListsSchemas_FiltersObjects_AndReturnsDossier()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        // An isolated server identity so the schema grouping and counts are deterministic in a shared catalog.
        var serverRef = "${env:SQLFLOW_TEST_" + suffix + "}";
        var ordersKey = $"{serverRef}|dw|dbo|orders";
        var now = DateTime.UtcNow;

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Objects.Add(SeedObject(ordersKey, serverRef, "DW", "dbo", "Orders", "Table", now,
                    script: "CREATE TABLE [DW].[dbo].[Orders] ([Id] int NOT NULL, [Amount] decimal(18,2) NULL);"));
                db.Objects.Add(SeedObject($"{serverRef}|dw|dbo|vorders", serverRef, "DW", "dbo", "vOrders", "View", now));
                db.Objects.Add(SeedObject($"{serverRef}|dw|stg|orders", serverRef, "DW", "stg", "Orders", "Table", now));

                db.ObjectColumns.Add(new CatalogObjectColumn { ObjectKey = ordersKey, Ordinal = 1, Name = "Id", DataType = "int", Nullable = false, Tier = "Observed" });
                db.ObjectColumns.Add(new CatalogObjectColumn { ObjectKey = ordersKey, Ordinal = 2, Name = "Amount", DataType = "decimal(18,2)", Nullable = true, Tier = "Observed" });
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueReadTokenAsync(client);

            // The schema hierarchy: two schemas under DW on our server, dbo with 2 objects and stg with 1.
            var schemas = await GetJsonAsync<IReadOnlyList<SchemaDto>>(
                client, token, $"/api/v1/lineage/schemas?serverRef={Uri.EscapeDataString(serverRef)}");
            Assert.Equal(2, schemas.Count);
            Assert.Equal(2, schemas.Single(s => s.Schema == "dbo").ObjectCount);
            Assert.Equal(1, schemas.Single(s => s.Schema == "stg").ObjectCount);
            Assert.All(schemas, s => Assert.Equal("DW", s.Database));

            // Filtering objects by database + schema enumerates just that schema's objects.
            var dboObjects = await GetJsonAsync<PagedResult<ObjectDto>>(
                client, token, $"/api/v1/lineage/objects?serverRef={Uri.EscapeDataString(serverRef)}&database=DW&schema=dbo&pageSize=200");
            Assert.Equal(2, dboObjects.Total);
            Assert.All(dboObjects.Items, o => Assert.Equal("dbo", o.Schema));

            // The dossier returns the object's generating script and its column dictionary in one payload.
            var dossier = await GetJsonAsync<ObjectDossierDto>(
                client, token, $"/api/v1/lineage/objects/dossier?key={Uri.EscapeDataString(ordersKey)}");
            Assert.Equal(ordersKey, dossier.Object.Key);
            Assert.Contains("CREATE TABLE", dossier.Object.Script ?? string.Empty, StringComparison.Ordinal);
            Assert.Equal(2, dossier.Columns.Count);
            Assert.Equal(["Amount", "Id"], dossier.Columns.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal));
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.ObjectColumns.Where(c => c.ObjectKey.StartsWith(serverRef)).ExecuteDeleteAsync();
            await db.Objects.Where(o => o.ServerRef == serverRef).ExecuteDeleteAsync();
        }
    }

    private static CatalogObject SeedObject(
        string key, string serverRef, string database, string schema, string name, string kind, DateTime now, string? script = null)
        => new()
        {
            Key = key,
            ServerRef = serverRef,
            Database = database,
            Schema = schema,
            Name = name,
            Kind = kind,
            Script = script,
            ScriptTier = script is null ? null : "Observed",
            ScriptUpdatedUtc = script is null ? null : now,
            FirstSeenUtc = now,
            LastSeenUtc = now,
        };

    private static async Task<string> IssueReadTokenAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["read"]));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    private static async Task<T> GetJsonAsync<T>(HttpClient client, string token, string relativeUri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(relativeUri, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(value);
        return value;
    }
}
