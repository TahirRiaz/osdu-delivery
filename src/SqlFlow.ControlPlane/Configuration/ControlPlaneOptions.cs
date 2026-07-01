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

    public RateLimitOptions RateLimit { get; set; } = new();

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
/// routed to those pools.</summary>
public sealed class WorkerOptions
{
    public string[] Pools { get; set; } = [];
}

/// <summary>The managed git-to-catalog sync. <see cref="PollSeconds"/> is how often the control plane scans for
/// repo sources due to sync (each source has its own interval); lower it only for tests that need a fast tick.</summary>
public sealed class ManagedSyncOptions
{
    public int PollSeconds { get; set; } = 30;
}
