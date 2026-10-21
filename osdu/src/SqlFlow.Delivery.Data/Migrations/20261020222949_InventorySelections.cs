using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class InventorySelections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "InventorySelectionId",
                schema: "osdu",
                table: "InventoryRemoval",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "InventorySelection",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    InventorySelectionId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    InventoryId = table.Column<int>(type: "int", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Actor = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ConditionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ConditionsKey = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    FiltersJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Query = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Direction = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    NotesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ColumnsJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    BuildRunId = table.Column<long>(type: "bigint", nullable: true),
                    Candidates = table.Column<long>(type: "bigint", nullable: false),
                    ToCheck = table.Column<long>(type: "bigint", nullable: false),
                    Checked = table.Column<long>(type: "bigint", nullable: false),
                    Matched = table.Column<long>(type: "bigint", nullable: false),
                    Outside = table.Column<long>(type: "bigint", nullable: false),
                    Changed = table.Column<long>(type: "bigint", nullable: false),
                    Requests = table.Column<long>(type: "bigint", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventorySelection", x => new { x.PartitionId, x.InventorySelectionId });
                });

            migrationBuilder.CreateTable(
                name: "InventorySelectionItem",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    InventorySelectionId = table.Column<long>(type: "bigint", nullable: false),
                    InventoryRecordId = table.Column<long>(type: "bigint", nullable: false),
                    MatchedVersion = table.Column<long>(type: "bigint", nullable: true),
                    ValuesJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventorySelectionItem", x => new { x.PartitionId, x.InventorySelectionId, x.InventoryRecordId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventorySelection_PartitionId_CreatedUtc",
                schema: "osdu",
                table: "InventorySelection",
                columns: new[] { "PartitionId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_InventorySelection_PartitionId_InventoryId_ConditionsKey_InventorySelectionId",
                schema: "osdu",
                table: "InventorySelection",
                columns: new[] { "PartitionId", "InventoryId", "ConditionsKey", "InventorySelectionId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InventorySelection",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "InventorySelectionItem",
                schema: "osdu");

            migrationBuilder.DropColumn(
                name: "InventorySelectionId",
                schema: "osdu",
                table: "InventoryRemoval");
        }
    }
}
