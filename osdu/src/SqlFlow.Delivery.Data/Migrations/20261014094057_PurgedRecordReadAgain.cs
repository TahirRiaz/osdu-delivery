using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class PurgedRecordReadAgain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PlanRequestedUtc",
                schema: "osdu",
                table: "PurgedRecord",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceKeyJson",
                schema: "osdu",
                table: "PurgedRecord",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurgedRecord_PartitionId_FlowId_PlanRequestedUtc_DeliveryKey",
                schema: "osdu",
                table: "PurgedRecord",
                columns: new[] { "PartitionId", "FlowId", "PlanRequestedUtc", "DeliveryKey" },
                filter: "[PlanRequestedUtc] IS NOT NULL")
                .Annotation("SqlServer:Include", new[] { "SourceKeyJson" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PurgedRecord_PartitionId_FlowId_PlanRequestedUtc_DeliveryKey",
                schema: "osdu",
                table: "PurgedRecord");

            migrationBuilder.DropColumn(
                name: "PlanRequestedUtc",
                schema: "osdu",
                table: "PurgedRecord");

            migrationBuilder.DropColumn(
                name: "SourceKeyJson",
                schema: "osdu",
                table: "PurgedRecord");
        }
    }
}
