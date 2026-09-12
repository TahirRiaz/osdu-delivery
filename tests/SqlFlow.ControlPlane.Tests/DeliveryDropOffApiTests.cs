using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The drop-off area (docs/delivery/submitting-records.md): the pre-step to a submission, where files are uploaded to a
/// place the compute nodes read and a submission afterwards points at where they landed. What is asserted here is that
/// the files actually arrive, that the ledger says what arrived and who put it there, that the boundary refuses what a
/// drop-off cannot be, and that deleting one takes the files back. Gated on a reachable catalog database.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DeliveryDropOffApiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqlflow-dropoff-" + Guid.NewGuid().ToString("N")[..10]);
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(PayloadRoots.DropOffEnvironmentVariable);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PayloadRoots.DropOffEnvironmentVariable, _previousRoot);
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A file still held open is left to the temp folder's own cleanup.
        }
    }

    [SkippableFact]
    public async Task An_upload_lands_its_files_and_the_ledger_says_what_arrived()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.ProvisionAsync(cs);
        Environment.SetEnvironmentVariable(PayloadRoots.DropOffEnvironmentVariable, _root);
        await using var factory = Factory(cs);
        using var client = factory.CreateClient();
        var token = await TokenAsync(client);

        var curves = Encoding.UTF8.GetBytes("depth,value\n1000.0,12.5\n");
        var notes = Encoding.UTF8.GetBytes("a second file\n");
        using var response = await UploadAsync(client, token, [("curves.csv", curves), ("notes.txt", notes)], "the wellbore run");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"expected 200, got {response.StatusCode}: {body}");
        var dropOff = await response.Content.ReadFromJsonAsync<DeliveryDropOffDto>();
        Assert.NotNull(dropOff);
        Assert.Equal(DropOffStatus.Complete, dropOff.Status);
        Assert.Equal(2, dropOff.FileCount);
        Assert.Equal(curves.Length + notes.Length, dropOff.TotalBytes);
        Assert.Equal("the wellbore run", dropOff.Label);
        Assert.StartsWith(_root, dropOff.Location.Replace('/', Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

        // The files are where the answer says they are, byte for byte, and the hashes are of what landed.
        foreach (var (name, content) in new[] { ("curves.csv", curves), ("notes.txt", notes) })
        {
            var path = Path.Combine(dropOff.Location, name);
            Assert.True(File.Exists(path), $"{path} was not written");
            Assert.Equal(content, await File.ReadAllBytesAsync(path));
            var file = Assert.Single(dropOff.Files, f => f.Name == name);
            Assert.Equal(content.Length, file.Bytes);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), file.Sha256);
        }

        await using var db = CatalogDatabase.Create(cs);
        var row = await db.DeliveryDropOffs.AsNoTracking().SingleAsync(d => d.DropOffId == dropOff.DropOffId);
        Assert.Equal(DropOffStatus.Complete, row.Status);
        Assert.NotNull(row.CompletedUtc);
        Assert.False(string.IsNullOrWhiteSpace(row.UploadedBy));
    }

    [SkippableFact]
    public async Task A_drop_off_is_listed_read_back_and_taken_away_again()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.ProvisionAsync(cs);
        Environment.SetEnvironmentVariable(PayloadRoots.DropOffEnvironmentVariable, _root);
        await using var factory = Factory(cs);
        using var client = factory.CreateClient();
        var token = await TokenAsync(client);

        using var uploaded = await UploadAsync(client, token, [("curves.csv", Encoding.UTF8.GetBytes("depth,value\n1,2\n"))], label: null);
        var dropOff = (await uploaded.Content.ReadFromJsonAsync<DeliveryDropOffDto>())!;

        using var listed = await GetAsync(client, token, "/api/v1/delivery/dropoffs");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var all = (await listed.Content.ReadFromJsonAsync<List<DeliveryDropOffDto>>())!;
        Assert.Contains(all, d => d.DropOffId == dropOff.DropOffId);

        using var one = await GetAsync(client, token, $"/api/v1/delivery/dropoffs/{dropOff.DropOffId}");
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        Assert.Equal(dropOff.Location, (await one.Content.ReadFromJsonAsync<DeliveryDropOffDto>())!.Location);

        using var missing = await GetAsync(client, token, $"/api/v1/delivery/dropoffs/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        // The area says what it is, so a caller knows before uploading.
        using var area = await GetAsync(client, token, "/api/v1/delivery/dropoff-area");
        var described = (await area.Content.ReadFromJsonAsync<DeliveryDropOffAreaDto>())!;
        Assert.True(described.Enabled);
        Assert.Equal(0, described.RetentionDays);

        // Taking it back removes the files and says so; the row stays, because the ledger says what happened.
        using var deleted = await DeleteAsync(client, token, $"/api/v1/delivery/dropoffs/{dropOff.DropOffId}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal(DropOffStatus.Deleted, (await deleted.Content.ReadFromJsonAsync<DeliveryDropOffDto>())!.Status);
        Assert.False(Directory.Exists(dropOff.Location));

        await using var db = CatalogDatabase.Create(cs);
        var row = await db.DeliveryDropOffs.AsNoTracking().SingleAsync(d => d.DropOffId == dropOff.DropOffId);
        Assert.Equal(DropOffStatus.Deleted, row.Status);
        Assert.NotNull(row.DeletedUtc);
    }

    [SkippableFact]
    public async Task The_boundary_refuses_what_a_drop_off_cannot_be()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.ProvisionAsync(cs);
        Environment.SetEnvironmentVariable(PayloadRoots.DropOffEnvironmentVariable, _root);
        await using var factory = Factory(cs).WithSetting("ControlPlane:DropOff:MaxFilesPerUpload", "2");
        using var client = factory.CreateClient();
        var token = await TokenAsync(client);
        var content = Encoding.UTF8.GetBytes("x\n");

        // A body with no parts at all cannot be parsed as a form, so the parse guard is what refuses it.
        using (var empty = await UploadAsync(client, token, [], label: null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
            Assert.Contains("multipart/form-data", await empty.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // A form that parses but carries no file parts is refused for what it is.
        using (var none = await UploadAsync(client, token, [], label: "nothing attached"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
            Assert.Contains("carries no files", await none.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using (var path = await UploadAsync(client, token, [("../escape.csv", content)], label: null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, path.StatusCode);
            Assert.Contains("is a path, not a file name", await path.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using (var nested = await UploadAsync(client, token, [("folder/curves.csv", content)], label: null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, nested.StatusCode);
        }

        using (var tooMany = await UploadAsync(client, token, [("a.csv", content), ("b.csv", content), ("c.csv", content)], label: null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
            Assert.Contains("at most 2 files", await tooMany.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using (var twice = await UploadAsync(client, token, [("same.csv", content), ("same.csv", content)], label: null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, twice.StatusCode);
            Assert.Contains("uploaded twice", await twice.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // Nothing was stored for any of them.
        await using var db = CatalogDatabase.Create(cs);
        Assert.Equal(0, await db.DeliveryDropOffs.AsNoTracking().CountAsync(d => d.Location.StartsWith(_root)));
    }

    [SkippableFact]
    public async Task A_deployment_with_no_drop_off_area_says_so_instead_of_taking_files()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.ProvisionAsync(cs);
        Environment.SetEnvironmentVariable(PayloadRoots.DropOffEnvironmentVariable, null);
        await using var factory = Factory(cs);
        using var client = factory.CreateClient();
        var token = await TokenAsync(client);

        using var area = await GetAsync(client, token, "/api/v1/delivery/dropoff-area");
        var described = (await area.Content.ReadFromJsonAsync<DeliveryDropOffAreaDto>())!;
        Assert.False(described.Enabled);
        Assert.Null(described.Location);

        using var refused = await UploadAsync(client, token, [("curves.csv", Encoding.UTF8.GetBytes("x\n"))], label: null);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains(PayloadRoots.DropOffEnvironmentVariable, await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Uploading_needs_a_token()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.ProvisionAsync(cs);
        Environment.SetEnvironmentVariable(PayloadRoots.DropOffEnvironmentVariable, _root);
        await using var factory = Factory(cs);
        using var client = factory.CreateClient();

        using var anonymous = await UploadAsync(client, token: null, [("curves.csv", Encoding.UTF8.GetBytes("x\n"))], label: null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    private static ControlPlaneAppFactory Factory(string cs)
        => new ControlPlaneAppFactory().WithCatalog(cs).WithSetting("ControlPlane:Worker:Enabled", "false");

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

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client, string? token, IReadOnlyList<(string Name, byte[] Content)> files, string? label)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/delivery/dropoffs", UriKind.Relative));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var form = new MultipartFormDataContent();
        foreach (var (name, content) in files)
        {
            form.Add(new ByteArrayContent(content), "files", name);
        }

        if (label is not null)
        {
            form.Add(new StringContent(label), "label");
        }

        request.Content = form;
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> DeleteAsync(HttpClient client, string token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }
}
