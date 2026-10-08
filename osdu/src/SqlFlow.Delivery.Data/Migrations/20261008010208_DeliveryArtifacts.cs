using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// Keeps what deliveries create in OSDU, or set out to create (docs/atomic-delivery-plan.md): <c>osdu.Artifact</c>, one row
    /// per dataset, record, session, version or rows a unit of work made, keyed by the partition first, unique on the record and
    /// the unit's slot, indexed on <c>(PartitionId, TargetId)</c> for the inventory and a lookup by OSDU id, and filtered on the
    /// open states, covering what the sweep that undoes what aborted deliveries left and the counts of open undos read. No other table changes; the table is created empty,
    /// and going back down drops it.
    /// </summary>
    public partial class DeliveryArtifacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Artifact",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    ArtifactId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FlowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeliveryKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UnitStartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Slot = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Role = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    TargetId = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true, collation: "Latin1_General_100_BIN2"),
                    Locator = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: true),
                    PriorVersion = table.Column<long>(type: "bigint", nullable: true),
                    State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    UndoAttempts = table.Column<int>(type: "int", nullable: false),
                    NextUndoUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SubmissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SettledUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SettledRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SettledBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Artifact", x => new { x.PartitionId, x.ArtifactId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Artifact_Open",
                schema: "osdu",
                table: "Artifact",
                columns: new[] { "PartitionId", "FlowId", "State", "NextUndoUtc" },
                filter: "[State] IN (N'intent', N'pending', N'due', N'failed')")
                .Annotation("SqlServer:Include", new[] { "DeliveryKey", "UnitId", "UndoAttempts", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Artifact_PartitionId_FlowId_DeliveryKey_UnitId_Slot",
                schema: "osdu",
                table: "Artifact",
                columns: new[] { "PartitionId", "FlowId", "DeliveryKey", "UnitId", "Slot" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Artifact_PartitionId_TargetId",
                schema: "osdu",
                table: "Artifact",
                columns: new[] { "PartitionId", "TargetId" },
                filter: "[TargetId] IS NOT NULL")
                .Annotation("SqlServer:Include", new[] { "State", "Role", "FlowId", "DeliveryKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Artifact",
                schema: "osdu");
        }
    }
}
