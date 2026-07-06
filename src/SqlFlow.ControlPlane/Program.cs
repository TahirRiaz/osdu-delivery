using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Background;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.ControlPlane.Infrastructure;
using SqlFlow.ControlPlane.Security;
using SqlFlow.Execution;
using SqlFlow.Node;

var builder = WebApplication.CreateBuilder(args);

// ---- Configuration (fail fast on a misconfigured deployment) -------------------------------------------------
var options = builder.Configuration.GetSection(ControlPlaneOptions.SectionName).Get<ControlPlaneOptions>() ?? new ControlPlaneOptions();
options.Validate();
builder.Services.AddOptions<ControlPlaneOptions>()
    .Bind(builder.Configuration.GetSection(ControlPlaneOptions.SectionName))
    .Validate(o => { try { o.Validate(); return true; } catch (InvalidOperationException) { return false; } },
        "ControlPlane configuration is invalid; see the eager startup validation message for the specific field.")
    .ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);

// ---- SqlFlow engine: the one shared composition (source readers, secret chain, connection registry, loaders,
// the flow runner, and the DocumentExecutor). The control plane runs flows through the exact same engine wiring as
// the CLI and worker nodes, so a triggered run behaves identically. This also provides the secret chain
// (IAzureCredentialFactory, the env + Key Vault providers, ISecretResolver) the catalog connection provider needs.
builder.Services.AddSqlFlowEngine();
builder.Services.AddSingleton<CatalogConnectionProvider>();
builder.Services.AddSingleton<TokenIssuer>();
builder.Services.AddSingleton<DeviceCodeStore>();

// ---- Identity: regular SQLFlow users (username + PBKDF2 password hash in the catalog) and Azure single sign-on
// (Entra ID token exchange with JIT provisioning). Both paths end in the same SQLFlow-issued token, so the API
// surface authorizes one token type no matter how the user signed in.
builder.Services.AddSingleton<IPasswordHasher<CatalogUser>, PasswordHasher<CatalogUser>>();
builder.Services.AddSingleton<LoginThrottle>();
if (options.AzureAd.Enabled)
{
    builder.Services.AddSingleton<IExternalTokenValidator, EntraTokenValidator>();
}

// ---- Bootstrap provisioning: migrations, built-in roles, the initial admin, and the optional demo repo source.
// Retries in the background until the catalog is reachable, so start order (app before database) never matters.
builder.Services.AddHostedService<BootstrapProvisioningService>();

// ---- Run queue: the catalog's Run table is a durable work queue. The dispatcher enqueues a run (references only:
// repo + flow name) and nudges the worker; the background worker atomically claims the oldest queued run, runs it
// through the shared DocumentExecutor, and records the outcome under the same id the trigger returned. Durability,
// ordering, and the queued/running/finished lifecycle live in the database, so runs survive a restart and are
// visible to the read API from the moment they are queued.
builder.Services.AddSingleton<RunQueueSignal>();
builder.Services.AddSingleton<InProcessRunDispatcher>();
builder.Services.AddSingleton<IRunDispatcher>(sp => sp.GetRequiredService<InProcessRunDispatcher>());
// The shared node runtime (also run standalone by `sqlflow worker`); hosted here as a background service that
// idles on the in-process nudge. API-only replicas (Worker:Enabled=false) skip hosting it so the HTTP tier can
// scale on request load behind an ingress while compute scales on queue depth as separate worker processes.
builder.Services.AddSingleton<RunWorker>();
if (options.Worker.Enabled)
{
    builder.Services.AddHostedService<RunExecutionWorker>();
}

// ---- Scheduler: scans the catalog for due cron/interval schedules and fires them by enqueuing onto the same
// queue (one run path). Schedules come from git (YAML, synced) and the API (ad-hoc / pause-resume); firing
// advances the next-fire atomically so multiple control-plane nodes never double-fire an occurrence.
builder.Services.AddHostedService<SchedulerService>();

// ---- Managed sync: keeps the shadow catalog current from git. A background service pulls each registered repo
// source's branch tip on its interval and runs the same catalog sync the CLI's `db sync` runs.
builder.Services.AddHostedService<RepoSyncService>();

// ---- Catalog read model: pooled, read-only, transient-retry --------------------------------------------------
builder.Services.AddDbContextPool<CatalogDbContext>((sp, db) =>
{
    var connectionString = sp.GetRequiredService<CatalogConnectionProvider>().ConnectionString;
    db.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure());
    db.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
});

// ---- AuthN / AuthZ -------------------------------------------------------------------------------------------
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Jwt.SigningKey!));
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(jwt =>
    {
        jwt.MapInboundClaims = false; // keep "sub"/"scope" claim types as issued
        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = options.Jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = options.Jwt.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            // Pin the algorithm so a token can never be validated under an unintended alg (no 'none', no
            // HS/RS confusion if an asymmetric key is introduced in the Identity phase).
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });

builder.Services.AddAuthorization(authz =>
{
    static bool HasScope(System.Security.Claims.ClaimsPrincipal user, string scope)
        => user.FindFirst("scope")?.Value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(scope) == true;

    authz.AddPolicy("read", policy => policy.RequireAuthenticatedUser());
    authz.AddPolicy("operate", policy => policy
        .RequireAuthenticatedUser()
        .RequireAssertion(context => HasScope(context.User, "operate")));
    authz.AddPolicy("admin", policy => policy
        .RequireAuthenticatedUser()
        .RequireAssertion(context => HasScope(context.User, "admin")));
});

// ---- Cross-cutting: problem details, OpenAPI, compression, health, rate limiting, CORS -----------------------
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddResponseCompression();
builder.Services.AddHealthChecks().AddDbContextCheck<CatalogDbContext>("catalog");

builder.Services.AddRateLimiter(rate =>
{
    rate.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    rate.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var partitionKey = context.User.FindFirst("sub")?.Value
            ?? context.Connection.RemoteIpAddress?.ToString()
            ?? "anonymous";
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = options.RateLimit.PermitPerWindow,
            Window = TimeSpan.FromSeconds(options.RateLimit.WindowSeconds),
            QueueLimit = 0,
        });
    });
});

const string corsPolicy = "ConfiguredOrigins";
var hasCors = options.Cors.AllowedOrigins.Length > 0;
if (hasCors)
{
    builder.Services.AddCors(cors => cors.AddPolicy(corsPolicy, policy => policy
        .WithOrigins(options.Cors.AllowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()));
}

var app = builder.Build();

// ---- Pipeline ------------------------------------------------------------------------------------------------
// Forwarded headers run FIRST: everything downstream (the rate limiter's per-IP partition, the login throttle,
// diagnostics) must see the real client address, not the ingress's. Only the configured proxies are trusted, so
// the header is never honored from an arbitrary client.
if (options.Proxy.Enabled)
{
    var forwarded = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        ForwardLimit = options.Proxy.ForwardLimit,
    };
    forwarded.KnownNetworks.Clear();
    forwarded.KnownProxies.Clear();
    foreach (var cidr in options.Proxy.KnownNetworks)
    {
        var (prefix, length) = ProxyOptions.ParseNetwork(cidr);
        forwarded.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, length));
    }

    foreach (var proxy in options.Proxy.KnownProxies)
    {
        forwarded.KnownProxies.Add(System.Net.IPAddress.Parse(proxy));
    }

    app.UseForwardedHeaders(forwarded);
}

app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseResponseCompression();
if (hasCors)
{
    app.UseCors(corsPolicy);
}

app.UseAuthentication();
app.UseRateLimiter(); // after authentication so the partition can key on the subject
app.UseAuthorization();

// Liveness has no dependencies; readiness probes the catalog database. Probes are exempt from the rate limiter
// so a shared egress IP's traffic can never throttle a liveness/readiness check into a false negative.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).DisableRateLimiting();
app.MapHealthChecks("/health/ready").DisableRateLimiting();
app.MapOpenApi();

// The device-authorization approval page (the verificationUri for the RFC 8628 grant). Served at the root so the
// URL advertised to a headless client resolves without a separate front-end deployment.
app.MapDeviceApprovalPage();

var v1 = app.MapGroup("/api/v1");

// The sign-in surface: providers discovery and local login are always available; the Entra token exchange is
// mapped when Azure SSO is enabled; the break-glass bootstrap token endpoint only when a secret is configured.
v1.MapAuthEndpoints(options);

// The authenticated read surface: repos/pipelines, runs, lineage, cross-repo search, and schedules.
v1.MapGroup(string.Empty).RequireAuthorization("read")
    .MapCatalogEndpoints()
    .MapRunEndpoints()
    .MapLineageEndpoints()
    .MapSearchEndpoints()
    .MapScheduleReadEndpoints()
    .MapNodeEndpoints()
    .MapRepoSourceReadEndpoints()
    .MapSummaryEndpoints();

// The operate surface: triggering/cancelling a run and managing schedules are privileged operations, so they live
// under the "operate" scope rather than the read group.
v1.MapGroup(string.Empty).RequireAuthorization("operate")
    .MapRunTriggerEndpoints()
    .MapScheduleWriteEndpoints()
    .MapRepoSourceWriteEndpoints();

// The admin surface: user and role administration requires the "admin" scope (the admin role, or a bootstrap
// token that requested it).
v1.MapGroup(string.Empty).RequireAuthorization("admin")
    .MapUserEndpoints();

app.Run();

/// <summary>Exposed so the integration tests can host the app through WebApplicationFactory.</summary>
public partial class Program;
