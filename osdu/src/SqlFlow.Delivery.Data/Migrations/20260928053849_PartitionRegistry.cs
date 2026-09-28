using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class PartitionRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Partition",
                schema: "osdu",
                columns: table => new
                {
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Partition", x => x.Name);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Partition_IsDefault",
                schema: "osdu",
                table: "Partition",
                column: "IsDefault",
                unique: true,
                filter: "[IsDefault] = 1");

            // The partitions the module already keeps data under are registered, so what was there stays reachable and the
            // switcher lists it: every cache scope and every partition an interface is described in that is a literal
            // partition id (a header reference the sync could not resolve is not one). When there is exactly one, it is the
            // default, so a flow that names no partitions runs in it.
            migrationBuilder.Sql("""
                INSERT INTO [osdu].[Partition] ([Name], [Description], [IsDefault], [CreatedUtc], [CreatedBy], [UpdatedUtc], [UpdatedBy])
                SELECT s.[Name], N'Registered when the partition registry was added: the catalog already kept data under it.', CAST(0 AS bit),
                       SYSUTCDATETIME(), N'migration PartitionRegistry', SYSUTCDATETIME(), N'migration PartitionRegistry'
                FROM (
                    SELECT [Scope] AS [Name] FROM [osdu].[CacheVersion]
                    UNION SELECT [Scope] FROM [osdu].[CacheDefinition]
                    UNION SELECT [Partition] FROM [osdu].[Interface] WHERE [Partition] <> N''
                ) AS s
                WHERE LEN(s.[Name]) BETWEEN 1 AND 200 AND s.[Name] NOT LIKE N'%[^A-Za-z0-9_.-]%';

                UPDATE [osdu].[Partition] SET [IsDefault] = 1 WHERE (SELECT COUNT(*) FROM [osdu].[Partition]) = 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Partition",
                schema: "osdu");
        }
    }
}
