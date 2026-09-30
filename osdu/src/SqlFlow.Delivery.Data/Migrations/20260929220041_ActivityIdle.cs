using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// Marks the runs that changed nothing (docs/ledger.md, <c>osdu.Activity</c>): <c>Activity.Idle</c>, set by the run
    /// that completes the activity, and an index on <c>(PartitionId, Idle, StartedUtc)</c> so the audit trail opens without
    /// them, and counts them, with a seek. A run written before the column is marked idle from what the ledger still says
    /// of it: a deliver or intake run that completed, whose submission planned, sent, held, blocked and failed nothing and
    /// leaves nothing waiting, and under whose run id no attempt was made. Every other activity stays as it was.
    /// </summary>
    public partial class ActivityIdle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Idle",
                schema: "osdu",
                table: "Activity",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // Before the index, so it is built once over the marked rows. A seek of the submission by its unique id and of
            // the attempts by run id per activity; the activity table holds one row per run and intervention.
            migrationBuilder.Sql(
                """
                UPDATE a SET a.[Idle] = 1
                FROM [osdu].[Activity] AS a
                INNER JOIN [osdu].[Submission] AS s ON s.[SubmissionId] = a.[SubmissionId]
                WHERE a.[Kind] IN (N'deliver', N'intake') AND a.[Outcome] = N'completed' AND a.[RunId] IS NOT NULL
                    AND s.[Planned] = 0 AND s.[Delivered] = 0 AND s.[UnchangedAtPush] = 0 AND s.[Held] = 0 AND s.[Failed] = 0
                    AND s.[Blocked] = 0 AND s.[AwaitingApproval] = 0 AND s.[Waiting] = 0
                    AND NOT EXISTS (SELECT 1 FROM [osdu].[Attempt] AS t WHERE t.[RunId] = a.[RunId]);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Activity_PartitionId_Idle_StartedUtc",
                schema: "osdu",
                table: "Activity",
                columns: new[] { "PartitionId", "Idle", "StartedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Activity_PartitionId_Idle_StartedUtc",
                schema: "osdu",
                table: "Activity");

            migrationBuilder.DropColumn(
                name: "Idle",
                schema: "osdu",
                table: "Activity");
        }
    }
}
