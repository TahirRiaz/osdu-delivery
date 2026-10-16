using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class LookupCacheTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Kind",
                schema: "osdu",
                table: "CacheDefinition",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(400)",
                oldMaxLength: 400);

            migrationBuilder.AlterColumn<string>(
                name: "Endpoint",
                schema: "osdu",
                table: "CacheDefinition",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(1000)",
                oldMaxLength: 1000);

            migrationBuilder.AddColumn<string>(
                name: "Connection",
                schema: "osdu",
                table: "CacheDefinition",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DictionaryPath",
                schema: "osdu",
                table: "CacheDefinition",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KeyField",
                schema: "osdu",
                table: "CacheDefinition",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Origin",
                schema: "osdu",
                table: "CacheDefinition",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "osdu");

            migrationBuilder.AddColumn<string>(
                name: "SourceObject",
                schema: "osdu",
                table: "CacheDefinition",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Connection",
                schema: "osdu",
                table: "CacheDefinition");

            migrationBuilder.DropColumn(
                name: "DictionaryPath",
                schema: "osdu",
                table: "CacheDefinition");

            migrationBuilder.DropColumn(
                name: "KeyField",
                schema: "osdu",
                table: "CacheDefinition");

            migrationBuilder.DropColumn(
                name: "Origin",
                schema: "osdu",
                table: "CacheDefinition");

            migrationBuilder.DropColumn(
                name: "SourceObject",
                schema: "osdu",
                table: "CacheDefinition");

            migrationBuilder.AlterColumn<string>(
                name: "Kind",
                schema: "osdu",
                table: "CacheDefinition",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(400)",
                oldMaxLength: 400,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Endpoint",
                schema: "osdu",
                table: "CacheDefinition",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(1000)",
                oldMaxLength: 1000,
                oldNullable: true);
        }
    }
}
