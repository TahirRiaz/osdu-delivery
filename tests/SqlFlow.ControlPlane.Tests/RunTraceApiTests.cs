using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The run trace endpoints end to end against the real catalog: <c>GET /runs/{id}/trace</c> returns the seeded
/// RunEvent rows as one timestamp-ordered feed paged in SQL, the pipeline-anchored form
/// (<c>GET /pipelines/{id}/trace</c>) resolves the pipeline's NEWEST run, and the <c>/trace/text</c> forms render
/// the whole trace as one plain-text document (the Copy-trace and LLM-debugging surface). Unknown ids are 404s.
/// Gated on a reachable catalog database, like the other DB-backed tests.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RunTraceApiTests
{
    [SkippableFact]
    public async Task Trace_OrdersEvents_PagesInSql_ResolvesPipelineLatest_AndRendersText()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.ProvisionAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = FlowIdentity.FromName("cp_trace_" + suffix);
        var flowName = "cp_trace_flow_" + suffix;
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var oldRunId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        // Both runs belong to one group, so the group-scoped list (the one scope that resolves the last action)
        // covers them in a single request.
        var groupId = Guid.NewGuid();
        var t0 = new DateTime(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                // An OLDER run of the same pipeline, with its own trace: the pipeline-anchored endpoint must
                // resolve past it to the newest run.
                db.Runs.Add(SeedRun(oldRunId, pipelineId, repoId, flowName, t0.AddHours(-1), groupId));
                db.RunEvents.Add(new CatalogRunEvent
                {
                    RunId = oldRunId, RepoId = repoId, Ordinal = 1, TimestampUtc = t0.AddHours(-1), Level = "info",
                    Step = "flow", Message = "the previous run",
                });

                db.Runs.Add(SeedRun(runId, pipelineId, repoId, flowName, t0.AddSeconds(30), groupId));
                db.RunEvents.Add(new CatalogRunEvent
                {
                    RunId = runId, RepoId = repoId, Ordinal = 1, TimestampUtc = t0.AddSeconds(1), Level = "info",
                    Step = "plan", Message = "watermark resolved to 2026-07-08",
                });
                db.RunEvents.Add(new CatalogRunEvent
                {
                    RunId = runId, RepoId = repoId, Ordinal = 2, TimestampUtc = t0.AddSeconds(3), Level = "debug",
                    Step = "source.open", Message = "read 'a.parquet' (31 row(s))", Rows = 31, ElapsedMs = 42.5,
                });
                db.RunEvents.Add(new CatalogRunEvent
                {
                    RunId = runId, RepoId = repoId, Ordinal = 3, TimestampUtc = t0.AddSeconds(4), Level = "error",
                    Step = "deliver", Message = "run failed: upstream returned 503",
                });
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueReadTokenAsync(client);

            var trace = await GetJsonAsync<PagedResult<RunTraceEntryDto>>(
                client, token, $"/api/v1/runs/{runId}/trace?pageSize=200");

            // One feed in time order, carrying each event's level, message and measurements.
            Assert.Equal(3, trace.Total);
            Assert.Equal(["plan", "source.open", "deliver"], trace.Items.Select(e => e.Step));

            var planned = trace.Items[0];
            Assert.Equal("info", planned.Level);
            Assert.Equal("watermark resolved to 2026-07-08", planned.Message);

            var fileRead = trace.Items[1];
            Assert.Equal(31, fileRead.Rows);
            Assert.Equal(42.5, fileRead.ElapsedMs);

            var failed = trace.Items[2];
            Assert.Equal("error", failed.Level);
            Assert.Equal("run failed: upstream returned 503", failed.Message);

            // Paging happens in SQL over the ordered feed: page 2 of size 2 holds the third entry alone.
            var page2 = await GetJsonAsync<PagedResult<RunTraceEntryDto>>(
                client, token, $"/api/v1/runs/{runId}/trace?page=2&pageSize=2");
            Assert.Equal(3, page2.Total);
            Assert.Equal(["deliver"], page2.Items.Select(e => e.Step));

            // The pipeline-anchored form resolves the NEWEST run: its entries, not the older run's.
            var latest = await GetJsonAsync<PagedResult<RunTraceEntryDto>>(
                client, token, $"/api/v1/pipelines/{pipelineId}/trace?pageSize=200");
            Assert.Equal(3, latest.Total);
            Assert.All(latest.Items, e => Assert.Equal(runId, e.RunId));

            // The text rendering: one plain-text document with the run header and every event message.
            var text = await GetTextAsync(client, token, $"/api/v1/pipelines/{pipelineId}/trace/text");
            Assert.Contains($"run {runId}", text, StringComparison.Ordinal);
            Assert.Contains($"flow '{flowName}' (test) status succeeded", text, StringComparison.Ordinal);
            Assert.Contains("watermark resolved to 2026-07-08", text, StringComparison.Ordinal);
            Assert.Contains("read 'a.parquet' (31 row(s))", text, StringComparison.Ordinal);
            Assert.Contains("run failed: upstream returned 503", text, StringComparison.Ordinal);
            Assert.DoesNotContain("the previous run", text, StringComparison.Ordinal);

            var runText = await GetTextAsync(client, token, $"/api/v1/runs/{runId}/trace/text");
            Assert.Equal(text, runText); // both forms render the same run through the same path

            // A run group's member list carries each member's newest trace event as its "last action" (the batch
            // overview's live column): the newest event for the new run, the older run's own event for the older
            // run. Both runs were seeded into the same group so one request covers both.
            var members = await GetJsonAsync<PagedResult<RunSummaryDto>>(
                client, token, $"/api/v1/runs?groupId={groupId}&pageSize=50");
            var newest = members.Items.Single(r => r.RunId == runId);
            Assert.Equal("run failed: upstream returned 503", newest.LastAction);
            Assert.NotNull(newest.LastActionUtc);
            var older = members.Items.Single(r => r.RunId == oldRunId);
            Assert.Equal("the previous run", older.LastAction);

            // Every other list scope leaves it null: resolving it costs a per-row lookup into the event log and
            // only a group's member table renders it, so the run board and the pipeline history do not pay for it.
            var listed = await GetJsonAsync<PagedResult<RunSummaryDto>>(
                client, token, $"/api/v1/runs?pipelineId={pipelineId}&pageSize=50");
            Assert.All(listed.Items, r => Assert.Null(r.LastAction));
            Assert.All(listed.Items, r => Assert.Null(r.LastActionUtc));

            using (var missingRun = await SendAsync(client, HttpMethod.Get, $"/api/v1/runs/{Guid.NewGuid()}/trace", token))
            {
                Assert.Equal(HttpStatusCode.NotFound, missingRun.StatusCode);
            }

            using (var missingPipeline = await SendAsync(
                client, HttpMethod.Get, $"/api/v1/pipelines/{Guid.NewGuid()}/trace/text", token))
            {
                Assert.Equal(HttpStatusCode.NotFound, missingPipeline.StatusCode);
            }
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.RunEvents.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
        }
    }

    private static CatalogRun SeedRun(
        Guid runId, Guid pipelineId, Guid repoId, string flowName, DateTime writtenUtc, Guid groupId)
        => new()
        {
            RunId = runId,
            PipelineId = pipelineId,
            RepoId = repoId,
            FlowName = flowName,
            FlowKind = "test",
            Status = RunStatuses.Succeeded,
            Success = true,
            WrittenUtc = writtenUtc,
            GroupId = groupId,
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
        using var response = await SendAsync(client, HttpMethod.Get, relativeUri, token);
        if (!response.IsSuccessStatusCode)
        {
            // Surface the ProblemDetails body: a bare status code is undebuggable from the test log.
            Assert.Fail($"GET {relativeUri} -> {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }

        var value = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(value);
        return value;
    }

    private static async Task<string> GetTextAsync(HttpClient client, string token, string relativeUri)
    {
        using var response = await SendAsync(client, HttpMethod.Get, relativeUri, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadAsStringAsync();
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string relativeUri, string token)
    {
        var request = new HttpRequestMessage(method, new Uri(relativeUri, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }
}
