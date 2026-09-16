using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// Answers the worker's reads of the record table from an index alone. A read that found its records in an index and
    /// went on to the table for the rest held its place in the index while it waited for a record another node was
    /// writing, and that node, moving the record in the same index, waited for the read: the database ended one of them
    /// as a deadlock victim. The flow's claim index now carries what those reads need, and a new index does the same for
    /// one submission's records. The claim index is rebuilt in place (DROP_EXISTING), so the table is never without it
    /// and the rebuild reads the index it replaces rather than the table.
    /// </summary>
    public partial class CoverWorkerReads : Migration
    {
        private const string Schema = "osdu";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE INDEX [IX_Record_FlowId_Status_NextAttemptUtc]
                    ON [osdu].[Record] ([FlowId], [Status], [NextAttemptUtc])
                    INCLUDE ([LastSubmissionId], [LeaseExpiresUtc], [UpdatedUtc], [PendingDocumentRef])
                    WITH (DROP_EXISTING = ON);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Record_FlowId_LastSubmissionId_Status_NextAttemptUtc",
                schema: Schema,
                table: "Record",
                columns: new[] { "FlowId", "LastSubmissionId", "Status", "NextAttemptUtc" })
                .Annotation("SqlServer:Include", new[] { "LeaseExpiresUtc", "UpdatedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Record_FlowId_LastSubmissionId_Status_NextAttemptUtc",
                schema: Schema,
                table: "Record");

            migrationBuilder.Sql("""
                CREATE INDEX [IX_Record_FlowId_Status_NextAttemptUtc]
                    ON [osdu].[Record] ([FlowId], [Status], [NextAttemptUtc])
                    WITH (DROP_EXISTING = ON);
                """);
        }
    }
}
