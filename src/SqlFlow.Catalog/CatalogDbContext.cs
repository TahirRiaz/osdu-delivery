using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Catalog;

/// <summary>
/// The EF Core context for the shadow catalog (schema <c>catalog</c>). EF owns this schema: it is created and
/// created from this model (<see cref="CatalogDatabase.ProvisionAsync"/>), never by hand. This is the only place in
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

    public DbSet<CatalogRunGroup> RunGroups => Set<CatalogRunGroup>();

    public DbSet<CatalogFlowVersion> FlowVersions => Set<CatalogFlowVersion>();

    public DbSet<CatalogRunEvent> RunEvents => Set<CatalogRunEvent>();

    public DbSet<CatalogMaintenanceSetting> MaintenanceSettings => Set<CatalogMaintenanceSetting>();

    public DbSet<CatalogSchedule> Schedules => Set<CatalogSchedule>();

    public DbSet<CatalogScheduleMember> ScheduleMembers => Set<CatalogScheduleMember>();

    public DbSet<CatalogScheduleParent> ScheduleParents => Set<CatalogScheduleParent>();

    public DbSet<CatalogNode> Nodes => Set<CatalogNode>();

    public DbSet<CatalogWorkerPoolDesired> WorkerPools => Set<CatalogWorkerPoolDesired>();

    public DbSet<CatalogRepoSource> RepoSources => Set<CatalogRepoSource>();

    public DbSet<CatalogActivityEvent> ActivityEvents => Set<CatalogActivityEvent>();

    public DbSet<CatalogUser> Users => Set<CatalogUser>();

    public DbSet<CatalogRole> Roles => Set<CatalogRole>();

    public DbSet<CatalogAccessToken> AccessTokens => Set<CatalogAccessToken>();

    public DbSet<CatalogComputeTask> ComputeTasks => Set<CatalogComputeTask>();

    public DbSet<CatalogNotificationEvent> NotificationEvents => Set<CatalogNotificationEvent>();

    public DbSet<CatalogNotificationSubscription> NotificationSubscriptions => Set<CatalogNotificationSubscription>();

    public DbSet<CatalogNotificationDelivery> NotificationDeliveries => Set<CatalogNotificationDelivery>();

    public DbSet<CatalogNotificationWatermark> NotificationWatermarks => Set<CatalogNotificationWatermark>();

    /// <summary>The estate digests: periodic and on-demand summaries of the notification event stream.</summary>
    public DbSet<CatalogNotificationDigest> NotificationDigests => Set<CatalogNotificationDigest>();

    // The delivery ledger (schema delivery): see DeliveryEntities.cs.
    public DbSet<DeliverySubmission> DeliverySubmissions => Set<DeliverySubmission>();

    /// <summary>The records sources sent in submission requests rather than in drops.</summary>
    public DbSet<DeliveryInlineSubmission> DeliveryInlineSubmissions => Set<DeliveryInlineSubmission>();

    public DbSet<DeliveryRecord> DeliveryRecords => Set<DeliveryRecord>();

    /// <summary>The <c>delivery.RecordCount</c> indexed view (SQL Server only): a flow's records counted by status.</summary>
    public DbSet<DeliveryRecordCount> DeliveryRecordCounts => Set<DeliveryRecordCount>();

    public DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();

    public DbSet<DeliveryWorkBatch> DeliveryWorkBatches => Set<DeliveryWorkBatch>();

    public DbSet<DeliverySourceWatermark> DeliveryWatermarks => Set<DeliverySourceWatermark>();

    public DbSet<DeliveryActivity> DeliveryActivities => Set<DeliveryActivity>();

    public DbSet<DeliveryMapping> DeliveryMappings => Set<DeliveryMapping>();

    public DbSet<DeliverySnapshot> DeliverySnapshots => Set<DeliverySnapshot>();

    public DbSet<DeliveryCacheDefinition> DeliveryCacheDefinitions => Set<DeliveryCacheDefinition>();

    public DbSet<DeliveryCacheSet> DeliveryCacheSets => Set<DeliveryCacheSet>();

    public DbSet<DeliveryCacheSetEntry> DeliveryCacheSetEntries => Set<DeliveryCacheSetEntry>();

    public DbSet<DeliveryUpdateTag> DeliveryUpdateTags => Set<DeliveryUpdateTag>();

    public DbSet<DeliverySnapshotItem> DeliverySnapshotItems => Set<DeliverySnapshotItem>();

    public DbSet<DeliveryRetrieval> DeliveryRetrievals => Set<DeliveryRetrieval>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(SchemaName);
        DeliveryModel.Configure(modelBuilder, Database.IsSqlServer());

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
            entity.Property(p => p.ExecutionMode).HasMaxLength(16).IsRequired();
            entity.Property(p => p.Lifecycle).HasMaxLength(16).IsRequired();
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
            entity.Property(r => r.FlowVersionHash).HasMaxLength(64);
            entity.Property(r => r.Operation).HasMaxLength(16).IsRequired();
            entity.Property(r => r.Host).HasMaxLength(256);
            // The requested submission (a re-run) and the produced one: a submission's page lists both.
            entity.HasIndex(r => r.SubmissionId);
            entity.HasIndex(r => r.ResultSubmissionId);
            entity.HasIndex(r => new { r.PipelineId, r.Operation });
            entity.Property(r => r.TriggerSource).HasMaxLength(16);
            entity.Property(r => r.RequestedBy).HasMaxLength(200);
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
            // The run-trace prune identifies its candidates as terminal-but-not-failed runs older than the retention
            // window: WHERE Status IN ('succeeded','cancelled','skipped') AND WrittenUtc < @cutoff. This composite
            // makes that a seek and carries PipelineId so the "is there a newer run for this pipeline" check that
            // decides the candidate is not the latest is covered without a lookup back to the base row.
            entity.HasIndex(r => new { r.Status, r.WrittenUtc }).IncludeProperties(r => r.PipelineId);
            // The group-gating claim subquery asks "does this group have an unfinished member in a lower wave":
            // WHERE GroupId = @g AND GroupWave < @w AND Status IN ('queued','running'). This composite makes that
            // a seek, and also serves the group-detail board (a group's members by wave) and the groupId filter.
            entity.HasIndex(r => new { r.GroupId, r.GroupWave, r.Status });
            // At most ONE running execution per pipeline, enforced by the database instead of by the claim's own
            // check. The claim's "no running sibling of this pipeline" gate is a plain read under READ COMMITTED,
            // so two nodes claiming two different queued runs of the SAME pipeline can each observe no running
            // sibling and both claim it (write skew: neither read sees the other's uncommitted flip). That matters
            // because the engine names a flow's work tables per FLOW, not per run, so two overlapping executions
            // share (and drop) one staging table. This filtered unique index makes the gate atomic: the loser's
            // claim fails with a duplicate key, which ClaimNextAsync reads as "another node just took this
            // pipeline" and answers by moving on to the next candidate run.
            // Fan-out members (a delivery flow spreading one run's intake or drains across the fleet) are the one
            // sanctioned way to have several runs of a pipeline executing at once: they share the ledger's leases,
            // never a staging table. They are exempt from the index; the claim gate keeps them within one family.
            entity.HasIndex(r => r.PipelineId, "UX_Run_RunningPipeline")
                .IsUnique()
                .HasFilter($"[Status] = '{RunStatuses.Running}' AND [FanOutRoot] IS NULL");
            // A fan-out root reads its members' state while it waits for them, and cancels them with itself.
            entity.HasIndex(r => new { r.FanOutRoot, r.Status });
        });

        modelBuilder.Entity<CatalogRunGroup>(entity =>
        {
            entity.ToTable("RunGroup");
            entity.HasKey(g => g.GroupId);
            entity.Property(g => g.Mode).HasMaxLength(16).IsRequired();
            entity.Property(g => g.Anchor).HasMaxLength(400).IsRequired();
            entity.Property(g => g.CommitSha).HasMaxLength(64);
            // A repo's run groups, newest first, for the group board.
            entity.HasIndex(g => new { g.RepoId, g.EnqueuedUtc });
        });

        modelBuilder.Entity<CatalogFlowVersion>(entity =>
        {
            entity.ToTable("FlowVersion");
            // Content-addressed: the hash IS the identity (same hash = same bytes), assigned by code, never by
            // the database. Yaml stays nvarchar(max): a flow document has no useful length bound.
            entity.HasKey(v => v.ContentHash);
            entity.Property(v => v.ContentHash).HasMaxLength(64).ValueGeneratedNever();
            entity.Property(v => v.Yaml).IsRequired();
        });

        modelBuilder.Entity<CatalogRunEvent>(entity =>
        {
            entity.ToTable("RunEvent");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Level).HasMaxLength(16).IsRequired();
            entity.Property(e => e.Step).HasMaxLength(128);
            // Message is nvarchar(max): an event message (an engine decision, an error detail) has no useful
            // length bound.
            entity.Property(e => e.Message).IsRequired();
            entity.HasIndex(e => e.RunId);
        });

        modelBuilder.Entity<CatalogMaintenanceSetting>(entity =>
        {
            entity.ToTable("MaintenanceSetting");
            entity.HasKey(s => s.Id);
            // A single operator-set row; the id is the fixed singleton key (1), never database-generated.
            entity.Property(s => s.Id).ValueGeneratedNever();
            entity.Property(s => s.UpdatedBy).HasMaxLength(256);
        });

        modelBuilder.Entity<CatalogSchedule>(entity =>
        {
            entity.ToTable("Schedule");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Name).HasMaxLength(400).IsRequired();
            entity.Property(s => s.Cron).HasMaxLength(256);
            entity.Property(s => s.Timezone).HasMaxLength(64).IsRequired();
            entity.Property(s => s.Source).HasMaxLength(16).IsRequired();
            entity.Property(s => s.Operation).HasMaxLength(16).IsRequired();
            entity.Property(s => s.DefinitionPath).HasMaxLength(1024);
            entity.Property(s => s.DefinitionFlow).HasMaxLength(400);
            entity.Property(s => s.LastStaleParents).HasMaxLength(2000);
            entity.HasIndex(s => s.RepoId);
            // A name is what flows join, so it identifies exactly one schedule in a repo. The estate scan already
            // collapses a redefined name to the first definition; the unique index is what keeps two sync paths (or
            // a yaml schedule and an api one) from racing a second row into existence behind it.
            entity.HasIndex(s => new { s.RepoId, s.Name }).IsUnique();
            // The scheduler scans for due, active schedules ordered by when they are next due:
            // WHERE Enabled = 1 AND Paused = 0 AND NextFireUtc <= now.
            entity.HasIndex(s => s.NextFireUtc);
        });

        modelBuilder.Entity<CatalogScheduleParent>(entity =>
        {
            entity.ToTable("ScheduleParent");
            // A schedule names a given parent at most once; the composite key makes a repeated declaration idempotent
            // rather than a duplicate that would make the fan-in wait on the same parent twice and stall nothing but
            // still read wrong in the API.
            entity.HasKey(p => new { p.ScheduleId, p.ParentName });
            entity.Property(p => p.ParentName).HasMaxLength(400).IsRequired();
            entity.HasIndex(p => p.RepoId);
            // The chained scan is the other half of the scheduler tick, and it drives from this table: every parent
            // row joins to its parent schedule by (RepoId, ParentName). Chained schedules are a small minority of the
            // estate, so scanning this table rather than filtering the whole Schedule table keeps the tick cheap.
            entity.HasIndex(p => new { p.RepoId, p.ParentName });
            entity.HasOne(p => p.Schedule)
                  .WithMany(s => s.Parents)
                  .HasForeignKey(p => p.ScheduleId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CatalogScheduleMember>(entity =>
        {
            entity.ToTable("ScheduleMember");
            // A flow joins a schedule at most once; the composite key makes a repeated join idempotent rather than
            // a duplicate that would enqueue the flow twice in one fire.
            entity.HasKey(m => new { m.ScheduleId, m.PipelineId });
            entity.Property(m => m.FlowName).HasMaxLength(400).IsRequired();
            entity.HasIndex(m => m.RepoId);
            // "Which schedules is this flow in?": the flow detail page's Schedules tab, and the only way a flow that
            // declares no schedule of its own can still show the one that runs it.
            entity.HasIndex(m => m.PipelineId);
        });

        modelBuilder.Entity<CatalogNode>(entity =>
        {
            entity.ToTable("Node");
            entity.HasKey(n => n.Name);
            entity.Property(n => n.Name).HasMaxLength(256);
            entity.Property(n => n.Version).HasMaxLength(64);
            entity.Property(n => n.Pool).HasMaxLength(128);
            // The fleet view lists nodes most-recently-seen first, and counts online nodes per pool.
            entity.HasIndex(n => n.LastSeenUtc);
            entity.HasIndex(n => n.Pool);
        });

        modelBuilder.Entity<CatalogWorkerPoolDesired>(entity =>
        {
            entity.ToTable("WorkerPool");
            // One desired-state row per pool; the empty string keys the default (untargeted) pool.
            entity.HasKey(p => p.Pool);
            entity.Property(p => p.Pool).HasMaxLength(128);
            entity.Property(p => p.UpdatedBy).HasMaxLength(256);
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

        modelBuilder.Entity<CatalogActivityEvent>(entity =>
        {
            entity.ToTable("ActivityEvent");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Kind).HasMaxLength(32).IsRequired();
            entity.Property(e => e.SubjectKey).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Level).HasMaxLength(16).IsRequired();
            entity.Property(e => e.Step).HasMaxLength(64);
            entity.Property(e => e.Status).HasMaxLength(16);
            // Message is nvarchar(max): an activity line (a clone error, a lineage warning) has no useful bound.
            entity.Property(e => e.Message).IsRequired();
            // The trace stream tails one (kind, subject)'s events in id order; also the prune-old-activities scan.
            entity.HasIndex(e => new { e.Kind, e.SubjectKey, e.Id });
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

        modelBuilder.Entity<CatalogComputeTask>(entity =>
        {
            entity.ToTable("ComputeTask");
            entity.HasKey(t => t.TaskId);
            entity.Property(t => t.Operation).HasMaxLength(32).IsRequired();
            entity.Property(t => t.SourceRef).HasMaxLength(512).IsRequired();
            entity.Property(t => t.ProviderKind).HasMaxLength(16);
            // The payload is compact JSON of a bounded contract; nvarchar(max) only because a long ${keyvault:...}
            // reference plus arguments has no single useful column bound.
            entity.Property(t => t.ArgumentsJson).IsRequired();
            entity.Property(t => t.TargetPool).HasMaxLength(128);
            entity.Property(t => t.Status).HasMaxLength(16).IsRequired();
            entity.Property(t => t.RequestedBy).HasMaxLength(256);
            entity.Property(t => t.ClaimedByNode).HasMaxLength(256);
            // Error and ResultJson are nvarchar(max): a provider error can be long, and the result is the
            // operation's JSON document (bounded by the executor's result-size cap, not by a column length).
            // The work-queue claim seeks the oldest queued task, exactly like the run claim.
            entity.HasIndex(t => new { t.Status, t.EnqueuedUtc });
            // The GUI lists recent tasks newest first, optionally per source.
            entity.HasIndex(t => t.EnqueuedUtc);
            entity.HasIndex(t => t.SourceRef);
        });

        modelBuilder.Entity<CatalogNotificationEvent>(entity =>
        {
            entity.ToTable("NotificationEvent");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Kind).HasMaxLength(32).IsRequired();
            entity.Property(e => e.FlowName).HasMaxLength(400).IsRequired();
            entity.Property(e => e.FlowKind).HasMaxLength(16).IsRequired();
            // Error is nvarchar(max): the run's error text, no useful bound.
            // Detection inserts each (run, kind) at most once: the dedup that makes re-scanning the watermark's
            // overlap window (and any freak concurrent detection) free instead of a source of duplicate alerts.
            entity.HasIndex(e => new { e.RunId, e.Kind }).IsUnique();
            // Retention prunes by detection time.
            entity.HasIndex(e => e.DetectedUtc);
        });

        modelBuilder.Entity<CatalogNotificationSubscription>(entity =>
        {
            entity.ToTable("NotificationSubscription");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Channel).HasMaxLength(16).IsRequired();
            entity.Property(s => s.Mode).HasMaxLength(16).IsRequired();
            entity.Property(s => s.Kinds).HasMaxLength(128).IsRequired();
            entity.Property(s => s.FlowPattern).HasMaxLength(400);
            entity.Property(s => s.EmailAddress).HasMaxLength(320);
            entity.Property(s => s.SlackTarget).HasMaxLength(64);
            // The owner's self-service list.
            entity.HasIndex(s => s.UserId);
            // The dispatch scan: enabled subscriptions whose next-due has arrived (or is null = immediate).
            entity.HasIndex(s => new { s.Enabled, s.NextDueUtc });
        });

        modelBuilder.Entity<CatalogNotificationDelivery>(entity =>
        {
            entity.ToTable("NotificationDelivery");
            entity.HasKey(d => d.Id);
            entity.Property(d => d.Channel).HasMaxLength(16).IsRequired();
            entity.Property(d => d.Target).HasMaxLength(512).IsRequired();
            entity.Property(d => d.Subject).HasMaxLength(512).IsRequired();
            entity.Property(d => d.Status).HasMaxLength(16).IsRequired();
            // TextBody / HtmlBody / SlackBlocksJson / LastError are nvarchar(max): composed message content and
            // channel error payloads have no useful column bound.
            entity.Property(d => d.TextBody).IsRequired();
            // The send loop seeks queued deliveries whose next attempt is due, oldest first.
            entity.HasIndex(d => new { d.Status, d.NextAttemptUtc });
            // The owner's self-service history, newest first; CreatedUtc alone serves retention pruning.
            entity.HasIndex(d => new { d.UserId, d.CreatedUtc });
            entity.HasIndex(d => d.CreatedUtc);
        });

        modelBuilder.Entity<CatalogNotificationWatermark>(entity =>
        {
            entity.ToTable("NotificationWatermark");
            entity.HasKey(w => w.Id);
            // The single row's id is assigned by code (WellKnownId), never by the database.
            entity.Property(w => w.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<CatalogNotificationDigest>(entity =>
        {
            entity.ToTable("NotificationDigest");
            entity.HasKey(d => d.Id);
            entity.Property(d => d.Origin).HasMaxLength(16).IsRequired();
            entity.Property(d => d.Subject).HasMaxLength(512).IsRequired();
            // TextBody / HtmlBody / SlackBlocksJson / GroupsJson are nvarchar(max): rendered message content and
            // the persisted per-flow grouping have no useful column bound (both are capped in the composer).
            entity.Property(d => d.TextBody).IsRequired();
            entity.Property(d => d.HtmlBody).IsRequired();
            entity.Property(d => d.SlackBlocksJson).IsRequired();
            entity.Property(d => d.GroupsJson).IsRequired();
            // The GUI list (newest first) and the retention prune both read this column.
            entity.HasIndex(d => d.GeneratedUtc);
            // "The latest scheduled digest": the generator's self-healing check that a window is not re-covered.
            entity.HasIndex(d => new { d.Origin, d.PeriodEndUtc });
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
