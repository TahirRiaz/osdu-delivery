using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// Retires the <c>osdu.RecordCount</c> indexed view. SQL Server maintained it inside the transaction of every write to
    /// <c>osdu.Record</c>, and it grouped a flow's records into a handful of rows, so every node delivering that flow met
    /// every other one on the same few rows: a serialization point that grew with the fleet, not with the data. A flow's
    /// statistics are counted from the records themselves through the status index instead.
    /// <para>
    /// Nothing is lost with the view: it held no data of its own, only counts of the record table, and dropping it frees
    /// the storage its clustered index took. Going back down recreates it, so an older build finds what it expects.
    /// </para>
    /// </summary>
    public partial class RetireRecordCountView : Migration
    {
        private const string CreateRecordCountView =
            "CREATE VIEW [osdu].[RecordCount] WITH SCHEMABINDING AS "
            + "SELECT [FlowId], [Status], [LastVerifyOutcome], "
            + "DATEADD(hour, DATEDIFF(hour, CONVERT(datetime2(0), '20000101', 112), [LastDeliveredUtc]), CONVERT(datetime2(0), '20000101', 112)) AS [DeliveredHour], "
            + "COUNT_BIG(*) AS [Records] "
            + "FROM [osdu].[Record] "
            + "GROUP BY [FlowId], [Status], [LastVerifyOutcome], "
            + "DATEADD(hour, DATEDIFF(hour, CONVERT(datetime2(0), '20000101', 112), [LastDeliveredUtc]), CONVERT(datetime2(0), '20000101', 112))";

        private const string CreateRecordCountIndex =
            "CREATE UNIQUE CLUSTERED INDEX [IX_RecordCount] ON [osdu].[RecordCount] "
            + "([FlowId], [Status], [LastVerifyOutcome], [DeliveredHour])";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // Removing the view removes its clustered index with it, and the maintenance every record write paid.
            migrationBuilder.Sql("IF OBJECT_ID(N'[osdu].[RecordCount]', N'V') IS NOT NULL DROP VIEW [osdu].[RecordCount];");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql("IF OBJECT_ID(N'[osdu].[RecordCount]', N'V') IS NULL EXEC(N'" + CreateRecordCountView.Replace("'", "''") + "');");
            migrationBuilder.Sql(CreateRecordCountIndex);
        }
    }
}
