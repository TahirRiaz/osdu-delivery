using System.Globalization;
using System.Text;

namespace SqlFlow.Delivery.Source;

/// <summary>One child dataset as the SQL builders see it: where it is, how it joins its record, and how its rows are ordered.</summary>
/// <param name="Name">The name a mapping reads the dataset under.</param>
/// <param name="Object">The dataset's table.</param>
/// <param name="Join">Child column to record key column, in record key order.</param>
/// <param name="OrderBy">The columns child rows are ordered by within a record.</param>
/// <param name="Updated">The dataset table's update column, when it carries one.</param>
/// <param name="Deleted">The dataset table's soft-delete column, when it carries one.</param>
/// <param name="MaxRowsPerRecord">The most rows one record may carry.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming", "CA1720:Identifier contains type name",
    Justification = "The flow document's key is 'object'; the layout mirrors the document so a message about it names what the author wrote.")]
public sealed record IngestionDataset(
    string Name,
    SourceObjectName Object,
    IReadOnlyList<KeyValuePair<string, string>> Join,
    IReadOnlyList<string> OrderBy,
    string? Updated,
    string? Deleted,
    int MaxRowsPerRecord);

/// <summary>
/// A flow's source as the SQL builders see it, once the flow's declarations have been checked against the tables: the record
/// table with its key columns and their SQL types, the system columns the tables actually carry, the scope predicate and the
/// child datasets.
/// </summary>
public sealed record IngestionLayout
{
    /// <summary>The record table.</summary>
    public required SourceObjectName Record { get; init; }

    /// <summary>The record key columns in key order, with the SQL type each holds.</summary>
    public required IReadOnlyList<SourceKeyColumn> Key { get; init; }

    /// <summary>The record table's update column, which an incremental read windows on.</summary>
    public required string Updated { get; init; }

    /// <summary>The record table's soft-delete column, when it carries one.</summary>
    public string? Deleted { get; init; }

    /// <summary>The scope predicate: record column to the run parameter that fills it, in declaration order.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Scope { get; init; } = [];

    /// <summary>The child datasets, in name order.</summary>
    public IReadOnlyList<IngestionDataset> Datasets { get; init; } = [];
}

/// <summary>
/// The SQL an ingestion read sends (docs/stage4-design.md sections 2.2 and 2.3). Every identifier a flow declares is
/// bracket-quoted through <see cref="SourceObjectName"/>, and every value a run supplies (the window, the scope, the keys,
/// the page bounds) goes as a parameter, so a column or table name can never carry a fragment of the statement.
/// </summary>
public static class IngestionSql
{
    /// <summary>The parameter holding the window's exclusive lower bound.</summary>
    public const string LowerParameter = "@lower";

    /// <summary>The parameter holding the window's inclusive upper bound.</summary>
    public const string UpperParameter = "@upper";

    /// <summary>The parameter holding the page size.</summary>
    public const string PageParameter = "@page";

    /// <summary>The parameter holding a JSON array of key tuples (each an array of the key parts, in key order).</summary>
    public const string KeysParameter = "@keys";

    /// <summary>The parameter holding how many candidate records one slice holds.</summary>
    public const string SliceSizeParameter = "@size";

    /// <summary>The prefix of the parameters holding the key the page resumes after.</summary>
    public const string AfterPrefix = "@a";

    /// <summary>The prefix of the parameters holding a key range's exclusive lower bound.</summary>
    public const string FromPrefix = "@f";

    /// <summary>The prefix of the parameters holding a key range's inclusive upper bound.</summary>
    public const string ToPrefix = "@t";

    /// <summary>The prefix of the parameters holding the scope values.</summary>
    public const string ScopePrefix = "@scope";

    /// <summary>The parameter naming the table whose columns are read.</summary>
    public const string ObjectParameter = "@object";

    /// <summary>Which bounds a candidate query carries beyond its selection.</summary>
    /// <param name="After">A keyset bound: only keys above the page's last key.</param>
    /// <param name="From">A slice's exclusive lower bound.</param>
    /// <param name="To">A slice's inclusive upper bound.</param>
    public readonly record struct KeyBounds(bool After, bool From, bool To)
    {
        /// <summary>No bounds: the whole candidate set.</summary>
        public static KeyBounds None => default;
    }

    /// <summary>The columns and types of one table, from the database that holds it.</summary>
    public static string Columns(SourceObjectName table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var database = SourceObjectName.Quote(table.Database);
        return $"""
            SELECT c.[name] AS column_name, t.[name] AS type_name, c.max_length, c.[precision], c.scale, c.is_nullable
            FROM {database}.sys.columns c
            INNER JOIN {database}.sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.[object_id] = OBJECT_ID({ObjectParameter})
            ORDER BY c.column_id;
            """;
    }

    /// <summary>The moment a read fixes its window at, as the source database keeps time.</summary>
    public static string UpperBound() => "SELECT SYSUTCDATETIME();";

    /// <summary>
    /// Which of the named keys the record table holds, and whether each is in this run's scope. A key the query does not
    /// return is missing; one it returns with <c>in_scope</c> 0 belongs to another scope's run.
    /// </summary>
    public static string KeyProbe(IngestionLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var scope = ScopePredicate(layout, "r");
        return $"""
            SELECT {KeyList(layout, "r")}, CAST(CASE WHEN {(scope.Length == 0 ? "1 = 1" : scope)} THEN 1 ELSE 0 END AS int) AS in_scope
            FROM {layout.Record.Quoted} r
            INNER JOIN OPENJSON({KeysParameter}) WITH ({OpenJsonColumns(layout)}) j ON {JoinOn(layout, "r", "j")};
            """;
    }

    /// <summary>
    /// One record of this run's scope whose key is unknown, or nothing when every key is known. SQLFlow's ingestion
    /// creates a target's data columns nullable and never tightens them, so a delivery cannot ask its key columns to be
    /// declared NOT NULL; what it needs is that no row it would deliver actually carries a NULL there. Only the columns
    /// that can hold one are tested, and only within the run's scope, so another scope's rows never refuse this run.
    /// </summary>
    public static string NullKeyProbe(IngestionLayout layout, IReadOnlyList<string> nullableKeys)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(nullableKeys);
        if (nullableKeys.Count == 0)
        {
            throw new ArgumentException("A null-key probe needs at least one column that can hold a null.", nameof(nullableKeys));
        }

        var scope = ScopePredicate(layout, "r");
        var unknown = string.Join(" OR ", nullableKeys.Select(k => $"r.{SourceObjectName.Quote(k)} IS NULL"));
        return $"""
            SELECT TOP (1) {string.Join(", ", nullableKeys.Select(k => $"CAST(CASE WHEN r.{SourceObjectName.Quote(k)} IS NULL THEN 1 ELSE 0 END AS int) AS {SourceObjectName.Quote(k)}"))}
            FROM {layout.Record.Quoted} r
            WHERE {(scope.Length == 0 ? "1 = 1" : scope)} AND ({unknown});
            """;
    }

    /// <summary>How many records this selection is expected to meet.</summary>
    public static string CandidateCount(IngestionLayout layout, SourceSelectionKind kind, bool hasLower)
        => $"SELECT COUNT_BIG(*) FROM ({CandidateSet(layout, kind, hasLower, KeyBounds.None)}) q;";

    /// <summary>One page of candidate record keys, in key order, resuming after the previous page's last key.</summary>
    public static string CandidatePage(IngestionLayout layout, SourceSelectionKind kind, bool hasLower, KeyBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return $"""
            SELECT TOP ({PageParameter}) {KeyList(layout, "q")}
            FROM ({CandidateSet(layout, kind, hasLower, bounds)}) q
            ORDER BY {KeyList(layout, "q")};
            """;
    }

    /// <summary>
    /// The keys that cut the candidate set into slices: every <c>@size</c>-th key in key order, which become the slices'
    /// inclusive upper bounds (the last slice runs to the end of the key space).
    /// </summary>
    public static string SliceBounds(IngestionLayout layout, SourceSelectionKind kind, bool hasLower)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return $"""
            SELECT {KeyList(layout, "b")}
            FROM (
                SELECT {KeyList(layout, "q")}, ROW_NUMBER() OVER (ORDER BY {KeyList(layout, "q")}) AS rn
                FROM ({CandidateSet(layout, kind, hasLower, KeyBounds.None)}) q
            ) b
            WHERE b.rn % {SliceSizeParameter} = 0
            ORDER BY b.rn;
            """;
    }

    /// <summary>
    /// The rows of one page: the page's keys into a table variable, then the record rows, then one result set per child
    /// dataset, each joined on the keys, ordered so a reader can group child rows by record in one forward pass.
    /// </summary>
    public static string PageRows(IngestionLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var columns = string.Join(", ", layout.Key.Select(k => $"{SourceObjectName.Quote(k.Name)} {k.SqlType} NOT NULL"));
        var names = string.Join(", ", layout.Key.Select(k => SourceObjectName.Quote(k.Name)));
        var sql = new StringBuilder();
        sql.Append("DECLARE @page_keys TABLE (").Append(columns).AppendLine(");");
        sql.Append("INSERT INTO @page_keys (").Append(names).Append(") SELECT ").Append(names)
            .Append(" FROM OPENJSON(").Append(KeysParameter).Append(") WITH (").Append(OpenJsonColumns(layout)).AppendLine(");");
        sql.Append("SELECT r.* FROM ").Append(layout.Record.Quoted).Append(" r INNER JOIN @page_keys k ON ").Append(JoinOn(layout, "r", "k"))
            .Append(" ORDER BY ").Append(KeyList(layout, "r")).AppendLine(";");

        foreach (var dataset in layout.Datasets)
        {
            var on = string.Join(" AND ", dataset.Join.Select(j => $"c.{SourceObjectName.Quote(j.Key)} = k.{SourceObjectName.Quote(j.Value)}"));
            var order = dataset.Join.Select(j => $"c.{SourceObjectName.Quote(j.Key)}")
                .Concat(dataset.OrderBy.Select(o => $"c.{SourceObjectName.Quote(o)}"));
            sql.Append("SELECT c.* FROM ").Append(dataset.Object.Quoted).Append(" c INNER JOIN @page_keys k ON ").Append(on);
            if (dataset.Deleted is { } deleted)
            {
                sql.Append(" WHERE c.").Append(SourceObjectName.Quote(deleted)).Append(" IS NULL");
            }

            sql.Append(" ORDER BY ").Append(string.Join(", ", order)).AppendLine(";");
        }

        return sql.ToString();
    }

    /// <summary>The distinct record keys a selection covers, as a derived table whose columns are the key columns.</summary>
    internal static string CandidateSet(IngestionLayout layout, SourceSelectionKind kind, bool hasLower, KeyBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (kind == SourceSelectionKind.Keys)
        {
            return $"""
                SELECT DISTINCT {KeyList(layout, "r")}
                FROM {layout.Record.Quoted} r
                INNER JOIN OPENJSON({KeysParameter}) WITH ({OpenJsonColumns(layout)}) j ON {JoinOn(layout, "r", "j")}
                {Where(layout, "r", null, bounds)}
                """;
        }

        var window = kind == SourceSelectionKind.Incremental;
        var records = $"""
            SELECT DISTINCT {KeyList(layout, "r")}
            FROM {layout.Record.Quoted} r
            {Where(layout, "r", window ? WindowPredicate("r", layout.Updated, hasLower) : null, bounds)}
            """;
        if (!window)
        {
            return records;
        }

        // A record whose own row did not change in the window is still a candidate when one of its child rows did, or when
        // a child row was soft-deleted in it: the record's document is built from those rows as well.
        var parts = new List<string> { records };
        foreach (var dataset in layout.Datasets.Where(d => d.Updated is not null || d.Deleted is not null))
        {
            var changed = new List<string>();
            if (dataset.Updated is { } updated)
            {
                changed.Add(WindowPredicate("c", updated, hasLower));
            }

            if (dataset.Deleted is { } deleted)
            {
                changed.Add(WindowPredicate("c", deleted, hasLower));
            }

            var on = string.Join(" AND ", dataset.Join.Select(j => $"c.{SourceObjectName.Quote(j.Key)} = r.{SourceObjectName.Quote(j.Value)}"));
            parts.Add($"""
                SELECT DISTINCT {KeyList(layout, "r")}
                FROM {layout.Record.Quoted} r
                INNER JOIN {dataset.Object.Quoted} c ON {on}
                {Where(layout, "r", "(" + string.Join(" OR ", changed.Select(c => "(" + c + ")")) + ")", bounds)}
                """);
        }

        return string.Join(Environment.NewLine + "UNION" + Environment.NewLine, parts);
    }

    private static string Where(IngestionLayout layout, string alias, string? changed, KeyBounds bounds)
    {
        var predicates = new List<string>();
        if (changed is not null)
        {
            predicates.Add(changed);
        }

        var scope = ScopePredicate(layout, alias);
        if (scope.Length > 0)
        {
            predicates.Add(scope);
        }

        if (bounds.After)
        {
            predicates.Add(Above(layout, alias, AfterPrefix));
        }

        if (bounds.From)
        {
            predicates.Add(Above(layout, alias, FromPrefix));
        }

        if (bounds.To)
        {
            predicates.Add(AtMost(layout, alias, ToPrefix));
        }

        return predicates.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", predicates);
    }

    private static string WindowPredicate(string alias, string column, bool hasLower)
    {
        var quoted = $"{alias}.{SourceObjectName.Quote(column)}";
        return hasLower
            ? $"{quoted} > {LowerParameter} AND {quoted} <= {UpperParameter}"
            : $"{quoted} <= {UpperParameter}";
    }

    private static string ScopePredicate(IngestionLayout layout, string alias)
        => string.Join(" AND ", layout.Scope.Select((s, i) => $"{alias}.{SourceObjectName.Quote(s.Key)} = {ScopePrefix}{i.ToString(CultureInfo.InvariantCulture)}"));

    /// <summary>The key of a row is above the key the parameters hold, comparing part by part in key order.</summary>
    private static string Above(IngestionLayout layout, string alias, string prefix) => Compare(layout, alias, prefix, ">");

    /// <summary>The key of a row is at most the key the parameters hold.</summary>
    private static string AtMost(IngestionLayout layout, string alias, string prefix) => Compare(layout, alias, prefix, "<", orEqual: true);

    private static string Compare(IngestionLayout layout, string alias, string prefix, string op, bool orEqual = false)
    {
        var text = new StringBuilder();
        for (var i = 0; i < layout.Key.Count; i++)
        {
            var column = $"{alias}.{SourceObjectName.Quote(layout.Key[i].Name)}";
            var parameter = prefix + i.ToString(CultureInfo.InvariantCulture);
            var last = i == layout.Key.Count - 1;
            text.Append('(').Append(column).Append(' ').Append(op).Append(last && orEqual ? "= " : " ").Append(parameter);
            if (!last)
            {
                text.Append(" OR (").Append(column).Append(" = ").Append(parameter).Append(" AND ");
            }
        }

        for (var i = 0; i < layout.Key.Count - 1; i++)
        {
            text.Append("))");
        }

        text.Append(')');
        return text.ToString();
    }

    private static string KeyList(IngestionLayout layout, string alias)
        => string.Join(", ", layout.Key.Select(k => $"{alias}.{SourceObjectName.Quote(k.Name)}"));

    private static string JoinOn(IngestionLayout layout, string left, string right)
        => string.Join(" AND ", layout.Key.Select(k => $"{left}.{SourceObjectName.Quote(k.Name)} = {right}.{SourceObjectName.Quote(k.Name)}"));

    private static string OpenJsonColumns(IngestionLayout layout)
        => string.Join(", ", layout.Key.Select((k, i) => $"{SourceObjectName.Quote(k.Name)} {k.SqlType} '$[{i.ToString(CultureInfo.InvariantCulture)}]'"));
}
