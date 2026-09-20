using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// The identity index: every value a record is findable by, so an operator holding a wellbore id, a well name, a
    /// log id, an OSDU id or the name of the file a record arrived in finds the record across every flow in
    /// milliseconds, instead of having to know which flow delivered it. One row per value per record, the value folded
    /// to upper case in <c>Token</c> and kept as written in <c>Display</c>, with <c>Kind</c> saying where it came from
    /// (identity, key, label, osdu, file). The primary key leads with the token, which is what makes a prefix search a
    /// seek; the second index is the record's own rows, which a staging rewrites as a set.
    ///
    /// The table is created empty. Records staged after this migration write their rows as they are staged, and the
    /// control plane fills in the records already in the ledger, a page at a time, from what their rows hold.
    /// </summary>
    public partial class RecordIdentityIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RecordIdentity",
                schema: "osdu",
                columns: table => new
                {
                    FlowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeliveryKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Token = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Display = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecordIdentity", x => new { x.Token, x.FlowId, x.DeliveryKey });
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecordIdentity_FlowId_DeliveryKey",
                schema: "osdu",
                table: "RecordIdentity",
                columns: new[] { "FlowId", "DeliveryKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecordIdentity",
                schema: "osdu");
        }
    }
}
