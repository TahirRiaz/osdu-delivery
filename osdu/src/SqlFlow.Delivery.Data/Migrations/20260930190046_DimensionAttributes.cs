using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// Attributes of dimension keys (docs/dimension-plan.md, Attributes): a dimension keeps the attributes it reads
    /// (<c>AttributesJson</c>), and <c>DimensionAttribute</c> holds one row per key and attribute, the value read from the
    /// record the key names and the record it came from, indexed by attribute and value so the keys an attribute value
    /// holds, and an attribute's values, are one seek. A dimension built before it has no attributes until its declaration
    /// names some and it is built again.
    /// </summary>
    public partial class DimensionAttributes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AttributesJson",
                schema: "osdu",
                table: "Dimension",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DimensionAttribute",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    DimensionId = table.Column<int>(type: "int", nullable: false),
                    ValueId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Value = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ValueFrom = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DimensionAttribute", x => new { x.PartitionId, x.DimensionId, x.ValueId, x.Name });
                });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionAttribute_PartitionId_DimensionId_Name_Value",
                schema: "osdu",
                table: "DimensionAttribute",
                columns: new[] { "PartitionId", "DimensionId", "Name", "Value" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DimensionAttribute",
                schema: "osdu");

            migrationBuilder.DropColumn(
                name: "AttributesJson",
                schema: "osdu",
                table: "Dimension");
        }
    }
}
