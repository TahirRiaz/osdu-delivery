using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.ControlPlane.Configuration;

/// <summary>
/// How fast approved cache changes are carried out to the estate (section <c>Osdu:CacheRollout</c>). When a cached
/// value moves, every record built from it has to be delivered again, and one corrected unit can reach millions of
/// rows: the rollout marks them in bounded batches instead of one statement, and these settings are the pace.
/// </summary>
public sealed class CacheRolloutOptions
{
    /// <summary>The configuration section the control plane module binds this from.</summary>
    public const string SectionName = "Osdu:CacheRollout";

    /// <summary>Turns the automatic rollout off. Approved changes then wait, and the GUI still shows what is queued.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Records marked for redelivery per batch.</summary>
    public int BatchSize { get; set; } = 5_000;

    /// <summary>Batches per tick, across all approved changes.</summary>
    public int BatchesPerPass { get; set; } = 2;

    /// <summary>Seconds between ticks.</summary>
    public int PollSeconds { get; set; } = 60;

    /// <exception cref="InvalidOperationException">A setting is outside its range; the message names it.</exception>
    public void Validate()
    {
        if (BatchSize < 1)
        {
            throw new InvalidOperationException($"{SectionName}:BatchSize must be positive.");
        }

        if (BatchesPerPass < 1)
        {
            throw new InvalidOperationException($"{SectionName}:BatchesPerPass must be positive.");
        }

        if (PollSeconds < 1)
        {
            throw new InvalidOperationException($"{SectionName}:PollSeconds must be positive.");
        }
    }
}

/// <summary>
/// The scheduled target probe (section <c>Osdu:TargetProbe</c>): how often the deployment asks every active delivery
/// flow's OSDU whether it still answers with that flow's credentials, so an operator and an alert see a target go away
/// without anyone pressing "Probe target".
/// <para>Off by default, and deliberately so: one probe is a token exchange and a request against a live OSDU, for every
/// interface of every active delivery flow, every interval. Switch it on where that cost is understood, and use
/// <see cref="Pipelines"/> to narrow it to the flows that matter.</para>
/// </summary>
public sealed class TargetProbeOptions
{
    /// <summary>The configuration section the control plane module binds this from.</summary>
    public const string SectionName = "Osdu:TargetProbe";

    /// <summary>The floor under <see cref="IntervalMinutes"/>: below this the probes cost more than they tell.</summary>
    public const int MinimumIntervalMinutes = 5;

    /// <summary>Runs the scheduled probe. Off by default, since every pass reaches a live OSDU.</summary>
    public bool Enabled { get; set; }

    /// <summary>Minutes between passes. Default 15, and never below <see cref="MinimumIntervalMinutes"/>.</summary>
    public int IntervalMinutes { get; set; } = 15;

    /// <summary>
    /// Seconds a pass waits for the probes it just queued to come back from the nodes before it moves on. A probe that
    /// has not come back by then is recorded by the next pass instead, so this only decides how quickly an outcome
    /// shows up, never whether it is recorded. Zero waits not at all. Default 60.
    /// </summary>
    public int SettleSeconds { get; set; } = 60;

    /// <summary>Interfaces probed in one pass, across every flow. Default 200.</summary>
    public int MaxPerPass { get; set; } = 200;

    /// <summary>
    /// The delivery flows to probe, by pipeline name, comma separated. Empty means every active delivery pipeline. A
    /// name no active delivery pipeline carries is reported in the pass's log rather than silently probing nothing.
    /// </summary>
    public string? Pipelines { get; set; }

    /// <summary>The flows <see cref="Pipelines"/> names, trimmed and deduplicated; empty for every active delivery pipeline.</summary>
    public IReadOnlyList<string> PipelineNames()
        => string.IsNullOrWhiteSpace(Pipelines)
            ? []
            : Pipelines.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    /// <exception cref="InvalidOperationException">A setting is outside its range; the message names it.</exception>
    public void Validate()
    {
        if (IntervalMinutes < MinimumIntervalMinutes || IntervalMinutes > 1440)
        {
            throw new InvalidOperationException(
                $"{SectionName}:IntervalMinutes must be between {MinimumIntervalMinutes} and 1440 (a day): every pass costs a token " +
                "exchange and a request against a live OSDU for each interface of each active delivery flow.");
        }

        if (SettleSeconds is < 0 or > 300)
        {
            throw new InvalidOperationException($"{SectionName}:SettleSeconds must be between 0 and 300.");
        }

        if (MaxPerPass is < 1 or > 1000)
        {
            throw new InvalidOperationException($"{SectionName}:MaxPerPass must be between 1 and 1000.");
        }

        if (!string.IsNullOrWhiteSpace(Pipelines) && PipelineNames().Count == 0)
        {
            throw new InvalidOperationException(
                $"{SectionName}:Pipelines names no flow. Leave it empty to probe every active delivery pipeline, or list the pipeline names, comma separated.");
        }
    }
}

/// <summary>
/// The OSDU data definitions the Templates page browses (section <c>Osdu:SchemaRepository</c>): the Open Group's
/// public repository of OSDU schemas, read through its GitLab API into a local copy on disk, one release at a time. The
/// defaults are the public repository; point both URLs at a mirror of it when the control plane cannot reach
/// community.opengroup.org.
/// </summary>
public sealed class SchemaRepositoryOptions
{
    /// <summary>The configuration section the control plane module binds this from.</summary>
    public const string SectionName = "Osdu:SchemaRepository";

    /// <summary>The GitLab API URL of the data definitions project.</summary>
    public Uri ApiUrl { get; set; } = OsduDataDefinitions.DefaultApiUrl;

    /// <summary>The project's web page, which the page links a release's schema files on.</summary>
    public Uri WebUrl { get; set; } = OsduDataDefinitions.DefaultWebUrl;

    /// <summary>Where the local copy lives; empty means <c>sqlflow/osdu-data-definitions</c> under the temp folder.</summary>
    public string? CacheDirectory { get; set; }

    /// <summary>Minutes the release list may age before it is read again from the repository; a sync reads it at once. Default a day.</summary>
    public int RefreshMinutes { get; set; } = 1440;

    /// <summary>Minutes downloading one release may take. Default 15.</summary>
    public int DownloadTimeoutMinutes { get; set; } = 15;

    /// <summary>Brings the local copy up (the release list, and the newest release when it is not on disk) when the control plane starts. Default on.</summary>
    public bool WarmOnStart { get; set; } = true;

    /// <exception cref="InvalidOperationException">A setting is outside its range; the message names it.</exception>
    public void Validate()
    {
        RequireHttpUrl(ApiUrl, nameof(ApiUrl));
        RequireHttpUrl(WebUrl, nameof(WebUrl));
        if (RefreshMinutes is < 1 or > 10080)
        {
            throw new InvalidOperationException($"{SectionName}:RefreshMinutes must be between 1 and 10080 (a week).");
        }

        if (DownloadTimeoutMinutes is < 1 or > 240)
        {
            throw new InvalidOperationException($"{SectionName}:DownloadTimeoutMinutes must be between 1 and 240.");
        }
    }

    private static void RequireHttpUrl(Uri? url, string name)
    {
        if (url is null || !url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException($"{SectionName}:{name} must be an absolute http or https URL.");
        }
    }
}

/// <summary>
/// Where the module's own database lives (section <c>Osdu:Database</c>). Left empty, the <c>osdu</c> schema sits in the
/// catalog's database, which is what a control plane does unless a deployment gives the module a database of its own.
/// </summary>
public sealed class OsduDatabaseOptions
{
    /// <summary>The configuration section the control plane module binds this from.</summary>
    public const string SectionName = "Osdu:Database";

    /// <summary>
    /// A <c>${env:NAME}</c> or <c>${keyvault:NAME}</c> reference to the connection string of the database holding the
    /// <c>osdu</c> schema; never a connection string itself. Empty means the catalog's own database.
    /// </summary>
    public string? Connection { get; set; }
}
