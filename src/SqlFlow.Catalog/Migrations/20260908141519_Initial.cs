using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Retrieval",
                schema: "delivery",
                columns: table => new
                {
                    RetrievalId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FlowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FlowName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Actor = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kinds = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Query = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    WindowField = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    WindowFrom = table.Column<DateTime>(type: "datetime2", nullable: true),
                    WindowTo = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Location = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ManifestLocation = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Records = table.Column<long>(type: "bigint", nullable: false),
                    Files = table.Column<int>(type: "int", nullable: false),
                    Bytes = table.Column<long>(type: "bigint", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Retrieval", x => x.RetrievalId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Retrieval_FlowId_StartedUtc",
                schema: "delivery",
                table: "Retrieval",
                columns: new[] { "FlowId", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Retrieval_FlowId_Status_StartedUtc",
                schema: "delivery",
                table: "Retrieval",
                columns: new[] { "FlowId", "Status", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Retrieval_RunId",
                schema: "delivery",
                table: "Retrieval",
                column: "RunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Retrieval",
                schema: "delivery");
        }
    }
}
