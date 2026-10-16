using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// What recovering a lease that ran out needs to settle its records truthfully (osdu/docs/reference/concepts/record-lifecycle.md):
    /// <list type="bullet">
    /// <item><c>Lease.MaxAttempts</c>: the tries the claiming worker's flow gives a record, so the recovery fails a record
    /// whose worker stopped mid-attempt on every try instead of handing it back for ever; 0 for a lease claimed before.</item>
    /// <item><c>RecordEvent.ClaimAttemptCount</c>: the record's try count as the claim of the try left it, so a completion
    /// that arrives after its lease ran out and the record was handed back is applied only while no later claim, release,
    /// removal or delivery moved it; null for an event appended before, which is applied as it was.</item>
    /// <item><c>Lease.ForRemoval</c>: a lease a removal holds records under while it takes them out of OSDU, so no worker
    /// claims one meanwhile, and which lets its records go as they are when it ends or runs out; false for every lease
    /// before, each a worker's.</item>
    /// </list>
    /// The columns are added empty; going back down drops them.
    /// </summary>
    public partial class LeaseRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ClaimAttemptCount",
                schema: "osdu",
                table: "RecordEvent",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ForRemoval",
                schema: "osdu",
                table: "Lease",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MaxAttempts",
                schema: "osdu",
                table: "Lease",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClaimAttemptCount",
                schema: "osdu",
                table: "RecordEvent");

            migrationBuilder.DropColumn(
                name: "ForRemoval",
                schema: "osdu",
                table: "Lease");

            migrationBuilder.DropColumn(
                name: "MaxAttempts",
                schema: "osdu",
                table: "Lease");
        }
    }
}
