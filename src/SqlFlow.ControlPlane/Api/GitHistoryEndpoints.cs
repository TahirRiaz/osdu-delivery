using LibGit2Sharp;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Node;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// The change-history read surface (<c>/flow-history</c>): how a pipeline's YAML has been edited over time, from
/// the git history of the repo the estate syncs. "Who changed this flow's mapping, and when."
///
/// Credentials never leave the control plane. A caller names a repo or a flow; the credential reference stored
/// against it is resolved here through the same <see cref="GitMaterializer.ResolveCredentialsAsync"/> path the
/// managed sync uses, so no client (the GUI, a script) ever holds a git token. Only repositories the estate
/// already manages are reachable: a registered repo source. An arbitrary remote URL is not an addressable target.
/// </summary>
public static class GitHistoryEndpoints
{
    /// <summary>How stale the read-only history clone may be before a request refreshes it. A minute keeps a
    /// burst of related queries (a log, then a diff of one of its commits) on one fetch while still reflecting a
    /// push made moments ago.</summary>
    private static readonly TimeSpan HistoryFreshness = TimeSpan.FromMinutes(1);

    /// <summary>The most commits one query returns. A history read is a browsing aid, not a bulk export.</summary>
    private const int MaxCommits = 200;

    /// <summary>How many commits one query walks before giving up on finding more matches. Bounds the cost of a
    /// filter that matches nothing recent (a folder untouched for years) without truncating a normal answer.</summary>
    private const int MaxScannedCommits = 5_000;

    /// <summary>The largest patch returned inline. A diff past this is reported truncated rather than streamed:
    /// a generated snapshot of a large table can run to megabytes and nothing useful reads it as one blob.</summary>
    private const int MaxPatchChars = 200_000;

    public static RouteGroupBuilder MapGitHistoryEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var flows = group.MapGroup("/flow-history").WithTags("FlowHistory");
        flows.MapGet("/commits", ListFlowCommitsAsync).WithName("ListFlowDefinitionCommits");
        flows.MapGet("/flows/{pipelineId:guid}", GetFlowFileHistoryAsync).WithName("GetFlowDefinitionHistory");
        flows.MapGet("/diff", GetFlowDiffAsync).WithName("GetFlowDefinitionDiff");

        return group;
    }

    // ---- Flow definitions (the pipeline YAML) --------------------------------------------------------------

    /// <summary>
    /// Commits that changed pipeline YAML in one synced repository, newest first, filterable by path prefix,
    /// author, date, and message text. This is the ETL code's history, not the databases'.
    /// </summary>
    private static async Task<Results<Ok<IReadOnlyList<GitCommitDto>>, ProblemHttpResult>> ListFlowCommitsAsync(
        CatalogDbContext db, ISecretResolver secrets, Guid? repoId, string? path, string? author, string? message,
        DateTime? since, DateTime? until, int? limit, CancellationToken ct)
    {
        var source = await ResolveSourceAsync(db, repoId, ct).ConfigureAwait(false);
        if (source is null)
        {
            return BadRequest(
                "No repository",
                "No git-synced repository matched. Pass repoId, or register a repo source first; only repositories this estate syncs are readable.");
        }

        return await ReadAsync(
            secrets, source.RemoteUrl, source.Branch, source.CredentialReference, source.CredentialUsername,
            repo => Log(repo, path, author, message, since, until, limit), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One flow's definition history: every commit that touched THAT flow's YAML file, newest first. The file is
    /// the pipeline's own recorded path, so a caller needs only the pipeline id it already has.
    /// </summary>
    private static async Task<Results<Ok<IReadOnlyList<GitCommitDto>>, ProblemHttpResult>> GetFlowFileHistoryAsync(
        Guid pipelineId, CatalogDbContext db, ISecretResolver secrets, int? limit, CancellationToken ct)
    {
        var pipeline = await db.Pipelines.AsNoTracking()
            .Where(p => p.Id == pipelineId)
            .Select(p => new { p.RepoId, p.RelativePath, p.Name })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (pipeline is null)
        {
            return NotFound("pipeline", pipelineId);
        }

        var source = await ResolveSourceAsync(db, pipeline.RepoId, ct).ConfigureAwait(false);
        if (source is null)
        {
            return BadRequest(
                "Repository not git-synced",
                $"Flow '{pipeline.Name}' belongs to a repo with no registered git source, so it has no commit history to read.");
        }

        return await ReadAsync(
            secrets, source.RemoteUrl, source.Branch, source.CredentialReference, source.CredentialUsername,
            repo => Log(repo, pipeline.RelativePath, author: null, message: null, since: null, until: null, limit),
            ct).ConfigureAwait(false);
    }

    /// <summary>The patch one commit applied, optionally narrowed to a single file: what actually changed in the
    /// YAML, not just that it changed.</summary>
    private static async Task<Results<Ok<GitDiffDto>, ProblemHttpResult>> GetFlowDiffAsync(
        CatalogDbContext db, ISecretResolver secrets, Guid? repoId, string sha, string? path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sha))
        {
            return BadRequest("Missing commit", "Pass the commit sha to diff (from /flow-history/commits).");
        }

        var source = await ResolveSourceAsync(db, repoId, ct).ConfigureAwait(false);
        if (source is null)
        {
            return BadRequest("No repository", "No git-synced repository matched. Pass repoId.");
        }

        return await ReadDiffAsync(
            secrets, source.RemoteUrl, source.Branch, source.CredentialReference, source.CredentialUsername,
            sha, path, ct).ConfigureAwait(false);
    }

    // ---- The shared git read path --------------------------------------------------------------------------

    /// <summary>Resolves which registered git source backs a repo. With no repoId, the estate's single source is
    /// used; with several, the caller must say which.</summary>
    private static async Task<CatalogRepoSource?> ResolveSourceAsync(CatalogDbContext db, Guid? repoId, CancellationToken ct)
    {
        if (repoId is { } id)
        {
            var name = await db.Repos.AsNoTracking().Where(r => r.Id == id).Select(r => r.Name)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            return name is null
                ? null
                : await db.RepoSources.AsNoTracking().FirstOrDefaultAsync(s => s.Name == name, ct).ConfigureAwait(false);
        }

        var sources = await db.RepoSources.AsNoTracking().Take(2).ToListAsync(ct).ConfigureAwait(false);
        return sources.Count == 1 ? sources[0] : null;
    }

    private static async Task<Results<Ok<IReadOnlyList<GitCommitDto>>, ProblemHttpResult>> ReadAsync(
        ISecretResolver secrets, string remoteUrl, string branch, string? credentialReference, string? username,
        Func<Repository, IReadOnlyList<GitCommitDto>> read, CancellationToken ct)
    {
        try
        {
            var credentials = await GitMaterializer
                .ResolveCredentialsAsync(secrets, credentialReference, username, ct).ConfigureAwait(false);

            // git and file IO are blocking, and a cold clone is slow; keep both off the request thread.
            var commits = await Task.Run(
                () =>
                {
                    var workingDir = new GitMaterializer()
                        .EnsureHistoryClone(remoteUrl, branch, credentials, HistoryFreshness, ct);
                    using var repo = new Repository(workingDir);
                    return read(repo);
                },
                ct).ConfigureAwait(false);

            return TypedResults.Ok(commits);
        }
        catch (Exception ex) when (ex is SqlFlowNodeException or SqlFlowException or LibGit2SharpException)
        {
            return BadRequest("History unavailable", SecretHygiene.RedactedMessage(ex));
        }
    }

    private static async Task<Results<Ok<GitDiffDto>, ProblemHttpResult>> ReadDiffAsync(
        ISecretResolver secrets, string remoteUrl, string branch, string? credentialReference, string? username,
        string sha, string? path, CancellationToken ct)
    {
        try
        {
            var credentials = await GitMaterializer
                .ResolveCredentialsAsync(secrets, credentialReference, username, ct).ConfigureAwait(false);

            var diff = await Task.Run(
                () =>
                {
                    var workingDir = new GitMaterializer()
                        .EnsureHistoryClone(remoteUrl, branch, credentials, HistoryFreshness, ct);
                    using var repo = new Repository(workingDir);
                    return Diff(repo, sha, path);
                },
                ct).ConfigureAwait(false);

            return diff is null
                ? BadRequest("Unknown commit", $"Commit '{sha}' is not in this repository's history.")
                : TypedResults.Ok(diff);
        }
        catch (Exception ex) when (ex is SqlFlowNodeException or SqlFlowException or LibGit2SharpException)
        {
            return BadRequest("Diff unavailable", SecretHygiene.RedactedMessage(ex));
        }
    }

    /// <summary>A query-bound instant as UTC. Minimal-API binding parses a trailing 'Z' into a local-kind
    /// DateTime, so comparing it straight against a commit's UTC timestamp would move the window boundary by the
    /// host's offset; an unspecified kind is read as local time, the usual reading of a bare date.</summary>
    private static DateTime? AsUtc(DateTime? value)
        => value is not { } instant
            ? null
            : instant.Kind == DateTimeKind.Utc ? instant : instant.ToUniversalTime();

    /// <summary>
    /// The commit log, newest first, filtered. Path matching is done against each commit's own changed-file set
    /// rather than through LibGit2Sharp's <c>Commits.QueryBy</c>: that API resolves an exact blob path only (a
    /// folder prefix matches nothing) and throws on some merge topologies, whereas an estate browses by folder
    /// ("what changed under citybike") as often as by file.
    ///
    /// The walk is bounded by <see cref="MaxScannedCommits"/>, so a path that has not been touched in a very long
    /// time stops the search rather than reading an entire repository's history on one request.
    /// </summary>
    internal static IReadOnlyList<GitCommitDto> Log(
        Repository repo, string? path, string? author, string? message, DateTime? since, DateTime? until, int? limit)
    {
        var take = Math.Clamp(limit ?? 50, 1, MaxCommits);
        var prefix = string.IsNullOrWhiteSpace(path) ? null : path.Replace('\\', '/').Trim('/');

        var results = new List<GitCommitDto>(take);
        var scanned = 0;
        foreach (var commit in repo.Commits)
        {
            if (results.Count >= take || ++scanned > MaxScannedCommits)
            {
                break;
            }

            var when = commit.Author.When.UtcDateTime;
            if ((since is { } from && when < from) || (until is { } to && when > to))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(author)
                && !commit.Author.Name.Contains(author, StringComparison.OrdinalIgnoreCase)
                && !commit.Author.Email.Contains(author, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(message)
                && !commit.Message.Contains(message, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var changed = ChangedPaths(repo, commit);
            if (prefix is not null && !changed.Any(p => Touches(p, prefix)))
            {
                continue;
            }

            results.Add(new GitCommitDto(
                commit.Sha,
                commit.Sha[..Math.Min(8, commit.Sha.Length)],
                commit.Author.Name,
                commit.Author.Email,
                when,
                commit.MessageShort.Trim(),
                prefix is null ? changed : changed.Where(p => Touches(p, prefix)).ToList()));
        }

        return results;
    }

    /// <summary>True when a changed path IS the filter or lives under it as a folder. Compared case-insensitively:
    /// the repositories here are authored on Windows and read on Linux, so a case-sensitive match would silently
    /// return nothing for a correctly-spelled folder.</summary>
    private static bool Touches(string changedPath, string prefix)
        => changedPath.Equals(prefix, StringComparison.OrdinalIgnoreCase)
           || changedPath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>The paths a commit changed against its first parent; empty for a root commit's whole tree, which
    /// is reported as the tree itself rather than pretending nothing changed.</summary>
    private static IReadOnlyList<string> ChangedPaths(Repository repo, Commit commit)
    {
        var parent = commit.Parents.FirstOrDefault();
        var changes = repo.Diff.Compare<TreeChanges>(parent?.Tree, commit.Tree);
        return changes.Select(c => c.Path).OrderBy(p => p, StringComparer.Ordinal).Take(MaxCommits).ToList();
    }

    internal static GitDiffDto? Diff(Repository repo, string sha, string? path)
    {
        if (repo.Lookup<Commit>(sha) is not { } commit)
        {
            return null;
        }

        var parent = commit.Parents.FirstOrDefault();
        var compareOptions = new CompareOptions { ContextLines = 3 };
        var patch = string.IsNullOrWhiteSpace(path)
            ? repo.Diff.Compare<Patch>(parent?.Tree, commit.Tree, compareOptions)
            : repo.Diff.Compare<Patch>(
                parent?.Tree, commit.Tree, [path.Replace('\\', '/').TrimStart('/')], compareOptions);

        var text = patch.Content ?? string.Empty;
        var truncated = text.Length > MaxPatchChars;
        return new GitDiffDto(
            commit.Sha,
            commit.Author.Name,
            commit.Author.When.UtcDateTime,
            commit.MessageShort.Trim(),
            path,
            patch.LinesAdded,
            patch.LinesDeleted,
            truncated ? text[..MaxPatchChars] : text,
            truncated);
    }

    private static ProblemHttpResult NotFound(string resource, long id)
        => TypedResults.Problem(
            detail: $"No {resource} with id '{id.ToString(System.Globalization.CultureInfo.InvariantCulture)}'.",
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found");

    private static ProblemHttpResult NotFound(string resource, Guid id)
        => TypedResults.Problem(
            detail: $"No {resource} with id '{id}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");

    private static ProblemHttpResult BadRequest(string title, string detail)
        => TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest, title: title);
}
