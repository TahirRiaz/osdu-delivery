using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class SearchTerms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SearchTerm",
                schema: "osdu",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TermId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TermKey = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: false),
                    System = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Dataset = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Column = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    RoutesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FlowsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SearchTerm", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SearchTermRefinement",
                schema: "osdu",
                columns: table => new
                {
                    TermId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TermKey = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Excluded = table.Column<bool>(type: "bit", nullable: false),
                    Route = table.Column<string>(type: "nvarchar(1100)", maxLength: 1100, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SearchTermRefinement", x => x.TermId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SearchTerm_EntityType_TermId",
                schema: "osdu",
                table: "SearchTerm",
                columns: new[] { "EntityType", "TermId" });

            migrationBuilder.CreateIndex(
                name: "IX_SearchTerm_RepoId_TermId",
                schema: "osdu",
                table: "SearchTerm",
                columns: new[] { "RepoId", "TermId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SearchTermRefinement_EntityType_Name",
                schema: "osdu",
                table: "SearchTermRefinement",
                columns: new[] { "EntityType", "Name" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SearchTerm",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "SearchTermRefinement",
                schema: "osdu");
        }
    }
}
