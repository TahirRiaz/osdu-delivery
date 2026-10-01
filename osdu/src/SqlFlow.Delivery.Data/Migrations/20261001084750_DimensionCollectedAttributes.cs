using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// Collected attributes (docs/dimension-plan.md, Collected attributes): a key holds several values of one attribute, so
    /// <c>DimensionAttribute</c> is keyed by value as well, and a collected value says how many of the key's records hold it
    /// (<c>Records</c>). A dimension keeps what its last build collected (<c>CollectedJson</c>): each attribute's field, and
    /// the texts each value stands for, which a search picking it asks for. Existing rows keep their values, one per key and
    /// attribute, and need no rebuild.
    /// </summary>
    public partial class DimensionCollectedAttributes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_DimensionAttribute",
                schema: "osdu",
                table: "DimensionAttribute");

            migrationBuilder.AddColumn<long>(
                name: "Records",
                schema: "osdu",
                table: "DimensionAttribute",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CollectedJson",
                schema: "osdu",
                table: "Dimension",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_DimensionAttribute",
                schema: "osdu",
                table: "DimensionAttribute",
                columns: new[] { "PartitionId", "DimensionId", "ValueId", "Name", "Value" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_DimensionAttribute",
                schema: "osdu",
                table: "DimensionAttribute");

            // The earlier key holds one value per key and attribute: the collected values go, and any other second value with them.
            migrationBuilder.Sql("""
                DELETE FROM [osdu].[DimensionAttribute] WHERE [Records] IS NOT NULL;
                WITH [ranked] AS (
                    SELECT ROW_NUMBER() OVER (PARTITION BY [PartitionId], [DimensionId], [ValueId], [Name] ORDER BY [Value]) AS [n]
                    FROM [osdu].[DimensionAttribute])
                DELETE FROM [ranked] WHERE [n] > 1;
                """);

            migrationBuilder.DropColumn(
                name: "Records",
                schema: "osdu",
                table: "DimensionAttribute");

            migrationBuilder.DropColumn(
                name: "CollectedJson",
                schema: "osdu",
                table: "Dimension");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DimensionAttribute",
                schema: "osdu",
                table: "DimensionAttribute",
                columns: new[] { "PartitionId", "DimensionId", "ValueId", "Name" });
        }
    }
}
