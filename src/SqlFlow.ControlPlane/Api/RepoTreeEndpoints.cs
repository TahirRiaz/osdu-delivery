using System.Text;
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
/// What a synced repository actually HOLDS, as opposed to what the catalog imported from it. The catalog knows only
/// the flow files a sync selected, so a folder of mappings, of documentation, or of flows excluded from the import is
/// invisible to every catalog-derived listing. These endpoints read the repository itself, so the repo view can show
/// its real folder outline rather than only the folders that happen to hold a registered pipeline, and open the YAML
/// documents in it.
///
/// The listing is content-free: paths, folder-or-file, and blob sizes. The one body served is a YAML document's text
/// (flows, mappings, schedule libraries), bounded, and passed through the same secret redaction every catalog copy of
/// a flow document gets. No client ever holds a git credential: the repo's registered source supplies the remote and
/// its credential REFERENCE, resolved through the same <see cref="GitMaterializer.ResolveCredentialsAsync"/> path the
/// managed sync uses.
/// </summary>
public static class RepoTreeEndpoints
{
    /// <summary>How stale the read-only clone may be before a read refreshes it. Matches the history surface, so a
    /// file pushed moments ago shows up while a burst of browsing costs one fetch at most.</summary>
    private static readonly TimeSpan TreeFreshness = TimeSpan.FromMinutes(1);

    /// <summary>The most entries one listing returns. A repo of flows runs to a few thousand files; the cap keeps a
    /// repository that also carries a data dump from turning a folder outline into a megabyte response, and the
    /// clipping is reported rather than silent.</summary>
    private const int MaxEntries = 20_000;

    /// <summary>The most characters of one document a preview returns. A flow or mapping is a few kilobytes; a
    /// document past this is served cut and reported truncated rather than streamed whole into an editor.</summary>
    public const int MaxPreviewChars = 1_000_000;

    public static RouteGroupBuilder MapRepoTreeEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var repos = group.MapGroup("/repos").WithTags("Repositories");
        repos.MapGet("/{id:guid}/tree", GetRepoTreeAsync).WithName("GetRepositoryTree");
        repos.MapGet("/{id:guid}/file", GetRepoFileAsync).WithName("GetRepositoryFile");

        return group;
    }

    /// <summary>
    /// Every folder and file in one repository, repo-relative and forward-slashed, sorted by path. A repo tracked by
    /// a registered git source is read from its branch tip through the shared read-only clone; a repo synced by the
    /// CLI from a local path is read from that path when the control-plane host can see it. Neither is reachable for
    /// a repo with no source and no visible root path, which is reported as a 400 rather than as an empty tree that
    /// would read as "this repository holds nothing".
    /// </summary>
    private static async Task<Results<Ok<RepoTreeDto>, ProblemHttpResult>> GetRepoTreeAsync(
        Guid id, CatalogDbContext db, ISecretResolver secrets, CancellationToken ct)
    {
        var located = await LocateAsync(id, db, ct).ConfigureAwait(false);
        switch (located)
        {
            case Unlocatable unlocatable:
                return unlocatable.Problem;
            case LocatedInGit git:
                try
                {
                    var entries = await ReadCloneAsync(
                        secrets,
                        git.Source,
                        repository =>
                        {
                            var found = new List<RepoTreeEntryDto>();
                            if (repository.Head.Tip is { } tip)
                            {
                                Walk(tip.Tree, prefix: string.Empty, found, ct);
                            }

                            return found;
                        },
                        ct).ConfigureAwait(false);
                    return TypedResults.Ok(Listing("git", entries));
                }
                catch (Exception ex) when (ex is SqlFlowNodeException or SqlFlowException or LibGit2SharpException)
                {
                    return BadRequest("Contents unavailable", SecretHygiene.RedactedMessage(ex));
                }

            default:
                var rootPath = ((LocatedOnDisk)located).RootPath;
                var listed = await Task.Run(() => ReadFromDisk(rootPath, ct), ct).ConfigureAwait(false);
                return TypedResults.Ok(Listing("disk", listed));
        }
    }

    /// <summary>
    /// One YAML document of a repository, as it stands where the listing reads it: the branch tip of a git-tracked
    /// repo, or the recorded root path of a local one. Only <c>.yaml</c> and <c>.yml</c> files are served, since those
    /// are the documents the estate is written in (flows, mappings, schedule libraries); anything else is a 400 naming
    /// why, not a guess at how to show it. The path is repo-relative; one that climbs out of the repository or into
    /// git's own metadata is refused before anything is read.
    /// </summary>
    private static async Task<Results<Ok<RepoFileDto>, ProblemHttpResult>> GetRepoFileAsync(
        Guid id, string? path, CatalogDbContext db, ISecretResolver secrets, CancellationToken ct)
    {
        if (NormalizePath(path) is not { } relative)
        {
            return BadRequest(
                "Invalid path",
                "Pass the repo-relative path of a file inside the repository (no '..' segments and nothing under .git).");
        }

        if (!IsYamlDocument(relative))
        {
            return BadRequest(
                "No preview",
                $"'{relative}' is not a YAML document; only .yaml and .yml files are previewed.");
        }

        var located = await LocateAsync(id, db, ct).ConfigureAwait(false);
        switch (located)
        {
            case Unlocatable unlocatable:
                return unlocatable.Problem;
            case LocatedInGit git:
                try
                {
                    var document = await ReadCloneAsync<DocumentText?>(
                        secrets,
                        git.Source,
                        repository =>
                        {
                            if (repository.Head.Tip is not { } tip
                                || tip[relative] is not { TargetType: TreeEntryTargetType.Blob } entry)
                            {
                                return null;
                            }

                            var blob = (Blob)entry.Target;
                            return ReadDocument(blob.GetContentStream(), blob.Size);
                        },
                        ct).ConfigureAwait(false);
                    return Served(relative, "git", document, $"'{relative}' is not a file on the synced branch.");
                }
                catch (Exception ex) when (ex is SqlFlowNodeException or SqlFlowException or LibGit2SharpException)
                {
                    return BadRequest("Contents unavailable", SecretHygiene.RedactedMessage(ex));
                }

            default:
                var root = Path.GetFullPath(((LocatedOnDisk)located).RootPath);
                var full = Path.GetFullPath(Path.Combine(root, relative));
                var rootPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
                if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest("Invalid path", $"'{relative}' does not resolve inside the repository.");
                }

                try
                {
                    var document = await Task.Run<DocumentText?>(
                        () => File.Exists(full)
                            ? ReadDocument(new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), new FileInfo(full).Length)
                            : null,
                        ct).ConfigureAwait(false);
                    return Served(relative, "disk", document, $"'{relative}' is not a file under the repo's root path.");
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    return BadRequest("Contents unavailable", SecretHygiene.RedactedMessage(ex));
                }
        }
    }

    /// <summary>Where a repository's contents can be read from, or why they cannot.</summary>
    private abstract record Location;

    /// <summary>A repo tracked by a registered git source: read from its branch tip.</summary>
    private sealed record LocatedInGit(CatalogRepoSource Source) : Location;

    /// <summary>A repo synced from a local path the control-plane host can see.</summary>
    private sealed record LocatedOnDisk(string RootPath) : Location;

    /// <summary>A repo whose contents this host cannot reach, with the problem that says so.</summary>
    private sealed record Unlocatable(ProblemHttpResult Problem) : Location;

    /// <summary>Resolves where one repository's contents live: its registered git source first (matched by name, as
    /// the sync matches it), else its recorded root path when the host can see it.</summary>
    private static async Task<Location> LocateAsync(Guid id, CatalogDbContext db, CancellationToken ct)
    {
        var repo = await db.Repos.AsNoTracking().Where(r => r.Id == id)
            .Select(r => new { r.Name, r.RootPath })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (repo is null)
        {
            return new Unlocatable(NotFound($"No repository with id '{id}'."));
        }

        var source = await db.RepoSources.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Name == repo.Name, ct).ConfigureAwait(false);
        if (source is not null)
        {
            return new LocatedInGit(source);
        }

        if (string.IsNullOrWhiteSpace(repo.RootPath))
        {
            return new Unlocatable(BadRequest(
                "Contents unavailable",
                $"Repo '{repo.Name}' has no registered git source and no recorded root path, so its files cannot be listed."));
        }

        if (!Directory.Exists(repo.RootPath))
        {
            return new Unlocatable(BadRequest(
                "Contents unavailable",
                $"The control-plane host cannot see the repo's root path '{repo.RootPath}', so its files cannot be listed. A repo synced from a local path is browsable only where the flow files live."));
        }

        return new LocatedOnDisk(repo.RootPath);
    }

    /// <summary>Runs one read against the shared read-only clone of a git source, refreshed when stale. The credential
    /// reference is resolved here, and the git and file IO run off the request thread because a cold clone is slow.</summary>
    private static async Task<T> ReadCloneAsync<T>(
        ISecretResolver secrets, CatalogRepoSource source, Func<Repository, T> read, CancellationToken ct)
    {
        var credentials = await GitMaterializer
            .ResolveCredentialsAsync(secrets, source.CredentialReference, source.CredentialUsername, ct)
            .ConfigureAwait(false);

        return await Task.Run(
            () =>
            {
                var workingDir = new GitMaterializer()
                    .EnsureHistoryClone(source.RemoteUrl, source.Branch, credentials, TreeFreshness, ct);
                using var repository = new Repository(workingDir);
                return read(repository);
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>A document's text as read: redacted, cut at <see cref="MaxPreviewChars"/>, with its size in bytes.</summary>
    private sealed record DocumentText(string Text, long SizeBytes, bool Truncated, bool Binary);

    /// <summary>Reads at most <see cref="MaxPreviewChars"/> characters of a document and disposes the stream. UTF-8
    /// unless a byte-order mark says otherwise. A NUL character marks content that is not text whatever its extension
    /// claims, and is reported rather than shown as noise.</summary>
    private static DocumentText ReadDocument(Stream stream, long sizeBytes)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 16);
        var buffer = new char[MaxPreviewChars + 1];
        var read = reader.ReadBlock(buffer, 0, buffer.Length);
        var kept = Math.Min(read, MaxPreviewChars);
        var text = new string(buffer, 0, kept);
        return text.Contains('\0', StringComparison.Ordinal)
            ? new DocumentText(string.Empty, sizeBytes, Truncated: false, Binary: true)
            : new DocumentText(SecretHygiene.RedactedMessage(text), sizeBytes, read > MaxPreviewChars, Binary: false);
    }

    /// <summary>The response for one read document: the text, a 404 when there was no such file, or a 400 when the
    /// file turned out not to be text.</summary>
    private static Results<Ok<RepoFileDto>, ProblemHttpResult> Served(
        string relative, string readFrom, DocumentText? document, string missing)
    {
        if (document is null)
        {
            return NotFound(missing);
        }

        return document.Binary
            ? BadRequest("No preview", $"'{relative}' holds binary content, not a YAML document.")
            : TypedResults.Ok(new RepoFileDto(relative, readFrom, document.SizeBytes, document.Text, document.Truncated));
    }

    /// <summary>A caller's path as a repo-relative, forward-slashed one, or null when it is blank, climbs out of the
    /// repository, or points into git's own metadata folder. A backslash is accepted as a separator, since the
    /// repositories here are authored on Windows.</summary>
    internal static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var segments = path.Trim().Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0
            || segments.Any(s => s is "." or ".." || s.Equals(".git", StringComparison.OrdinalIgnoreCase) || s.Contains(':', StringComparison.Ordinal)))
        {
            return null;
        }

        return string.Join('/', segments);
    }

    /// <summary>Whether a path names a YAML document by its extension.</summary>
    internal static bool IsYamlDocument(string path)
        => path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);

    /// <summary>Depth-first walk of a commit's tree, emitting a folder before its contents so the outline can be
    /// rebuilt from the paths alone. Stops at <see cref="MaxEntries"/>; submodules (git links) are not entered, since
    /// their contents belong to another repository.</summary>
    private static void Walk(Tree tree, string prefix, List<RepoTreeEntryDto> into, CancellationToken ct)
    {
        foreach (var entry in tree)
        {
            if (into.Count >= MaxEntries)
            {
                return;
            }

            ct.ThrowIfCancellationRequested();
            var path = prefix.Length == 0 ? entry.Name : $"{prefix}/{entry.Name}";
            switch (entry.TargetType)
            {
                case TreeEntryTargetType.Tree:
                    into.Add(new RepoTreeEntryDto(path, IsFolder: true, SizeBytes: 0));
                    Walk((Tree)entry.Target, path, into, ct);
                    break;
                case TreeEntryTargetType.Blob:
                    into.Add(new RepoTreeEntryDto(path, IsFolder: false, ((Blob)entry.Target).Size));
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>The same listing for a repo synced from a local path: the working tree on disk, minus git's own
    /// metadata folder, which is machinery rather than repository content.</summary>
    private static List<RepoTreeEntryDto> ReadFromDisk(string rootPath, CancellationToken ct)
    {
        var found = new List<RepoTreeEntryDto>();
        var pending = new Stack<string>();
        pending.Push(rootPath);

        while (pending.Count > 0 && found.Count < MaxEntries)
        {
            ct.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateFileSystemEntries(directory).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
            {
                // A folder the host may not read (or one that vanished mid-walk) is skipped rather than failing the
                // whole listing: the rest of the repository is still an honest answer.
                continue;
            }

            foreach (var child in children)
            {
                if (found.Count >= MaxEntries)
                {
                    break;
                }

                var isFolder = Directory.Exists(child);
                if (isFolder && string.Equals(Path.GetFileName(child), ".git", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(rootPath, child).Replace('\\', '/');
                if (isFolder)
                {
                    found.Add(new RepoTreeEntryDto(relative, IsFolder: true, SizeBytes: 0));
                    pending.Push(child);
                }
                else
                {
                    found.Add(new RepoTreeEntryDto(relative, IsFolder: false, FileSize(child)));
                }
            }
        }

        return found;
    }

    /// <summary>A file's size on disk, or 0 when the host cannot stat it. A size is a browsing aid, so an unreadable
    /// one drops the number rather than the file.</summary>
    private static long FileSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return 0;
        }
    }

    /// <summary>Sorts a walk into one path-ordered listing and reports whether the cap clipped it.</summary>
    private static RepoTreeDto Listing(string readFrom, List<RepoTreeEntryDto> entries)
    {
        var truncated = entries.Count >= MaxEntries;
        entries.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return new RepoTreeDto(readFrom, entries, truncated);
    }

    private static ProblemHttpResult NotFound(string detail)
        => TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status404NotFound, title: "Not found");

    private static ProblemHttpResult BadRequest(string title, string detail)
        => TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest, title: title);
}
