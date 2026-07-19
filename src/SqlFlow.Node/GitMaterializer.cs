using System.Collections.Concurrent;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Node;

/// <summary>Optional git credentials for cloning a private remote: a username plus a secret (a GitHub PAT or a
/// BitBucket app password). A node resolves these from its own environment; they live in memory only for the clone.</summary>
public sealed record GitMaterializerCredentials(string? Username, string Secret);

/// <summary>
/// Materializes a flow's exact committed version on a node: it ensures the repository is checked out at a specific
/// commit SHA in a local cache and returns that working directory, so a run executes the precise version that was
/// committed (reproducible) and a node can run a flow it has no locally synced copy of. Each SHA gets its own
/// directory under the cache (keyed by remote + SHA), so a materialized commit is reused across runs and different
/// commits never disturb each other. A node executes several claimed runs concurrently, so two runs pinned to the
/// same commit (a schedule firing several batches of one source at once) can materialize the same directory at the
/// same time; each working directory is guarded by its own lock so those runs serialize on the clone and the later
/// ones reuse the finished checkout, rather than racing into the same <c>.git</c> and colliding on git's
/// <c>config.lock</c>. The lock is process-wide (keyed by the absolute working directory), so every materializer
/// instance in the process coordinates on the shared cache; different nodes use their own caches and processes.
/// </summary>
public sealed class GitMaterializer
{
    // One monitor object per working directory, shared across every GitMaterializer in the process (the run worker
    // and the managed-sync service each hold their own instance but write the same cache root). A commit's clone and
    // its reuse fast-path both run under this lock, so concurrent runs for the same commit serialize and the later
    // ones return the finished checkout; runs for different commits take different locks and stay parallel. Entries
    // are never removed: the set of distinct materialized directories a process touches is small and each monitor is
    // tiny, so the map's footprint is negligible against the checkouts themselves.
    private static readonly ConcurrentDictionary<string, object> WorkingDirLocks = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _cacheRoot;

    /// <param name="cacheRoot">Where materialized commits are cached; defaults to a per-user temp location.</param>
    public GitMaterializer(string? cacheRoot = null)
        => _cacheRoot = cacheRoot ?? Path.Combine(Path.GetTempPath(), "sqlflow", "node-cache");

    private static object LockFor(string workingDir)
        => WorkingDirLocks.GetOrAdd(Path.GetFullPath(workingDir), static _ => new object());

    /// <summary>
    /// Ensures <paramref name="remoteUrl"/> is checked out at <paramref name="commitSha"/> in the cache and returns
    /// the working directory. Reuses an already-materialized commit; otherwise clones the remote and checks the
    /// commit out (fetching first if the commit is not yet present in an existing cache clone).
    /// </summary>
    public string Materialize(string remoteUrl, string commitSha, GitMaterializerCredentials? credentials, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(commitSha);

        var workingDir = Path.Combine(_cacheRoot, StableFolder(remoteUrl), commitSha);

        ct.ThrowIfCancellationRequested();

        // Serialize on this exact working directory: concurrent runs pinned to the same commit would otherwise both
        // clone into it and collide on git's config.lock. The first to enter clones; the rest wait and hit the reuse
        // fast-path below. Runs for other commits take other locks and stay parallel.
        lock (LockFor(workingDir))
        {
            // Reuse: a cache directory already checked out at this exact commit is taken as-is (this is the fast path
            // a waiter lands on once the first materialization of this commit has finished).
            if (Repository.IsValid(workingDir) && HeadIsAt(workingDir, commitSha))
            {
                return workingDir;
            }

            ct.ThrowIfCancellationRequested();

            try
            {
                // A partial or wrong-commit directory is rebuilt from scratch, so a previously interrupted
                // materialization never leaves a half-checked-out tree behind.
                DeleteDirectory(workingDir);

                Directory.CreateDirectory(workingDir);
                var options = new CloneOptions { Checkout = false };
                options.FetchOptions.CredentialsProvider = CredentialsProvider(credentials);
                Repository.Clone(remoteUrl, workingDir, options);

                using var repo = new Repository(workingDir);
                var commit = repo.Lookup<Commit>(commitSha)
                    ?? throw new SqlFlowNodeException($"commit '{commitSha}' was not found in '{remoteUrl}'.");
                Commands.Checkout(repo, commit);
                return workingDir;
            }
            catch (Exception ex) when (ex is LibGit2SharpException or IOException or UnauthorizedAccessException)
            {
                // Leave nothing usable behind on failure, so the next attempt re-materializes cleanly. Wrap the
                // cause (a clone error, or a filesystem error cleaning up a leftover checkout) with context so the
                // run records what could not be materialized instead of a bare "Directory not empty" message.
                TryDeleteDirectory(workingDir);
                throw new SqlFlowNodeException($"could not materialize '{remoteUrl}' at '{commitSha}': {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Materializes the current HEAD of a branch into a stable per-repo working directory and returns that
    /// directory plus the resolved commit SHA. Used by the managed catalog sync to pull a tracked repo before
    /// syncing it. Each call refreshes to the branch tip (a fresh checkout), so the synced estate always reflects
    /// the latest commit; the returned SHA is recorded so the sync is attributable to an exact commit.
    /// </summary>
    public (string WorkingDirectory, string CommitSha) MaterializeBranch(
        string remoteUrl, string branch, GitMaterializerCredentials? credentials, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteUrl);

        var workingDir = Path.Combine(_cacheRoot, StableFolder(remoteUrl), "branch");
        ct.ThrowIfCancellationRequested();

        // A branch sync tears down and re-clones this one per-repo directory, so two concurrent syncs of the same
        // repo (a second control-plane node, or a fast poll interval) would collide on it; the per-directory lock
        // serializes them. The key differs from any commit directory, so branch syncs and pinned runs never contend.
        lock (LockFor(workingDir))
        {
            try
            {
                // A fresh checkout each sync keeps the logic simple and correct (no fetch/merge edge cases); the
                // synced estate is small and the sync runs on an interval, so re-cloning the branch tip is an
                // acceptable cost.
                DeleteDirectory(workingDir);

                Directory.CreateDirectory(workingDir);
                var options = new CloneOptions { BranchName = string.IsNullOrWhiteSpace(branch) ? null : branch };
                options.FetchOptions.CredentialsProvider = CredentialsProvider(credentials);
                Repository.Clone(remoteUrl, workingDir, options);

                using var repo = new Repository(workingDir);
                var sha = repo.Head.Tip?.Sha
                    ?? throw new SqlFlowNodeException($"'{remoteUrl}' (branch '{branch}') has no commits to sync.");
                return (workingDir, sha);
            }
            catch (Exception ex) when (ex is LibGit2SharpException or IOException or UnauthorizedAccessException)
            {
                TryDeleteDirectory(workingDir);
                throw new SqlFlowNodeException($"could not pull '{remoteUrl}' (branch '{branch}'): {ex.Message}", ex);
            }
        }
    }

    private static bool HeadIsAt(string workingDir, string commitSha)
    {
        try
        {
            using var repo = new Repository(workingDir);
            var head = repo.Head.Tip?.Sha;
            return head is not null
                && (string.Equals(head, commitSha, StringComparison.OrdinalIgnoreCase)
                    || head.StartsWith(commitSha, StringComparison.OrdinalIgnoreCase));
        }
        catch (LibGit2SharpException)
        {
            return false;
        }
    }

    /// <summary>Resolves git credentials from the host's own environment (edge resolution): a token in
    /// <c>SQLFLOW_GIT_TOKEN</c> (with an optional <c>SQLFLOW_GIT_USERNAME</c>), or null when unset - which covers
    /// public and local-path remotes. The token never travels through the control plane or the queue.</summary>
    public static GitMaterializerCredentials? CredentialsFromEnvironment()
    {
        var token = Environment.GetEnvironmentVariable("SQLFLOW_GIT_TOKEN");
        return string.IsNullOrWhiteSpace(token)
            ? null
            : new GitMaterializerCredentials(Environment.GetEnvironmentVariable("SQLFLOW_GIT_USERNAME"), token);
    }

    /// <summary>
    /// Resolves the git credential for a clone from a stored <c>${scheme:locator}</c> reference (a Key Vault or
    /// environment reference held on the repo source; the secret value itself is created and maintained in the vault,
    /// never stored in the catalog). A blank reference falls back to <see cref="CredentialsFromEnvironment"/>, which
    /// covers public remotes and the single-credential deployment. The resolved secret lives in memory only for the
    /// clone. This is the one credential-resolution path shared by the control plane's managed sync and a compute
    /// node materializing a pinned commit.
    /// </summary>
    public static async Task<GitMaterializerCredentials?> ResolveCredentialsAsync(
        ISecretResolver resolver, string? credentialReference, string? username, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        if (string.IsNullOrWhiteSpace(credentialReference))
        {
            return CredentialsFromEnvironment();
        }

        var secret = await resolver.ResolveAsync(credentialReference, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new SqlFlowNodeException(
                $"the git credential reference '{credentialReference}' resolved to an empty value; create the secret in the vault it points to.");
        }

        return new GitMaterializerCredentials(string.IsNullOrWhiteSpace(username) ? null : username, secret);
    }

    private static CredentialsHandler? CredentialsProvider(GitMaterializerCredentials? credentials)
    {
        if (credentials is null)
        {
            return null;
        }

        // GitHub accepts any non-empty username alongside a token; its conventional placeholder is x-access-token.
        var username = string.IsNullOrWhiteSpace(credentials.Username) ? "x-access-token" : credentials.Username;
        return (_, _, _) => new UsernamePasswordCredentials { Username = username, Password = credentials.Secret };
    }

    private static string StableFolder(string remoteUrl)
    {
        // A filesystem-safe, collision-resistant folder name per remote (the URL can contain characters that are not
        // valid in a path), so each repo's commits cache under their own directory.
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(remoteUrl));
        return Convert.ToHexStringLower(hash)[..16];
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        // Delete in place if the filesystem cooperates. A node runs its cache on a container overlay filesystem,
        // where removing a directory that holds many small files (a .git object store) can transiently fail with
        // "Directory not empty" (ENOTEMPTY): the kernel finishes unlinking the children after the parent rmdir is
        // attempted, so a retry succeeds once the pending unlinks settle.
        if (TryPurgeDirectory(path))
        {
            return;
        }

        // Still not gone after retrying (a file is genuinely held open, or the overlay is being stubborn). A
        // leftover partial checkout must never wedge a run, so move the tree aside instead. A rename to a sibling
        // name on the same volume is atomic and immune to ENOTEMPTY, which frees the original path for a clean
        // re-clone even if the moved copy cannot be removed yet; the moved copy is then purged best-effort.
        var abandoned = path + ".stale-" + Guid.NewGuid().ToString("N");
        Directory.Move(path, abandoned);
        TryPurgeDirectory(abandoned);
    }

    /// <summary>
    /// Deletes <paramref name="path"/> and everything under it, retrying to absorb the transient
    /// "Directory not empty" / sharing-violation races that a container overlay filesystem raises while it settles
    /// pending unlinks. Returns true if the tree is gone, false if it still could not be removed after retrying.
    /// </summary>
    private static bool TryPurgeDirectory(string path)
    {
        const int maxAttempts = 6;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // A git working tree contains read-only objects under .git; clear the attribute before deleting.
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    var attributes = File.GetAttributes(file);
                    if (attributes.HasFlag(FileAttributes.ReadOnly))
                    {
                        File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                    }
                }

                Directory.Delete(path, recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= maxAttempts)
                {
                    return false;
                }

                // Back off a little longer each attempt to let the filesystem finish the outstanding unlinks.
                Thread.Sleep(25 * attempt);
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            DeleteDirectory(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a locked file is left for the OS / next attempt.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>A node-runtime failure (for example a git materialization that could not produce the requested commit).</summary>
public sealed class SqlFlowNodeException : Exception
{
    public SqlFlowNodeException(string message)
        : base(message)
    {
    }

    public SqlFlowNodeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
