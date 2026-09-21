using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// The recency indexes the Records page opens on: the most recently updated records across every flow, and the most
    /// recent of one custody state. Without them, "what has the delivery system taken in lately" would have to order
    /// the whole record table, which is the one listing in the product with no flow and no term to seek by. Both end in
    /// <c>UpdatedUtc</c>, so the newest records are the end of a range the server walks backwards and the listing costs
    /// what it shows rather than what the ledger holds. Indexes only: no table, column or row changes.
    /// </summary>
    public partial class RecentRecordsIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Record_Status_UpdatedUtc",
                schema: "osdu",
                table: "Record",
                columns: new[] { "Status", "UpdatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Record_UpdatedUtc",
                schema: "osdu",
                table: "Record",
                column: "UpdatedUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Record_Status_UpdatedUtc",
                schema: "osdu",
                table: "Record");

            migrationBuilder.DropIndex(
                name: "IX_Record_UpdatedUtc",
                schema: "osdu",
                table: "Record");
        }
    }
}
