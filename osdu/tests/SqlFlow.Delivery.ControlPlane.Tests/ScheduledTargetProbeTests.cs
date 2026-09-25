using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlFlow.Catalog;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.ControlPlane.Background;
using SqlFlow.Delivery.ControlPlane.Configuration;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The scheduled target probe (go-live map OPS-2): off unless a deployment asks for it, and once asked, probing every
/// interface of every active delivery flow it covers through the same node operation the operator's "Probe target"
/// queues, recording what came back in the ledger's audit trail and counting it on the delivery meter.
/// </summary>
[Collection(SqlServerSuite.Name)]
public sealed class ScheduledTargetProbeTests
{
    /// <summary>Off by default, on by configuration, and never paced faster than the floor.</summary>
    [Fact]
    public async Task The_schedule_is_off_until_a_deployment_asks_for_it_and_is_never_paced_below_the_floor()
    {
        await using (var off = Host())
        {
            using var client = off.CreateClient();

            // Nothing is hosted, so nothing probes: the switch is the registration itself.
            Assert.DoesNotContain(off.Services.GetServices<IHostedService>(), service => service is ScheduledTargetProbeService);
        }

        await using (var on = Host().WithSetting("Osdu:TargetProbe:Enabled", "true"))
        {
            using var client = on.CreateClient();

            Assert.Contains(on.Services.GetServices<IHostedService>(), service => service is ScheduledTargetProbeService);
            var options = on.Services.GetRequiredService<IOptions<TargetProbeOptions>>().Value;
            Assert.Equal((15, 60, 200), (options.IntervalMinutes, options.SettleSeconds, options.MaxPerPass));
            Assert.Empty(options.PipelineNames());
        }

        await using var tooFast = Host()
            .WithSetting("Osdu:TargetProbe:Enabled", "true")
            .WithSetting("Osdu:TargetProbe:IntervalMinutes", "1");

        var error = Assert.ThrowsAny<Exception>(() => tooFast.CreateClient());

        Assert.Contains("Osdu:TargetProbe:IntervalMinutes must be between 5 and 1440", Flatten(error), StringComparison.Ordinal);
    }

    /// <summary>Every range the options hold, and the flows a deployment narrows the probe to.</summary>
    [Fact]
    public void The_options_name_the_setting_that_is_out_of_range_and_read_the_flows_they_are_narrowed_to()
    {
        Assert.Null(Record.Exception(() => new TargetProbeOptions().Validate()));
        Assert.False(new TargetProbeOptions().Enabled);

        Assert.Contains("IntervalMinutes", Invalid(new TargetProbeOptions { IntervalMinutes = 4 }), StringComparison.Ordinal);
        Assert.Contains("IntervalMinutes", Invalid(new TargetProbeOptions { IntervalMinutes = 1441 }), StringComparison.Ordinal);
        Assert.Contains("SettleSeconds", Invalid(new TargetProbeOptions { SettleSeconds = -1 }), StringComparison.Ordinal);
        Assert.Contains("SettleSeconds", Invalid(new TargetProbeOptions { SettleSeconds = 301 }), StringComparison.Ordinal);
        Assert.Contains("MaxPerPass", Invalid(new TargetProbeOptions { MaxPerPass = 0 }), StringComparison.Ordinal);
        Assert.Contains("MaxPerPass", Invalid(new TargetProbeOptions { MaxPerPass = 1001 }), StringComparison.Ordinal);

        // A list of separators alone would silently probe everything, so it is refused instead.
        Assert.Contains("Pipelines names no flow", Invalid(new TargetProbeOptions { Pipelines = " , ," }), StringComparison.Ordinal);
        Assert.Equal(
            ["recall-welllog-03-header-delivery", "wells-wellbore-03-header-delivery"],
            new TargetProbeOptions { Pipelines = " recall-welllog-03-header-delivery , wells-wellbore-03-header-delivery ,recall-welllog-03-header-delivery" }.PipelineNames());
    }

    /// <summary>
    /// One pass against a real catalog: nothing is probed while the schedule is off, each interface of the source is
    /// probed through <c>delivery-probe</c> once it runs, and the next pass records what each node answered as the
    /// flow's own audit entry and counts it, a refusing target and a probe that could not run included.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Each_interface_is_probed_and_what_came_back_is_recorded_counted_and_redacted()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var flowName = "probe-source-" + suffix;
        var idleName = "probe-idle-" + suffix;
        var repoId = FlowIdentity.FromName("repo/cp-probe-" + suffix);
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var idleId = CatalogIdentity.Pipeline(repoId, idleName);
        var ledgers = new[] { FlowId.Of(flowName + "/wellbores"), FlowId.Of(flowName + "/welllogs"), FlowId.Of(flowName + "/trajectories") };
        var now = DateTime.UtcNow;

        await SeedAsync(cs, repoId, pipelineId, flowName, idleId, idleName, now);

        using var capture = new MetricsCapture();
        var logs = new ConcurrentQueue<string>();
        using var loggers = new LoggerFactory([new CapturingLoggerProvider(logs)]);
        try
        {
            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");
            using var client = factory.CreateClient();

            // The host has been up long enough to have described this repository's interfaces, and has probed nothing:
            // the schedule is off in this host, exactly as a deployment that has not asked for it.
            await WaitForInterfacesAsync(cs, repoId, 3);
            Assert.Empty(await ProbeTasksAsync(cs, flowName));

            var probe = Service(factory, loggers, new TargetProbeOptions
            {
                Enabled = true,
                // Both flows named, so a pass of this test's host is about this test's estate alone. The idle flow is
                // named and inactive, which is what proves an inactive pipeline is passed over.
                Pipelines = $"{flowName},{idleName},probe-absent-{suffix}",
                SettleSeconds = 0,
            });

            await probe.ProbePassAsync(CancellationToken.None);

            // One probe per interface of the source, through the operator's own path: the task names the interface the
            // node acts through, and nothing was queued for the inactive flow.
            var queued = await ProbeTasksAsync(cs, flowName);
            Assert.Equal(3, queued.Count);
            Assert.All(queued, t => Assert.Equal("delivery-probe", t.Operation));
            Assert.All(queued, t => Assert.Equal(ScheduledTargetProbeService.ScheduleActor, t.RequestedBy));
            Assert.Equal(
                ["trajectories", "wellbores", "welllogs"],
                queued.Select(t => InterfaceOf(t.ArgumentsJson)).Order(StringComparer.Ordinal).ToList());
            Assert.Empty(await ProbeTasksAsync(cs, idleName));

            // A name that matches no active delivery pipeline is reported rather than quietly probing nothing: both the
            // flow that is switched off and the one that does not exist are named.
            Assert.Contains(
                logs,
                line => line.Contains("names no active delivery pipeline", StringComparison.Ordinal)
                    && line.Contains(idleName, StringComparison.Ordinal)
                    && line.Contains("probe-absent-" + suffix, StringComparison.Ordinal));

            // Each probe is open in the audit trail under the schedule, one per interface, and none has an outcome yet.
            var open = await ActivitiesAsync(cs, ledgers);
            Assert.Equal(3, open.Count);
            Assert.All(open, a => Assert.Equal((ScheduledTargetProbeService.ActivityKind, ScheduledTargetProbeService.ScheduleActor, "running"), (a.Kind, a.Actor, a.Outcome)));
            Assert.Empty(capture.Of("osdu_delivery.probes", "flow", flowName + "/wellbores"));

            // The nodes answer: one target is there, one refuses, and one probe never ran, its reason carrying a token.
            var byInterface = queued.ToDictionary(t => InterfaceOf(t.ArgumentsJson), StringComparer.Ordinal);
            await SucceedAsync(cs, byInterface["wellbores"].TaskId, """
                {"flow":"probe/wellbores","reachable":true,"status":200,"detail":"the service answered","path":"/api/storage/v2/info"}
                """);
            await SucceedAsync(cs, byInterface["welllogs"].TaskId, """
                {"flow":"probe/welllogs","reachable":false,"status":503,"detail":"the service is unavailable","path":"/api/os-wellbore-ddms/ddms/v3/about"}
                """);
            await FailAsync(cs, byInterface["trajectories"].TaskId, "the token could not be exchanged: Bearer eyJhbGciOiJIUzI1NiJ9.secret-token");

            await probe.ProbePassAsync(CancellationToken.None);

            // What each node answered is now the flow's own audit entry: reachable completes, anything else fails, which
            // is what an operator filters the trail by and what an alert is built on.
            var settled = (await ActivitiesAsync(cs, ledgers))
                .Where(a => a.Outcome != "running")
                .ToDictionary(a => a.FlowName, StringComparer.Ordinal);
            Assert.Equal(3, settled.Count);
            Assert.Equal("completed", settled[flowName + "/wellbores"].Outcome);
            Assert.Equal("reachable: HTTP 200 at /api/storage/v2/info (the service answered)", settled[flowName + "/wellbores"].Summary);
            Assert.Equal("failed", settled[flowName + "/welllogs"].Outcome);
            Assert.Equal(
                "unreachable: HTTP 503 at /api/os-wellbore-ddms/ddms/v3/about (the service is unavailable)",
                settled[flowName + "/welllogs"].Summary);
            Assert.Equal("failed", settled[flowName + "/trajectories"].Outcome);
            Assert.StartsWith("the probe could not run: the token could not be exchanged", settled[flowName + "/trajectories"].Summary, StringComparison.Ordinal);
            Assert.All(settled.Values, a => Assert.NotNull(a.CompletedUtc));

            // A probe is never a way to leak a secret: the token the node reported is redacted before it is stored.
            Assert.DoesNotContain("secret-token", settled[flowName + "/trajectories"].Summary, StringComparison.Ordinal);
            Assert.Contains("Bearer ***", settled[flowName + "/trajectories"].Summary, StringComparison.Ordinal);

            // And the same three outcomes are on the meter, tagged with the flow and the interface they are about.
            Assert.Equal(
                ["reachable"],
                capture.Of("osdu_delivery.probes", "flow", flowName + "/wellbores").Select(m => m.Tags["outcome"]));
            Assert.Equal("wellbores", capture.Of("osdu_delivery.probes", "flow", flowName + "/wellbores").Single().Tags["interface"]);
            Assert.Equal(
                ["unreachable"],
                capture.Of("osdu_delivery.probes", "flow", flowName + "/welllogs").Select(m => m.Tags["outcome"]));
            Assert.Equal(
                ["error"],
                capture.Of("osdu_delivery.probes", "flow", flowName + "/trajectories").Select(m => m.Tags["outcome"]));
            Assert.All(capture.Of("osdu_delivery.probes", "flow", flowName + "/welllogs"), m => Assert.Equal(1, m.Value));

            // The pass that settled them queued the next round, which the ledger carries open for the pass after it: a
            // probe is never lost because the host stopped between queueing it and reading its result.
            Assert.Equal(6, (await ProbeTasksAsync(cs, flowName)).Count);
            Assert.Equal(3, (await ActivitiesAsync(cs, ledgers)).Count(a => a.Outcome == "running"));

            // A probe whose task the queue no longer holds settles as an error rather than being carried for ever.
            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.ComputeTasks.Where(t => t.SourceRef == flowName && t.Status == RunStatuses.Queued).ExecuteDeleteAsync();
            }

            await probe.ProbePassAsync(CancellationToken.None);
            var abandoned = (await ActivitiesAsync(cs, ledgers)).Where(a => a.Summary is not null && a.Summary.Contains("no longer in the queue", StringComparison.Ordinal)).ToList();
            Assert.Equal(3, abandoned.Count);
            Assert.All(abandoned, a => Assert.Equal("failed", a.Outcome));
        }
        finally
        {
            await using (var osdu = SampleEstate.Context(cs))
            {
                await osdu.DeliveryActivities.Where(a => ledgers.Contains(a.FlowId)).ExecuteDeleteAsync();
                await osdu.DeliveryInterfaces.Where(i => i.RepoId == repoId).ExecuteDeleteAsync();
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.ComputeTasks.Where(t => t.SourceRef == flowName || t.SourceRef == idleName).ExecuteDeleteAsync();
                await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
                await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
            }
        }
    }

    /// <summary>The service as the host would hold it, with a logger this test can read and options it sets itself.</summary>
    private static ScheduledTargetProbeService Service(ControlPlaneAppFactory factory, ILoggerFactory loggers, TargetProbeOptions options)
    {
        options.Validate();
        return new ScheduledTargetProbeService(
            factory.Services, TimeProvider.System, Options.Create(options), loggers.CreateLogger<ScheduledTargetProbeService>());
    }

    /// <summary>A source of three interfaces, and an inactive flow beside it that no pass may reach.</summary>
    private static async Task SeedAsync(
        string cs, Guid repoId, Guid pipelineId, string flowName, Guid idleId, string idleName, DateTime now)
    {
        var yaml = $$"""
            flowType: delivery
            name: {{flowName}}
            source:
              connection: ${env:OSDU_SAMPLE_DB}
              work: ../.work/probe
            render:
              parameters:
                dataPartition: dev
            target:
              endpoint: ${env:OSDU_URL}
              headers: { data-partition-id: dev }
              protocolOptions: { ddmsRoot: /api/os-wellbore-ddms }
            interfaces:
              wellbores:
                record: { object: OsduSample.ing.Wellbore, key: [facility_name] }
                mapping: Wellbore@1.0.0
              welllogs:
                record: { object: OsduSample.ing.WellLog, key: [source_project, log_id] }
                bulk: { root: ../data/curves, locationColumn: curve_folder, hashColumn: payload_hash }
                mapping: WellLog@1.4.0
              trajectories:
                record: { object: OsduSample.ing.WellboreTrajectory, key: [source_project, trajectory_id] }
                mapping: WellboreTrajectory@1.0.0
            """;
        var idleYaml = $$"""
            flowType: delivery
            name: {{idleName}}
            source:
              connection: ${env:OSDU_SAMPLE_DB}
              work: ../.work/probe-idle
              record: { object: OsduSample.ing.Wellbore, key: [facility_name] }
            render:
              mapping: Wellbore@1.0.0
              parameters:
                dataPartition: dev
            target:
              endpoint: ${env:OSDU_URL}
              headers: { data-partition-id: dev }
            """;

        await using var db = CatalogDatabase.Create(cs);
        db.Repos.Add(new CatalogRepo
        {
            Id = repoId, Name = "cp-probe", RemoteUrl = "https://example/cp-probe.git",
            RootPath = Path.GetTempPath(), FirstSeenUtc = now, LastSyncUtc = now,
        });
        db.Pipelines.Add(Pipeline(pipelineId, repoId, flowName, yaml, active: true, now));
        db.Pipelines.Add(Pipeline(idleId, repoId, idleName, idleYaml, active: false, now));
        await db.SaveChangesAsync();
    }

    private static CatalogPipeline Pipeline(Guid id, Guid repoId, string name, string yaml, bool active, DateTime now) => new()
    {
        Id = id,
        RepoId = repoId,
        Name = name,
        Kind = "delivery",
        RelativePath = "flows/" + name + ".yaml",
        ContentHash = new string('0', 64),
        Yaml = yaml,
        DefinitionJson = string.Create(CultureInfo.InvariantCulture, $$"""{"name":"{{name}}","flowKind":"delivery"}"""),
        Active = active,
        Wave = 0,
        FirstSeenUtc = now,
        LastSeenUtc = now,
    };

    /// <summary>The probe tasks queued for one flow, newest last.</summary>
    private static async Task<List<ComputeTaskRow>> ProbeTasksAsync(string cs, string flowName)
    {
        await using var db = CatalogDatabase.Create(cs);
        var rows = await db.ComputeTasks.AsNoTracking()
            .Where(t => t.SourceRef == flowName)
            .OrderBy(t => t.EnqueuedUtc)
            .Select(t => new { t.TaskId, t.Operation, t.ArgumentsJson, t.RequestedBy })
            .ToListAsync();
        return rows.Select(r => new ComputeTaskRow(r.TaskId, r.Operation, r.ArgumentsJson, r.RequestedBy)).ToList();
    }

    private static async Task<List<DeliveryActivity>> ActivitiesAsync(string cs, IReadOnlyList<Guid> flowIds)
    {
        await using var osdu = SampleEstate.Context(cs);
        return await osdu.DeliveryActivities.AsNoTracking()
            .Where(a => flowIds.Contains(a.FlowId))
            .OrderBy(a => a.ActivityId)
            .ToListAsync();
    }

    /// <summary>A node taking the task and recording what the target answered, exactly as the queue journals it.</summary>
    private static async Task SucceedAsync(string cs, Guid taskId, string resultJson)
    {
        await using var db = CatalogDatabase.Create(cs);
        Assert.NotNull(await ComputeTaskStore.MarkHandedOutAsync(db, taskId, "probe-test-node", DateTime.UtcNow));
        Assert.True(await ComputeTaskStore.CompleteAsync(db, taskId, resultJson, DateTime.UtcNow, "probe-test-node"));
    }

    /// <summary>A node taking the task and failing it, as a probe that could not run at all is recorded.</summary>
    private static async Task FailAsync(string cs, Guid taskId, string error)
    {
        await using var db = CatalogDatabase.Create(cs);
        Assert.NotNull(await ComputeTaskStore.MarkHandedOutAsync(db, taskId, "probe-test-node", DateTime.UtcNow));
        Assert.True(await ComputeTaskStore.FailAsync(db, taskId, error, DateTime.UtcNow, "probe-test-node"));
    }

    private static string InterfaceOf(string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        return document.RootElement.GetProperty("arguments").GetProperty("interface").GetString()!;
    }

    private static async Task WaitForInterfacesAsync(string cs, Guid repoId, int expected)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            await using var osdu = SampleEstate.Context(cs);
            if (await osdu.DeliveryInterfaces.CountAsync(i => i.RepoId == repoId) >= expected)
            {
                return;
            }

            await Task.Delay(250);
        }

        Assert.Fail($"The control plane did not describe the {expected} interface(s) of repository {repoId:D} within 30 seconds of starting.");
    }

    private static string Invalid(TargetProbeOptions options)
        => Assert.Throws<InvalidOperationException>(options.Validate).Message;

    /// <summary>Every message of an exception chain, so an assertion can name the setting the host refused.</summary>
    private static string Flatten(Exception error)
    {
        var messages = new List<string>();
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
            if (current is AggregateException aggregate)
            {
                messages.AddRange(aggregate.InnerExceptions.Select(Flatten));
            }
        }

        return string.Join(" -> ", messages);
    }

    /// <summary>The control plane with the module, and without the settings a test host must not act on.</summary>
    private static ControlPlaneAppFactory Host()
        => new ControlPlaneAppFactory()
            .WithModules(new DeliveryControlPlaneModule())
            .WithSetting("ControlPlane:Worker:Enabled", "false")
            .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");

    private sealed record ComputeTaskRow(Guid TaskId, string Operation, string ArgumentsJson, string? RequestedBy);
}
