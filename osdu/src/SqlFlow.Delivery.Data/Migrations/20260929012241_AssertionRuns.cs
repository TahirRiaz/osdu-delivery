using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// Adds the report of assertion flows (docs/assertions-design.md section 7): <c>osdu.AssertionRun</c>, one row per run of a
    /// flow's tests in a partition with how they came out, and <c>osdu.AssertionResult</c>, one row per test and run with the
    /// whole result as JSON. Both are ledger tables, keyed by partition first, and a flow's latest result of each test is one
    /// seek of the results' index by flow and test.
    /// </summary>
    public partial class AssertionRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssertionResult",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    ResultId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AssertionRunId = table.Column<long>(type: "bigint", nullable: false),
                    FlowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TestName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    Matched = table.Column<long>(type: "bigint", nullable: true),
                    Evaluated = table.Column<long>(type: "bigint", nullable: true),
                    Sampled = table.Column<bool>(type: "bit", nullable: false),
                    Assertions = table.Column<int>(type: "int", nullable: false),
                    FailedAssertions = table.Column<int>(type: "int", nullable: false),
                    DefinitionHash = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssertionResult", x => new { x.PartitionId, x.ResultId });
                });

            migrationBuilder.CreateTable(
                name: "AssertionRun",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    AssertionRunId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FlowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FlowName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Actor = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Selection = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Tests = table.Column<int>(type: "int", nullable: false),
                    Passed = table.Column<int>(type: "int", nullable: false),
                    Failed = table.Column<int>(type: "int", nullable: false),
                    Warned = table.Column<int>(type: "int", nullable: false),
                    Errored = table.Column<int>(type: "int", nullable: false),
                    Skipped = table.Column<int>(type: "int", nullable: false),
                    DefinitionsHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssertionRun", x => new { x.PartitionId, x.AssertionRunId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssertionResult_PartitionId_AssertionRunId",
                schema: "osdu",
                table: "AssertionResult",
                columns: new[] { "PartitionId", "AssertionRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssertionResult_PartitionId_FlowId_TestName_AssertionRunId",
                schema: "osdu",
                table: "AssertionResult",
                columns: new[] { "PartitionId", "FlowId", "TestName", "AssertionRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssertionResult_ResultId",
                schema: "osdu",
                table: "AssertionResult",
                column: "ResultId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssertionRun_AssertionRunId",
                schema: "osdu",
                table: "AssertionRun",
                column: "AssertionRunId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssertionRun_PartitionId_FlowId_StartedUtc",
                schema: "osdu",
                table: "AssertionRun",
                columns: new[] { "PartitionId", "FlowId", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AssertionRun_RunId",
                schema: "osdu",
                table: "AssertionRun",
                column: "RunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssertionResult",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "AssertionRun",
                schema: "osdu");
        }
    }
}
