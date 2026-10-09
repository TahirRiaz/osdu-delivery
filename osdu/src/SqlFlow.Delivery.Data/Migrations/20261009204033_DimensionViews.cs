using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// The views dimension flows declare over their tables (osdu/docs/dimension-plan.md, Views): <c>DimensionView</c> records
    /// each view a flow made in the schema, with the statement it was written by, so a build writes only views it recorded
    /// and drops only those its flow no longer declares; <c>DimensionViewCheck</c> keeps what each build's check of a view
    /// found in a partition. The views themselves, like the dimension tables they read, are written by builds, not here.
    /// </summary>
    public partial class DimensionViews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DimensionView",
                schema: "osdu",
                columns: table => new
                {
                    ViewId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ViewName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    FlowName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    LedgerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DeclarationJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Sql = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SqlHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    TablesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ColumnsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    WrittenRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WrittenBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    WrittenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DimensionView", x => x.ViewId);
                });

            migrationBuilder.CreateTable(
                name: "DimensionViewCheck",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    CheckId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ViewId = table.Column<int>(type: "int", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Rows = table.Column<long>(type: "bigint", nullable: false),
                    JoinsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ColumnsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CheckedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DurationMs = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DimensionViewCheck", x => new { x.PartitionId, x.CheckId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionView_FlowName",
                schema: "osdu",
                table: "DimensionView",
                column: "FlowName");

            migrationBuilder.CreateIndex(
                name: "IX_DimensionView_Name",
                schema: "osdu",
                table: "DimensionView",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionView_ViewName",
                schema: "osdu",
                table: "DimensionView",
                column: "ViewName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionViewCheck_CheckId",
                schema: "osdu",
                table: "DimensionViewCheck",
                column: "CheckId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionViewCheck_ViewId_PartitionId_CheckedUtc",
                schema: "osdu",
                table: "DimensionViewCheck",
                columns: new[] { "ViewId", "PartitionId", "CheckedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DimensionView",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "DimensionViewCheck",
                schema: "osdu");
        }
    }
}
