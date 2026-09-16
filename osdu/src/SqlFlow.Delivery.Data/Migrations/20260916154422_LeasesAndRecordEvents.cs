using System;
using System.Globalization;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// Moves the worker's writes off the record table while it delivers. A claim becomes one row in <c>osdu.Lease</c>,
    /// renewed alone however many records it holds, and what the worker learns (a completed step, a try's outcome) is
    /// appended to <c>osdu.RecordEvent</c> and applied to the records when the lease is checkpointed, closed or recovered.
    /// The records keep their lease token; the expiry moves to the lease, so <c>Record.LeaseExpiresUtc</c> and
    /// <c>WorkBatch.LeaseExpiresUtc</c> go. Every lease a stopped worker left behind becomes a lease row expiring when it
    /// did, and the next claim of its flow recovers it like any other. The two covering indexes that carried the record's
    /// expiry are rebuilt in place without it.
    /// </summary>
    public partial class LeasesAndRecordEvents : Migration
    {
        private const string Schema = "osdu";

        // A token is the worker's name, a slash and a 32-character id; a token of any other shape is its own owner.
        private const string OwnerOfToken =
            "CASE WHEN LEN({0}) > 33 AND SUBSTRING({0}, LEN({0}) - 32, 1) = N'/' THEN LEFT({0}, LEN({0}) - 33) ELSE {0} END";

        private static readonly string LeasesFromBatches = $"""
            INSERT INTO [osdu].[Lease] ([Token], [FlowId], [SubmissionId], [WorkBatch], [Owner], [RunId], [AcquiredUtc], [ExpiresUtc])
            SELECT b.[LeaseOwner], b.[FlowId], b.[SubmissionId], b.[Index], {string.Format(CultureInfo.InvariantCulture, OwnerOfToken, "b.[LeaseOwner]")}, b.[RunId],
                   COALESCE(b.[StartedUtc], b.[CreatedUtc]), COALESCE(b.[LeaseExpiresUtc], SYSUTCDATETIME())
            FROM [osdu].[WorkBatch] AS b
            WHERE b.[LeaseOwner] IS NOT NULL;
            """;

        // The records a retry claim leased carry a token no batch names; each such token becomes one lease, expiring when
        // the last of its records' leases did.
        private static readonly string LeasesFromRecords = $"""
            INSERT INTO [osdu].[Lease] ([Token], [FlowId], [SubmissionId], [WorkBatch], [Owner], [RunId], [AcquiredUtc], [ExpiresUtc])
            SELECT l.[LeaseOwner], l.[FlowId], NULL, NULL, {string.Format(CultureInfo.InvariantCulture, OwnerOfToken, "l.[LeaseOwner]")}, NULL,
                   COALESCE(l.[UpdatedUtc], SYSUTCDATETIME()), COALESCE(l.[LeaseExpiresUtc], SYSUTCDATETIME())
            FROM (
                SELECT r.[LeaseOwner], r.[FlowId], r.[UpdatedUtc],
                       MAX(r.[LeaseExpiresUtc]) OVER (PARTITION BY r.[LeaseOwner]) AS [LeaseExpiresUtc],
                       ROW_NUMBER() OVER (PARTITION BY r.[LeaseOwner] ORDER BY r.[UpdatedUtc] DESC) AS [Rank]
                FROM [osdu].[Record] AS r
                WHERE r.[LeaseOwner] IS NOT NULL) AS l
            WHERE l.[Rank] = 1
              AND NOT EXISTS (SELECT 1 FROM [osdu].[Lease] AS e WHERE e.[Token] = l.[LeaseOwner]);
            """;

        /// <summary>
        /// The event table's clustered key grows at one end, where every worker appends. <c>OPTIMIZE_FOR_SEQUENTIAL_KEY</c>
        /// keeps the appends from queueing on that last page, where the server has it (SQL Server 2019 and later, Azure SQL).
        /// </summary>
        private const string SequentialEventKey =
            "IF CAST(SERVERPROPERTY('ProductMajorVersion') AS int) >= 15 OR CAST(SERVERPROPERTY('EngineEdition') AS int) IN (5, 8)\n"
            + "    EXEC(N'ALTER INDEX [PK_RecordEvent] ON [osdu].[RecordEvent] SET (OPTIMIZE_FOR_SEQUENTIAL_KEY = ON)');";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Lease",
                schema: Schema,
                columns: table => new
                {
                    Token = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    FlowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubmissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WorkBatch = table.Column<int>(type: "int", nullable: true),
                    Owner = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AcquiredUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Lease", x => x.Token);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Lease_FlowId_ExpiresUtc",
                schema: Schema,
                table: "Lease",
                columns: new[] { "FlowId", "ExpiresUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Lease_SubmissionId_ExpiresUtc",
                schema: Schema,
                table: "Lease",
                columns: new[] { "SubmissionId", "ExpiresUtc" });

            // The leases in flight, before the columns that describe them go.
            migrationBuilder.Sql(LeasesFromBatches);
            migrationBuilder.Sql(LeasesFromRecords);

            migrationBuilder.DropIndex(name: "IX_Record_Status_LeaseExpiresUtc", schema: Schema, table: "Record");
            migrationBuilder.Sql("""
                CREATE INDEX [IX_Record_FlowId_Status_NextAttemptUtc]
                    ON [osdu].[Record] ([FlowId], [Status], [NextAttemptUtc])
                    INCLUDE ([LastSubmissionId], [UpdatedUtc], [PendingDocumentRef])
                    WITH (DROP_EXISTING = ON);
                """);
            migrationBuilder.Sql("""
                CREATE INDEX [IX_Record_FlowId_LastSubmissionId_Status_NextAttemptUtc]
                    ON [osdu].[Record] ([FlowId], [LastSubmissionId], [Status], [NextAttemptUtc])
                    INCLUDE ([UpdatedUtc])
                    WITH (DROP_EXISTING = ON);
                """);
            migrationBuilder.DropColumn(name: "LeaseExpiresUtc", schema: Schema, table: "Record");

            migrationBuilder.DropIndex(name: "IX_WorkBatch_Status_LeaseExpiresUtc", schema: Schema, table: "WorkBatch");
            migrationBuilder.DropColumn(name: "LeaseExpiresUtc", schema: Schema, table: "WorkBatch");

            migrationBuilder.CreateTable(
                name: "RecordEvent",
                schema: Schema,
                columns: table => new
                {
                    EventId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LeaseToken = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    FlowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeliveryKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    AtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StepJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    Promote = table.Column<bool>(type: "bit", nullable: false),
                    NothingSent = table.Column<bool>(type: "bit", nullable: false),
                    NextAttemptUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    TargetId = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TargetVersion = table.Column<long>(type: "bigint", nullable: true),
                    TargetStateJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PendingStepJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClaimSubmissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClaimDocumentRef = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ClaimRenderContext = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClaimSourceFingerprint = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ClaimSourceModifiedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClaimSourceFileName = table.Column<string>(type: "nvarchar(800)", maxLength: 800, nullable: true),
                    ClaimSourceRowNumber = table.Column<long>(type: "bigint", nullable: true),
                    ClaimSourceUpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClaimMetadataHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ClaimPayloadHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ClaimPayloadModifiedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClaimMetadata = table.Column<bool>(type: "bit", nullable: false),
                    ClaimPayload = table.Column<bool>(type: "bit", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecordEvent", x => x.EventId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecordEvent_LeaseToken_FlowId_DeliveryKey_EventId",
                schema: Schema,
                table: "RecordEvent",
                columns: new[] { "LeaseToken", "FlowId", "DeliveryKey", "EventId" });

            migrationBuilder.CreateIndex(
                name: "IX_RecordEvent_FlowId_AtUtc",
                schema: Schema,
                table: "RecordEvent",
                columns: new[] { "FlowId", "AtUtc" })
                .Annotation("SqlServer:Include", new[] { "LeaseToken" });

            migrationBuilder.Sql(SequentialEventKey);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Events a lease has not applied yet exist nowhere else, and the schema below has nowhere to keep them.
            migrationBuilder.Sql("""
                DECLARE @unapplied bigint = (SELECT COUNT_BIG(*) FROM [osdu].[RecordEvent]);
                IF @unapplied > 0
                BEGIN
                    DECLARE @message nvarchar(2048) = CONCAT(
                        N'LeasesAndRecordEvents cannot be reverted: ', @unapplied,
                        N' delivery event(s) in osdu.RecordEvent have not been applied to their records. Let the running workers finish, and run each flow of a stopped worker again once its leases have expired so the run recovers them, then revert again.');
                    THROW 50000, @message, 1;
                END
                """);

            migrationBuilder.AddColumn<DateTime>(name: "LeaseExpiresUtc", schema: Schema, table: "WorkBatch", type: "datetime2", nullable: true);
            migrationBuilder.AddColumn<DateTime>(name: "LeaseExpiresUtc", schema: Schema, table: "Record", type: "datetime2", nullable: true);
            migrationBuilder.Sql("""
                UPDATE b SET [LeaseExpiresUtc] = l.[ExpiresUtc]
                FROM [osdu].[WorkBatch] AS b
                INNER JOIN [osdu].[Lease] AS l ON l.[Token] = b.[LeaseOwner];
                UPDATE r SET [LeaseExpiresUtc] = l.[ExpiresUtc]
                FROM [osdu].[Record] AS r
                INNER JOIN [osdu].[Lease] AS l ON l.[Token] = r.[LeaseOwner];
                """);

            migrationBuilder.CreateIndex(
                name: "IX_WorkBatch_Status_LeaseExpiresUtc",
                schema: Schema,
                table: "WorkBatch",
                columns: new[] { "Status", "LeaseExpiresUtc" });

            migrationBuilder.Sql("""
                CREATE INDEX [IX_Record_FlowId_Status_NextAttemptUtc]
                    ON [osdu].[Record] ([FlowId], [Status], [NextAttemptUtc])
                    INCLUDE ([LastSubmissionId], [LeaseExpiresUtc], [UpdatedUtc], [PendingDocumentRef])
                    WITH (DROP_EXISTING = ON);
                """);
            migrationBuilder.Sql("""
                CREATE INDEX [IX_Record_FlowId_LastSubmissionId_Status_NextAttemptUtc]
                    ON [osdu].[Record] ([FlowId], [LastSubmissionId], [Status], [NextAttemptUtc])
                    INCLUDE ([LeaseExpiresUtc], [UpdatedUtc])
                    WITH (DROP_EXISTING = ON);
                """);
            migrationBuilder.CreateIndex(
                name: "IX_Record_Status_LeaseExpiresUtc",
                schema: Schema,
                table: "Record",
                columns: new[] { "Status", "LeaseExpiresUtc" });

            migrationBuilder.DropTable(name: "RecordEvent", schema: Schema);
            migrationBuilder.DropTable(name: "Lease", schema: Schema);
        }
    }
}
