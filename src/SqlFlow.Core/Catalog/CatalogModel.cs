namespace SqlFlow.Core.Catalog;

/// <summary>Whether a catalog object is a table or a view.</summary>
public enum ObjectType
{
    Table,
    View,
}

/// <summary>The kind of a table constraint.</summary>
public enum ConstraintKind
{
    Default,
    Check,
    ForeignKey,
    PrimaryKey,
    Unique,
}

/// <summary>
/// The full structured shape of one table or view, the single introspection result shared by source
/// discovery and the schema-sync planner. Provider-neutral (no SqlClient).
/// </summary>
public sealed record CatalogObject
{
    public required ThreePartName Name { get; init; }
    public required ObjectType Type { get; init; }
    public required IReadOnlyList<CatalogColumn> Columns { get; init; }
    public IReadOnlyList<CatalogIndex> Indexes { get; init; } = [];
    public IReadOnlyList<CatalogConstraint> Constraints { get; init; } = [];
    public bool IsTemporal { get; init; }
}

/// <summary>One column of a catalog object, with the full metadata the planner needs to evolve it safely.</summary>
public sealed record CatalogColumn
{
    public required string Name { get; init; }
    public required int Ordinal { get; init; }

    /// <summary>The rendered native type, for example <c>nvarchar(50)</c>, <c>decimal(18, 2)</c>,
    /// <c>datetime2(7)</c>. For SQL Server it round-trips through <c>SqlDataType</c>.</summary>
    public required string NativeType { get; init; }

    public bool IsNullable { get; init; } = true;
    public string? Collation { get; init; }
    public bool IsIdentity { get; init; }
    public long? IdentitySeed { get; init; }
    public long? IdentityIncrement { get; init; }

    /// <summary>The computed-column expression without the AS keyword; null if not computed.</summary>
    public string? ComputedExpression { get; init; }
    public bool ComputedPersisted { get; init; }
    public string? DefaultExpression { get; init; }
    public bool IsPrimaryKeyMember { get; init; }
}

/// <summary>An index on a catalog object.</summary>
public sealed record CatalogIndex
{
    public required string Name { get; init; }
    public bool IsPrimaryKey { get; init; }
    public bool IsUnique { get; init; }
    public bool IsClustered { get; init; }
    public bool IsColumnStore { get; init; }
    public required IReadOnlyList<string> KeyColumns { get; init; }
}

/// <summary>A constraint on a catalog object; its bound columns tell the planner what blocks an ALTER COLUMN.</summary>
public sealed record CatalogConstraint
{
    public required string Name { get; init; }
    public ConstraintKind Kind { get; init; }
    public required IReadOnlyList<string> Columns { get; init; }
    public string? Definition { get; init; }
}
