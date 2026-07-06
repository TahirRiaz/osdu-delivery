using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Catalog;

/// <summary>
/// The EF Core context for the shadow catalog (schema <c>catalog</c>). EF owns this schema: it is created and
/// upgraded by migrations (<see cref="CatalogDatabase.MigrateAsync"/>), never by hand. This is the only place in
/// the product that uses Entity Framework; the engine stays on direct ADO.NET.
/// </summary>
public sealed class CatalogDbContext : DbContext
{
    public const string SchemaName = "catalog";

    public CatalogDbContext(DbContextOptions<CatalogDbContext> options)
        : base(options)
    {
    }

    public DbSet<CatalogRepo> Repos => Set<CatalogRepo>();

    public DbSet<CatalogPipeline> Pipelines => Set<CatalogPipeline>();

    public DbSet<CatalogRun> Runs => Set<CatalogRun>();

    public DbSet<CatalogObject> Objects => Set<CatalogObject>();

    public DbSet<CatalogLineageEdge> LineageEdges => Set<CatalogLineageEdge>();

    public DbSet<CatalogFlowDependency> FlowDependencies => Set<CatalogFlowDependency>();

    public DbSet<CatalogRunFile> RunFiles => Set<CatalogRunFile>();

    public DbSet<CatalogRunAssertion> RunAssertions => Set<CatalogRunAssertion>();

    public DbSet<CatalogRunStatement> RunStatements => Set<CatalogRunStatement>();

    public DbSet<CatalogRunSurrogateKey> RunSurrogateKeys => Set<CatalogRunSurrogateKey>();

    public DbSet<CatalogRunHealthCheckMetric> RunHealthCheckMetrics => Set<CatalogRunHealthCheckMetric>();

    public DbSet<CatalogObjectColumn> ObjectColumns => Set<CatalogObjectColumn>();

    public DbSet<CatalogPipelineColumn> PipelineColumns => Set<CatalogPipelineColumn>();

    public DbSet<CatalogSchedule> Schedules => Set<CatalogSchedule>();

    public DbSet<CatalogNode> Nodes => Set<CatalogNode>();

    public DbSet<CatalogRepoSource> RepoSources => Set<CatalogRepoSource>();

    public DbSet<CatalogUser> Users => Set<CatalogUser>();

    public DbSet<CatalogRole> Roles => Set<CatalogRole>();

    public DbSet<CatalogAccessToken> AccessTokens => Set<CatalogAccessToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(SchemaName);

        modelBuilder.Entity<CatalogRepo>(entity =>
        {
            entity.ToTable("Repo");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Name).HasMaxLength(256).IsRequired();
            entity.Property(r => r.RemoteUrl).HasMaxLength(1024);
            entity.Property(r => r.RootPath).HasMaxLength(1024);
            entity.HasIndex(r => r.Name).IsUnique();
        });

        modelBuilder.Entity<CatalogPipeline>(entity =>
        {
            entity.ToTable("Pipeline");
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Name).HasMaxLength(400).IsRequired();
            entity.Property(p => p.Kind).HasMaxLength(16).IsRequired();
            entity.Property(p => p.Batch).HasMaxLength(250);
            entity.Property(p => p.RelativePath).HasMaxLength(1024).IsRequired();
            entity.Property(p => p.SourceServer).HasMaxLength(512);
            entity.Property(p => p.TargetServer).HasMaxLength(512);
            entity.Property(p => p.ContentHash).HasMaxLength(64);
            // A flow name is unique WITHIN a repo, not globally: the same name can exist in different repos and
            // they are distinct pipelines (the catalog spans repos).
            entity.HasIndex(p => new { p.RepoId, p.Name }).IsUnique();
            entity.HasIndex(p => p.Name);
            entity.HasIndex(p => p.Kind);
            entity.HasIndex(p => p.Active);
            entity.HasIndex(p => new { p.RepoId, p.Wave }); // list a repo's pipelines in execution order
        });

        modelBuilder.Entity<CatalogRun>(entity =>
        {
            entity.ToTable("Run");
            entity.HasKey(r => r.RunId);
            entity.Property(r => r.FlowName).HasMaxLength(400).IsRequired();
            entity.Property(r => r.FlowKind).HasMaxLength(16).IsRequired();
            entity.Property(r => r.Status).HasMaxLength(16).IsRequired();
            entity.Property(r => r.ClaimedByNode).HasMaxLength(256);
            entity.Property(r => r.TargetPool).HasMaxLength(128);
            entity.Property(r => r.CommitSha).HasMaxLength(64);
            entity.Property(r => r.FilePattern).HasMaxLength(200);
            entity.Property(r => r.Host).HasMaxLength(256);
            // PipelineId is a soft link (no FK): a run can outlive its pipeline being removed from git, so the
            // history stays even when the Pipeline row is gone. The GUI left-joins on it; it is indexed for that.
            entity.HasIndex(r => r.PipelineId);
            entity.HasIndex(r => r.RepoId);
            entity.HasIndex(r => r.FlowName);
            entity.HasIndex(r => r.WrittenUtc);
            // The work-queue claim seeks the oldest run in a given state: WHERE Status = 'queued' ORDER BY
            // EnqueuedUtc. This composite index makes that a range seek instead of a scan, and also serves the
            // "recover runs stuck running" recovery query.
            entity.HasIndex(r => new { r.Status, r.EnqueuedUtc });
            // The latest-run board resolves each pipeline's newest run (top-1 per pipeline by WrittenUtc, then
            // RunId): this composite serves that as a per-pipeline seek in exactly the query's order.
            entity.HasIndex(r => new { r.PipelineId, r.WrittenUtc, r.RunId }).IsDescending(false, true, true);
        });

        modelBuilder.Entity<CatalogObject>(entity =>
        {
            entity.ToTable("Object");
            entity.HasKey(o => o.Key);
            entity.Property(o => o.Key).HasMaxLength(900);
            entity.Property(o => o.ServerRef).HasMaxLength(512).IsRequired();
            entity.Property(o => o.Database).HasMaxLength(256);
            entity.Property(o => o.Schema).HasMaxLength(256);
            entity.Property(o => o.Name).HasMaxLength(512).IsRequired();
            entity.Property(o => o.Kind).HasMaxLength(32);
            // The module body is unbounded; nvarchar(max) so a long proc/view definition is never truncated.
            entity.Property(o => o.Definition);
            // The generating DDL is unbounded too (a wide CREATE TABLE, a long view body); nvarchar(max).
            entity.Property(o => o.Script);
            entity.Property(o => o.ScriptTier).HasMaxLength(16);
            // A DB-generated surrogate that serves only as the full-text KEY INDEX (Key is too wide to be one).
            entity.Property(o => o.FullTextKey).UseIdentityColumn();
            entity.HasIndex(o => o.FullTextKey).IsUnique();
            entity.HasIndex(o => o.Name);
            entity.HasIndex(o => o.ServerRef);
        });

        modelBuilder.Entity<CatalogLineageEdge>(entity =>
        {
            entity.ToTable("LineageEdge");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Flow).HasMaxLength(400);
            entity.Property(e => e.ViaModule).HasMaxLength(900);
            entity.Property(e => e.Relation).HasMaxLength(16).IsRequired();
            entity.Property(e => e.ObjectKey).HasMaxLength(900).IsRequired();
            entity.Property(e => e.ObjectName).HasMaxLength(512);
            entity.Property(e => e.Tier).HasMaxLength(16).IsRequired();
            // The hot queries: every edge on an object (cross-repo "what touches X"), and a flow's edges.
            entity.HasIndex(e => e.ObjectKey);
            entity.HasIndex(e => e.RepoId);
            entity.HasIndex(e => e.PipelineId);
        });

        modelBuilder.Entity<CatalogFlowDependency>(entity =>
        {
            entity.ToTable("FlowDependency");
            entity.HasKey(d => d.Id);
            entity.Property(d => d.FromFlow).HasMaxLength(400).IsRequired();
            entity.Property(d => d.ToFlow).HasMaxLength(400).IsRequired();
            entity.HasIndex(d => d.RepoId);
            entity.HasIndex(d => d.FromPipelineId);
            entity.HasIndex(d => d.ToPipelineId);
        });

        modelBuilder.Entity<CatalogRunFile>(entity =>
        {
            entity.ToTable("RunFile");
            entity.HasKey(f => f.Id);
            entity.Property(f => f.Name).HasMaxLength(512).IsRequired();
            entity.Property(f => f.Path).HasMaxLength(1024);
            entity.HasIndex(f => f.RunId);
        });

        modelBuilder.Entity<CatalogRunAssertion>(entity =>
        {
            entity.ToTable("RunAssertion");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Name).HasMaxLength(256).IsRequired();
            entity.Property(a => a.Result).HasMaxLength(512);
            entity.Property(a => a.AssertedValue).HasMaxLength(512);
            entity.HasIndex(a => a.RunId);
        });

        modelBuilder.Entity<CatalogRunStatement>(entity =>
        {
            entity.ToTable("RunStatement");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Step).HasMaxLength(128).IsRequired();
            // Sql is nvarchar(max): a generated statement (a CREATE TABLE, a MERGE) has no useful length bound.
            entity.Property(s => s.Sql).IsRequired();
            entity.HasIndex(s => s.RunId);
        });

        modelBuilder.Entity<CatalogRunSurrogateKey>(entity =>
        {
            entity.ToTable("RunSurrogateKey");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.SurrogateTable).HasMaxLength(776).IsRequired();
            entity.Property(s => s.SurrogateColumn).HasMaxLength(256).IsRequired();
            entity.HasIndex(s => s.RunId);
        });

        modelBuilder.Entity<CatalogRunHealthCheckMetric>(entity =>
        {
            entity.ToTable("RunHealthCheckMetric");
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Name).HasMaxLength(256).IsRequired();
            entity.Property(m => m.ModelTrainer).HasMaxLength(128);
            entity.HasIndex(m => m.RunId);
        });

        modelBuilder.Entity<CatalogObjectColumn>(entity =>
        {
            entity.ToTable("ObjectColumn");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.ObjectKey).HasMaxLength(900).IsRequired();
            entity.Property(c => c.Name).HasMaxLength(512).IsRequired();
            entity.Property(c => c.DataType).HasMaxLength(128);
            entity.Property(c => c.Tier).HasMaxLength(16).IsRequired();
            // Column-by-name search across every object.
            entity.HasIndex(c => c.Name);
            // One row per column position per object: a true invariant (sys.columns.column_id is unique per
            // object) and the idempotency backstop for the sync's delete-by-key + re-insert. The composite also
            // serves the "an object's columns, in order" drill-down (ObjectKey is the leftmost prefix).
            entity.HasIndex(c => new { c.ObjectKey, c.Ordinal }).IsUnique();
        });

        modelBuilder.Entity<CatalogPipelineColumn>(entity =>
        {
            entity.ToTable("PipelineColumn");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Kind).HasMaxLength(16).IsRequired();
            entity.Property(c => c.ColumnName).HasMaxLength(512).IsRequired();
            entity.Property(c => c.SourceColumn).HasMaxLength(512);
            // The expression and type are authored/generated SQL: bounded generously, never a blob, but wide
            // enough for a real CAST/CASE expression.
            entity.Property(c => c.Expression).HasMaxLength(4000);
            entity.Property(c => c.DataType).HasMaxLength(128);
            // A pipeline's transforms, in order, for the drill-down; and column-by-name search across the estate.
            entity.HasIndex(c => c.PipelineId);
            entity.HasIndex(c => c.RepoId);
            entity.HasIndex(c => c.ColumnName);
            // One row per column position per (pipeline, kind): the idempotency backstop for the sync/run's
            // delete-by-(pipeline, kind) + re-insert, and the "a pipeline's declared columns, in order" drill-down.
            entity.HasIndex(c => new { c.PipelineId, c.Kind, c.Ordinal }).IsUnique();
        });

        modelBuilder.Entity<CatalogSchedule>(entity =>
        {
            entity.ToTable("Schedule");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.FlowName).HasMaxLength(400).IsRequired();
            entity.Property(s => s.Cron).HasMaxLength(256);
            entity.Property(s => s.Timezone).HasMaxLength(64).IsRequired();
            entity.Property(s => s.Source).HasMaxLength(16).IsRequired();
            entity.HasIndex(s => s.RepoId);
            entity.HasIndex(s => s.PipelineId);
            // The scheduler scans for due, active schedules ordered by when they are next due:
            // WHERE Enabled = 1 AND Paused = 0 AND NextFireUtc <= now.
            entity.HasIndex(s => s.NextFireUtc);
        });

        modelBuilder.Entity<CatalogNode>(entity =>
        {
            entity.ToTable("Node");
            entity.HasKey(n => n.Name);
            entity.Property(n => n.Name).HasMaxLength(256);
            entity.Property(n => n.Version).HasMaxLength(64);
            // The fleet view lists nodes most-recently-seen first.
            entity.HasIndex(n => n.LastSeenUtc);
        });

        modelBuilder.Entity<CatalogRepoSource>(entity =>
        {
            entity.ToTable("RepoSource");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Name).HasMaxLength(256).IsRequired();
            entity.Property(s => s.RemoteUrl).HasMaxLength(1024).IsRequired();
            entity.Property(s => s.Branch).HasMaxLength(256).IsRequired();
            // A secret reference (${keyvault:...}/${env:...}), not a secret value: bounded, never a blob.
            entity.Property(s => s.CredentialReference).HasMaxLength(512);
            entity.Property(s => s.CredentialUsername).HasMaxLength(256);
            // A JSON array of excluded flow paths: unbounded (a large estate can exclude many files).
            entity.Property(s => s.ExcludedFlowPaths);
            entity.HasIndex(s => s.Name).IsUnique();
            // The sync loop scans for enabled sources whose next sync is due.
            entity.HasIndex(s => s.NextSyncUtc);
        });

        modelBuilder.Entity<CatalogRole>(entity =>
        {
            entity.ToTable("Role");
            entity.HasKey(r => r.Name);
            entity.Property(r => r.Name).HasMaxLength(64);
            entity.Property(r => r.Scopes).HasMaxLength(256).IsRequired();
            entity.Property(r => r.Description).HasMaxLength(512).IsRequired();
        });

        modelBuilder.Entity<CatalogUser>(entity =>
        {
            entity.ToTable("User");
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Username).HasMaxLength(256).IsRequired();
            entity.Property(u => u.Email).HasMaxLength(320);
            entity.Property(u => u.DisplayName).HasMaxLength(256);
            // PBKDF2 hashes render well under 512 chars; bounded so the column is never an accidental blob.
            entity.Property(u => u.PasswordHash).HasMaxLength(512);
            entity.Property(u => u.Role).HasMaxLength(64).IsRequired();
            entity.Property(u => u.Provider).HasMaxLength(16).IsRequired();
            entity.Property(u => u.ExternalObjectId).HasMaxLength(64);
            // One account per sign-in name across providers, so a token subject is always unambiguous.
            entity.HasIndex(u => u.Username).IsUnique();
            // JIT provisioning keys on the Entra object id; filtered unique so local users (null) do not collide.
            entity.HasIndex(u => u.ExternalObjectId).IsUnique().HasFilter("[ExternalObjectId] IS NOT NULL");
        });

        modelBuilder.Entity<CatalogAccessToken>(entity =>
        {
            entity.ToTable("AccessToken");
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Name).HasMaxLength(200).IsRequired();
            // SHA-256 hex is exactly 64 chars; bounded to that.
            entity.Property(t => t.TokenHash).HasMaxLength(64).IsRequired();
            entity.Property(t => t.Prefix).HasMaxLength(32).IsRequired();
            entity.Property(t => t.Scopes).HasMaxLength(256).IsRequired();
            // Authentication looks a presented token up by its hash: unique (a hash collision would be a distinct
            // secret) and the seek index for every authenticated PAT request.
            entity.HasIndex(t => t.TokenHash).IsUnique();
            // The owner's token list (self-service management) seeks by user.
            entity.HasIndex(t => t.UserId);
        });
    }
}
