using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// The tables of dimension flows (docs/dimension-plan.md, docs/ledger.md): <c>Dimension</c>, one row per dimension of a
    /// flow in a partition; <c>DimensionRun</c>, one per build; <c>DimensionMember</c>, a dimension's clean values with their
    /// search filters; <c>DimensionValue</c>, its originals exactly as the index holds them, each under its member or with the
    /// reason it has none; and <c>DimensionChange</c>, what each build changed of them. Every table is keyed by the ledger
    /// partition first, and clean values and originals are compared in the binary collation, as OSDU ids are.
    /// </summary>
    public partial class DimensionFlows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Dimension",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    DimensionId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FlowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FlowName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Query = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Path = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    FieldIndex = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    NestedPath = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    AggregateBy = table.Column<string>(type: "nvarchar(1100)", maxLength: 1100, nullable: true),
                    Repeats = table.Column<bool>(type: "bit", nullable: false),
                    CleanJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DefinitionHash = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Members = table.Column<long>(type: "bigint", nullable: false),
                    Originals = table.Column<long>(type: "bigint", nullable: false),
                    LastRunId = table.Column<long>(type: "bigint", nullable: true),
                    LastBuiltUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dimension", x => new { x.PartitionId, x.DimensionId });
                });

            migrationBuilder.CreateTable(
                name: "DimensionChange",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    ChangeId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DimensionId = table.Column<int>(type: "int", nullable: false),
                    DimensionRunId = table.Column<long>(type: "bigint", nullable: false),
                    ValueId = table.Column<long>(type: "bigint", nullable: false),
                    Change = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    FromMemberId = table.Column<long>(type: "bigint", nullable: true),
                    ToMemberId = table.Column<long>(type: "bigint", nullable: true),
                    ChangedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DimensionChange", x => new { x.PartitionId, x.ChangeId });
                });

            migrationBuilder.CreateTable(
                name: "DimensionMember",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    MemberId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DimensionId = table.Column<int>(type: "int", nullable: false),
                    Value = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Records = table.Column<long>(type: "bigint", nullable: false),
                    RecordsExact = table.Column<bool>(type: "bit", nullable: false),
                    Originals = table.Column<int>(type: "int", nullable: false),
                    Unfilterable = table.Column<int>(type: "int", nullable: false),
                    Filter = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FilterParts = table.Column<int>(type: "int", nullable: false),
                    FirstSeenRunId = table.Column<long>(type: "bigint", nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RemovedRunId = table.Column<long>(type: "bigint", nullable: true),
                    RemovedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DimensionMember", x => new { x.PartitionId, x.MemberId });
                });

            migrationBuilder.CreateTable(
                name: "DimensionRun",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    DimensionRunId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DimensionId = table.Column<int>(type: "int", nullable: false),
                    FlowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Actor = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    DefinitionHash = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Query = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    AggregateBy = table.Column<string>(type: "nvarchar(1100)", maxLength: 1100, nullable: true),
                    Templates = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Records = table.Column<long>(type: "bigint", nullable: true),
                    WithValue = table.Column<long>(type: "bigint", nullable: true),
                    Nulls = table.Column<long>(type: "bigint", nullable: false),
                    TooLong = table.Column<long>(type: "bigint", nullable: true),
                    Unreadable = table.Column<long>(type: "bigint", nullable: false),
                    Members = table.Column<long>(type: "bigint", nullable: false),
                    Originals = table.Column<long>(type: "bigint", nullable: false),
                    LeftOut = table.Column<long>(type: "bigint", nullable: false),
                    Unfilterable = table.Column<long>(type: "bigint", nullable: false),
                    MembersAdded = table.Column<long>(type: "bigint", nullable: false),
                    MembersRemoved = table.Column<long>(type: "bigint", nullable: false),
                    MembersRestored = table.Column<long>(type: "bigint", nullable: false),
                    OriginalsAdded = table.Column<long>(type: "bigint", nullable: false),
                    OriginalsRemoved = table.Column<long>(type: "bigint", nullable: false),
                    OriginalsMoved = table.Column<long>(type: "bigint", nullable: false),
                    OriginalsRestored = table.Column<long>(type: "bigint", nullable: false),
                    Aggregations = table.Column<int>(type: "int", nullable: false),
                    Slices = table.Column<int>(type: "int", nullable: false),
                    Splits = table.Column<int>(type: "int", nullable: false),
                    ScannedSlices = table.Column<int>(type: "int", nullable: false),
                    ScanPages = table.Column<int>(type: "int", nullable: false),
                    ScannedUnits = table.Column<long>(type: "bigint", nullable: false),
                    CountQueries = table.Column<int>(type: "int", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DimensionRun", x => new { x.PartitionId, x.DimensionRunId });
                });

            migrationBuilder.CreateTable(
                name: "DimensionValue",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    ValueId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DimensionId = table.Column<int>(type: "int", nullable: false),
                    Original = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false, collation: "Latin1_General_100_BIN2"),
                    OriginalHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    MemberId = table.Column<long>(type: "bigint", nullable: true),
                    LeftOut = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Count = table.Column<long>(type: "bigint", nullable: false),
                    Filterable = table.Column<bool>(type: "bit", nullable: false),
                    FirstSeenRunId = table.Column<long>(type: "bigint", nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RemovedRunId = table.Column<long>(type: "bigint", nullable: true),
                    RemovedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    MemberSinceRunId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DimensionValue", x => new { x.PartitionId, x.ValueId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Dimension_DimensionId",
                schema: "osdu",
                table: "Dimension",
                column: "DimensionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dimension_PartitionId_FlowId_Name",
                schema: "osdu",
                table: "Dimension",
                columns: new[] { "PartitionId", "FlowId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionChange_ChangeId",
                schema: "osdu",
                table: "DimensionChange",
                column: "ChangeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionChange_PartitionId_DimensionId_ValueId_ChangeId",
                schema: "osdu",
                table: "DimensionChange",
                columns: new[] { "PartitionId", "DimensionId", "ValueId", "ChangeId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionChange_PartitionId_DimensionRunId",
                schema: "osdu",
                table: "DimensionChange",
                columns: new[] { "PartitionId", "DimensionRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionMember_MemberId",
                schema: "osdu",
                table: "DimensionMember",
                column: "MemberId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionMember_PartitionId_DimensionId_Records_Value",
                schema: "osdu",
                table: "DimensionMember",
                columns: new[] { "PartitionId", "DimensionId", "Records", "Value" },
                descending: new[] { false, false, true, false });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionMember_PartitionId_DimensionId_RemovedRunId",
                schema: "osdu",
                table: "DimensionMember",
                columns: new[] { "PartitionId", "DimensionId", "RemovedRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionMember_PartitionId_DimensionId_Value",
                schema: "osdu",
                table: "DimensionMember",
                columns: new[] { "PartitionId", "DimensionId", "Value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionRun_DimensionRunId",
                schema: "osdu",
                table: "DimensionRun",
                column: "DimensionRunId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionRun_PartitionId_DimensionId_StartedUtc",
                schema: "osdu",
                table: "DimensionRun",
                columns: new[] { "PartitionId", "DimensionId", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionRun_RunId",
                schema: "osdu",
                table: "DimensionRun",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_DimensionValue_PartitionId_DimensionId_Count_ValueId",
                schema: "osdu",
                table: "DimensionValue",
                columns: new[] { "PartitionId", "DimensionId", "Count", "ValueId" },
                descending: new[] { false, false, true, false });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionValue_PartitionId_DimensionId_MemberId",
                schema: "osdu",
                table: "DimensionValue",
                columns: new[] { "PartitionId", "DimensionId", "MemberId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionValue_PartitionId_DimensionId_OriginalHash",
                schema: "osdu",
                table: "DimensionValue",
                columns: new[] { "PartitionId", "DimensionId", "OriginalHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionValue_ValueId",
                schema: "osdu",
                table: "DimensionValue",
                column: "ValueId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Dimension",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "DimensionChange",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "DimensionMember",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "DimensionRun",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "DimensionValue",
                schema: "osdu");
        }
    }
}
