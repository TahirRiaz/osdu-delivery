using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.EnsureSchema(
                name: "delivery");

            migrationBuilder.CreateTable(
                name: "AccessToken",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Prefix = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Scopes = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastUsedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RevokedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessToken", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Activity",
                schema: "delivery",
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
                name: "ActivityEvent",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ActivityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SubjectKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Level = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Step = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Message = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Terminal = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityEvent", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Attempt",
                schema: "delivery",
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
                    Error = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attempt", x => x.AttemptId);
                });

            migrationBuilder.CreateTable(
                name: "ComputeTask",
                schema: "catalog",
                columns: table => new
                {
                    TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Operation = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SourceRef = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    ProviderKind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    ArgumentsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TargetPool = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    RequestedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    EnqueuedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EndUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClaimedByNode = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CancelRequestedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResultJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComputeTask", x => x.TaskId);
                });

            migrationBuilder.CreateTable(
                name: "FlowVersion",
                schema: "catalog",
                columns: table => new
                {
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Yaml = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlowVersion", x => x.ContentHash);
                });

            migrationBuilder.CreateTable(
                name: "MaintenanceSetting",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    RunTraceRetentionDays = table.Column<int>(type: "int", nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceSetting", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Mapping",
                schema: "delivery",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Version = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
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
                name: "Node",
                schema: "catalog",
                columns: table => new
                {
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Pool = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    BusyRuns = table.Column<int>(type: "int", nullable: false),
                    RestartRequestedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Node", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "NotificationDelivery",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Target = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    TextBody = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    HtmlBody = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SlackBlocksJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EventCount = table.Column<int>(type: "int", nullable: false),
                    FirstEventId = table.Column<long>(type: "bigint", nullable: false),
                    LastEventId = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClaimedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SentUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationDelivery", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationDigest",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Origin = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    PeriodStartUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PeriodEndUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    GeneratedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    GeneratedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EventCount = table.Column<int>(type: "int", nullable: false),
                    FlowCount = table.Column<int>(type: "int", nullable: false),
                    FailedCount = table.Column<int>(type: "int", nullable: false),
                    CancelledCount = table.Column<int>(type: "int", nullable: false),
                    SkippedCount = table.Column<int>(type: "int", nullable: false),
                    FirstEventId = table.Column<long>(type: "bigint", nullable: false),
                    LastEventId = table.Column<long>(type: "bigint", nullable: false),
                    Truncated = table.Column<bool>(type: "bit", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    TextBody = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    HtmlBody = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SlackBlocksJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    GroupsJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationDigest", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationEvent",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PipelineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FlowName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    FlowKind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    OccurredUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DetectedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationEvent", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationSubscription",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Mode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Kinds = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    FlowPattern = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    EmailAddress = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    SlackTarget = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    DigestIntervalMinutes = table.Column<int>(type: "int", nullable: false),
                    CooldownMinutes = table.Column<int>(type: "int", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    LastEventId = table.Column<long>(type: "bigint", nullable: false),
                    NextDueUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSentUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationSubscription", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationWatermark",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    RunsWatermarkUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DigestDueUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DigestPeriodStartUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DigestCursorEventId = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationWatermark", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Pipeline",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Batch = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    RelativePath = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    ExecutionMode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Lifecycle = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    SourceServer = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    TargetServer = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Yaml = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DefinitionJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Active = table.Column<bool>(type: "bit", nullable: false),
                    Wave = table.Column<int>(type: "int", nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Pipeline", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Record",
                schema: "delivery",
                columns: table => new
                {
                    DeliveryKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FlowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    MappingName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RenderContext = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceFingerprint = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    MetadataHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PayloadHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
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
                    PendingDocument = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PendingRenderContext = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PendingSourceFingerprint = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PendingMetadataHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PendingPayloadHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PendingPayloadLocation = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    PendingMetadata = table.Column<bool>(type: "bit", nullable: false),
                    PendingPayload = table.Column<bool>(type: "bit", nullable: false),
                    Blocked = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Record", x => x.DeliveryKey);
                });

            migrationBuilder.CreateTable(
                name: "Repo",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RemoteUrl = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    RootPath = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    FirstSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSyncUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Repo", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RepoSource",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RemoteUrl = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    Branch = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CredentialReference = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    CredentialUsername = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ExcludedFlowPaths = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    SyncIntervalSeconds = table.Column<int>(type: "int", nullable: false),
                    NextSyncUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSyncUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSyncedSha = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ForceLineageOnNextSync = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepoSource", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Role",
                schema: "catalog",
                columns: table => new
                {
                    Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Scopes = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Role", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "Run",
                schema: "catalog",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PipelineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FlowName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    FlowKind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Success = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    EnqueuedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClaimedByNode = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Attempt = table.Column<int>(type: "int", nullable: false),
                    CancelRequestedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TargetPool = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CommitSha = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    FlowVersionHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Operation = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Force = table.Column<bool>(type: "bit", nullable: false),
                    SubmissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ParametersJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GroupWave = table.Column<int>(type: "int", nullable: false),
                    GroupMaxConcurrency = table.Column<int>(type: "int", nullable: true),
                    TriggerSource = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    TriggerScheduleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RequestedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SchemaVersion = table.Column<int>(type: "int", nullable: false),
                    WrittenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EndUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DurationSeconds = table.Column<double>(type: "float", nullable: true),
                    RowsLoaded = table.Column<long>(type: "bigint", nullable: true),
                    RowsInserted = table.Column<long>(type: "bigint", nullable: true),
                    RowsUpdated = table.Column<long>(type: "bigint", nullable: true),
                    RowsDeleted = table.Column<long>(type: "bigint", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Host = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ResultSubmissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecordsPlanned = table.Column<int>(type: "int", nullable: true),
                    RecordsDelivered = table.Column<int>(type: "int", nullable: true),
                    RecordsHeld = table.Column<int>(type: "int", nullable: true),
                    RecordsFailed = table.Column<int>(type: "int", nullable: true),
                    RecordsSkipped = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Run", x => x.RunId);
                });

            migrationBuilder.CreateTable(
                name: "RunEvent",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Level = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Step = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Message = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Rows = table.Column<long>(type: "bigint", nullable: true),
                    ElapsedMs = table.Column<double>(type: "float", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunEvent", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RunGroup",
                schema: "catalog",
                columns: table => new
                {
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Mode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Anchor = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    MemberCount = table.Column<int>(type: "int", nullable: false),
                    CommitSha = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    EnqueuedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunGroup", x => x.GroupId);
                });

            migrationBuilder.CreateTable(
                name: "Schedule",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Cron = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    IntervalSeconds = table.Column<int>(type: "int", nullable: true),
                    ParentFreshnessHours = table.Column<int>(type: "int", nullable: false),
                    LastStaleParents = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    LastParentFireUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Timezone = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    Catchup = table.Column<bool>(type: "bit", nullable: false),
                    MaxConcurrency = table.Column<int>(type: "int", nullable: true),
                    Paused = table.Column<bool>(type: "bit", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    DefinitionPath = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    DefinitionFlow = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    DefinitionYaml = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NextFireUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastFireUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Schedule", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScheduleMember",
                schema: "catalog",
                columns: table => new
                {
                    ScheduleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PipelineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FlowName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleMember", x => new { x.ScheduleId, x.PipelineId });
                });

            migrationBuilder.CreateTable(
                name: "Snapshot",
                schema: "delivery",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CapturedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Current = table.Column<bool>(type: "bit", nullable: false),
                    RelativePath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    SummaryJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Snapshot", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SourceWatermark",
                schema: "delivery",
                columns: table => new
                {
                    FlowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    TableName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    RecordedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceWatermark", x => new { x.FlowId, x.Scope, x.TableName });
                });

            migrationBuilder.CreateTable(
                name: "Submission",
                schema: "delivery",
                columns: table => new
                {
                    SubmissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FlowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FlowName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    MappingReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RenderContext = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DropLocation = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ParametersJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RecordCount = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ReceivedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Planned = table.Column<int>(type: "int", nullable: false),
                    SkippedUnchanged = table.Column<int>(type: "int", nullable: false),
                    Blocked = table.Column<int>(type: "int", nullable: false),
                    Delivered = table.Column<int>(type: "int", nullable: false),
                    Held = table.Column<int>(type: "int", nullable: false),
                    Failed = table.Column<int>(type: "int", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Submission", x => x.SubmissionId);
                });

            migrationBuilder.CreateTable(
                name: "User",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Username = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    DisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    PasswordHash = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    Role = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ExternalObjectId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Active = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastLoginUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_User", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkerPool",
                schema: "catalog",
                columns: table => new
                {
                    Pool = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    MinReplicas = table.Column<int>(type: "int", nullable: false),
                    ManualReplicas = table.Column<int>(type: "int", nullable: false),
                    ManualUntilUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerPool", x => x.Pool);
                });

            migrationBuilder.CreateTable(
                name: "ScheduleParent",
                schema: "catalog",
                columns: table => new
                {
                    ScheduleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleParent", x => new { x.ScheduleId, x.ParentName });
                    table.ForeignKey(
                        name: "FK_ScheduleParent_Schedule_ScheduleId",
                        column: x => x.ScheduleId,
                        principalSchema: "catalog",
                        principalTable: "Schedule",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccessToken_TokenHash",
                schema: "catalog",
                table: "AccessToken",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessToken_UserId",
                schema: "catalog",
                table: "AccessToken",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Activity_Actor_StartedUtc",
                schema: "delivery",
                table: "Activity",
                columns: new[] { "Actor", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Activity_DeliveryKey_StartedUtc",
                schema: "delivery",
                table: "Activity",
                columns: new[] { "DeliveryKey", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Activity_FlowId_StartedUtc",
                schema: "delivery",
                table: "Activity",
                columns: new[] { "FlowId", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Activity_Kind_StartedUtc",
                schema: "delivery",
                table: "Activity",
                columns: new[] { "Kind", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Activity_RunId",
                schema: "delivery",
                table: "Activity",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_Activity_StartedUtc",
                schema: "delivery",
                table: "Activity",
                column: "StartedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Activity_SubmissionId",
                schema: "delivery",
                table: "Activity",
                column: "SubmissionId");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEvent_Kind_SubjectKey_Id",
                schema: "catalog",
                table: "ActivityEvent",
                columns: new[] { "Kind", "SubjectKey", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Attempt_DeliveryKey_StartedUtc",
                schema: "delivery",
                table: "Attempt",
                columns: new[] { "DeliveryKey", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Attempt_RunId",
                schema: "delivery",
                table: "Attempt",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_Attempt_StartedUtc",
                schema: "delivery",
                table: "Attempt",
                column: "StartedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Attempt_SubmissionId",
                schema: "delivery",
                table: "Attempt",
                column: "SubmissionId");

            migrationBuilder.CreateIndex(
                name: "IX_ComputeTask_EnqueuedUtc",
                schema: "catalog",
                table: "ComputeTask",
                column: "EnqueuedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ComputeTask_SourceRef",
                schema: "catalog",
                table: "ComputeTask",
                column: "SourceRef");

            migrationBuilder.CreateIndex(
                name: "IX_ComputeTask_Status_EnqueuedUtc",
                schema: "catalog",
                table: "ComputeTask",
                columns: new[] { "Status", "EnqueuedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Mapping_RepoId_Kind",
                schema: "delivery",
                table: "Mapping",
                columns: new[] { "RepoId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_Mapping_RepoId_Reference",
                schema: "delivery",
                table: "Mapping",
                columns: new[] { "RepoId", "Reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Node_LastSeenUtc",
                schema: "catalog",
                table: "Node",
                column: "LastSeenUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Node_Pool",
                schema: "catalog",
                table: "Node",
                column: "Pool");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDelivery_CreatedUtc",
                schema: "catalog",
                table: "NotificationDelivery",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDelivery_Status_NextAttemptUtc",
                schema: "catalog",
                table: "NotificationDelivery",
                columns: new[] { "Status", "NextAttemptUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDelivery_UserId_CreatedUtc",
                schema: "catalog",
                table: "NotificationDelivery",
                columns: new[] { "UserId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDigest_GeneratedUtc",
                schema: "catalog",
                table: "NotificationDigest",
                column: "GeneratedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDigest_Origin_PeriodEndUtc",
                schema: "catalog",
                table: "NotificationDigest",
                columns: new[] { "Origin", "PeriodEndUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationEvent_DetectedUtc",
                schema: "catalog",
                table: "NotificationEvent",
                column: "DetectedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationEvent_RunId_Kind",
                schema: "catalog",
                table: "NotificationEvent",
                columns: new[] { "RunId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationSubscription_Enabled_NextDueUtc",
                schema: "catalog",
                table: "NotificationSubscription",
                columns: new[] { "Enabled", "NextDueUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationSubscription_UserId",
                schema: "catalog",
                table: "NotificationSubscription",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Pipeline_Active",
                schema: "catalog",
                table: "Pipeline",
                column: "Active");

            migrationBuilder.CreateIndex(
                name: "IX_Pipeline_Kind",
                schema: "catalog",
                table: "Pipeline",
                column: "Kind");

            migrationBuilder.CreateIndex(
                name: "IX_Pipeline_Name",
                schema: "catalog",
                table: "Pipeline",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Pipeline_RepoId_Name",
                schema: "catalog",
                table: "Pipeline",
                columns: new[] { "RepoId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Pipeline_RepoId_Wave",
                schema: "catalog",
                table: "Pipeline",
                columns: new[] { "RepoId", "Wave" });

            migrationBuilder.CreateIndex(
                name: "IX_Record_FlowId_Label",
                schema: "delivery",
                table: "Record",
                columns: new[] { "FlowId", "Label" });

            migrationBuilder.CreateIndex(
                name: "IX_Record_FlowId_LastDeliveredUtc",
                schema: "delivery",
                table: "Record",
                columns: new[] { "FlowId", "LastDeliveredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Record_FlowId_LastSubmissionId",
                schema: "delivery",
                table: "Record",
                columns: new[] { "FlowId", "LastSubmissionId" });

            migrationBuilder.CreateIndex(
                name: "IX_Record_FlowId_LastVerifiedUtc",
                schema: "delivery",
                table: "Record",
                columns: new[] { "FlowId", "LastVerifiedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Record_FlowId_LastVerifyOutcome",
                schema: "delivery",
                table: "Record",
                columns: new[] { "FlowId", "LastVerifyOutcome" });

            migrationBuilder.CreateIndex(
                name: "IX_Record_FlowId_SourceKey",
                schema: "delivery",
                table: "Record",
                columns: new[] { "FlowId", "SourceKey" });

            migrationBuilder.CreateIndex(
                name: "IX_Record_FlowId_Status_NextAttemptUtc",
                schema: "delivery",
                table: "Record",
                columns: new[] { "FlowId", "Status", "NextAttemptUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Record_FlowId_TargetId",
                schema: "delivery",
                table: "Record",
                columns: new[] { "FlowId", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "IX_Record_FlowId_UpdatedUtc",
                schema: "delivery",
                table: "Record",
                columns: new[] { "FlowId", "UpdatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Record_Status_LeaseExpiresUtc",
                schema: "delivery",
                table: "Record",
                columns: new[] { "Status", "LeaseExpiresUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Repo_Name",
                schema: "catalog",
                table: "Repo",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RepoSource_Name",
                schema: "catalog",
                table: "RepoSource",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RepoSource_NextSyncUtc",
                schema: "catalog",
                table: "RepoSource",
                column: "NextSyncUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Run_FlowName",
                schema: "catalog",
                table: "Run",
                column: "FlowName");

            migrationBuilder.CreateIndex(
                name: "IX_Run_GroupId_GroupWave_Status",
                schema: "catalog",
                table: "Run",
                columns: new[] { "GroupId", "GroupWave", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Run_PipelineId",
                schema: "catalog",
                table: "Run",
                column: "PipelineId");

            migrationBuilder.CreateIndex(
                name: "IX_Run_PipelineId_Operation",
                schema: "catalog",
                table: "Run",
                columns: new[] { "PipelineId", "Operation" });

            migrationBuilder.CreateIndex(
                name: "IX_Run_PipelineId_WrittenUtc_RunId",
                schema: "catalog",
                table: "Run",
                columns: new[] { "PipelineId", "WrittenUtc", "RunId" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_Run_RepoId",
                schema: "catalog",
                table: "Run",
                column: "RepoId");

            migrationBuilder.CreateIndex(
                name: "IX_Run_ResultSubmissionId",
                schema: "catalog",
                table: "Run",
                column: "ResultSubmissionId");

            migrationBuilder.CreateIndex(
                name: "IX_Run_Status_EnqueuedUtc",
                schema: "catalog",
                table: "Run",
                columns: new[] { "Status", "EnqueuedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Run_Status_WrittenUtc",
                schema: "catalog",
                table: "Run",
                columns: new[] { "Status", "WrittenUtc" })
                .Annotation("SqlServer:Include", new[] { "PipelineId" });

            migrationBuilder.CreateIndex(
                name: "IX_Run_SubmissionId",
                schema: "catalog",
                table: "Run",
                column: "SubmissionId");

            migrationBuilder.CreateIndex(
                name: "IX_Run_WrittenUtc",
                schema: "catalog",
                table: "Run",
                column: "WrittenUtc");

            migrationBuilder.CreateIndex(
                name: "UX_Run_RunningPipeline",
                schema: "catalog",
                table: "Run",
                column: "PipelineId",
                unique: true,
                filter: "[Status] = 'running'");

            migrationBuilder.CreateIndex(
                name: "IX_RunEvent_RunId",
                schema: "catalog",
                table: "RunEvent",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_RunGroup_RepoId_EnqueuedUtc",
                schema: "catalog",
                table: "RunGroup",
                columns: new[] { "RepoId", "EnqueuedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Schedule_NextFireUtc",
                schema: "catalog",
                table: "Schedule",
                column: "NextFireUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Schedule_RepoId",
                schema: "catalog",
                table: "Schedule",
                column: "RepoId");

            migrationBuilder.CreateIndex(
                name: "IX_Schedule_RepoId_Name",
                schema: "catalog",
                table: "Schedule",
                columns: new[] { "RepoId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleMember_PipelineId",
                schema: "catalog",
                table: "ScheduleMember",
                column: "PipelineId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleMember_RepoId",
                schema: "catalog",
                table: "ScheduleMember",
                column: "RepoId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleParent_RepoId",
                schema: "catalog",
                table: "ScheduleParent",
                column: "RepoId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleParent_RepoId_ParentName",
                schema: "catalog",
                table: "ScheduleParent",
                columns: new[] { "RepoId", "ParentName" });

            migrationBuilder.CreateIndex(
                name: "IX_Snapshot_RepoId_Kind_Name",
                schema: "delivery",
                table: "Snapshot",
                columns: new[] { "RepoId", "Kind", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Submission_FlowId_ReceivedUtc",
                schema: "delivery",
                table: "Submission",
                columns: new[] { "FlowId", "ReceivedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Submission_FlowId_Status",
                schema: "delivery",
                table: "Submission",
                columns: new[] { "FlowId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_User_ExternalObjectId",
                schema: "catalog",
                table: "User",
                column: "ExternalObjectId",
                unique: true,
                filter: "[ExternalObjectId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_User_Username",
                schema: "catalog",
                table: "User",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccessToken",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "Activity",
                schema: "delivery");

            migrationBuilder.DropTable(
                name: "ActivityEvent",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "Attempt",
                schema: "delivery");

            migrationBuilder.DropTable(
                name: "ComputeTask",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "FlowVersion",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "MaintenanceSetting",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "Mapping",
                schema: "delivery");

            migrationBuilder.DropTable(
                name: "Node",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "NotificationDelivery",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "NotificationDigest",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "NotificationEvent",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "NotificationSubscription",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "NotificationWatermark",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "Pipeline",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "Record",
                schema: "delivery");

            migrationBuilder.DropTable(
                name: "Repo",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "RepoSource",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "Role",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "Run",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "RunEvent",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "RunGroup",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "ScheduleMember",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "ScheduleParent",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "Snapshot",
                schema: "delivery");

            migrationBuilder.DropTable(
                name: "SourceWatermark",
                schema: "delivery");

            migrationBuilder.DropTable(
                name: "Submission",
                schema: "delivery");

            migrationBuilder.DropTable(
                name: "User",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "WorkerPool",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "Schedule",
                schema: "catalog");
        }
    }
}
