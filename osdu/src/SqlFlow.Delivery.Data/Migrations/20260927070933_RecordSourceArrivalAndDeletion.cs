using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// What a record's history needs to date the changes of its row. A record keeps when the ingestion table first
    /// inserted its row (<c>InsertedDate_DW</c>), which later changes never move, and an attempt keeps when the ingestion
    /// table marked the row deleted, for the hold of a deleted row. Two nullable columns: nothing is rewritten, and a
    /// record learns its arrival the next time a plan reads its row.
    /// </summary>
    public partial class RecordSourceArrivalAndDeletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "SourceInsertedUtc",
                schema: "osdu",
                table: "Record",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SourceDeletedUtc",
                schema: "osdu",
                table: "Attempt",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SourceInsertedUtc",
                schema: "osdu",
                table: "Record");

            migrationBuilder.DropColumn(
                name: "SourceDeletedUtc",
                schema: "osdu",
                table: "Attempt");
        }
    }
}
