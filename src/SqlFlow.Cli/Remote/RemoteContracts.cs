using System.Text.Json.Serialization;

namespace SqlFlow.Cli.Remote;

// The CLI's client-side mirror of the control plane's wire contracts. The CLI deliberately does not reference
// the SqlFlow.ControlPlane project (an ASP.NET Core host); these records ARE the protocol boundary, kept
// field-for-field with the server records they mirror (see src/SqlFlow.ControlPlane/Api). Everything is
// camelCase JSON on the wire (ASP.NET's web defaults), deserialized case-insensitively here; the device token
// response alone follows the OAuth snake_case convention and pins its names explicitly.

internal sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long Total);

internal sealed record RepoDto(
    Guid Id, string Name, string? RemoteUrl, string? RootPath, DateTime FirstSeenUtc, DateTime LastSyncUtc);

internal sealed record LoginRequest(string Username, string Password);

internal sealed record SessionResponse(
    string AccessToken, string TokenType, int ExpiresIn, string Subject, string Role, IReadOnlyList<string> Scopes);

internal sealed record DeviceAuthorizationRequest(string? ClientId, string? Scope);

internal sealed record DeviceAuthorizationResponse(
    string DeviceCode, string UserCode, string VerificationUri, string VerificationUriComplete, int ExpiresIn, int Interval);

internal sealed record DeviceTokenRequest(string DeviceCode);

internal sealed record DeviceTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("scope")] string Scope);

internal sealed record DeviceErrorResponse([property: JsonPropertyName("error")] string Error);

internal sealed record AccessTokenDto(
    Guid Id, string Name, string Prefix, IReadOnlyList<string> Scopes, DateTime CreatedUtc,
    DateTime? ExpiresUtc, DateTime? LastUsedUtc, DateTime? RevokedUtc);

internal sealed record CreateAccessTokenRequest(string Name, IReadOnlyList<string>? Scopes, int? ExpiresInDays);

internal sealed record CreatedAccessTokenDto(AccessTokenDto Token, string Secret);

internal sealed record RunTriggerRequest(
    Guid RepoId, string FlowName, string? Pool = null, string? CommitSha = null, string? Scope = null,
    string? Operation = null, bool Force = false, IReadOnlyDictionary<string, string>? Values = null, string? Drop = null,
    Guid? SubmissionId = null, IReadOnlyList<Guid>? RecordKeys = null, string? PublishTo = null, string? Redeliver = null);

internal sealed record RunTriggerAccepted(Guid RunId, string Status);

internal sealed record RunGroupAccepted(Guid GroupId, int MemberCount, string Status);

internal sealed record RunScopePreviewMemberDto(string FlowName, string FlowKind, int Wave);

internal sealed record RunScopePreviewDto(
    string Scope, string Anchor, int MemberCount, int WaveCount, IReadOnlyList<RunScopePreviewMemberDto> Members);

internal sealed record RunSummaryDto(
    Guid RunId, Guid PipelineId, Guid? RepoId, string FlowName, string FlowKind, string Batch, int Wave,
    string Status, bool Success,
    string? TargetPool, string? CommitSha, DateTime WrittenUtc, DateTime? EnqueuedUtc, double? DurationSeconds,
    long? RowsLoaded, long? RowsInserted, long? RowsUpdated, long? RowsDeleted, Guid? GroupId,
    string? LastAction, DateTime? LastActionUtc, string? Error, string Operation, bool Force);

internal sealed record RunDetailDto(
    Guid RunId, Guid PipelineId, Guid? RepoId, string FlowName, string FlowKind, string Batch, int Wave,
    string Status, bool Success,
    string? TargetPool, string? CommitSha, DateTime? EnqueuedUtc, string? ClaimedByNode, DateTime? CancelRequestedUtc,
    int SchemaVersion, DateTime WrittenUtc, DateTime? StartUtc, DateTime? EndUtc, double? DurationSeconds,
    long? RowsLoaded, long? RowsInserted, long? RowsUpdated, long? RowsDeleted, string? Error, string? Host, string? RequestedBy,
    string Operation, bool Force, Guid? SubmissionId, string? ParametersJson, Guid? ResultSubmissionId,
    int? RecordsPlanned, int? RecordsDelivered, int? RecordsHeld, int? RecordsFailed, int? RecordsSkipped, Guid? GroupId,
    Guid? FanOutRoot, int? FanOutSlot, int? FanOutCount, string? ResultJson);

internal sealed record RunTraceEntryDto(
    long Id, Guid RunId, Guid? RepoId, int Ordinal, DateTime TimestampUtc, string Level,
    string? Step, string Message, long? Rows, double? ElapsedMs);

internal sealed record RunTraceStreamEndDto(string Status);

internal sealed record RunGroupCountsDto(
    int Total, int Queued, int Running, int Succeeded, int Failed, int Cancelled, int Skipped);

internal sealed record RunGroupDto(
    Guid GroupId, Guid RepoId, string Mode, string Anchor, int MemberCount, string? CommitSha, DateTime EnqueuedUtc,
    RunGroupCountsDto Counts);

internal sealed record IdentityDto(string Subject, string? Role, IReadOnlyList<string> Scopes, Guid? UserId);

internal sealed record RunCountsDto(long Queued, long Running, long Succeeded, long Failed, long Cancelled, long Last24h);

internal sealed record DashboardDto(
    long Repos, long Pipelines, long ActivePipelines, RunCountsDto Runs,
    long NodesOnline, long NodesTotal, long SchedulesEnabled, long SchedulesPaused,
    long RepoSources, long RepoSourcesWithErrors, DateTime AsOfUtc);

internal sealed record NodeDto(string Name, DateTime FirstSeenUtc, DateTime LastSeenUtc, string? Version, bool Online);

internal sealed record ScheduleDto(
    Guid Id, Guid RepoId, string Name, IReadOnlyList<Guid> MemberPipelineIds, string? Cron, int? IntervalSeconds,
    string Timezone, bool Enabled, bool Catchup, bool Paused, string Source, DateTime? NextFireUtc,
    DateTime? LastFireUtc, Guid? LastRunId, Guid? LastGroupId, bool LastGroupActive, DateTime CreatedUtc,
    DateTime UpdatedUtc, int? MaxConcurrency, RunGroupCountsDto? LastCounts, string Operation = "deliver");

internal sealed record CreateScheduleRequest(
    Guid RepoId, IReadOnlyList<string> Members, string? Cron, int? IntervalSeconds, string? Timezone, bool? Enabled,
    bool? Catchup = null, string? Name = null, int? MaxConcurrency = null, string? Operation = null);

internal sealed record ScheduleCreated(Guid Id, DateTime? NextFireUtc);

internal sealed record ScheduleRunAccepted(Guid RunId, Guid? GroupId = null, int MemberCount = 1);

internal sealed record RepoSourceDto(
    Guid Id, string Name, string RemoteUrl, string Branch, bool Enabled, int SyncIntervalSeconds,
    DateTime? NextSyncUtc, DateTime? LastSyncUtc, string? LastSyncedSha, string? LastError,
    string? CredentialReference, string? CredentialUsername, IReadOnlyList<string> ExcludedFlowPaths,
    DateTime CreatedUtc, DateTime UpdatedUtc);

internal sealed record RegisterRepoSourceRequest(
    string Name, string RemoteUrl, string? Branch, int? SyncIntervalSeconds, bool? Enabled,
    string? CredentialReference, string? CredentialUsername, string[]? ExcludedFlowPaths);

internal sealed record RepoSourceRegistered(Guid Id);

internal sealed record DiscoverRepoRequest(string RemoteUrl, string? Branch, string? CredentialReference, string? CredentialUsername);

internal sealed record DiscoveredFlowDto(
    string RelativePath, string? FlowName, string? Kind, long SizeBytes, bool ParseOk, string? ParseError, string? Content);

internal sealed record RepoSyncResultDto(
    int PipelinesAdded, int PipelinesUpdated, int PipelinesUnchanged, int PipelinesDeactivated, int PipelinesDeleted,
    int RunsAdded, int RunsSkipped, int RunsFailed,
    int DocumentsAdded, int DocumentsUpdated, int DocumentsUnchanged, int DocumentsRemoved, int DocumentsInvalid,
    IReadOnlyList<string> Warnings);

internal sealed record PipelineSummaryDto(
    Guid Id, Guid RepoId, string Name, string Kind, string? Batch, int Wave, bool Active, string ExecutionMode,
    string? SourceServer, string? TargetServer, string RelativePath, DateTime FirstSeenUtc, DateTime LastSeenUtc,
    string Lifecycle = "production");

internal sealed record PipelineDetailDto(
    Guid Id, Guid RepoId, string Name, string Kind, string? Batch, int Wave, bool Active, string ExecutionMode,
    string? SourceServer, string? TargetServer, string RelativePath, string ContentHash,
    string Yaml, string DefinitionJson, DateTime FirstSeenUtc, DateTime LastSeenUtc,
    string Lifecycle = "production");

internal sealed record FlowHitDto(
    string Id, string Name, string Kind, string? Batch, string RelativePath, string RepoId, string RepoName,
    string MatchedIn, string Snippet);

internal sealed record SearchCategoryDto<T>(long Total, IReadOnlyList<T> Items);

internal sealed record AllSearchDto(
    string Query,
    IReadOnlyList<string> Tokens,
    SearchCategoryDto<FlowHitDto> Flows);
