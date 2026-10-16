using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// Groups blocked records by the problem that keeps them so (osdu/docs/reference/concepts/record-lifecycle.md,
    /// Issues), and names every record a release reached.
    /// <list type="bullet">
    /// <item><c>Record.ProblemHash</c>: the hash of a blocked, held or failed record's error with every part that names the
    /// record replaced, which records refused for the same reason share; null for every other record. A filtered index on
    /// <c>(PartitionId, FlowId, ProblemHash, UpdatedUtc)</c>, including the status and the file, counts a flow's problems,
    /// lists a problem's records newest first and releases them, and holds the blocked records alone.</item>
    /// <item><c>IX_Record_Unsorted</c>: the blocked, held or failed records with no problem yet, which the control plane's
    /// backfill (<c>RecordProblemBackfillService</c>) reads and sorts a page at a time. The pattern is decided in code, so
    /// no statement here computes it: the index is built over every record blocked now, and empties as they are sorted.</item>
    /// <item><c>RecordEvent.ProblemHash</c>: the problem a worker's completion holds or fails its record with, which the lease
    /// applies with the rest of the completion.</item>
    /// <item><c>osdu.ActivityRecord</c>: one row per record a release changed, keyed by the record and then the activity, so
    /// a record's history holds the releases that reached it however many records they reached.</item>
    /// </list>
    /// Every column is added empty, so the record table is not rewritten; the two indexes are built over it, the second
    /// holding only what is blocked. Going back down drops the table, the indexes and both columns.
    /// </summary>
    public partial class RecordProblems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ProblemHash",
                schema: "osdu",
                table: "RecordEvent",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ProblemHash",
                schema: "osdu",
                table: "Record",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ActivityRecord",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    FlowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeliveryKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActivityId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityRecord", x => new { x.PartitionId, x.FlowId, x.DeliveryKey, x.ActivityId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Record_PartitionId_FlowId_ProblemHash_UpdatedUtc",
                schema: "osdu",
                table: "Record",
                columns: new[] { "PartitionId", "FlowId", "ProblemHash", "UpdatedUtc" },
                filter: "[ProblemHash] IS NOT NULL")
                .Annotation("SqlServer:Include", new[] { "Status", "PendingSourceFileName" });

            migrationBuilder.CreateIndex(
                name: "IX_Record_Unsorted",
                schema: "osdu",
                table: "Record",
                columns: new[] { "PartitionId", "FlowId" },
                filter: "[ProblemHash] IS NULL AND [Blocked]=(1) AND ([Status] IN (N'held', N'failed'))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActivityRecord",
                schema: "osdu");

            migrationBuilder.DropIndex(
                name: "IX_Record_PartitionId_FlowId_ProblemHash_UpdatedUtc",
                schema: "osdu",
                table: "Record");

            migrationBuilder.DropIndex(
                name: "IX_Record_Unsorted",
                schema: "osdu",
                table: "Record");

            migrationBuilder.DropColumn(
                name: "ProblemHash",
                schema: "osdu",
                table: "RecordEvent");

            migrationBuilder.DropColumn(
                name: "ProblemHash",
                schema: "osdu",
                table: "Record");
        }
    }
}
