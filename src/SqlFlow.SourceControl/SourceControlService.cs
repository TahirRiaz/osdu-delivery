using System.Diagnostics;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Secrets;
using SqlFlow.Core.SourceControl;

namespace SqlFlow.SourceControl;

/// <summary>
/// The end-to-end source-control run, one code path for the CLI and the tests: resolve the managed database's
/// connection, script every object with SMO, write the snapshot into the git working tree (recording adds,
/// changes, and deletions), then commit and push it. Operational failures are returned as a failed
/// <see cref="SourceControlResult"/> (so the run still writes its artifact), the same contract the other flow
/// runners follow. Git credentials and the connection are resolved from references at run time and never logged.
/// </summary>
public sealed class SourceControlService
{
    private readonly IConnectionResolver _resolver;
    private readonly ISecretResolver _secrets;
    private readonly IDatabaseScripter _scripter;
    private readonly Func<GitWorkspaceConfig, IGitWorkspace> _gitFactory;

    public SourceControlService(
        IConnectionResolver resolver,
        ISecretResolver secrets,
        IDatabaseScripter scripter,
        Func<GitWorkspaceConfig, IGitWorkspace>? gitFactory = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(scripter);
        _resolver = resolver;
        _secrets = secrets;
        _scripter = scripter;
        _gitFactory = gitFactory ?? (config => new LibGit2GitWorkspace(config));
    }

    public async Task<SourceControlResult> RunAsync(
        SourceControlFlow flow, SourceControlRunOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        options ??= new SourceControlRunOptions();

        var runId = options.RunId ?? Guid.NewGuid();
        var repository = flow.Repository;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var resolved = await _resolver
                .ResolveAsync(flow.ConnectionReference, ConnectionRole.Source, ct: ct)
                .ConfigureAwait(false);

            var scripted = _scripter.Script(resolved.CanonicalString, flow.Database, flow.Scripting, ct);

            var credentials = await BuildCredentialsAsync(repository, ct).ConfigureAwait(false);
            var config = new GitWorkspaceConfig
            {
                WorkingDirectory = repository.WorkingDirectory,
                Remote = repository.Remote,
                Branch = repository.Branch,
                AuthorName = repository.AuthorName,
                AuthorEmail = repository.AuthorEmail,
                Credentials = credentials,
                Push = options.Push,
                PathScope = scripted.DatabaseName,
            };

            var workspace = _gitFactory(config);
            workspace.EnsureReady(ct);

            var write = SnapshotWriter.Write(repository.WorkingDirectory, scripted, ct);

            var commit = options.DryRun
                ? new GitCommitResult { Committed = false, Pushed = false, FilesChanged = 0 }
                : workspace.Commit(BuildCommitMessage(flow.SysAlias, scripted, write), ct);

            stopwatch.Stop();
            return new SourceControlResult
            {
                RunId = runId,
                Success = true,
                DatabaseName = scripted.DatabaseName,
                WorkingDirectory = Path.GetFullPath(repository.WorkingDirectory),
                Remote = repository.Remote,
                Branch = repository.Branch,
                DryRun = options.DryRun,
                ObjectsScripted = scripted.Objects.Count,
                Added = write.Added.Count,
                Changed = write.Changed.Count,
                Deleted = write.Deleted.Count,
                Unchanged = write.Unchanged.Count,
                Committed = commit.Committed,
                CommitSha = commit.CommitSha,
                Pushed = commit.Pushed,
                Objects = scripted.Objects.Select(o => o.RelativePath).ToList(),
                AddedObjects = write.Added,
                ChangedObjects = write.Changed,
                DeletedObjects = write.Deleted,
                Warnings = scripted.Warnings,
                DurationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 3),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            return new SourceControlResult
            {
                RunId = runId,
                Success = false,
                Error = SecretHygiene.RedactedMessage(ex),
                WorkingDirectory = Path.GetFullPath(repository.WorkingDirectory),
                Remote = repository.Remote,
                Branch = repository.Branch,
                DryRun = options.DryRun,
                DurationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 3),
            };
        }
    }

    private async Task<GitCredentials?> BuildCredentialsAsync(SourceControlRepository repository, CancellationToken ct)
    {
        // Credentials are needed to clone a private remote and to push; without a remote the history is local
        // and no credential is resolved at all.
        if (repository.Remote is null || repository.Secret is null)
        {
            return null;
        }

        var secret = await _secrets.ResolveAsync(repository.Secret, ct).ConfigureAwait(false);
        var username = repository.Username is null
            ? null
            : await _secrets.ResolveAsync(repository.Username, ct).ConfigureAwait(false);

        return new GitCredentials
        {
            Username = string.IsNullOrWhiteSpace(username) ? null : username,
            Secret = secret,
        };
    }

    private static string BuildCommitMessage(string flowName, ScriptedDatabase scripted, SnapshotWriteResult write)
        => $"{flowName}: snapshot {scripted.DatabaseName} " +
           $"({write.Added.Count} added, {write.Changed.Count} changed, {write.Deleted.Count} deleted)";
}
