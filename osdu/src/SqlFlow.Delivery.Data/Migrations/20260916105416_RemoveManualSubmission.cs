using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveManualSubmission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InlineSubmission",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "SubmissionLanding",
                schema: "osdu");

            migrationBuilder.DropIndex(
                name: "IX_Submission_FlowId_Reference",
                schema: "osdu",
                table: "Submission");

            migrationBuilder.DropColumn(
                name: "GroupId",
                schema: "osdu",
                table: "Submission");

            migrationBuilder.DropColumn(
                name: "Reference",
                schema: "osdu",
                table: "Submission");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "GroupId",
                schema: "osdu",
                table: "Submission",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Reference",
                schema: "osdu",
                table: "Submission",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "InlineSubmission",
                schema: "osdu",
                columns: table => new
                {
                    SubmissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChildRowCount = table.Column<long>(type: "bigint", nullable: false),
                    ContentBytes = table.Column<int>(type: "int", nullable: false),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Error = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    FlowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FlowName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Force = table.Column<bool>(type: "bit", nullable: false),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LandedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    MappingReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Operation = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    OsduRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ParametersJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReceivedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ReceivedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RecordCount = table.Column<int>(type: "int", nullable: false),
                    RecordsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RequestHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InlineSubmission", x => x.SubmissionId);
                });

            migrationBuilder.CreateTable(
                name: "SubmissionLanding",
                schema: "osdu",
                columns: table => new
                {
                    SubmissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Dataset = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Bytes = table.Column<long>(type: "bigint", nullable: false),
                    ContentHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(800)", maxLength: 800, nullable: false),
                    Format = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Location = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    PreFlowName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PreRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowCount = table.Column<long>(type: "bigint", nullable: false),
                    WrittenUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubmissionLanding", x => new { x.SubmissionId, x.Dataset });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Submission_FlowId_Reference",
                schema: "osdu",
                table: "Submission",
                columns: new[] { "FlowId", "Reference" });

            migrationBuilder.CreateIndex(
                name: "IX_InlineSubmission_FlowId_ReceivedUtc",
                schema: "osdu",
                table: "InlineSubmission",
                columns: new[] { "FlowId", "ReceivedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_InlineSubmission_GroupId",
                schema: "osdu",
                table: "InlineSubmission",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_InlineSubmission_Status_ReceivedUtc",
                schema: "osdu",
                table: "InlineSubmission",
                columns: new[] { "Status", "ReceivedUtc" },
                filter: "[Status] IN ('accepted','landed','queued')");

            migrationBuilder.CreateIndex(
                name: "IX_SubmissionLanding_FileName",
                schema: "osdu",
                table: "SubmissionLanding",
                column: "FileName",
                unique: true);
        }
    }
}
