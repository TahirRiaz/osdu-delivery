using SqlFlow.Core.Identity;

namespace SqlFlow.Core.Acquire;

/// <summary>
/// A validated generic API-acquisition flow (<c>flowType: acq</c>): fetch from a third-party system and land the
/// <b>raw</b> response payloads as files in the data lake, one file per page/iteration, preserving the original
/// format byte-for-byte. It performs no CSV/flatten transform: downstream SQLFlow file flows (json/csv/xml) ingest
/// the landed files into SQL. This is the consolidation of the estate's Azure Automation runbooks into one
/// declarative, testable, schedulable engine.
/// </summary>
public sealed record AcquireFlow
{
    public required string Name { get; init; }

    /// <summary>Stable, system-generated identity derived from <see cref="Name"/> (never authored in YAML).</summary>
    public Guid FlowId => FlowIdentity.FromName(Name);

    /// <summary>The batch (source system) grouping label under which this flow's runs report.</summary>
    public string? Batch { get; init; }

    public required AcquireSource Source { get; init; }

    /// <summary>
    /// The single landing sink, used by a flow that fetches one stream into one raw location. Mutually exclusive with
    /// <see cref="Items"/>: a flow declares either this or a non-empty <see cref="Items"/> list, never both. Null when
    /// the flow is multi-item.
    /// </summary>
    public AcquireLanding? Landing { get; init; }

    /// <summary>
    /// Multiple landing sinks driven off the one shared <see cref="Source"/>. Each item overlays its own transport
    /// <see cref="AcquireItem.Options"/> (e.g. a distinct S3 <c>prefix</c>) onto the source's options and lands into
    /// its own <see cref="AcquireItem.Landing"/>. This folds what would otherwise be N near-identical single-landing
    /// acq files (same bucket/credentials, different prefix and target) into one flow. Empty for a single-landing flow.
    /// </summary>
    public IReadOnlyList<AcquireItem> Items { get; init; } = [];

    /// <summary>
    /// The normalized landing units the engine runs: the explicit <see cref="Items"/> when present, otherwise the
    /// single top-level <see cref="Landing"/> wrapped as one item with no option overlay. The loader guarantees one
    /// of the two is set, so a single-landing flow and a multi-item flow drive the exact same engine loop.
    /// </summary>
    public IReadOnlyList<AcquireItem> EffectiveItems =>
        Items.Count > 0
            ? Items
            : [new AcquireItem { Landing = Landing ?? throw new InvalidOperationException($"acquire flow '{Name}' has neither a 'landing' nor 'items'.") }];

    /// <summary>Incremental resume settings; null means the flow fetches its full declared window every run.</summary>
    public AcquireIncremental? Incremental { get; init; }

    /// <summary>
    /// Declared runtime parameters: name to default value. Each becomes a template variable (<c>{name}</c>)
    /// usable anywhere templates render (request path/query/headers/body, iteration bounds, landing paths).
    /// A caller (the CLI, the control plane trigger, or the debugger) can override any of them per run; a
    /// parameter declared with a null default has no fallback and must be supplied at run time.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Params { get; init; }
        = new Dictionary<string, string?>(StringComparer.Ordinal);
}

/// <summary>
/// One landing sink of a multi-item acquisition flow: a per-item overlay of transport options applied on top of the
/// flow's shared <see cref="AcquireSource.Options"/> (typically just the S3 <c>prefix</c> / SFTP path that selects
/// this item's objects), plus the raw location those objects land in. The shared source (bucket, credentials, region,
/// modified-within window, iterations, reliability) is authored once on the flow; only what differs per item lives here.
/// </summary>
public sealed record AcquireItem
{
    /// <summary>Transport options overlaid onto <see cref="AcquireSource.Options"/> for this item (item keys win).
    /// Empty for the wrapped single-landing case.</summary>
    public IReadOnlyDictionary<string, string?> Options { get; init; }
        = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where this item's fetched payloads land.</summary>
    public required AcquireLanding Landing { get; init; }
}

/// <summary>The transport a source speaks.</summary>
public enum AcquireTransport
{
    /// <summary>HTTP(S): REST, GraphQL, SOAP-over-POST. The dominant transport.</summary>
    Http,

    /// <summary>SFTP file download.</summary>
    Sftp,

    /// <summary>AWS S3 object download.</summary>
    S3,

    /// <summary>Azure Storage Table entity query.</summary>
    AzureTable,
}

/// <summary>
/// The source side of an acquisition flow: which transport, where, how to authenticate, what to request, how to
/// page, how to fan out, and the reliability envelope. HTTP uses <see cref="Auth"/>/<see cref="Request"/>/
/// <see cref="Pagination"/>; the file/table transports use <see cref="Transport"/>-specific options carried in
/// <see cref="Options"/> and the shared <see cref="Iterations"/>/<see cref="Reliability"/>.
/// </summary>
public sealed record AcquireSource
{
    public AcquireTransport Transport { get; init; } = AcquireTransport.Http;

    /// <summary>
    /// The base endpoint: an HTTP(S) origin (e.g. <c>https://api.acme.com</c>), an <c>sftp://host[:port]</c> URL,
    /// an <c>s3://bucket</c> URL, or the Azure Table account URL. Interpreted per <see cref="Transport"/>.
    /// </summary>
    public required string BaseUrl { get; init; }

    /// <summary>HTTP authentication. Ignored by non-HTTP transports, which carry credentials in <see cref="Options"/>.</summary>
    public AcquireAuth Auth { get; init; } = new() { Type = AcquireAuthType.None };

    /// <summary>The HTTP request template. Required for the HTTP transport.</summary>
    public AcquireRequest? Request { get; init; }

    /// <summary>HTTP pagination strategy. Defaults to a single page.</summary>
    public AcquirePagination Pagination { get; init; } = new();

    /// <summary>
    /// Fan-out iterations applied outer-to-inner. Each combination of iteration values produces one request
    /// pipeline (which may itself page). Empty means a single request pipeline.
    /// </summary>
    public IReadOnlyList<AcquireIteration> Iterations { get; init; } = [];

    public AcquireReliability Reliability { get; init; } = new();

    /// <summary>Transport-specific options (SFTP/S3/Table credentials and locators, secret references included).</summary>
    public IReadOnlyDictionary<string, string?> Options { get; init; }
        = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
}

// ---------------------------------------------------------------------------------------------------------------
// Authentication
// ---------------------------------------------------------------------------------------------------------------

/// <summary>The HTTP authentication strategy. Secret material is always a <c>${...}</c> reference, never inline.</summary>
public enum AcquireAuthType
{
    None,

    /// <summary>A static secret sent in a request header (name from <see cref="AcquireAuth.HeaderName"/>).</summary>
    ApiKeyHeader,

    /// <summary>A static secret sent as a query parameter (name from <see cref="AcquireAuth.ParamName"/>).</summary>
    ApiKeyQuery,

    /// <summary><c>Authorization: &lt;prefix&gt;&lt;token&gt;</c> with a static token secret (prefix default <c>Bearer </c>).</summary>
    Bearer,

    /// <summary>HTTP Basic with a username/password pair of secret references.</summary>
    Basic,

    /// <summary>OAuth2 client-credentials grant against <see cref="AcquireAuth.Token"/>.</summary>
    OAuth2ClientCredentials,

    /// <summary>OAuth2 refresh-token exchange against <see cref="AcquireAuth.Token"/>.</summary>
    OAuth2RefreshToken,

    /// <summary>
    /// A generic token exchange: POST <see cref="AcquireAuth.Token"/> and read the access token out of the JSON
    /// response by path. Covers the Kolumbus-style pre-formed-form-body exchange and any bespoke token endpoint.
    /// </summary>
    TokenExchange,
}

/// <summary>Where a token endpoint is discovered from.</summary>
public sealed record AcquireAuth
{
    public required AcquireAuthType Type { get; init; }

    /// <summary>Primary secret reference: the token (Bearer/api-key), the password (Basic), or the client secret / refresh token (OAuth).</summary>
    public string? SecretRef { get; init; }

    /// <summary>Secondary secret reference: the username (Basic) or the client id (OAuth) when carried as a secret.</summary>
    public string? SecondarySecretRef { get; init; }

    /// <summary>Header name for <see cref="AcquireAuthType.ApiKeyHeader"/> (e.g. <c>X-Api-Key</c>, <c>X-Authorization</c>).</summary>
    public string? HeaderName { get; init; }

    /// <summary>Query-parameter name for <see cref="AcquireAuthType.ApiKeyQuery"/> (e.g. <c>token</c>).</summary>
    public string? ParamName { get; init; }

    /// <summary>The prefix prepended to a Bearer / api-key header value. Default <c>Bearer </c> for Bearer, empty otherwise.</summary>
    public string? ValuePrefix { get; init; }

    /// <summary>The token-endpoint description for the OAuth / exchange auth types.</summary>
    public AcquireTokenEndpoint? Token { get; init; }
}

/// <summary>How to obtain and apply an access token for the OAuth / token-exchange auth types.</summary>
public sealed record AcquireTokenEndpoint
{
    /// <summary>The token endpoint URL. When <see cref="DiscoveryUrl"/> is set this is resolved from OIDC discovery instead.</summary>
    public string? Url { get; init; }

    /// <summary>An OIDC <c>.well-known/openid-configuration</c> URL whose <c>token_endpoint</c> supplies <see cref="Url"/>.</summary>
    public string? DiscoveryUrl { get; init; }

    /// <summary>HTTP method for the token request. Default <c>POST</c>.</summary>
    public string Method { get; init; } = "POST";

    /// <summary>How the token request body is encoded: <c>form</c> (urlencoded) or <c>json</c>.</summary>
    public AcquireBodyKind BodyKind { get; init; } = AcquireBodyKind.Form;

    /// <summary>
    /// The token-request body fields (e.g. <c>grant_type</c>, <c>client_id</c>, <c>scope</c>, <c>audience</c>,
    /// <c>refreshToken</c>). Values may be <c>${...}</c> secret references and <c>{token}</c> templates.
    /// </summary>
    public IReadOnlyDictionary<string, string> Body { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// A pre-formed body sent verbatim (e.g. a Key Vault secret that already holds the full urlencoded form). When
    /// set, <see cref="Body"/> is ignored. Resolved for <c>${...}</c> references before sending.
    /// </summary>
    public string? RawBody { get; init; }

    /// <summary>Extra headers on the token request (e.g. an explicit <c>Content-Type</c>).</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Send the client id/secret as an HTTP Basic header on the token request rather than in the body.</summary>
    public bool BasicAuthClient { get; init; }

    /// <summary>The JSON path in the token response holding the access token. Default <c>access_token</c>.</summary>
    public string TokenPath { get; init; } = "access_token";

    /// <summary>How the resolved token is applied to data requests: as a Bearer header (default) or a named header.</summary>
    public string? ApplyHeaderName { get; init; }

    /// <summary>The prefix on the applied token header value. Default <c>Bearer </c>.</summary>
    public string ApplyPrefix { get; init; } = "Bearer ";

    /// <summary>Re-acquire the token for every outer iteration rather than once per run (some issuers scope tokens per window).</summary>
    public bool RefreshPerIteration { get; init; }
}

// ---------------------------------------------------------------------------------------------------------------
// Request
// ---------------------------------------------------------------------------------------------------------------

/// <summary>How a request body is encoded.</summary>
public enum AcquireBodyKind
{
    None,

    /// <summary>application/json.</summary>
    Json,

    /// <summary>application/x-www-form-urlencoded.</summary>
    Form,

    /// <summary>A GraphQL query, wrapped as <c>{ "query": ..., "variables": ... }</c> and sent as JSON.</summary>
    GraphQl,

    /// <summary>A SOAP envelope sent as text/xml.</summary>
    Soap,

    /// <summary>An opaque body sent with an explicit Content-Type.</summary>
    Raw,
}

/// <summary>The HTTP request template. Path/query/header/body values support <c>{token}</c> substitution from the
/// per-request context (iteration variables, the watermark, and built-in date tokens).</summary>
public sealed record AcquireRequest
{
    public string Method { get; init; } = "GET";

    /// <summary>The path (and optional inline query) appended to the source base URL. Supports <c>{placeholder}</c> tokens.</summary>
    public string Path { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> Query { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public AcquireBodyKind BodyKind { get; init; } = AcquireBodyKind.None;

    /// <summary>The literal/templated body for JSON/GraphQL/SOAP/Raw bodies.</summary>
    public string? Body { get; init; }

    /// <summary>Body fields for a <see cref="AcquireBodyKind.Form"/> body.</summary>
    public IReadOnlyDictionary<string, string> BodyFields { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Explicit Content-Type for a <see cref="AcquireBodyKind.Raw"/> body.</summary>
    public string? ContentType { get; init; }
}

// ---------------------------------------------------------------------------------------------------------------
// Pagination
// ---------------------------------------------------------------------------------------------------------------

public enum AcquirePaginationStrategy
{
    /// <summary>A single request.</summary>
    None,

    /// <summary>Increment a page-number query param until an empty page or <see cref="AcquirePagination.MaxPages"/>.</summary>
    Page,

    /// <summary>Advance an offset by a fixed limit until an empty page or the page cap.</summary>
    Offset,

    /// <summary>Follow a cursor read out of the JSON response body until it is absent.</summary>
    CursorBody,

    /// <summary>Follow the RFC 5988 <c>Link: &lt;url&gt;; rel="next"</c> response header until it is absent.</summary>
    LinkHeader,

    /// <summary>
    /// Keyset pagination on a monotonically increasing id: send <c>idAfter</c> starting from the max already landed
    /// (or a configured seed), advance to the max id observed in each page, stop on a terminal status or an empty page.
    /// </summary>
    Keyset,
}

public sealed record AcquirePagination
{
    public AcquirePaginationStrategy Strategy { get; init; } = AcquirePaginationStrategy.None;

    /// <summary>Runaway-loop guard: the maximum number of pages fetched per request pipeline. Default 50.</summary>
    public int MaxPages { get; init; } = 50;

    // Page strategy
    public string PageParam { get; init; } = "page";
    public int StartPage { get; init; } = 1;

    // Offset strategy
    public string OffsetParam { get; init; } = "offset";
    public string LimitParam { get; init; } = "limit";
    public int Limit { get; init; } = 100;

    // Cursor-body strategy
    public string CursorParam { get; init; } = "cursor";

    /// <summary>The JSON path the next cursor / next id is read from (cursor-body and keyset strategies).</summary>
    public string? CursorPath { get; init; }

    // Keyset strategy
    public string KeysetParam { get; init; } = "idAfter";

    /// <summary>The JSON path (relative to each record) of the id advanced by keyset pagination.</summary>
    public string? KeysetIdPath { get; init; }

    /// <summary>An HTTP status that terminates keyset/page pagination without being an error (e.g. 202 "no more").</summary>
    public int? StopOnStatus { get; init; }

    /// <summary>The JSON path to the record array used to detect an empty page (default: whole body is the array).</summary>
    public string? RecordsPath { get; init; }
}

// ---------------------------------------------------------------------------------------------------------------
// Iteration (fan-out)
// ---------------------------------------------------------------------------------------------------------------

public enum AcquireIterationKind
{
    /// <summary>Walk a date window in fixed steps, binding a from/to variable pair per step.</summary>
    DateWindow,

    /// <summary>Iterate a static list of values, binding one variable per element.</summary>
    List,

    /// <summary>Fetch a list of ids from a prior request and iterate (optionally batched) over them.</summary>
    IdsFrom,
}

public enum AcquireWindowGranularity
{
    Hour,
    Day,
    Month,
}

/// <summary>One fan-out dimension. The engine expands the cartesian product of all iterations (outer-to-inner) and
/// runs one request pipeline per combination, binding each iteration's variables into the request context.</summary>
public sealed record AcquireIteration
{
    public required AcquireIterationKind Kind { get; init; }

    /// <summary>The context variable this iteration binds (for <see cref="AcquireIterationKind.List"/> /
    /// <see cref="AcquireIterationKind.IdsFrom"/>), e.g. <c>operatorId</c> or <c>bikeId</c>.</summary>
    public string? Variable { get; init; }

    // --- DateWindow ---
    public AcquireWindowGranularity Granularity { get; init; } = AcquireWindowGranularity.Day;

    /// <summary>Inclusive window start. Relative expressions (<c>now-3d</c>, <c>yesterday</c>, <c>startOfMonth</c>) or an ISO date. When omitted, resolved from the incremental watermark or defaults.</summary>
    public string? From { get; init; }

    /// <summary>Exclusive window end. Relative expressions or an ISO date. Default <c>now</c>.</summary>
    public string? To { get; init; }

    /// <summary>The context variable bound to each step's lower bound (default <c>window.from</c>).</summary>
    public string FromVariable { get; init; } = "window.from";

    /// <summary>The context variable bound to each step's upper bound (default <c>window.to</c>).</summary>
    public string ToVariable { get; init; } = "window.to";

    // --- List ---
    public IReadOnlyList<string> Values { get; init; } = [];

    // --- IdsFrom ---
    /// <summary>The request that produces the id list (a full request template).</summary>
    public AcquireRequest? IdRequest { get; init; }

    /// <summary>The JSON path selecting the ids from the id request's response, e.g. <c>$[*].BikeId</c>.</summary>
    public string? IdPath { get; init; }

    /// <summary>When &gt; 1, ids are grouped into batches of this size and the variable is bound to a joined batch.</summary>
    public int BatchSize { get; init; } = 1;

    /// <summary>The separator joining a batch of ids into the bound variable (default comma).</summary>
    public string BatchSeparator { get; init; } = ",";
}

// ---------------------------------------------------------------------------------------------------------------
// Reliability
// ---------------------------------------------------------------------------------------------------------------

public sealed record AcquireReliability
{
    /// <summary>Per-request timeout. Default 100s.</summary>
    public int TimeoutSeconds { get; init; } = 100;

    public AcquireRetry Retry { get; init; } = new();

    /// <summary>Outbound request rate cap (requests/second) via a token bucket. 0 or less means unlimited.</summary>
    public double RateLimitRps { get; init; }

    /// <summary>Hard cap on a single response held in memory before landing. Default 500 MB.</summary>
    public long MaxResponseBytes { get; init; } = 500L * 1024 * 1024;

    public bool VerifyTls { get; init; } = true;

    /// <summary>SSRF host allowlist. Empty means "any public host" (private / loopback / link-local / metadata IPs are always blocked).</summary>
    public IReadOnlyList<string> UrlAllowlist { get; init; } = [];
}

public sealed record AcquireRetry
{
    public int MaxAttempts { get; init; } = 4;
    public int BaseDelayMs { get; init; } = 500;
    public int MaxDelayMs { get; init; } = 30_000;

    /// <summary>Honor a server <c>Retry-After</c> header as a floor on the backoff (bounded by <see cref="MaxDelayMs"/>).</summary>
    public bool HonorRetryAfter { get; init; } = true;
}

// ---------------------------------------------------------------------------------------------------------------
// Landing (raw sink)
// ---------------------------------------------------------------------------------------------------------------

public enum AcquireCompression
{
    None,
    Gzip,
}

/// <summary>How a fetched payload is written to the raw zone, preserving its original format.</summary>
public sealed record AcquireLanding
{
    /// <summary>
    /// The raw-zone base location: an ADLS Gen2 / Blob URI (<c>abfss://fs@account.dfs.core.windows.net/base</c> or
    /// the https form) or a local/UNC path. The per-run/per-page path is composed under it.
    /// </summary>
    public required string Target { get; init; }

    /// <summary>
    /// The relative path (under <see cref="Target"/>) for a fetched payload, minus extension. Supports date tokens
    /// (<c>{yyyy}</c>, <c>{MM}</c>, <c>{dd}</c>, <c>{yyyyMMdd}</c>), iteration variables (<c>{operatorId}</c>,
    /// <c>{window.from}</c>), and <c>{page}</c>/<c>{runId}</c>. When it does not vary per page, the page index is
    /// appended so pages never collide.
    /// </summary>
    public required string PathTemplate { get; init; }

    /// <summary>The output format: <c>auto</c> (derive extension from the response content-type / declared format),
    /// or an explicit <c>json</c>/<c>xml</c>/<c>csv</c>/<c>txt</c>/<c>bin</c>.</summary>
    public string Format { get; init; } = "auto";

    public AcquireCompression Compression { get; init; } = AcquireCompression.None;

    /// <summary>Overwrite an existing landed file; when false, a run that would collide fails rather than clobber.</summary>
    public bool Overwrite { get; init; } = true;

    /// <summary>Also write a <c>&lt;file&gt;.headers.json</c> sidecar of the (redacted) response headers per page.</summary>
    public bool PersistHeaders { get; init; }

    /// <summary>Skip landing a payload whose record array is empty (or whose body is empty), rather than writing a zero-row file.</summary>
    public bool SkipEmpty { get; init; } = true;

    /// <summary>Skip rewriting a landed file whose target already holds byte-identical content, so a fetch that
    /// returned the same file does not bump its last-modified time and re-trigger downstream ingestion. On by default.
    /// Turn it off to write every fetched payload unconditionally, avoiding the per-file hash comparison. Has no effect
    /// when <see cref="Overwrite"/> is false.</summary>
    public bool SkipUnchanged { get; init; } = true;
}

// ---------------------------------------------------------------------------------------------------------------
// Incremental
// ---------------------------------------------------------------------------------------------------------------

public enum AcquireWatermarkSource
{
    /// <summary>Derive the resume point by inspecting what is already landed in the lake (max id / latest partition).</summary>
    Lake,

    /// <summary>Track the max of a response field across a run and persist it in the run record for the next run.</summary>
    Response,
}

/// <summary>Incremental resume configuration for keyset/date-window flows.</summary>
public sealed record AcquireIncremental
{
    public AcquireWatermarkSource Source { get; init; } = AcquireWatermarkSource.Response;

    /// <summary>The response field whose max is the watermark (response source), e.g. <c>updated_at</c> or <c>reportId</c>.</summary>
    public string? Column { get; init; }

    /// <summary>The context variable the resolved watermark is bound to (so the request/iteration can send it), e.g. <c>idAfter</c>.</summary>
    public string? BindVariable { get; init; }

    /// <summary>A seed used on the first run when no prior watermark exists.</summary>
    public string? Seed { get; init; }
}
