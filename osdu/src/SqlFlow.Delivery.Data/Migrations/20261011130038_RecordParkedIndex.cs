using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// The parked records (held, failed, removed or reverted) by whether they are blocked (<c>IX_Record_Parked</c>, keyed on
    /// <c>(PartitionId, FlowId, Blocked, Status)</c> and including the request to plan a record again and its kept document):
    /// a flow's statistics count its blocked records, and its held or failed ones nothing will move, from the range that holds
    /// the parked records alone. Built over the records parked now; going back down drops it.
    /// </summary>
    public partial class RecordParkedIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Record_Parked",
                schema: "osdu",
                table: "Record",
                columns: new[] { "PartitionId", "FlowId", "Blocked", "Status" },
                filter: "[Status] IN (N'held', N'failed', N'deleted', N'reverted')")
                .Annotation("SqlServer:Include", new[] { "PlanRequestedUtc", "PendingDocumentRef" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Record_Parked",
                schema: "osdu",
                table: "Record");
        }
    }
}
