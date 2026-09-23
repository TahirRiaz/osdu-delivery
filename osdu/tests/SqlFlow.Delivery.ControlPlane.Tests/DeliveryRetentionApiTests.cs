using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.ControlPlane.Api;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The ledger's retention pass (<c>POST /api/v1/delivery/ledger/prune</c>), which is the one way an operator ages the
/// <c>osdu</c> schema's history out. Traceability is the product, so what it may take is exactly bounded: delivery tries
/// older than the cut-off go only where a later try of the same record explains the record's outcome, and an activity's
/// captured run log is cleared without the audit row it belongs to ever being deleted. These tests hold the endpoint to
/// that bound, because a retention pass that took one row too many would quietly cost a delivered record its history.
/// </summary>
public sealed class DeliveryRetentionApiTests
{
    private const string FlowName = "wells-retention";

    [Fact]
    public async Task Pruning_needs_the_admin_scope_and_a_cut_off_of_at_least_a_day()
    {
        await using var factory = Factory(catalog: null);
        using var client = factory.CreateClient();

        // Operating the estate is not administering it: the retention pass deletes history, so it sits behind admin.
        var operate = await TokenAsync(client, ["read", "operate"]);
        using var refused = await PruneAsync(client, operate, 90);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        // And an anonymous caller never reaches it at all.
        using var anonymous = await client.PostAsJsonAsync(
            new Uri("/api/v1/delivery/ledger/prune", UriKind.Relative), new DeliveryPruneRequest(90));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task A_cut_off_below_a_day_is_refused_before_anything_is_deleted()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        await using var factory = Factory(cs);
        using var client = factory.CreateClient();
        var admin = await TokenAsync(client, ["admin"]);

        foreach (var days in new[] { 0, -1 })
        {
            using var response = await PruneAsync(client, admin, days);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("olderThanDays must be at least 1", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// One pass over a seeded history: what it takes, what it keeps, and what it reports. The record's last try and every
    /// try inside the window survive; every audit row survives with its actor, parameters and outcome, and only the logs
    /// of the settled activities outside the window are gone.
    /// </summary>
    [Fact]
    public async Task The_retention_pass_ages_out_old_tries_and_run_logs_and_keeps_what_a_record_is_reconstructible_from()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var flowId = FlowId.Of(FlowName + "-" + Guid.NewGuid().ToString("N"));
        var key = Guid.NewGuid();
        var lonely = Guid.NewGuid();
        // Whole seconds, so what is read back out of datetime2 is exactly what was written and the assertions are about
        // retention rather than about clock precision.
        var now = new DateTime(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        var ancient = now.AddDays(-400);
        var old = now.AddDays(-200);
        var recent = now.AddDays(-2);
        var log = new string('x', 4_000);

        try
        {
            await using (var osdu = SampleEstate.Context(cs))
            {
                osdu.DeliveryAttempts.AddRange(
                    Attempt(flowId, key, ancient, AttemptOutcomes.Failed),
                    Attempt(flowId, key, old, AttemptOutcomes.Retry),
                    Attempt(flowId, key, recent, AttemptOutcomes.Delivered),
                    // A record whose only try is older than the cut-off: its last outcome has to stay explainable, so
                    // the pass leaves it, however old it is.
                    Attempt(flowId, lonely, ancient, AttemptOutcomes.Delivered));
                osdu.DeliveryActivities.AddRange(
                    Activity(flowId, "deliver", old, completed: true, log),
                    Activity(flowId, "delete", old, completed: true, log),
                    // Outside the window but never finished: a run still going keeps everything it holds.
                    Activity(flowId, "deliver", old, completed: false, log),
                    // Inside the window: the operator asked for the last 30 days to stay readable.
                    Activity(flowId, "deliver", recent, completed: true, log));
                await osdu.SaveChangesAsync();
            }

            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var admin = await TokenAsync(client, ["admin"]);

            using var response = await PruneAsync(client, admin, 30);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<DeliveryPruneResult>();
            Assert.NotNull(result);

            await using (var osdu = SampleEstate.Context(cs))
            {
                // Both tries outside the window went, because a later try of that record explains its outcome; the
                // record's own last try stayed, and so did the only try of the record that has one, however old it is.
                var attempts = await osdu.DeliveryAttempts.AsNoTracking()
                    .Where(a => a.FlowId == flowId)
                    .OrderBy(a => a.StartedUtc)
                    .ToListAsync();
                Assert.Equal([recent], attempts.Where(a => a.DeliveryKey == key).Select(a => a.StartedUtc).ToList());
                var kept = Assert.Single(attempts, a => a.DeliveryKey == lonely);
                Assert.Equal(ancient, kept.StartedUtc);

                // Every audit row is still there: the pass deletes none of them.
                var activities = await osdu.DeliveryActivities.AsNoTracking()
                    .Where(a => a.FlowId == flowId)
                    .ToListAsync();
                Assert.Equal(4, activities.Count);

                // The settled activities outside the window lost their log and nothing else.
                var settledOutside = activities.Where(a => a.StartedUtc == old && a.CompletedUtc != null).ToList();
                Assert.Equal(2, settledOutside.Count);
                Assert.All(settledOutside, a =>
                {
                    Assert.Null(a.Log);
                    Assert.Equal("retention tests", a.Actor);
                    Assert.Equal("""{"force":false}""", a.ParametersJson);
                    Assert.Equal("completed", a.Outcome);
                    Assert.Equal("what the run did", a.Summary);
                });

                // The one still running and the one inside the window kept theirs.
                var keptLogs = activities.Where(a => a.CompletedUtc is null || a.StartedUtc == recent).ToList();
                Assert.Equal(2, keptLogs.Count);
                Assert.All(keptLogs, a => Assert.Equal(log, a.Log));
            }

            // The counts the operator sees are what actually moved: two tries deleted, two logs cleared. The prune runs
            // against a shared database, so other suites' rows may be aged out by the same pass; these are the floor.
            Assert.True(result.AttemptsPruned >= 2, $"attemptsPruned was {result.AttemptsPruned}");
            Assert.True(result.ActivityLogsCleared >= 2, $"activityLogsCleared was {result.ActivityLogsCleared}");

            // And it is idempotent: a second pass at the same cut-off finds nothing of this flow left to take.
            using var again = await PruneAsync(client, admin, 30);
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            await using (var osdu = SampleEstate.Context(cs))
            {
                Assert.Equal(2, await osdu.DeliveryAttempts.CountAsync(a => a.FlowId == flowId));
                Assert.Equal(4, await osdu.DeliveryActivities.CountAsync(a => a.FlowId == flowId));
                Assert.Equal(2, await osdu.DeliveryActivities.CountAsync(a => a.FlowId == flowId && a.Log != null));
            }
        }
        finally
        {
            await using var osdu = SampleEstate.Context(cs);
            await osdu.DeliveryAttempts.Where(a => a.FlowId == flowId).ExecuteDeleteAsync();
            await osdu.DeliveryActivities.Where(a => a.FlowId == flowId).ExecuteDeleteAsync();
        }
    }

    /// <summary>The outcomes a try settles as, as the ledger writes them.</summary>
    private static class AttemptOutcomes
    {
        public const string Delivered = "delivered";

        public const string Retry = "retry";

        public const string Failed = "failed";
    }

    private static DeliveryAttempt Attempt(Guid flowId, Guid key, DateTime startedUtc, string outcome) => new()
    {
        FlowId = flowId,
        DeliveryKey = key,
        Worker = "retention-tests",
        StartedUtc = startedUtc,
        CompletedUtc = startedUtc.AddSeconds(2),
        Outcome = outcome,
        Phase = "metadata",
    };

    private static DeliveryActivity Activity(Guid flowId, string kind, DateTime startedUtc, bool completed, string log) => new()
    {
        FlowId = flowId,
        FlowName = FlowName,
        Kind = kind,
        Actor = "retention tests",
        StartedUtc = startedUtc,
        CompletedUtc = completed ? startedUtc.AddMinutes(1) : null,
        Outcome = completed ? "completed" : "running",
        ParametersJson = """{"force":false}""",
        Summary = completed ? "what the run did" : null,
        Log = log,
    };

    private static ControlPlaneAppFactory Factory(string? catalog)
    {
        var factory = new ControlPlaneAppFactory();
        if (catalog is not null)
        {
            factory = factory.WithCatalog(catalog);
        }

        return factory
            .WithModules(new DeliveryControlPlaneModule())
            .WithSetting("ControlPlane:Worker:Enabled", "false")
            .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");
    }

    private static async Task<string> TokenAsync(HttpClient client, IReadOnlyList<string> scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, scopes));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    private static async Task<HttpResponseMessage> PruneAsync(HttpClient client, string token, int olderThanDays)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/delivery/ledger/prune", UriKind.Relative))
        {
            Content = JsonContent.Create(new DeliveryPruneRequest(olderThanDays)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }
}
