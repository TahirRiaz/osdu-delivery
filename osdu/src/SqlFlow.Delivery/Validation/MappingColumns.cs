using System.Text.RegularExpressions;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Validation;

/// <summary>How a mapping entry reads a dataset column.</summary>
public enum ColumnRole
{
    /// <summary>The column is the entry's source: its value, after the entry's modifiers, is what the entry writes.</summary>
    Value,

    /// <summary>The column's value, after the entry's modifiers, is compared with a cached field to find the cached record the entry writes from.</summary>
    FindBy,

    /// <summary>The column decides whether the entry applies.</summary>
    AppliesWhen,
}

/// <summary>One entry reading a column, and how. <see cref="Find"/> is the findBy line a <see cref="ColumnRole.FindBy"/> use compares the column in.</summary>
public sealed record MappingColumnUse(MappingEntry Entry, ColumnRole Role, FindBy? Find = null);

/// <summary>A column of the dataset's row or of a child dataset's row: whether the dataset key or the label reads it, and every entry that does.</summary>
public sealed record MappingColumn(string Name, bool Key, bool Label, IReadOnlyList<MappingColumnUse> Uses);

/// <summary>A child dataset: the repeaters that write one array item per row of it, and the columns read from each row.</summary>
public sealed record MappingChildDataset(string Name, IReadOnlyList<MappingEntry> Repeaters, IReadOnlyList<MappingColumn> Columns)
{
    /// <summary>The names of the columns read from each row, in the order the mapping first reads them.</summary>
    public IReadOnlyList<string> ColumnNames => Columns.Select(c => c.Name).ToList();
}

/// <summary>
/// The source columns a mapping reads (docs/delivery/mapping-templates.md): the record row's, each child dataset's, and the
/// dataset key's, each with what the mapping does with it. It is the column half of a flow's source contract (which columns
/// the flow's ingestion tables have to hold, and which template variable each one fills), and the column list an API
/// submission's records are checked against (docs/stage4-design.md section 4).
/// </summary>
public sealed record MappingSourceColumns(IReadOnlyList<MappingColumn> Record, IReadOnlyList<MappingChildDataset> Datasets, IReadOnlyList<string> Key)
{
    /// <summary>The names of the dataset row's columns: the key's first, then in the order the mapping first reads them.</summary>
    public IReadOnlyList<string> RecordNames => Record.Select(c => c.Name).ToList();
}

public static class MappingColumns
{
    /// <summary>
    /// Every column the mapping reads, per dataset: the dataset key first, then each entry's source, findBy values and
    /// condition in document order, then the label's columns. A child dataset a repeater names is listed even when its item
    /// entries read no column of it. Column names compare without case, and the first spelling is the one kept.
    /// </summary>
    public static MappingSourceColumns Read(MappingDefinition mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        var root = new ColumnsBuilder();
        var children = new Dictionary<string, ChildBuilder>(StringComparer.OrdinalIgnoreCase);
        var childOrder = new List<ChildBuilder>();

        ChildBuilder Child(string name)
        {
            if (!children.TryGetValue(name, out var child))
            {
                child = new ChildBuilder(name);
                children[name] = child;
                childOrder.Add(child);
            }

            return child;
        }

        ColumnBuilder Column(DatasetColumn column) => (column.Child is null ? root : Child(column.Child).Columns).Get(column.Column);

        foreach (var key in mapping.Dataset.Key)
        {
            root.Get(key).Key = true;
        }

        foreach (var entry in mapping.Entries)
        {
            if (entry.IsRepeater)
            {
                Child(entry.Source!.Child!).Repeaters.Add(entry);
            }

            if (entry.Source?.Column is { } source)
            {
                Column(source).Uses.Add(new MappingColumnUse(entry, ColumnRole.Value));
            }

            foreach (var find in entry.FindBy)
            {
                if (find.Column is { } compared)
                {
                    Column(compared).Uses.Add(new MappingColumnUse(entry, ColumnRole.FindBy, find));
                }
            }

            if (entry.AppliesWhen is { } condition)
            {
                Column(condition.Column).Uses.Add(new MappingColumnUse(entry, ColumnRole.AppliesWhen));
            }
        }

        if (mapping.Dataset.Label is { } label)
        {
            foreach (Match token in MappingMapper.LabelToken().Matches(label))
            {
                root.Get(token.Groups["column"].Value[(DatasetColumn.Prefix.Length + 1)..]).Label = true;
            }
        }

        return new MappingSourceColumns(
            root.Build(),
            childOrder.Select(c => new MappingChildDataset(c.Name, [.. c.Repeaters], c.Columns.Build())).ToList(),
            mapping.Dataset.Key);
    }

    /// <summary>The columns of one dataset in the order they are first read, keyed by name without case.</summary>
    private sealed class ColumnsBuilder
    {
        private readonly Dictionary<string, ColumnBuilder> _byName = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<ColumnBuilder> _order = [];

        public ColumnBuilder Get(string name)
        {
            if (!_byName.TryGetValue(name, out var column))
            {
                column = new ColumnBuilder(name);
                _byName[name] = column;
                _order.Add(column);
            }

            return column;
        }

        public IReadOnlyList<MappingColumn> Build() => _order.Select(c => new MappingColumn(c.Name, c.Key, c.Label, [.. c.Uses])).ToList();
    }

    private sealed class ColumnBuilder(string name)
    {
        public string Name { get; } = name;

        public bool Key { get; set; }

        public bool Label { get; set; }

        public List<MappingColumnUse> Uses { get; } = [];
    }

    private sealed class ChildBuilder(string name)
    {
        public string Name { get; } = name;

        public List<MappingEntry> Repeaters { get; } = [];

        public ColumnsBuilder Columns { get; } = new();
    }
}
