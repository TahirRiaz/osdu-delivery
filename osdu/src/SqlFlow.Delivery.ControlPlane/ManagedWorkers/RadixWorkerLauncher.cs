using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SqlFlow.Core.Secrets;
using SqlFlow.Dispatch.ManagedWorkers;

namespace SqlFlow.Delivery.ControlPlane.ManagedWorkers;

/// <summary>
/// Starts managed workers as Radix jobs, through the job scheduler Radix runs beside a job component in the same
/// application and environment (<c>http://&lt;job-name&gt;:&lt;schedulerPort&gt;</c>, which needs no credential because only
/// the application's own components can reach it). Each worker is one job: its <c>jobId</c> is the worker's name, its
/// variables name the node, its pool and its drain window, and it never restarts itself (<c>backoffLimit: 0</c>) or lets
/// the cluster autoscaler move it (<c>safeToRestart: false</c>); the managed fleet replaces a worker that ends. The
/// worker's node token, the module database and every connection come from the job component's own Radix secrets,
/// never from a launch request, which Radix stores in the job's specification.
/// </summary>
/// <remarks>
/// Every call runs under <see cref="RadixLauncherOptions.RequestTimeoutSeconds"/>. A start that failed after the request
/// may have reached the scheduler (a timeout, a connection lost after sending, a server error, an answer that cannot be
/// read) is reported as possibly started; only a start that never left (no name, no connection) is reported as not
/// started. The scheduler's timestamps are read leniently (an empty or unreadable one is no timestamp), and whatever it
/// answers is bounded and redacted before it reaches a message. Error text names the scheduler by scheme, host and port
/// only.
/// </remarks>
public sealed partial class RadixWorkerLauncher : IWorkerLauncher
{
    /// <summary>The launcher's name in <c>ControlPlane:ManagedWorkers:Launcher</c>.</summary>
    public const string LauncherName = "radix";

    /// <summary>The named HTTP client the launcher calls the job scheduler with.</summary>
    public const string HttpClientName = "managed-workers-radix";

    private const int MaxDetailLength = 300;

    /// <summary>The most of an error answer read, so a huge body never reaches memory.</summary>
    private const int MaxErrorBodyBytes = 4096;

    /// <summary>The variable Radix sets in every component to the commit it built and deployed them from.</summary>
    public const string CommitVariable = "RADIX_GIT_COMMIT_HASH";

    /// <summary>How long a started job's Radix name is kept without the scheduler listing it.</summary>
    private static readonly TimeSpan KeepUnlistedJobName = TimeSpan.FromMinutes(10);

    /// <summary>How recent a list must be for a stop to trust it: a worker it does not name is then no job to stop, and a
    /// burst of stops after the fleet's own list asks the scheduler nothing more.</summary>
    private static readonly TimeSpan TrustListFor = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Func<HttpClient> _client;
    private readonly RadixLauncherOptions _options;
    private readonly TimeProvider _clock;
    private readonly string _scheduler;

    // The scheduler stops a job by its Radix name, which it generates; the fleet knows a worker by its jobId. Each entry
    // remembers when it was last confirmed, so names the scheduler no longer lists are dropped.
    private readonly ConcurrentDictionary<string, (string Job, DateTime SeenUtc)> _jobNames = new(StringComparer.Ordinal);

    private long _listedTicks;

    /// <summary>Creates the launcher. <paramref name="client"/> hands out the named client; the options are validated by
    /// the module before the launcher is used. <paramref name="deploymentBuild"/> is the commit Radix deployed this
    /// application from (<see cref="CommitVariable"/>), when it says.</summary>
    public RadixWorkerLauncher(
        Func<HttpClient> client, RadixLauncherOptions options, TimeProvider? clock = null, string? deploymentBuild = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        _client = client;
        _options = options;
        _clock = clock ?? TimeProvider.System;
        DeploymentBuild = string.IsNullOrWhiteSpace(deploymentBuild) ? null : deploymentBuild.Trim();
        _scheduler = options.JobSchedulerUrl is { IsAbsoluteUri: true } url
            ? $"{url.Scheme}://{url.Host}{(url.IsDefaultPort ? string.Empty : ":" + url.Port.ToString(CultureInfo.InvariantCulture))}"
            : "(no address)";
    }

    public string Name => LauncherName;

    /// <summary>Radix ends a job at its time limit; the fleet drains a worker well before.</summary>
    public TimeSpan? WorkerTimeLimit => TimeSpan.FromHours(_options.TimeLimitHours);

    /// <summary>The commit Radix built the control plane and the worker job component from, in one deployment: a new one
    /// is a rollout, which replaces the workers; the same one is a restart, which keeps them.</summary>
    public string? DeploymentBuild { get; }

    public async Task<IReadOnlyList<LaunchedWorker>> ListAsync(CancellationToken ct)
    {
        var jobs = await SendAsync<List<RadixJobStatus>>(HttpMethod.Get, "api/v1/jobs", null, "list the jobs", ct).ConfigureAwait(false)
            ?? [];
        var now = _clock.GetUtcNow().UtcDateTime;

        // The newest job under a jobId wins; the scheduler never reuses one, but a manual retry could.
        var newest = new Dictionary<string, RadixJobStatus>(StringComparer.Ordinal);
        foreach (var job in jobs)
        {
            if (string.IsNullOrWhiteSpace(job.JobId) || string.IsNullOrWhiteSpace(job.Name))
            {
                continue;
            }

            if (!newest.TryGetValue(job.JobId, out var seen)
                || (Timestamp(job.Created) ?? DateTimeOffset.MinValue) >= (Timestamp(seen.Created) ?? DateTimeOffset.MinValue))
            {
                newest[job.JobId] = job;
            }
        }

        var workers = new List<LaunchedWorker>(newest.Count);
        foreach (var (jobId, job) in newest)
        {
            _jobNames[jobId] = (job.Name!, now);
            var (state, detail) = Map(job);
            workers.Add(new LaunchedWorker(jobId, state, Timestamp(job.Created)?.UtcDateTime, detail));
        }

        foreach (var (jobId, entry) in _jobNames)
        {
            if (!newest.ContainsKey(jobId) && now - entry.SeenUtc > KeepUnlistedJobName)
            {
                _jobNames.TryRemove(jobId, out _);
            }
        }

        Interlocked.Exchange(ref _listedTicks, now.Ticks);
        return workers;
    }

    public async Task LaunchAsync(WorkerLaunch launch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SQLFLOW_NODE_NAME"] = launch.Name,
            ["SQLFLOW_WORKER_DRAIN_SECONDS"] = (_options.DrainMinutes * 60).ToString(CultureInfo.InvariantCulture),
        };

        // A default pool worker serves untargeted runs only: it gets no pool at all, rather than an empty one.
        if (launch.Pool.Length > 0)
        {
            variables["SQLFLOW_WORKER_POOL"] = launch.Pool;
        }

        var request = new RadixJobRequest(
            launch.Name,
            variables,
            (long)_options.TimeLimitHours * 3600,
            BackoffLimit: 0,
            SafeToRestart: false,
            _options.ResourcesFor(launch.Pool)?.ToRequest());

        var job = await SendAsync<RadixJobStatus>(HttpMethod.Post, "api/v1/jobs", request, $"start worker '{launch.Name}'", ct, launching: true)
            .ConfigureAwait(false);
        if (job?.Name is { Length: > 0 } name)
        {
            _jobNames[launch.Name] = (name, _clock.GetUtcNow().UtcDateTime);
        }
    }

    public async Task StopAsync(string workerName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerName);
        if (!_jobNames.TryGetValue(workerName, out var entry))
        {
            // Asked again only when the last list is old: the fleet lists every tick, so a stop of a worker its list did
            // not name is a job the scheduler does not have, and a burst of stops never lists once per stop.
            var listed = new DateTime(Interlocked.Read(ref _listedTicks), DateTimeKind.Utc);
            if (_clock.GetUtcNow().UtcDateTime - listed < TrustListFor)
            {
                return;
            }

            await ListAsync(ct).ConfigureAwait(false);
            if (!_jobNames.TryGetValue(workerName, out entry))
            {
                return; // the scheduler has no such job any more: nothing to stop
            }
        }

        await SendAsync<JsonElement?>(
            HttpMethod.Post, $"api/v1/jobs/{Uri.EscapeDataString(entry.Job)}/stop", null, $"stop worker '{workerName}'", ct,
            notFoundIsDone: true).ConfigureAwait(false);
    }

    /// <summary>Where a Radix job stands, in the fleet's words.</summary>
    internal static (LaunchedWorkerState State, string? Detail) Map(RadixJobStatus job)
    {
        var status = job.Status is null ? null : Bound(job.Status);
        var detail = string.IsNullOrWhiteSpace(job.Message) ? status : $"{status}: {Bound(job.Message)}";
        return job.Status switch
        {
            "Waiting" or "Active" => (LaunchedWorkerState.Starting, detail),
            "Running" => (LaunchedWorkerState.Running, detail),
            "Succeeded" or "Failed" or "Stopped" or "Stopping" or "Completed" => (LaunchedWorkerState.Ended, detail),
            // A status this launcher does not know is counted as running, so a new word from Radix never makes the
            // fleet start a second worker beside one that exists.
            _ => (LaunchedWorkerState.Running, detail),
        };
    }

    /// <summary>A scheduler timestamp, or null when it is missing, empty or not a timestamp. Go writes up to nine
    /// fractional digits and .NET reads seven, so any beyond the seventh are dropped first.</summary>
    internal static DateTimeOffset? Timestamp(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = ExtraFractionDigits().Replace(text.Trim(), "$1");
        return DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value
            : null;
    }

    [GeneratedRegex(@"(\.\d{7})\d+", RegexOptions.CultureInvariant)]
    private static partial Regex ExtraFractionDigits();

    private async Task<T?> SendAsync<T>(
        HttpMethod method, string path, object? body, string what, CancellationToken ct, bool launching = false,
        bool notFoundIsDone = false)
    {
        using var call = CancellationTokenSource.CreateLinkedTokenSource(ct);
        call.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        }

        HttpResponseMessage response;
        try
        {
            response = await _client().SendAsync(request, call.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new WorkerLauncherException(
                $"the Radix job scheduler at {_scheduler} did not answer within {_options.RequestTimeoutSeconds} s when asked to {what}",
                mayHaveStarted: launching);
        }
        catch (HttpRequestException ex)
        {
            // Only a request that never reached the scheduler (no name, no connection) is known not to have started a job.
            var neverSent = ex.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError
                or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError;
            throw new WorkerLauncherException(
                $"the Radix job scheduler at {_scheduler} could not be reached to {what}: {SecretHygiene.RedactedMessage(ex)}",
                mayHaveStarted: launching && !neverSent, ex);
        }

        using (response)
        {
            if (notFoundIsDone && response.StatusCode == HttpStatusCode.NotFound)
            {
                return default;
            }

            if (!response.IsSuccessStatusCode)
            {
                var text = await ReadBoundedAsync(response, call.Token).ConfigureAwait(false);
                throw new WorkerLauncherException(
                    $"the Radix job scheduler at {_scheduler} refused to {what}: {(int)response.StatusCode} {response.ReasonPhrase}"
                    + (text.Length > 0 ? $": {text}" : string.Empty),
                    mayHaveStarted: launching && (int)response.StatusCode >= 500);
            }

            if (typeof(T) == typeof(JsonElement?))
            {
                return default;
            }

            try
            {
                return await response.Content.ReadFromJsonAsync<T>(Json, call.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                throw new WorkerLauncherException(
                    $"the Radix job scheduler's answer when asked to {what} could not be read: {SecretHygiene.RedactedMessage(ex)}",
                    mayHaveStarted: launching, ex);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new WorkerLauncherException(
                    $"the Radix job scheduler at {_scheduler} did not finish its answer within {_options.RequestTimeoutSeconds} s when asked to {what}",
                    mayHaveStarted: launching);
            }
        }
    }

    /// <summary>The start of an error answer, at most <see cref="MaxErrorBodyBytes"/> read, bounded and redacted.</summary>
    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[MaxErrorBodyBytes];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false)) > 0)
            {
                total += read;
            }

            return Bound(Encoding.UTF8.GetString(buffer, 0, total));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static string Bound(string text)
    {
        var flat = SecretHygiene.RedactedMessage(text.ReplaceLineEndings(" ").Trim());
        return flat.Length <= MaxDetailLength ? flat : flat[..MaxDetailLength] + "...";
    }

    /// <summary>One job as the scheduler reports it (the fields this launcher reads). Timestamps are kept as text and read
    /// leniently: a scheduler that sends an empty string for a time it does not have still lists.</summary>
    internal sealed record RadixJobStatus(
        string? Name, string? JobId, string? Status, string? Created, string? Ended, string? Message);

    /// <summary>A job to start (the fields this launcher sends).</summary>
    internal sealed record RadixJobRequest(
        string JobId, Dictionary<string, string> Variables, long TimeLimitSeconds, int BackoffLimit, bool SafeToRestart,
        RadixResourcesRequest? Resources);

    /// <summary>A job's CPU and memory requests and limits, as the scheduler takes them.</summary>
    internal sealed record RadixResourcesRequest(Dictionary<string, string>? Requests, Dictionary<string, string>? Limits);
}
