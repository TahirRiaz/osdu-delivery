using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// Separates the ledger per flow: a record is keyed by its flow and its delivery key, so several flows can read the same
    /// ingestion rows and each keeps its own records, attempts and activities. Every attempt is given the flow of its record,
    /// a record that queued or delivered a document claims its OSDU id (one OSDU record, one flow, enforced by a unique
    /// index), and an interrupted cache-change rollout keeps its place.
    /// <para>The migration is ordered for a ledger of hundreds of millions of records. Every backfill runs first, while the
    /// record table is still keyed by delivery key (so each join is a key match) and before the table is rebuilt (so the rows
    /// the backfills widen are packed again by the rebuild). The record table's nonclustered indexes are dropped before its
    /// clustered key changes and built once after, instead of being rebuilt by the key drop and again by the key build. The
    /// indexed statistics view is bound to the table's schema, so it goes before the key changes and comes back after. The
    /// attempt table's append-only indexes are set to optimize for sequential keys where the server supports it, because
    /// every drain appends to them at once.</para>
    /// </summary>
    public partial class LedgerPerFlow : Migration
    {
        private const string Schema = "osdu";

        private const string CreateRecordCountView =
            "CREATE VIEW [osdu].[RecordCount] WITH SCHEMABINDING AS "
            + "SELECT [FlowId], [Status], [LastVerifyOutcome], "
            + "DATEADD(hour, DATEDIFF(hour, CONVERT(datetime2(0), '20000101', 112), [LastDeliveredUtc]), CONVERT(datetime2(0), '20000101', 112)) AS [DeliveredHour], "
            + "COUNT_BIG(*) AS [Records] "
            + "FROM [osdu].[Record] "
            + "GROUP BY [FlowId], [Status], [LastVerifyOutcome], "
            + "DATEADD(hour, DATEDIFF(hour, CONVERT(datetime2(0), '20000101', 112), [LastDeliveredUtc]), CONVERT(datetime2(0), '20000101', 112))";

        private const string CreateRecordCountIndex =
            "CREATE UNIQUE CLUSTERED INDEX [IX_RecordCount] ON [osdu].[RecordCount] "
            + "([FlowId], [Status], [LastVerifyOutcome], [DeliveredHour])";

        /// <summary>
        /// Sets the sequential-key optimization on the attempt table's two ever-increasing indexes, where the server has it
        /// (SQL Server 2019 and later, Azure SQL). Concurrent drains append attempts at the same end of both.
        /// </summary>
        private const string SequentialAttemptKeys =
            "IF CAST(SERVERPROPERTY('ProductMajorVersion') AS int) >= 15 OR CAST(SERVERPROPERTY('EngineEdition') AS int) IN (5, 8)\n"
            + "BEGIN\n"
            + "    EXEC(N'ALTER INDEX [PK_Attempt] ON [osdu].[Attempt] SET (OPTIMIZE_FOR_SEQUENTIAL_KEY = {0})');\n"
            + "    EXEC(N'ALTER INDEX [IX_Attempt_StartedUtc] ON [osdu].[Attempt] SET (OPTIMIZE_FOR_SEQUENTIAL_KEY = {0})');\n"
            + "END";

        /// <summary>The record table's nonclustered indexes this migration leaves as they are, which it drops and builds again around the key change.</summary>
        private static readonly RecordIndex[] KeptRecordIndexes =
        [
            new("IX_Record_Label", ["Label"]),
            new("IX_Record_LeaseOwner", ["LeaseOwner"]),
            new("IX_Record_SourceFileName", ["SourceFileName"]),
            new("IX_Record_SourceKey", ["SourceKey"]),
            new("IX_Record_TargetId", ["TargetId"]),
            new("IX_Record_FlowId_Label", ["FlowId", "Label"]),
            new("IX_Record_FlowId_LastDeliveredUtc", ["FlowId", "LastDeliveredUtc"]),
            new("IX_Record_FlowId_LastVerifiedUtc", ["FlowId", "LastVerifiedUtc"]),
            new("IX_Record_FlowId_LastVerifyOutcome", ["FlowId", "LastVerifyOutcome"]),
            new("IX_Record_FlowId_PlanRequestedUtc", ["FlowId", "PlanRequestedUtc"], Filter: "[PlanRequestedUtc] IS NOT NULL"),
            new("IX_Record_FlowId_SourceKey", ["FlowId", "SourceKey"]),
            new("IX_Record_FlowId_TargetId", ["FlowId", "TargetId"]),
            new("IX_Record_FlowId_UpdatedUtc", ["FlowId", "UpdatedUtc"]),
            new("IX_Record_LastSubmissionId_WorkBatch", ["LastSubmissionId", "WorkBatch"]),
            new("IX_Record_Status_LeaseExpiresUtc", ["Status", "LeaseExpiresUtc"]),
            new("IX_Record_FlowId_LastSubmissionId_UpdatedUtc", ["FlowId", "LastSubmissionId", "UpdatedUtc"]),
            new("IX_Record_FlowId_SourceFileName_SourceRowNumber", ["FlowId", "SourceFileName", "SourceRowNumber"]),
            new("IX_Record_FlowId_Status_NextAttemptUtc", ["FlowId", "Status", "NextAttemptUtc"]),
            new("IX_Record_FlowId_Status_UpdatedUtc", ["FlowId", "Status", "UpdatedUtc"]),
        ];

        /// <summary>The record table's nonclustered indexes that only the earlier, delivery-keyed ledger has.</summary>
        private static readonly RecordIndex[] EarlierRecordIndexes =
        [
            new("IX_Record_CacheSetId_DeliveryKey", ["CacheSetId", "DeliveryKey"], Filter: "[CacheSetId] IS NOT NULL"),
            new("IX_Record_FlowId_DeliveryKey", ["FlowId", "DeliveryKey"]),
        ];

        /// <summary>The record table's nonclustered indexes that only the per-flow ledger has.</summary>
        private static readonly RecordIndex[] PerFlowRecordIndexes =
        [
            new("IX_Record_CacheSetId_DeliveryKey_FlowId", ["CacheSetId", "DeliveryKey", "FlowId"], Filter: "[CacheSetId] IS NOT NULL"),
            new("IX_Record_ClaimedTargetId", ["ClaimedTargetId"], Unique: true, Filter: "[ClaimedTargetId] IS NOT NULL"),
            new("IX_Record_DeliveryKey", ["DeliveryKey"]),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every attempt names its record's flow. Until now a delivery key named one record across every flow, and the
            // record table is still keyed by it here, so the join is a key match. Records are never deleted, so every
            // attempt has one; the submission is the fallback for a ledger that was edited by hand, and an attempt neither
            // places stops the migration rather than being given a made-up flow.
            migrationBuilder.AddColumn<Guid>(
                name: "FlowId",
                schema: Schema,
                table: "Attempt",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE a SET [FlowId] = r.[FlowId] "
                + "FROM [osdu].[Attempt] AS a INNER JOIN [osdu].[Record] AS r ON r.[DeliveryKey] = a.[DeliveryKey];");

            migrationBuilder.Sql(
                "UPDATE a SET [FlowId] = s.[FlowId] "
                + "FROM [osdu].[Attempt] AS a INNER JOIN [osdu].[Submission] AS s ON s.[SubmissionId] = a.[SubmissionId] "
                + "WHERE a.[FlowId] IS NULL;");

            migrationBuilder.Sql(
                "DECLARE @unplaced bigint = (SELECT COUNT_BIG(*) FROM [osdu].[Attempt] WHERE [FlowId] IS NULL);\n"
                + "IF @unplaced > 0\n"
                + "BEGIN\n"
                + "    DECLARE @message nvarchar(2048) = CONCAT(@unplaced, N' attempt(s) in [osdu].[Attempt] belong to no record and no submission, so no flow can be given to them. ', "
                + "N'List them with SELECT * FROM [osdu].[Attempt] WHERE [FlowId] IS NULL, and restore their records before migrating again.');\n"
                + "    THROW 50000, @message, 1;\n"
                + "END");

            migrationBuilder.AlterColumn<Guid>(
                name: "FlowId",
                schema: Schema,
                table: "Attempt",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            // A record that queued or delivered a document wrote, or was about to write, its OSDU id: that is its claim.
            // Two records holding one id cannot both keep it, and the migration says so before it changes anything more.
            migrationBuilder.AddColumn<string>(
                name: "ClaimedTargetId",
                schema: Schema,
                table: "Record",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.Sql(
                "DECLARE @shared bigint = (SELECT COUNT_BIG(*) FROM (SELECT [TargetId] COLLATE Latin1_General_100_BIN2 AS [TargetId] FROM [osdu].[Record] "
                + "WHERE [TargetId] IS NOT NULL AND ([PendingDocumentRef] IS NOT NULL OR [LastDeliveredUtc] IS NOT NULL) "
                + "GROUP BY [TargetId] COLLATE Latin1_General_100_BIN2 HAVING COUNT_BIG(*) > 1) AS d);\n"
                + "IF @shared > 0\n"
                + "BEGIN\n"
                + "    DECLARE @message nvarchar(2048) = CONCAT(@shared, N' OSDU id(s) are held by more than one record in [osdu].[Record], and each OSDU record belongs to one record. ', "
                + "N'Find them by grouping the delivered and queued records on [TargetId], and settle which record owns each id before migrating again.');\n"
                + "    THROW 50000, @message, 1;\n"
                + "END");

            migrationBuilder.Sql(
                "UPDATE [osdu].[Record] SET [ClaimedTargetId] = [TargetId] "
                + "WHERE [TargetId] IS NOT NULL AND ([PendingDocumentRef] IS NOT NULL OR [LastDeliveredUtc] IS NOT NULL);");

            // A rollout's cursor named a key, which named one record; its flow completes the cursor.
            migrationBuilder.AddColumn<Guid>(
                name: "CursorFlowId",
                schema: Schema,
                table: "UpdateTag",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE t SET [CursorFlowId] = r.[FlowId] "
                + "FROM [osdu].[UpdateTag] AS t INNER JOIN [osdu].[Record] AS r ON r.[DeliveryKey] = t.[Cursor] "
                + "WHERE t.[Cursor] IS NOT NULL;");

            // The record table's key changes: the view and every nonclustered index go first, so the table is rebuilt once.
            migrationBuilder.Sql("DROP VIEW [osdu].[RecordCount]");
            RemoveRecordIndexes(migrationBuilder, KeptRecordIndexes);
            RemoveRecordIndexes(migrationBuilder, EarlierRecordIndexes);

            migrationBuilder.DropPrimaryKey(
                name: "PK_Record",
                schema: Schema,
                table: "Record");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Record",
                schema: Schema,
                table: "Record",
                columns: new[] { "FlowId", "DeliveryKey" });

            CreateRecordIndexes(migrationBuilder, KeptRecordIndexes);
            CreateRecordIndexes(migrationBuilder, PerFlowRecordIndexes);

            // A record's attempts and interventions are found by its flow and key.
            migrationBuilder.DropIndex(
                name: "IX_Attempt_DeliveryKey_StartedUtc",
                schema: Schema,
                table: "Attempt");

            migrationBuilder.DropIndex(
                name: "IX_Attempt_RunId_DeliveryKey",
                schema: Schema,
                table: "Attempt");

            migrationBuilder.DropIndex(
                name: "IX_Attempt_SubmissionId",
                schema: Schema,
                table: "Attempt");

            migrationBuilder.DropIndex(
                name: "IX_Activity_DeliveryKey_StartedUtc",
                schema: Schema,
                table: "Activity");

            migrationBuilder.CreateIndex(
                name: "IX_Attempt_FlowId_DeliveryKey_StartedUtc",
                schema: Schema,
                table: "Attempt",
                columns: new[] { "FlowId", "DeliveryKey", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Attempt_RunId_FlowId_DeliveryKey",
                schema: Schema,
                table: "Attempt",
                columns: new[] { "RunId", "FlowId", "DeliveryKey" });

            migrationBuilder.CreateIndex(
                name: "IX_Attempt_SubmissionId_Outcome_Phase",
                schema: Schema,
                table: "Attempt",
                columns: new[] { "SubmissionId", "Outcome", "Phase" })
                .Annotation("SqlServer:Include", new[] { "DeliveryKey" });

            migrationBuilder.CreateIndex(
                name: "IX_Activity_FlowId_DeliveryKey_StartedUtc",
                schema: Schema,
                table: "Activity",
                columns: new[] { "FlowId", "DeliveryKey", "StartedUtc" });

            migrationBuilder.Sql(string.Format(System.Globalization.CultureInfo.InvariantCulture, SequentialAttemptKeys, "ON"));

            migrationBuilder.Sql(CreateRecordCountView);
            migrationBuilder.Sql(CreateRecordCountIndex);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // One row per delivery key cannot hold what several flows recorded for the same row: that ledger stays as it is.
            migrationBuilder.Sql(
                "DECLARE @shared bigint = (SELECT COUNT_BIG(*) FROM (SELECT [DeliveryKey] FROM [osdu].[Record] GROUP BY [DeliveryKey] HAVING COUNT_BIG(*) > 1) AS d);\n"
                + "IF @shared > 0\n"
                + "BEGIN\n"
                + "    DECLARE @message nvarchar(2048) = CONCAT(@shared, N' delivery key(s) have records in more than one flow, and the earlier ledger keeps one record per key. ', "
                + "N'The ledger cannot be taken back past LedgerPerFlow without losing one flow''s history.');\n"
                + "    THROW 50000, @message, 1;\n"
                + "END");

            migrationBuilder.Sql(string.Format(System.Globalization.CultureInfo.InvariantCulture, SequentialAttemptKeys, "OFF"));

            migrationBuilder.Sql("DROP VIEW [osdu].[RecordCount]");
            RemoveRecordIndexes(migrationBuilder, KeptRecordIndexes);
            RemoveRecordIndexes(migrationBuilder, PerFlowRecordIndexes);

            migrationBuilder.DropPrimaryKey(
                name: "PK_Record",
                schema: Schema,
                table: "Record");

            migrationBuilder.DropColumn(
                name: "ClaimedTargetId",
                schema: Schema,
                table: "Record");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Record",
                schema: Schema,
                table: "Record",
                column: "DeliveryKey");

            CreateRecordIndexes(migrationBuilder, KeptRecordIndexes);
            CreateRecordIndexes(migrationBuilder, EarlierRecordIndexes);

            migrationBuilder.DropIndex(
                name: "IX_Attempt_FlowId_DeliveryKey_StartedUtc",
                schema: Schema,
                table: "Attempt");

            migrationBuilder.DropIndex(
                name: "IX_Attempt_RunId_FlowId_DeliveryKey",
                schema: Schema,
                table: "Attempt");

            migrationBuilder.DropIndex(
                name: "IX_Attempt_SubmissionId_Outcome_Phase",
                schema: Schema,
                table: "Attempt");

            migrationBuilder.DropIndex(
                name: "IX_Activity_FlowId_DeliveryKey_StartedUtc",
                schema: Schema,
                table: "Activity");

            migrationBuilder.DropColumn(
                name: "FlowId",
                schema: Schema,
                table: "Attempt");

            migrationBuilder.DropColumn(
                name: "CursorFlowId",
                schema: Schema,
                table: "UpdateTag");

            migrationBuilder.CreateIndex(
                name: "IX_Attempt_DeliveryKey_StartedUtc",
                schema: Schema,
                table: "Attempt",
                columns: new[] { "DeliveryKey", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Attempt_RunId_DeliveryKey",
                schema: Schema,
                table: "Attempt",
                columns: new[] { "RunId", "DeliveryKey" });

            migrationBuilder.CreateIndex(
                name: "IX_Attempt_SubmissionId",
                schema: Schema,
                table: "Attempt",
                column: "SubmissionId");

            migrationBuilder.CreateIndex(
                name: "IX_Activity_DeliveryKey_StartedUtc",
                schema: Schema,
                table: "Activity",
                columns: new[] { "DeliveryKey", "StartedUtc" });

            migrationBuilder.Sql(CreateRecordCountView);
            migrationBuilder.Sql(CreateRecordCountIndex);
        }

        private static void RemoveRecordIndexes(MigrationBuilder migrationBuilder, RecordIndex[] indexes)
        {
            foreach (var index in indexes)
            {
                migrationBuilder.DropIndex(name: index.Name, schema: Schema, table: "Record");
            }
        }

        private static void CreateRecordIndexes(MigrationBuilder migrationBuilder, RecordIndex[] indexes)
        {
            foreach (var index in indexes)
            {
                migrationBuilder.CreateIndex(
                    name: index.Name,
                    schema: Schema,
                    table: "Record",
                    columns: index.Columns,
                    unique: index.Unique,
                    filter: index.Filter);
            }
        }

        /// <summary>One nonclustered index of the record table, as the model declares it.</summary>
        private sealed record RecordIndex(string Name, string[] Columns, bool Unique = false, string Filter = null);
    }
}
