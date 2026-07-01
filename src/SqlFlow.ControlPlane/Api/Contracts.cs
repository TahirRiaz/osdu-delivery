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

/// <summary>A pipeline (flow) as it appears in lists: the hot dimensions, without the heavy YAML/definition body.</summary>
public sealed record PipelineSummaryDto(
    Guid Id, Guid RepoId, string Name, string Kind, string? Batch, int Wave, bool Active,
    string? SourceServer, string? TargetServer, string RelativePath, DateTime FirstSeenUtc, DateTime LastSeenUtc);

/// <summary>A single pipeline with its full (secret-redacted) definition for the detail view.</summary>
public sealed record PipelineDetailDto(
    Guid Id, Guid RepoId, string Name, string Kind, string? Batch, int Wave, bool Active,
    string? SourceServer, string? TargetServer, string RelativePath, string ContentHash,
    string Yaml, string DefinitionJson, DateTime FirstSeenUtc, DateTime LastSeenUtc);

/// <summary>The bootstrap token request body (only honored when a bootstrap secret is configured).</summary>
public sealed record TokenRequest(string Secret, string? Subject, IReadOnlyList<string>? Scopes);

/// <summary>An issued bearer token.</summary>
public sealed record TokenResponse(string AccessToken, string TokenType, int ExpiresIn);
