using System.Text.RegularExpressions;
using SqlFlow.ControlPlane.Configuration;

namespace SqlFlow.Delivery.ControlPlane.ManagedWorkers;

/// <summary>
/// The <c>radix</c> launcher (section <c>ControlPlane:ManagedWorkers:Radix</c>): where the job scheduler is, how long a
/// job may live, how long a worker stopped while busy keeps finishing its runs, and the CPU and memory each pool's jobs
/// ask for. Validated only when managed workers use this launcher.
/// </summary>
public sealed partial class RadixLauncherOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = ManagedWorkersOptions.Section + ":Radix";

    /// <summary>The job component's scheduler, <c>http://&lt;job-name&gt;:&lt;schedulerPort&gt;</c>. Required.</summary>
    public Uri? JobSchedulerUrl { get; set; }

    /// <summary>The job's time limit: at least <c>ControlPlane:ManagedWorkers:MaxLifetimeHours</c> plus twice
    /// <see cref="DrainMinutes"/>. The fleet drains a worker it could not replace halfway between its lifetime and this
    /// limit, so that worker still has a whole drain window to finish its runs before Radix ends it.</summary>
    public int TimeLimitHours { get; set; } = 24;

    /// <summary>How long a worker stopped while busy keeps finishing its runs.</summary>
    public int DrainMinutes { get; set; } = 60;

    /// <summary>The timeout of each call to the job scheduler.</summary>
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>The CPU and memory every job asks for, unless its pool says otherwise; none leaves the job component's own.</summary>
    public RadixResources? Resources { get; set; }

    /// <summary>Per-pool resources, by pool name; each must be a managed pool.</summary>
    public Dictionary<string, RadixPoolOptions> Pools { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The resources a pool's jobs ask for: the pool's own, else the default, else none.</summary>
    public RadixResources? ResourcesFor(string pool)
        => Pools.TryGetValue(pool, out var own) && own.Resources is not null ? own.Resources : Resources;

    /// <summary>Rejects settings that cannot work, naming the setting. <paramref name="managed"/> is the managed workers'
    /// own configuration, which the time limit and the per-pool settings are checked against.</summary>
    /// <exception cref="InvalidOperationException">A setting cannot work.</exception>
    public void Validate(ManagedWorkersOptions managed)
    {
        ArgumentNullException.ThrowIfNull(managed);
        if (JobSchedulerUrl is not { IsAbsoluteUri: true } url || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"{SectionName}:JobSchedulerUrl is required: the job component's scheduler, http://<job-name>:<schedulerPort> (for example http://worker:8080).");
        }

        if (url.UserInfo.Length > 0 || url.Query.Length > 0 || url.Fragment.Length > 0)
        {
            throw new InvalidOperationException(
                $"{SectionName}:JobSchedulerUrl is the scheduler's address only: no credentials, query or fragment (the scheduler needs none).");
        }

        if (DrainMinutes is < 0 or > 1440)
        {
            throw new InvalidOperationException($"{SectionName}:DrainMinutes must be between 0 and 1440.");
        }

        // The fleet's last-resort drain comes halfway between the lifetime and the limit; twice the drain window between
        // them leaves that worker a whole window before Radix ends the job.
        var leastHours = Math.Max(managed.MaxLifetimeHours + 1L, ((managed.MaxLifetimeHours * 60L) + (2L * DrainMinutes) + 59) / 60);
        if (TimeLimitHours is < 1 or > 720 || TimeLimitHours < leastHours)
        {
            throw new InvalidOperationException(
                $"{SectionName}:TimeLimitHours must be between 1 and 720, greater than {ManagedWorkersOptions.Section}:MaxLifetimeHours ({managed.MaxLifetimeHours}) by at least twice {SectionName}:DrainMinutes ({DrainMinutes}): at least {leastHours} here, so a worker that could not be replaced in time still drains for a whole window before Radix ends the job.");
        }

        if (RequestTimeoutSeconds is < 1 or > 300)
        {
            throw new InvalidOperationException($"{SectionName}:RequestTimeoutSeconds must be between 1 and 300.");
        }

        Resources?.Validate($"{SectionName}:Resources");
        foreach (var (pool, options) in Pools)
        {
            if (!managed.Pools.ContainsKey(pool))
            {
                throw new InvalidOperationException(
                    $"{SectionName}:Pools:{pool} names a pool that is not managed; list it under {ManagedWorkersOptions.Section}:Pools first.");
            }

            options.Resources?.Validate($"{SectionName}:Pools:{pool}:Resources");
        }
    }

    /// <summary>A Kubernetes quantity: a number with an optional decimal part and an optional unit suffix.</summary>
    [GeneratedRegex(@"^[0-9]+(\.[0-9]+)?(m|k|M|G|T|P|E|Ki|Mi|Gi|Ti|Pi|Ei)?$", RegexOptions.CultureInvariant)]
    internal static partial Regex Quantity();
}

/// <summary>One pool's settings for the <c>radix</c> launcher.</summary>
public sealed class RadixPoolOptions
{
    /// <summary>The CPU and memory this pool's jobs ask for.</summary>
    public RadixResources? Resources { get; set; }
}

/// <summary>CPU and memory requests and limits for a job, as Kubernetes quantities (for example <c>500m</c>, <c>2Gi</c>).</summary>
public sealed class RadixResources
{
    public RadixResourceList? Requests { get; set; }

    public RadixResourceList? Limits { get; set; }

    internal void Validate(string setting)
    {
        Requests?.Validate($"{setting}:Requests");
        Limits?.Validate($"{setting}:Limits");
    }

    internal RadixWorkerLauncher.RadixResourcesRequest? ToRequest()
    {
        var requests = Requests?.ToMap();
        var limits = Limits?.ToMap();
        return requests is null && limits is null ? null : new RadixWorkerLauncher.RadixResourcesRequest(requests, limits);
    }
}

/// <summary>A CPU and a memory quantity.</summary>
public sealed class RadixResourceList
{
    public string? Cpu { get; set; }

    public string? Memory { get; set; }

    internal void Validate(string setting)
    {
        foreach (var (name, value) in new[] { ("Cpu", Cpu), ("Memory", Memory) })
        {
            if (value is not null && !RadixLauncherOptions.Quantity().IsMatch(value.Trim()))
            {
                throw new InvalidOperationException(
                    $"{setting}:{name} '{value}' is not a Kubernetes quantity (for example 500m, 2, 512Mi, 2Gi).");
            }
        }
    }

    internal Dictionary<string, string>? ToMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(Cpu))
        {
            map["cpu"] = Cpu.Trim();
        }

        if (!string.IsNullOrWhiteSpace(Memory))
        {
            map["memory"] = Memory.Trim();
        }

        return map.Count == 0 ? null : map;
    }
}
