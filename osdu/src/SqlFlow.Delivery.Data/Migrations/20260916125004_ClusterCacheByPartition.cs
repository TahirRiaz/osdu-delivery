using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// Stores the cache's versions and items in partition order. Both tables were clustered on an id that says nothing about
    /// the partition, so a statement a refresh filters by its partition could read, and lock, every partition's rows: two
    /// partitions refreshing at once each waited on the other's new rows until the database ended one as a deadlock victim.
    /// Clustered by partition first, such a statement is a range seek of its own partition. The primary keys stay as they
    /// are, nonclustered. Each table's nonclustered indexes are dropped before its clustered key changes and built once after.
    /// </summary>
    public partial class ClusterCacheByPartition : Migration
    {
        private const string Schema = "osdu";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_CacheVersion_Scope_Sequence", schema: Schema, table: "CacheVersion");
            migrationBuilder.DropIndex(name: "IX_CacheVersion_Scope_Version", schema: Schema, table: "CacheVersion");
            migrationBuilder.DropIndex(name: "IX_CacheVersion_RunId", schema: Schema, table: "CacheVersion");
            migrationBuilder.DropPrimaryKey(name: "PK_CacheVersion", schema: Schema, table: "CacheVersion");

            migrationBuilder.CreateIndex(
                name: "IX_CacheVersion_Scope_Sequence",
                schema: Schema,
                table: "CacheVersion",
                columns: new[] { "Scope", "Sequence" },
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_CacheVersion",
                schema: Schema,
                table: "CacheVersion",
                column: "Id")
                .Annotation("SqlServer:Clustered", false);

            CreateVersionIndexes(migrationBuilder);

            migrationBuilder.DropIndex(name: "IX_CacheItem_Scope_TypeName_RecordId_FromSequence", schema: Schema, table: "CacheItem");
            migrationBuilder.DropIndex(name: "IX_CacheItem_Scope_ToSequence", schema: Schema, table: "CacheItem");
            migrationBuilder.DropPrimaryKey(name: "PK_CacheItem", schema: Schema, table: "CacheItem");

            migrationBuilder.CreateIndex(
                name: "IX_CacheItem_Scope_ItemId",
                schema: Schema,
                table: "CacheItem",
                columns: new[] { "Scope", "ItemId" },
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_CacheItem",
                schema: Schema,
                table: "CacheItem",
                column: "ItemId")
                .Annotation("SqlServer:Clustered", false);

            CreateItemIndexes(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_CacheVersion_Scope_Version", schema: Schema, table: "CacheVersion");
            migrationBuilder.DropIndex(name: "IX_CacheVersion_RunId", schema: Schema, table: "CacheVersion");
            migrationBuilder.DropPrimaryKey(name: "PK_CacheVersion", schema: Schema, table: "CacheVersion");
            migrationBuilder.DropIndex(name: "IX_CacheVersion_Scope_Sequence", schema: Schema, table: "CacheVersion");

            migrationBuilder.AddPrimaryKey(name: "PK_CacheVersion", schema: Schema, table: "CacheVersion", column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_CacheVersion_Scope_Sequence",
                schema: Schema,
                table: "CacheVersion",
                columns: new[] { "Scope", "Sequence" },
                unique: true);

            CreateVersionIndexes(migrationBuilder);

            migrationBuilder.DropIndex(name: "IX_CacheItem_Scope_TypeName_RecordId_FromSequence", schema: Schema, table: "CacheItem");
            migrationBuilder.DropIndex(name: "IX_CacheItem_Scope_ToSequence", schema: Schema, table: "CacheItem");
            migrationBuilder.DropPrimaryKey(name: "PK_CacheItem", schema: Schema, table: "CacheItem");
            migrationBuilder.DropIndex(name: "IX_CacheItem_Scope_ItemId", schema: Schema, table: "CacheItem");

            migrationBuilder.AddPrimaryKey(name: "PK_CacheItem", schema: Schema, table: "CacheItem", column: "ItemId");

            CreateItemIndexes(migrationBuilder);
        }

        private static void CreateVersionIndexes(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_CacheVersion_Scope_Version",
                schema: Schema,
                table: "CacheVersion",
                columns: new[] { "Scope", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CacheVersion_RunId",
                schema: Schema,
                table: "CacheVersion",
                column: "RunId");
        }

        private static void CreateItemIndexes(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_CacheItem_Scope_TypeName_RecordId_FromSequence",
                schema: Schema,
                table: "CacheItem",
                columns: new[] { "Scope", "TypeName", "RecordId", "FromSequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CacheItem_Scope_ToSequence",
                schema: Schema,
                table: "CacheItem",
                columns: new[] { "Scope", "ToSequence" });
        }
    }
}
