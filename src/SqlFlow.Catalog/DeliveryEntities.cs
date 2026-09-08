using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Catalog;

// The delivery ledger (docs/delivery/ledger.md): submissions, records and append-only attempts, the audit trail of
// interventions, the tier-0 source watermarks, and the read model of the mapping and snapshot documents the sync
// found in the repositories. Statuses are stored as short strings so the tables read without a decoder ring;
// column names are camelCase to match the document model. Everything the GUI lists is index-backed.

/// <summary>One drop handed over by the preparing side. Its id is the idempotency key.</summary>
public sealed class DeliverySubmission
{
    public Guid SubmissionId { get; set; }

    public Guid FlowId { get; set; }

    public string FlowName { get; set; } = string.Empty;

    public string MappingReference { get; set; } = string.Empty;

    public string RenderContext { get; set; } = string.Empty;

    public string DropLocation { get; set; } = string.Empty;

    public string ParametersJson { get; set; } = "{}";

    public int RecordCount { get; set; }

    public string Status { get; set; } = "received";

    public DateTime ReceivedUtc { get; set; }

    public DateTime? StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    public int Planned { get; set; }

    public int SkippedUnchanged { get; set; }

    public int Blocked { get; set; }

    public int Delivered { get; set; }

    public int Held { get; set; }

    public int Failed { get; set; }

    public string? Error { get; set; }
}

/// <summary>The current state of one deliverable, keyed by its deterministic delivery key.</summary>
public sealed class DeliveryRecord
{
    public Guid DeliveryKey { get; set; }

    public Guid FlowId { get; set; }

    public string SourceKey { get; set; } = string.Empty;

    public string? Label { get; set; }

    public string MappingName { get; set; } = string.Empty;

    public string? RenderContext { get; set; }

    public string? SourceFingerprint { get; set; }

    public string? MetadataHash { get; set; }

    public string? PayloadHash { get; set; }

    public string? TargetId { get; set; }

    public long? TargetVersion { get; set; }

    public string Status { get; set; } = "pending";

    public DateTime? LastDeliveredUtc { get; set; }

    public DateTime? LastVerifiedUtc { get; set; }

    public string? LastVerifyOutcome { get; set; }

    public string? LeaseOwner { get; set; }

    public DateTime? LeaseExpiresUtc { get; set; }

    public Guid? LastSubmissionId { get; set; }

    public int AttemptCount { get; set; }

    public DateTime? NextAttemptUtc { get; set; }

    public string? LastError { get; set; }

    public string? PendingDocument { get; set; }

    public string? PendingRenderContext { get; set; }

    public string? PendingSourceFingerprint { get; set; }

    public string? PendingMetadataHash { get; set; }

    public string? PendingPayloadHash { get; set; }

    public string? PendingPayloadLocation { get; set; }

    public bool PendingMetadata { get; set; }

    public bool PendingPayload { get; set; }

    public bool Blocked { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }
}

/// <summary>One delivery try, append-only: the record's history.</summary>
public sealed class DeliveryAttempt
{
    public long AttemptId { get; set; }

    public Guid DeliveryKey { get; set; }

    public Guid? SubmissionId { get; set; }

    /// <summary>The platform run the attempt happened in, when it did (operator actions carry none).</summary>
    public Guid? RunId { get; set; }

    public string Worker { get; set; } = string.Empty;

    public DateTime StartedUtc { get; set; }

    public DateTime CompletedUtc { get; set; }

    public string Outcome { get; set; } = string.Empty;

    public string Phase { get; set; } = string.Empty;

    public string? MetadataHash { get; set; }

    public string? PayloadHash { get; set; }

    public long? TargetVersion { get; set; }

    public string? Error { get; set; }
}

/// <summary>Tier-0 watermark: the source table version of one flow scope (the parameter set).</summary>
public sealed class DeliverySourceWatermark
{
    public Guid FlowId { get; set; }

    public string Scope { get; set; } = string.Empty;

    public string TableName { get; set; } = string.Empty;

    public long Version { get; set; }

    public DateTime RecordedUtc { get; set; }
}

/// <summary>The audit trail of runs and interventions: who did what, when, with which inputs, and the outcome.</summary>
public sealed class DeliveryActivity
{
    public long ActivityId { get; set; }

    public Guid FlowId { get; set; }

    public string FlowName { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string Actor { get; set; } = string.Empty;

    public DateTime StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    public string Outcome { get; set; } = "running";

    public string? ParametersJson { get; set; }

    public Guid? SubmissionId { get; set; }

    public Guid? DeliveryKey { get; set; }

    /// <summary>The platform run the activity ran as, when it was a run (deliver, verify, known-state).</summary>
    public Guid? RunId { get; set; }

    public string? Summary { get; set; }

    public string? Log { get; set; }
}

/// <summary>A mapping document as the sync found it in a repository: the read model behind the mappings page.</summary>
public sealed class DeliveryMapping
{
    /// <summary>Stable id: derived from the repo id and the mapping reference.</summary>
    public Guid Id { get; set; }

    public Guid RepoId { get; set; }

    /// <summary>Name@version.</summary>
    public string Reference { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public string ContentHash { get; set; } = string.Empty;

    /// <summary>The document text, secret-redacted, as it was when synced.</summary>
    public string Yaml { get; set; } = string.Empty;

    /// <summary>A parsed summary (source system, natural key, scopes, property and fixture counts), for listings.</summary>
    public string SummaryJson { get; set; } = "{}";

    /// <summary>valid or invalid.</summary>
    public string Status { get; set; } = "valid";

    public string? Message { get; set; }

    public DateTime FirstSeenUtc { get; set; }

    public DateTime LastSeenUtc { get; set; }
}

/// <summary>A schema or reference snapshot version as the sync found it in a repository's snapshot store.</summary>
public sealed class DeliverySnapshot
{
    /// <summary>Stable id: derived from the repo id, the kind and the version label.</summary>
    public Guid Id { get; set; }

    public Guid RepoId { get; set; }

    /// <summary>schema or references.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The OSDU kind for a schema snapshot; the version label for a reference snapshot.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The content-derived version: the schema hash prefix, or the reference version label.</summary>
    public string Version { get; set; } = string.Empty;

    public DateTime? CapturedUtc { get; set; }

    /// <summary>Whether this reference version is the one <c>pinned</c> resolves to.</summary>
    public bool Current { get; set; }

    public string RelativePath { get; set; } = string.Empty;

    /// <summary>A parsed summary (reference type names and item counts, or the schema's required properties).</summary>
    public string SummaryJson { get; set; } = "{}";

    public DateTime FirstSeenUtc { get; set; }

    public DateTime LastSeenUtc { get; set; }
}

/// <summary>The EF model of the delivery ledger, in the <c>delivery</c> schema of the catalog database.</summary>
public static class DeliveryModel
{
    public const string SchemaName = "delivery";

    public static void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<DeliverySubmission>(e =>
        {
            e.ToTable("Submission", SchemaName);
            e.HasKey(s => s.SubmissionId);
            e.Property(s => s.FlowName).HasMaxLength(200).IsRequired();
            e.Property(s => s.MappingReference).HasMaxLength(200).IsRequired();
            e.Property(s => s.RenderContext).IsRequired();
            e.Property(s => s.DropLocation).HasMaxLength(2000).IsRequired();
            e.Property(s => s.ParametersJson).IsRequired();
            e.Property(s => s.Status).HasMaxLength(16).IsRequired();
            e.Property(s => s.Error).HasMaxLength(4000);
            e.HasIndex(s => new { s.FlowId, s.ReceivedUtc });
            e.HasIndex(s => new { s.FlowId, s.Status });
        });

        modelBuilder.Entity<DeliveryRecord>(e =>
        {
            e.ToTable("Record", SchemaName);
            e.HasKey(r => r.DeliveryKey);
            e.Property(r => r.SourceKey).HasMaxLength(400).IsRequired();
            e.Property(r => r.Label).HasMaxLength(400);
            e.Property(r => r.MappingName).HasMaxLength(200).IsRequired();
            e.Property(r => r.SourceFingerprint).HasMaxLength(200);
            e.Property(r => r.MetadataHash).HasMaxLength(64);
            e.Property(r => r.PayloadHash).HasMaxLength(64);
            e.Property(r => r.TargetId).HasMaxLength(500);
            e.Property(r => r.Status).HasMaxLength(16).IsRequired();
            e.Property(r => r.LastVerifyOutcome).HasMaxLength(16);
            e.Property(r => r.LeaseOwner).HasMaxLength(200);
            e.Property(r => r.LastError).HasMaxLength(2000);
            e.Property(r => r.PendingSourceFingerprint).HasMaxLength(200);
            e.Property(r => r.PendingMetadataHash).HasMaxLength(64);
            e.Property(r => r.PendingPayloadHash).HasMaxLength(64);
            e.Property(r => r.PendingPayloadLocation).HasMaxLength(2000);

            // Worker and intake paths.
            e.HasIndex(r => new { r.FlowId, r.Status, r.NextAttemptUtc });
            e.HasIndex(r => new { r.FlowId, r.LastSubmissionId });
            e.HasIndex(r => new { r.Status, r.LeaseExpiresUtc });
            e.HasIndex(r => new { r.FlowId, r.LastVerifiedUtc });

            // GUI: prefix search and recency listings, all answered from an index.
            e.HasIndex(r => new { r.FlowId, r.Label });
            e.HasIndex(r => new { r.FlowId, r.SourceKey });
            e.HasIndex(r => new { r.FlowId, r.TargetId });
            e.HasIndex(r => new { r.FlowId, r.UpdatedUtc });
            e.HasIndex(r => new { r.FlowId, r.LastDeliveredUtc });
            e.HasIndex(r => new { r.FlowId, r.LastVerifyOutcome });
        });

        modelBuilder.Entity<DeliveryAttempt>(e =>
        {
            e.ToTable("Attempt", SchemaName);
            e.HasKey(a => a.AttemptId);
            e.Property(a => a.AttemptId).ValueGeneratedOnAdd();
            e.Property(a => a.Worker).HasMaxLength(200).IsRequired();
            e.Property(a => a.Outcome).HasMaxLength(16).IsRequired();
            e.Property(a => a.Phase).HasMaxLength(32).IsRequired();
            e.Property(a => a.MetadataHash).HasMaxLength(64);
            e.Property(a => a.PayloadHash).HasMaxLength(64);
            e.Property(a => a.Error).HasMaxLength(2000);
            e.HasIndex(a => new { a.DeliveryKey, a.StartedUtc });
            e.HasIndex(a => a.StartedUtc);
            e.HasIndex(a => a.SubmissionId);
            e.HasIndex(a => a.RunId);
        });

        modelBuilder.Entity<DeliverySourceWatermark>(e =>
        {
            e.ToTable("SourceWatermark", SchemaName);
            e.HasKey(w => new { w.FlowId, w.Scope, w.TableName });
            e.Property(w => w.Scope).HasMaxLength(400);
            e.Property(w => w.TableName).HasMaxLength(400);
        });

        modelBuilder.Entity<DeliveryActivity>(e =>
        {
            e.ToTable("Activity", SchemaName);
            e.HasKey(a => a.ActivityId);
            e.Property(a => a.ActivityId).ValueGeneratedOnAdd();
            e.Property(a => a.FlowName).HasMaxLength(200).IsRequired();
            e.Property(a => a.Kind).HasMaxLength(32).IsRequired();
            e.Property(a => a.Actor).HasMaxLength(200).IsRequired();
            e.Property(a => a.Outcome).HasMaxLength(16).IsRequired();
            e.Property(a => a.Summary).HasMaxLength(2000);
            e.HasIndex(a => new { a.FlowId, a.StartedUtc });
            e.HasIndex(a => new { a.DeliveryKey, a.StartedUtc });
            e.HasIndex(a => a.SubmissionId);
            e.HasIndex(a => a.RunId);
            e.HasIndex(a => new { a.Kind, a.StartedUtc });
            e.HasIndex(a => new { a.Actor, a.StartedUtc });
            e.HasIndex(a => a.StartedUtc);
        });

        modelBuilder.Entity<DeliveryMapping>(e =>
        {
            e.ToTable("Mapping", SchemaName);
            e.HasKey(m => m.Id);
            e.Property(m => m.Reference).HasMaxLength(200).IsRequired();
            e.Property(m => m.Name).HasMaxLength(150).IsRequired();
            e.Property(m => m.Version).HasMaxLength(50).IsRequired();
            e.Property(m => m.Kind).HasMaxLength(200).IsRequired();
            e.Property(m => m.RelativePath).HasMaxLength(1000).IsRequired();
            e.Property(m => m.ContentHash).HasMaxLength(64).IsRequired();
            e.Property(m => m.Yaml).IsRequired();
            e.Property(m => m.SummaryJson).IsRequired();
            e.Property(m => m.Status).HasMaxLength(16).IsRequired();
            e.Property(m => m.Message).HasMaxLength(4000);
            e.HasIndex(m => new { m.RepoId, m.Reference }).IsUnique();
            e.HasIndex(m => new { m.RepoId, m.Kind });
        });

        modelBuilder.Entity<DeliverySnapshot>(e =>
        {
            e.ToTable("Snapshot", SchemaName);
            e.HasKey(s => s.Id);
            e.Property(s => s.Kind).HasMaxLength(16).IsRequired();
            e.Property(s => s.Name).HasMaxLength(200).IsRequired();
            e.Property(s => s.Version).HasMaxLength(64).IsRequired();
            e.Property(s => s.RelativePath).HasMaxLength(1000).IsRequired();
            e.Property(s => s.SummaryJson).IsRequired();
            e.HasIndex(s => new { s.RepoId, s.Kind, s.Name }).IsUnique();
        });
    }
}
