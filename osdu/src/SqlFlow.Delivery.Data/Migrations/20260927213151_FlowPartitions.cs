using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class FlowPartitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Interface_RepoId_FlowName_Interface",
                schema: "osdu",
                table: "Interface");

            migrationBuilder.DropIndex(
                name: "IX_ConfigProperty_RepoId_Name",
                schema: "osdu",
                table: "ConfigProperty");

            migrationBuilder.DropIndex(
                name: "IX_CacheDefinition_RepoId_FlowName_Name",
                schema: "osdu",
                table: "CacheDefinition");

            migrationBuilder.AddColumn<string>(
                name: "Partition",
                schema: "osdu",
                table: "Interface",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Partition",
                schema: "osdu",
                table: "ConfigProperty",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DeclaresPartitions",
                schema: "osdu",
                table: "CacheDefinition",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Interface_RepoId_FlowName_Interface_Partition",
                schema: "osdu",
                table: "Interface",
                columns: new[] { "RepoId", "FlowName", "Interface", "Partition" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConfigProperty_RepoId_Partition_Name",
                schema: "osdu",
                table: "ConfigProperty",
                columns: new[] { "RepoId", "Partition", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CacheDefinition_RepoId_FlowName_Name_Scope",
                schema: "osdu",
                table: "CacheDefinition",
                columns: new[] { "RepoId", "FlowName", "Name", "Scope" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The schema before partitions keeps one row per interface, per name and scope, and per flow and type: the rows a
            // flow that names its partitions wrote for them cannot be held there, and would refuse the unique indexes restored
            // below. They are read models and configuration the next sync and 'sqlflow config set' write again.
            migrationBuilder.Sql("DELETE FROM [osdu].[Interface] WHERE [Partition] <> N'';");
            migrationBuilder.Sql("DELETE FROM [osdu].[ConfigProperty] WHERE [Partition] IS NOT NULL;");
            migrationBuilder.Sql("DELETE FROM [osdu].[CacheDefinition] WHERE [DeclaresPartitions] = 1;");

            migrationBuilder.DropIndex(
                name: "IX_Interface_RepoId_FlowName_Interface_Partition",
                schema: "osdu",
                table: "Interface");

            migrationBuilder.DropIndex(
                name: "IX_ConfigProperty_RepoId_Partition_Name",
                schema: "osdu",
                table: "ConfigProperty");

            migrationBuilder.DropIndex(
                name: "IX_CacheDefinition_RepoId_FlowName_Name_Scope",
                schema: "osdu",
                table: "CacheDefinition");

            migrationBuilder.DropColumn(
                name: "Partition",
                schema: "osdu",
                table: "Interface");

            migrationBuilder.DropColumn(
                name: "Partition",
                schema: "osdu",
                table: "ConfigProperty");

            migrationBuilder.DropColumn(
                name: "DeclaresPartitions",
                schema: "osdu",
                table: "CacheDefinition");

            migrationBuilder.CreateIndex(
                name: "IX_Interface_RepoId_FlowName_Interface",
                schema: "osdu",
                table: "Interface",
                columns: new[] { "RepoId", "FlowName", "Interface" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConfigProperty_RepoId_Name",
                schema: "osdu",
                table: "ConfigProperty",
                columns: new[] { "RepoId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CacheDefinition_RepoId_FlowName_Name",
                schema: "osdu",
                table: "CacheDefinition",
                columns: new[] { "RepoId", "FlowName", "Name" },
                unique: true);
        }
    }
}
