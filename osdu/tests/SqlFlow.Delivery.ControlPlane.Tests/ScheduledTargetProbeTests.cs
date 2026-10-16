using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlFlow.Catalog;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.ControlPlane.Background;
using SqlFlow.Delivery.ControlPlane.Configuration;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The scheduled target probe (go-live map OPS-2): off unless a deployment asks for it, and once asked, probing every
/// interface of every active delivery flow it covers through the same probe the operator's "Probe target" runs, in the
/// control plane, recording what came back in the ledger's audit trail and counting it on the delivery meter.
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
            Assert.Equal((15, 200), (options.IntervalMinutes, options.MaxPerPass));
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
        Assert.Contains("MaxPerPass", Invalid(new TargetProbeOptions { MaxPerPass = 0 }), StringComparison.Ordinal);
        Assert.Contains("MaxPerPass", Invalid(new TargetProbeOptions { MaxPerPass = 1001 }), StringComparison.Ordinal);

        // A list of separators alone would silently probe everything, so it is refused instead.
        Assert.Contains("Pipelines names no flow", Invalid(new TargetProbeOptions { Pipelines = " , ," }), StringComparison.Ordinal);
        Assert.Equal(
            ["welldb-welllog-03-header-delivery", "wells-wellbore-03-header-delivery"],
            new TargetProbeOptions { Pipelines = " welldb-welllog-03-header-delivery , wells-wellbore-03-header-delivery ,welldb-welllog-03-header-delivery" }.PipelineNames());
    }

    /// <summary>
    /// One pass against a real catalog and a stand-in OSDU: nothing is probed while the schedule is off; once it runs, each
    /// interface of an active flow is probed in the control plane, with the endpoint its reference resolves to from the
    /// repository's central configuration, exactly as a run of it resolves it, and nothing is queued for a node. What each
    /// probe found is the flow's own audit entry, counted and redacted, a refusing target, a target that never answered and
    /// a probe that could not run at all included; and a probe an earlier pass left open is closed as unfinished.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Each_interface_is_probed_in_the_control_plane_and_what_it_found_is_recorded_counted_and_redacted()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var flowName = "probe-source-" + suffix;
        var idleName = "probe-idle-" + suffix;
        var brokenName = "probe-broken-" + suffix;
        var repoId = FlowIdentity.FromName("repo/cp-probe-" + suffix);
        var root = Path.Combine(Path.GetTempPath(), "cp-probe-" + suffix);
        var urlName = "PROBE_OSDU_URL_" + suffix.ToUpperInvariant();
        var unsetName = "PROBE_UNSET_" + suffix.ToUpperInvariant();
        var ledgers = new[] { FlowId.Of(flowName + "/wellbores"), FlowId.Of(flowName + "/welllogs"), FlowId.Of(flowName + "/trajectories"), FlowId.Of(brokenName) };
        var now = DateTime.UtcNow;

        await SeedAsync(cs, repoId, root, flowName, idleName, brokenName, urlName, unsetName, now);
        var store = new DeliveryConfigStore(() => SampleEstate.Context(cs));
        await using (var catalog = CatalogDatabase.Create(cs))
        {
            await store.SetAsync(repoId, null, urlName, "https://osdu.example.com", null, "tests", now, catalog);
        }

        // A probe queued for a node before probes ran in the control plane, which no pass will ever hear back from.
        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        await ledger.RegisterAsync(ledgers[0], "dev", flowName + "/wellbores");
        var leftover = await ledger.StartActivityAsync(new ActivityRecord
        {
            FlowId = ledgers[0],
            FlowName = flowName + "/wellbores",
            Kind = ScheduledTargetProbeService.ActivityKind,
            Actor = ScheduledTargetProbeService.ScheduleActor,
            StartedUtc = now.AddHours(-1),
            ParametersJson = """{"interface":"wellbores"}""",
        });

        var osdu = new StandInOsdu();
        using var capture = new MetricsCapture();
        var logs = new ConcurrentQueue<string>();
        using var loggers = new LoggerFactory([new CapturingLoggerProvider(logs)]);
        try
        {
            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false")
                .WithServices(services => services.AddSingleton(new TargetClients(TimeProvider.System, osdu, allowLoopback: false)));
            using var client = factory.CreateClient();

            // The host has been up long enough to have described this repository's interfaces, and has probed nothing:
            // the schedule is off in this host, exactly as a deployment that has not asked for it.
            await WaitForInterfacesAsync(cs, repoId, 3);
            Assert.Empty(osdu.Asked);

            var probe = Service(factory, loggers, new TargetProbeOptions
            {
                Enabled = true,
                // The flows named, so a pass of this test's host is about this test's estate alone. The idle flow is named
                // and inactive, which is what proves an inactive pipeline is passed over.
                Pipelines = $"{flowName},{idleName},{brokenName},probe-absent-{suffix}",
            });

            await probe.ProbePassAsync(CancellationToken.None);

            // The probe left open is closed first, as one that did not finish.
            var closed = (await ActivitiesAsync(cs, ledgers)).Single(a => a.ActivityId == leftover.ActivityId);
            Assert.Equal("failed", closed.Outcome);
            Assert.StartsWith("the probe did not finish", closed.Summary, StringComparison.Ordinal);

            // Each probe asked the platform itself, at the endpoint the central configuration gives the flow's reference,
            // and nothing was queued for a node, for either flow.
            Assert.All(osdu.Asked, url => Assert.Equal("osdu.example.com", url.Host));
            Assert.Contains(osdu.Asked, url => url.AbsolutePath == "/api/storage/v2/info");
            Assert.Contains(osdu.Asked, url => url.AbsolutePath.StartsWith("/api/os-wellbore-ddms/", StringComparison.Ordinal));
            Assert.Contains(osdu.Asked, url => url.AbsolutePath == "/api/search/v2/info");
            await using (var db = CatalogDatabase.Create(cs))
            {
                Assert.Empty(await db.ComputeTasks.AsNoTracking().Where(t => t.SourceRef == flowName || t.SourceRef == idleName || t.SourceRef == brokenName).ToListAsync());
            }

            // A name that matches no active delivery pipeline is reported rather than quietly probing nothing: both the
            // flow that is switched off and the one that does not exist are named.
            Assert.Contains(
                logs,
                line => line.Contains("names no active delivery pipeline", StringComparison.Ordinal)
                    && line.Contains(idleName, StringComparison.Ordinal)
                    && line.Contains("probe-absent-" + suffix, StringComparison.Ordinal));

            // What each probe found is the flow's own audit entry under the schedule, closed in the pass that ran it:
            // reachable completes, anything else fails, which is what an operator filters the trail by and what an alert is
            // built on.
            var settled = (await ActivitiesAsync(cs, ledgers))
                .Where(a => a.ActivityId != leftover.ActivityId)
                .ToDictionary(a => a.FlowName, StringComparer.Ordinal);
            Assert.Equal(4, settled.Count);
            Assert.All(settled.Values, a => Assert.Equal((ScheduledTargetProbeService.ActivityKind, ScheduledTargetProbeService.ScheduleActor), (a.Kind, a.Actor)));
            Assert.All(settled.Values, a => Assert.NotNull(a.CompletedUtc));
            Assert.Equal("completed", settled[flowName + "/wellbores"].Outcome);
            Assert.Equal("reachable: HTTP 200 at /api/storage/v2/info (the service answered)", settled[flowName + "/wellbores"].Summary);
            Assert.Equal("failed", settled[flowName + "/welllogs"].Outcome);
            Assert.StartsWith("unreachable: HTTP 401 at /api/os-wellbore-ddms/", settled[flowName + "/welllogs"].Summary, StringComparison.Ordinal);
            Assert.Equal("failed", settled[flowName + "/trajectories"].Outcome);
            Assert.StartsWith("unreachable: no answer at /api/search/v2/info", settled[flowName + "/trajectories"].Summary, StringComparison.Ordinal);

            // A probe that could not run (a reference nothing resolves) says why, naming the reference and not a value.
            Assert.Equal("failed", settled[brokenName].Outcome);
            Assert.StartsWith("the probe could not run:", settled[brokenName].Summary, StringComparison.Ordinal);
            Assert.Contains(unsetName, settled[brokenName].Summary, StringComparison.Ordinal);

            // A probe is never a way to leak a secret: the token the transport's failure carried is redacted before it is stored.
            Assert.DoesNotContain("secret-token", settled[flowName + "/trajectories"].Summary, StringComparison.Ordinal);
            Assert.Contains("Bearer ***", settled[flowName + "/trajectories"].Summary, StringComparison.Ordinal);

            // And the same outcomes are on the meter, the unfinished probe's first, tagged with the flow, the interface and
            // the partition the interface's ledger is kept under (the one its header names).
            var wellbores = capture.Of("osdu_delivery.probes", "flow", flowName + "/wellbores");
            Assert.Equal(["error", "reachable"], wellbores.Select(m => m.Tags["outcome"]));
            Assert.All(wellbores, m => Assert.Equal(("wellbores", "dev"), (m.Tags["interface"], m.Tags["partition"])));
            Assert.Equal(["unreachable"], capture.Of("osdu_delivery.probes", "flow", flowName + "/welllogs").Select(m => m.Tags["outcome"]));
            Assert.Equal(["unreachable"], capture.Of("osdu_delivery.probes", "flow", flowName + "/trajectories").Select(m => m.Tags["outcome"]));
            Assert.Equal(["error"], capture.Of("osdu_delivery.probes", "flow", brokenName).Select(m => m.Tags["outcome"]));
            Assert.All(capture.Of("osdu_delivery.probes", "flow", flowName + "/welllogs"), m => Assert.Equal(1, m.Value));

            // Nothing is left open for the next pass, which probes each interface again and records each once more.
            Assert.DoesNotContain(await ActivitiesAsync(cs, ledgers), a => a.Outcome == "running");
            await probe.ProbePassAsync(CancellationToken.None);
            var again = await ActivitiesAsync(cs, ledgers);
            Assert.Equal(9, again.Count);
            Assert.DoesNotContain(again, a => a.Outcome == "running");
        }
        finally
        {
            await using (var context = SampleEstate.Context(cs))
            {
                await context.DeliveryActivities.Where(a => ledgers.Contains(a.FlowId)).ExecuteDeleteAsync();
                await context.DeliveryLedgers.Where(l => ledgers.Contains(l.FlowId)).ExecuteDeleteAsync();
                await context.DeliveryInterfaces.Where(i => i.RepoId == repoId).ExecuteDeleteAsync();
                await context.DeliveryConfigProperties.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.ComputeTasks.Where(t => t.SourceRef == flowName || t.SourceRef == idleName || t.SourceRef == brokenName).ExecuteDeleteAsync();
                await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
                await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
            }

            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>The service as the host would hold it, with a logger this test can read and options it sets itself.</summary>
    private static ScheduledTargetProbeService Service(ControlPlaneAppFactory factory, ILoggerFactory loggers, TargetProbeOptions options)
    {
        options.Validate();
        return new ScheduledTargetProbeService(
            factory.Services, TimeProvider.System, Options.Create(options), loggers.CreateLogger<ScheduledTargetProbeService>());
    }

    /// <summary>
    /// A source of three interfaces whose endpoint is a reference the central configuration resolves, an inactive flow beside
    /// it that no pass may reach, and a flow whose endpoint names a reference nothing resolves. Each flow file is written
    /// where the catalog says the repository is, since a probe reads the flow as a run does.
    /// </summary>
    private static async Task SeedAsync(
        string cs, Guid repoId, string root, string flowName, string idleName, string brokenName, string urlName, string unsetName, DateTime now)
    {
        var yaml = $$"""
            flowType: delivery
            name: {{flowName}}
            source:
              connection: ${env:OSDU_DATA_DB}
              work: ../.work/probe
            render:
              parameters:
                dataPartition: dev
            target:
              endpoint: ${env:{{urlName}}}
              headers: { data-partition-id: dev }
              protocolOptions: { ddmsRoot: /api/os-wellbore-ddms }
            interfaces:
              wellbores:
                record: { object: OsduData.silver.Wellbore, key: [facility_name] }
                mapping: Wellbore@1.0.0
              welllogs:
                record: { object: OsduData.silver.WellLog, key: [source_project, log_id] }
                bulk: { root: ../data/curves, locationColumn: curve_folder, hashColumn: payload_hash }
                mapping: WellLog@1.4.0
              trajectories:
                record: { object: OsduData.silver.WellboreTrajectory, key: [source_project, trajectory_id] }
                mapping: WellboreTrajectory@1.0.0
                protocolOptions: { probePath: /api/search/v2/info }
            """;
        var idleYaml = $$"""
            flowType: delivery
            name: {{idleName}}
            source:
              connection: ${env:OSDU_DATA_DB}
              work: ../.work/probe-idle
              record: { object: OsduData.silver.Wellbore, key: [facility_name] }
            render:
              mapping: Wellbore@1.0.0
              parameters:
                dataPartition: dev
            target:
              protocol: storage
              endpoint: ${env:{{urlName}}}
              headers: { data-partition-id: dev }
            """;
        var brokenYaml = $$"""
            flowType: delivery
            name: {{brokenName}}
            source:
              connection: ${env:OSDU_DATA_DB}
              work: ../.work/probe-broken
              record: { object: OsduData.silver.Wellbore, key: [facility_name] }
            render:
              mapping: Wellbore@1.0.0
              parameters:
                dataPartition: dev
            target:
              protocol: storage
              endpoint: ${env:{{unsetName}}}
              headers: { data-partition-id: dev }
            """;

        var flows = Directory.CreateDirectory(Path.Combine(root, "flows")).FullName;
        await using var db = CatalogDatabase.Create(cs);
        db.Repos.Add(new CatalogRepo
        {
            Id = repoId, Name = "cp-probe", RemoteUrl = "https://example/cp-probe.git",
            RootPath = root, FirstSeenUtc = now, LastSyncUtc = now,
        });
        foreach (var (name, text, active) in new[] { (flowName, yaml, true), (idleName, idleYaml, false), (brokenName, brokenYaml, true) })
        {
            await File.WriteAllTextAsync(Path.Combine(flows, name + ".yaml"), text);
            db.Pipelines.Add(Pipeline(CatalogIdentity.Pipeline(repoId, name), repoId, name, text, active, now));
        }

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

    private static async Task<List<DeliveryActivity>> ActivitiesAsync(string cs, IReadOnlyList<Guid> flowIds)
    {
        await using var osdu = SampleEstate.Context(cs);
        return await osdu.DeliveryActivities.AsNoTracking()
            .Where(a => flowIds.Contains(a.FlowId))
            .OrderBy(a => a.ActivityId)
            .ToListAsync();
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

    /// <summary>
    /// The platform the probes reach: the storage service answers, the Wellbore DDMS refuses the flow's credentials, and the
    /// search service never answers, its failure carrying a token as a careless proxy's would. Every URL asked is kept.
    /// </summary>
    private sealed class StandInOsdu : HttpMessageHandler
    {
        public ConcurrentQueue<Uri> Asked { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!;
            Asked.Enqueue(url);
            var path = url.AbsolutePath;
            if (path.StartsWith("/api/os-wellbore-ddms/", StringComparison.Ordinal))
            {
                return Answer(HttpStatusCode.Unauthorized, """{"code":401,"reason":"Unauthorized","message":"The token was refused."}""");
            }

            if (path == "/api/search/v2/info")
            {
                throw new HttpRequestException("The connection was reset while sending Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.secret-token");
            }

            return path.EndsWith("/info", StringComparison.Ordinal)
                ? Answer(HttpStatusCode.OK, """{"groupId":"org.opengroup.osdu","version":"0.0.0"}""")
                : Answer(HttpStatusCode.NotFound, """{"code":404,"reason":"Not Found","message":"No such path."}""");
        }

        private static Task<HttpResponseMessage> Answer(HttpStatusCode status, string json)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
