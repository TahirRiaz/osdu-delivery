using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// Lets a record wait for a record it refers to (docs/interfaces-design.md section 7). A record keeps the ids its
    /// pending document refers to beside the document, and, while it waits, the id it waits for, which a filtered index
    /// finds when the record holding that id lands. Submissions and work batches count the records left waiting. The
    /// columns are added empty; the index is built in the migration's transaction, which the hosts do not write through,
    /// since they refuse to start against a pending migration.
    /// </summary>
    public partial class RecordWaits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Waiting",
                schema: "osdu",
                table: "WorkBatch",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "Waiting",
                schema: "osdu",
                table: "Submission",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "PendingReferences",
                schema: "osdu",
                table: "Record",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WaitingFor",
                schema: "osdu",
                table: "Record",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.CreateIndex(
                name: "IX_Record_WaitingFor",
                schema: "osdu",
                table: "Record",
                column: "WaitingFor",
                filter: "[WaitingFor] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Record_WaitingFor",
                schema: "osdu",
                table: "Record");

            migrationBuilder.DropColumn(
                name: "Waiting",
                schema: "osdu",
                table: "WorkBatch");

            migrationBuilder.DropColumn(
                name: "Waiting",
                schema: "osdu",
                table: "Submission");

            migrationBuilder.DropColumn(
                name: "PendingReferences",
                schema: "osdu",
                table: "Record");

            migrationBuilder.DropColumn(
                name: "WaitingFor",
                schema: "osdu",
                table: "Record");
        }
    }
}
