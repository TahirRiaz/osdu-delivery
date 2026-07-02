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

    public ManagedSyncOptions ManagedSync { get; set; } = new();

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

        if (RateLimit.PermitPerWindow < 1 || RateLimit.WindowSeconds < 1)
        {
            throw new InvalidOperationException("ControlPlane:RateLimit:PermitPerWindow and WindowSeconds must be positive.");
        }

        if (Scheduler.PollSeconds < 1)
        {
            throw new InvalidOperationException("ControlPlane:Scheduler:PollSeconds must be positive.");
        }

        AzureAd.Validate();
        Bootstrap.Validate();
        Proxy.Validate();
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

    public int AccessTokenMinutes { get; set; } = 60;

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
    public bool Enabled { get; set; }

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

    /// <summary>The effective authority, with the public-cloud default applied.</summary>
    public string ResolveAuthority()
        => string.IsNullOrWhiteSpace(Authority)
            ? $"https://login.microsoftonline.com/{TenantId}/v2.0"
            : Authority.TrimEnd('/');

    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(TenantId) || string.IsNullOrWhiteSpace(ClientId))
        {
            throw new InvalidOperationException(
                "ControlPlane:AzureAd:TenantId and ControlPlane:AzureAd:ClientId are required when AzureAd is enabled.");
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
    public int PollSeconds { get; set; } = 30;
}
