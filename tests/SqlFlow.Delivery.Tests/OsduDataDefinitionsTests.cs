using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Templates;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The OSDU data definitions kept as a local copy: the release list read over the repository's GitLab API (page by page)
/// and kept on disk, each release's Generated folder downloaded once as an archive and read from disk after that, across
/// instances, a sync reading the list again and downloading what is missing, and the failures each step can meet.
/// </summary>
public sealed class OsduDataDefinitionsTests : IDisposable
{
    private const string OlderCommit = "0000000000000000000000000000000000000291";
    private const string ReleaseCommit = "99f8fc88d8ad838b5738ac5ad92ac643538b5766";
    private const string NextCommit = "31f0000000000000000000000000000000000031";
    private const string WellboreKind = "osdu:wks:master-data--Wellbore:1.3.0";

    /// <summary>The v0.30.0 release's Generated folder, reduced to a Wellbore schema and the abstract schemas it refers to.</summary>
    private static readonly Dictionary<string, string> Tree = new(StringComparer.Ordinal)
    {
        ["SchemaStatus.json"] = """
            { "osdu:wks:AbstractAccessControlList:1.0.0": "PUBLISHED", "osdu:wks:master-data--Wellbore:1.0.0": "PUBLISHED",
              "osdu:wks:master-data--Wellbore:1.3.0": "PUBLISHED", "osdu:wks:master-data--Well:1.2.0": "DEVELOPMENT", "osdu:wks:Manifest:1.0.0": "PUBLISHED" }
            """,
        ["master-data/Wellbore.1.3.0.json"] = """
            { "$id": "https://schema.osdu.opengroup.org/json/master-data/Wellbore.1.3.0.json", "x-osdu-schema-source": "osdu:wks:master-data--Wellbore:1.3.0", "type": "object",
              "properties": {
                "acl": { "$ref": "../abstract/AbstractAccessControlList.1.0.0.json" },
                "data": { "allOf": [ { "$ref": "../abstract/AbstractFacility.1.1.0.json" }, { "type": "object", "properties": { "WellID": { "type": "string" } } } ] } } }
            """,
        ["abstract/AbstractAccessControlList.1.0.0.json"] = """
            { "type": "object", "properties": { "owners": { "type": "array", "items": { "type": "string" } } } }
            """,
        ["abstract/AbstractFacility.1.1.0.json"] = """
            { "type": "object", "properties": { "FacilityName": { "type": "string" }, "Owners": { "$ref": "AbstractAccessControlList.1.0.0.json" } } }
            """,
    };

    private static readonly Dictionary<string, string> OlderTree = new(StringComparer.Ordinal)
    {
        ["SchemaStatus.json"] = """{ "osdu:wks:master-data--Well:1.0.0": "DEVELOPMENT" }""",
    };

    private readonly string _cache = Samples.NewTempDirectory();

    public void Dispose()
    {
        if (Directory.Exists(_cache))
        {
            Directory.Delete(_cache, recursive: true);
        }
    }

    [Fact]
    public async Task The_release_list_is_read_page_by_page_and_kept_on_disk_for_the_next_instance()
    {
        var repository = new Repository();
        using var http = repository.Client();
        var definitions = Definitions(http, Timeout.InfiniteTimeSpan);

        var releases = await definitions.ReleasesAsync();
        Assert.Equal(["v0.30.0", "v0.29.1", "v0.28.3.1"], releases.Select(r => r.Name));
        Assert.Equal(ReleaseCommit, releases[0].Commit);
        Assert.Equal(2, repository.TagPages);
        Assert.NotNull(definitions.SyncedUtc);
        Assert.False(definitions.IsLocal(releases[0]));

        // A new instance over the same local copy (a restart) answers from disk without asking the repository.
        var offline = new Repository { TagsFail = true };
        using var offlineHttp = offline.Client();
        var restarted = Definitions(offlineHttp, Timeout.InfiniteTimeSpan);
        Assert.Equal(["v0.30.0", "v0.29.1", "v0.28.3.1"], (await restarted.ReleasesAsync()).Select(r => r.Name));
        Assert.Equal(0, offline.TagPages);
    }

    [Fact]
    public async Task A_release_is_downloaded_once_and_read_from_disk_after_that_even_by_the_next_instance()
    {
        var repository = new Repository();
        using var http = repository.Client();
        var clock = new TestClock();
        var definitions = Definitions(http, Timeout.InfiniteTimeSpan, clock);

        var index = await definitions.IndexAsync(release: null);
        Assert.Equal("v0.30.0", index.Release.Name);

        // Abstract building blocks and the manifest are not records a mapping fills, so the index leaves them out.
        Assert.Equal(["osdu:wks:master-data--Well:1.2.0", "osdu:wks:master-data--Wellbore:1.3.0", "osdu:wks:master-data--Wellbore:1.0.0"], index.Schemas.Select(s => s.Kind));
        var wellbore = index.Schemas[1];
        Assert.Equal(("master-data--Wellbore", "1.3.0", "PUBLISHED", "master-data/Wellbore.1.3.0.json"), (wellbore.EntityType, wellbore.Version, wellbore.Status, wellbore.Path));
        Assert.Equal(
            "https://community.opengroup.org/osdu/data/data-definitions/-/blob/v0.30.0/Generated/master-data/Wellbore.1.3.0.json",
            definitions.FileWebUrl(index.Release, wellbore.Path).AbsoluteUri);
        Assert.True(definitions.IsLocal(index.Release));

        var file = await definitions.FetchAsync("v0.30.0", WellboreKind);
        Assert.Equal($"OSDU data definitions v0.30.0 ({ReleaseCommit[..12]}) Generated/master-data/Wellbore.1.3.0.json", file.Origin);
        Assert.Equal(["master-data/Wellbore.1.3.0.json", "abstract/AbstractAccessControlList.1.0.0.json", "abstract/AbstractFacility.1.1.0.json"], file.Files);
        Assert.Equal(["AbstractAccessControlList.1.0.0", "AbstractFacility.1.1.0"], Assert.IsType<JsonObject>(file.Schema.Root["definitions"]).Select(d => d.Key));
        var template = OsduTemplate.From(file.Schema);
        foreach (var path in new[] { "osdu.acl.owners", "osdu.data.FacilityName", "osdu.data.WellID" })
        {
            Assert.NotNull(template.Find(TemplatePath.TryParse(path, out var parsed, out _) ? parsed! : throw new InvalidOperationException(path)));
        }

        // The whole release came down as one archive, however many files were read.
        Assert.Equal([ReleaseCommit], repository.Archives);

        // The next instance reads the same release from disk: no archive, and the same template version.
        var restarted = Definitions(http, Timeout.InfiniteTimeSpan, clock);
        Assert.Equal(file.Schema.Version, (await restarted.FetchAsync("v0.30.0", WellboreKind)).Schema.Version);
        Assert.Equal([ReleaseCommit], repository.Archives);

        // An older release is its own download, at its own commit.
        Assert.Equal(["osdu:wks:master-data--Well:1.0.0"], (await definitions.IndexAsync("v0.29.1")).Schemas.Select(s => s.Kind));
        Assert.Equal([ReleaseCommit, OlderCommit], repository.Archives);

        // A checkout of the same release bundles to the same template version.
        var root = Samples.NewTempDirectory();
        foreach (var (path, json) in Tree)
        {
            var target = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, json);
        }

        Assert.Equal(file.Schema.Version, (await TemplateSources.FromDirectoryAsync(root, WellboreKind, clock)).Version);
    }

    [Fact]
    public async Task A_sync_reads_the_release_list_again_and_downloads_the_release_it_is_asked_for()
    {
        var repository = new Repository();
        using var http = repository.Client();
        var definitions = Definitions(http, Timeout.InfiniteTimeSpan);
        Assert.Equal("v0.30.0", (await definitions.ReleasesAsync())[0].Name);

        repository.NextRelease = true;

        // Kept until a sync: the list does not change on its own.
        Assert.DoesNotContain(await definitions.ReleasesAsync(), r => r.Name == "v0.31.0");

        var sync = await definitions.SyncAsync(release: null);
        Assert.Equal("v0.31.0", sync.Releases[0].Name);
        Assert.Equal(["v0.31.0"], sync.Downloaded);
        Assert.True(definitions.IsLocal(sync.Releases[0]));

        // A release already on disk is not downloaded again.
        var again = await definitions.SyncAsync("v0.31.0");
        Assert.Empty(again.Downloaded);
        Assert.Equal([NextCommit], repository.Archives);

        await Assert.ThrowsAsync<DataDefinitionsException>(() => definitions.SyncAsync("v9.9.9"));
    }

    [Fact]
    public async Task What_the_repository_does_not_hold_is_not_found_and_a_repository_that_fails_says_so()
    {
        var repository = new Repository();
        using var http = repository.Client();
        var definitions = Definitions(http, Timeout.InfiniteTimeSpan);

        var release = await Assert.ThrowsAsync<DataDefinitionsException>(() => definitions.IndexAsync("v9.9.9"));
        Assert.True(release.NotFound);
        Assert.Contains("no release 'v9.9.9'; the latest is v0.30.0", release.Message, StringComparison.Ordinal);

        var kind = await Assert.ThrowsAsync<DataDefinitionsException>(() => definitions.FetchAsync(null, "osdu:wks:master-data--Missing:1.0.0"));
        Assert.True(kind.NotFound);
        Assert.Contains("Generated/master-data/Missing.1.0.0.json at v0.30.0", kind.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<FlowValidationException>(() => definitions.FetchAsync(null, "Wellbore"));

        // No list on disk and the repository down: the failure is the answer.
        var down = new Repository { TagsFail = true };
        using var downHttp = down.Client();
        var failure = await Assert.ThrowsAsync<DataDefinitionsException>(() => Definitions(downHttp, Timeout.InfiniteTimeSpan, cache: Samples.NewTempDirectory()).ReleasesAsync());
        Assert.False(failure.NotFound);
        Assert.Contains("answered 503", failure.Message, StringComparison.Ordinal);

        // An archive whose entry would land outside the release folder is refused, and nothing of it is kept.
        var hostile = new Repository { HostileArchive = true };
        using var hostileHttp = hostile.Client();
        var refused = Definitions(hostileHttp, Timeout.InfiniteTimeSpan, cache: Samples.NewTempDirectory());
        var outside = await Assert.ThrowsAsync<DataDefinitionsException>(() => refused.IndexAsync(null));
        Assert.Contains("leads outside the release folder", outside.Message, StringComparison.Ordinal);
        Assert.False(refused.IsLocal((await refused.ReleasesAsync())[0]));
    }

    [Fact]
    public async Task A_reference_that_leads_outside_the_data_definitions_is_refused()
    {
        var root = Samples.NewTempDirectory();
        Directory.CreateDirectory(Path.Combine(root, "master-data"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "master-data", "Wellbore.1.3.0.json"),
            """{ "type": "object", "properties": { "acl": { "$ref": "../../private/AbstractAccessControlList.1.0.0.json" }, "data": { "type": "object" } } }""");

        var ex = await Assert.ThrowsAsync<DeliveryException>(() => TemplateSources.FromDirectoryAsync(root, WellboreKind, new TestClock()));
        Assert.Contains("leads outside the data definitions", ex.Message, StringComparison.Ordinal);
    }

    private OsduDataDefinitions Definitions(HttpClient http, TimeSpan freshness, TimeProvider? time = null, string? cache = null)
        => new(() => http, OsduDataDefinitions.DefaultApiUrl, OsduDataDefinitions.DefaultWebUrl, cache ?? _cache, freshness, TimeSpan.FromMinutes(1), time ?? new TestClock());

    /// <summary>The repository's GitLab API: the tag list in two pages, and each release's Generated folder as a tar.gz archive.</summary>
    private sealed class Repository
    {
        public bool TagsFail { get; init; }

        public bool HostileArchive { get; init; }

        public bool NextRelease { get; set; }

        public int TagPages { get; private set; }

        public List<string> Archives { get; } = [];

        public HttpClient Client() => new(new Handler(this), disposeHandler: true);

        private HttpResponseMessage Answer(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = request.RequestUri.Query;
            if (path.EndsWith("/repository/tags", StringComparison.Ordinal))
            {
                if (TagsFail)
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("""{ "message": "maintenance" }""") };
                }

                TagPages++;
                // The page number ends the query (per_page=100&page=1); "per_page=100" alone must not read as page 1.
                if (query.EndsWith("&page=1", StringComparison.Ordinal))
                {
                    var next = NextRelease ? $$"""{ "name": "v0.31.0", "commit": { "id": "{{NextCommit}}", "committed_date": "2026-10-01T10:00:00.000+00:00" } },""" : string.Empty;
                    var first = Json($$"""
                        [ {{next}}
                          { "name": "v0.29.1", "commit": { "id": "{{OlderCommit}}", "committed_date": "2026-01-29T15:47:11.000+08:00" } },
                          { "name": "milestone-27", "commit": { "id": "{{OlderCommit}}" } } ]
                        """);
                    first.Headers.Add("X-Next-Page", "2");
                    return first;
                }

                return Json($$"""
                    [ { "name": "v0.30.0", "commit": { "id": "{{ReleaseCommit}}", "committed_date": "2026-07-17T14:55:57.000+08:00" } },
                      { "name": "v0.28.3.1", "commit": { "id": "{{OlderCommit}}" } } ]
                    """);
            }

            if (path.EndsWith("/repository/archive.tar.gz", StringComparison.Ordinal) && query.Contains("path=Generated", StringComparison.Ordinal))
            {
                foreach (var (commit, tree) in new[] { (ReleaseCommit, Tree), (OlderCommit, OlderTree), (NextCommit, Tree) })
                {
                    if (query.Contains("sha=" + commit, StringComparison.Ordinal))
                    {
                        Archives.Add(commit);
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Archive(commit, tree, HostileArchive)) };
                    }
                }
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string json)
            => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        /// <summary>A GitLab archive of the Generated folder: one top folder named for the project, release and commit.</summary>
        private static byte[] Archive(string commit, IReadOnlyDictionary<string, string> tree, bool hostile)
        {
            using var buffer = new MemoryStream();
            using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
            using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
            {
                var top = $"data-definitions-{commit}-Generated/";
                tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, top));
                foreach (var (path, json) in tree)
                {
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, top + "Generated/" + path) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(json)) });
                }

                if (hostile)
                {
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, top + "Generated/../../escaped.json") { DataStream = new MemoryStream("{}"u8.ToArray()) });
                }
            }

            return buffer.ToArray();
        }

        private sealed class Handler(Repository repository) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(repository.Answer(request));
        }
    }
}
