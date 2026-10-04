using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// Keeps what the gate before a record is sent found of each record's document (docs/validation-plan.md).
    /// <list type="bullet">
    /// <item><c>Record.ValidationOutcome</c>, <c>ValidationProblems</c> and <c>ValidatedUtc</c>: what the last check of the
    /// record's document came to (valid, invalid or unverified), how many problems it found and when; null until a document
    /// of the record was checked. A filtered index on <c>(PartitionId, FlowId, ValidationOutcome, UpdatedUtc)</c> counts a
    /// flow's records by outcome and lists the invalid ones newest first, and holds only the records a check reached.</item>
    /// <item><c>Record.AcceptedMetadataHash</c>: the metadata hash of the pending document a release accepted as it is, which
    /// the gate sends whatever its verdict says.</item>
    /// <item><c>RecordEvent.ValidationOutcome</c>, <c>ValidationProblems</c> and <c>ValidatedUtc</c>: what a worker's try
    /// found, which the lease applies to the record with the rest of the completion.</item>
    /// </list>
    /// Every column is added empty, so the record table is not rewritten, and the index starts empty. Going back down drops
    /// the index and the columns.
    /// </summary>
    public partial class RecordValidation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ValidatedUtc",
                schema: "osdu",
                table: "RecordEvent",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ValidationOutcome",
                schema: "osdu",
                table: "RecordEvent",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ValidationProblems",
                schema: "osdu",
                table: "RecordEvent",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AcceptedMetadataHash",
                schema: "osdu",
                table: "Record",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ValidatedUtc",
                schema: "osdu",
                table: "Record",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ValidationOutcome",
                schema: "osdu",
                table: "Record",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ValidationProblems",
                schema: "osdu",
                table: "Record",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Record_PartitionId_FlowId_ValidationOutcome_UpdatedUtc",
                schema: "osdu",
                table: "Record",
                columns: new[] { "PartitionId", "FlowId", "ValidationOutcome", "UpdatedUtc" },
                filter: "[ValidationOutcome] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Record_PartitionId_FlowId_ValidationOutcome_UpdatedUtc",
                schema: "osdu",
                table: "Record");

            migrationBuilder.DropColumn(
                name: "ValidatedUtc",
                schema: "osdu",
                table: "RecordEvent");

            migrationBuilder.DropColumn(
                name: "ValidationOutcome",
                schema: "osdu",
                table: "RecordEvent");

            migrationBuilder.DropColumn(
                name: "ValidationProblems",
                schema: "osdu",
                table: "RecordEvent");

            migrationBuilder.DropColumn(
                name: "AcceptedMetadataHash",
                schema: "osdu",
                table: "Record");

            migrationBuilder.DropColumn(
                name: "ValidatedUtc",
                schema: "osdu",
                table: "Record");

            migrationBuilder.DropColumn(
                name: "ValidationOutcome",
                schema: "osdu",
                table: "Record");

            migrationBuilder.DropColumn(
                name: "ValidationProblems",
                schema: "osdu",
                table: "Record");
        }
    }
}
