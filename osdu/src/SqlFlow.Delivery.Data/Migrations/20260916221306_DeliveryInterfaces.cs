using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class DeliveryInterfaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Interface",
                schema: "osdu",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FlowName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Interface = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    LedgerFlowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LedgerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Route = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    RouteReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    MappingReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RecordObject = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    AfterJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    RelativePath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Active = table.Column<bool>(type: "bit", nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Interface", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Interface_LedgerFlowId",
                schema: "osdu",
                table: "Interface",
                column: "LedgerFlowId");

            migrationBuilder.CreateIndex(
                name: "IX_Interface_RepoId_FlowName_Interface",
                schema: "osdu",
                table: "Interface",
                columns: new[] { "RepoId", "FlowName", "Interface" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Interface",
                schema: "osdu");
        }
    }
}
