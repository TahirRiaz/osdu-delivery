using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// Keys the ledger by partition (docs/ledger.md, Partitions). The ledger gains its directory: <c>osdu.LedgerPartition</c>
    /// numbers every partition it keeps rows under, and <c>osdu.Ledger</c> names the partition of every ledger identity.
    /// Every ledger table gains <c>PartitionId</c> as the first column of its primary key and of every index that reads a
    /// flow or a partition, so one partition's rows are kept together and every read seeks a range of one partition.
    /// <para>The directory is filled from what the ledger already holds. A ledger takes the partition the interface catalog
    /// describes it in; else the one partition its records' OSDU ids name; else it is unassigned (partition 0) until its next
    /// run adopts it: a ledger with no ids yet, and one whose ids name more than one partition, which that run then refuses.</para>
    /// <para>The migration is ordered for a ledger of hundreds of millions of records, as <c>LedgerPerFlow</c> is. The
    /// directory is filled first, while every table still has the indexes its reads need. Every nonclustered index of a
    /// ledger table is dropped before its clustered key changes and built once after, instead of being rebuilt by the key
    /// drop and again by the key build. The partition is written while the old key still holds, and the rebuild packs the
    /// rows it widened. The append-only keys are set to optimize for sequential keys again where the server supports it.</para>
    /// </summary>
    public partial class LedgerPartitions : Migration
    {
        private const string Schema = "osdu";

        /// <summary>
        /// Sets the sequential-key optimization on the append-only keys (the attempt table's key and start index, the event
        /// table's key), where the server has it (SQL Server 2019 and later, Azure SQL): every drain appends at their end.
        /// </summary>
        private const string SequentialKeys =
            "IF CAST(SERVERPROPERTY('ProductMajorVersion') AS int) >= 15 OR CAST(SERVERPROPERTY('EngineEdition') AS int) IN (5, 8)\n"
            + "BEGIN\n"
            + "    EXEC(N'ALTER INDEX [PK_Attempt] ON [osdu].[Attempt] SET (OPTIMIZE_FOR_SEQUENTIAL_KEY = ON)');\n"
            + "    EXEC(N'ALTER INDEX [IX_Attempt_StartedUtc] ON [osdu].[Attempt] SET (OPTIMIZE_FOR_SEQUENTIAL_KEY = ON)');\n"
            + "    EXEC(N'ALTER INDEX [PK_RecordEvent] ON [osdu].[RecordEvent] SET (OPTIMIZE_FOR_SEQUENTIAL_KEY = ON)');\n"
            + "END";

        /// <summary>
        /// Fills the directory from what the ledger holds: every ledger identity any ledger table carries, named as the
        /// interface catalog, its latest submission, activity or retrieval names it, in the partition the catalog describes
        /// it in or the one partition its records' OSDU ids name. A partition id is compared regardless of case, as the
        /// registry compares it, and an OSDU id's first segment is taken as a partition only when it is one.
        /// </summary>
        private const string FillDirectory = """
            CREATE TABLE #Ledger (
                [FlowId] uniqueidentifier NOT NULL PRIMARY KEY,
                [Kind] nvarchar(16) COLLATE DATABASE_DEFAULT NOT NULL DEFAULT N'delivery',
                [FlowName] nvarchar(200) COLLATE DATABASE_DEFAULT NULL,
                [Interface] nvarchar(64) COLLATE DATABASE_DEFAULT NOT NULL DEFAULT N'',
                [LedgerName] nvarchar(200) COLLATE DATABASE_DEFAULT NULL,
                [Partition] nvarchar(200) COLLATE DATABASE_DEFAULT NULL);

            INSERT INTO #Ledger ([FlowId])
            SELECT [FlowId] FROM [osdu].[Record]
            UNION SELECT [FlowId] FROM [osdu].[Submission]
            UNION SELECT [FlowId] FROM [osdu].[Attempt]
            UNION SELECT [FlowId] FROM [osdu].[WorkBatch]
            UNION SELECT [FlowId] FROM [osdu].[Lease]
            UNION SELECT [FlowId] FROM [osdu].[RecordEvent]
            UNION SELECT [FlowId] FROM [osdu].[RecordIdentity]
            UNION SELECT [FlowId] FROM [osdu].[SourceWatermark]
            UNION SELECT [FlowId] FROM [osdu].[Activity]
            UNION SELECT [FlowId] FROM [osdu].[Retrieval];

            -- The partition the interface catalog describes the ledger in, when it describes it in one.
            UPDATE l SET [Partition] = i.[Partition]
            FROM #Ledger AS l
            INNER JOIN (
                SELECT [LedgerFlowId], MIN([Partition]) AS [Partition]
                FROM [osdu].[Interface]
                WHERE [Partition] <> N''
                GROUP BY [LedgerFlowId]
                HAVING COUNT(DISTINCT [Partition]) = 1) AS i ON i.[LedgerFlowId] = l.[FlowId];

            -- Otherwise the one partition the OSDU ids of its records name.
            UPDATE l SET [Partition] = p.[Partition]
            FROM #Ledger AS l
            INNER JOIN (
                SELECT [FlowId], MIN([Partition]) AS [Partition]
                FROM (
                    SELECT r.[FlowId], LEFT(n.[Id], CHARINDEX(N':', n.[Id]) - 1) COLLATE DATABASE_DEFAULT AS [Partition]
                    FROM [osdu].[Record] AS r
                    CROSS APPLY (SELECT CONVERT(nvarchar(500), COALESCE(r.[TargetId], r.[ClaimedTargetId])) COLLATE DATABASE_DEFAULT AS [Id]) AS n
                    WHERE CHARINDEX(N':', n.[Id]) BETWEEN 2 AND 201) AS ids
                WHERE [Partition] NOT LIKE N'%[^A-Za-z0-9_.-]%'
                GROUP BY [FlowId]
                HAVING COUNT(DISTINCT [Partition]) = 1) AS p ON p.[FlowId] = l.[FlowId]
            WHERE l.[Partition] IS NULL;

            -- Its names: as the interface catalog describes it, else as its latest submission, activity or retrieval names it.
            UPDATE l SET [FlowName] = LEFT(i.[FlowName], 200), [Interface] = i.[Interface], [LedgerName] = i.[LedgerName]
            FROM #Ledger AS l
            CROSS APPLY (
                SELECT TOP (1) [FlowName], [Interface], [LedgerName]
                FROM [osdu].[Interface]
                WHERE [LedgerFlowId] = l.[FlowId]
                ORDER BY [Active] DESC, [LastSeenUtc] DESC) AS i;

            UPDATE l SET [FlowName] = s.[FlowName]
            FROM #Ledger AS l
            CROSS APPLY (SELECT TOP (1) [FlowName] FROM [osdu].[Submission] WHERE [FlowId] = l.[FlowId] ORDER BY [ReceivedUtc] DESC) AS s
            WHERE l.[FlowName] IS NULL;

            UPDATE l SET [FlowName] = a.[FlowName]
            FROM #Ledger AS l
            CROSS APPLY (SELECT TOP (1) [FlowName] FROM [osdu].[Activity] WHERE [FlowId] = l.[FlowId] ORDER BY [StartedUtc] DESC) AS a
            WHERE l.[FlowName] IS NULL;

            UPDATE l SET [FlowName] = r.[FlowName], [Kind] = N'retrieval'
            FROM #Ledger AS l
            CROSS APPLY (SELECT TOP (1) [FlowName] FROM [osdu].[Retrieval] WHERE [FlowId] = l.[FlowId] ORDER BY [StartedUtc] DESC) AS r;

            UPDATE #Ledger SET [FlowName] = CONVERT(nvarchar(36), [FlowId]) WHERE [FlowName] IS NULL;
            UPDATE #Ledger SET [LedgerName] = [FlowName] WHERE [LedgerName] IS NULL;

            -- One number per partition, in name order.
            INSERT INTO [osdu].[LedgerPartition] ([Name], [CreatedUtc])
            SELECT MIN([Partition]), SYSUTCDATETIME()
            FROM #Ledger
            WHERE [Partition] IS NOT NULL
            GROUP BY [Partition]
            ORDER BY MIN([Partition]);

            INSERT INTO [osdu].[Ledger] ([PartitionId], [FlowId], [Kind], [FlowName], [Interface], [LedgerName], [RegisteredUtc])
            SELECT ISNULL(p.[PartitionId], 0), l.[FlowId], l.[Kind], l.[FlowName], l.[Interface], l.[LedgerName], SYSUTCDATETIME()
            FROM #Ledger AS l
            LEFT JOIN [osdu].[LedgerPartition] AS p ON p.[Name] = l.[Partition];

            DROP TABLE #Ledger;
            """;

        /// <summary>One nonclustered index of a ledger table, as the migration drops and builds it.</summary>
        private sealed record TableIndex(string Name, string[] Columns, bool Unique = false, string Filter = null, string[] Include = null);

        /// <summary>One ledger table: its primary key before and after, and every nonclustered index before and after.</summary>
        private sealed record LedgerTable(string Name, string[] EarlierKey, string[] PartitionKey, TableIndex[] EarlierIndexes, TableIndex[] PartitionIndexes);

        private static readonly LedgerTable[] Tables =
        [
            new("Submission",
                ["SubmissionId"],
                ["PartitionId", "SubmissionId"],
                [
                    new("IX_Submission_FlowId_Kind_ReceivedUtc", ["FlowId", "Kind", "ReceivedUtc"]),
                    new("IX_Submission_FlowId_ReceivedUtc", ["FlowId", "ReceivedUtc"]),
                    new("IX_Submission_FlowId_Status", ["FlowId", "Status"]),
                    new("IX_Submission_RunId", ["RunId"]),
                ],
                [
                    new("IX_Submission_SubmissionId", ["SubmissionId"], Unique: true),
                    new("IX_Submission_PartitionId_FlowId_Kind_ReceivedUtc", ["PartitionId", "FlowId", "Kind", "ReceivedUtc"]),
                    new("IX_Submission_PartitionId_FlowId_ReceivedUtc", ["PartitionId", "FlowId", "ReceivedUtc"]),
                    new("IX_Submission_PartitionId_FlowId_Status", ["PartitionId", "FlowId", "Status"]),
                    new("IX_Submission_PartitionId_ReceivedUtc", ["PartitionId", "ReceivedUtc"]),
                    new("IX_Submission_RunId", ["RunId"]),
                ]),
            new("Record",
                ["FlowId", "DeliveryKey"],
                ["PartitionId", "FlowId", "DeliveryKey"],
                [
                    new("IX_Record_CacheSetId_DeliveryKey_FlowId", ["CacheSetId", "DeliveryKey", "FlowId"], Filter: "[CacheSetId] IS NOT NULL"),
                    new("IX_Record_ClaimedTargetId", ["ClaimedTargetId"], Unique: true, Filter: "[ClaimedTargetId] IS NOT NULL"),
                    new("IX_Record_DeliveryKey", ["DeliveryKey"]),
                    new("IX_Record_FlowId_Label", ["FlowId", "Label"]),
                    new("IX_Record_FlowId_LastDeliveredUtc", ["FlowId", "LastDeliveredUtc"]),
                    new("IX_Record_FlowId_LastSubmissionId_Status_NextAttemptUtc", ["FlowId", "LastSubmissionId", "Status", "NextAttemptUtc"], Include: ["UpdatedUtc"]),
                    new("IX_Record_FlowId_LastSubmissionId_UpdatedUtc", ["FlowId", "LastSubmissionId", "UpdatedUtc"]),
                    new("IX_Record_FlowId_LastVerifiedUtc", ["FlowId", "LastVerifiedUtc"]),
                    new("IX_Record_FlowId_LastVerifyOutcome", ["FlowId", "LastVerifyOutcome"]),
                    new("IX_Record_FlowId_PlanRequestedUtc", ["FlowId", "PlanRequestedUtc"], Filter: "[PlanRequestedUtc] IS NOT NULL"),
                    new("IX_Record_FlowId_SourceFileName_SourceRowNumber", ["FlowId", "SourceFileName", "SourceRowNumber"]),
                    new("IX_Record_FlowId_SourceKey", ["FlowId", "SourceKey"]),
                    new("IX_Record_FlowId_Status_NextAttemptUtc", ["FlowId", "Status", "NextAttemptUtc"], Include: ["LastSubmissionId", "UpdatedUtc", "PendingDocumentRef"]),
                    new("IX_Record_FlowId_Status_UpdatedUtc", ["FlowId", "Status", "UpdatedUtc"]),
                    new("IX_Record_FlowId_TargetId", ["FlowId", "TargetId"]),
                    new("IX_Record_FlowId_UpdatedUtc", ["FlowId", "UpdatedUtc"]),
                    new("IX_Record_Label", ["Label"]),
                    new("IX_Record_LastSubmissionId_WorkBatch", ["LastSubmissionId", "WorkBatch"]),
                    new("IX_Record_LeaseOwner", ["LeaseOwner"]),
                    new("IX_Record_SourceFileName", ["SourceFileName"]),
                    new("IX_Record_SourceKey", ["SourceKey"]),
                    new("IX_Record_Status_UpdatedUtc", ["Status", "UpdatedUtc"]),
                    new("IX_Record_TargetId", ["TargetId"]),
                    new("IX_Record_UpdatedUtc", ["UpdatedUtc"]),
                    new("IX_Record_WaitingFor", ["WaitingFor"], Filter: "[WaitingFor] IS NOT NULL"),
                ],
                [
                    new("IX_Record_PartitionId_FlowId_Status_NextAttemptUtc", ["PartitionId", "FlowId", "Status", "NextAttemptUtc"], Include: ["LastSubmissionId", "UpdatedUtc", "PendingDocumentRef"]),
                    new("IX_Record_PartitionId_FlowId_LastSubmissionId_Status_NextAttemptUtc", ["PartitionId", "FlowId", "LastSubmissionId", "Status", "NextAttemptUtc"], Include: ["UpdatedUtc"]),
                    new("IX_Record_CacheSetId_DeliveryKey_FlowId", ["CacheSetId", "DeliveryKey", "FlowId"], Filter: "[CacheSetId] IS NOT NULL"),
                    new("IX_Record_ClaimedTargetId", ["ClaimedTargetId"], Unique: true, Filter: "[ClaimedTargetId] IS NOT NULL"),
                    new("IX_Record_LastSubmissionId_WorkBatch", ["LastSubmissionId", "WorkBatch"]),
                    new("IX_Record_LeaseOwner", ["LeaseOwner"]),
                    new("IX_Record_PartitionId_FlowId_LastSubmissionId_UpdatedUtc", ["PartitionId", "FlowId", "LastSubmissionId", "UpdatedUtc"]),
                    new("IX_Record_PartitionId_FlowId_LastVerifiedUtc", ["PartitionId", "FlowId", "LastVerifiedUtc"]),
                    new("IX_Record_PartitionId_FlowId_Label", ["PartitionId", "FlowId", "Label"]),
                    new("IX_Record_PartitionId_FlowId_SourceKey", ["PartitionId", "FlowId", "SourceKey"]),
                    new("IX_Record_PartitionId_FlowId_TargetId", ["PartitionId", "FlowId", "TargetId"]),
                    new("IX_Record_PartitionId_FlowId_UpdatedUtc", ["PartitionId", "FlowId", "UpdatedUtc"]),
                    new("IX_Record_PartitionId_FlowId_Status_UpdatedUtc", ["PartitionId", "FlowId", "Status", "UpdatedUtc"]),
                    new("IX_Record_PartitionId_FlowId_LastDeliveredUtc", ["PartitionId", "FlowId", "LastDeliveredUtc"]),
                    new("IX_Record_PartitionId_FlowId_LastVerifyOutcome", ["PartitionId", "FlowId", "LastVerifyOutcome"]),
                    new("IX_Record_PartitionId_DeliveryKey", ["PartitionId", "DeliveryKey"]),
                    new("IX_Record_PartitionId_TargetId", ["PartitionId", "TargetId"]),
                    new("IX_Record_PartitionId_SourceKey", ["PartitionId", "SourceKey"]),
                    new("IX_Record_PartitionId_Label", ["PartitionId", "Label"]),
                    new("IX_Record_PartitionId_SourceFileName", ["PartitionId", "SourceFileName"]),
                    new("IX_Record_PartitionId_UpdatedUtc", ["PartitionId", "UpdatedUtc"]),
                    new("IX_Record_PartitionId_Status_UpdatedUtc", ["PartitionId", "Status", "UpdatedUtc"]),
                    new("IX_Record_PartitionId_FlowId_SourceFileName_SourceRowNumber", ["PartitionId", "FlowId", "SourceFileName", "SourceRowNumber"]),
                    new("IX_Record_PartitionId_FlowId_PlanRequestedUtc", ["PartitionId", "FlowId", "PlanRequestedUtc"], Filter: "[PlanRequestedUtc] IS NOT NULL"),
                    new("IX_Record_PartitionId_WaitingFor", ["PartitionId", "WaitingFor"], Filter: "[WaitingFor] IS NOT NULL"),
                ]),
            new("RecordIdentity",
                ["Token", "FlowId", "DeliveryKey"],
                ["PartitionId", "Token", "FlowId", "DeliveryKey"],
                [
                    new("IX_RecordIdentity_FlowId_DeliveryKey", ["FlowId", "DeliveryKey"]),
                    new("IX_RecordIdentity_FlowId_Token", ["FlowId", "Token"]),
                ],
                [
                    new("IX_RecordIdentity_PartitionId_FlowId_DeliveryKey", ["PartitionId", "FlowId", "DeliveryKey"]),
                    new("IX_RecordIdentity_PartitionId_FlowId_Token", ["PartitionId", "FlowId", "Token"]),
                ]),
            new("Attempt",
                ["AttemptId"],
                ["PartitionId", "AttemptId"],
                [
                    new("IX_Attempt_FlowId_DeliveryKey_StartedUtc", ["FlowId", "DeliveryKey", "StartedUtc"]),
                    new("IX_Attempt_RunId_FlowId_DeliveryKey", ["RunId", "FlowId", "DeliveryKey"]),
                    new("IX_Attempt_StartedUtc", ["StartedUtc"]),
                    new("IX_Attempt_SubmissionId_Outcome_Phase", ["SubmissionId", "Outcome", "Phase"], Include: ["DeliveryKey"]),
                ],
                [
                    new("IX_Attempt_PartitionId_FlowId_DeliveryKey_StartedUtc", ["PartitionId", "FlowId", "DeliveryKey", "StartedUtc"]),
                    new("IX_Attempt_RunId_PartitionId_FlowId_DeliveryKey", ["RunId", "PartitionId", "FlowId", "DeliveryKey"]),
                    new("IX_Attempt_StartedUtc", ["StartedUtc"]),
                    new("IX_Attempt_SubmissionId_Outcome_Phase", ["SubmissionId", "Outcome", "Phase"], Include: ["DeliveryKey"]),
                ]),
            new("WorkBatch",
                ["SubmissionId", "Index"],
                ["PartitionId", "SubmissionId", "Index"],
                [
                    new("IX_WorkBatch_FlowId_Status_CreatedUtc", ["FlowId", "Status", "CreatedUtc"]),
                    new("IX_WorkBatch_SubmissionId_Status", ["SubmissionId", "Status"]),
                ],
                [
                    new("IX_WorkBatch_PartitionId_FlowId_Status_CreatedUtc", ["PartitionId", "FlowId", "Status", "CreatedUtc"]),
                    new("IX_WorkBatch_SubmissionId_Index", ["SubmissionId", "Index"], Unique: true, Include: ["Status"]),
                ]),
            new("Lease",
                ["Token"],
                ["PartitionId", "Token"],
                [
                    new("IX_Lease_FlowId_ExpiresUtc", ["FlowId", "ExpiresUtc"]),
                    new("IX_Lease_SubmissionId_ExpiresUtc", ["SubmissionId", "ExpiresUtc"]),
                ],
                [
                    new("IX_Lease_Token", ["Token"], Unique: true),
                    new("IX_Lease_PartitionId_FlowId_ExpiresUtc", ["PartitionId", "FlowId", "ExpiresUtc"]),
                    new("IX_Lease_SubmissionId_ExpiresUtc", ["SubmissionId", "ExpiresUtc"]),
                ]),
            new("RecordEvent",
                ["EventId"],
                ["PartitionId", "EventId"],
                [
                    new("IX_RecordEvent_FlowId_AtUtc", ["FlowId", "AtUtc"], Include: ["LeaseToken"]),
                    new("IX_RecordEvent_LeaseToken_FlowId_DeliveryKey_EventId", ["LeaseToken", "FlowId", "DeliveryKey", "EventId"]),
                ],
                [
                    new("IX_RecordEvent_LeaseToken_FlowId_DeliveryKey_EventId", ["LeaseToken", "FlowId", "DeliveryKey", "EventId"]),
                    new("IX_RecordEvent_PartitionId_FlowId_AtUtc", ["PartitionId", "FlowId", "AtUtc"], Include: ["LeaseToken"]),
                ]),
            new("SourceWatermark",
                ["FlowId", "Scope"],
                ["PartitionId", "FlowId", "Scope"],
                [],
                []),
            new("Activity",
                ["ActivityId"],
                ["PartitionId", "ActivityId"],
                [
                    new("IX_Activity_Actor_StartedUtc", ["Actor", "StartedUtc"]),
                    new("IX_Activity_FlowId_DeliveryKey_StartedUtc", ["FlowId", "DeliveryKey", "StartedUtc"]),
                    new("IX_Activity_FlowId_StartedUtc", ["FlowId", "StartedUtc"]),
                    new("IX_Activity_Kind_StartedUtc", ["Kind", "StartedUtc"]),
                    new("IX_Activity_RunId", ["RunId"]),
                    new("IX_Activity_StartedUtc", ["StartedUtc"]),
                    new("IX_Activity_SubmissionId", ["SubmissionId"]),
                ],
                [
                    new("IX_Activity_ActivityId", ["ActivityId"], Unique: true),
                    new("IX_Activity_PartitionId_FlowId_StartedUtc", ["PartitionId", "FlowId", "StartedUtc"]),
                    new("IX_Activity_PartitionId_FlowId_DeliveryKey_StartedUtc", ["PartitionId", "FlowId", "DeliveryKey", "StartedUtc"]),
                    new("IX_Activity_SubmissionId", ["SubmissionId"]),
                    new("IX_Activity_RunId", ["RunId"]),
                    new("IX_Activity_PartitionId_Kind_StartedUtc", ["PartitionId", "Kind", "StartedUtc"]),
                    new("IX_Activity_PartitionId_Actor_StartedUtc", ["PartitionId", "Actor", "StartedUtc"]),
                    new("IX_Activity_PartitionId_StartedUtc", ["PartitionId", "StartedUtc"]),
                ]),
            new("Retrieval",
                ["RetrievalId"],
                ["PartitionId", "RetrievalId"],
                [
                    new("IX_Retrieval_FlowId_StartedUtc", ["FlowId", "StartedUtc"]),
                    new("IX_Retrieval_FlowId_Status_StartedUtc", ["FlowId", "Status", "StartedUtc"]),
                    new("IX_Retrieval_RunId", ["RunId"]),
                ],
                [
                    new("IX_Retrieval_RetrievalId", ["RetrievalId"], Unique: true),
                    new("IX_Retrieval_PartitionId_FlowId_StartedUtc", ["PartitionId", "FlowId", "StartedUtc"]),
                    new("IX_Retrieval_PartitionId_FlowId_Status_StartedUtc", ["PartitionId", "FlowId", "Status", "StartedUtc"]),
                    new("IX_Retrieval_RunId", ["RunId"]),
                ]),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The directory, filled while every table still has the indexes its reads use.
            migrationBuilder.CreateTable(
                name: "LedgerPartition",
                schema: Schema,
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LedgerPartition", x => x.PartitionId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerPartition_Name",
                schema: Schema,
                table: "LedgerPartition",
                column: "Name",
                unique: true);

            migrationBuilder.CreateTable(
                name: "Ledger",
                schema: Schema,
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    FlowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    FlowName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Interface = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LedgerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RegisteredUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Ledger", x => new { x.PartitionId, x.FlowId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Ledger_FlowId",
                schema: Schema,
                table: "Ledger",
                column: "FlowId",
                unique: true);

            migrationBuilder.Sql(FillDirectory);

            // Every nonclustered index goes before the keys change, and is built once after.
            foreach (var table in Tables)
            {
                foreach (var index in table.EarlierIndexes)
                {
                    migrationBuilder.DropIndex(name: index.Name, schema: Schema, table: table.Name);
                }
            }

            // Every row takes its ledger's partition, while the old key still holds. Every ledger identity a table carries is
            // in the directory, so a row left without one means the ledger changed under the migration.
            foreach (var table in Tables)
            {
                migrationBuilder.AddColumn<short>(name: "PartitionId", schema: Schema, table: table.Name, type: "smallint", nullable: true);
                migrationBuilder.Sql(
                    $"UPDATE t SET [PartitionId] = l.[PartitionId] FROM [osdu].[{table.Name}] AS t "
                    + "INNER JOIN [osdu].[Ledger] AS l ON l.[FlowId] = t.[FlowId];");
                migrationBuilder.Sql(
                    $"DECLARE @unplaced bigint = (SELECT COUNT_BIG(*) FROM [osdu].[{table.Name}] WHERE [PartitionId] IS NULL);\n"
                    + "IF @unplaced > 0\n"
                    + "BEGIN\n"
                    + $"    DECLARE @message nvarchar(2048) = CONCAT(@unplaced, N' row(s) of [osdu].[{table.Name}] belong to a ledger the directory does not hold, ', "
                    + "N'so no partition can be given to them: the ledger was written while the migration ran. Stop every host and migrate again.');\n"
                    + "    THROW 50000, @message, 1;\n"
                    + "END");
                migrationBuilder.AlterColumn<short>(
                    name: "PartitionId",
                    schema: Schema,
                    table: table.Name,
                    type: "smallint",
                    nullable: false,
                    oldClrType: typeof(short),
                    oldType: "smallint",
                    oldNullable: true);
            }

            foreach (var table in Tables)
            {
                migrationBuilder.DropPrimaryKey(name: $"PK_{table.Name}", schema: Schema, table: table.Name);
                migrationBuilder.AddPrimaryKey(name: $"PK_{table.Name}", schema: Schema, table: table.Name, columns: table.PartitionKey);
            }

            foreach (var table in Tables)
            {
                foreach (var index in table.PartitionIndexes)
                {
                    Create(migrationBuilder, table.Name, index);
                }
            }

            // A partition's cache changes of one status, newest first.
            migrationBuilder.CreateIndex(
                name: "IX_UpdateTag_Scope_Status",
                schema: Schema,
                table: "UpdateTag",
                columns: new[] { "Scope", "Status" });

            migrationBuilder.Sql(SequentialKeys);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A ledger identity names one partition, so the earlier keys hold every row the partition keys held.
            migrationBuilder.DropIndex(name: "IX_UpdateTag_Scope_Status", schema: Schema, table: "UpdateTag");

            foreach (var table in Tables)
            {
                foreach (var index in table.PartitionIndexes)
                {
                    migrationBuilder.DropIndex(name: index.Name, schema: Schema, table: table.Name);
                }
            }

            foreach (var table in Tables)
            {
                migrationBuilder.DropPrimaryKey(name: $"PK_{table.Name}", schema: Schema, table: table.Name);
                migrationBuilder.AddPrimaryKey(name: $"PK_{table.Name}", schema: Schema, table: table.Name, columns: table.EarlierKey);
                migrationBuilder.DropColumn(name: "PartitionId", schema: Schema, table: table.Name);
            }

            foreach (var table in Tables)
            {
                foreach (var index in table.EarlierIndexes)
                {
                    Create(migrationBuilder, table.Name, index);
                }
            }

            migrationBuilder.DropTable(name: "Ledger", schema: Schema);
            migrationBuilder.DropTable(name: "LedgerPartition", schema: Schema);

            migrationBuilder.Sql(SequentialKeys);
        }

        private static void Create(MigrationBuilder migrationBuilder, string table, TableIndex index)
        {
            var created = migrationBuilder.CreateIndex(
                name: index.Name,
                schema: Schema,
                table: table,
                columns: index.Columns,
                unique: index.Unique,
                filter: index.Filter);
            if (index.Include is { Length: > 0 } include)
            {
                created.Annotation("SqlServer:Include", include);
            }
        }
    }
}
