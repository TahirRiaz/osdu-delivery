using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class CacheStepsOnPipelines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KeepCurrent",
                schema: "osdu",
                table: "CacheDefinition");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "KeepCurrent",
                schema: "osdu",
                table: "CacheDefinition",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }
    }
}
