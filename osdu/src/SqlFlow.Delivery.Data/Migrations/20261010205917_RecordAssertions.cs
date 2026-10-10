using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// The assertions of a mapping (osdu/docs/reference/flow/mapping-assertions.md): a record keeps how many judgements of
    /// its mapping's assertions failed on the last document the check before sending read (<c>AssertionFailures</c>, null
    /// when the mapping states none), written through the record's events like the rest of the verdict, and indexed by flow
    /// for the records that failed one.
    /// </summary>
    public partial class RecordAssertions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "AssertionFailures",
                schema: "osdu",
                table: "RecordEvent",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AssertionFailures",
                schema: "osdu",
                table: "Record",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Record_PartitionId_FlowId_AssertionFailures_UpdatedUtc",
                schema: "osdu",
                table: "Record",
                columns: new[] { "PartitionId", "FlowId", "AssertionFailures", "UpdatedUtc" },
                filter: "[AssertionFailures] > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Record_PartitionId_FlowId_AssertionFailures_UpdatedUtc",
                schema: "osdu",
                table: "Record");

            migrationBuilder.DropColumn(
                name: "AssertionFailures",
                schema: "osdu",
                table: "RecordEvent");

            migrationBuilder.DropColumn(
                name: "AssertionFailures",
                schema: "osdu",
                table: "Record");
        }
    }
}
