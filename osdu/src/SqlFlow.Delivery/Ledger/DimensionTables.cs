using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Model;
using SqlFlow.SqlServer.Schema;

namespace SqlFlow.Delivery.Ledger;

/// <summary>An attribute column of a dimension's table: its name, and whether it is collected, so a key holds several.</summary>
public sealed record DimensionTableColumn(string Name, bool Collected);

/// <summary>
/// A dimension's table as a build keeps it: its name, and its attribute columns in the order the dimension declares them.
/// </summary>
/// <param name="Name">The table's name in the module's schema, without the schema.</param>
/// <param name="Columns">The attribute columns, in the order the dimension declares them.</param>
public sealed record DimensionTableSpec(string Name, IReadOnlyList<DimensionTableColumn> Columns);

/// <summary>
/// The table of a dimension (docs/dimension-plan.md, The table): the dimension as one table any SQL client reads and joins
/// on, in the module's schema, named after its flow and its name. A row per key and value it collects (one row for a key
/// of a dimension collecting nothing, or collecting no value), with the row's number, the partition, the key's number, the
/// key exactly as the index holds it, its value, the records of the row, the search filter finding the key's records, and
/// a column per attribute named as the dimension declares it. A key under no value, and a key no build finds any more,
/// is no row.
/// </summary>
/// <remarks>
/// <para>
/// A build makes the table and keeps it: it writes the rows in the transaction that writes the rest of the dimension, so
/// the table is never behind it, and it writes only what differs from the rows the table held, so a row keeps its number
/// for as long as the dimension holds it.
/// </para>
/// <para>
/// The table's schema follows the declaration through SQLFlow's schema evolution, the same that widens an ingestion
/// table: a first build creates the table with its identity key, an attribute the flow starts to declare gets its
/// column on the next build, and nothing is ever dropped or narrowed. An attribute the flow stops declaring keeps its
/// column, emptied, so a query that names it still runs.
/// </para>
/// </remarks>
public static class DimensionTables
{
    /// <summary>What every dimension table's name begins with, so the dimensions of a schema are told from the ledger's tables at a glance.</summary>
    public const string Prefix = "dim_";

    /// <summary>The longest name a table is given: what SQL Server allows an object, less the room its primary key's name needs.</summary>
    private const int MaxNameLength = 120;

    /// <summary>The collation text is matched exactly by, whatever the database compares by.</summary>
    internal const string Exact = "Latin1_General_100_BIN2";

    /// <summary>The columns a table has whatever its dimension declares, which an attribute cannot be named after.</summary>
    public static readonly IReadOnlyList<string> FixedColumns = ["id", "partition", "key_id", "key", "value", "records", "filter"];

    /// <summary>The key SQLFlow's schema evolution holds a table to: a change to it is refused, never applied.</summary>
    internal static readonly IReadOnlySet<string> KeyColumns = new HashSet<string>(["id"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The table's name: <c>dim_&lt;flow&gt;_&lt;dimension&gt;</c>, each with whatever is not a letter, a digit or an
    /// underscore made an underscore, so it needs no quoting. A name longer than a table's may be is cut, and ends with
    /// eight characters of its hash so two long names stay apart.
    /// </summary>
    public static string NameOf(string flowName, string dimension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        ArgumentException.ThrowIfNullOrWhiteSpace(dimension);
        var name = $"{Prefix}{Plain(Kept(flowName))}_{Plain(dimension)}";
        if (name.Length <= MaxNameLength)
        {
            return name;
        }

        var tail = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8].ToLowerInvariant();
        return $"{name[..(MaxNameLength - 9)]}_{tail}";
    }

    /// <summary>The table of dimension <paramref name="dimension"/> of flow <paramref name="flowName"/>, reading <paramref name="attributes"/>.</summary>
    /// <exception cref="DeliveryException">The dimension declares more attributes than a build lays out.</exception>
    public static DimensionTableSpec Of(string flowName, string dimension, IReadOnlyList<DimensionAttributeSpec> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        if (attributes.Count > DimensionSpec.MaxAttributes)
        {
            throw new DeliveryException(
                string.Create(CultureInfo.InvariantCulture, $"Dimension {dimension} of {flowName} declares {attributes.Count} attributes, and a dimension's table holds {DimensionSpec.MaxAttributes}."));
        }

        return new DimensionTableSpec(NameOf(flowName, dimension), attributes.Select(a => new DimensionTableColumn(a.Name, a.IsCollected)).ToList());
    }

    /// <summary>A table's name with its schema, as a statement names it: <c>[osdu].[dim_...]</c>.</summary>
    public static string Qualified(string name) => $"[{DeliveryModel.SchemaName}].{Quoted(name)}";

    /// <summary>A table's name as a person types it: <c>osdu.dim_...</c>, which needs no quoting.</summary>
    public static string Shown(string name) => $"{DeliveryModel.SchemaName}.{name}";

    /// <summary>
    /// The columns <paramref name="table"/> is to have, which SQLFlow's schema evolution brings the table to: the fixed
    /// ones first, so an attribute the flow declares later takes its place after them as the others did.
    /// </summary>
    internal static IReadOnlyList<SqlColumn> Desired(DimensionTableSpec table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var columns = new List<SqlColumn>
        {
            new() { Name = "id", DataType = Type("bigint"), IsNullable = false, IsIdentity = true, IsPrimaryKey = true, Role = ColumnRole.Identity, Origin = ColumnOrigin.Computed },
            new() { Name = "partition", DataType = Text(256), IsNullable = false },
            new() { Name = "key_id", DataType = Type("bigint"), IsNullable = false },
            new() { Name = "key", DataType = Text(DeliveryDimensionValue.MaxOriginalLength), IsNullable = false },
            new() { Name = "value", DataType = Text(256), IsNullable = false },
            new() { Name = "records", DataType = Type("bigint"), IsNullable = false },
            new() { Name = "filter", DataType = Text(DeliveryDimensionValue.MaxFilterLength), IsNullable = true },
        };
        columns.AddRange(table.Columns.Select(c => new SqlColumn { Name = c.Name, DataType = Text(DeliveryDimensionAttributeValue.MaxValueLength), IsNullable = true }));
        return columns;
    }

    /// <summary>
    /// The indexes a table is read through, made when it has none of the name: a key's rows, which a build matches the
    /// rows it writes by and a join on the key's number seeks; and the rows of a partition in value order, which a page
    /// reads and a count counts.
    /// </summary>
    internal static string IndexSql(string name)
    {
        var table = Qualified(name);
        var literal = table.Replace("'", "''", StringComparison.Ordinal);
        return $"""
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'{literal}', N'U') AND [name] = N'IX_key')
                CREATE INDEX [IX_key] ON {table} ([partition], [key_id]);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'{literal}', N'U') AND [name] = N'IX_value')
                CREATE INDEX [IX_value] ON {table} ([partition], [value], [id]);
            """;
    }

    /// <summary>
    /// The statements that bring the rows of one partition in <paramref name="table"/> to the rows a build laid out in
    /// <c>#DimRow</c> (a key, the value it collects, and each attribute in the column of its place): a row that left is
    /// deleted, one that changed rewritten, a new one added, so a build that found the same thing writes nothing and a row
    /// keeps its number. <paramref name="retired"/> are the columns of attributes the dimension no longer declares, which
    /// are emptied. The statement takes <c>@partition</c>, and <c>@rewrite</c>: 1 when the attribute that makes the rows is
    /// no longer the one the table's rows were written with, so no row of it can be matched and all are written again.
    /// </summary>
    internal static string ApplySql(DimensionTableSpec table, IReadOnlyCollection<string> retired)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(retired);
        var target = Qualified(table.Name);
        var attributes = table.Columns.Select((column, index) => (Column: Quoted(column.Name), Slot: $"[{Slot(index + 1)}]", column.Collected)).ToList();
        var collected = attributes.FirstOrDefault(a => a.Collected).Column;

        // A row is a key and the value it collects, each compared exactly, whatever the database compares text by.
        var same = collected is null
            ? "s.[ValueId] = t.[key_id]"
            : $"s.[ValueId] = t.[key_id] AND s.[Part] = ISNULL(t.{collected}, N'') COLLATE {Exact}";
        var sql = new StringBuilder();
        sql.Append(CultureInfo.InvariantCulture, $"IF @rewrite = 1\n    DELETE FROM {target} WHERE [partition] = @partition;\n\n");
        sql.Append(CultureInfo.InvariantCulture, $"DELETE t\nFROM {target} AS t\nWHERE t.[partition] = @partition\n  AND NOT EXISTS (SELECT 1 FROM #DimRow AS s WHERE {same});\n\n");

        sql.Append("UPDATE t SET t.[key] = s.[Key], t.[value] = s.[Value], t.[records] = s.[Records], t.[filter] = s.[Filter]");
        foreach (var attribute in attributes)
        {
            sql.Append(CultureInfo.InvariantCulture, $", t.{attribute.Column} = s.{attribute.Slot}");
        }

        foreach (var column in retired)
        {
            sql.Append(CultureInfo.InvariantCulture, $", t.{Quoted(column)} = NULL");
        }

        sql.Append(CultureInfo.InvariantCulture, $"\nFROM {target} AS t\nINNER JOIN #DimRow AS s ON {same}\nWHERE t.[partition] = @partition\n  AND (EXISTS (\n");
        sql.Append("        SELECT s.[Key], s.[Value], s.[Records], s.[Filter]");
        foreach (var attribute in attributes)
        {
            sql.Append(CultureInfo.InvariantCulture, $", s.{attribute.Slot}");
        }

        sql.Append(CultureInfo.InvariantCulture, $"\n        EXCEPT\n        SELECT t.[key] COLLATE {Exact}, t.[value] COLLATE {Exact}, t.[records], t.[filter] COLLATE {Exact}");
        foreach (var attribute in attributes)
        {
            sql.Append(CultureInfo.InvariantCulture, $", t.{attribute.Column} COLLATE {Exact}");
        }

        sql.Append(')');
        foreach (var column in retired)
        {
            sql.Append(CultureInfo.InvariantCulture, $"\n       OR t.{Quoted(column)} IS NOT NULL");
        }

        sql.Append(");\n\n");

        // New rows take their numbers in the table's own order: by value, then key, then the value collected.
        sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO {target} ([partition], [key_id], [key], [value], [records], [filter]");
        foreach (var attribute in attributes)
        {
            sql.Append(CultureInfo.InvariantCulture, $", {attribute.Column}");
        }

        sql.Append(")\nSELECT @partition, s.[ValueId], s.[Key], s.[Value], s.[Records], s.[Filter]");
        foreach (var attribute in attributes)
        {
            sql.Append(CultureInfo.InvariantCulture, $", s.{attribute.Slot}");
        }

        sql.Append(CultureInfo.InvariantCulture, $"\nFROM #DimRow AS s\nWHERE NOT EXISTS (SELECT 1 FROM {target} AS t WHERE t.[partition] = @partition AND {same})\nORDER BY s.[Value], s.[Key], s.[Part];");
        return sql.ToString();
    }

    /// <summary>The most attributes a build lays out beside a key, which is the most a dimension declares.</summary>
    internal const int Slots = DimensionSpec.MaxAttributes;

    /// <summary>The column of <c>#DimRow</c> the attribute of place <paramref name="ordinal"/> is laid out in, from 1: <c>A01</c> onwards.</summary>
    internal static string Slot(int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ordinal, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ordinal, Slots);
        return string.Create(CultureInfo.InvariantCulture, $"A{ordinal:D2}");
    }

    /// <summary>An identifier in brackets, a closing bracket inside it doubled.</summary>
    internal static string Quoted(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private static SqlDataType Type(string name) => new() { BaseType = name };

    private static SqlDataType Text(int length) => new() { BaseType = "nvarchar", Length = length };

    /// <summary>A flow's name as the ledger keeps it, which is what a dimension is known by.</summary>
    private static string Kept(string flowName)
        => flowName.Length > DeliveryLedger.MaxFlowNameLength ? flowName[..DeliveryLedger.MaxFlowNameLength] : flowName;

    private static string Plain(string text)
    {
        var plain = new StringBuilder(text.Length);
        foreach (var c in text.Trim())
        {
            plain.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        }

        return plain.ToString();
    }
}
