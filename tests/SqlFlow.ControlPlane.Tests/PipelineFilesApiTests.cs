using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The pipeline-files endpoint over a seeded catalog: the files a pipeline has processed across its runs, one row
/// per distinct file (a file re-pulled by several runs is deduplicated), newest-modified first, searchable, and
/// each flagged <c>lastRun</c> when it was processed by the pipeline's most recent file-bearing run.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PipelineFilesApiTests
{
    [SkippableFact]
    public async Task Files_OverSeededRuns_Deduplicate_FlagLastRun_Sort_AndSearch()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = FlowIdentity.FromName("cp_files_" + suffix);
        var pipelineId = CatalogIdentity.Pipeline(repoId, "cp_land_" + suffix);
        var olderRun = Guid.NewGuid();
        var newerRun = Guid.NewGuid();

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Pipelines.Add(new CatalogPipeline
                {
                    Id = pipelineId,
                    RepoId = repoId,
                    Name = "cp_land_" + suffix,
                    Kind = "file",
                    RelativePath = "land.flow.yaml",
                    ContentHash = "hash",
                    Active = true,
                    FirstSeenUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                    LastSeenUtc = new DateTime(2024, 6, 2, 0, 0, 0, DateTimeKind.Utc),
                });
                db.Runs.Add(Run(olderRun, pipelineId, repoId, new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc)));
                db.Runs.Add(Run(newerRun, pipelineId, repoId, new DateTime(2024, 6, 2, 0, 0, 0, DateTimeKind.Utc)));

                // a.csv only in the older run; b.csv in both (re-pulled); c.csv only in the newer run.
                db.RunFiles.Add(File(olderRun, repoId, "a.csv", new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)));
                db.RunFiles.Add(File(olderRun, repoId, "b.csv", new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero)));
                db.RunFiles.Add(File(newerRun, repoId, "b.csv", new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero)));
                db.RunFiles.Add(File(newerRun, repoId, "c.csv", new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero)));
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueReadTokenAsync(client);

            var page = await GetJsonAsync<PagedResult<PipelineFileDto>>(
                client, token, $"/api/v1/pipelines/{pipelineId}/files");

            // Deduplicated: three distinct files (b.csv collapsed from two runs), newest-modified first.
            Assert.Equal(3, page.Total);
            Assert.Equal(["b.csv", "c.csv", "a.csv"], page.Items.Select(f => f.Name));

            // The newest run (newerRun) processed b.csv and c.csv; a.csv (older run only) is not flagged.
            Assert.True(page.Items.Single(f => f.Name == "b.csv").LastRun);
            Assert.True(page.Items.Single(f => f.Name == "c.csv").LastRun);
            Assert.False(page.Items.Single(f => f.Name == "a.csv").LastRun);

            // Search narrows by name.
            var search = await GetJsonAsync<PagedResult<PipelineFileDto>>(
                client, token, $"/api/v1/pipelines/{pipelineId}/files?search=c.csv");
            var only = Assert.Single(search.Items);
            Assert.Equal("c.csv", only.Name);

            // An unknown pipeline is a 404 (distinct from a real pipeline that has processed no files).
            using var missing = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/v1/pipelines/{Guid.NewGuid()}/files", UriKind.Relative));
            missing.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var missingResponse = await client.SendAsync(missing);
            Assert.Equal(System.Net.HttpStatusCode.NotFound, missingResponse.StatusCode);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.RunFiles.Where(f => f.RunId == olderRun || f.RunId == newerRun).ExecuteDeleteAsync();
            await db.Runs.Where(r => r.RunId == olderRun || r.RunId == newerRun).ExecuteDeleteAsync();
            await db.Pipelines.Where(pp => pp.Id == pipelineId).ExecuteDeleteAsync();
        }
    }

    private static CatalogRun Run(Guid runId, Guid pipelineId, Guid repoId, DateTime startUtc)
        => new()
        {
            RunId = runId,
            PipelineId = pipelineId,
            RepoId = repoId,
            FlowName = "land",
            FlowKind = "file",
            Success = true,
            StartUtc = startUtc,
            EndUtc = startUtc,
            WrittenUtc = startUtc,
        };

    private static CatalogRunFile File(Guid runId, Guid repoId, string name, DateTimeOffset modified)
        => new()
        {
            RunId = runId,
            RepoId = repoId,
            Name = name,
            Path = "/data/" + name,
            Modified = modified,
            Rows = 1,
            Columns = 1,
            SizeBytes = 100,
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
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(relativeUri, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(payload);
        return payload;
    }
}
