using SqlFlow.Core.Runs;

namespace SqlFlow.ControlPlane.Api;

/// <summary>One page of results plus the totals a client needs to paginate. Offset paging (page/pageSize) is
/// sufficient for the catalog's cardinality; the contract can move to a cursor without breaking callers. A listing
/// that counts only as far as a bound (the delivery ledger's record listing) sets <c>TotalCapped</c>, and its total is
/// then a floor: that many match, and more.</summary>
/// <typeparam name="T">The item DTO.</typeparam>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long Total, bool TotalCapped = false);

/// <summary>Page request normalization: clamps to safe bounds so a client cannot request an unbounded page.</summary>
public static class PageRequest
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public static (int Page, int PageSize) Normalize(int? page, int? pageSize)
        => (Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize));
}

/// <summary>One commit in a synced repository's history: who changed what, when, and which paths it touched.</summary>
public sealed record GitCommitDto(
    string Sha,
    string ShortSha,
    string AuthorName,
    string AuthorEmail,
    DateTime CommittedUtc,
    string Message,
    IReadOnlyList<string> ChangedPaths);

/// <summary>The patch one commit applied, optionally scoped to a single file. <c>Truncated</c> says the text was
/// cut at the inline limit, so a reader never mistakes a clipped patch for a complete one.</summary>
public sealed record GitDiffDto(
    string Sha,
    string AuthorName,
    DateTime CommittedUtc,
    string Message,
    string? Path,
    int LinesAdded,
    int LinesDeleted,
    string Patch,
    bool Truncated);

/// <summary>A synced source repository.</summary>
public sealed record RepoDto(
    Guid Id, string Name, string? RemoteUrl, string? RootPath, DateTime FirstSeenUtc, DateTime LastSyncUtc);

/// <summary>One entry in a repository's content listing: a repo-relative, forward-slashed path, whether it is a
/// folder, and the file's size in bytes (0 for a folder, and for a file the host could not stat).</summary>
public sealed record RepoTreeEntryDto(string Path, bool IsFolder, long SizeBytes);

/// <summary>Everything one repository holds, path-ordered: the folders and files as they stand on the synced branch
/// (or on disk for a local-path repo), not only what the catalog imported. <c>ReadFrom</c> is <c>git</c> or
/// <c>disk</c>; <c>Truncated</c> says the listing hit its cap and is partial.</summary>
public sealed record RepoTreeDto(string ReadFrom, IReadOnlyList<RepoTreeEntryDto> Entries, bool Truncated);

/// <summary>A pipeline (flow) as it appears in lists: the hot dimensions, without the heavy YAML/definition body.
/// <c>ExecutionMode</c> is <c>auto</c> or <c>manual</c> (the flow's YAML <c>mode:</c>); manual flows are excluded
/// from schedules and group runs and execute only when triggered directly. <c>Lifecycle</c> is <c>production</c>
/// or <c>development</c> (the YAML <c>lifecycle:</c>); development flows run normally but never generate
/// notification events.</summary>
public sealed record PipelineSummaryDto(
    Guid Id, Guid RepoId, string Name, string Kind, string? Batch, int Wave, bool Active, string ExecutionMode,
    string Lifecycle, string? SourceServer, string? TargetServer, string RelativePath, DateTime FirstSeenUtc,
    DateTime LastSeenUtc);

/// <summary>One batch (source-system grouping) of a repo's flows with how many flows it holds: the grouping level
/// between a repo and its flows in batch-grouped browsing surfaces. A flow that declares no batch reports under
/// <see cref="SqlFlow.Catalog.CatalogPipeline.DefaultBatch"/>.</summary>
public sealed record PipelineBatchDto(Guid RepoId, string Batch, int FlowCount, int ActiveCount);

/// <summary>A single pipeline with its full (secret-redacted) definition for the detail view.</summary>
public sealed record PipelineDetailDto(
    Guid Id, Guid RepoId, string Name, string Kind, string? Batch, int Wave, bool Active, string ExecutionMode,
    string Lifecycle, string? SourceServer, string? TargetServer, string RelativePath, string ContentHash,
    string Yaml, string DefinitionJson, DateTime FirstSeenUtc, DateTime LastSeenUtc);

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
public sealed record EntraProviderDto(bool Enabled, string? ClientId, string? Authority);

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

/// <summary>Renames a user and rewrites their display name and email (a null or blank optional field clears it).
/// The username only changes for a local user; an SSO account's is owned by Entra.</summary>
public sealed record UpdateUserProfileRequest(string Username, string? Email, string? DisplayName);

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

/// <summary>Who a bearer credential authenticates as: the resolved subject, role, and effective scopes, plus
/// the backing catalog user id (null for a bootstrap token, which has no account). A headless client's
/// "whoami": one call that answers "am I signed in, as whom, and what am I allowed to do".</summary>
public sealed record IdentityDto(string Subject, string? Role, IReadOnlyList<string> Scopes, Guid? UserId);

/// <summary>One notification channel's availability on this deployment: whether it can send at all, and (for
/// email) which transport backs it, so the settings page can say "email via smtp" instead of a bare toggle.</summary>
public sealed record NotificationChannelAvailabilityDto(bool Available, string? Provider);

/// <summary>What the notification settings page needs to render itself: whether the pipeline runs at all, which
/// channels this deployment can send on, the valid kind / mode vocabularies with their defaults, and the caller's
/// account email (the default destination when a subscription sets no override).</summary>
public sealed record NotificationOptionsDto(
    bool Enabled,
    NotificationChannelAvailabilityDto Email,
    NotificationChannelAvailabilityDto Slack,
    IReadOnlyList<string> Kinds,
    IReadOnlyList<string> DefaultKinds,
    IReadOnlyList<string> Modes,
    string? UserEmail,
    int DefaultDigestIntervalMinutes,
    int DefaultCooldownMinutes,
    EstateDigestOptionsDto EstateDigest);

/// <summary>How this deployment produces estate digests: whether the periodic generator runs and on what period,
/// plus the bounds the on-demand window must fall inside. Lets the settings page state the cadence in words
/// instead of leaving the reader to guess when the next one lands.</summary>
public sealed record EstateDigestOptionsDto(
    bool Enabled, int IntervalMinutes, int MinWindowMinutes, int MaxWindowMinutes);

/// <summary>One estate digest as a list shows it: its window, headline and per-kind counts, without the rendered
/// bodies. <see cref="GeneratedBy"/> names the person who asked for a manual digest; null for a scheduled one.</summary>
public sealed record NotificationDigestSummaryDto(
    Guid Id, string Origin, DateTime PeriodStartUtc, DateTime PeriodEndUtc, DateTime GeneratedUtc,
    string? GeneratedBy, string Subject, int EventCount, int FlowCount, int FailedCount, int CancelledCount,
    int SkippedCount, bool Truncated);

/// <summary>One flow's slice of a digest, as the GUI's digest table renders it: what happened, how many times,
/// when it last happened, the error worth reading, and the run to open. <see cref="Kind"/> is the notification
/// event kind (<c>run_failed</c>, <c>run_cancelled</c>, ...), not a run status.</summary>
public sealed record NotificationDigestFlowDto(
    string FlowName, string FlowKind, string Kind, int Count, DateTime LastOccurredUtc, Guid LastRunId,
    Guid PipelineId, string? LastError);

/// <summary>One estate digest opened for reading: the summary, the per-flow rows behind it, and the composed
/// bodies. <see cref="Flows"/> is what the GUI tabulates; <see cref="TextBody"/> is the message as it would be
/// delivered, which is what a reader copies out or checks before sending.</summary>
public sealed record NotificationDigestDto(
    NotificationDigestSummaryDto Summary, IReadOnlyList<NotificationDigestFlowDto> Flows, string TextBody,
    string HtmlBody);

/// <summary>
/// Generates a digest on demand over one period, most often a single day. Both instants are UTC, and both are
/// required: a digest answers "what happened between these two moments", which a lookback from now cannot express
/// once the question is about a particular day. An end in the future is clamped to now, so asking for today
/// yields today so far rather than a period the estate has not lived through yet.
/// </summary>
public sealed record GenerateNotificationDigestRequest(DateTime FromUtc, DateTime ToUtc);

/// <summary>Sends an already-generated digest through one of the caller's own subscriptions, which is what
/// supplies the channel and destination.</summary>
public sealed record SendNotificationDigestRequest(Guid SubscriptionId);

/// <summary>One notification opt-in as its owner lists it.</summary>
public sealed record NotificationSubscriptionDto(
    Guid Id, string Channel, string Mode, IReadOnlyList<string> Kinds, string? FlowPattern, string? EmailAddress,
    string? SlackTarget, int DigestIntervalMinutes, int CooldownMinutes, bool Enabled, DateTime? LastSentUtc,
    DateTime? NextDueUtc, DateTime CreatedUtc, DateTime UpdatedUtc);

/// <summary>Creates a notification opt-in. <see cref="Channel"/> is fixed for the subscription's lifetime (make a
/// new one to switch); everything else has a served default: <see cref="Mode"/> immediate, <see cref="Kinds"/> the
/// server defaults, a null <see cref="EmailAddress"/> / <see cref="SlackTarget"/> meaning "my account email" /
/// "direct-message me", and the interval / cooldown defaults from the options endpoint.</summary>
public sealed record CreateNotificationSubscriptionRequest(
    string Channel, string? Mode, IReadOnlyList<string>? Kinds, string? FlowPattern, string? EmailAddress,
    string? SlackTarget, int? DigestIntervalMinutes, int? CooldownMinutes);

/// <summary>Edits a notification opt-in. Null means "keep the current value"; for the clearable strings
/// (<see cref="FlowPattern"/>, <see cref="EmailAddress"/>, <see cref="SlackTarget"/>) an empty string clears the
/// field back to its default behavior. The channel is not editable.</summary>
public sealed record UpdateNotificationSubscriptionRequest(
    string? Mode, IReadOnlyList<string>? Kinds, string? FlowPattern, string? EmailAddress, string? SlackTarget,
    int? DigestIntervalMinutes, int? CooldownMinutes, bool? Enabled);

/// <summary>One outbound notification as its owner sees it in the history: where it went, what it covered, and
/// whether (and why not) it arrived. The composed bodies are omitted; <see cref="Subject"/> identifies the message.</summary>
public sealed record NotificationDeliveryDto(
    Guid Id, Guid SubscriptionId, string Channel, string Target, string Subject, string Status, int Attempts,
    int EventCount, string? LastError, DateTime CreatedUtc, DateTime? SentUtc);

/// <summary>The response to anything that puts a message on the outbox (a test send, a digest send): the
/// delivery row to watch in the deliveries history.</summary>
public sealed record NotificationQueuedDeliveryDto(Guid DeliveryId);

/// <summary>The outcome of a manual local-path repo sync: the pipeline reconciliation counts, the run artifacts
/// discovered, and any warnings the pass surfaced (bounded). This is the compact summary the GUI shows after a
/// "Sync now".</summary>
public sealed record RepoSyncResultDto(
    int PipelinesAdded, int PipelinesUpdated, int PipelinesUnchanged, int PipelinesDeactivated, int PipelinesDeleted,
    int RunsAdded, int RunsSkipped, int RunsFailed,
    int DocumentsAdded, int DocumentsUpdated, int DocumentsUnchanged, int DocumentsRemoved, int DocumentsInvalid,
    IReadOnlyList<string> Warnings);
