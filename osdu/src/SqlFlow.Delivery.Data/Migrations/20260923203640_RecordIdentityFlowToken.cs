using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// The identity index in flow order, for the Records page narrowed to one flow. The primary key starts with the
    /// token, so a prefix seek reads the first candidates of every flow, and a flow sharing a common prefix with many
    /// others could find its own records past the candidate bound. With the flow first, a lookup narrowed to one flow
    /// reads that flow's tokens only, and stays the size of its candidates. Index only: no table, column or row changes.
    /// </summary>
    public partial class RecordIdentityFlowToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_RecordIdentity_FlowId_Token",
                schema: "osdu",
                table: "RecordIdentity",
                columns: new[] { "FlowId", "Token" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RecordIdentity_FlowId_Token",
                schema: "osdu",
                table: "RecordIdentity");
        }
    }
}
