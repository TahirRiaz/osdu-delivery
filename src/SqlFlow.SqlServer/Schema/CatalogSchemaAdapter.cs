using SqlFlow.Core.Catalog;
using SqlFlow.Core.Connections;

namespace SqlFlow.SqlServer.Schema;

/// <summary>
/// Bridges a live <see cref="CatalogObject"/> (from <see cref="ICatalogReader"/>) into the
/// <see cref="SqlColumn"/> list the <see cref="SchemaEvolutionPlanner"/> diffs against. For a SQL Server
/// object the native type strings round-trip through <see cref="SqlDataType.Parse"/>; for a foreign source
/// (MySQL, PostgreSQL) the provider's <see cref="ISourceTypeMapper"/> translates each native type into the
/// SQL Server type that will hold it, and an unmappable type throws rather than degrading into an opaque
/// type the evolution planner cannot reason about. This is the single point where introspection feeds
/// evolution.
/// </summary>
public static class CatalogSchemaAdapter
{
    public static IReadOnlyList<SqlColumn> ToColumns(CatalogObject catalogObject)
    {
        ArgumentNullException.ThrowIfNull(catalogObject);

        return catalogObject.Columns
            .OrderBy(c => c.Ordinal)
            .Select(c => new SqlColumn
            {
                Name = c.Name,
                DataType = SqlDataType.Parse(c.NativeType),
                IsNullable = c.IsNullable,
                IsIdentity = c.IsIdentity,
                IsPrimaryKey = c.IsPrimaryKeyMember,
            })
            .ToList();
    }

    /// <summary>Maps each column's native source type to its SQL Server equivalent through the provider's
    /// type mapper before parsing, so staging and target DDL are always valid T-SQL.</summary>
    public static IReadOnlyList<SqlColumn> ToColumns(CatalogObject catalogObject, ISourceTypeMapper typeMapper)
    {
        ArgumentNullException.ThrowIfNull(catalogObject);
        ArgumentNullException.ThrowIfNull(typeMapper);

        return catalogObject.Columns
            .OrderBy(c => c.Ordinal)
            .Select(c => new SqlColumn
            {
                Name = c.Name,
                DataType = SqlDataType.Parse(typeMapper.ToSqlServerType(c)),
                IsNullable = c.IsNullable,
                IsIdentity = c.IsIdentity,
                IsPrimaryKey = c.IsPrimaryKeyMember,
            })
            .ToList();
    }
}
