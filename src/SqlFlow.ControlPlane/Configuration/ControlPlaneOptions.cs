using System.Net;
using System.Text;

namespace SqlFlow.ControlPlane.Configuration;

/// <summary>
/// The bound configuration for the control plane (section <c>ControlPlane</c>). Every value comes from
/// configuration (env vars / appsettings); nothing security-relevant has an insecure default, and the required
/// fields are validated at startup (fail fast) by <see cref="Validate"/>.
/// </summary>
public sealed class ControlPlaneOptions
{
    public const string SectionName = "ControlPlane";

    public CatalogOptions Catalog { get; set; } = new();

    public JwtOptions Jwt { get; set; } = new();

    public CorsOptions Cors { get; set; } = new();

    public AzureAdOptions AzureAd { get; set; } = new();

    public BootstrapOptions Bootstrap { get; set; } = new();

    public RateLimitOptions RateLimit { get; set; } = new();

    public ProxyOptions Proxy { get; set; } = new();

    public SchedulerOptions Scheduler { get; set; } = new();

    public WorkerOptions Worker { get; set; } = new();

    public ReaperOptions Reaper { get; set; } = new();

    public ManagedSyncOptions ManagedSync { get; set; } = new();

    public NotificationOptions Notifications { get; set; } = new();

    public RunTraceRetentionOptions RunTrace { get; set; } = new();

    /// <summary>Validates the options, throwing a clear startup error for any missing or unsafe required value.
    /// Called during host build so a misconfigured deployment never starts serving.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Catalog.ConnectionReference))
        {
            throw new InvalidOperationException(
                "ControlPlane:Catalog:ConnectionReference is required (a connection string or a ${env:...}/${keyvault:...} reference; defaults to ${env:SQLFLOW_CATALOG_DB}).");
        }

        if (string.IsNullOrWhiteSpace(Jwt.Issuer) || string.IsNullOrWhiteSpace(Jwt.Audience))
        {
            throw new InvalidOperationException("ControlPlane:Jwt:Issuer and ControlPlane:Jwt:Audience are required.");
        }

        if (string.IsNullOrWhiteSpace(Jwt.SigningKey) || Encoding.UTF8.GetByteCount(Jwt.SigningKey) < 32)
        {
            throw new InvalidOperationException(
                "ControlPlane:Jwt:SigningKey is required and must be at least 32 bytes (256-bit) for HS256. Set it from a secret (env/Key Vault), never a literal in source.");
        }

        // The bootstrap secret is optional (its endpoint is only mapped when set), but when present it is a
        // credential a caller presents verbatim, so a guessable one is as dangerous as a weak signing key.
        if (!string.IsNullOrEmpty(Jwt.BootstrapSecret) && Encoding.UTF8.GetByteCount(Jwt.BootstrapSecret) < 32)
        {
            throw new InvalidOperationException(
                "ControlPlane:Jwt:BootstrapSecret, when set, must be at least 32 bytes (UTF-8). Set it from a secret (env/Key Vault), never a literal in source.");
        }

        if (Jwt.AccessTokenMinutes is < 1 or > 1440)
        {
            throw new InvalidOperationException("ControlPlane:Jwt:AccessTokenMinutes must be between 1 and 1440.");
        }

        if (Jwt.SessionMaxDays is < 1 or > 365)
        {
            throw new InvalidOperationException("ControlPlane:Jwt:SessionMaxDays must be between 1 and 365.");
        }

        if (RateLimit.PermitPerWindow < 1 || RateLimit.WindowSeconds < 1)
        {
            throw new InvalidOperationException("ControlPlane:RateLimit:PermitPerWindow and WindowSeconds must be positive.");
        }

        if (Scheduler.PollSeconds < 1)
        {
            throw new InvalidOperationException("ControlPlane:Scheduler:PollSeconds must be positive.");
        }

        if (Worker.MaxConcurrentRuns < 1)
        {
            throw new InvalidOperationException("ControlPlane:Worker:MaxConcurrentRuns must be at least 1.");
        }

        if (Worker.PollMilliseconds < 250)
        {
            throw new InvalidOperationException(
                "ControlPlane:Worker:PollMilliseconds must be at least 250, so a misconfigured value cannot spin the drain loop against the catalog.");
        }

        if (Reaper.PollSeconds < 1)
        {
            throw new InvalidOperationException("ControlPlane:Reaper:PollSeconds must be positive.");
        }

        if (Reaper.StaleAfterSeconds < 60)
        {
            throw new InvalidOperationException(
                "ControlPlane:Reaper:StaleAfterSeconds must be at least 60, comfortably larger than a node's heartbeat cadence, so a brief heartbeat gap never fails a live node's runs.");
        }

        if (Reaper.NodeRetentionHours < 0)
        {
            throw new InvalidOperationException(
                "ControlPlane:Reaper:NodeRetentionHours must be zero or positive (0 disables pruning stale nodes from the fleet registry).");
        }

        AzureAd.Validate();
        Bootstrap.Validate();
        Proxy.Validate();
        Notifications.Validate();
        RunTrace.Validate();
    }
}

/// <summary>How the control plane reaches the shadow catalog database.</summary>
public sealed class CatalogOptions
{
    /// <summary>A connection string or a secret reference (<c>${env:NAME}</c> / <c>${keyvault:vault/secret}</c>),
    /// resolved through the SqlFlow secret resolver. Defaults to <c>${env:SQLFLOW_CATALOG_DB}</c>.</summary>
    public string ConnectionReference { get; set; } = "${env:SQLFLOW_CATALOG_DB}";
}

/// <summary>JWT bearer settings. The signing key is symmetric (HS256) for the foundation; the Identity phase
/// replaces issuance with an asymmetric key / external provider without changing the validation contract.</summary>
public sealed class JwtOptions
{
    public string Issuer { get; set; } = "sqlflow-control-plane";

    public string Audience { get; set; } = "sqlflow";

    /// <summary>The HS256 signing key (>= 32 bytes). Required; sourced from a secret, never a literal.</summary>
    public string? SigningKey { get; set; }

    /// <summary>How long one issued session token is valid. A signed-in GUI rolls its token well before this lapses
    /// (<c>POST /auth/renew</c>), so this is not how long a user stays signed in: it is how long a leaked token keeps
    /// working, and how long a working day may pause before the session lapses on its own.</summary>
    public int AccessTokenMinutes { get; set; } = 720;

    /// <summary>The absolute ceiling on a rolled session: renewal is refused once this long has passed since the
    /// user actually authenticated, no matter how continuously they have used SQLFlow. Bounds "keep me signed in on
    /// this device" so it is a long convenience, not a permanent one.</summary>
    public int SessionMaxDays { get; set; } = 30;

    /// <summary>When set, the bootstrap token endpoint is enabled and issues a token for callers that present
    /// this exact secret. Left null in production once a real identity provider is in place (Identity phase);
    /// with it null the endpoint is not mapped at all.</summary>
    public string? BootstrapSecret { get; set; }
}

/// <summary>CORS allowlist. Empty means same-origin only (no cross-origin access granted).</summary>
public sealed class CorsOptions
{
    public string[] AllowedOrigins { get; set; } = [];
}

/// <summary>
/// Microsoft Entra ID single sign-on. When enabled, the SPA signs the user in against Entra (MSAL, auth code +
/// PKCE) and posts the resulting ID token to <c>POST /api/v1/auth/exchange</c>; the control plane validates it
/// against the tenant's published keys and issues its own SQLFlow token, so every API call downstream uses one
/// token type regardless of how the user signed in.
/// </summary>
public sealed class AzureAdOptions
{
    /// <summary>Explicit on/off override for Entra single sign-on. Left unset (the default), SSO is offered
    /// automatically whenever a tenant id and client id are configured, so a deployment turns it on simply by
    /// supplying those credentials, with no separate flag to remember. Set it to <c>true</c> to require SSO (a
    /// missing credential is then a startup error), or to <c>false</c> to force it off even when credentials are
    /// present (a kill switch). The effective state is <see cref="IsEnabled"/>.</summary>
    public bool? Enabled { get; set; }

    /// <summary>The directory (tenant) id of the Entra tenant whose users may sign in.</summary>
    public string? TenantId { get; set; }

    /// <summary>The app registration (client) id the SPA signs in with; the audience the ID token must carry.</summary>
    public string? ClientId { get; set; }

    /// <summary>The OIDC authority. Defaults to the public-cloud tenant authority
    /// (<c>https://login.microsoftonline.com/{TenantId}/v2.0</c>); override only for sovereign clouds.</summary>
    public string? Authority { get; set; }

    /// <summary>The role a first-time Entra user is provisioned with (least privilege by default; an admin raises
    /// it afterwards).</summary>
    public string DefaultRole { get; set; } = "viewer";

    /// <summary>Whether both credentials Entra needs (tenant id and client id) are configured, i.e. SSO CAN be
    /// offered. The auto-enable default keys on this.</summary>
    public bool HasCredentials
        => !string.IsNullOrWhiteSpace(TenantId) && !string.IsNullOrWhiteSpace(ClientId);

    /// <summary>Whether Entra single sign-on is actually offered: the explicit <see cref="Enabled"/> flag when set,
    /// otherwise on automatically when <see cref="HasCredentials"/> is true. Local username/password sign-in is
    /// always available regardless; this only gates the additive Microsoft option.</summary>
    public bool IsEnabled => Enabled ?? HasCredentials;

    /// <summary>The effective authority, with the public-cloud default applied.</summary>
    public string ResolveAuthority()
        => string.IsNullOrWhiteSpace(Authority)
            ? $"https://login.microsoftonline.com/{TenantId}/v2.0"
            : Authority.TrimEnd('/');

    public void Validate()
    {
        // An explicit request for SSO with nothing to sign in against is a misconfiguration, not a silent no-op:
        // fail fast and name the missing credentials. (Leaving Enabled unset instead auto-enables only when both
        // are present, so this never fires for the credential-driven default.)
        if (Enabled == true && !HasCredentials)
        {
            throw new InvalidOperationException(
                "ControlPlane:AzureAd:Enabled is true but TenantId and/or ClientId are missing. Provide both, or leave Enabled unset to enable SSO automatically only when the credentials are present.");
        }

        if (!IsEnabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(DefaultRole))
        {
            throw new InvalidOperationException("ControlPlane:AzureAd:DefaultRole must not be blank.");
        }
    }
}

/// <summary>
/// First-run provisioning. On startup the control plane (retrying until the catalog database is reachable)
/// applies pending catalog migrations, seeds the built-in roles, creates the initial admin user when configured
/// and absent, and registers the optional demo repo source. Everything is idempotent: a restart converges to the
/// same state and never overwrites operator changes (an existing admin's password is not reset).
/// </summary>
public sealed class BootstrapOptions
{
    /// <summary>Whether startup applies pending EF catalog migrations. Disable only when a DBA applies migration
    /// scripts out of band; pending migrations are then logged as a warning.</summary>
    public bool ApplyMigrations { get; set; } = true;

    /// <summary>Whether startup may CREATE the catalog database when it does not exist (and initialise the catalog
    /// schema into an empty database). Default <c>false</c>: startup only migrates an EXISTING catalog and refuses
    /// to conjure a database or inject catalog tables into a populated non-catalog database, so a wrong or mistyped
    /// connection fails loudly instead of provisioning against the wrong (possibly production) server. Set
    /// <c>true</c> only for first-time provisioning or ephemeral/test databases.</summary>
    public bool AllowCreate { get; set; }

    /// <summary>The initial admin's sign-in name. Set together with <see cref="AdminPasswordReference"/>.</summary>
    public string? AdminUsername { get; set; }

    /// <summary>The initial admin's password as a secret reference (<c>${env:...}</c> / <c>${keyvault:...}</c>),
    /// resolved through the SqlFlow secret resolver. Never a literal in source control.</summary>
    public string? AdminPasswordReference { get; set; }

    /// <summary>An optional repo source registered at bootstrap, so a fresh install has a synced estate to look at.</summary>
    public DemoRepoOptions? DemoRepo { get; set; }

    public void Validate()
    {
        var hasUsername = !string.IsNullOrWhiteSpace(AdminUsername);
        var hasPassword = !string.IsNullOrWhiteSpace(AdminPasswordReference);
        if (hasUsername != hasPassword)
        {
            throw new InvalidOperationException(
                "ControlPlane:Bootstrap:AdminUsername and ControlPlane:Bootstrap:AdminPasswordReference must be set together.");
        }

        if (DemoRepo is not null)
        {
            if (string.IsNullOrWhiteSpace(DemoRepo.Name) || string.IsNullOrWhiteSpace(DemoRepo.RemoteUrl))
            {
                throw new InvalidOperationException(
                    "ControlPlane:Bootstrap:DemoRepo requires both Name and RemoteUrl when configured.");
            }

            if (DemoRepo.SyncIntervalSeconds < 1)
            {
                throw new InvalidOperationException("ControlPlane:Bootstrap:DemoRepo:SyncIntervalSeconds must be positive.");
            }
        }
    }
}

/// <summary>The demo repo source bootstrap registers (same shape as an API registration).</summary>
public sealed class DemoRepoOptions
{
    public string? Name { get; set; }

    public string? RemoteUrl { get; set; }

    public string Branch { get; set; } = "main";

    public int SyncIntervalSeconds { get; set; } = 300;
}

/// <summary>The global fixed-window rate limit, partitioned per authenticated subject (or client IP when
/// anonymous), so one caller cannot exhaust the host.</summary>
public sealed class RateLimitOptions
{
    public int PermitPerWindow { get; set; } = 120;

    public int WindowSeconds { get; set; } = 60;
}

/// <summary>The schedule scanner cadence: how often the control plane checks the catalog for due schedules and
/// enqueues their runs. Cron schedules have minute granularity, so the default catches a due schedule within a few
/// seconds of its minute; lower it only for tests that need a fast tick.</summary>
public sealed class SchedulerOptions
{
    public int PollSeconds { get; set; } = 15;
}

/// <summary>The control plane's in-process worker. <see cref="Pools"/> are the run pools it serves: empty (the
/// default) means it claims only untargeted runs, which is the single-node default; set it to also drain runs
/// routed to those pools. <see cref="Enabled"/> turns the in-process worker off entirely for API-only replicas:
/// in a scaled deployment the HTTP tier scales on request load behind the ingress while compute scales on queue
/// depth as separate worker processes, and this switch is what keeps the two independent.</summary>
public sealed class WorkerOptions
{
    public bool Enabled { get; set; } = true;

    public string[] Pools { get; set; } = [];

    /// <summary>How many claimed runs this node executes at once. The queue's atomic claim already supports
    /// concurrent claimants, so this only sizes the node's own in-flight work; a saturated node stops claiming
    /// and leaves queued runs for other nodes. Minimum 1 (a strictly serial node).</summary>
    public int MaxConcurrentRuns { get; set; } = 4;

    /// <summary>The drain loop's poll fallback in milliseconds. A triggered run starts at once via the in-process
    /// nudge; this only bounds how long schedule- and other-node-enqueued runs (and recovery) wait to be picked
    /// up. Minimum 250, so a misconfigured value cannot spin-loop the worker against the catalog.</summary>
    public int PollMilliseconds { get; set; } = 2000;
}

/// <summary>The orphan-run reaper: the control plane sweeps for runs left <c>running</c> by a node that has stopped
/// heartbeating and fails them, releasing the run and the pipeline gate a dead node would otherwise hold forever.
/// <see cref="PollSeconds"/> is how often it sweeps. <see cref="StaleAfterSeconds"/> is how long a claiming node may
/// be silent before its runs are declared orphaned; it must be comfortably larger than a node's heartbeat cadence
/// (a few beats) so a transient catalog blip never fails a live node's work. The default gives several missed beats
/// of margin over the 60s fleet online window.</summary>
public sealed class ReaperOptions
{
    public int PollSeconds { get; set; } = 30;

    public int StaleAfterSeconds { get; set; } = 180;

    /// <summary>How long a node may be offline before the sweep prunes it from the fleet registry. Every worker pod
    /// registers under a fresh name (each orchestrator revision, each autoscale-up), and the registry never removes
    /// the ones that stopped heartbeating, so without a prune the fleet view grows a dead row per pod forever. The
    /// default keeps a day of history for debugging a recent failure while clearing the long-dead clutter; 0 disables
    /// the prune (rows then linger until deleted by hand).</summary>
    public int NodeRetentionHours { get; set; } = 24;
}

/// <summary>
/// Housekeeping for the per-run SQL trace. Every run persists its generated statements (the RunStatement table, a
/// full SQL blob per statement) and its events (RunEvent); across a busy estate these two tables grow without
/// bound and are almost never read once a run is old and succeeded. This prunes that detail on a cadence, keeping
/// only what stays useful: each pipeline's most recent run, every failed run (so the offending SQL is always there
/// to debug), any run still in flight, and anything still inside the grace window. The run header row itself (its
/// stats and its error message) is never touched, so the history and every failure reason stay complete and only
/// the heavy, unread detail is reclaimed. The manual GUI trigger runs the exact same prune on demand.
/// </summary>
public sealed class RunTraceRetentionOptions
{
    /// <summary>Turns the automatic pruning sweep off. Off means the two trace tables grow unbounded until someone
    /// triggers the manual cleanup, which still works. On by default. Note the sweep also does nothing while no
    /// retention period is set (that setting is operator-tunable from the GUI and defaults to "keep forever").</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the automatic prune sweep runs (when enabled and a retention period is set).</summary>
    public int PollSeconds { get; set; } = 3600;

    public void Validate()
    {
        if (PollSeconds < 1)
        {
            throw new InvalidOperationException("ControlPlane:RunTrace:PollSeconds must be positive.");
        }
    }
}

/// <summary>
/// Reverse-proxy awareness. Behind an ingress or load balancer the raw connection address is the proxy's, which
/// would collapse every caller into one rate-limit partition and one login-throttle key. When enabled, the
/// standard <c>X-Forwarded-For</c>/<c>X-Forwarded-Proto</c> headers are honored, but ONLY from the proxies listed
/// here (a client on the open internet can never spoof its address by sending the header itself). List the
/// ingress/LB addresses as CIDRs in <see cref="KnownNetworks"/> (for example <c>10.0.0.0/8</c> inside a cluster)
/// or as single IPs in <see cref="KnownProxies"/>.
/// </summary>
public sealed class ProxyOptions
{
    public bool Enabled { get; set; }

    /// <summary>Trusted proxy networks in CIDR form (for example <c>10.244.0.0/16</c>).</summary>
    public string[] KnownNetworks { get; set; } = [];

    /// <summary>Trusted individual proxy addresses (IPv4 or IPv6).</summary>
    public string[] KnownProxies { get; set; } = [];

    /// <summary>How many proxy hops to unwind from <c>X-Forwarded-For</c>; 1 for a single ingress (the default),
    /// 2 when a CDN sits in front of the ingress, and so on.</summary>
    public int ForwardLimit { get; set; } = 1;

    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (KnownNetworks.Length == 0 && KnownProxies.Length == 0)
        {
            throw new InvalidOperationException(
                "ControlPlane:Proxy is enabled but trusts no proxies; set KnownNetworks (CIDRs) and/or KnownProxies (IPs) to the ingress addresses. Trusting nothing would silently ignore the forwarded headers.");
        }

        if (ForwardLimit < 1)
        {
            throw new InvalidOperationException("ControlPlane:Proxy:ForwardLimit must be at least 1.");
        }

        foreach (var network in KnownNetworks)
        {
            ParseNetwork(network);
        }

        foreach (var proxy in KnownProxies)
        {
            if (!IPAddress.TryParse(proxy, out _))
            {
                throw new InvalidOperationException($"ControlPlane:Proxy:KnownProxies entry '{proxy}' is not a valid IP address.");
            }
        }
    }

    /// <summary>Parses a CIDR into its prefix and length, throwing a configuration error naming the bad entry.</summary>
    public static (IPAddress Prefix, int PrefixLength) ParseNetwork(string cidr)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cidr);
        var parts = cidr.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 2
            && IPAddress.TryParse(parts[0], out var prefix)
            && int.TryParse(parts[1], out var length)
            && length >= 0
            && length <= (prefix.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32))
        {
            return (prefix, length);
        }

        throw new InvalidOperationException(
            $"ControlPlane:Proxy:KnownNetworks entry '{cidr}' is not a valid CIDR (expected e.g. 10.0.0.0/8 or fd00::/8).");
    }
}

/// <summary>The managed git-to-catalog sync. <see cref="PollSeconds"/> is how often the control plane scans for
/// repo sources due to sync (each source has its own interval); lower it only for tests that need a fast tick.</summary>
public sealed class ManagedSyncOptions
{
    /// <summary>Whether this instance may claim managed syncs at all. On by default, but claiming additionally
    /// requires <see cref="WorkerOptions.Enabled"/>: the sync's lineage step reads object code from the
    /// referenced data-plane servers, and the estate provisions those credentials on worker-role instances (the
    /// same place pipelines execute), so an API-only replica never claims work it cannot complete. Turn this
    /// off for a local dev instance sharing the production catalog: the claim is queue-based, so a laptop that
    /// participates steals due syncs from the deployed estate and runs them with its own filesystem,
    /// credentials, and code version.</summary>
    public bool Enabled { get; set; } = true;

    public int PollSeconds { get; set; } = 30;

    /// <summary>Whether the managed sync runs the connected (derived) lineage tier: it opens each referenced SQL
    /// Server and expands module bodies (procedures, views) through the T-SQL parser, so a stored-procedure flow
    /// gains the reads/writes of the procedure it executes instead of appearing as an edgeless root. On by
    /// default so the estate's lineage is complete without an operator running <c>db sync --connect</c> by hand;
    /// the connection uses the source's own resolved secrets, and a connect failure is non-fatal (the offline
    /// tiers still land and the failure is recorded as a warning). Turn it off for a deployment whose control
    /// plane cannot reach the data-plane SQL Servers, so those syncs stay purely offline.</summary>
    public bool ConnectLineage { get; set; } = true;
}

/// <summary>
/// The notification pipeline: detects failed runs (and failed assertions on green runs) in the catalog and sends
/// opted-in users email and/or Slack messages, immediately (cooldown-coalesced) or as periodic digests. The
/// service itself always runs (it keeps the event stream current); a channel is offered to users only when its
/// section here is configured. Secrets (SMTP password, Graph client secret, Slack bot token) are references
/// (<c>${env:...}</c> / <c>${keyvault:...}</c>) resolved through the SqlFlow secret resolver, never literals.
/// </summary>
public sealed class NotificationOptions
{
    /// <summary>Turns the notification background service off entirely (nothing is detected or sent, and the
    /// subscription API reports every channel unavailable). On by default: with no channel configured the service
    /// idles harmlessly, so the default costs nothing until someone configures a channel and opts in.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The pipeline tick: how often detection, dispatch, and sending run. This bounds how quickly an
    /// "immediate" subscriber hears about a failure.</summary>
    public int PollSeconds { get; set; } = 30;

    /// <summary>How far behind the watermark each detection scan re-reads (absorbing writer clock skew and detail
    /// rows landing moments after their run row). Re-scanning is deduplicated, so a generous overlap is free.</summary>
    public int DetectionOverlapMinutes { get; set; } = 30;

    /// <summary>How many send attempts a message gets before it is recorded as permanently failed. Retries back
    /// off (1, 5, 15, then 60 minutes), so transient SMTP / Graph / Slack outages self-heal without spam.</summary>
    public int MaxDeliveryAttempts { get; set; } = 5;

    /// <summary>How long detected events are kept for audit ("what would have been notified") before pruning.</summary>
    public int EventRetentionDays { get; set; } = 30;

    /// <summary>How long sent / failed deliveries are kept as per-user history before pruning.</summary>
    public int DeliveryRetentionDays { get; set; } = 90;

    /// <summary>The GUI's public base URL (for example <c>https://sqlflow.example.com</c>), used to render "open
    /// this run" links in messages. When unset, messages carry no links.</summary>
    public string? GuiBaseUrl { get; set; }

    public EmailNotificationOptions Email { get; set; } = new();

    public SlackNotificationOptions Slack { get; set; } = new();

    /// <summary>Whether the email channel can actually send (a provider is configured).</summary>
    public bool EmailConfigured => !string.Equals(Email.Provider, EmailNotificationOptions.ProviderNone, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the Slack channel can actually send (a bot token reference is configured).</summary>
    public bool SlackConfigured => !string.IsNullOrWhiteSpace(Slack.BotTokenReference);

    public void Validate()
    {
        if (PollSeconds < 1)
        {
            throw new InvalidOperationException("ControlPlane:Notifications:PollSeconds must be positive.");
        }

        if (DetectionOverlapMinutes < 1)
        {
            throw new InvalidOperationException("ControlPlane:Notifications:DetectionOverlapMinutes must be positive.");
        }

        if (MaxDeliveryAttempts is < 1 or > 10)
        {
            throw new InvalidOperationException("ControlPlane:Notifications:MaxDeliveryAttempts must be between 1 and 10.");
        }

        if (EventRetentionDays < 1 || DeliveryRetentionDays < 1)
        {
            throw new InvalidOperationException(
                "ControlPlane:Notifications:EventRetentionDays and DeliveryRetentionDays must be positive.");
        }

        if (!string.IsNullOrWhiteSpace(GuiBaseUrl)
            && (!Uri.TryCreate(GuiBaseUrl, UriKind.Absolute, out var gui)
                || (gui.Scheme != Uri.UriSchemeHttp && gui.Scheme != Uri.UriSchemeHttps)))
        {
            throw new InvalidOperationException(
                "ControlPlane:Notifications:GuiBaseUrl must be an absolute http(s) URL when set.");
        }

        Email.Validate();
        Slack.Validate();
    }
}

/// <summary>The email channel. <see cref="Provider"/> picks the transport: <c>none</c> (email off, the default),
/// <c>smtp</c> (any SMTP relay, authenticated or not), or <c>graph</c> (Microsoft Graph <c>sendMail</c> as a
/// configured mailbox, the Microsoft 365 path that needs no SMTP relay at all).</summary>
public sealed class EmailNotificationOptions
{
    public const string ProviderNone = "none";
    public const string ProviderSmtp = "smtp";
    public const string ProviderGraph = "graph";

    public string Provider { get; set; } = ProviderNone;

    /// <summary>The From address for SMTP mail (Graph sends as its <see cref="GraphEmailOptions.SenderId"/>
    /// mailbox, but this is still used for display defaults). Required whenever a provider is configured.</summary>
    public string? FromAddress { get; set; }

    /// <summary>The display name shown next to the From address.</summary>
    public string FromDisplayName { get; set; } = "SQLFlow";

    public SmtpEmailOptions Smtp { get; set; } = new();

    public GraphEmailOptions Graph { get; set; } = new();

    public void Validate()
    {
        var provider = Provider?.Trim().ToLowerInvariant();
        if (provider is not (ProviderNone or ProviderSmtp or ProviderGraph))
        {
            throw new InvalidOperationException(
                "ControlPlane:Notifications:Email:Provider must be one of: none, smtp, graph.");
        }

        if (provider == ProviderNone)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(FromAddress) || !System.Net.Mail.MailAddress.TryCreate(FromAddress, out _))
        {
            throw new InvalidOperationException(
                "ControlPlane:Notifications:Email:FromAddress must be a valid email address when a provider is configured.");
        }

        if (provider == ProviderSmtp)
        {
            Smtp.Validate();
        }
        else
        {
            Graph.Validate();
        }
    }
}

/// <summary>An SMTP relay. Credentials are optional (an internal relay often authenticates by network) but must
/// be given as a pair; both are secret references resolved at send time, so rotation needs no restart.</summary>
public sealed class SmtpEmailOptions
{
    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    /// <summary>The transport security: <c>starttls</c> (default, port 587), <c>ssl</c> (implicit TLS, port 465),
    /// <c>auto</c> (opportunistic STARTTLS), or <c>none</c> (plain; only for an isolated internal relay).</summary>
    public string SslMode { get; set; } = "starttls";

    /// <summary>The SMTP username, or a secret reference to it; null for an unauthenticated relay.</summary>
    public string? UsernameReference { get; set; }

    /// <summary>The SMTP password as a secret reference; required exactly when a username is set.</summary>
    public string? PasswordReference { get; set; }

    public int TimeoutSeconds { get; set; } = 30;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            throw new InvalidOperationException(
                "ControlPlane:Notifications:Email:Smtp:Host is required when the email provider is smtp.");
        }

        if (Port is < 1 or > 65535)
        {
            throw new InvalidOperationException("ControlPlane:Notifications:Email:Smtp:Port must be between 1 and 65535.");
        }

        var mode = SslMode?.Trim().ToLowerInvariant();
        if (mode is not ("none" or "starttls" or "ssl" or "auto"))
        {
            throw new InvalidOperationException(
                "ControlPlane:Notifications:Email:Smtp:SslMode must be one of: none, starttls, ssl, auto.");
        }

        if (string.IsNullOrWhiteSpace(UsernameReference) != string.IsNullOrWhiteSpace(PasswordReference))
        {
            throw new InvalidOperationException(
                "ControlPlane:Notifications:Email:Smtp:UsernameReference and PasswordReference must be set together.");
        }

        if (TimeoutSeconds < 1)
        {
            throw new InvalidOperationException("ControlPlane:Notifications:Email:Smtp:TimeoutSeconds must be positive.");
        }
    }
}

/// <summary>
/// Microsoft Graph <c>sendMail</c>. The message is sent as the <see cref="SenderId"/> mailbox (the app needs the
/// application permission <c>Mail.Send</c>, ideally scoped to that mailbox with an application access policy).
/// Authentication uses the explicit app registration below when all three values are set; otherwise the ambient
/// SqlFlow Azure credential (managed identity in Azure, the shared <c>SQLFLOW_AZURE_AUTH</c> intent elsewhere).
/// </summary>
public sealed class GraphEmailOptions
{
    /// <summary>The sending mailbox: a user principal name (<c>alerts@contoso.com</c>) or object id.</summary>
    public string? SenderId { get; set; }

    public string? TenantId { get; set; }

    public string? ClientId { get; set; }

    /// <summary>The app's client secret as a secret reference; never a literal.</summary>
    public string? ClientSecretReference { get; set; }

    /// <summary>The Graph endpoint; override only for sovereign clouds.</summary>
    public string BaseUrl { get; set; } = "https://graph.microsoft.com/v1.0";

    /// <summary>The token scope requested for Graph; must match <see cref="BaseUrl"/>'s cloud.</summary>
    public string Scope { get; set; } = "https://graph.microsoft.com/.default";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SenderId))
        {
            throw new InvalidOperationException(
                "ControlPlane:Notifications:Email:Graph:SenderId (the sending mailbox) is required when the email provider is graph.");
        }

        var explicitApp = new[] { TenantId, ClientId, ClientSecretReference }.Count(v => !string.IsNullOrWhiteSpace(v));
        if (explicitApp is not (0 or 3))
        {
            throw new InvalidOperationException(
                "ControlPlane:Notifications:Email:Graph:TenantId, ClientId, and ClientSecretReference must be set together (or all omitted to use the ambient Azure credential).");
        }

        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("ControlPlane:Notifications:Email:Graph:BaseUrl must be an absolute https URL.");
        }

        if (string.IsNullOrWhiteSpace(Scope))
        {
            throw new InvalidOperationException("ControlPlane:Notifications:Email:Graph:Scope must not be blank.");
        }
    }
}

/// <summary>The Slack channel: proactive messages posted with a bot token (<c>chat:write</c>; direct messages by
/// email additionally need <c>users:read.email</c> and <c>im:write</c>). Configured when
/// <see cref="BotTokenReference"/> is set; the same Slack app the SqlFlow Slack assistant uses works here.</summary>
public sealed class SlackNotificationOptions
{
    /// <summary>The bot token (<c>xoxb-...</c>) as a secret reference; never a literal.</summary>
    public string? BotTokenReference { get; set; }

    /// <summary>The Slack Web API base; override only for testing.</summary>
    public string BaseUrl { get; set; } = "https://slack.com/api/";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(BotTokenReference))
        {
            return;
        }

        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("ControlPlane:Notifications:Slack:BaseUrl must be an absolute http(s) URL.");
        }
    }
}
