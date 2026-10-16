using System.Net;
using System.Text;
using System.Text.Json;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.Delivery.ControlPlane.ManagedWorkers;
using SqlFlow.Dispatch.ManagedWorkers;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The <c>radix</c> launcher against a fake Radix job scheduler: what a start sends (the job id, the node's variables,
/// the time limit, no restarts, no eviction, the pool's resources), how the scheduler's job statuses read, that a stop
/// goes to Radix's own job name, and that every failure becomes a bounded, redacted message saying whether the job may
/// have been created anyway.
/// </summary>
public sealed class RadixWorkerLauncherTests
{
    private static readonly Uri Scheduler = new("http://worker:8080/");

    [Fact]
    public async Task AStart_PostsOneJob_NamedForTheWorker_WithTheNodesVariables()
    {
        var scheduler = new FakeScheduler();
        scheduler.Respond(HttpMethod.Post, "/api/v1/jobs", HttpStatusCode.OK, """{"name":"batch-abc-job-1","jobId":"worker-heavy-0123456789","status":"Waiting"}""");
        var launcher = Launcher(scheduler, new RadixLauncherOptions { JobSchedulerUrl = Scheduler, TimeLimitHours = 30, DrainMinutes = 45 });

        await launcher.LaunchAsync(new WorkerLaunch("worker-heavy-0123456789", "heavy"), CancellationToken.None);

        var request = Assert.Single(scheduler.Requests);
        Assert.Equal(("POST", "/api/v1/jobs"), (request.Method, request.Path));
        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        Assert.Equal("worker-heavy-0123456789", root.GetProperty("jobId").GetString());
        Assert.Equal(30 * 3600, root.GetProperty("timeLimitSeconds").GetInt64());
        Assert.Equal(0, root.GetProperty("backoffLimit").GetInt32());
        Assert.False(root.GetProperty("safeToRestart").GetBoolean());
        var variables = root.GetProperty("variables");
        Assert.Equal("worker-heavy-0123456789", variables.GetProperty("SQLFLOW_NODE_NAME").GetString());
        Assert.Equal("heavy", variables.GetProperty("SQLFLOW_WORKER_POOL").GetString());
        Assert.Equal("2700", variables.GetProperty("SQLFLOW_WORKER_DRAIN_SECONDS").GetString());
        Assert.False(root.TryGetProperty("resources", out _));
        Assert.False(root.TryGetProperty("payload", out _));
        Assert.DoesNotContain("TOKEN", request.Body, StringComparison.OrdinalIgnoreCase); // credentials come from Radix secrets
    }

    [Fact]
    public async Task ADefaultPoolWorker_ServesNoNamedPool()
    {
        var scheduler = new FakeScheduler();
        scheduler.Respond(HttpMethod.Post, "/api/v1/jobs", HttpStatusCode.OK, """{"name":"batch-1","jobId":"worker-default-0123456789"}""");

        await Launcher(scheduler).LaunchAsync(new WorkerLaunch("worker-default-0123456789", ""), CancellationToken.None);

        using var body = JsonDocument.Parse(Assert.Single(scheduler.Requests).Body!);
        Assert.False(body.RootElement.GetProperty("variables").TryGetProperty("SQLFLOW_WORKER_POOL", out _));
    }

    [Fact]
    public async Task APoolsOwnResources_WinOverTheDefault()
    {
        var scheduler = new FakeScheduler();
        scheduler.Respond(HttpMethod.Post, "/api/v1/jobs", HttpStatusCode.OK, """{"name":"batch-1"}""");
        var options = new RadixLauncherOptions
        {
            JobSchedulerUrl = Scheduler,
            Resources = new RadixResources { Requests = new RadixResourceList { Cpu = "500m", Memory = "1Gi" } },
        };
        options.Pools["heavy"] = new RadixPoolOptions
        {
            Resources = new RadixResources
            {
                Requests = new RadixResourceList { Cpu = "2", Memory = "8Gi" },
                Limits = new RadixResourceList { Memory = "8Gi" },
            },
        };
        var launcher = Launcher(scheduler, options);

        await launcher.LaunchAsync(new WorkerLaunch("worker-heavy-0123456789", "heavy"), CancellationToken.None);
        await launcher.LaunchAsync(new WorkerLaunch("worker-default-0123456789", ""), CancellationToken.None);

        using var heavy = JsonDocument.Parse(scheduler.Requests[0].Body!);
        var resources = heavy.RootElement.GetProperty("resources");
        Assert.Equal("2", resources.GetProperty("requests").GetProperty("cpu").GetString());
        Assert.Equal("8Gi", resources.GetProperty("limits").GetProperty("memory").GetString());
        Assert.False(resources.GetProperty("limits").TryGetProperty("cpu", out _));

        using var standard = JsonDocument.Parse(scheduler.Requests[1].Body!);
        Assert.Equal("500m", standard.RootElement.GetProperty("resources").GetProperty("requests").GetProperty("cpu").GetString());
        Assert.False(standard.RootElement.GetProperty("resources").TryGetProperty("limits", out _));
    }

    [Fact]
    public async Task TheList_ReadsEveryStatus_AndSkipsJobsThatAreNotWorkers()
    {
        var scheduler = new FakeScheduler();
        scheduler.Respond(HttpMethod.Get, "/api/v1/jobs", HttpStatusCode.OK, """
            [
              {"name":"b1","jobId":"worker-default-0000000001","status":"Waiting","created":"2026-10-13T10:00:00Z"},
              {"name":"b2","jobId":"worker-default-0000000002","status":"Active","created":"2026-10-13T10:01:00Z"},
              {"name":"b3","jobId":"worker-default-0000000003","status":"Running","created":"2026-10-13T10:02:00+02:00"},
              {"name":"b4","jobId":"worker-default-0000000004","status":"Succeeded"},
              {"name":"b5","jobId":"worker-default-0000000005","status":"Failed","message":"BackoffLimitExceeded"},
              {"name":"b6","jobId":"worker-default-0000000006","status":"Stopped"},
              {"name":"b7","jobId":"worker-default-0000000007","status":"Stopping"},
              {"name":"b8","jobId":"worker-default-0000000008","status":"SomethingNew"},
              {"name":"b9","status":"Running"},
              {"jobId":"worker-default-0000000010","status":"Running"}
            ]
            """);

        var workers = (await Launcher(scheduler).ListAsync(CancellationToken.None)).ToDictionary(w => w.Name);

        Assert.Equal(8, workers.Count);
        Assert.Equal(LaunchedWorkerState.Starting, workers["worker-default-0000000001"].State);
        Assert.Equal(LaunchedWorkerState.Starting, workers["worker-default-0000000002"].State);
        Assert.Equal(LaunchedWorkerState.Running, workers["worker-default-0000000003"].State);
        Assert.Equal(new DateTime(2026, 10, 13, 8, 2, 0, DateTimeKind.Utc), workers["worker-default-0000000003"].LaunchedUtc);
        Assert.Equal(LaunchedWorkerState.Ended, workers["worker-default-0000000004"].State);
        Assert.Equal(LaunchedWorkerState.Ended, workers["worker-default-0000000005"].State);
        Assert.Equal("Failed: BackoffLimitExceeded", workers["worker-default-0000000005"].Detail);
        Assert.Equal(LaunchedWorkerState.Ended, workers["worker-default-0000000006"].State);
        Assert.Equal(LaunchedWorkerState.Ended, workers["worker-default-0000000007"].State);
        Assert.Equal(LaunchedWorkerState.Running, workers["worker-default-0000000008"].State); // an unknown word never starts a second
    }

    [Fact]
    public async Task TheList_KeepsTheNewestJobUnderAJobId()
    {
        var scheduler = new FakeScheduler();
        scheduler.Respond(HttpMethod.Get, "/api/v1/jobs", HttpStatusCode.OK, """
            [
              {"name":"newer","jobId":"worker-default-0000000001","status":"Running","created":"2026-10-13T11:00:00Z"},
              {"name":"older","jobId":"worker-default-0000000001","status":"Failed","created":"2026-10-13T10:00:00Z"}
            ]
            """);
        scheduler.Respond(HttpMethod.Post, "/api/v1/jobs/newer/stop", HttpStatusCode.OK, "{}");
        var launcher = Launcher(scheduler);

        var worker = Assert.Single(await launcher.ListAsync(CancellationToken.None));
        Assert.Equal(LaunchedWorkerState.Running, worker.State);

        await launcher.StopAsync("worker-default-0000000001", CancellationToken.None);
        Assert.Equal("/api/v1/jobs/newer/stop", scheduler.Requests[^1].Path);
    }

    [Fact]
    public async Task AStop_UsesRadixsOwnJobName_FromTheStartAnswer()
    {
        var scheduler = new FakeScheduler();
        scheduler.Respond(HttpMethod.Post, "/api/v1/jobs", HttpStatusCode.OK, """{"name":"batch-xyz-job-7","jobId":"worker-default-0123456789"}""");
        scheduler.Respond(HttpMethod.Post, "/api/v1/jobs/batch-xyz-job-7/stop", HttpStatusCode.OK, "{}");
        var launcher = Launcher(scheduler);

        await launcher.LaunchAsync(new WorkerLaunch("worker-default-0123456789", ""), CancellationToken.None);
        await launcher.StopAsync("worker-default-0123456789", CancellationToken.None);

        Assert.Equal(["POST /api/v1/jobs", "POST /api/v1/jobs/batch-xyz-job-7/stop"], scheduler.Requests.Select(r => $"{r.Method} {r.Path}"));
    }

    [Fact]
    public async Task StoppingAWorkerTheSchedulerDoesNotHave_DoesNothing()
    {
        var scheduler = new FakeScheduler();
        scheduler.Respond(HttpMethod.Get, "/api/v1/jobs", HttpStatusCode.OK, "[]");

        await Launcher(scheduler).StopAsync("worker-default-0123456789", CancellationToken.None);

        Assert.Equal(["GET /api/v1/jobs"], scheduler.Requests.Select(r => $"{r.Method} {r.Path}"));
    }

    [Fact]
    public async Task StopsRightAfterAList_TrustIt_SoABurstAsksTheSchedulerNothingMore()
    {
        var scheduler = new FakeScheduler();
        scheduler.Respond(HttpMethod.Get, "/api/v1/jobs", HttpStatusCode.OK, """[{"name":"b1","jobId":"worker-default-0000000001","status":"Running"}]""");
        scheduler.Respond(HttpMethod.Post, "/api/v1/jobs/b1/stop", HttpStatusCode.OK, "{}");
        var launcher = Launcher(scheduler);

        await launcher.ListAsync(CancellationToken.None);
        await launcher.StopAsync("worker-default-0000000002", CancellationToken.None); // not a job the list named
        await launcher.StopAsync("worker-default-0000000003", CancellationToken.None);
        await launcher.StopAsync("worker-default-0000000001", CancellationToken.None);

        Assert.Equal(
            ["GET /api/v1/jobs", "POST /api/v1/jobs/b1/stop"], scheduler.Requests.Select(r => $"{r.Method} {r.Path}"));
    }

    [Theory]
    [InlineData(" 4faca8595c5283a9d0f17a623b9255a0d9866a2e ", "4faca8595c5283a9d0f17a623b9255a0d9866a2e")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TheDeploymentBuild_IsTheCommitRadixDeployedFrom(string? commit, string? expected)
    {
        var options = new RadixLauncherOptions { JobSchedulerUrl = Scheduler };
        var launcher = new RadixWorkerLauncher(() => new HttpClient(new FakeScheduler()), options, deploymentBuild: commit);

        Assert.Equal(expected, launcher.DeploymentBuild);
        Assert.Equal("RADIX_GIT_COMMIT_HASH", RadixWorkerLauncher.CommitVariable);
    }

    [Fact]
    public async Task StoppingAJobThatIsAlreadyGone_IsNotAnError()
    {
        var scheduler = new FakeScheduler();
        scheduler.Respond(HttpMethod.Get, "/api/v1/jobs", HttpStatusCode.OK, """[{"name":"b1","jobId":"worker-default-0123456789","status":"Running"}]""");
        scheduler.Respond(HttpMethod.Post, "/api/v1/jobs/b1/stop", HttpStatusCode.NotFound, """{"message":"not found"}""");

        await Launcher(scheduler).StopAsync("worker-default-0123456789", CancellationToken.None);
    }

    [Fact]
    public async Task ARefusedStart_SaysWhy_AndThatNothingStarted()
    {
        var scheduler = new FakeScheduler();
        scheduler.Respond(HttpMethod.Post, "/api/v1/jobs", HttpStatusCode.BadRequest, """{"message":"invalid resources"}""");

        var ex = await Assert.ThrowsAsync<WorkerLauncherException>(
            () => Launcher(scheduler).LaunchAsync(new WorkerLaunch("worker-default-0123456789", ""), CancellationToken.None));

        Assert.Contains("refused to start worker 'worker-default-0123456789': 400 Bad Request", ex.Message, StringComparison.Ordinal);
        Assert.Contains("invalid resources", ex.Message, StringComparison.Ordinal);
        Assert.False(ex.MayHaveStarted);
    }

    [Fact]
    public async Task AServerErrorOnAStart_MayHaveCreatedTheJob()
    {
        var scheduler = new FakeScheduler();
        scheduler.Respond(HttpMethod.Post, "/api/v1/jobs", HttpStatusCode.InternalServerError, "boom");

        var ex = await Assert.ThrowsAsync<WorkerLauncherException>(
            () => Launcher(scheduler).LaunchAsync(new WorkerLaunch("worker-default-0123456789", ""), CancellationToken.None));

        Assert.True(ex.MayHaveStarted);
    }

    [Fact]
    public async Task AStartThatTimesOut_MayHaveCreatedTheJob()
    {
        var scheduler = new FakeScheduler { Delay = TimeSpan.FromSeconds(5) };
        var launcher = Launcher(scheduler, new RadixLauncherOptions { JobSchedulerUrl = Scheduler, RequestTimeoutSeconds = 1 });

        var ex = await Assert.ThrowsAsync<WorkerLauncherException>(
            () => launcher.LaunchAsync(new WorkerLaunch("worker-default-0123456789", ""), CancellationToken.None));

        Assert.True(ex.MayHaveStarted);
        Assert.Contains("did not answer within 1 s", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreachableScheduler_StartsNothing_AndNamesWhere()
    {
        var scheduler = new FakeScheduler { Unreachable = true };

        var ex = await Assert.ThrowsAsync<WorkerLauncherException>(() => Launcher(scheduler).ListAsync(CancellationToken.None));

        Assert.False(ex.MayHaveStarted);
        Assert.Contains("at http://worker:8080 could not be reached to list the jobs", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyOrMissingTimestamps_AreNoTimestamps_NotAFailedList()
    {
        var scheduler = new FakeScheduler();
        scheduler.Respond(HttpMethod.Get, "/api/v1/jobs", HttpStatusCode.OK, """
            [
              {"name":"b1","jobId":"worker-default-0000000001","status":"Running","created":"","started":"","ended":""},
              {"name":"b2","jobId":"worker-default-0000000002","status":"Waiting","created":null},
              {"name":"b3","jobId":"worker-default-0000000003","status":"Running","created":"not a time"},
              {"name":"b4","jobId":"worker-default-0000000004","status":"Running","created":"2026-10-13T10:00:00.123456789Z"}
            ]
            """);

        var workers = (await Launcher(scheduler).ListAsync(CancellationToken.None)).ToDictionary(w => w.Name);

        Assert.Equal(4, workers.Count);
        Assert.Null(workers["worker-default-0000000001"].LaunchedUtc);
        Assert.Null(workers["worker-default-0000000002"].LaunchedUtc);
        Assert.Null(workers["worker-default-0000000003"].LaunchedUtc);
        // Nine fractional digits, as Go writes them: read to the seventh.
        Assert.Equal(new DateTime(2026, 10, 13, 10, 0, 0, DateTimeKind.Utc).AddTicks(1234567), workers["worker-default-0000000004"].LaunchedUtc);
    }

    [Fact]
    public async Task AStartThatCouldNotConnect_StartedNothing_ButOneWhoseAnswerWasLost_MayHaveStarted()
    {
        var unreachable = new FakeScheduler { Failure = new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused (worker:8080)") };
        var refused = await Assert.ThrowsAsync<WorkerLauncherException>(
            () => Launcher(unreachable).LaunchAsync(new WorkerLaunch("worker-default-0123456789", ""), CancellationToken.None));
        Assert.False(refused.MayHaveStarted);

        var lost = new FakeScheduler { Failure = new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended prematurely.") };
        var ended = await Assert.ThrowsAsync<WorkerLauncherException>(
            () => Launcher(lost).LaunchAsync(new WorkerLaunch("worker-default-0123456789", ""), CancellationToken.None));
        Assert.True(ended.MayHaveStarted);
    }

    [Fact]
    public void TheWorkerTimeLimit_IsTheJobsTimeLimit()
        => Assert.Equal(
            TimeSpan.FromHours(30),
            Launcher(new FakeScheduler(), new RadixLauncherOptions { JobSchedulerUrl = Scheduler, TimeLimitHours = 30 }).WorkerTimeLimit);

    [Theory]
    [InlineData("http://user:secret@worker:8080")]
    [InlineData("http://worker:8080/?token=x")]
    [InlineData("http://worker:8080/#x")]
    public void ASchedulerUrlWithCredentialsOrAQuery_IsRefused(string url)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new RadixLauncherOptions { JobSchedulerUrl = new Uri(url) }.Validate(Managed()));
        Assert.Contains("JobSchedulerUrl is the scheduler's address only", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreadableAnswer_IsAnError_NotAnEmptyFleet()
    {
        var scheduler = new FakeScheduler();
        scheduler.Respond(HttpMethod.Get, "/api/v1/jobs", HttpStatusCode.OK, "<html>proxy error</html>");

        var ex = await Assert.ThrowsAsync<WorkerLauncherException>(() => Launcher(scheduler).ListAsync(CancellationToken.None));

        Assert.Contains("could not be read", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhatTheSchedulerSays_IsBoundedAndRedacted()
    {
        var scheduler = new FakeScheduler();
        var noisy = "Server=tcp:db;Password=hunter2-not-a-real-secret;" + new string('x', 2000);
        scheduler.Respond(HttpMethod.Post, "/api/v1/jobs", HttpStatusCode.BadRequest, noisy);

        var ex = await Assert.ThrowsAsync<WorkerLauncherException>(
            () => Launcher(scheduler).LaunchAsync(new WorkerLaunch("worker-default-0123456789", ""), CancellationToken.None));

        Assert.DoesNotContain("hunter2", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.Message.Length < 600, $"message is {ex.Message.Length} characters");
    }

    // ------------------------------------------------------------------------------------------------- settings -----

    [Fact]
    public void TheSchedulerUrl_IsRequired()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new RadixLauncherOptions().Validate(Managed()));
        Assert.Contains("ControlPlane:ManagedWorkers:Radix:JobSchedulerUrl is required", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTimeLimit_MustOutliveTheWorkersLifetime()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new RadixLauncherOptions { JobSchedulerUrl = Scheduler, TimeLimitHours = 12 }.Validate(Managed(maxLifetimeHours: 12)));
        Assert.Contains("greater than ControlPlane:ManagedWorkers:MaxLifetimeHours (12)", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(13, 60, false)] // half of one hour past the lifetime is less than the hour's drain
    [InlineData(14, 60, true)]
    [InlineData(13, 30, true)]
    [InlineData(13, 0, true)]
    [InlineData(16, 120, true)]
    [InlineData(15, 120, false)]
    public void TheTimeLimit_LeavesTwoDrainWindowsBeyondTheLifetime(int timeLimitHours, int drainMinutes, bool valid)
    {
        var options = new RadixLauncherOptions { JobSchedulerUrl = Scheduler, TimeLimitHours = timeLimitHours, DrainMinutes = drainMinutes };
        if (valid)
        {
            options.Validate(Managed(maxLifetimeHours: 12));
            return;
        }

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(Managed(maxLifetimeHours: 12)));
        Assert.Contains($"by at least twice ControlPlane:ManagedWorkers:Radix:DrainMinutes ({drainMinutes})", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResourcesForAPoolThatIsNotManaged_AreRefused()
    {
        var options = new RadixLauncherOptions { JobSchedulerUrl = Scheduler };
        options.Pools["heavy"] = new RadixPoolOptions();

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(Managed()));
        Assert.Contains("Radix:Pools:heavy names a pool that is not managed", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("500m", true)]
    [InlineData("2", true)]
    [InlineData("1.5", true)]
    [InlineData("512Mi", true)]
    [InlineData("2Gi", true)]
    [InlineData("two", false)]
    [InlineData("512 Mi", false)]
    [InlineData("-1", false)]
    public void AResourceQuantity_IsAKubernetesQuantity(string quantity, bool valid)
    {
        var options = new RadixLauncherOptions
        {
            JobSchedulerUrl = Scheduler,
            Resources = new RadixResources { Limits = new RadixResourceList { Memory = quantity } },
        };

        if (valid)
        {
            options.Validate(Managed());
        }
        else
        {
            var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(Managed()));
            Assert.Contains("Radix:Resources:Limits:Memory", ex.Message, StringComparison.Ordinal);
        }
    }

    // -------------------------------------------------------------------------------------------------- support -----

    private static ManagedWorkersOptions Managed(int maxLifetimeHours = 12)
        => new() { Enabled = true, Launcher = "radix", MaxLifetimeHours = maxLifetimeHours };

    private static RadixWorkerLauncher Launcher(FakeScheduler scheduler, RadixLauncherOptions? options = null)
    {
        options ??= new RadixLauncherOptions { JobSchedulerUrl = Scheduler };
        var client = new HttpClient(scheduler) { BaseAddress = options.JobSchedulerUrl, Timeout = Timeout.InfiniteTimeSpan };
        return new RadixWorkerLauncher(() => client, options);
    }

    private sealed record Recorded(string Method, string Path, string? Body);

    /// <summary>A job scheduler that answers what each test tells it to, records every call, and can be slow or gone.</summary>
    private sealed class FakeScheduler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _answers = new(StringComparer.Ordinal);

        public List<Recorded> Requests { get; } = [];

        public TimeSpan Delay { get; init; }

        public bool Unreachable { get; init; }

        /// <summary>Thrown for every request, when set.</summary>
        public Exception? Failure { get; init; }

        public void Respond(HttpMethod method, string path, HttpStatusCode status, string body)
            => _answers[$"{method.Method} {path}"] = (status, body);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Recorded(request.Method.Method, request.RequestUri!.AbsolutePath, body));
            if (Unreachable)
            {
                throw new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known. (worker:8080)");
            }

            if (Failure is not null)
            {
                throw Failure;
            }

            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            var (status, text) = _answers.TryGetValue($"{request.Method.Method} {request.RequestUri.AbsolutePath}", out var answer)
                ? answer
                : (HttpStatusCode.NotFound, "{}");
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }
}
