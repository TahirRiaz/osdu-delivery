using System.Text.Json.Serialization;

namespace SqlFlow.Cli.Remote;

// The CLI's client-side mirror of the control plane's wire contracts. The CLI deliberately does not reference
// the SqlFlow.ControlPlane project (an ASP.NET Core host); these records ARE the protocol boundary, kept
// field-for-field with the server records they mirror (see src/SqlFlow.ControlPlane/Api). Everything is
// camelCase JSON on the wire (ASP.NET's web defaults), deserialized case-insensitively here; the device token
// response alone follows the OAuth snake_case convention and pins its names explicitly.

/// <summary>The server's page envelope for every list endpoint.</summary>
internal sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long Total);

/// <summary>A synced source repository, as <c>GET /api/v1/repos</c> lists them.</summary>
internal sealed record RepoDto(
    Guid Id, string Name, string? RemoteUrl, string? RootPath, DateTime FirstSeenUtc, DateTime LastSyncUtc);

/// <summary>A local sign-in request (<c>POST /api/v1/auth/login</c>).</summary>
internal sealed record LoginRequest(string Username, string Password);

/// <summary>A signed-in session: the bearer token plus the identity facts.</summary>
internal sealed record SessionResponse(
    string AccessToken, string TokenType, int ExpiresIn, string Subject, string Role, IReadOnlyList<string> Scopes);

/// <summary>Starts the RFC 8628 device grant (<c>POST /api/v1/auth/device</c>).</summary>
internal sealed record DeviceAuthorizationRequest(string? ClientId, string? Scope);

/// <summary>The device-authorization response: the codes, the approval URL, and polling hints.</summary>
internal sealed record DeviceAuthorizationResponse(
    string DeviceCode, string UserCode, string VerificationUri, string VerificationUriComplete, int ExpiresIn, int Interval);

/// <summary>Request body for <c>POST /api/v1/auth/device/token</c>.</summary>
internal sealed record DeviceTokenRequest(string DeviceCode);

/// <summary>The minted device token; field names follow the OAuth token-response convention (snake_case).</summary>
internal sealed record DeviceTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("scope")] string Scope);

/// <summary>The RFC 8628 polling error envelope: <c>authorization_pending</c>, <c>slow_down</c>,
/// <c>access_denied</c>, <c>expired_token</c>, or <c>invalid_request</c>.</summary>
internal sealed record DeviceErrorResponse([property: JsonPropertyName("error")] string Error);

/// <summary>A personal access token as the listing shows it: never the secret, never the hash.</summary>
internal sealed record AccessTokenDto(
    Guid Id, string Name, string Prefix, IReadOnlyList<string> Scopes, DateTime CreatedUtc,
    DateTime? ExpiresUtc, DateTime? LastUsedUtc, DateTime? RevokedUtc);

/// <summary>Creates a personal access token (<c>POST /api/v1/me/tokens</c>); scopes are capped server-side to
/// the caller's own, and a null expiry means the token never expires.</summary>
internal sealed record CreateAccessTokenRequest(string Name, IReadOnlyList<string>? Scopes, int? ExpiresInDays);

/// <summary>The one-time response to creating a token: the listing view plus the never-again-retrievable
/// secret.</summary>
internal sealed record CreatedAccessTokenDto(AccessTokenDto Token, string Secret);

/// <summary>The body that triggers a run (<c>POST /api/v1/runs</c>): references only, never a secret.</summary>
internal sealed record RunTriggerRequest(
    Guid RepoId, string FlowName, string? Pool = null, string? CommitSha = null,
    bool FullLoad = false, DateTime? BackfillFrom = null, DateTime? BackfillTo = null, string? FilePattern = null,
    string? Scope = null, string? Batch = null);

/// <summary>The accepted-run acknowledgement for a single-flow trigger.</summary>
internal sealed record RunTriggerAccepted(Guid RunId, string Status);

/// <summary>The accepted-group acknowledgement for a node/batch trigger.</summary>
internal sealed record RunGroupAccepted(Guid GroupId, int MemberCount, string Status);

/// <summary>One flow a scope expansion would run, with the wave that orders it.</summary>
internal sealed record RunScopePreviewMemberDto(string FlowName, string FlowKind, int Wave);

/// <summary>What a node or batch trigger would enqueue, without enqueuing anything.</summary>
internal sealed record RunScopePreviewDto(
    string Scope, string Anchor, int MemberCount, int WaveCount, IReadOnlyList<RunScopePreviewMemberDto> Members);

/// <summary>A run as it appears in lists.</summary>
internal sealed record RunSummaryDto(
    Guid RunId, Guid PipelineId, Guid? RepoId, string FlowName, string FlowKind, string Batch, int Wave,
    string Status, bool Success,
    string? TargetPool, string? CommitSha, DateTime WrittenUtc, DateTime? EnqueuedUtc, double? DurationSeconds,
    long? RowsLoaded, long? RowsInserted, long? RowsUpdated, long? RowsDeleted, int FileCount, Guid? GroupId,
    string? LastAction, DateTime? LastActionUtc);

/// <summary>One run with its full header for the detail view.</summary>
internal sealed record RunDetailDto(
    Guid RunId, Guid PipelineId, Guid? RepoId, string FlowName, string FlowKind, string Batch, int Wave,
    string Status, bool Success,
    string? TargetPool, string? CommitSha, DateTime? EnqueuedUtc, string? ClaimedByNode, DateTime? CancelRequestedUtc,
    int SchemaVersion, DateTime WrittenUtc, DateTime? StartUtc, DateTime? EndUtc, double? DurationSeconds,
    long? RowsLoaded, long? RowsInserted, long? RowsUpdated, long? RowsDeleted, int FileCount, string? Error, string? Host,
    bool FullLoad, DateTime? BackfillFrom, DateTime? BackfillTo, string? FilePattern,
    string? IncrementalMode, string? IncrementalFilter, string? IncrementalWatermark, string? IncrementalWatermarkSource,
    int? FailedStatementOrdinal, string? FailedStatementStep, string? FailedStatementSql, Guid? GroupId);

/// <summary>One file a run processed (file flows).</summary>
internal sealed record RunFileDto(
    long Id, Guid RunId, Guid? RepoId, string Name, string? Path, long Rows, int Columns, long SizeBytes);

/// <summary>One data-quality assertion a run evaluated.</summary>
internal sealed record RunAssertionDto(
    long Id, Guid RunId, Guid? RepoId, string Name, string Result, string AssertedValue, bool Evaluated, string? Error);

/// <summary>One generated SQL statement a run executed, in execution order.</summary>
internal sealed record RunStatementDto(
    long Id, Guid RunId, Guid? RepoId, int Ordinal, DateTime? TimestampUtc, string Step, string Sql, string? Error);

/// <summary>One entry of a run's consolidated trace: an event or a generated statement, interleaved by time.</summary>
internal sealed record RunTraceEntryDto(
    long Id, Guid RunId, Guid? RepoId, string Kind, int Ordinal, DateTime? TimestampUtc, string Level,
    string? Step, string? Message, string? Sql, string? Error, long? Rows, double? ElapsedMs);

/// <summary>The payload of the live trace stream's final <c>end</c> event: the run's terminal status.</summary>
internal sealed record RunTraceStreamEndDto(string Status);

/// <summary>One surrogate-key generation outcome of a run (ingestion flows).</summary>
internal sealed record RunSurrogateKeyDto(
    long Id, Guid RunId, Guid? RepoId, int SurrogateKeyId, string SurrogateTable, string SurrogateColumn,
    bool IsRemote, long KeysGenerated, long RowsStamped, bool Executed, string? Error);

/// <summary>One per-metric health-check summary of a run (hc flows).</summary>
internal sealed record RunHealthCheckMetricDto(
    long Id, Guid RunId, Guid? RepoId, string Name, int SeriesPoints, int ImputedPoints, int ImmaturePoints,
    int Anomalies, int LevelShifts, bool ModelTrained, string? ModelTrainer, string? Error);

/// <summary>A run group's member counts by lifecycle state.</summary>
internal sealed record RunGroupCountsDto(
    int Total, int Queued, int Running, int Succeeded, int Failed, int Cancelled, int Skipped);

/// <summary>One run group's header plus the live rollup of its members' states.</summary>
internal sealed record RunGroupDto(
    Guid GroupId, Guid RepoId, string Mode, string Anchor, int MemberCount, string? CommitSha, DateTime EnqueuedUtc,
    RunGroupCountsDto Counts);
