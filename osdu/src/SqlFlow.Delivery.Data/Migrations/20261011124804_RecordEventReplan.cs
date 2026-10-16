using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// Whether a worker's completion asks for its record to be planned again (<c>RecordEvent.Replan</c>): the try found the
    /// record's queued document gone (its work batch pruned or rewritten), so the record drops it and the next run plans it
    /// from its row, instead of a hold that a release would only answer by sending the missing document again.
    /// </summary>
    public partial class RecordEventReplan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Replan",
                schema: "osdu",
                table: "RecordEvent",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Replan",
                schema: "osdu",
                table: "RecordEvent");
        }
    }
}
