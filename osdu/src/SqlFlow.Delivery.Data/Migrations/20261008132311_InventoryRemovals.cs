using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class InventoryRemovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InventoryRemoval",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    InventoryRemovalId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    InventoryId = table.Column<int>(type: "int", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Actor = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Finding = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    NamesIds = table.Column<bool>(type: "bit", nullable: false),
                    Requested = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Removed = table.Column<long>(type: "bigint", nullable: false),
                    Gone = table.Column<long>(type: "bigint", nullable: false),
                    Skipped = table.Column<long>(type: "bigint", nullable: false),
                    Failed = table.Column<long>(type: "bigint", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ActivityId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryRemoval", x => new { x.PartitionId, x.InventoryRemovalId });
                });

            migrationBuilder.CreateTable(
                name: "InventoryRemovalItem",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    InventoryRemovalItemId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    InventoryRemovalId = table.Column<long>(type: "bigint", nullable: false),
                    InventoryRecordId = table.Column<long>(type: "bigint", nullable: false),
                    TargetId = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Version = table.Column<long>(type: "bigint", nullable: true),
                    Finding = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    LedgerFlowId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeliveryKey = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecordedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryRemovalItem", x => new { x.PartitionId, x.InventoryRemovalItemId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryRemoval_PartitionId_InventoryId_InventoryRemovalId",
                schema: "osdu",
                table: "InventoryRemoval",
                columns: new[] { "PartitionId", "InventoryId", "InventoryRemovalId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryRemovalItem_PartitionId_InventoryRemovalId_InventoryRemovalItemId",
                schema: "osdu",
                table: "InventoryRemovalItem",
                columns: new[] { "PartitionId", "InventoryRemovalId", "InventoryRemovalItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryRemovalItem_PartitionId_InventoryRemovalId_Outcome_InventoryRemovalItemId",
                schema: "osdu",
                table: "InventoryRemovalItem",
                columns: new[] { "PartitionId", "InventoryRemovalId", "Outcome", "InventoryRemovalItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryRemovalItem_PartitionId_TargetId",
                schema: "osdu",
                table: "InventoryRemovalItem",
                columns: new[] { "PartitionId", "TargetId" })
                .Annotation("SqlServer:Include", new[] { "InventoryRemovalId", "Outcome" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InventoryRemoval",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "InventoryRemovalItem",
                schema: "osdu");
        }
    }
}
