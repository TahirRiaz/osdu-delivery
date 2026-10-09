using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// Indexes <c>osdu.PurgedRecord</c> on <c>(PartitionId, FlowId, PurgedUtc)</c>, carrying the OSDU id, so what a ledger
    /// deleted lately is one seek: an assertion run asks it before it judges what the search index lists, which can still
    /// list a record a removal took out of OSDU moments before (osdu/docs/reference/flow/assertion.md, Records the
    /// index may not list yet). No column or other table changes; going back down drops the index.
    /// </summary>
    public partial class PurgedRecordRecency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_PurgedRecord_PartitionId_FlowId_PurgedUtc",
                schema: "osdu",
                table: "PurgedRecord",
                columns: new[] { "PartitionId", "FlowId", "PurgedUtc" })
                .Annotation("SqlServer:Include", new[] { "TargetId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PurgedRecord_PartitionId_FlowId_PurgedUtc",
                schema: "osdu",
                table: "PurgedRecord");
        }
    }
}
