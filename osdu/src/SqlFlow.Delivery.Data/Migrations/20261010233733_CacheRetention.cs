using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// The partition cache's retention (osdu/docs/reference/concepts/partition-cache.md, Retention): a cache flow's declared
    /// retention (<c>CacheDefinition.RetentionDays</c>, 7 for every flow synced before it was declared), when a version's
    /// records were pruned (<c>CacheVersion.PrunedUtc</c>) and what it changed, recorded before they went
    /// (<c>CacheVersion.ChangesJson</c>), the cache version each delivery interface renders against
    /// (<c>Interface.CacheVersion</c>, null until the next repository sync records it, and no partition a delivery flow may
    /// read is pruned before then), and the index the change counts are read through.
    /// </summary>
    public partial class CacheRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CacheVersion",
                schema: "osdu",
                table: "Interface",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ChangesJson",
                schema: "osdu",
                table: "CacheVersion",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PrunedUtc",
                schema: "osdu",
                table: "CacheVersion",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetentionDays",
                schema: "osdu",
                table: "CacheDefinition",
                type: "int",
                nullable: false,
                defaultValue: 7);

            migrationBuilder.CreateIndex(
                name: "IX_CacheItem_Scope_FromSequence",
                schema: "osdu",
                table: "CacheItem",
                columns: new[] { "Scope", "FromSequence" })
                .Annotation("SqlServer:Include", new[] { "TypeName", "RecordId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CacheItem_Scope_FromSequence",
                schema: "osdu",
                table: "CacheItem");

            migrationBuilder.DropColumn(
                name: "CacheVersion",
                schema: "osdu",
                table: "Interface");

            migrationBuilder.DropColumn(
                name: "ChangesJson",
                schema: "osdu",
                table: "CacheVersion");

            migrationBuilder.DropColumn(
                name: "PrunedUtc",
                schema: "osdu",
                table: "CacheVersion");

            migrationBuilder.DropColumn(
                name: "RetentionDays",
                schema: "osdu",
                table: "CacheDefinition");
        }
    }
}
