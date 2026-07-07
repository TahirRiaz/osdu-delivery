namespace SqlFlow.ControlPlane.Api;

/// <summary>One page of results plus the totals a client needs to paginate. Offset paging (page/pageSize) is
/// sufficient for the catalog's cardinality; the contract can move to a cursor without breaking callers.</summary>
/// <typeparam name="T">The item DTO.</typeparam>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long Total);

/// <summary>Page request normalization: clamps to safe bounds so a client cannot request an unbounded page.</summary>
public static class PageRequest
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public static (int Page, int PageSize) Normalize(int? page, int? pageSize)
        => (Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize));
}

/// <summary>A synced source repository.</summary>
public sealed record RepoDto(
    Guid Id, string Name, string? RemoteUrl, string? RootPath, DateTime FirstSeenUtc, DateTime LastSyncUtc);

/// <summary>The outcome of a manual local-path repo sync: the pipeline reconciliation counts and the lineage tallies
/// (objects, columns, edges, waves, dependencies), whether the derived tier connected to the live database, and any
/// warnings the pass surfaced (bounded). This is the compact summary the GUI shows after a "Sync now".</summary>
public sealed record RepoSyncResultDto(
    int PipelinesAdded, int PipelinesUpdated, int PipelinesUnchanged, int PipelinesDeactivated,
    int Objects, int Columns, int Edges, int Waves, int Dependencies,
    bool Connected, IReadOnlyList<string> Warnings);

/// <summary>A pipeline (flow) as it appears in lists: the hot dimensions, without the heavy YAML/definition body.</summary>
public sealed record PipelineSummaryDto(
    Guid Id, Guid RepoId, string Name, string Kind, string? Batch, int Wave, bool Active,
    string? SourceServer, string? TargetServer, string RelativePath, DateTime FirstSeenUtc, DateTime LastSeenUtc);

/// <summary>A single pipeline with its full (secret-redacted) definition for the detail view.</summary>
public sealed record PipelineDetailDto(
    Guid Id, Guid RepoId, string Name, string Kind, string? Batch, int Wave, bool Active,
    string? SourceServer, string? TargetServer, string RelativePath, string ContentHash,
    string Yaml, string DefinitionJson, DateTime FirstSeenUtc, DateTime LastSeenUtc);

/// <summary>One resolved column of a pipeline's pre-ingestion transformation view: <c>declared</c> rows come
/// from the flow YAML (the source of truth), <c>detected</c> rows from the latest run's generated view.</summary>
public sealed record PipelineColumnDto(
    string Kind, int Ordinal, string ColumnName, string? SourceColumn, string? Expression, string? DataType,
    int? SortOrder, bool IsVirtual, bool ExcludeFromView, bool Converted);

/// <summary>One distinct file a pipeline has processed across its whole run history (deduplicated by name+path,
/// carrying the newest processing's metadata). <see cref="LastRun"/> flags the files processed by the pipeline's
/// most recent file-bearing run, so the view can separate "what the last run found" from "everything ever seen".</summary>
public sealed record PipelineFileDto(
    string Name, string? Path, DateTimeOffset? Modified, long Rows, long SizeBytes, bool LastRun, DateTime? LastProcessedUtc);

/// <summary>The bootstrap token request body (only honored when a bootstrap secret is configured).</summary>
public sealed record TokenRequest(string Secret, string? Subject, IReadOnlyList<string>? Scopes);

/// <summary>An issued bearer token.</summary>
public sealed record TokenResponse(string AccessToken, string TokenType, int ExpiresIn);

/// <summary>A local (regular SQLFlow user) sign-in.</summary>
public sealed record LoginRequest(string Username, string Password);

/// <summary>An external-token sign-in: the SPA posts the Entra ID token it obtained via MSAL and receives a
/// SQLFlow token in exchange.</summary>
public sealed record ExchangeRequest(string Token);

/// <summary>A signed-in session: the SQLFlow bearer token plus the identity facts the GUI shapes itself with
/// (which nav to show, which actions to enable) without decoding the token client-side.</summary>
public sealed record SessionResponse(
    string AccessToken, string TokenType, int ExpiresIn, string Subject, string Role, IReadOnlyList<string> Scopes);

/// <summary>The Entra ID sign-in settings a browser client needs to run the MSAL flow. All values are public by
/// design (a SPA's client id and tenant id ship in its bundle); secrets never appear here.</summary>
public sealed record EntraProviderDto(bool Enabled, string? TenantId, string? ClientId, string? Authority);

/// <summary>Which sign-in methods this control plane offers, so the login page renders only what works.</summary>
public sealed record AuthProvidersDto(bool Local, bool Bootstrap, EntraProviderDto Entra);

/// <summary>A control-plane user as the admin surface lists it. Never carries the password hash.</summary>
public sealed record UserDto(
    Guid Id, string Username, string? Email, string? DisplayName, string Role, string Provider, bool Active,
    DateTime CreatedUtc, DateTime UpdatedUtc, DateTime? LastLoginUtc);

/// <summary>A role and the scopes it grants.</summary>
public sealed record RoleDto(string Name, string Scopes, string Description);

/// <summary>Creates a local user (SSO users are provisioned by their first sign-in, never through this).</summary>
public sealed record CreateUserRequest(string Username, string Password, string Role, string? Email, string? DisplayName);

/// <summary>Changes a user's role.</summary>
public sealed record SetRoleRequest(string Role);

/// <summary>Resets a local user's password.</summary>
public sealed record SetPasswordRequest(string Password);

/// <summary>A personal access token as its owner lists it. Never carries the secret (unrecoverable after creation)
/// nor its hash; <see cref="Prefix"/> is the recognizable, non-secret lead. <see cref="RevokedUtc"/> non-null means
/// the token is revoked; <see cref="ExpiresUtc"/> in the past means expired.</summary>
public sealed record AccessTokenDto(
    Guid Id, string Name, string Prefix, IReadOnlyList<string> Scopes, DateTime CreatedUtc,
    DateTime? ExpiresUtc, DateTime? LastUsedUtc, DateTime? RevokedUtc);

/// <summary>Creates a personal access token. <see cref="Scopes"/> is capped server-side to the caller's own scopes
/// (an empty/omitted list defaults to all of them); <see cref="ExpiresInDays"/> null means the token never
/// expires.</summary>
public sealed record CreateAccessTokenRequest(string Name, IReadOnlyList<string>? Scopes, int? ExpiresInDays);

/// <summary>The response to creating a token: the listing view plus the one-time <see cref="Secret"/>. The secret is
/// shown exactly once here and is never retrievable again.</summary>
public sealed record CreatedAccessTokenDto(AccessTokenDto Token, string Secret);
