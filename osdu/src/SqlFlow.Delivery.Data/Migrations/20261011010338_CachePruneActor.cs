using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// Who pruned a version of a partition's cache (<c>CacheVersion.PrunedBy</c>): the requester of the refresh or the
    /// account of the import whose retention pruned it, or the operator who purged the cache's history from the Cache page,
    /// the API or <c>sqlflow cache prune</c>. Null for a version pruned before this was recorded.
    /// </summary>
    public partial class CachePruneActor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PrunedBy",
                schema: "osdu",
                table: "CacheVersion",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PrunedBy",
                schema: "osdu",
                table: "CacheVersion");
        }
    }
}
