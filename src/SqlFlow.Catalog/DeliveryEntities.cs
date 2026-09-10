using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

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

    public long RecordCount { get; set; }

    /// <summary>The work location the intake wrote its batches under.</summary>
    public string? WorkLocation { get; set; }

    /// <summary>How many work batches the intake wrote.</summary>
    public int BatchCount { get; set; }

    /// <summary>How many root-scope partitions the drop declared.</summary>
    public int Partitions { get; set; }

    public string Status { get; set; } = "received";

    public DateTime ReceivedUtc { get; set; }

    public DateTime? StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    public long Planned { get; set; }

    public long SkippedUnchanged { get; set; }

    /// <summary>Records the drop carried in a version older than the one delivered or queued; skipped, never sent.</summary>
    public long SkippedStale { get; set; }

    /// <summary>Records whose queued document was, when its turn came, what OSDU already held; nothing was sent.</summary>
    public long UnchangedAtPush { get; set; }

    public long Blocked { get; set; }

    public long Delivered { get; set; }

    public long Held { get; set; }

    public long Failed { get; set; }

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

    /// <summary>When the source row OSDU's document was built from last changed (the flow's source.lastModified).</summary>
    public DateTime? SourceModifiedUtc { get; set; }

    public string? MetadataHash { get; set; }

    public string? PayloadHash { get; set; }

    /// <summary>The newest modified time among the chunk files OSDU's payload was delivered from.</summary>
    public DateTime? PayloadModifiedUtc { get; set; }

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

    /// <summary>Where the pending document sits in the submission's work batches (batch:offset:length).</summary>
    public string? PendingDocumentRef { get; set; }

    /// <summary>The work batch the pending document was written in.</summary>
    public int? WorkBatch { get; set; }

    /// <summary>The identifiers the target returned for what it holds now (a JSON object merged step by step).</summary>
    public string? TargetStateJson { get; set; }

    /// <summary>The completed steps of the pending delivery and what they returned (a JSON object keyed by step).</summary>
    public string? PendingStepJson { get; set; }

    /// <summary>
    /// The cache values this record was built from, as the id of the set it shares with every other record that
    /// read the same values (<see cref="DeliveryCacheSet"/>). One column rather than a row per dependency: at
    /// estate scale the records number in the hundreds of millions and the distinct sets in the thousands.
    /// </summary>
    public long? CacheSetId { get; set; }

    public string? PendingRenderContext { get; set; }

    public string? PendingSourceFingerprint { get; set; }

    public DateTime? PendingSourceModifiedUtc { get; set; }

    public string? PendingMetadataHash { get; set; }

    public string? PendingPayloadHash { get; set; }

    public DateTime? PendingPayloadModifiedUtc { get; set; }

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

    /// <summary>The steps of the try and what the target answered, as JSON.</summary>
    public string? ResultJson { get; set; }

    /// <summary>The work batch the try belonged to, when it ran from one.</summary>
    public int? WorkBatch { get; set; }
}

/// <summary>
/// One row of the <c>delivery.RecordCount</c> indexed view: how many of a flow's records share a status, a last verify
/// outcome and the hour they were last delivered in. SQL Server maintains the view in the transaction of every record
/// write, so a flow's statistics read a few rows however many records the flow holds. Read-only, and SQL Server only.
/// </summary>
public sealed class DeliveryRecordCount
{
    public Guid FlowId { get; set; }

    public string Status { get; set; } = string.Empty;

    public string? LastVerifyOutcome { get; set; }

    /// <summary>The hour <see cref="DeliveryRecord.LastDeliveredUtc"/> falls in, truncated; null for a record never delivered.</summary>
    public DateTime? DeliveredHour { get; set; }

    public long Records { get; set; }
}

/// <summary>An indexed view the catalog carries beside its tables: where it lives and the batches that create it, in order.</summary>
public sealed record CatalogIndexedView(string Schema, string Name, IReadOnlyList<string> Batches);

/// <summary>One work batch of a submission: a file of rendered documents, claimed and drained as one unit.</summary>
public sealed class DeliveryWorkBatch
{
    public Guid SubmissionId { get; set; }

    public int Index { get; set; }

    public Guid FlowId { get; set; }

    public string Location { get; set; } = string.Empty;

    public int RecordCount { get; set; }

    /// <summary>queued, running, done, failed.</summary>
    public string Status { get; set; } = "queued";

    public string? LeaseOwner { get; set; }

    public DateTime? LeaseExpiresUtc { get; set; }

    public Guid? RunId { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime? StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    public long Delivered { get; set; }

    public long Held { get; set; }

    public long Failed { get; set; }

    public long Retrying { get; set; }

    public string? Error { get; set; }
}

/// <summary>Tier-0 watermark: the source table version of one flow scope (the parameter set).</summary>
public sealed class DeliverySourceWatermark
{
    public Guid FlowId { get; set; }

    public string Scope { get; set; } = string.Empty;

    public string TableName { get; set; } = string.Empty;

    public long Version { get; set; }

    /// <summary>
    /// The render context the last run of this scope used. The whole-run gate compares it, so a cache, mapping or
    /// schema version that moved re-renders the scope even when no source table advanced.
    /// </summary>
    public string? ContextHash { get; set; }

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

/// <summary>
/// A cached OSDU type as a retrieval flow declares it: what the flow keeps cached for the mappings to resolve
/// against, and which paths of each record it captures. The sync reads it out of the flow document, so the GUI
/// shows what the cache is meant to hold without opening the repository.
/// </summary>
public sealed class DeliveryCacheDefinition
{
    /// <summary>Stable id: derived from the repo id, the flow name and the cached type name.</summary>
    public Guid Id { get; set; }

    public Guid RepoId { get; set; }

    /// <summary>The retrieval flow that maintains this cached type.</summary>
    public string FlowName { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    /// <summary>The short name mappings use (UnitOfMeasure).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The OSDU entity type (reference-data--UnitOfMeasure).</summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>The search kind the capture sweeps.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The search query narrowing the capture.</summary>
    public string? Query { get; set; }

    /// <summary>The captured paths as JSON: <c>[{ "path": "data.Code", "as": "Code" }]</c>.</summary>
    public string FieldsJson { get; set; } = "[]";

    /// <summary>Whether a refresh makes its snapshot the one <c>pinned</c> resolves to.</summary>
    public bool MakeCurrent { get; set; }

    public DateTime FirstSeenUtc { get; set; }

    public DateTime LastSeenUtc { get; set; }
}

/// <summary>
/// One cached record of the current reference snapshot: its OSDU id and the values captured at the declared paths.
/// The sync writes these so the cache is queryable where everything else about a delivery is, without reading the
/// snapshot files. The snapshot in the store stays the authority a render resolves against.
/// </summary>
public sealed class DeliverySnapshotItem
{
    public long ItemId { get; set; }

    /// <summary>The <see cref="DeliverySnapshot"/> row the item belongs to.</summary>
    public Guid SnapshotId { get; set; }

    public Guid RepoId { get; set; }

    /// <summary>The cached type's short name (UnitOfMeasure).</summary>
    public string TypeName { get; set; } = string.Empty;

    public string EntityType { get; set; } = string.Empty;

    /// <summary>The OSDU record id, without a version.</summary>
    public string RecordId { get; set; } = string.Empty;

    /// <summary>The captured values as JSON, in whatever shape the paths yielded.</summary>
    public string FieldsJson { get; set; } = "{}";

    /// <summary>Every scalar the item holds, newline separated: what a search over cached values matches on.</summary>
    public string Terms { get; set; } = string.Empty;
}

/// <summary>
/// One distinct combination of cached values that records were built from: the set of (type, cached record, path,
/// value) a render consumed. Records share sets heavily (every log that resolved metres and the same wellbore
/// shares one), so the estate's dependency trail is a few thousand sets rather than a row per record per value.
/// A record points at its set; a cache change finds the sets that hold the changed value and, through them, the
/// records to update.
/// </summary>
public sealed class DeliveryCacheSet
{
    public long SetId { get; set; }

    /// <summary>Content hash of the set's entries: the identity a render computes without a round trip.</summary>
    public string SetHash { get; set; } = string.Empty;

    public int EntryCount { get; set; }

    /// <summary>
    /// Set while a change to one of these values is tagged and unapproved. The planner reads the gated sets once
    /// per run and skips their records, so holding records back never means writing to them.
    /// </summary>
    public bool Gated { get; set; }

    public DateTime FirstSeenUtc { get; set; }

    public DateTime LastSeenUtc { get; set; }
}

/// <summary>One cached value inside a set: what was read, and what it read at the time.</summary>
public sealed class DeliveryCacheSetEntry
{
    public long SetId { get; set; }

    /// <summary>The cached type's short name (UnitOfMeasure).</summary>
    public string TypeName { get; set; } = string.Empty;

    /// <summary>The cached record's OSDU id.</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>What the mapping read: <c>id</c>, <c>Name</c>, <c>NameAlias.AliasName</c>.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>match (the value the source resolved by) or value (a value written into the document).</summary>
    public string Kind { get; set; } = "value";

    public string ValueHash { get; set; } = string.Empty;

    /// <summary>The value as it was consumed, truncated for display.</summary>
    public string ValueText { get; set; } = string.Empty;
}

/// <summary>
/// One change to the cache that delivered records were built from, and what happens about it. A tag is written per
/// change, not per record: a corrected unit name is one decision covering every record that read it, with the count
/// of what it affects. Under <c>approve</c> the affected sets are gated until someone decides; under <c>auto</c> it
/// is approved as written. Either way the update is rolled out in batches from <see cref="Cursor"/>, so a change
/// touching millions of records drains at a controlled rate instead of flooding the estate.
/// </summary>
public sealed class DeliveryUpdateTag
{
    public long TagId { get; set; }

    /// <summary>What caused the tag; <c>cache</c> today.</summary>
    public string Kind { get; set; } = "cache";

    public string TypeName { get; set; } = string.Empty;

    public string ItemId { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    /// <summary>changed (the value moved), removed (the cached record is gone) or unmatched (what it matched by is gone).</summary>
    public string Change { get; set; } = "changed";

    public string? OldValue { get; set; }

    public string? NewValue { get; set; }

    public string? FromVersion { get; set; }

    public string ToVersion { get; set; } = string.Empty;

    /// <summary>auto or approve, as the cache declared for this type when the tag was written.</summary>
    public string Mode { get; set; } = "approve";

    /// <summary>pending, approved, rejected, rolling or applied.</summary>
    public string Status { get; set; } = "pending";

    /// <summary>The cache sets holding the changed value, comma separated; the records are found through them.</summary>
    public string SetIds { get; set; } = string.Empty;

    /// <summary>How many delivered records were built from the old value when the change was found.</summary>
    public long AffectedRecords { get; set; }

    /// <summary>How many of them the rollout has marked for redelivery so far.</summary>
    public long Processed { get; set; }

    /// <summary>Where the rollout got to, in delivery-key order, so a pass resumes rather than restarts.</summary>
    public Guid? Cursor { get; set; }

    public DateTime DetectedUtc { get; set; }

    public DateTime? DecidedUtc { get; set; }

    public string? DecidedBy { get; set; }

    public DateTime? StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }
}

/// <summary>The EF model of the delivery ledger, in the <c>delivery</c> schema of the catalog database.</summary>
/// <summary>One retrieval run: the window it covered, where its files went, and its outcome.</summary>
public sealed class DeliveryRetrieval
{
    public long RetrievalId { get; set; }

    public Guid FlowId { get; set; }

    public string FlowName { get; set; } = string.Empty;

    public Guid? RunId { get; set; }

    public string Actor { get; set; } = string.Empty;

    /// <summary>The kinds the run covered, comma separated.</summary>
    public string Kinds { get; set; } = string.Empty;

    /// <summary>The query as it ran, window included.</summary>
    public string? Query { get; set; }

    public string? WindowField { get; set; }

    public DateTime? WindowFrom { get; set; }

    public DateTime? WindowTo { get; set; }

    public string Location { get; set; } = string.Empty;

    public string? ManifestLocation { get; set; }

    public string Status { get; set; } = "running";

    public long Records { get; set; }

    public int Files { get; set; }

    /// <summary>Uncompressed bytes written.</summary>
    public long Bytes { get; set; }

    public DateTime StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    public string? Error { get; set; }
}

public static class DeliveryModel
{
    public const string SchemaName = "delivery";

    /// <summary>The indexed view that counts a flow's records (<see cref="DeliveryRecordCount"/>).</summary>
    public const string RecordCountView = "RecordCount";

    // The hour a record was last delivered in. DATEADD/DATEDIFF against a fixed origin is deterministic and precise,
    // which an indexed view's grouping requires; the origin is converted with an explicit style for the same reason.
    private const string DeliveredHourSql =
        "DATEADD(hour, DATEDIFF(hour, CONVERT(datetime2(0), '20000101', 112), [LastDeliveredUtc]), CONVERT(datetime2(0), '20000101', 112))";

    /// <summary>
    /// The indexed views the EF model cannot declare, created on SQL Server right after the tables and verified with
    /// them. SQL Server maintains an indexed view with the table it reads, in the same transaction, so what one answers is
    /// derived from the ledger and never counted separately.
    /// </summary>
    public static IReadOnlyList<CatalogIndexedView> IndexedViews { get; } =
    [
        new(SchemaName, RecordCountView,
        [
            "CREATE VIEW [" + SchemaName + "].[" + RecordCountView + "] WITH SCHEMABINDING AS " +
            "SELECT [FlowId], [Status], [LastVerifyOutcome], " + DeliveredHourSql + " AS [DeliveredHour], COUNT_BIG(*) AS [Records] " +
            "FROM [" + SchemaName + "].[Record] " +
            "GROUP BY [FlowId], [Status], [LastVerifyOutcome], " + DeliveredHourSql,
            "CREATE UNIQUE CLUSTERED INDEX [IX_" + RecordCountView + "] ON [" + SchemaName + "].[" + RecordCountView + "] " +
            "([FlowId], [Status], [LastVerifyOutcome], [DeliveredHour])",
        ]),
    ];

    /// <summary>
    /// The collation of the columns that key on an OSDU record id. OSDU ids are case-sensitive:
    /// <c>...UnitOfMeasure:ft</c> (the foot) and <c>...UnitOfMeasure:fT</c> (the femtotesla) are two records, and SQL
    /// Server's default collation folds case, which would make them one key. SQLite compares ordinally already.
    /// </summary>
    public const string OsduIdCollation = "Latin1_General_100_BIN2";

    /// <param name="modelBuilder">The catalog model being built.</param>
    /// <param name="sqlServer">Whether the model is for SQL Server, the provider whose default collation folds case.</param>
    public static void Configure(ModelBuilder modelBuilder, bool sqlServer)
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
            e.Property(s => s.WorkLocation).HasMaxLength(2000);
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
            e.Property(r => r.PendingDocumentRef).HasMaxLength(64);

            // Worker and intake paths.
            e.HasIndex(r => new { r.FlowId, r.Status, r.NextAttemptUtc });
            // The rollout walks one set's records in key order; the filtered index keeps untagged records out of it.
            e.HasIndex(r => new { r.CacheSetId, r.DeliveryKey }).HasFilter("[CacheSetId] IS NOT NULL");
            e.HasIndex(r => new { r.LastSubmissionId, r.WorkBatch });
            e.HasIndex(r => r.LeaseOwner);
            // A submission's records, most recent first, read in index order however many the submission holds.
            e.HasIndex(r => new { r.FlowId, r.LastSubmissionId, r.UpdatedUtc });
            e.HasIndex(r => new { r.Status, r.LeaseExpiresUtc });
            e.HasIndex(r => new { r.FlowId, r.LastVerifiedUtc });

            // GUI: prefix search and recency listings, all answered from an index.
            e.HasIndex(r => new { r.FlowId, r.Label });
            e.HasIndex(r => new { r.FlowId, r.SourceKey });
            e.HasIndex(r => new { r.FlowId, r.TargetId });
            e.HasIndex(r => new { r.FlowId, r.UpdatedUtc });
            e.HasIndex(r => new { r.FlowId, r.Status, r.UpdatedUtc });
            e.HasIndex(r => new { r.FlowId, r.LastDeliveredUtc });
            e.HasIndex(r => new { r.FlowId, r.LastVerifyOutcome });

            // The global lookup (the search box): a delivery key is the primary key; an OSDU id, a source key or a label
            // prefix answers from these across every flow.
            e.HasIndex(r => r.TargetId);
            e.HasIndex(r => r.SourceKey);
            e.HasIndex(r => r.Label);

            // Key-ordered walks of one flow: the known-state stream and a removal's key list page through it by key.
            e.HasIndex(r => new { r.FlowId, r.DeliveryKey });
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
            // A run's records: the listing's run filter seeks the run and joins on the key without reading the attempt.
            e.HasIndex(a => new { a.RunId, a.DeliveryKey });
        });

        modelBuilder.Entity<DeliveryRecordCount>(e =>
        {
            // Created by CatalogDatabase from IndexedViews, not by EnsureCreated: EF cannot declare an indexed view.
            e.HasNoKey();
            e.ToView(RecordCountView, SchemaName);
            e.Property(c => c.Status).HasMaxLength(16);
            e.Property(c => c.LastVerifyOutcome).HasMaxLength(16);
        });

        modelBuilder.Entity<DeliveryWorkBatch>(e =>
        {
            e.ToTable("WorkBatch", SchemaName);
            e.HasKey(b => new { b.SubmissionId, b.Index });
            e.Property(b => b.Location).HasMaxLength(2000).IsRequired();
            e.Property(b => b.Status).HasMaxLength(16).IsRequired();
            e.Property(b => b.LeaseOwner).HasMaxLength(200);
            e.Property(b => b.Error).HasMaxLength(2000);
            // The claim: the oldest queued batch of a flow (or a submission), and the lease sweep.
            e.HasIndex(b => new { b.FlowId, b.Status, b.CreatedUtc });
            e.HasIndex(b => new { b.SubmissionId, b.Status });
            e.HasIndex(b => new { b.Status, b.LeaseExpiresUtc });
        });

        modelBuilder.Entity<DeliverySourceWatermark>(e =>
        {
            e.ToTable("SourceWatermark", SchemaName);
            e.HasKey(w => new { w.FlowId, w.Scope, w.TableName });
            e.Property(w => w.Scope).HasMaxLength(400);
            e.Property(w => w.ContextHash).HasMaxLength(64);
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

        modelBuilder.Entity<DeliveryRetrieval>(e =>
        {
            e.ToTable("Retrieval", SchemaName);
            e.HasKey(r => r.RetrievalId);
            e.Property(r => r.RetrievalId).ValueGeneratedOnAdd();
            e.Property(r => r.FlowName).HasMaxLength(200).IsRequired();
            e.Property(r => r.Actor).HasMaxLength(200).IsRequired();
            e.Property(r => r.Kinds).HasMaxLength(4000).IsRequired();
            e.Property(r => r.WindowField).HasMaxLength(200);
            e.Property(r => r.Location).HasMaxLength(2000).IsRequired();
            e.Property(r => r.ManifestLocation).HasMaxLength(2000);
            e.Property(r => r.Status).HasMaxLength(16).IsRequired();
            e.Property(r => r.Error).HasMaxLength(4000);
            // The flow's listing, the watermark chain (the last done run), and the run's row.
            e.HasIndex(r => new { r.FlowId, r.StartedUtc });
            e.HasIndex(r => new { r.FlowId, r.Status, r.StartedUtc });
            e.HasIndex(r => r.RunId);
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

        modelBuilder.Entity<DeliveryCacheSet>(e =>
        {
            e.ToTable("CacheSet", SchemaName);
            e.HasKey(c => c.SetId);
            e.Property(c => c.SetHash).HasMaxLength(64).IsRequired();
            e.HasIndex(c => c.SetHash).IsUnique();
            // The planner reads this every run: a filtered index keeps it to the handful of gated sets.
            e.HasIndex(c => c.Gated).HasFilter("[Gated] = 1");
        });

        modelBuilder.Entity<DeliveryCacheSetEntry>(e =>
        {
            e.ToTable("CacheSetEntry", SchemaName);
            e.Property(c => c.TypeName).HasMaxLength(200).IsRequired();
            OsduId(e.Property(c => c.ItemId), sqlServer).HasMaxLength(512).IsRequired();
            e.Property(c => c.Path).HasMaxLength(400).IsRequired();
            e.Property(c => c.Kind).HasMaxLength(16).IsRequired();
            e.Property(c => c.ValueHash).HasMaxLength(64).IsRequired();
            e.Property(c => c.ValueText).HasMaxLength(400).IsRequired();
            e.HasKey(c => new { c.SetId, c.TypeName, c.ItemId, c.Path, c.Kind });
            // The impact query: which sets hold this cached value.
            e.HasIndex(c => new { c.TypeName, c.ItemId });
        });

        modelBuilder.Entity<DeliveryUpdateTag>(e =>
        {
            e.ToTable("UpdateTag", SchemaName);
            e.HasKey(t => t.TagId);
            e.Property(t => t.Kind).HasMaxLength(16).IsRequired();
            e.Property(t => t.TypeName).HasMaxLength(200).IsRequired();
            OsduId(e.Property(t => t.ItemId), sqlServer).HasMaxLength(512).IsRequired();
            e.Property(t => t.Path).HasMaxLength(400).IsRequired();
            e.Property(t => t.Change).HasMaxLength(16).IsRequired();
            e.Property(t => t.OldValue).HasMaxLength(400);
            e.Property(t => t.NewValue).HasMaxLength(400);
            e.Property(t => t.FromVersion).HasMaxLength(64);
            e.Property(t => t.ToVersion).HasMaxLength(64).IsRequired();
            e.Property(t => t.Mode).HasMaxLength(16).IsRequired();
            e.Property(t => t.Status).HasMaxLength(16).IsRequired();
            e.Property(t => t.DecidedBy).HasMaxLength(200);
            e.Property(t => t.SetIds).IsRequired();
            e.HasIndex(t => t.Status);
            e.HasIndex(t => new { t.TypeName, t.ItemId, t.Path, t.Status });
        });

        modelBuilder.Entity<DeliveryCacheDefinition>(e =>
        {
            e.ToTable("CacheDefinition", SchemaName);
            e.HasKey(c => c.Id);
            e.Property(c => c.FlowName).HasMaxLength(200).IsRequired();
            e.Property(c => c.RelativePath).HasMaxLength(1000).IsRequired();
            e.Property(c => c.Name).HasMaxLength(200).IsRequired();
            e.Property(c => c.EntityType).HasMaxLength(200).IsRequired();
            e.Property(c => c.Kind).HasMaxLength(400).IsRequired();
            e.Property(c => c.Query).HasMaxLength(4000);
            e.Property(c => c.FieldsJson).IsRequired();
            e.HasIndex(c => new { c.RepoId, c.FlowName, c.Name }).IsUnique();
            e.HasIndex(c => c.Name);
        });

        modelBuilder.Entity<DeliverySnapshotItem>(e =>
        {
            e.ToTable("SnapshotItem", SchemaName);
            e.HasKey(i => i.ItemId);
            e.Property(i => i.TypeName).HasMaxLength(200).IsRequired();
            e.Property(i => i.EntityType).HasMaxLength(200).IsRequired();
            OsduId(e.Property(i => i.RecordId), sqlServer).HasMaxLength(512).IsRequired();
            e.Property(i => i.FieldsJson).IsRequired();
            e.Property(i => i.Terms).IsRequired();
            e.HasIndex(i => new { i.SnapshotId, i.TypeName, i.RecordId }).IsUnique();
            // One cached record across every version the catalog carries: the type listing is its prefix, so this
            // one index answers both the version-scoped listing and a value's history.
            e.HasIndex(i => new { i.RepoId, i.TypeName, i.RecordId });
        });
    }

    private static PropertyBuilder<string> OsduId(PropertyBuilder<string> property, bool sqlServer)
        => sqlServer ? property.UseCollation(OsduIdCollation) : property;
}
