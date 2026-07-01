using SqlFlow.Core.Connections;
using SqlFlow.Core.Secrets;
using SqlFlow.Core.SourceControl;
using SqlFlow.SourceControl;
using SqlFlow.SqlServer;
using Xunit;

namespace SqlFlow.Tests.SourceControl;

/// <summary>
/// The orchestration contract, with the SMO scripter and the git workspace faked so the wiring is provable
/// without a database or a repository: a successful run scripts, writes the working tree, and commits; a dry
/// run writes but never commits; a scripting failure is returned as a failed result (so the run still records
/// an artifact) rather than thrown.
/// </summary>
public sealed class SourceControlServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_scm_svc_" + Guid.NewGuid().ToString("N"));

    public SourceControlServiceTests() => Directory.CreateDirectory(_dir);

    private SourceControlFlow Flow() => new()
    {
        FlowId = 1,
        SysAlias = "scm-test",
        Server = "DW",
        Repository = new SourceControlRepository { WorkingDirectory = _dir, AuthorName = "t", AuthorEmail = "t@t" },
    };

    private static IConnectionResolver Resolver()
    {
        var dataSource = new DataSource
        {
            Alias = "DW",
            Kind = DataSourceKind.MSSQL,
            ConnectionRef = "Server=(local);Database=Warehouse;Integrated Security=true;TrustServerCertificate=true",
            Credential = new CredentialProfile { Mode = CredentialMode.InlineConnectionString },
        };
        return WithoutDatabaseResolver.Build([dataSource], new SecretResolver([new EnvSecretProvider()]), SqlServerSourceProvider.CreateRegistry());
    }

    private static ScriptedDatabase Snapshot() => new()
    {
        DatabaseName = "Warehouse",
        Objects =
        [
            new ScriptedObject { Folder = "Table", Schema = "dbo", Name = "Customer", RelativePath = "Warehouse/Table/dbo.Customer.sql", Sql = "CREATE TABLE x;\n" },
            new ScriptedObject { Folder = "View", Schema = "dbo", Name = "vCustomer", RelativePath = "Warehouse/View/dbo.vCustomer.sql", Sql = "CREATE VIEW y AS SELECT 1;\n" },
        ],
    };

    [Fact]
    public async Task Run_Scripts_Writes_AndCommits()
    {
        var git = new RecordingGitWorkspace(new GitCommitResult { Committed = true, CommitSha = "abc123", Pushed = false, FilesChanged = 2 });
        var service = new SourceControlService(Resolver(), new SecretResolver([new EnvSecretProvider()]),
            new FakeScripter(Snapshot()), _ => git);

        var result = await service.RunAsync(Flow());

        Assert.True(result.Success);
        Assert.Equal("Warehouse", result.DatabaseName);
        Assert.Equal(2, result.ObjectsScripted);
        Assert.Equal(2, result.Added);
        Assert.True(result.Committed);
        Assert.Equal("abc123", result.CommitSha);
        Assert.True(git.Ready);
        Assert.NotNull(git.LastMessage);
        Assert.True(File.Exists(Path.Combine(_dir, "Warehouse", "Table", "dbo.Customer.sql")));
        Assert.Equal(2, result.Objects.Count);
    }

    [Fact]
    public async Task DryRun_WritesButNeverCommits()
    {
        var git = new RecordingGitWorkspace(new GitCommitResult { Committed = true, CommitSha = "should-not-be-used" });
        var service = new SourceControlService(Resolver(), new SecretResolver([new EnvSecretProvider()]),
            new FakeScripter(Snapshot()), _ => git);

        var result = await service.RunAsync(Flow(), new SourceControlRunOptions { DryRun = true });

        Assert.True(result.Success);
        Assert.True(result.DryRun);
        Assert.False(result.Committed);
        Assert.Null(git.LastMessage); // Commit was never called
        Assert.True(File.Exists(Path.Combine(_dir, "Warehouse", "Table", "dbo.Customer.sql"))); // but the tree was written
    }

    [Fact]
    public async Task ScriptingFailure_IsReturnedAsAFailedResult()
    {
        var git = new RecordingGitWorkspace(new GitCommitResult());
        var service = new SourceControlService(Resolver(), new SecretResolver([new EnvSecretProvider()]),
            new ThrowingScripter(), _ => git);

        var result = await service.RunAsync(Flow());

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.False(git.Ready); // failed before the workspace was touched
    }

    private sealed class FakeScripter(ScriptedDatabase snapshot) : IDatabaseScripter
    {
        public ScriptedDatabase Script(string connectionString, string? database, SourceControlScripting scripting, CancellationToken ct = default)
            => snapshot;
    }

    private sealed class ThrowingScripter : IDatabaseScripter
    {
        public ScriptedDatabase Script(string connectionString, string? database, SourceControlScripting scripting, CancellationToken ct = default)
            => throw new InvalidOperationException("login failed for user");
    }

    private sealed class RecordingGitWorkspace(GitCommitResult result) : IGitWorkspace
    {
        public bool Ready { get; private set; }
        public string? LastMessage { get; private set; }

        public void EnsureReady(CancellationToken ct = default) => Ready = true;

        public GitCommitResult Commit(string message, CancellationToken ct = default)
        {
            LastMessage = message;
            return result;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
