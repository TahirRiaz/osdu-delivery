using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// Keeps the reversals of runs and submissions (docs/reversal-plan.md).
    /// <list type="bullet">
    /// <item><c>osdu.Reversal</c>: one row per source (a run or a submission) of a ledger, unique on the partition, the
    /// ledger and the source, with the submissions it covers, its state and the latest run that worked on it.</item>
    /// <item><c>osdu.ReversalItem</c>: one row per record the source delivered, keyed by the reversal and the delivery key:
    /// what the source wrote of it, what OSDU held before, and what the reversal did. Indexed on
    /// <c>(PartitionId, ReversalId, State, DeliveryKey) INCLUDE (Outcome)</c> for the next page to settle and the counts,
    /// and on <c>(PartitionId, ReversalId, Outcome, DeliveryKey)</c> for its records by outcome.</item>
    /// </list>
    /// The custody state <c>reverted</c> and the attempt outcome <c>restored</c> are values of existing columns, so no other
    /// table changes. Both tables are created empty; going back down drops them.
    /// </summary>
    public partial class RecordReversals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Reversal",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    ReversalId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FlowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FlowName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceKind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubmissionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    RequestedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RequestedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CapturedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reversal", x => new { x.PartitionId, x.ReversalId });
                });

            migrationBuilder.CreateTable(
                name: "ReversalItem",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    ReversalId = table.Column<long>(type: "bigint", nullable: false),
                    DeliveryKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetId = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true, collation: "Latin1_General_100_BIN2"),
                    FirstAttemptId = table.Column<long>(type: "bigint", nullable: false),
                    FirstVersion = table.Column<long>(type: "bigint", nullable: true),
                    RunVersion = table.Column<long>(type: "bigint", nullable: true),
                    Prior = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    PriorVersion = table.Column<long>(type: "bigint", nullable: true),
                    PriorAttemptId = table.Column<long>(type: "bigint", nullable: true),
                    State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RestoredVersion = table.Column<long>(type: "bigint", nullable: true),
                    NewVersion = table.Column<long>(type: "bigint", nullable: true),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReversalItem", x => new { x.PartitionId, x.ReversalId, x.DeliveryKey });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Reversal_PartitionId_FlowId_RequestedUtc",
                schema: "osdu",
                table: "Reversal",
                columns: new[] { "PartitionId", "FlowId", "RequestedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Reversal_PartitionId_FlowId_SourceKind_SourceId",
                schema: "osdu",
                table: "Reversal",
                columns: new[] { "PartitionId", "FlowId", "SourceKind", "SourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Reversal_ReversalId",
                schema: "osdu",
                table: "Reversal",
                column: "ReversalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReversalItem_PartitionId_ReversalId_Outcome_DeliveryKey",
                schema: "osdu",
                table: "ReversalItem",
                columns: new[] { "PartitionId", "ReversalId", "Outcome", "DeliveryKey" });

            migrationBuilder.CreateIndex(
                name: "IX_ReversalItem_PartitionId_ReversalId_State_DeliveryKey",
                schema: "osdu",
                table: "ReversalItem",
                columns: new[] { "PartitionId", "ReversalId", "State", "DeliveryKey" })
                .Annotation("SqlServer:Include", new[] { "Outcome" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Reversal",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "ReversalItem",
                schema: "osdu");
        }
    }
}
