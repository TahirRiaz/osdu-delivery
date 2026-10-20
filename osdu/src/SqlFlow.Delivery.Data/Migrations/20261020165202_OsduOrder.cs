using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class OsduOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TargetPartition",
                schema: "osdu",
                table: "Interface",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "SchemaLineage",
                schema: "osdu",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Release = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Commit = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PublishedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    BuiltUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    BuiltBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kinds = table.Column<int>(type: "int", nullable: false),
                    Relationships = table.Column<int>(type: "int", nullable: false),
                    FailuresJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchemaLineage", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SchemaLineageKind",
                schema: "osdu",
                columns: table => new
                {
                    LineageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Version = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    RelationshipsJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchemaLineageKind", x => new { x.LineageId, x.Kind });
                });

            migrationBuilder.CreateIndex(
                name: "IX_SchemaLineage_Release",
                schema: "osdu",
                table: "SchemaLineage",
                column: "Release",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SchemaLineageKind_Kind",
                schema: "osdu",
                table: "SchemaLineageKind",
                column: "Kind");

            migrationBuilder.CreateIndex(
                name: "IX_SchemaLineageKind_LineageId_EntityType",
                schema: "osdu",
                table: "SchemaLineageKind",
                columns: new[] { "LineageId", "EntityType" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SchemaLineage",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "SchemaLineageKind",
                schema: "osdu");

            migrationBuilder.DropColumn(
                name: "TargetPartition",
                schema: "osdu",
                table: "Interface");
        }
    }
}
