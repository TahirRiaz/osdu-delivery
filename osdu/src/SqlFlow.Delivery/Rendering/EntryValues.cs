using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Rendering;

/// <summary>
/// The value of one mapping entry for one row (docs/delivery/mapping-templates.md): the condition, the source or static
/// value, the modifiers, the cache lookup, what an empty value does, and the conversion to the template variable's type.
/// A value that cannot be produced either leaves the variable out or holds the record with a reason naming the target.
/// </summary>
internal static partial class EntryValues
{
    /// <summary>The value of <paramref name="entry"/> for the row, converted to its variable's type, or null when the variable is left out.</summary>
    public static JsonNode? Evaluate(
        MappingEntry entry, SourceRow root, SourceRow? item, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages, SearchTrail searched)
    {
        if (entry.AppliesWhen is { } condition && !Applies(condition, root, item))
        {
            return null;
        }

        var path = entry.Target.Text;
        object? raw;
        if (entry.Static is { } fixedValue)
        {
            raw = Expand(fixedValue, renderer);
        }
        else if (entry.Source!.Kind == MappingSourceKind.DatasetColumn)
        {
            var column = entry.Source.Column!;
            if (!TryModify(entry.Modifiers, Read(column, root, item), path, holds, out raw))
            {
                return null;
            }

            if (IsEmpty(raw))
            {
                if (entry.Required)
                {
                    holds.Add($"{path}: {column} is empty, and the entry is required");
                }

                return null;
            }
        }
        else if (entry.Source!.Kind == MappingSourceKind.Search)
        {
            raw = Searched(entry, root, item, renderer, holds, searched);
            if (raw is null)
            {
                return null;
            }
        }
        else
        {
            raw = Cached(entry, root, item, renderer, holds, usages);
            if (raw is null)
            {
                return null;
            }
        }

        return raw is null ? null : Convert(raw, renderer.Schema.Resolve(entry.Target.SchemaPath), path, holds);
    }

    /// <summary>
    /// The value of <paramref name="entry"/> in a record's shape, without a row: a static value converted exactly as a
    /// render converts it, or a placeholder naming the type the template gives the variable and where the value comes from
    /// (a list of one where the variable is a list of values). A value that could not be written whatever the row, a single
    /// value on an object, is noted and left out, as a render holds it.
    /// </summary>
    public static JsonNode? Describe(MappingEntry entry, MappingRenderer renderer, List<string> notes)
    {
        var path = entry.Target.Text;
        var property = renderer.Schema.Resolve(entry.Target.SchemaPath);
        if (entry.Static is { } fixedValue)
        {
            return Convert(Expand(fixedValue, renderer), property, path, notes);
        }

        var type = property?.Type ?? SchemaType.Any;
        switch (type)
        {
            case SchemaType.String:
                return JsonValue.Create(Placeholder(entry, property!.Format is { } format ? $"{format} string" : Name(type)));
            case SchemaType.Number:
            case SchemaType.Integer:
            case SchemaType.Boolean:
                return JsonValue.Create(Placeholder(entry, Name(type)));
            case SchemaType.Any:
                return JsonValue.Create(Placeholder(entry, "value"));
        }

        if (type == SchemaType.Array && property!.ItemScalarType is { } itemType && itemType != SchemaType.Object)
        {
            var itemFormat = property?.ItemFormat;
            return new JsonArray(JsonValue.Create(Placeholder(entry, itemFormat is not null ? $"{itemFormat} string" : Name(itemType))));
        }

        // An object, or a list of objects: a cached field can carry one whole, and a dataset column never can.
        if (entry.Source!.Kind == MappingSourceKind.Cache)
        {
            return JsonValue.Create(Placeholder(entry, Name(type)));
        }

        notes.Add($"{path}: a single value from {entry.Source} cannot be written where the template takes an {Name(type)}");
        return null;
    }

    /// <summary>A placeholder for a value read from a row or the cache: <c>&lt;number from dataset.depth | trim, optional&gt;</c>.</summary>
    private static string Placeholder(MappingEntry entry, string type)
    {
        var source = entry.Source!;
        var origin = source.Resolves
            ? entry.FindBy.Count == 0 ? source.ToString() : $"{source} by {FindText(entry)}"
            : entry.Modifiers.Count == 0 ? source.ToString() : $"{source} | {ModifierText(entry.Modifiers)}";
        var optional = entry.Required ? string.Empty : ", optional";
        var when = entry.AppliesWhen is { } condition ? $", when {condition}" : string.Empty;
        return $"<{type} from {origin}{optional}{when}>";
    }

    /// <summary>
    /// A cache source's findBy lines as one phrase, consecutive lines comparing the same value run together
    /// (<c>Code/Name = (dataset.unit | trim)</c>); the modifiers change the value compared, so they sit with it.
    /// </summary>
    private static string FindText(MappingEntry entry)
    {
        // A cached field is stored without its data. root, so either spelling names it; a search compares the path
        // exactly as it is written, so it is shown as written.
        var searched = entry.Source?.Kind == MappingSourceKind.Search;
        var groups = new List<(List<string> Fields, string Operand)>();
        foreach (var find in entry.FindBy)
        {
            var field = searched ? find.Field : ReferenceField.Normalize(find.Field);
            var operand = find.Literal is not null
                ? $"'{find.Literal}'"
                : entry.Modifiers.Count == 0 ? find.Column!.ToString() : $"({find.Column} | {ModifierText(entry.Modifiers)})";
            if (groups.Count > 0 && string.Equals(groups[^1].Operand, operand, StringComparison.Ordinal))
            {
                groups[^1].Fields.Add(field);
            }
            else
            {
                groups.Add(([field], operand));
            }
        }

        return string.Join(" or ", groups.Select(g => $"{string.Join('/', g.Fields)} = {g.Operand}"));
    }

    /// <summary>The modifiers as a pipeline; a replace of more than a few values is counted rather than listed.</summary>
    private static string ModifierText(IReadOnlyList<Modifier> modifiers) => string.Join(" | ", modifiers.Select(modifier => modifier.Kind switch
    {
        ModifierKind.Split => $"split('{modifier.Separator}', {modifier.Part})",
        ModifierKind.Replace when modifier.Replacements.Count > 3 => $"replace({modifier.Replacements.Count} values)",
        _ => modifier.ToString(),
    }));

    /// <summary>Whether an entry's <c>appliesWhen</c> holds for the row. Comparison is of trimmed text, ignoring case.</summary>
    public static bool Applies(EntryCondition condition, SourceRow root, SourceRow? item)
    {
        var text = SourceRow.Stringify(Read(condition.Column, root, item))?.Trim();
        var empty = string.IsNullOrEmpty(text);
        return condition.Operator switch
        {
            ConditionOperator.IsEmpty => empty,
            ConditionOperator.IsNotEmpty => !empty,
            ConditionOperator.Is => !empty && string.Equals(text, condition.Text!.Trim(), StringComparison.OrdinalIgnoreCase),
            _ => empty || !string.Equals(text, condition.Text!.Trim(), StringComparison.OrdinalIgnoreCase),
        };
    }

    private static object? Read(DatasetColumn column, SourceRow root, SourceRow? item)
        => column.Child is null ? root.Get(column.Column) : item?.Get(column.Column);

    private static bool IsEmpty(object? value) => value is null || (value is string text && string.IsNullOrWhiteSpace(text));

    /// <summary>
    /// Applies the modifiers in order. False when a modifier could not read the value (a date that is not a date), which
    /// holds the record whatever the entry's required flag says: the value was there, and it was wrong.
    /// </summary>
    private static bool TryModify(IReadOnlyList<Modifier> modifiers, object? value, string path, List<string> holds, out object? result)
    {
        result = value;
        foreach (var modifier in modifiers)
        {
            if (result is null)
            {
                return true;
            }

            var text = result is string s ? s : SourceRow.Stringify(result);
            switch (modifier.Kind)
            {
                case ModifierKind.Trim:
                    result = text?.Trim();
                    break;
                case ModifierKind.Upper:
                    result = text?.ToUpperInvariant();
                    break;
                case ModifierKind.Lower:
                    result = text?.ToLowerInvariant();
                    break;
                case ModifierKind.Split:
                    result = Split(text, modifier.Separator!, modifier.Part!.Value);
                    break;
                case ModifierKind.Replace:
                    result = Replace(text, modifier.Replacements);
                    break;
                case ModifierKind.Equals:
                    result = text is null ? null : string.Equals(text.Trim(), modifier.Text?.Trim(), StringComparison.OrdinalIgnoreCase);
                    break;
                case ModifierKind.Date:
                    if (!DateValues.TryRead(result, modifier.Text, out var date))
                    {
                        holds.Add(modifier.Text is null
                            ? $"{path}: '{text}' is not an ISO 8601 date or date-time, such as 2026-09-01 or 2026-09-01T10:15:30Z; give the format it is written in, such as date: dd.MM.yyyy"
                            : $"{path}: '{text}' is not a date/time in the format {modifier.Text}");
                        result = null;
                        return false;
                    }

                    result = date;
                    break;
                case ModifierKind.Number:
                    if (!NumberValues.TryRead(result, modifier.DecimalSeparator, modifier.GroupSeparator, out var number, out var numberProblem))
                    {
                        holds.Add($"{path}: {numberProblem}");
                        result = null;
                        return false;
                    }

                    result = number;
                    break;
                default:
                    throw new DeliveryException($"{path}: modifier '{modifier.Kind}' is not supported.");
            }
        }

        return true;
    }

    private static string? Split(string? text, string separator, int part)
    {
        if (text is null)
        {
            return null;
        }

        var parts = separator == " " ? Whitespace().Split(text.Trim()) : text.Split(separator, StringSplitOptions.None);
        if (part > parts.Length)
        {
            return null;
        }

        var value = parts[part - 1].Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>An exact match of the trimmed value wins; otherwise a key that matches ignoring case, when exactly one does; otherwise the value is unchanged.</summary>
    private static string? Replace(string? text, IReadOnlyDictionary<string, string> replacements)
    {
        if (text is null)
        {
            return null;
        }

        var value = text.Trim();
        if (replacements.TryGetValue(value, out var exact))
        {
            return exact;
        }

        var loose = replacements.Where(kv => string.Equals(kv.Key, value, StringComparison.OrdinalIgnoreCase)).ToList();
        return loose.Count == 1 ? loose[0].Value : value;
    }

    /// <summary>
    /// Resolves a search source: each findBy line in turn asks the platform for the one record whose property is exactly
    /// the line's value, and the first line that finds exactly one wins. A line the run has not asked yet stops the render
    /// there, naming the question, because what the later lines are asked depends on what this one finds. A value that is
    /// already an OSDU id of the kind searched names its record without a search.
    /// </summary>
    /// <remarks>
    /// Doubt holds the record whatever the entry's required flag says: a value several records answer to, a query the
    /// platform refused, or a value that could not be asked for on a line when no other line found the record. Each of
    /// those says nothing about which record was meant, and delivering the record without the reference, or with one
    /// picked from several, would put a wrong document into OSDU with nothing to show it was a guess. Only a value every
    /// line asked for and found nothing for is a clean miss, which an optional entry leaves out as a cache miss does.
    /// </remarks>
    private static object? Searched(MappingEntry entry, SourceRow root, SourceRow? item, MappingRenderer renderer, List<string> holds, SearchTrail searched)
    {
        var path = entry.Target.Text;
        var name = entry.Source!.CacheType!;
        var outcomes = new List<string>();
        var unaskable = false;
        foreach (var find in entry.FindBy)
        {
            string? value;
            if (find.Literal is not null)
            {
                value = find.Literal;
            }
            else
            {
                if (!TryModify(entry.Modifiers, Read(find.Column!, root, item), path, holds, out var modified))
                {
                    return null;
                }

                value = modified is bool flag ? (flag ? "true" : "false") : SourceRow.Stringify(modified);
            }

            value = value?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (!renderer.Searches.TryField(name, find.Field, out var search, out var field))
            {
                holds.Add(
                    $"{path}: search.{name}.{find.Field} has not been resolved against the schema search '{name}' pins, so no query can be written for it; the mapping's preflight says why");
                return null;
            }

            var kind = search!.Declaration.Kind;

            // A value that already is an OSDU id names its record, so nothing is asked; an id of another entity type
            // would be a reference to the wrong kind of record, which no search would have returned.
            if (OsduId().IsMatch(value))
            {
                var entityType = value.Split(':')[1];
                var searchedType = kind.Split(':')[2];
                if (string.Equals(entityType, searchedType, StringComparison.Ordinal))
                {
                    return WithVersionSeparator(value);
                }

                holds.Add($"{path}: '{value}' is already an OSDU id, of a {entityType} record, and search.{name} looks for {searchedType} records ({kind})");
                return null;
            }

            OsduQuery query;
            try
            {
                query = OsduQuery.Equal(field!, value);
            }
            catch (OsduQueryException ex)
            {
                // The value cannot be asked for on this line; a later line may still find the record, and if none does the
                // record is held, since nobody knows whether the platform holds it.
                unaskable = true;
                outcomes.Add($"{find.Field} '{value}' cannot be searched for: {ex.Message}");
                continue;
            }

            var question = new SearchQuestion(kind, find.Field, value, query.Text);
            if (!renderer.Search.TryAnswer(question, out var answer))
            {
                // Not asked yet. The render stops here rather than guessing, and the caller asks and renders again.
                searched.Ask(question);
                return null;
            }

            searched.Use(question, answer);
            switch (answer.Outcome)
            {
                case SearchOutcome.Found:
                    return WithVersionSeparator(answer.Id!);
                case SearchOutcome.NotFound:
                    outcomes.Add($"{find.Field} '{value}' {answer.Describe()}");
                    continue;
                default:
                    holds.Add(
                        $"{path}: searching {kind} for {find.Field} '{value}' {answer.Describe()}; a reference picked from several, or taken without an answer, would put a wrong document into OSDU, so the record is held. Make the incoming value name one record.");
                    return null;
            }
        }

        if (outcomes.Count == 0)
        {
            if (entry.Required)
            {
                var operands = string.Join(", ", entry.FindBy.Where(f => f.Column is not null).Select(f => f.Column!.ToString()).Distinct(StringComparer.Ordinal));
                holds.Add($"{path}: {operands} is empty, so there is nothing to search for, and the entry is required");
            }

            return null;
        }

        var searchedKind = renderer.Searches.Searches.TryGetValue(name, out var declared) ? declared.Declaration.Kind : name;
        var reason = $"{path}: no {name} on the platform ({searchedKind}) matches: {string.Join("; ", outcomes)}";
        if (unaskable)
        {
            holds.Add(reason + ". A value that cannot be searched for proves nothing about whether the record exists, so the record is held whatever the entry's required flag says");
            return null;
        }

        return Missing(entry, reason, holds);
    }

    /// <summary>
    /// Resolves a cache source: each findBy line in turn compares its cached field with its value, and the first line that
    /// finds exactly one record wins. The value an entry matched by and the value it read are both recorded as
    /// dependencies of the render, so a later cache version can tell which records it changes.
    /// </summary>
    private static object? Cached(MappingEntry entry, SourceRow root, SourceRow? item, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages)
    {
        var path = entry.Target.Text;
        var source = entry.Source!;
        var typeName = source.CacheType!;
        var version = CacheLabel(renderer.Context);
        var type = renderer.References.Type(typeName);
        if (type is null)
        {
            holds.Add($"{path}: the cache holds no type '{typeName}' in {version}");
            return null;
        }

        var tried = new List<(FindBy Find, string Value)>();
        (FindBy Find, string Value, ReferenceMatch Found)? undecided = null;
        foreach (var find in entry.FindBy)
        {
            string? value;
            if (find.Literal is not null)
            {
                value = find.Literal;
            }
            else
            {
                if (!TryModify(entry.Modifiers, Read(find.Column!, root, item), path, holds, out var modified))
                {
                    return null;
                }

                value = modified is bool flag ? (flag ? "true" : "false") : SourceRow.Stringify(modified);
            }

            value = value?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            tried.Add((find, value));

            // A value that already is an OSDU id names its record without a lookup.
            if (OsduId().IsMatch(value))
            {
                if (type.Match("id", value.TrimEnd(':')) is { } byId)
                {
                    usages.Add(new CacheUsage(typeName, byId.Id, "id", value, CacheUsageKind.Match));
                    return Select(entry, type, byId, renderer, holds, usages);
                }

                if (source.ReadsRecordId)
                {
                    return WithVersionSeparator(value);
                }

                return Missing(entry, $"{path}: '{value}' is already an OSDU id that {version} does not hold, so '{source.CacheField}' cannot be read from the cache", holds);
            }

            var found = type.Find(find.Field, value, entry.IgnoreSeparators);
            if (found.Item is { } hit)
            {
                usages.Add(new CacheUsage(typeName, hit.Id, ReferenceField.Normalize(find.Field), value, CacheUsageKind.Match));
                return Select(entry, type, hit, renderer, holds, usages);
            }

            if (found.IsCaseAmbiguous)
            {
                undecided ??= (find, value, found);
            }
        }

        if (undecided is { } choice)
        {
            // Several records answer to the value. Taking the first would deliver the wrong reference without a trace, so
            // the record is held whatever the required flag says, and the reason names every candidate.
            var candidates = string.Join(", ", choice.Found.CaseVariants.Select(c => c.Id));
            holds.Add(
                $"{path}: '{choice.Value}' matches {choice.Found.CaseVariants.Count} {typeName} records by {ReferenceField.Normalize(choice.Find.Field)} {choice.Found.Loosening} ({candidates}) in {version}; make the incoming value exact with a replace modifier");
            return null;
        }

        if (tried.Count == 0)
        {
            var operands = string.Join(", ", entry.FindBy.Where(f => f.Column is not null).Select(f => f.Column!.ToString()).Distinct(StringComparer.Ordinal));
            if (entry.Required)
            {
                holds.Add($"{path}: {operands} is empty, so there is nothing to find in the cache, and the entry is required");
            }

            return null;
        }

        var fold = entry.IgnoreSeparators ? ", even with punctuation and spacing ignored" : string.Empty;
        var described = tried.Select(t => t.Value).Distinct(StringComparer.Ordinal).Count() == 1
            ? $"'{tried[0].Value}' by {string.Join("/", tried.Select(t => ReferenceField.Normalize(t.Find.Field)))}"
            : string.Join(" or ", tried.Select(t => $"{ReferenceField.Normalize(t.Find.Field)} '{t.Value}'"));
        return Missing(entry, $"{path}: no {typeName} matches {described}{fold} in {version}", holds);
    }

    /// <summary>The cache version a render read, as a hold reason names it: "version 20260910T165153Z of the cache of partition 'dev'".</summary>
    private static string CacheLabel(RenderContext context)
        => context.CacheScope is null ? $"cache version {context.CacheVersion}" : $"version {context.CacheVersion} of the cache of partition '{context.CacheScope}'";

    private static object? Select(MappingEntry entry, ReferenceType type, ReferenceItem hit, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages)
    {
        var source = entry.Source!;
        if (source.ReadsRecordId)
        {
            usages.Add(new CacheUsage(type.Name, hit.Id, "id", hit.Id, CacheUsageKind.Value));
            return WithVersionSeparator(hit.Id);
        }

        var field = source.CacheField!;
        if (type.Value(hit, field) is not { } cached)
        {
            return Missing(
                entry,
                $"{entry.Target.Text}: {type.Name} '{hit.Id}' caches nothing at '{field}' in {CacheLabel(renderer.Context)}. Cached: {string.Join(", ", type.FieldNames.Prepend("id"))}",
                holds);
        }

        usages.Add(new CacheUsage(type.Name, hit.Id, ReferenceField.Normalize(field), cached.Text, CacheUsageKind.Value));
        return cached.Node.DeepClone();
    }

    /// <summary>A value the cache does not have: a required entry holds the record, an optional one leaves the variable out.</summary>
    private static object? Missing(MappingEntry entry, string reason, List<string> holds)
    {
        if (entry.Required)
        {
            holds.Add(reason);
        }

        return null;
    }

    private static string WithVersionSeparator(string id) => id.EndsWith(':') ? id : id + ":";

    private static JsonNode Expand(JsonNode node, MappingRenderer renderer) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => JsonValue.Create(MappingEntry.ExpandParameters(text, renderer.ParameterValue)),
        JsonObject obj => new JsonObject(obj.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value is null ? null : Expand(kv.Value, renderer)))),
        JsonArray array => new JsonArray(array.Select(item => item is null ? null : Expand(item, renderer)).ToArray()),
        _ => node.DeepClone(),
    };

    /// <summary>
    /// Converts a value to the type its variable declares: text becomes a number, an integer or a boolean where the schema
    /// says so, a single value bound to a list becomes a list of one, and a set of values fills a list or holds a single
    /// value. A date is written in the form the variable's format takes. A value that cannot take the type holds the record
    /// with a reason naming the target.
    /// </summary>
    private static JsonNode? Convert(object raw, SchemaProperty? property, string path, List<string> holds)
    {
        var type = property?.Type ?? SchemaType.Any;
        if (raw is JsonArray set)
        {
            return ConvertSet(set, property, path, holds);
        }

        if (raw is JsonObject obj)
        {
            if (type is SchemaType.Object or SchemaType.Any)
            {
                return obj;
            }

            holds.Add($"{path}: an object cannot be written where the template takes a {Name(type)}");
            return null;
        }

        var scalar = raw is JsonValue value ? Native(value) : raw;
        if (type == SchemaType.Array && property?.ItemScalarType is { } itemType)
        {
            var single = Scalar(scalar, itemType, property?.ItemFormat, path, holds);
            return single is null ? null : new JsonArray(single);
        }

        return Scalar(scalar, type, property?.Format, path, holds);
    }

    private static JsonNode? ConvertSet(JsonArray set, SchemaProperty? property, string path, List<string> holds)
    {
        var type = property?.Type ?? SchemaType.Any;
        if (type is SchemaType.Array && property?.ItemScalarType is { } itemType)
        {
            var items = new JsonArray();
            foreach (var element in set)
            {
                if (element is not null && Scalar(Native(element), itemType, property?.ItemFormat, path, holds) is { } converted)
                {
                    items.Add(converted);
                }
            }

            return items.Count > 0 ? items : null;
        }

        if (type is SchemaType.Array or SchemaType.Object or SchemaType.Any)
        {
            return set.DeepClone();
        }

        if (set.Count == 1 && set[0] is { } only)
        {
            return Scalar(Native(only), type, property?.Format, path, holds);
        }

        holds.Add($"{path}: {set.Count} values were given but the template takes one {Name(type)}; read one value or fill a list");
        return null;
    }

    private static JsonNode? Scalar(object raw, SchemaType type, string? format, string path, List<string> holds)
    {
        if (DateValues.IsDate(raw) && type is SchemaType.String or SchemaType.Any)
        {
            var written = DateValues.Write(raw, format, out var dateProblem);
            if (written is null)
            {
                holds.Add($"{path}: {dateProblem}");
                return null;
            }

            return JsonValue.Create(written);
        }

        // A number is written in the form its property takes: a number, an integer in its format's range, or its shortest text.
        string? problem;
        switch (type)
        {
            case SchemaType.Number:
                return Written(NumberValues.ToNumber(raw, out problem), problem, path, holds);
            case SchemaType.Integer:
                return Written(NumberValues.ToInteger(raw, format, out problem), problem, path, holds);
            case SchemaType.Any when NumberValues.IsNumber(raw):
                return Written(NumberValues.ToNumber(raw, out problem), problem, path, holds);
            case SchemaType.String when NumberValues.IsNumber(raw):
                return Written(NumberValues.ToText(raw, out problem) is { } text ? JsonValue.Create(text) : null, problem, path, holds);
        }

        try
        {
            switch (type)
            {
                case SchemaType.String:
                    return JsonValue.Create(SourceRow.Stringify(raw));
                case SchemaType.Boolean:
                    return raw switch
                    {
                        bool b => JsonValue.Create(b),
                        _ => JsonValue.Create(ParseBool(SourceRow.Stringify(raw)!)),
                    };
                case SchemaType.Object:
                case SchemaType.Array:
                    throw new FormatException($"a single value cannot be written where the template takes a {Name(type)}");
                default:
                    return JsonValue.Create(Clr(raw));
            }
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException)
        {
            holds.Add($"{path}: value '{SourceRow.Stringify(raw)}' is not a valid {Name(type)} ({ex.Message})");
            return null;
        }
    }

    /// <summary>A converted value, or the reason it could not be converted added as a hold naming the target.</summary>
    private static JsonNode? Written(JsonNode? value, string? problem, string path, List<string> holds)
    {
        if (value is null)
        {
            holds.Add($"{path}: {problem}");
        }

        return value;
    }

    /// <summary>A JSON value as the CLR value the conversions expect, so a cached or static value converts like a dataset value.</summary>
    private static object Native(JsonNode node) => node is JsonValue value
        ? value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.True or JsonValueKind.False => value.GetValue<bool>(),
            JsonValueKind.Number => value.TryGetValue<long>(out var whole) ? whole : value.GetValue<double>(),
            _ => node,
        }
        : node;

    /// <summary>A value that is not a number or a date, for a variable the template does not type: text and booleans as they are, anything else as its text.</summary>
    private static object Clr(object raw) => raw is string or bool ? raw : SourceRow.Stringify(raw)!;

    private static bool ParseBool(string text)
    {
        var t = text.Trim();
        if (t.Equals("true", StringComparison.OrdinalIgnoreCase) || t is "1" || t.Equals("yes", StringComparison.OrdinalIgnoreCase) || t.Equals("y", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (t.Equals("false", StringComparison.OrdinalIgnoreCase) || t is "0" || t.Equals("no", StringComparison.OrdinalIgnoreCase) || t.Equals("n", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        throw new FormatException($"'{text}' is not a boolean");
    }

    private static string Name(SchemaType type) => type.ToString().ToLowerInvariant();
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^[\w\-\.]+:[\w\-\.]+--[\w\-\.]+:[\w\-\.\:\%]+$")]
    private static partial Regex OsduId();
}
