using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// Keys and values of dimensions (docs/dimension-plan.md, Keys and values): each original (a key, exactly as the index
    /// holds it) keeps the label read from the record it names (<c>Label</c>, <c>LabelFrom</c>) and its own search filter
    /// (<c>Filter</c>); a dimension keeps where its labels are read (<c>LabelJson</c>); a build counts the keys it labelled,
    /// those it could not, and the searches it asked (<c>Labelled</c>, <c>Unlabelled</c>, <c>LabelQueries</c>). Rows written
    /// before it hold no label and no filter until the dimension's next build.
    /// </summary>
    public partial class DimensionLabels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Filter",
                schema: "osdu",
                table: "DimensionValue",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Label",
                schema: "osdu",
                table: "DimensionValue",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LabelFrom",
                schema: "osdu",
                table: "DimensionValue",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LabelQueries",
                schema: "osdu",
                table: "DimensionRun",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "Labelled",
                schema: "osdu",
                table: "DimensionRun",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "Unlabelled",
                schema: "osdu",
                table: "DimensionRun",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "LabelJson",
                schema: "osdu",
                table: "Dimension",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Filter",
                schema: "osdu",
                table: "DimensionValue");

            migrationBuilder.DropColumn(
                name: "Label",
                schema: "osdu",
                table: "DimensionValue");

            migrationBuilder.DropColumn(
                name: "LabelFrom",
                schema: "osdu",
                table: "DimensionValue");

            migrationBuilder.DropColumn(
                name: "LabelQueries",
                schema: "osdu",
                table: "DimensionRun");

            migrationBuilder.DropColumn(
                name: "Labelled",
                schema: "osdu",
                table: "DimensionRun");

            migrationBuilder.DropColumn(
                name: "Unlabelled",
                schema: "osdu",
                table: "DimensionRun");

            migrationBuilder.DropColumn(
                name: "LabelJson",
                schema: "osdu",
                table: "Dimension");
        }
    }
}
