using System.Globalization;
using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// Reads a mapping's <c>record</c> block (docs/mapping-templates.md): a tree laid out the way the rendered record is, in
/// which every property is one of three nodes. A literal is written as the value it is. A value node reads one value
/// with <c>from</c> (a column), <c>value</c> (a literal with settings), <c>cache</c> or <c>search</c>, and takes its
/// settings beside it. A forEach node repeats the rows of a child dataset as the items of an array, each item laid out
/// under <c>item</c>. Columns are named as they are: inside a forEach a name reads the item's row, and
/// <c>dataset.&lt;column&gt;</c> reads the dataset's own row from anywhere.
/// </summary>
internal static partial class MappingMapper
{
    internal const string FromKey = "from";
    internal const string ValueKey = "value";
    internal const string CacheKey = "cache";
    internal const string SearchKey = "search";
    internal const string ForEachKey = "forEach";
    internal const string ItemKey = "item";
    internal const string FindByKey = "findBy";
    internal const string ModifiersKey = "modifiers";
    internal const string WhenKey = "when";
    internal const string RequiredKey = "required";
    internal const string IgnoreSeparatorsKey = "ignoreSeparators";
    internal const string DescriptionKey = "description";

    /// <summary>The keys a value node reads its value with; naming one of them makes a map a value node.</summary>
    internal static readonly IReadOnlyList<string> SourceKeys = [FromKey, ValueKey, CacheKey, SearchKey];

    /// <summary>The settings a value node takes beside the key it reads its value with.</summary>
    internal static readonly IReadOnlyList<string> ValueSettings = [FindByKey, ModifiersKey, WhenKey, RequiredKey, IgnoreSeparatorsKey, DescriptionKey];

    /// <summary>The settings a forEach node takes beside the child dataset it repeats.</summary>
    internal static readonly IReadOnlyList<string> RepeatSettings = [ItemKey, WhenKey, RequiredKey, DescriptionKey];

    /// <summary>Every word the record tree reads as part of a node, so never as the name of a property of the record.</summary>
    internal static readonly IReadOnlyList<string> NodeKeys =
        [FromKey, ValueKey, CacheKey, SearchKey, ForEachKey, ItemKey, FindByKey, ModifiersKey, WhenKey, RequiredKey, IgnoreSeparatorsKey, DescriptionKey];

    /// <summary>The value keys, as a message lists them.</summary>
    private static string SourceList => string.Join(", ", SourceKeys.Take(SourceKeys.Count - 1)) + " or " + SourceKeys[^1];

    /// <summary>What a column a node names reads: the dataset's own row, or the item row of the forEach the node is under.</summary>
    private sealed record TreeScope(string? Child)
    {
        public static TreeScope Root { get; } = new((string?)null);

        public string Rows => Child is null ? "the dataset's own row" : $"the rows of {Child}, which the enclosing forEach repeats";
    }

    /// <summary>Reads the record block into one entry per variable, in document order.</summary>
    private static List<MappingEntry> ReadRecord(Dictionary<string, object?>? record, string source)
    {
        if (record is null || record.Count == 0)
        {
            throw new FlowValidationException(
                $"{source}: 'record' lays out the record the mapping renders, with its acl, legal and data, such as record: {{ data: {{ Name: {{ from: name }} }} }}.");
        }

        var entries = new List<MappingEntry>();
        foreach (var (key, node) in record)
        {
            var location = "record." + key;
            Node(node, [PropertyName(key, $"{source}: {location}")], location, TreeScope.Root, entries, source);
        }

        return entries;
    }

    private static void Node(object? node, IReadOnlyList<string> path, string location, TreeScope scope, List<MappingEntry> entries, string source)
    {
        var at = $"{source}: {location}";
        switch (node)
        {
            case null:
                throw new FlowValidationException($"{at} has no value; give it one, or remove it.");

            case IDictionary<object, object> map when Has(map, ForEachKey):
                Repeat(map, path, location, scope, entries, source);
                return;

            case IDictionary<object, object> map when SourceKeys.Any(key => Has(map, key)):
                entries.Add(Value(map, path, location, scope, entries.Count, source));
                return;

            case IDictionary<object, object> map when map.Count == 0:
                throw new FlowValidationException($"{at} is an empty object; name the properties it holds, or write value: {{}} for an empty object.");

            case IDictionary<object, object> map:
                foreach (var (key, child) in map)
                {
                    var name = KeyText(key);
                    Node(child, [.. path, PropertyName(name, at)], location + "." + name, scope, entries, source);
                }

                return;

            case IEnumerable<object> list:
                if (MappedItem(list, string.Empty) is { } mapped)
                {
                    throw new FlowValidationException(
                        $"{at} is a list, and a list is written whole, as the literal it is; its item {mapped} reads a value. "
                        + $"An array whose items come from rows is a forEach node: {{ {ForEachKey}: <child dataset>, {ItemKey}: {{ ... }} }}.");
                }

                entries.Add(Literal(node, path, location, entries.Count, source));
                return;

            default:
                entries.Add(Literal(node, path, location, entries.Count, source));
                return;
        }
    }

    private static MappingEntry Literal(object node, IReadOnlyList<string> path, string location, int index, string source)
    {
        var at = $"{source}: {location}";
        return new MappingEntry
        {
            Index = index,
            Location = location,
            Target = Target(path, at),
            Static = StaticValue(node, at) ?? throw new FlowValidationException($"{at} has no value; give it one, or remove it."),
        };
    }

    /// <summary>
    /// A forEach node: an entry that repeats the child dataset's rows as the array's items, then the entries of the item,
    /// each reading the item's row. Its own when and required read the row the node is in, since they decide for the whole
    /// array.
    /// </summary>
    private static void Repeat(IDictionary<object, object> map, IReadOnlyList<string> path, string location, TreeScope scope, List<MappingEntry> entries, string source)
    {
        var at = $"{source}: {location}";
        foreach (var key in map.Keys.Select(KeyText))
        {
            if (key == ForEachKey || RepeatSettings.Contains(key))
            {
                continue;
            }

            throw new FlowValidationException(SourceKeys.Contains(key)
                ? $"{at} repeats rows with {ForEachKey} and reads a value with {key}; a node does one of them. What each row gives is a property under {ItemKey}."
                : $"{at}: a {ForEachKey} node takes {ForEachKey}, {SettingList(RepeatSettings)}, not '{key}'.");
        }

        if (scope.Child is not null)
        {
            throw new FlowValidationException(
                $"{at} repeats rows inside the items of the forEach over {scope.Child}; a repeated array inside a repeated item is not supported.");
        }

        var child = Get(map, ForEachKey) is { } named and not (IDictionary<object, object> or IEnumerable<object>) ? ScalarText(named).Trim() : string.Empty;
        if (!NamePattern().IsMatch(child) || child == DatasetColumn.Prefix)
        {
            throw new FlowValidationException(
                $"{at}: {ForEachKey} names the child dataset whose rows become the array's items, such as {ForEachKey}: curves; '{child}' is not one.");
        }

        entries.Add(new MappingEntry
        {
            Index = entries.Count,
            Location = location,
            Target = Target(path, at),
            Source = new MappingSource { Kind = MappingSourceKind.DatasetRows, Child = child },
            AppliesWhen = Has(map, WhenKey) ? Condition(Get(map, WhenKey), scope, at) : null,
            Required = Flag(map, RequiredKey, at) ?? true,
            Description = Text(map, DescriptionKey, at),
        });

        var item = Get(map, ItemKey) switch
        {
            IDictionary<object, object> { Count: > 0 } properties when !Has(properties, ForEachKey) && !SourceKeys.Any(key => Has(properties, key)) => properties,
            IDictionary<object, object> { Count: > 0 } => throw new FlowValidationException(
                $"{at}.{ItemKey} reads one value, and the items of a repeated array are objects: {ItemKey} names each property a row fills, such as {ItemKey}: {{ CurveID: {{ from: curve_id }} }}."),
            null when !Has(map, ItemKey) => throw new FlowValidationException(
                $"{at} repeats the rows of {child} and lays out no item; {ItemKey} names each property a row fills, such as {ItemKey}: {{ CurveID: {{ from: curve_id }} }}."),
            _ => throw new FlowValidationException(
                $"{at}.{ItemKey} names each property a row fills, such as {ItemKey}: {{ CurveID: {{ from: curve_id }} }}."),
        };

        var itemScope = new TreeScope(child);
        var itemPath = path.Take(path.Count - 1).Append(path[^1] + "[]").ToList();
        foreach (var (key, node) in item)
        {
            var name = KeyText(key);
            var itemLocation = $"{location}.{ItemKey}.{name}";
            Node(node, [.. itemPath, PropertyName(name, $"{source}: {location}.{ItemKey}")], itemLocation, itemScope, entries, source);
        }
    }

    /// <summary>A value node: one value read from a column, a literal, the cache or a search, and the settings beside it.</summary>
    private static MappingEntry Value(IDictionary<object, object> map, IReadOnlyList<string> path, string location, TreeScope scope, int index, string source)
    {
        var at = $"{source}: {location}";
        foreach (var key in map.Keys.Select(KeyText))
        {
            if (SourceKeys.Contains(key) || ValueSettings.Contains(key))
            {
                continue;
            }

            throw new FlowValidationException(key == ItemKey
                ? $"{at} names {ItemKey}, which lays out the items of a {ForEachKey} node; add {ForEachKey}: <child dataset>, or remove {ItemKey}."
                : $"{at}: a value node takes {SourceList}, then {SettingList(ValueSettings)}, not '{key}'. To lay out an object instead, write its property names alone.");
        }

        var named = SourceKeys.Where(key => Has(map, key)).ToList();
        if (named.Count > 1)
        {
            throw new FlowValidationException($"{at} reads its value with {string.Join(" and ", named)}; a node reads one of {SourceList}.");
        }

        var target = Target(path, at);
        var description = Text(map, DescriptionKey, at);
        var condition = Has(map, WhenKey) ? Condition(Get(map, WhenKey), scope, at) : null;

        if (named[0] == ValueKey)
        {
            if (new[] { FindByKey, ModifiersKey, RequiredKey, IgnoreSeparatorsKey }.Any(key => Has(map, key)))
            {
                throw new FlowValidationException(
                    $"{at}: a literal value takes only {WhenKey} and {DescriptionKey} beside it; {FindByKey}, {ModifiersKey}, {RequiredKey} and {IgnoreSeparatorsKey} belong to a node that reads the dataset, the cache or a search.");
            }

            return new MappingEntry
            {
                Index = index,
                Location = location,
                Target = target,
                Static = StaticValue(Get(map, ValueKey), at)
                    ?? throw new FlowValidationException($"{at}: {ValueKey} is empty; leave the property out by removing it."),
                AppliesWhen = condition,
                Description = description,
            };
        }

        var read = named[0] switch
        {
            FromKey => new MappingSource { Kind = MappingSourceKind.DatasetColumn, Column = Column(RequiredText(map, FromKey, at), scope, $"{at}: {FromKey}") },
            CacheKey => CacheSource(RequiredText(map, CacheKey, at), at),
            _ => SearchSource(RequiredText(map, SearchKey, at), at),
        };

        if (!read.Resolves && Has(map, FindByKey))
        {
            throw new FlowValidationException($"{at}: {FindByKey} selects the record a {CacheKey} or a {SearchKey} node reads; this node reads the column {Written(read.Column!, scope)}.");
        }

        if (read.Kind != MappingSourceKind.Cache && Has(map, IgnoreSeparatorsKey))
        {
            throw new FlowValidationException($"{at}: {IgnoreSeparatorsKey} loosens how a value is matched against the cache, so it only applies to a {CacheKey} node.");
        }

        var findBy = read.Resolves ? FindByLines(Get(map, FindByKey), read, scope, at) : [];
        if (read.Resolves && findBy.Count == 0)
        {
            throw new FlowValidationException(
                $"{at}: a {read.Prefix} node needs {FindByKey}, which says which record to read, such as {FindByKey}: {FindByExample(read.Prefix)}.");
        }

        var modifiers = Modifiers(Get(map, ModifiersKey), Has(map, ModifiersKey), scope, at);
        if (modifiers.Count > 0 && read.Resolves && findBy.All(f => f.Column is null))
        {
            var found = read.Kind == MappingSourceKind.Search ? "what a search finds is" : "cache values are";
            throw new FlowValidationException($"{at}: modifiers change incoming dataset values, and this node's {FindByKey} reads none; {found} never modified.");
        }

        var ids = modifiers.Count(m => m.Kind == ModifierKind.Id);
        if (ids > 0 && read.Resolves)
        {
            // A resolved node's modifiers change the value its findBy lines compare, and the node gives the record's id itself;
            // an id built there would be compared with the cached or searched field, never written.
            throw new FlowValidationException(
                $"{at}: the id modifier builds the id a node writes from a dataset value, and a {read.Prefix} node already gives what it writes; read the value with {FromKey}: <column>, and build the id from it.");
        }

        if (ids > 1)
        {
            throw new FlowValidationException($"{at}: a node builds one id, and its modifiers list id {ids} times.");
        }

        if (ids == 1 && modifiers[^1].Kind != ModifierKind.Id)
        {
            // A modifier after the id would change the id it built, into one its template does not describe.
            throw new FlowValidationException($"{at}: id builds what the node writes, so it is the last modifier; move {modifiers[^1]} before it.");
        }

        return new MappingEntry
        {
            Index = index,
            Location = location,
            Target = target,
            Source = read,
            FindBy = findBy,
            Modifiers = modifiers,
            AppliesWhen = condition,
            Required = Flag(map, RequiredKey, at) ?? true,
            IgnoreSeparators = Flag(map, IgnoreSeparatorsKey, at) ?? false,
            Description = description,
        };
    }

    /// <summary><c>cache: UnitOfMeasure.id</c>: the cached type, then the field read, or a path into one.</summary>
    private static MappingSource CacheSource(string text, string at)
    {
        var parts = text.Split('.');
        if (parts.Length < 2 || !NamePattern().IsMatch(parts[0]) || parts.Skip(1).Any(p => !FieldPattern().IsMatch(p)))
        {
            throw new FlowValidationException(
                $"{at}: {CacheKey} names the cached type and the field it reads, such as {CacheKey}: UnitOfMeasure.id or {CacheKey}: CurveDictionary.log_curve_type_id; '{text}' is not one.");
        }

        return new MappingSource { Kind = MappingSourceKind.Cache, CacheType = parts[0], CacheField = string.Join('.', parts.Skip(1)) };
    }

    /// <summary><c>search: Wellbore</c>: one of the mapping's searches, which gives the id of the one record it finds.</summary>
    private static MappingSource SearchSource(string text, string at)
    {
        if (!NamePattern().IsMatch(text))
        {
            // A search asks the platform for the id of the record that matches and nothing else, so an answer is one id
            // whatever the mapping reads. What else a record needs comes from the dataset, the cache or a literal.
            throw new FlowValidationException(
                $"{at}: {SearchKey} names one of the mapping's searches, such as {SearchKey}: Wellbore, and gives the id of the record it finds; '{text}' is not a search name.");
        }

        return new MappingSource { Kind = MappingSourceKind.Search, CacheType = text, CacheField = "id" };
    }

    private static string FindByExample(string prefix)
        => prefix == MappingSource.SearchPrefix ? "data.FacilityName = wellbore_uwi" : "Code = unit";

    /// <summary>
    /// A cache or a search node's findBy: one line or a list, each comparing a field of the record the node reads with a
    /// column or a quoted text, tried in order until one finds a record.
    /// </summary>
    private static List<FindBy> FindByLines(object? value, MappingSource read, TreeScope scope, string at)
    {
        var example = FindByExample(read.Prefix);
        List<string> lines = value switch
        {
            null => [],
            string one => [one],
            IEnumerable<object> many => many.Select(line => line as string
                ?? throw new FlowValidationException($"{at}: each {FindByKey} line is text, such as {example}.")).ToList(),
            _ => throw new FlowValidationException($"{at}: {FindByKey} is one line or a list of lines, such as {FindByKey}: {example}."),
        };

        var result = new List<FindBy>(lines.Count);
        foreach (var line in lines)
        {
            var match = FindByPattern().Match(line);
            if (!match.Success)
            {
                throw new FlowValidationException(
                    $"{at}: {FindByKey} '{line}' must read <field> = <column>, or <field> = 'text' for a fixed text, such as {example}.");
            }

            var field = match.Groups["field"].Value;
            if (field.StartsWith(MappingSource.CachePrefix + ".", StringComparison.Ordinal) || field.StartsWith(MappingSource.SearchPrefix + ".", StringComparison.Ordinal))
            {
                throw new FlowValidationException(
                    $"{at}: {FindByKey} '{line}' names '{field}'; a line compares a field of the record the node reads, written without the {read.Prefix} and its name, such as {example}.");
            }

            if (field.Split('.').Any(p => !FieldPattern().IsMatch(p)))
            {
                throw new FlowValidationException($"{at}: {FindByKey} '{line}' names an invalid field '{field}'.");
            }

            // A search compares a property of the records' data, named the way a query names it: unquoted, so only
            // letters, digits and underscores, and never the record's own metadata, which the indexer maps apart from
            // the schema and a lookup has no business comparing.
            if (read.Kind == MappingSourceKind.Search && (!field.StartsWith(SearchDataPrefix, StringComparison.Ordinal) || !OsduPath.IsPath(field)))
            {
                throw new FlowValidationException(
                    $"{at}: {FindByKey} '{line}' compares '{field}', and a search compares a property under data, named by dotted names of letters, digits and underscores, such as {example}.");
            }

            var operand = match.Groups["operand"].Value.Trim();
            if (Quoted(operand) is { } literal)
            {
                if (literal.Length == 0)
                {
                    throw new FlowValidationException($"{at}: {FindByKey} '{line}' compares with empty text.");
                }

                result.Add(new FindBy(read.CacheType!, field, null, literal) { Prefix = read.Prefix });
                continue;
            }

            result.Add(new FindBy(read.CacheType!, field, Column(operand, scope, $"{at}: {FindByKey} '{line}'"), null) { Prefix = read.Prefix });
        }

        return result;
    }

    /// <summary><c>when: log_status is FINAL</c>: a column compared with text, or tested for emptiness.</summary>
    private static EntryCondition Condition(object? value, TreeScope scope, string at)
    {
        var text = value switch
        {
            string written => written,
            null or IDictionary<object, object> or IEnumerable<object> => throw new FlowValidationException(
                $"{at}: {WhenKey} is one line, such as {WhenKey}: log_status is FINAL."),
            _ => ScalarText(value),
        };

        var match = ConditionPattern().Match(text.Trim());
        if (!match.Success)
        {
            throw new FlowValidationException($"{at}: {WhenKey} '{text}' must read <column> is <text>, is not <text>, is empty, or is not empty.");
        }

        var column = Column(match.Groups["column"].Value, scope, $"{at}: {WhenKey} '{text}'");
        var negated = match.Groups["not"].Success;
        var rest = match.Groups["rest"].Value.Trim();
        if (rest == "empty")
        {
            return new EntryCondition(column, negated ? ConditionOperator.IsNotEmpty : ConditionOperator.IsEmpty, null);
        }

        var compared = Quoted(rest) ?? rest;
        if (compared.Length == 0)
        {
            throw new FlowValidationException($"{at}: {WhenKey} '{text}' compares with empty text; write 'is empty' instead.");
        }

        return new EntryCondition(column, negated ? ConditionOperator.IsNot : ConditionOperator.Is, compared);
    }

    /// <summary>A node's modifiers, with the columns an id template names read in the node's scope.</summary>
    private static List<Modifier> Modifiers(object? value, bool written, TreeScope scope, string at)
    {
        if (!written)
        {
            return [];
        }

        if (value is not IEnumerable<object> list)
        {
            throw new FlowValidationException($"{at}: {ModifiersKey} is a list, such as {ModifiersKey}: [trim].");
        }

        var modifiers = new List<Modifier>();
        foreach (var (modifier, i) in list.Select((m, i) => (m, i)))
        {
            var where = $"{at} {ModifiersKey}[{i}]";
            var read = modifier is IDictionary<object, object> map && map.Count == 1 && KeyText(map.Keys.First()) == "id" && map.Values.First() is string template
                ? new Dictionary<object, object> { ["id"] = IdColumns(template, scope, where) }
                : modifier;
            modifiers.Add(ParseModifier(read, where));
        }

        return modifiers;
    }

    /// <summary>
    /// An id template with each column token written the way the loaded model reads it, <c>{dataset.column}</c> or
    /// <c>{dataset.child.column}</c>: a node writes <c>{column}</c> for its own row and <c>{dataset.column}</c> for the
    /// dataset's row. <c>{value}</c>, <c>{param.name}</c> and <c>{cache.Type.field}</c> are kept as they are.
    /// </summary>
    private static string IdColumns(string template, TreeScope scope, string where) => IdToken().Replace(template, token =>
    {
        var name = token.Groups["token"].Value.Trim();
        return name == "value" || name.StartsWith("param.", StringComparison.Ordinal) || name.StartsWith(MappingSource.CachePrefix + ".", StringComparison.Ordinal)
            ? token.Value
            : "{" + Column(name, scope, $"{where}: id token {token.Value}") + "}";
    });

    /// <summary>
    /// A column a node names: <c>column</c> reads the row the node is in (inside a forEach, the item's row), and
    /// <c>dataset.column</c> reads the dataset's own row from anywhere. Inside a forEach, <c>child.column</c> names the
    /// item's row explicitly.
    /// </summary>
    private static DatasetColumn Column(string text, TreeScope scope, string where)
    {
        var parts = text.Trim().Split('.');
        if (parts.Length > 2 || parts.Any(p => !NamePattern().IsMatch(p)))
        {
            throw new FlowValidationException(
                $"{where}: '{text}' is not a column; name a column of {scope.Rows}, such as log_id, or a column of the dataset's own row as dataset.<column>.");
        }

        if (parts.Length == 1)
        {
            return new DatasetColumn(scope.Child, parts[0]);
        }

        if (parts[0] == DatasetColumn.Prefix)
        {
            return new DatasetColumn(null, parts[1]);
        }

        if (parts[0] == scope.Child)
        {
            return new DatasetColumn(scope.Child, parts[1]);
        }

        throw new FlowValidationException(scope.Child is null
            ? $"{where}: '{text}' reads dataset '{parts[0]}', and outside a {ForEachKey} a node reads the dataset's own row; a child dataset's rows are read under the {ForEachKey} that repeats them."
            : $"{where}: '{text}' reads dataset '{parts[0]}', and under the {ForEachKey} over {scope.Child} a node reads {scope.Child}'s row, or the dataset's own row as dataset.<column>.");
    }

    /// <summary>A column as the node that reads it names it.</summary>
    private static string Written(DatasetColumn column, TreeScope scope)
        => column.Child is null && scope.Child is not null ? $"{DatasetColumn.Prefix}.{column.Column}" : column.Column;

    /// <summary>A key naming a property of the record: one name, never a path and never one of the words a node reads.</summary>
    private static string PropertyName(string name, string at)
    {
        if (NodeKeys.Contains(name))
        {
            throw new FlowValidationException(
                $"{at}: '{name}' is a word of a value or a {ForEachKey} node, and this node reads no value: give it {SourceList}, or {ForEachKey}. "
                + $"A record property called '{name}' is written inside a literal object, as {ValueKey}: {{ {name}: ... }}.");
        }

        if (!PropertyPattern().IsMatch(name))
        {
            throw new FlowValidationException(
                $"{at}: '{name}' is not a property name. A key in the record is one property of it, so nest the properties a path names rather than writing the path.");
        }

        return name;
    }

    private static TemplatePath Target(IReadOnlyList<string> path, string at)
        => TemplatePath.TryParse(TemplatePath.Prefix + "." + string.Join('.', path), out var target, out var error)
            ? target!
            : throw new FlowValidationException($"{at}: {error}.");

    /// <summary>Where, below a literal, a value or a forEach node sits (<c>[0].Reviewers</c>), or null when it is literal throughout.</summary>
    private static string? MappedItem(object? node, string at)
    {
        switch (node)
        {
            case IDictionary<object, object> map when Has(map, ForEachKey) || SourceKeys.Any(key => Has(map, key)):
                return at;
            case IDictionary<object, object> map:
                foreach (var (key, value) in map)
                {
                    if (MappedItem(value, $"{at}.{KeyText(key)}") is { } found)
                    {
                        return found;
                    }
                }

                return null;
            case IEnumerable<object> list:
                foreach (var (item, i) in list.Select((item, i) => (item, i)))
                {
                    if (MappedItem(item, $"{at}[{i.ToString(CultureInfo.InvariantCulture)}]") is { } found)
                    {
                        return found;
                    }
                }

                return null;
            default:
                return null;
        }
    }

    private static bool Has(IDictionary<object, object> map, string key) => map.Keys.Any(k => KeyText(k) == key);

    private static object? Get(IDictionary<object, object> map, string key)
    {
        foreach (var (k, value) in map)
        {
            if (KeyText(k) == key)
            {
                return value;
            }
        }

        return null;
    }

    private static bool? Flag(IDictionary<object, object> map, string key, string at) => Get(map, key) switch
    {
        null when !Has(map, key) => null,
        bool flag => flag,
        _ => throw new FlowValidationException($"{at}: {key} is true or false."),
    };

    private static string? Text(IDictionary<object, object> map, string key, string at) => Get(map, key) switch
    {
        null => null,
        IDictionary<object, object> or IEnumerable<object> => throw new FlowValidationException($"{at}: {key} is one text."),
        var value => ScalarText(value),
    };

    private static string RequiredText(IDictionary<object, object> map, string key, string at)
        => Text(map, key, at)?.Trim() is { Length: > 0 } text ? text : throw new FlowValidationException($"{at}: {key} is empty.");

    [GeneratedRegex(@"^[^\.\[\]\s]+$")]
    private static partial Regex PropertyPattern();

    [GeneratedRegex(@"\{(?<token>[^{}]*)\}")]
    private static partial Regex IdToken();
}
