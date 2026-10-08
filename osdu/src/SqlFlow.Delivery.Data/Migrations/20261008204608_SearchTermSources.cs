using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// Search terms keyed by the source table and column a delivery flow reads (osdu/docs/search-terms.md), one row per term,
    /// repository and entity type, in place of a key of the mapping's source system and dataset. The rows are a read model
    /// the control plane writes again from the pipelines when it starts, so they are emptied here rather than converted;
    /// what people made of the terms (osdu.SearchTermRefinement) is kept, and moved to the terms' new keys as they are written.
    /// </summary>
    public partial class SearchTermSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM [osdu].[SearchTerm];");

            migrationBuilder.DropIndex(
                name: "IX_SearchTerm_RepoId_TermId",
                schema: "osdu",
                table: "SearchTerm");

            migrationBuilder.DropColumn(
                name: "Dataset",
                schema: "osdu",
                table: "SearchTerm");

            migrationBuilder.DropColumn(
                name: "System",
                schema: "osdu",
                table: "SearchTerm");

            migrationBuilder.AddColumn<string>(
                name: "Source",
                schema: "osdu",
                table: "SearchTerm",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_SearchTerm_RepoId_TermId_EntityType",
                schema: "osdu",
                table: "SearchTerm",
                columns: new[] { "RepoId", "TermId", "EntityType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The rows are a read model of the other key; the earlier version writes them again when it starts.
            migrationBuilder.Sql("DELETE FROM [osdu].[SearchTerm];");

            migrationBuilder.DropIndex(
                name: "IX_SearchTerm_RepoId_TermId_EntityType",
                schema: "osdu",
                table: "SearchTerm");

            migrationBuilder.DropColumn(
                name: "Source",
                schema: "osdu",
                table: "SearchTerm");

            migrationBuilder.AddColumn<string>(
                name: "Dataset",
                schema: "osdu",
                table: "SearchTerm",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "System",
                schema: "osdu",
                table: "SearchTerm",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_SearchTerm_RepoId_TermId",
                schema: "osdu",
                table: "SearchTerm",
                columns: new[] { "RepoId", "TermId" },
                unique: true);
        }
    }
}
