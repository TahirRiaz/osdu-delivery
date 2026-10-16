using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialOsduSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "osdu");

            migrationBuilder.CreateTable(
                name: "Activity",
                schema: "osdu",
                columns: table => new
                {
                    ActivityId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FlowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FlowName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Outcome = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ParametersJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SubmissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeliveryKey = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Summary = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Log = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Activity", x => x.ActivityId);
                });

            migrationBuilder.CreateTable(
                name: "Attempt",
                schema: "osdu",
                columns: table => new
                {
                    AttemptId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeliveryKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubmissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Worker = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Phase = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    MetadataHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PayloadHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    TargetVersion = table.Column<long>(type: "bigint", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ResultJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    WorkBatch = table.Column<int>(type: "int", nullable: true),
                    SourceFileName = table.Column<string>(type: "nvarchar(800)", maxLength: 800, nullable: true),
                    SourceRowNumber = table.Column<long>(type: "bigint", nullable: true),
                    SourceUpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attempt", x => x.AttemptId);
                });

            migrationBuilder.CreateTable(
                name: "CacheDefinition",
                schema: "osdu",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FlowName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Endpoint = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    RelativePath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Query = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    FieldsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OnChange = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CacheDefinition", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CacheItem",
                schema: "osdu",
                columns: table => new
                {
                    ItemId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Scope = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TypeName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RecordId = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false, collation: "Latin1_General_100_BIN2"),
                    FieldsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Terms = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FromSequence = table.Column<int>(type: "int", nullable: false),
                    ToSequence = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CacheItem", x => x.ItemId);
                });

            migrationBuilder.CreateTable(
                name: "CacheMember",
                schema: "osdu",
                columns: table => new
                {
                    Scope = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TypeName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RecordId = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false, collation: "Latin1_General_100_BIN2"),
                    FlowName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CacheMember", x => new { x.Scope, x.TypeName, x.RecordId, x.FlowName });
                });

            migrationBuilder.CreateTable(
                name: "CacheSet",
                schema: "osdu",
                columns: table => new
                {
                    SetId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SetHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EntryCount = table.Column<int>(type: "int", nullable: false),
                    Gated = table.Column<bool>(type: "bit", nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CacheSet", x => x.SetId);
                });

            migrationBuilder.CreateTable(
                name: "CacheSetEntry",
                schema: "osdu",
                columns: table => new
                {
                    SetId = table.Column<long>(type: "bigint", nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TypeName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ItemId = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Path = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ValueHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ValueText = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CacheSetEntry", x => new { x.SetId, x.Scope, x.TypeName, x.ItemId, x.Path, x.Kind });
                });

            migrationBuilder.CreateTable(
                name: "CacheVersion",
                schema: "osdu",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    FlowName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    CapturedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PreviousVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Current = table.Column<bool>(type: "bit", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CapturedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Origin = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    TypesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Items = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CacheVersion", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InlineSubmission",
                schema: "osdu",
                columns: table => new
                {
                    SubmissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FlowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FlowName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    MappingReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Operation = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Force = table.Column<bool>(type: "bit", nullable: false),
                    ParametersJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RecordsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RequestHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RecordCount = table.Column<int>(type: "int", nullable: false),
                    ChildRowCount = table.Column<long>(type: "bigint", nullable: false),
                    ContentBytes = table.Column<int>(type: "int", nullable: false),
                    ReceivedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReceivedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    LandedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OsduRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InlineSubmission", x => x.SubmissionId);
                });

            migrationBuilder.CreateTable(
                name: "Mapping",
                schema: "osdu",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Version = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TemplateVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RelativePath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Yaml = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SummaryJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    FirstSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Mapping", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Record",
                schema: "osdu",
                columns: table => new
                {
                    DeliveryKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FlowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    SourceKeyJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Label = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    MappingName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RenderContext = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceFingerprint = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SourceModifiedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SourceFileName = table.Column<string>(type: "nvarchar(800)", maxLength: 800, nullable: true),
                    SourceRowNumber = table.Column<long>(type: "bigint", nullable: true),
                    SourceUpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    MetadataHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PayloadHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PayloadModifiedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TargetId = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TargetVersion = table.Column<long>(type: "bigint", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    LastDeliveredUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastVerifiedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastVerifyOutcome = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    LeaseOwner = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LeaseExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSubmissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    NextAttemptUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    PendingDocumentRef = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    WorkBatch = table.Column<int>(type: "int", nullable: true),
                    TargetStateJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PendingStepJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CacheSetId = table.Column<long>(type: "bigint", nullable: true),
                    PendingRenderContext = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PendingSourceFingerprint = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PendingSourceModifiedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PendingSourceFileName = table.Column<string>(type: "nvarchar(800)", maxLength: 800, nullable: true),
                    PendingSourceRowNumber = table.Column<long>(type: "bigint", nullable: true),
                    PendingSourceUpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PendingMetadataHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PendingPayloadHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PendingPayloadModifiedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PendingPayloadLocation = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    PendingMetadata = table.Column<bool>(type: "bit", nullable: false),
                    PendingPayload = table.Column<bool>(type: "bit", nullable: false),
                    Blocked = table.Column<bool>(type: "bit", nullable: false),
                    PlanRequestedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Record", x => x.DeliveryKey);
                });

            migrationBuilder.CreateTable(
                name: "Retrieval",
                schema: "osdu",
                columns: table => new
                {
                    RetrievalId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FlowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FlowName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Actor = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kinds = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Query = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    WindowField = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    WindowFrom = table.Column<DateTime>(type: "datetime2", nullable: true),
                    WindowTo = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Location = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ManifestLocation = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Records = table.Column<long>(type: "bigint", nullable: false),
                    Files = table.Column<int>(type: "int", nullable: false),
                    Bytes = table.Column<long>(type: "bigint", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Retrieval", x => x.RetrievalId);
                });

            migrationBuilder.CreateTable(
                name: "SchemaVersion",
                schema: "osdu",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    ModuleVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    LastMigration = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    AppliedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AppliedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    MinimumCatalogMigration = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchemaVersion", x => x.Id);
                    table.CheckConstraint("CK_SchemaVersion_SingleRow", "[Id] = 1");
                });

            migrationBuilder.CreateTable(
                name: "SourceWatermark",
                schema: "osdu",
                columns: table => new
                {
                    FlowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    UpdatedThroughUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SubmissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContextHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RecordedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceWatermark", x => new { x.FlowId, x.Scope });
                });

            migrationBuilder.CreateTable(
                name: "Submission",
                schema: "osdu",
                columns: table => new
                {
                    SubmissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FlowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FlowName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    MappingReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RenderContext = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ParametersJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RecordCount = table.Column<long>(type: "bigint", nullable: false),
                    WorkLocation = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    BatchCount = table.Column<int>(type: "int", nullable: false),
                    Slices = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ReceivedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Planned = table.Column<long>(type: "bigint", nullable: false),
                    SkippedUnchanged = table.Column<long>(type: "bigint", nullable: false),
                    AwaitingApproval = table.Column<long>(type: "bigint", nullable: false),
                    SkippedStale = table.Column<long>(type: "bigint", nullable: false),
                    UnchangedAtPush = table.Column<long>(type: "bigint", nullable: false),
                    Blocked = table.Column<long>(type: "bigint", nullable: false),
                    Delivered = table.Column<long>(type: "bigint", nullable: false),
                    Held = table.Column<long>(type: "bigint", nullable: false),
                    Failed = table.Column<long>(type: "bigint", nullable: false),
                    Untracked = table.Column<long>(type: "bigint", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    SourceConnection = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    SourceObject = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    WindowFromUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    WindowToUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SourceWindowJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Submission", x => x.SubmissionId);
                });

            migrationBuilder.CreateTable(
                name: "SubmissionLanding",
                schema: "osdu",
                columns: table => new
                {
                    SubmissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Dataset = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    PreFlowName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Location = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(800)", maxLength: 800, nullable: false),
                    Format = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    RowCount = table.Column<long>(type: "bigint", nullable: false),
                    Bytes = table.Column<long>(type: "bigint", nullable: false),
                    ContentHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    WrittenUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PreRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubmissionLanding", x => new { x.SubmissionId, x.Dataset });
                });

            migrationBuilder.CreateTable(
                name: "Template",
                schema: "osdu",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SchemaJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Origin = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    CapturedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CapturedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Template", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UpdateTag",
                schema: "osdu",
                columns: table => new
                {
                    TagId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TypeName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ItemId = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Path = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Change = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    OldValue = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    NewValue = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    FromVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ToVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Mode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    SetIds = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AffectedRecords = table.Column<long>(type: "bigint", nullable: false),
                    Processed = table.Column<long>(type: "bigint", nullable: false),
                    Cursor = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DetectedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DecidedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DecidedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpdateTag", x => x.TagId);
                });

            migrationBuilder.CreateTable(
                name: "WorkBatch",
                schema: "osdu",
                columns: table => new
                {
                    SubmissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Index = table.Column<int>(type: "int", nullable: false),
                    FlowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Location = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    RecordCount = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    LeaseOwner = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LeaseExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Delivered = table.Column<long>(type: "bigint", nullable: false),
                    Held = table.Column<long>(type: "bigint", nullable: false),
                    Failed = table.Column<long>(type: "bigint", nullable: false),
                    Retrying = table.Column<long>(type: "bigint", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkBatch", x => new { x.SubmissionId, x.Index });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Activity_Actor_StartedUtc",
                schema: "osdu",
                table: "Activity",
                columns: new[] { "Actor", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Activity_DeliveryKey_StartedUtc",
                schema: "osdu",
                table: "Activity",
                columns: new[] { "DeliveryKey", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Activity_FlowId_StartedUtc",
                schema: "osdu",
                table: "Activity",
                columns: new[] { "FlowId", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Activity_Kind_StartedUtc",
                schema: "osdu",
                table: "Activity",
                columns: new[] { "Kind", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Activity_RunId",
                schema: "osdu",
                table: "Activity",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_Activity_StartedUtc",
                schema: "osdu",
                table: "Activity",
                column: "StartedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Activity_SubmissionId",
                schema: "osdu",
                table: "Activity",
                column: "SubmissionId");

            migrationBuilder.CreateIndex(
                name: "IX_Attempt_DeliveryKey_StartedUtc",
                schema: "osdu",
                table: "Attempt",
                columns: new[] { "DeliveryKey", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Attempt_RunId_DeliveryKey",
                schema: "osdu",
                table: "Attempt",
                columns: new[] { "RunId", "DeliveryKey" });

            migrationBuilder.CreateIndex(
                name: "IX_Attempt_StartedUtc",
                schema: "osdu",
                table: "Attempt",
                column: "StartedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Attempt_SubmissionId",
                schema: "osdu",
                table: "Attempt",
                column: "SubmissionId");

            migrationBuilder.CreateIndex(
                name: "IX_CacheDefinition_FlowName",
                schema: "osdu",
                table: "CacheDefinition",
                column: "FlowName");

            migrationBuilder.CreateIndex(
                name: "IX_CacheDefinition_RepoId_FlowName_Name",
                schema: "osdu",
                table: "CacheDefinition",
                columns: new[] { "RepoId", "FlowName", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CacheDefinition_Scope_Name",
                schema: "osdu",
                table: "CacheDefinition",
                columns: new[] { "Scope", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_CacheItem_Scope_ToSequence",
                schema: "osdu",
                table: "CacheItem",
                columns: new[] { "Scope", "ToSequence" });

            migrationBuilder.CreateIndex(
                name: "IX_CacheItem_Scope_TypeName_RecordId_FromSequence",
                schema: "osdu",
                table: "CacheItem",
                columns: new[] { "Scope", "TypeName", "RecordId", "FromSequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CacheMember_Scope_FlowName_TypeName",
                schema: "osdu",
                table: "CacheMember",
                columns: new[] { "Scope", "FlowName", "TypeName" });

            migrationBuilder.CreateIndex(
                name: "IX_CacheSet_Gated",
                schema: "osdu",
                table: "CacheSet",
                column: "Gated",
                filter: "[Gated] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_CacheSet_SetHash",
                schema: "osdu",
                table: "CacheSet",
                column: "SetHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CacheSetEntry_Scope_TypeName_ItemId",
                schema: "osdu",
                table: "CacheSetEntry",
                columns: new[] { "Scope", "TypeName", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_CacheVersion_RunId",
                schema: "osdu",
                table: "CacheVersion",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_CacheVersion_Scope_Sequence",
                schema: "osdu",
                table: "CacheVersion",
                columns: new[] { "Scope", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CacheVersion_Scope_Version",
                schema: "osdu",
                table: "CacheVersion",
                columns: new[] { "Scope", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InlineSubmission_FlowId_ReceivedUtc",
                schema: "osdu",
                table: "InlineSubmission",
                columns: new[] { "FlowId", "ReceivedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_InlineSubmission_GroupId",
                schema: "osdu",
                table: "InlineSubmission",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_InlineSubmission_Status_ReceivedUtc",
                schema: "osdu",
                table: "InlineSubmission",
                columns: new[] { "Status", "ReceivedUtc" },
                filter: "[Status] IN ('accepted','landed','queued')");

            migrationBuilder.CreateIndex(
                name: "IX_Mapping_Kind_TemplateVersion",
                schema: "osdu",
                table: "Mapping",
                columns: new[] { "Kind", "TemplateVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_Mapping_RepoId_Kind",
                schema: "osdu",
                table: "Mapping",
                columns: new[] { "RepoId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_Mapping_RepoId_Reference",
                schema: "osdu",
                table: "Mapping",
                columns: new[] { "RepoId", "Reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Record_CacheSetId_DeliveryKey",
                schema: "osdu",
                table: "Record",
                columns: new[] { "CacheSetId", "DeliveryKey" },
                filter: "[CacheSetId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Record_FlowId_DeliveryKey",
                schema: "osdu",
                table: "Record",
                columns: new[] { "FlowId", "DeliveryKey" });

            migrationBuilder.CreateIndex(
                name: "IX_Record_FlowId_Label",
                schema: "osdu",
                table: "Record",
                columns: new[] { "FlowId", "Label" });

            migrationBuilder.CreateIndex(
                name: "IX_Record_FlowId_LastDeliveredUtc",
                schema: "osdu",
                table: "Record",
                columns: new[] { "FlowId", "LastDeliveredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Record_FlowId_LastSubmissionId_UpdatedUtc",
                schema: "osdu",
                table: "Record",
                columns: new[] { "FlowId", "LastSubmissionId", "UpdatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Record_FlowId_LastVerifiedUtc",
                schema: "osdu",
                table: "Record",
                columns: new[] { "FlowId", "LastVerifiedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Record_FlowId_LastVerifyOutcome",
                schema: "osdu",
                table: "Record",
                columns: new[] { "FlowId", "LastVerifyOutcome" });

            migrationBuilder.CreateIndex(
                name: "IX_Record_FlowId_PlanRequestedUtc",
                schema: "osdu",
                table: "Record",
                columns: new[] { "FlowId", "PlanRequestedUtc" },
                filter: "[PlanRequestedUtc] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Record_FlowId_SourceFileName_SourceRowNumber",
                schema: "osdu",
                table: "Record",
                columns: new[] { "FlowId", "SourceFileName", "SourceRowNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_Record_FlowId_SourceKey",
                schema: "osdu",
                table: "Record",
                columns: new[] { "FlowId", "SourceKey" });

            migrationBuilder.CreateIndex(
                name: "IX_Record_FlowId_Status_NextAttemptUtc",
                schema: "osdu",
                table: "Record",
                columns: new[] { "FlowId", "Status", "NextAttemptUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Record_FlowId_Status_UpdatedUtc",
                schema: "osdu",
                table: "Record",
                columns: new[] { "FlowId", "Status", "UpdatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Record_FlowId_TargetId",
                schema: "osdu",
                table: "Record",
                columns: new[] { "FlowId", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "IX_Record_FlowId_UpdatedUtc",
                schema: "osdu",
                table: "Record",
                columns: new[] { "FlowId", "UpdatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Record_Label",
                schema: "osdu",
                table: "Record",
                column: "Label");

            migrationBuilder.CreateIndex(
                name: "IX_Record_LastSubmissionId_WorkBatch",
                schema: "osdu",
                table: "Record",
                columns: new[] { "LastSubmissionId", "WorkBatch" });

            migrationBuilder.CreateIndex(
                name: "IX_Record_LeaseOwner",
                schema: "osdu",
                table: "Record",
                column: "LeaseOwner");

            migrationBuilder.CreateIndex(
                name: "IX_Record_SourceFileName",
                schema: "osdu",
                table: "Record",
                column: "SourceFileName");

            migrationBuilder.CreateIndex(
                name: "IX_Record_SourceKey",
                schema: "osdu",
                table: "Record",
                column: "SourceKey");

            migrationBuilder.CreateIndex(
                name: "IX_Record_Status_LeaseExpiresUtc",
                schema: "osdu",
                table: "Record",
                columns: new[] { "Status", "LeaseExpiresUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Record_TargetId",
                schema: "osdu",
                table: "Record",
                column: "TargetId");

            migrationBuilder.CreateIndex(
                name: "IX_Retrieval_FlowId_StartedUtc",
                schema: "osdu",
                table: "Retrieval",
                columns: new[] { "FlowId", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Retrieval_FlowId_Status_StartedUtc",
                schema: "osdu",
                table: "Retrieval",
                columns: new[] { "FlowId", "Status", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Retrieval_RunId",
                schema: "osdu",
                table: "Retrieval",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_Submission_FlowId_Kind_ReceivedUtc",
                schema: "osdu",
                table: "Submission",
                columns: new[] { "FlowId", "Kind", "ReceivedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Submission_FlowId_ReceivedUtc",
                schema: "osdu",
                table: "Submission",
                columns: new[] { "FlowId", "ReceivedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Submission_FlowId_Reference",
                schema: "osdu",
                table: "Submission",
                columns: new[] { "FlowId", "Reference" });

            migrationBuilder.CreateIndex(
                name: "IX_Submission_FlowId_Status",
                schema: "osdu",
                table: "Submission",
                columns: new[] { "FlowId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Submission_RunId",
                schema: "osdu",
                table: "Submission",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_SubmissionLanding_FileName",
                schema: "osdu",
                table: "SubmissionLanding",
                column: "FileName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Template_Kind_Version",
                schema: "osdu",
                table: "Template",
                columns: new[] { "Kind", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UpdateTag_Scope_TypeName_ItemId_Path_Status",
                schema: "osdu",
                table: "UpdateTag",
                columns: new[] { "Scope", "TypeName", "ItemId", "Path", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_UpdateTag_Status",
                schema: "osdu",
                table: "UpdateTag",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_WorkBatch_FlowId_Status_CreatedUtc",
                schema: "osdu",
                table: "WorkBatch",
                columns: new[] { "FlowId", "Status", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkBatch_Status_LeaseExpiresUtc",
                schema: "osdu",
                table: "WorkBatch",
                columns: new[] { "Status", "LeaseExpiresUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkBatch_SubmissionId_Status",
                schema: "osdu",
                table: "WorkBatch",
                columns: new[] { "SubmissionId", "Status" });

            // The ledger's statistics: an indexed view SQL Server maintains in the transaction of every record write, so a
            // flow's counts are derived from the ledger and never counted separately. EF cannot declare an indexed view,
            // so the statements are fixed here; the grouping hour is computed against a constant origin because an
            // indexed view needs deterministic expressions.
            migrationBuilder.Sql(
                "CREATE VIEW [osdu].[RecordCount] WITH SCHEMABINDING AS "
                + "SELECT [FlowId], [Status], [LastVerifyOutcome], "
                + "DATEADD(hour, DATEDIFF(hour, CONVERT(datetime2(0), '20000101', 112), [LastDeliveredUtc]), CONVERT(datetime2(0), '20000101', 112)) AS [DeliveredHour], "
                + "COUNT_BIG(*) AS [Records] "
                + "FROM [osdu].[Record] "
                + "GROUP BY [FlowId], [Status], [LastVerifyOutcome], "
                + "DATEADD(hour, DATEDIFF(hour, CONVERT(datetime2(0), '20000101', 112), [LastDeliveredUtc]), CONVERT(datetime2(0), '20000101', 112))");

            migrationBuilder.Sql(
                "CREATE UNIQUE CLUSTERED INDEX [IX_RecordCount] ON [osdu].[RecordCount] "
                + "([FlowId], [Status], [LastVerifyOutcome], [DeliveredHour])");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The view is bound to the record table's schema, so it goes before the table does.
            migrationBuilder.Sql("DROP VIEW [osdu].[RecordCount]");

            migrationBuilder.DropTable(
                name: "Activity",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "Attempt",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "CacheDefinition",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "CacheItem",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "CacheMember",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "CacheSet",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "CacheSetEntry",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "CacheVersion",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "InlineSubmission",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "Mapping",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "Record",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "Retrieval",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "SchemaVersion",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "SourceWatermark",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "Submission",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "SubmissionLanding",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "Template",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "UpdateTag",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "WorkBatch",
                schema: "osdu");
        }
    }
}
