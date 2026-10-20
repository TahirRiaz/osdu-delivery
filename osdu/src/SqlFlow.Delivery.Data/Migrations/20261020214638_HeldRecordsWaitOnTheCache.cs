using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class HeldRecordsWaitOnTheCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "HeldCacheSetId",
                schema: "osdu",
                table: "Record",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Record_HeldCacheSetId_Status",
                schema: "osdu",
                table: "Record",
                columns: new[] { "HeldCacheSetId", "Status" },
                filter: "[HeldCacheSetId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Record_HeldCacheSetId_Status",
                schema: "osdu",
                table: "Record");

            migrationBuilder.DropColumn(
                name: "HeldCacheSetId",
                schema: "osdu",
                table: "Record");
        }
    }
}
