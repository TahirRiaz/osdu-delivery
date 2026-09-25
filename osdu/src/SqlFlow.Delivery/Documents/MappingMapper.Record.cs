using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Delivery.Expressions;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// Reads a mapping's <c>record</c> block (docs/mapping-templates.md): a tree laid out the way the rendered record is. Every
/// word of the mapping language starts with <c>$</c> and every other key is a property of the record, so no template's
/// property name is ever read as the language, and no column's name either. A property is one of four nodes. A literal is
/// written as the value it is. An object is a map of property names. A value node reads one value with <c>$from</c> (a
/// column), <c>$expr</c> (an expression over the row), <c>$value</c> (a literal with settings), <c>$cache</c> or
/// <c>$search</c>, and takes its settings beside it. A <c>$forEach</c> node repeats the rows of a child dataset as the
/// items of an array, each item laid out under <c>$item</c>, and keeps only the rows its <c>$where</c> holds for. A bare column name reads the row the node is in (under a <c>$forEach</c>, the item's row), and
/// <c>$dataset.&lt;column&gt;</c> the dataset's own row from anywhere. A property whose own name starts with <c>$</c> is
/// written with one more: <c>$$name</c>.
/// </summary>
internal static partial class MappingMapper
{
    /// <summary>What starts every word of the mapping language, so a key or a reference that starts with it is never a record property or a column.</summary>
    internal const string Marker = "$";

    internal const string FromKey = "$from";
    internal const string ExprKey = "$expr";
    internal const string ValueKey = "$value";
    internal const string CacheKey = "$cache";
    internal const string SearchKey = "$search";
    internal const string ForEachKey = "$forEach";
    internal const string ItemKey = "$item";
    internal const string FindByKey = "$findBy";
    internal const string ModifiersKey = "$modifiers";
    internal const string WhenKey = "$when";
    internal const string WhereKey = "$where";
    internal const string RequiredKey = "$required";
    internal const string IgnoreSeparatorsKey = "$ignoreSeparators";
    internal const string DescriptionKey = "$description";

    /// <summary>How a column of the dataset's own row is named from anywhere in the tree: <c>$dataset.log_id</c>.</summary>
    internal const string DatasetReference = Marker + DatasetColumn.Prefix;

    /// <summary>How a replace names a table read from the partition's cache: <c>$cache.RecallUnits</c>.</summary>
    internal const string CacheReference = Marker + MappingSource.CachePrefix;

    /// <summary>The keys a value node reads its value with; naming one of them makes a map a value node.</summary>
    internal static readonly IReadOnlyList<string> SourceKeys = [FromKey, ExprKey, ValueKey, CacheKey, SearchKey];

    /// <summary>The settings a value node takes beside the key it reads its value with.</summary>
    internal static readonly IReadOnlyList<string> ValueSettings = [FindByKey, ModifiersKey, WhenKey, RequiredKey, IgnoreSeparatorsKey, DescriptionKey];

    /// <summary>The settings a <c>$forEach</c> node takes beside the child dataset it repeats.</summary>
    internal static readonly IReadOnlyList<string> RepeatSettings = [ItemKey, WhereKey, WhenKey, RequiredKey, DescriptionKey];

    /// <summary>Every word of the mapping language a key of the record tree can be.</summary>
    internal static readonly IReadOnlyList<string> NodeKeys =
        [FromKey, ExprKey, ValueKey, CacheKey, SearchKey, ForEachKey, ItemKey, WhereKey, FindByKey, ModifiersKey, WhenKey, RequiredKey, IgnoreSeparatorsKey, DescriptionKey];

    /// <summary>The value keys, as a message lists them.</summary>
    private static string SourceList => string.Join(", ", SourceKeys.Take(SourceKeys.Count - 1)) + " or " + SourceKeys[^1];

    /// <summary>What a column a node names reads: the dataset's own row, or the item row of the <c>$forEach</c> the node is under.</summary>
    private sealed record TreeScope(string? Child)
    {
        public static TreeScope Root { get; } = new((string?)null);

        public string Rows => Child is null ? "the dataset's own row" : $"the rows of {Child}, which the enclosing {ForEachKey} repeats";
    }

    /// <summary>True for a key that is a word of the mapping language: it starts with <c>$</c>, and not with <c>$$</c>.</summary>
    internal static bool IsNodeKey(string key) => key.StartsWith(Marker, StringComparison.Ordinal) && !key.StartsWith(Marker + Marker, StringComparison.Ordinal);

    /// <summary>The record property a key of the tree names: the key itself, or for <c>$$name</c> the name <c>$name</c>.</summary>
    internal static string PropertyOfKey(string key) => key.StartsWith(Marker + Marker, StringComparison.Ordinal) ? key[1..] : key;

    /// <summary>A record property as a key of the tree writes it: a name that starts with <c>$</c> takes one more.</summary>
    internal static string KeyOfProperty(string property) => property.StartsWith(Marker, StringComparison.Ordinal) ? Marker + property : property;

    /// <summary>Reads the record block into one entry per variable, in document order.</summary>
    private static List<MappingEntry> ReadRecord(Dictionary<string, object?>? record, string source)
    {
        if (record is null || record.Count == 0)
        {
            throw new FlowValidationException(
                $"{source}: 'record' lays out the record the mapping renders, with its acl, legal and data, such as record: {{ data: {{ Name: {{ {FromKey}: name }} }} }}.");
        }

        if (record.Keys.FirstOrDefault(IsNodeKey) is { } word)
        {
            throw new FlowValidationException(
                $"{source}: record holds '{word}', and record itself is the object the mapping renders: its keys are the record's properties (acl, legal, tags, data), and each of them holds a node.");
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

            case IDictionary<object, object> map when map.Count == 0:
                throw new FlowValidationException($"{at} is an empty object; name the properties it holds, or write {{ {ValueKey}: {{}} }} for an empty object.");

            case IDictionary<object, object> map when IsNode(map, at):
                if (Has(map, ForEachKey))
                {
                    Repeat(map, path, location, scope, entries, source);
                    return;
                }

                entries.Add(Value(map, path, location, scope, entries.Count, source));
                return;

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
                        $"{at} is a list, and a list is written whole, as the literal it is; its item {mapped} is a node of the mapping language, which a list does not hold yet. "
                        + $"An array whose items come from rows is a {ForEachKey} node: {{ {ForEachKey}: <child dataset>, {ItemKey}: {{ ... }} }}.");
                }

                entries.Add(Literal(TreeLiteral(list, at), path, location, entries.Count, source));
                return;

            default:
                entries.Add(Literal(TreeLiteral(node, at), path, location, entries.Count, source));
                return;
        }
    }

    /// <summary>
    /// Whether a map is a node of the mapping language: it holds a key that starts with <c>$</c>. A node's keys all do, so a
    /// map mixing them with record properties is refused, as is a word the language does not have.
    /// </summary>
    private static bool IsNode(IDictionary<object, object> map, string at)
    {
        var keys = map.Keys.Select(KeyText).ToList();
        var words = keys.Where(IsNodeKey).ToList();
        if (words.Count == 0)
        {
            return false;
        }

        if (keys.FirstOrDefault(k => !IsNodeKey(k)) is { } property)
        {
            throw new FlowValidationException(
                $"{at} holds {string.Join(" and ", words.Take(2).Select(w => $"'{w}'"))}, words of the mapping language, beside the property '{property}'; a node's keys all start with '{Marker}'. "
                + $"Lay the properties out under a key of their own, or write a property whose name starts with '{Marker}' as {Marker}{Marker}<name>.");
        }

        foreach (var word in words.Where(w => !NodeKeys.Contains(w)))
        {
            throw new FlowValidationException(Nearest(word, NodeKeys) is { } nearest
                ? $"{at}: '{word}' is not a word of the mapping language. Did you mean '{nearest}'? A property whose name starts with '{Marker}' is written {Marker}{Marker}{word[1..]}."
                : $"{at}: '{word}' is not a word of the mapping language, which reads {SourceList} with their settings, or {ForEachKey} and {ItemKey}. A property whose name starts with '{Marker}' is written {Marker}{Marker}{word[1..]}.");
        }

        if (!Has(map, ForEachKey) && !SourceKeys.Any(key => Has(map, key)))
        {
            throw new FlowValidationException(Has(map, ItemKey)
                ? $"{at} lays out {ItemKey}, the items of an array whose rows a {ForEachKey} node repeats; add {ForEachKey}: <child dataset>."
                : $"{at} reads no value: a node reads one with {SourceList}, or repeats the rows of a child dataset with {ForEachKey}, and takes its settings ({string.Join(", ", words)}) beside it.");
        }

        return true;
    }

    private static MappingEntry Literal(JsonNode node, IReadOnlyList<string> path, string location, int index, string source)
        => new()
        {
            Index = index,
            Location = location,
            Target = Target(path, $"{source}: {location}"),
            Static = node,
        };

    /// <summary>
    /// A <c>$forEach</c> node: an entry that repeats the child dataset's rows as the array's items, then the entries of the
    /// item, each reading the item's row. Its own <c>$when</c> and <c>$required</c> read the row the node is in, since they
    /// decide for the whole array; its <c>$where</c> reads each child row, and keeps the rows it holds for.
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
                $"{at} repeats rows inside the items of the {ForEachKey} over {scope.Child}; a repeated array inside a repeated item is not supported.");
        }

        var child = Get(map, ForEachKey) is { } named and not (IDictionary<object, object> or IEnumerable<object>) ? ScalarText(named).Trim() : string.Empty;
        if (!NamePattern().IsMatch(child))
        {
            throw new FlowValidationException(
                $"{at}: {ForEachKey} names the child dataset whose rows become the array's items, as the flow declares it under source.datasets, such as {ForEachKey}: curves; '{child}' is not one.");
        }

        entries.Add(new MappingEntry
        {
            Index = entries.Count,
            Location = location,
            Target = Target(path, at),
            Source = new MappingSource { Kind = MappingSourceKind.DatasetRows, Child = child },
            AppliesWhen = Has(map, WhenKey) ? Condition(Get(map, WhenKey), WhenKey, scope, at) : null,
            RowFilter = Has(map, WhereKey) ? Condition(Get(map, WhereKey), WhereKey, new TreeScope(child), at) : null,
            Required = Flag(map, RequiredKey, at) ?? true,
            Description = Text(map, DescriptionKey, at),
        });

        const string ItemExample = $"{ItemKey}: {{ CurveID: {{ {FromKey}: curve_id }} }}";
        var item = Get(map, ItemKey) switch
        {
            IDictionary<object, object> { Count: > 0 } properties when !properties.Keys.Select(KeyText).Any(IsNodeKey) => properties,
            IDictionary<object, object> { Count: > 0 } properties when properties.Keys.Select(KeyText).All(IsNodeKey) => throw new FlowValidationException(
                $"{at}.{ItemKey} is a node that reads one value, and the items of a repeated array are objects here: {ItemKey} names each property a row fills, such as {ItemExample}. "
                + "An array of values from rows is not supported yet."),
            IDictionary<object, object> { Count: > 0 } properties => throw new FlowValidationException(
                $"{at}.{ItemKey} holds '{properties.Keys.Select(KeyText).First(IsNodeKey)}', a word of the mapping language, beside properties of the item; {ItemKey} names each property a row fills, such as {ItemExample}."),
            null when !Has(map, ItemKey) => throw new FlowValidationException(
                $"{at} repeats the rows of {child} and lays out no item; {ItemKey} names each property a row fills, such as {ItemExample}."),
            _ => throw new FlowValidationException($"{at}.{ItemKey} names each property a row fills, such as {ItemExample}."),
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
                ? $"{at} names {ItemKey}, which lays out the items of a {ForEachKey} node; add {ForEachKey}: <child dataset> and leave out {string.Join(" and ", SourceKeys.Where(k => Has(map, k)))}, or remove {ItemKey}."
                : $"{at}: a value node takes {SourceList}, then {SettingList(ValueSettings)}, not '{key}'.");
        }

        var named = SourceKeys.Where(key => Has(map, key)).ToList();
        if (named.Count > 1)
        {
            throw new FlowValidationException($"{at} reads its value with {string.Join(" and ", named)}; a node reads one of {SourceList}.");
        }

        var target = Target(path, at);
        var description = Text(map, DescriptionKey, at);
        var condition = Has(map, WhenKey) ? Condition(Get(map, WhenKey), WhenKey, scope, at) : null;

        if (named[0] == ValueKey)
        {
            if (new[] { FindByKey, ModifiersKey, RequiredKey, IgnoreSeparatorsKey }.Any(key => Has(map, key)))
            {
                throw new FlowValidationException(
                    $"{at}: a literal {ValueKey} takes only {WhenKey} and {DescriptionKey} beside it; {FindByKey}, {ModifiersKey}, {RequiredKey} and {IgnoreSeparatorsKey} belong to a node that reads the dataset, the cache or a search.");
            }

            var literal = StaticValue(Get(map, ValueKey), at)
                ?? throw new FlowValidationException($"{at}: {ValueKey} is empty; leave the property out by removing it.");
            CheckLiteralTexts(literal, at);
            return new MappingEntry
            {
                Index = index,
                Location = location,
                Target = target,
                Static = literal,
                AppliesWhen = condition,
                Description = description,
            };
        }

        var read = named[0] switch
        {
            FromKey => new MappingSource { Kind = MappingSourceKind.DatasetColumn, Column = Column(RequiredText(map, FromKey, at), scope, $"{at}: {FromKey}") },
            ExprKey => new MappingSource { Kind = MappingSourceKind.Expression, Expression = Expression(Get(map, ExprKey), ExprKey, scope, at) },
            CacheKey => CacheSource(RequiredText(map, CacheKey, at), at),
            _ => SearchSource(RequiredText(map, SearchKey, at), at),
        };

        if (!read.Resolves && Has(map, FindByKey))
        {
            throw new FlowValidationException(read.Kind == MappingSourceKind.Expression
                ? $"{at}: {FindByKey} selects the record a {CacheKey} or a {SearchKey} node reads; this node computes its value with {ExprKey}."
                : $"{at}: {FindByKey} selects the record a {CacheKey} or a {SearchKey} node reads; this node reads the column {Written(read.Column!, scope)}.");
        }

        if (read.Kind != MappingSourceKind.Cache && Has(map, IgnoreSeparatorsKey))
        {
            throw new FlowValidationException($"{at}: {IgnoreSeparatorsKey} loosens how a value is matched against the cache, so it only applies to a {CacheKey} node.");
        }

        var findBy = read.Resolves ? FindByLines(Get(map, FindByKey), read, scope, at) : [];
        if (read.Resolves && findBy.Count == 0)
        {
            throw new FlowValidationException(
                $"{at}: a {Marker}{read.Prefix} node needs {FindByKey}, which says which record to read, such as {FindByKey}: {FindByExample(read.Prefix)}.");
        }

        var modifiers = Modifiers(Get(map, ModifiersKey), Has(map, ModifiersKey), scope, at);
        if (modifiers.Count > 0 && read.Resolves && findBy.All(f => f.Column is null))
        {
            var found = read.Kind == MappingSourceKind.Search ? "what a search finds is" : "cache values are";
            throw new FlowValidationException($"{at}: {ModifiersKey} change incoming dataset values, and this node's {FindByKey} reads none; {found} never modified.");
        }

        // id and ref both build the id a node writes, so the same rules hold for either.
        var builders = modifiers.Where(m => m.BuildsId).ToList();
        if (builders.Count > 0 && read.Resolves)
        {
            // A resolved node's modifiers change the value its findBy lines compare, and the node gives the record's id itself;
            // an id built there would be compared with the cached or searched field, never written.
            throw new FlowValidationException(
                $"{at}: the {Word(builders[0])} modifier builds the id a node writes from a dataset value, and a {Marker}{read.Prefix} node already gives what it writes; read the value with {FromKey}: <column>, and build the id from it.");
        }

        if (builders.Count > 1)
        {
            throw new FlowValidationException(
                $"{at}: a node builds one id, and its {ModifiersKey} list {string.Join(" and ", builders.Select(Word))}; keep one.");
        }

        if (builders.Count == 1 && !modifiers[^1].BuildsId)
        {
            // A modifier after the id would change the id it built, into one its template does not describe.
            throw new FlowValidationException($"{at}: {Word(builders[0])} builds what the node writes, so it is the last modifier; move {modifiers[^1]} before it.");
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

    /// <summary><c>$cache: UnitOfMeasure.id</c>: the cached type, then the field read, or a path into one.</summary>
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

    /// <summary><c>$search: Wellbore</c>: one of the mapping's searches, which gives the id of the one record it finds.</summary>
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
    /// A cache or a search node's <c>$findBy</c>: one line or a list, each comparing a field of the record the node reads
    /// with a column or a quoted text, tried in order until one finds a record.
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
            if (field.StartsWith(Marker, StringComparison.Ordinal)
                || field.StartsWith(MappingSource.CachePrefix + ".", StringComparison.Ordinal)
                || field.StartsWith(MappingSource.SearchPrefix + ".", StringComparison.Ordinal))
            {
                throw new FlowValidationException(
                    $"{at}: {FindByKey} '{line}' names '{field}'; a line compares a field of the record the node reads, written as the record names it, such as {example}.");
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

    /// <summary>
    /// A <c>$when</c> or a <c>$where</c>: an expression that gives true or false, such as <c>log_status = "FINAL"</c> or
    /// <c>not empty(curve_unit)</c>, read in the node's scope.
    /// </summary>
    private static MappingExpression Condition(object? value, string key, TreeScope scope, string at)
        => ReadCondition(ExpressionText(value, key, ConditionExample, at), key, scope.Child, out var problem)
            ?? throw new FlowValidationException($"{at}: {problem}");

    /// <summary><c>$expr: coalesce(log_name, log_source)</c>: a value computed from the row, read in the node's scope.</summary>
    private static MappingExpression Expression(object? value, string key, TreeScope scope, string at)
        => ReadExpression(ExpressionText(value, key, ExpressionExample, at), key, scope.Child, out var problem)
            ?? throw new FlowValidationException($"{at}: {problem}");

    private const string ConditionExample = "log_status = \"FINAL\"";

    private const string ExpressionExample = "coalesce(log_name, log_source)";

    /// <summary>
    /// A condition (<c>$when</c> or <c>$where</c>), or null with why it is refused: it does not read as an expression,
    /// it gives a value rather than true or false, or it reads no column and so decides the same for every row. The
    /// loader and the builder both ask this, so a draft is refused for what the loader would refuse it for.
    /// </summary>
    /// <param name="text">The condition as the tree writes it.</param>
    /// <param name="key">The key it is written under, for the message.</param>
    /// <param name="child">The child dataset whose rows a bare column name reads, or null for the dataset's own row.</param>
    /// <param name="problem">Why it is refused, or null.</param>
    internal static MappingExpression? ReadCondition(string text, string key, string? child, out string? problem)
    {
        if (OldCondition(text) is { } rewritten)
        {
            problem = $"{key} '{text}' is a condition as the mapping language no longer writes it; a condition is an expression now: {key}: {rewritten}";
            return null;
        }

        if (MappingExpression.TryParse(text, child, out var syntax) is not { } expression)
        {
            problem = $"{key} '{text}': {syntax}.";
            return null;
        }

        problem = !expression.IsCondition
            ? $"{key} '{text}' gives a value, and {key} is a condition, which gives true or false: compare the value, such as {key}: {ConditionExample}, or test it, such as {key}: not empty(log_status)."
            : expression.Columns.Count == 0
                ? $"{key} '{text}' reads no column, so it decides the same way for every row; remove it, or compare a column, such as {key}: {ConditionExample}."
                : null;
        return problem is null ? expression : null;
    }

    /// <summary>An expression a <c>$expr</c> node computes its value with, or null with why it is refused (see <see cref="ReadCondition"/>).</summary>
    internal static MappingExpression? ReadExpression(string text, string key, string? child, out string? problem)
    {
        if (MappingExpression.TryParse(text, child, out var syntax) is not { } expression)
        {
            problem = $"{key} '{text}': {syntax}.";
            return null;
        }

        problem = expression.Columns.Count == 0
            ? $"{key} '{text}' reads no column, so every record gets the same value; write it as a literal, whose text reads a parameter as {{{Marker}param.<name>}}."
            : null;
        return problem is null ? expression : null;
    }

    private static string ExpressionText(object? value, string key, string example, string at) => value switch
    {
        string written when !string.IsNullOrWhiteSpace(written) => written.Trim(),
        null or string => throw new FlowValidationException($"{at}: {key} is empty; write the expression, such as {key}: {example}."),
        IDictionary<object, object> or IEnumerable<object> => throw new FlowValidationException(
            $"{at}: {key} is one expression, written as text, such as {key}: {example}."),
        _ => ScalarText(value),
    };

    /// <summary>
    /// The expression a condition in the form the mapping language used to write (<c>log_status is FINAL</c>, <c>flag is
    /// not empty</c>) is written as now, or null when the text is not in that form.
    /// </summary>
    internal static string? OldCondition(string text)
    {
        var match = ConditionPattern().Match(text.Trim());
        if (!match.Success)
        {
            return null;
        }

        var column = match.Groups["column"].Value;
        var negated = match.Groups["not"].Success;
        var rest = match.Groups["rest"].Value.Trim();
        if (rest == "empty")
        {
            return negated ? $"not empty({column})" : $"empty({column})";
        }

        var compared = (Quoted(rest) ?? rest).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"{column} {(negated ? "!=" : "=")} \"{compared}\"";
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

        return list.Select((modifier, i) => ParseModifier(modifier, scope.Child, $"{at} {ModifiersKey}[{i.ToString(CultureInfo.InvariantCulture)}]")).ToList();
    }

    /// <summary>
    /// A column a node names: <c>column</c> reads the row the node is in (under a <c>$forEach</c>, the item's row), and
    /// <c>$dataset.column</c> reads the dataset's own row from anywhere.
    /// </summary>
    private static DatasetColumn Column(string text, TreeScope scope, string where)
    {
        var trimmed = text.Trim();
        if (NamePattern().IsMatch(trimmed))
        {
            return new DatasetColumn(scope.Child, trimmed);
        }

        var parts = trimmed.Split('.');
        if (parts.Length == 2 && parts[0] == DatasetReference && NamePattern().IsMatch(parts[1]))
        {
            return new DatasetColumn(null, parts[1]);
        }

        if (parts.Length == 2 && NamePattern().IsMatch(parts[1]) && (parts[0] == DatasetColumn.Prefix || parts[0] == scope.Child))
        {
            throw new FlowValidationException(parts[0] == DatasetColumn.Prefix
                ? $"{where}: '{text}' reads a column the way the mapping language no longer writes it: a column of the dataset's own row is {DatasetReference}.{parts[1]}, and a column of {scope.Rows} is {parts[1]} alone."
                : $"{where}: '{text}' names {scope.Child} again; under the {ForEachKey} over {scope.Child} a bare name reads its row, so write {parts[1]}.");
        }

        throw new FlowValidationException(
            $"{where}: '{text}' is not a column; name a column of {scope.Rows} as it is, such as log_id, or a column of the dataset's own row as {DatasetReference}.<column>.");
    }

    /// <summary>A column as the node that reads it names it.</summary>
    private static string Written(DatasetColumn column, TreeScope scope)
        => column.Child is null && scope.Child is not null ? $"{DatasetReference}.{column.Column}" : column.Column;

    /// <summary>A key naming a property of the record: one name, never a path, and <c>$$name</c> for a name that starts with <c>$</c>.</summary>
    private static string PropertyName(string key, string at)
    {
        var name = PropertyOfKey(key);
        if (!PropertyPattern().IsMatch(name))
        {
            throw new FlowValidationException(
                $"{at}: '{key}' is not a property name. A key in the record is one property of it, so nest the properties a path names rather than writing the path.");
        }

        return name;
    }

    private static TemplatePath Target(IReadOnlyList<string> path, string at)
        => TemplatePath.TryParse(TemplatePath.Prefix + "." + string.Join('.', path), out var target, out var error)
            ? target!
            : throw new FlowValidationException($"{at}: {error}.");

    /// <summary>Where, below a list, a node of the mapping language sits (<c>[0].Reviewers</c>), or null when it is literal throughout.</summary>
    private static string? MappedItem(object? node, string at)
    {
        switch (node)
        {
            case IDictionary<object, object> map when map.Keys.Select(KeyText).Any(IsNodeKey):
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

    /// <summary>
    /// A literal written in the tree as the value it is: a scalar, or a list whose objects are laid out like the tree's own,
    /// a key <c>$$name</c> naming the property <c>$name</c>. What <c>$value</c> holds is read verbatim instead.
    /// </summary>
    private static JsonNode TreeLiteral(object node, string at)
    {
        var literal = node switch
        {
            IDictionary<object, object> map => new JsonObject(map.Select(kv => new KeyValuePair<string, JsonNode?>(
                PropertyOfKey(KeyText(kv.Key)),
                kv.Value is null ? throw new FlowValidationException($"{at}: '{KeyText(kv.Key)}' in the list has no value.") : TreeLiteral(kv.Value, at)))),
            IEnumerable<object> list => new JsonArray(list.Select(item => item is null
                ? throw new FlowValidationException($"{at}: a list holds an empty item.")
                : TreeLiteral(item, at)).ToArray()),
            _ => StaticValue(node, at) ?? throw new FlowValidationException($"{at} has no value; give it one, or remove it."),
        };

        CheckLiteralTexts(literal, at);
        return literal;
    }

    private static void CheckLiteralTexts(JsonNode literal, string at)
    {
        if (LiteralTokenProblem(literal) is { } problem)
        {
            throw new FlowValidationException($"{at}: {problem}");
        }
    }

    /// <summary>
    /// Why a literal is refused, or null. Its texts read no token of the mapping language but a parameter,
    /// <c>{$param.name}</c>: any other <c>{$...}</c> would be written into the record as it stands, and so would
    /// <c>{param.name}</c> without its marker, neither of which a record means to carry. The builder asks the same of a
    /// draft's literal before it is written.
    /// </summary>
    internal static string? LiteralTokenProblem(JsonNode literal)
    {
        ArgumentNullException.ThrowIfNull(literal);
        foreach (var text in StaticTexts(literal))
        {
            foreach (Match token in LiteralToken().Matches(text))
            {
                if (ParameterToken().IsMatch(token.Value))
                {
                    continue;
                }

                var inner = token.Groups["inner"].Value;
                return token.Groups["marker"].Success
                    ? $"the literal '{text}' holds {token.Value}, and a literal reads only a parameter, {{{Marker}param.<name>}}; read a column's value with {FromKey}, and build an id from values with the id modifier."
                    : $"the literal '{text}' holds {token.Value}; a parameter is read as {{{Marker}{inner}}}, with the mapping's '{Marker}' marker.";
            }
        }

        return null;
    }

    /// <summary>The nearest word to a misspelled one, when it is an obvious near miss: two edits away at most, or the same ignoring case.</summary>
    private static string? Nearest(string written, IEnumerable<string> known)
        => known
            .Select(candidate => (Word: candidate, Distance: string.Equals(candidate, written, StringComparison.OrdinalIgnoreCase) ? 0 : Distance(written, candidate)))
            .Where(candidate => candidate.Distance <= 2)
            .OrderBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.Word, StringComparer.Ordinal)
            .Select(candidate => candidate.Word)
            .FirstOrDefault();

    /// <summary>The edit distance between two words, capped: only near misses are worth suggesting.</summary>
    private static int Distance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 2)
        {
            return int.MaxValue;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
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

    /// <summary>A token in braces, as a label, an id or a literal writes one.</summary>
    [GeneratedRegex(@"\{(?<token>[^{}]*)\}")]
    private static partial Regex BraceToken();

    /// <summary>What reads as a token of the mapping language inside a literal: <c>{$...}</c>, or a parameter written without its marker.</summary>
    [GeneratedRegex(@"\{\s*(?:(?<marker>\$)(?<inner>[^{}]*)|(?<inner>param\.[A-Za-z0-9_]+)\s*)\}")]
    private static partial Regex LiteralToken();

    [GeneratedRegex(MappingEntry.ParameterTokenPattern)]
    private static partial Regex ParameterToken();
}
