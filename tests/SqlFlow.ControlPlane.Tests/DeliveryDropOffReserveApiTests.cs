using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.Storage;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Reserving a drop-off and completing it: the path a file too large to send through the control plane takes
/// (docs/delivery/submitting-records.md). What is asserted is that the row exists before a single URL is handed out,
/// that completion is decided by what storage actually holds rather than by what the caller says, that a hash the
/// control plane never computed is recorded as the caller's claim and not as a check, and that a reservation whose
/// upload did not finish cannot be completed into a drop-off a submission would point at.
/// <para>
/// The deployment's own signed uploads are Azure Storage's. Here the drop-off area is a local folder, so a test issuer
/// stands in: it authorises the same locations and the test writes the bytes there itself, which is exactly what a
/// caller holding a URL does.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class DeliveryDropOffReserveApiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqlflow-reserve-" + Guid.NewGuid().ToString("N")[..10]);
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
    public async Task A_reserved_drop_off_is_recorded_before_any_url_is_handed_out_and_completes_on_what_landed()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.ProvisionAsync(cs);
        Environment.SetEnvironmentVariable(PayloadRoots.DropOffEnvironmentVariable, _root);
        await using var factory = Signing(cs);
        using var client = factory.CreateClient();
        var token = await TokenAsync(client);

        var curves = Encoding.UTF8.GetBytes("depth,value\n1000.0,12.5\n");
        var reservation = await ReserveAsync(client, token, [("curves.csv", curves.Length)], "the wellbore run");

        Assert.Equal(DropOffStatus.Uploading, reservation.Status);
        Assert.Equal("the wellbore run", reservation.Label);
        Assert.Single(reservation.Uploads);
        Assert.Equal("curves.csv", reservation.Uploads[0].Name);
        Assert.True(reservation.ReservedUntilUtc > DateTime.UtcNow, "the reservation expires in the future");

        // The row is there before anything has landed, so an upload nobody finishes is visible as one.
        await using (var db = Db(cs))
        {
            var row = await db.DeliveryDropOffs.AsNoTracking().SingleAsync(d => d.DropOffId == reservation.DropOffId);
            Assert.Equal(DropOffStatus.Uploading, row.Status);
            Assert.Equal(DropOffUploadMode.Signed, row.UploadMode);
            Assert.Equal(0, row.TotalBytes);
            Assert.NotNull(row.ReservedUntilUtc);
        }

        // A reservation is not something a submission may point at until it is complete.
        using (var early = await GetAsync(client, token, reservation.DropOffId))
        {
            var pending = (await early.Content.ReadFromJsonAsync<DeliveryDropOffDto>())!;
            Assert.Equal(DropOffStatus.Uploading, pending.Status);
        }

        // The caller writes the bytes to where its URL points, which here is the location the reservation named.
        await File.WriteAllBytesAsync(reservation.Uploads[0].Location, curves);

        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(curves));
        var completed = await CompleteAsync(client, token, reservation.DropOffId, [("curves.csv", hash)]);
        Assert.Equal(DropOffStatus.Complete, completed.Status);
        Assert.Equal(DropOffUploadMode.Signed, completed.UploadMode);
        Assert.Equal(1, completed.FileCount);
        Assert.Equal(curves.Length, completed.TotalBytes);

        // The size is storage's own answer; the hash is the caller's word, and the ledger says which is which.
        var file = Assert.Single(completed.Files);
        Assert.Equal("curves.csv", file.Name);
        Assert.Equal(curves.Length, file.Bytes);
        Assert.Equal(hash, file.Sha256);
        Assert.Equal(DropOffHashSource.Client, file.HashSource);

        // Completing again answers the same drop-off rather than re-reading storage, so a retried call is safe.
        var again = await CompleteAsync(client, token, reservation.DropOffId, [("curves.csv", hash)]);
        Assert.Equal(DropOffStatus.Complete, again.Status);
        Assert.Equal(completed.CompletedUtc, again.CompletedUtc);
    }

    [SkippableFact]
    public async Task A_file_that_did_not_land_whole_is_not_completed_into_a_drop_off()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.ProvisionAsync(cs);
        Environment.SetEnvironmentVariable(PayloadRoots.DropOffEnvironmentVariable, _root);
        await using var factory = Signing(cs);
        using var client = factory.CreateClient();
        var token = await TokenAsync(client);

        var reservation = await ReserveAsync(client, token, [("curves.csv", 24), ("notes.txt", 14)], label: null);

        // Nothing written at all: the reservation stays open so the caller can upload and complete again.
        using (var nothing = await CompleteRawAsync(client, token, reservation.DropOffId, []))
        {
            Assert.Equal(HttpStatusCode.BadRequest, nothing.StatusCode);
            var body = await nothing.Content.ReadAsStringAsync();
            Assert.Contains("not landed", body, StringComparison.Ordinal);
            Assert.Contains("curves.csv", body, StringComparison.Ordinal);
            Assert.Contains("notes.txt", body, StringComparison.Ordinal);
        }

        // One file short of what was reserved, and the refusal names the one that is missing.
        await File.WriteAllBytesAsync(reservation.Uploads[0].Location, new byte[24]);
        using (var partial = await CompleteRawAsync(client, token, reservation.DropOffId, []))
        {
            Assert.Equal(HttpStatusCode.BadRequest, partial.StatusCode);
            var body = await partial.Content.ReadAsStringAsync();
            Assert.Contains("notes.txt", body, StringComparison.Ordinal);
            Assert.DoesNotContain("curves.csv", body, StringComparison.Ordinal);
        }

        // An upload that stopped partway is short, which is not the file that was reserved.
        await File.WriteAllBytesAsync(reservation.Uploads[1].Location, new byte[9]);
        using (var truncated = await CompleteRawAsync(client, token, reservation.DropOffId, []))
        {
            Assert.Equal(HttpStatusCode.BadRequest, truncated.StatusCode);
            var body = await truncated.Content.ReadAsStringAsync();
            Assert.Contains("'notes.txt' is 9 bytes, not the 14 reserved", body, StringComparison.Ordinal);
        }

        // The row never became complete through any of that, so nothing could have pointed at it.
        await using var db = Db(cs);
        Assert.Equal(DropOffStatus.Uploading, (await db.DeliveryDropOffs.AsNoTracking().SingleAsync(d => d.DropOffId == reservation.DropOffId)).Status);

        // Written in full, it completes, and the file nobody reported a hash for says it has none.
        await File.WriteAllBytesAsync(reservation.Uploads[1].Location, new byte[14]);
        var completed = await CompleteAsync(client, token, reservation.DropOffId, []);
        Assert.Equal(DropOffStatus.Complete, completed.Status);
        Assert.All(completed.Files, f => Assert.Equal(DropOffHashSource.None, f.HashSource));
        Assert.All(completed.Files, f => Assert.Equal(string.Empty, f.Sha256));
    }

    [SkippableFact]
    public async Task A_reservation_refuses_what_it_cannot_account_for()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.ProvisionAsync(cs);
        Environment.SetEnvironmentVariable(PayloadRoots.DropOffEnvironmentVariable, _root);
        await using var factory = Signing(cs);
        using var client = factory.CreateClient();
        var token = await TokenAsync(client);

        // A path, not a name: nothing may be written outside the drop-off it belongs to.
        using (var path = await ReserveRawAsync(client, token, [("../escape.csv", 10)], null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, path.StatusCode);
            Assert.Contains("is a path, not a file name", await path.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using (var twice = await ReserveRawAsync(client, token, [("a.csv", 10), ("A.CSV", 10)], null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, twice.StatusCode);
            Assert.Contains("is named twice", await twice.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using (var empty = await ReserveRawAsync(client, token, [("a.csv", 0)], null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
            Assert.Contains("has nothing to deliver", await empty.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using (var none = await ReserveRawAsync(client, token, [], null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
            Assert.Contains("names the files it will upload", await none.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // Larger than a reserved file may be, which is a different and far higher ceiling than a streamed upload's.
        using (var huge = await ReserveRawAsync(client, token, [("a.csv", 65L * 1024 * 1024 * 1024)], null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, huge.StatusCode);
            Assert.Contains("at most 64 GB", await huge.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // A hash that is not a SHA-256 is refused rather than stored as one.
        var reservation = await ReserveAsync(client, token, [("a.csv", 4)], null);
        await File.WriteAllBytesAsync(reservation.Uploads[0].Location, new byte[4]);
        using (var bad = await CompleteRawAsync(client, token, reservation.DropOffId, [("a.csv", "not-a-hash")]))
        {
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Contains("64 hexadecimal characters", await bad.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // A hash for a file the reservation never named describes nothing that can be there.
        using (var stranger = await CompleteRawAsync(client, token, reservation.DropOffId, [("b.csv", new string('a', 64))]))
        {
            Assert.Equal(HttpStatusCode.BadRequest, stranger.StatusCode);
            Assert.Contains("reported but not reserved", await stranger.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    [SkippableFact]
    public async Task A_streamed_drop_off_has_nothing_to_complete_and_says_where_its_hashes_came_from()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.ProvisionAsync(cs);
        Environment.SetEnvironmentVariable(PayloadRoots.DropOffEnvironmentVariable, _root);
        await using var factory = Signing(cs);
        using var client = factory.CreateClient();
        var token = await TokenAsync(client);

        var content = Encoding.UTF8.GetBytes("depth,value\n1000.0,12.5\n");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/delivery/dropoffs", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var form = new MultipartFormDataContent { { new ByteArrayContent(content), "files", "curves.csv" } };
        request.Content = form;
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var streamed = (await response.Content.ReadFromJsonAsync<DeliveryDropOffDto>())!;

        Assert.Equal(DropOffUploadMode.Stream, streamed.UploadMode);
        Assert.Null(streamed.ReservedUntilUtc);
        Assert.Equal(DropOffHashSource.Computed, Assert.Single(streamed.Files).HashSource);

        using var completing = await CompleteRawAsync(client, token, streamed.DropOffId, []);
        Assert.Equal(HttpStatusCode.Conflict, completing.StatusCode);
        Assert.Contains("nothing to complete", await completing.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Without_a_store_that_can_issue_urls_the_area_says_so_and_a_reservation_is_refused()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.ProvisionAsync(cs);
        Environment.SetEnvironmentVariable(PayloadRoots.DropOffEnvironmentVariable, _root);

        // No test issuer: the host's own can only sign Azure Storage locations, and this drop-off area is a folder.
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs).WithSetting("ControlPlane:Worker:Enabled", "false");
        using var client = factory.CreateClient();
        var token = await TokenAsync(client);

        using var area = await GetRawAsync(client, token, new Uri("/api/v1/delivery/dropoff-area", UriKind.Relative));
        var described = (await area.Content.ReadFromJsonAsync<DeliveryDropOffAreaDto>())!;
        Assert.True(described.Enabled);
        Assert.False(described.SignedUploads);
        Assert.Equal(0, described.MaxSignedFileGigabytes);

        using var refused = await ReserveRawAsync(client, token, [("a.csv", 10)], null);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("cannot hand out upload URLs", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Reserving_and_completing_need_a_token()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.ProvisionAsync(cs);
        Environment.SetEnvironmentVariable(PayloadRoots.DropOffEnvironmentVariable, _root);
        await using var factory = Signing(cs);
        using var client = factory.CreateClient();

        using var reserve = await ReserveRawAsync(client, token: null, [("a.csv", 10)], null);
        Assert.Equal(HttpStatusCode.Unauthorized, reserve.StatusCode);

        using var complete = await CompleteRawAsync(client, token: null, Guid.NewGuid(), []);
        Assert.Equal(HttpStatusCode.Unauthorized, complete.StatusCode);
    }

    private static ControlPlaneAppFactory Signing(string cs)
        => new ControlPlaneAppFactory()
            .WithCatalog(cs)
            .WithSetting("ControlPlane:Worker:Enabled", "false")
            .WithServices(s => s.AddSingleton<ISignedUploadIssuer, LocalPathUploadIssuer>());

    private static CatalogDbContext Db(string cs)
        => new(new DbContextOptionsBuilder<CatalogDbContext>().UseSqlServer(cs).Options);

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

    private static async Task<DeliveryDropOffReservationDto> ReserveAsync(
        HttpClient client, string token, IReadOnlyList<(string Name, long Bytes)> files, string? label)
    {
        using var response = await ReserveRawAsync(client, token, files, label);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"expected 200, got {response.StatusCode}: {body}");
        var reservation = await response.Content.ReadFromJsonAsync<DeliveryDropOffReservationDto>();
        Assert.NotNull(reservation);
        return reservation;
    }

    private static Task<HttpResponseMessage> ReserveRawAsync(
        HttpClient client, string? token, IReadOnlyList<(string Name, long Bytes)> files, string? label)
        => SendAsync(
            client, token, new Uri("/api/v1/delivery/dropoffs/reserve", UriKind.Relative),
            new DeliveryDropOffReserveRequest(files.Select(f => new DeliveryDropOffReserveFile(f.Name, f.Bytes)).ToList(), label));

    private static async Task<DeliveryDropOffDto> CompleteAsync(
        HttpClient client, string token, Guid dropOffId, IReadOnlyList<(string Name, string Sha256)> files)
    {
        using var response = await CompleteRawAsync(client, token, dropOffId, files);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"expected 200, got {response.StatusCode}: {body}");
        var dropOff = await response.Content.ReadFromJsonAsync<DeliveryDropOffDto>();
        Assert.NotNull(dropOff);
        return dropOff;
    }

    private static Task<HttpResponseMessage> CompleteRawAsync(
        HttpClient client, string? token, Guid dropOffId, IReadOnlyList<(string Name, string Sha256)> files)
        => SendAsync(
            client, token, new Uri($"/api/v1/delivery/dropoffs/{dropOffId:D}/complete", UriKind.Relative),
            new DeliveryDropOffCompleteRequest(files.Select(f => new DeliveryDropOffCompleteFile(f.Name, f.Sha256)).ToList()));

    private static async Task<HttpResponseMessage> SendAsync<T>(HttpClient client, string? token, Uri uri, T body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(body) };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string token, Guid dropOffId)
        => GetRawAsync(client, token, new Uri($"/api/v1/delivery/dropoffs/{dropOffId:D}", UriKind.Relative));

    private static async Task<HttpResponseMessage> GetRawAsync(HttpClient client, string token, Uri uri)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return response;
    }

    /// <summary>
    /// Stands in for cloud storage handing out an upload URL, so the reserve-and-complete contract can be exercised
    /// where the drop-off area is a folder. It authorises exactly the location it is asked about and nothing else,
    /// which is what the real issuer does; the test then writes the bytes there, as a caller holding a URL would.
    /// </summary>
    private sealed class LocalPathUploadIssuer : ISignedUploadIssuer
    {
        public bool CanHandle(string location) => !string.IsNullOrWhiteSpace(location) && Path.IsPathRooted(location);

        public Task<SignedUpload> CreateUploadAsync(string location, TimeSpan lifetime, CancellationToken ct = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(location)!);
            return Task.FromResult(new SignedUpload(location, new Uri(new Uri("file:///"), location), DateTimeOffset.UtcNow + lifetime));
        }
    }
}
