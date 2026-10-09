using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// Keeps what the ledger holds of a record deleted from it after it was removed from OSDU
    /// (osdu/docs/reference/concepts/removal-and-reversal.md, Deleting removed records from the ledger):
    /// <c>osdu.PurgedRecord</c>, one row per record deleted, keyed by the partition first, with the record's key,
    /// source key, label, OSDU id and last version, how many attempts went with it, the intervention that deleted it,
    /// who and when. Indexed on <c>(PartitionId, FlowId, DeliveryKey)</c> for a record's page and on
    /// <c>(PartitionId, TargetId)</c> for a lookup by OSDU id. No other table changes; the table is created empty, and
    /// going back down drops it.
    /// </summary>
    public partial class RecordPurges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PurgedRecord",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    PurgedRecordId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FlowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeliveryKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    TargetId = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true, collation: "Latin1_General_100_BIN2"),
                    LastVersion = table.Column<long>(type: "bigint", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    ActivityId = table.Column<long>(type: "bigint", nullable: true),
                    PurgedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PurgedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurgedRecord", x => new { x.PartitionId, x.PurgedRecordId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_PurgedRecord_PartitionId_FlowId_DeliveryKey",
                schema: "osdu",
                table: "PurgedRecord",
                columns: new[] { "PartitionId", "FlowId", "DeliveryKey" });

            migrationBuilder.CreateIndex(
                name: "IX_PurgedRecord_PartitionId_TargetId",
                schema: "osdu",
                table: "PurgedRecord",
                columns: new[] { "PartitionId", "TargetId" },
                filter: "[TargetId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PurgedRecord",
                schema: "osdu");
        }
    }
}
