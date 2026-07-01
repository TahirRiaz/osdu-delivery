namespace SqlFlow.Core.SourceControl;

/// <summary>
/// A source-control flow (flowType: scm): the V3 equivalent of one <c>flw.SysSourceControl</c> row. It scripts
/// the full object definition of one SQLFlow-managed SQL Server database to disk and commits the snapshot to a
/// git repository, so the schema's change history lives in version control. Re-running it over time is what
/// produces the diff history. The database is identified by a declared connection (resolved through the same
/// secretless pipeline as every other flow); the repository, its remote, and the git credentials are all
/// references, never literals.
/// </summary>
public sealed record SourceControlFlow
{
    /// <summary>The stable, name-derived flow id (logs and artifacts key on it without a control database).</summary>
    public required int FlowId { get; init; }

    /// <summary>The flow name (legacy SysAlias); the snapshot's identity and run-history folder.</summary>
    public required string SysAlias { get; init; }

    public string? Description { get; init; }

    /// <summary>The name of the connection (declared under <c>connections:</c>) for the database to script.</summary>
    public required string Server { get; init; }

    /// <summary>The connection reference the resolver expects: the declared connection addressed as an
    /// <c>@alias</c> (the same convention every other flow uses).</summary>
    public string ConnectionReference => "@" + Server;

    /// <summary>An explicit database name to script; null means the connection's default catalog (DB_NAME()).
    /// Scripted objects land under a folder named for the resolved database, matching the legacy layout.</summary>
    public string? Database { get; init; }

    public required SourceControlRepository Repository { get; init; }

    public SourceControlScripting Scripting { get; init; } = new();
}

/// <summary>
/// Where and how the snapshot is committed. <see cref="WorkingDirectory"/> is the local git working tree; with
/// a <see cref="Remote"/> set the snapshot is cloned/pushed there (BitBucket or GitHub over HTTPS), authenticated
/// by <see cref="Username"/> plus <see cref="Secret"/> (a BitBucket app password or a GitHub personal access
/// token). Both auth fields are <c>${...}</c> references resolved at push time, so no credential rests in YAML.
/// </summary>
public sealed record SourceControlRepository
{
    /// <summary>The local git working tree the objects are written into (absolute, or resolved against the
    /// document's directory by the CLI). Cloned from <see cref="Remote"/> on first use when a remote is set.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>The git remote URL (HTTPS) to push to; null keeps the history purely local (still a real git
    /// repository, just never pushed).</summary>
    public string? Remote { get; init; }

    /// <summary>The branch the snapshot commits land on. Created if the repository is empty.</summary>
    public string Branch { get; init; } = "main";

    /// <summary>A <c>${...}</c> reference to the git username (BitBucket username, or any value with a GitHub
    /// PAT). Required to push to <see cref="Remote"/>; ignored for local-only history.</summary>
    public string? Username { get; init; }

    /// <summary>A <c>${...}</c> reference to the git secret: a BitBucket app password or a GitHub personal
    /// access token. Required to push; never a literal.</summary>
    public string? Secret { get; init; }

    /// <summary>The commit author/committer display name.</summary>
    public string AuthorName { get; init; } = "SQLFlow";

    /// <summary>The commit author/committer email.</summary>
    public string AuthorEmail { get; init; } = "sqlflow@localhost";
}

/// <summary>
/// What the scripter captures. By default every supported object category (<see cref="SourceControlObjectTypes"/>)
/// is scripted schema-only; <see cref="IncludeTypes"/>/<see cref="ExcludeTypes"/> narrow that, and
/// <see cref="DataTables"/> additionally scripts the row data of the named tables (the legacy
/// <c>ScriptDataForTables</c>), as INSERT statements, for reference/seed tables worth versioning.
/// </summary>
public sealed record SourceControlScripting
{
    /// <summary>Tables whose row data is scripted (in addition to their schema), each a <c>schema.table</c>
    /// name (case-insensitive). Empty means schema-only for every table.</summary>
    public IReadOnlyList<string> DataTables { get; init; } = [];

    /// <summary>If non-empty, only these object categories are scripted; otherwise all supported categories are.
    /// Each entry is one of <see cref="SourceControlObjectTypes.All"/>.</summary>
    public IReadOnlyList<string> IncludeTypes { get; init; } = [];

    /// <summary>Object categories to skip, applied after <see cref="IncludeTypes"/>.</summary>
    public IReadOnlyList<string> ExcludeTypes { get; init; } = [];
}
