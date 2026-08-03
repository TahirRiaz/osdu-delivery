using System.Globalization;
using System.Text;
using System.Xml.Linq;
using SqlFlow.Core;

namespace SqlFlow.Sources.Xml;

/// <summary>
/// Flattens one XML row element into one or more ordered (column, value) rows by walking it against an
/// <see cref="XmlFlattenConfig"/>. The XML counterpart of the JSON flattener: attributes (addressed as
/// <c>/@name</c>) and single child elements become columns, repeating sibling elements follow
/// <see cref="XmlFlattenConfig.RepeatHandling"/> (or explode into rows), excluded subtrees are dropped, and
/// xml-string paths keep a subtree as one XML-fragment column. Values are raw strings; aliased columns
/// coalesce so a missing version-specific path is never null when another supplies a value.
///
/// The flattener is the single source of truth for a record's columns: it is lossless (two distinct paths
/// that would map to the same column name are de-collided deterministically) and it reports the resolved
/// column schema (name, source path, large-text) via <see cref="SchemaColumns"/>. The de-collision is fixed
/// once per record by a schema-view pass (<see cref="NamePlan"/>) and then applied identically to every
/// exploded output row, so the schema view (which binds the load's columns and the formula) and the data
/// view (the streamed cells) always agree - a given source path lands in the same column in both.
/// </summary>
public sealed class XmlPathFlattener
{
    /// <summary>Safety bound on the cross-product size for one row, to fail loudly instead of exhausting memory.</summary>
    public const int MaxRowsPerRecord = 1_000_000;

    private readonly XmlFlattenConfig _config;

    public XmlPathFlattener(XmlFlattenConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
    }

    /// <summary>The schema view of a record: one row carrying every column it can produce (no row multiplication).</summary>
    public IReadOnlyList<KeyValuePair<string, string?>> Flatten(XElement record)
        => SchemaRow(record).ToPairs();

    /// <summary>The resolved output columns of a record (name, source path, large-text), in order. Drives the formula.</summary>
    public IReadOnlyList<XmlFlattenColumn> SchemaColumns(XElement record)
        => SchemaRow(record).ToColumns();

    /// <summary>The data view of a record: the actual output rows (exploded repeating elements multiply them).</summary>
    public IReadOnlyList<IReadOnlyList<KeyValuePair<string, string?>>> FlattenRows(XElement record)
    {
        // Pass 1: the schema traversal fixes the canonical name plan (document order, all siblings folded).
        // Pass 2: the data traversal reuses that same plan, so every exploded row names columns identically to
        // the schema view that bound the load's column set.
        var plan = new NamePlan(_config.AliasTargetColumns());
        FlattenElement(record, string.Empty, 0, [new OrderedRow(plan)], multiply: false);

        var rows = new List<OrderedRow> { new(plan) };
        FlattenElement(record, string.Empty, 0, rows, multiply: true);

        var result = new List<IReadOnlyList<KeyValuePair<string, string?>>>(rows.Count);
        foreach (var row in rows)
        {
            result.Add(row.ToPairs());
        }

        return result;
    }

    private OrderedRow SchemaRow(XElement record)
    {
        var rows = new List<OrderedRow> { new(new NamePlan(_config.AliasTargetColumns())) };
        FlattenElement(record, string.Empty, 0, rows, multiply: false);
        return rows[0];
    }

    private void FlattenElement(XElement element, string path, int depth, List<OrderedRow> rows, bool multiply)
    {
        if (_config.IncludeAttributes)
        {
            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration)
                {
                    continue;
                }

                var attrPath = $"{path}/@{attribute.Name.LocalName}";
                if (!_config.IsExcluded(attrPath) && _config.IsIncluded(attrPath))
                {
                    SetAll(rows, attrPath, attribute.Value, largeText: false);
                }
            }
        }

        var childElements = element.Elements().ToList();

        if (childElements.Count == 0)
        {
            // A leaf element contributes its text. The row root (empty path) with no children falls back to
            // its own element name for the column, and is always emitted (a scalar record is never empty).
            var leafPath = path.Length == 0 ? "/" + element.Name.LocalName : path;
            if (path.Length == 0 || (!_config.IsExcluded(path) && _config.IsIncluded(path)))
            {
                SetAll(rows, leafPath, TextOf(element), largeText: false);
            }

            return;
        }

        // Mixed content: a container element can carry its own direct text alongside child elements. Capture
        // it (under the element's own path) so it is not silently dropped.
        var directText = DirectText(element);
        if (directText.Length > 0)
        {
            var textPath = path.Length == 0 ? "/" + element.Name.LocalName : path;
            if (path.Length == 0 || (!_config.IsExcluded(path) && _config.IsIncluded(path)))
            {
                SetAll(rows, textPath, directText, largeText: false);
            }
        }

        foreach (var group in childElements.GroupBy(e => e.Name.LocalName, StringComparer.Ordinal))
        {
            var childPath = $"{path}/{group.Key}";
            if (_config.IsExcluded(childPath) || !_config.IsIncluded(childPath))
            {
                continue;
            }

            var elements = group.ToList();

            // A kept-as-string subtree, or any subtree whose flatten would land past the depth cap, becomes one
            // XML-fragment column (large text) at this path - in both the schema and the data view.
            if (_config.IsXmlString(childPath) || depth + 1 > _config.MaxDepth)
            {
                SetAll(rows, childPath, SerializeGroup(elements), largeText: true);
                continue;
            }

            // XML cannot tell a single occurrence of a repeating element from a non-repeating one. A lone
            // element is flattened in place EXCEPT when its path is configured to explode: there it must still
            // route through explode (with a [0] subscript) so a field whose element happens to occur once in
            // some records and many times in others always lands in the same column, matching JSON arrays.
            if (elements.Count == 1 && !_config.ShouldExplode(childPath))
            {
                FlattenElement(elements[0], childPath, depth + 1, rows, multiply);
            }
            else
            {
                HandleRepeat(elements, childPath, depth, rows, multiply);
            }
        }
    }

    private void HandleRepeat(List<XElement> elements, string path, int depth, List<OrderedRow> rows, bool multiply)
    {
        if (_config.ShouldExplode(path))
        {
            if (multiply)
            {
                ExplodeCrossProduct(elements, path, depth, rows);
            }
            else
            {
                ExplodeSchema(elements, path, depth, rows);
            }

            return;
        }

        switch (_config.RepeatHandling)
        {
            case XmlRepeatHandling.FirstElement:
                FlattenElement(elements[0], path, depth + 1, rows, multiply);
                break;

            case XmlRepeatHandling.LastElement:
                FlattenElement(elements[^1], path, depth + 1, rows, multiply);
                break;

            case XmlRepeatHandling.Join:
                SetAll(rows, path, string.Join(_config.JoinSeparator, elements.Select(TextOf)), largeText: false);
                break;

            case XmlRepeatHandling.Count:
                SetAll(rows, path, elements.Count.ToString(CultureInfo.InvariantCulture), largeText: false);
                break;

            case XmlRepeatHandling.Skip:
                break;

            // ToXml, and the unreachable Explode-default (an explode path never reaches here), keep the
            // repeating elements as one XML-fragment column (large text).
            default:
                SetAll(rows, path, SerializeGroup(elements), largeText: true);
                break;
        }
    }

    /// <summary>Schema view of an exploded repeating element: fold every element into the row(s) without multiplying.</summary>
    private void ExplodeSchema(List<XElement> elements, string path, int depth, List<OrderedRow> rows)
    {
        for (var i = 0; i < elements.Count; i++)
        {
            FlattenElement(elements[i], $"{path}[{i}]", depth + 1, rows, multiply: false);
        }
    }

    /// <summary>Data view of an exploded repeating element: one output row per element (cross-product). Empty keeps the parent.</summary>
    private void ExplodeCrossProduct(List<XElement> elements, string path, int depth, List<OrderedRow> rows)
    {
        if (elements.Count == 0)
        {
            return;
        }

        var original = new List<OrderedRow>(rows);
        rows.Clear();

        for (var i = 0; i < elements.Count; i++)
        {
            var clones = new List<OrderedRow>(original.Count);
            foreach (var row in original)
            {
                clones.Add(row.Clone());
            }

            FlattenElement(elements[i], $"{path}[{i}]", depth + 1, clones, multiply: true);
            rows.AddRange(clones);

            if (rows.Count > MaxRowsPerRecord)
            {
                throw new SqlFlowException(
                    $"Exploding '{path}' produced more than {MaxRowsPerRecord} rows for a single record. "
                    + "Narrow the explode paths or pre-split the data.");
            }
        }
    }

    /// <summary>
    /// Writes one source path's value into every row. A genuine alias source coalesces into the shared union
    /// column it feeds (never overwriting a non-null with null); any other path takes the de-collided column
    /// the shared name plan assigns to it, so the same path always lands in the same column across rows.
    /// </summary>
    private void SetAll(List<OrderedRow> rows, string rawPath, string? value, bool largeText)
    {
        var baseName = _config.ColumnName(rawPath);
        var normalizedPath = XmlFlattenConfig.NormalizeIndices(rawPath);
        var aliasSource = _config.IsAliased(rawPath);
        var normalizedValue = string.IsNullOrEmpty(value) ? null : value;

        foreach (var row in rows)
        {
            row.Write(baseName, normalizedPath, normalizedValue, largeText, aliasSource);
        }
    }

    /// <summary>Compact XML fragment for a kept subtree: the elements' raw XML, concatenated.</summary>
    private static string SerializeGroup(List<XElement> elements)
    {
        if (elements.Count == 1)
        {
            return elements[0].ToString(SaveOptions.DisableFormatting);
        }

        var sb = new StringBuilder();
        foreach (var element in elements)
        {
            sb.Append(element.ToString(SaveOptions.DisableFormatting));
        }

        return sb.ToString();
    }

    private string TextOf(XElement element) => _config.TrimText ? element.Value.Trim() : element.Value;

    /// <summary>The element's own direct text (and CDATA), excluding descendant elements' text. For mixed content.</summary>
    private string DirectText(XElement element)
    {
        var sb = new StringBuilder();
        foreach (var node in element.Nodes())
        {
            if (node is XText text)
            {
                sb.Append(text.Value);
            }
        }

        return _config.TrimText ? sb.ToString().Trim() : sb.ToString();
    }

    /// <summary>
    /// The per-record assignment of a normalized source path to its final, de-collided column name, shared by
    /// the schema and data passes. Built once during the schema traversal (document order) so the de-collision
    /// of two distinct paths that map to the same base name is identical for every exploded output row.
    /// Alias-source writes do not own a name here; they reserve their union-column name so a distinct natural
    /// field colliding on it is suffixed away instead of merged.
    /// </summary>
    private sealed class NamePlan
    {
        private readonly Dictionary<string, string> _nameByPath = new(StringComparer.Ordinal);
        private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Seeds the plan with the alias-target column names so a natural field colliding with one always
        /// de-collides into its own column, independent of whether the alias source precedes or follows it.
        /// </summary>
        public NamePlan(IEnumerable<string> reservedNames)
        {
            foreach (var name in reservedNames)
            {
                _used.Add(name);
            }
        }

        /// <summary>Marks an alias union-column name as taken so natural collisions on it de-collide.</summary>
        public void Reserve(string name) => _used.Add(name);

        /// <summary>The final column name for a normalized path: its first assignment, de-collided with a suffix.</summary>
        public string Resolve(string normalizedPath, string baseName)
        {
            if (_nameByPath.TryGetValue(normalizedPath, out var existing))
            {
                return existing;
            }

            var name = baseName;
            var suffix = 2;
            while (!_used.Add(name))
            {
                name = baseName + "_" + suffix.ToString(CultureInfo.InvariantCulture);
                suffix++;
            }

            _nameByPath[normalizedPath] = name;
            return name;
        }
    }

    /// <summary>
    /// An insertion-ordered, case-insensitive accumulator (matching SQL Server collation and the base
    /// pipeline). Column names come from the shared <see cref="NamePlan"/>, so distinct source paths never
    /// overwrite each other and the same path always folds to one column; each column also carries its source
    /// path and large-text flag for the schema view.
    /// </summary>
    private sealed class OrderedRow
    {
        private readonly NamePlan _plan;
        private readonly List<string> _order;
        private readonly Dictionary<string, string?> _values;
        private readonly Dictionary<string, ColumnMeta> _meta;

        public OrderedRow(NamePlan plan)
        {
            _plan = plan;
            _order = [];
            _values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            _meta = new Dictionary<string, ColumnMeta>(StringComparer.OrdinalIgnoreCase);
        }

        private OrderedRow(NamePlan plan, List<string> order, Dictionary<string, string?> values, Dictionary<string, ColumnMeta> meta)
        {
            _plan = plan;
            _order = order;
            _values = values;
            _meta = meta;
        }

        public void Write(string baseName, string normalizedPath, string? value, bool largeText, bool aliasSource)
        {
            if (aliasSource)
            {
                // Keep natural collisions off this union column, then coalesce: add, or upgrade a null, but
                // never overwrite a non-null with null.
                _plan.Reserve(baseName);
                if (_values.TryGetValue(baseName, out var existing))
                {
                    if (existing is null && value is not null)
                    {
                        _values[baseName] = value;
                    }
                }
                else
                {
                    _values[baseName] = value;
                    _order.Add(baseName);
                    _meta[baseName] = new ColumnMeta(normalizedPath, largeText);
                }

                return;
            }

            var name = _plan.Resolve(normalizedPath, baseName);
            if (_values.TryAdd(name, value))
            {
                _order.Add(name);
                _meta[name] = new ColumnMeta(normalizedPath, largeText);
            }
            else
            {
                // Same path written again (e.g. each element of an exploded repeat folds to one column).
                _values[name] = value;
            }
        }

        public OrderedRow Clone() => new(
            _plan,
            [.. _order],
            new Dictionary<string, string?>(_values, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, ColumnMeta>(_meta, StringComparer.OrdinalIgnoreCase));

        public IReadOnlyList<KeyValuePair<string, string?>> ToPairs()
        {
            var pairs = new List<KeyValuePair<string, string?>>(_order.Count);
            foreach (var name in _order)
            {
                pairs.Add(new KeyValuePair<string, string?>(name, _values[name]));
            }

            return pairs;
        }

        public IReadOnlyList<XmlFlattenColumn> ToColumns()
        {
            var columns = new List<XmlFlattenColumn>(_order.Count);
            foreach (var name in _order)
            {
                var meta = _meta[name];
                columns.Add(new XmlFlattenColumn(name, meta.SourcePath, meta.LargeText));
            }

            return columns;
        }

        private readonly record struct ColumnMeta(string SourcePath, bool LargeText);
    }
}
