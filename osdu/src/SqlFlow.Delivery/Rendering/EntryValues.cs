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
            if (!TryModify(entry, Read(column, root, item), root, item, renderer, holds, usages, out raw))
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
            raw = Searched(entry, root, item, renderer, holds, usages, searched);
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
        ModifierKind.Replace when modifier.Replacements.Count > 3 => $"replace({modifier.Replacements.Count} values"
            + (modifier.Otherwise.Kind == ReplaceFallbackKind.Keep ? string.Empty : $"; otherwise {modifier.Otherwise}") + ")",
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
    /// holds the record whatever the entry's required flag says: the value was there, and it was wrong. A replace reading a
    /// cached table records the rows it read among the render's cache usages.
    /// </summary>
    private static bool TryModify(
        MappingEntry entry, object? value, SourceRow root, SourceRow? item, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages, out object? result)
    {
        var path = entry.Target.Text;
        result = value;
        foreach (var modifier in entry.Modifiers)
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
                    if (!TryReplace(modifier, text, path, renderer, holds, usages, out result))
                    {
                        return false;
                    }

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
                case ModifierKind.Id:
                    if (!TryBuildId(modifier.Id!, text, entry, root, item, renderer, holds, usages, out result))
                    {
                        return false;
                    }

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

    /// <summary>
    /// Replaces the trimmed value by what the table lists for it (<see cref="ReplaceTables.Find"/>): an exact key first, then
    /// the one key that matches once case is ignored. A listed value may become no value. An unlisted value becomes what
    /// <c>otherwise</c> says, by default itself; an empty value stays empty, since there is nothing to look up. False, with a
    /// hold, when several keys match only once case is ignored and give different values: taking one would write a value
    /// nobody chose. A table read from the cache is looked up the same way (<see cref="TryReplaceFromCache"/>).
    /// </summary>
    private static bool TryReplace(
        Modifier modifier, string? text, string path, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages, out object? result)
    {
        result = null;
        if (text is null)
        {
            return true;
        }

        var value = text.Trim();
        if (value.Length == 0)
        {
            result = value;
            return true;
        }

        if (modifier.Table is { } cached)
        {
            return TryReplaceFromCache(modifier, cached, value, path, renderer, holds, usages, out result);
        }

        var lookup = ReplaceTables.Find(ReplaceTables.Of(modifier.Replacements), ReplaceTables.KeyField, ReplaceTables.ValueField, value);
        switch (lookup.Kind)
        {
            case ReplaceLookupKind.Found:
                result = lookup.Value?.Text;
                return true;
            case ReplaceLookupKind.Ambiguous:
                var keys = string.Join(", ", lookup.Rows.Select(row => $"'{row.Id}'"));
                holds.Add($"{path}: '{value}' matches the replace keys {keys} only once case is ignored, and they replace it with different values; make the incoming value exact");
                return false;
            default:
                result = Otherwise(modifier.Otherwise, value);
                return true;
        }
    }

    /// <summary>
    /// Replaces the value by what a table read from the cache gives for it: the row whose <c>match</c> field holds the
    /// value, by the cache's rules, gives what it holds at <c>field</c> (<see cref="ReplaceTables.Fields"/> settles either
    /// when the replace leaves it out). A row whose field is empty gives no value; <c>otherwise</c> decides only when no
    /// row holds the value.
    /// </summary>
    /// <remarks>
    /// Every row the replacement was decided by is recorded among the render's cache usages: the value it matched, and what
    /// it gave or that it gave nothing. A key a lookup table lists no row under is recorded too. So a later version of the
    /// table that changes a row, empties it, drops it, or comes to list a key it did not, tags exactly the records built
    /// from it. The record is held, whatever the entry's required flag says, when the cache version holds no such table,
    /// when its fields cannot be settled, when several rows answer to the value with different values, or when the row
    /// holds several values at its field: each of those leaves a value nobody chose.
    /// </remarks>
    private static bool TryReplaceFromCache(
        Modifier modifier, CachedReplaceTable table, string value, string path, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages, out object? result)
    {
        result = null;
        var version = CacheLabel(renderer.Context);
        var type = renderer.References.Type(table.CacheType);
        if (type is null)
        {
            holds.Add($"{path}: replace reads cache.{table.CacheType}, and {version} holds no type '{table.CacheType}'");
            return false;
        }

        if (ReplaceTables.Fields(table, type, out var problem) is not { } fields)
        {
            holds.Add($"{path}: {problem}");
            return false;
        }

        var lookup = ReplaceTables.Find(type, fields.Match, fields.Field, value);
        if (lookup.Kind == ReplaceLookupKind.Ambiguous)
        {
            var rows = string.Join(", ", lookup.Rows.Select(row => $"'{row.Id}'"));
            holds.Add(
                $"{path}: '{value}' matches the {type.Name} rows {rows} by {fields.Match} only once case is ignored in {version}, and they replace it with different values; make the incoming value exact");
            return false;
        }

        if (lookup.Kind == ReplaceLookupKind.NotListed)
        {
            // Only a key can be told apart from every other value a row might come to hold, so it is a key a lookup table
            // does not list that is recorded; a key longer than any the table can hold never will be listed.
            if (type.IsLookup && fields.Match.Equals(type.Key, StringComparison.OrdinalIgnoreCase) && value.Length <= LookupKeys.MaxLength)
            {
                usages.Add(new CacheUsage(type.Name, CacheUsage.ListingKey(value), fields.Field, value, CacheUsageKind.Unlisted));
            }

            result = Otherwise(modifier.Otherwise, value);
            return true;
        }

        foreach (var row in lookup.Rows)
        {
            usages.Add(new CacheUsage(type.Name, row.Id, fields.Match, value, CacheUsageKind.Match));
        }

        if (lookup.Value is not { } given || given.Node is JsonArray { Count: 0 })
        {
            foreach (var row in lookup.Rows)
            {
                usages.Add(new CacheUsage(type.Name, row.Id, fields.Field, string.Empty, CacheUsageKind.Empty));
            }

            return true;
        }

        if (ReplaceTables.SingleText(given) is not { } replaced)
        {
            var held = given.Node is JsonArray many ? $"{many.Count} values" : "an object";
            holds.Add(
                $"{path}: {type.Name} '{lookup.Rows[0].Id}' holds {held} at '{fields.Field}' in {version} ({Clip(given.Text)}), and a replace turns '{value}' into one value; replace by a field that holds one");
            return false;
        }

        foreach (var row in lookup.Rows)
        {
            usages.Add(new CacheUsage(type.Name, row.Id, fields.Field, given.Text, CacheUsageKind.Value));
        }

        result = replaced;
        return true;
    }

    /// <summary>A cached value as a hold reason quotes it: whole when short, its start otherwise.</summary>
    private static string Clip(string text) => text.Length <= 200 ? text : text[..200] + "...";

    /// <summary>What an unlisted value becomes under a replace's <c>otherwise</c>.</summary>
    private static string? Otherwise(ReplaceFallback fallback, string value) => fallback.Kind switch
    {
        ReplaceFallbackKind.Keep => value,
        ReplaceFallbackKind.Empty => null,
        _ => fallback.Text,
    };

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
    private static object? Searched(
        MappingEntry entry, SourceRow root, SourceRow? item, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages, SearchTrail searched)
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
                if (!TryModify(entry, Read(find.Column!, root, item), root, item, renderer, holds, usages, out var modified))
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
                    break;
                default:
                    holds.Add(
                        $"{path}: searching {kind} for {find.Field} '{value}' {answer.Describe()}; a reference picked from several, or taken without an answer, would put a wrong document into OSDU, so the record is held. Make the incoming value name one record.");
                    return null;
            }

            // Nothing holds the value exactly. Where the partition's indexer keeps a lowercased copy of text, the same value
            // is asked for regardless of case, and taken only when that finds exactly one record, which is the rule a cache
            // lookup keeps: codes that differ only by case are different records, so several answers select none of them.
            if (Caseless(renderer, field!, value) is not { } loose)
            {
                outcomes.Add($"{find.Field} '{value}' {answer.Describe()}");
                continue;
            }

            var looseQuestion = new SearchQuestion(kind, find.Field, value, loose.Text);
            if (!renderer.Search.TryAnswer(looseQuestion, out var looseAnswer))
            {
                searched.Ask(looseQuestion);
                return null;
            }

            searched.Use(looseQuestion, looseAnswer);
            switch (looseAnswer.Outcome)
            {
                case SearchOutcome.Found:
                    return WithVersionSeparator(looseAnswer.Id!);
                case SearchOutcome.NotFound:
                    outcomes.Add($"{find.Field} '{value}' found no record, exactly or once case is ignored");
                    continue;
                case SearchOutcome.Refused:
                    holds.Add(
                        $"{path}: searching {kind} for {find.Field} '{value}' found no record exactly, and once case is ignored it {looseAnswer.Describe()}; a reference taken without an answer would put a wrong document into OSDU, so the record is held.");
                    return null;
                default:
                    holds.Add(
                        $"{path}: searching {kind} for {find.Field} '{value}' found no record exactly, and once case is ignored it {looseAnswer.Describe()}; a reference picked from several would put a wrong document into OSDU, so the record is held. Make the incoming value exact with a replace modifier.");
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
    /// The query that asks for <paramref name="value"/> regardless of case, or null when there is none to ask: the system
    /// properties the render is pinned to do not say the partition's indexer keeps a lowercased copy of text, the property
    /// is not text, or the value cannot be asked for that way. Only a system property known to be on changes how a lookup
    /// is written, so a partition whose settings were never read is asked exact questions alone.
    /// </summary>
    private static OsduQuery? Caseless(MappingRenderer renderer, OsduField field, string value)
    {
        if (!renderer.Context.KeywordLower || field.Index != OsduFieldIndex.Text)
        {
            return null;
        }

        try
        {
            return OsduQuery.Equal(field, value, caseInsensitive: true);
        }
        catch (OsduQueryException)
        {
            // The exact question was asked and answered; the only value the lowercased copy refuses beyond it is a
            // spelling of the text null, which that copy holds for every record with no value.
            return null;
        }
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
                if (!TryModify(entry, Read(find.Column!, root, item), root, item, renderer, holds, usages, out var modified))
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

            // A value that already is an OSDU id names its record without a lookup, found by its record id whatever the type
            // caches under a field called ID. A lookup table holds no OSDU records, so there a value of that shape is matched
            // like any other.
            if (!type.IsLookup && OsduId().IsMatch(value))
            {
                if (CachedReferences.Parse(value) is { } named && type.ById(named.Id) is { } byId)
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
        if (source.ReadsRecordId && type.IsLookup)
        {
            // A lookup row's id is its key, not an OSDU record id, and writing it as a reference would point at nothing. The
            // gate refuses this mapping; a render that meets it anyway holds, whatever the entry's required flag says.
            holds.Add(
                $"{entry.Target.Text}: {type.Name} is a lookup table in {CacheLabel(renderer.Context)}, whose rows are not OSDU records, so it has no id to write; read one of its fields ({string.Join(", ", type.FieldNames)})");
            return null;
        }

        if (source.ReadsRecordId)
        {
            usages.Add(new CacheUsage(type.Name, hit.Id, "id", hit.Id, CacheUsageKind.Value));
            return WithVersionSeparator(hit.Id);
        }

        var field = source.CacheField!;
        if (type.Value(hit, field) is not { } cached)
        {
            // The document is built without the value, so a later version giving the record one changes it.
            usages.Add(new CacheUsage(type.Name, hit.Id, ReferenceField.Normalize(field), string.Empty, CacheUsageKind.Empty));
            return Missing(
                entry,
                $"{entry.Target.Text}: {type.Name} '{hit.Id}' caches nothing at '{field}' in {CacheLabel(renderer.Context)}. Cached: {string.Join(", ", type.FieldNames.Prepend("id"))}",
                holds);
        }

        usages.Add(new CacheUsage(type.Name, hit.Id, ReferenceField.Normalize(field), cached.Text, CacheUsageKind.Value));
        if (TakesReference(renderer, entry))
        {
            return References(entry, type, hit, field, cached, renderer, holds, usages);
        }

        return cached.Node.DeepClone();
    }

    /// <summary>Whether the entry's variable is a relationship, or a list of them: what it takes is an OSDU record id.</summary>
    private static bool TakesReference(MappingRenderer renderer, MappingEntry entry)
        => renderer.Schema.Resolve(entry.Target.SchemaPath) is { } property
            && (property.IsRelationship || property.Items?["x-osdu-relationship"] is JsonArray);

    /// <summary>
    /// A cached field written to a relationship: OSDU's own translations cache the id of the record a value stands for
    /// (<c>ExternalUnitOfMeasure.UnitOfMeasureID</c>), and the mapping writes it as the reference, with the colon that
    /// separates a version added where the cached id leaves it out. Where the cache holds records of the entity type an
    /// id names, the record it names is one of them, and it is recorded among the render's dependencies, so a version
    /// that drops it reaches this record.
    /// </summary>
    /// <remarks>
    /// A value that is not an OSDU record id, or one naming a record the cache holds that type of and not that record,
    /// holds the record whatever the entry's required flag says: writing it would put a reference to nothing into OSDU.
    /// The gate refuses a mapping whose cached field holds such values; this holds a render that meets one anyway.
    /// </remarks>
    private static object? References(
        MappingEntry entry, ReferenceType type, ReferenceItem hit, string field, ReferenceValue cached, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages)
    {
        var path = entry.Target.Text;
        var version = CacheLabel(renderer.Context);
        var written = new List<JsonNode?>();
        foreach (var node in cached.Node is JsonArray set ? set.ToList() : [cached.Node])
        {
            var text = node is JsonValue value && value.TryGetValue<string>(out var held) ? held.Trim() : null;
            if (CachedReferences.Parse(text) is not { } reference)
            {
                holds.Add(
                    $"{path}: {type.Name} '{hit.Id}' holds {Clip(node?.ToJsonString() ?? "null")} at '{field}' in {version}, which is not an OSDU record id, and {path} takes a reference to a record");
                return null;
            }

            var holding = CachedReferences.Holding(renderer.References, reference.EntityType);
            if (holding.Count > 0)
            {
                if (CachedReferences.Find(holding, reference) is not { } found)
                {
                    holds.Add(
                        $"{path}: {type.Name} '{hit.Id}' names {reference.Id} at '{field}', and {version} holds no such {reference.EntityType} record in {string.Join(", ", holding.Select(t => t.Name))}, so the reference would point at nothing");
                    return null;
                }

                usages.Add(new CacheUsage(found.Type.Name, found.Item.Id, "id", found.Item.Id, CacheUsageKind.Match));
            }

            written.Add(JsonValue.Create(CachedReferences.Written(text!)));
        }

        return cached.Node is JsonArray ? new JsonArray(written.ToArray()) : written[0];
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
        if (type == SchemaType.Array && property?.ItemScalarType is SchemaType.Object && raw is JsonObject item)
        {
            return new JsonArray(item.DeepClone());
        }

        if (type == SchemaType.Array && property?.ItemScalarType is { } itemType && itemType != SchemaType.Object)
        {
            var single = Scalar(scalar, itemType, property?.ItemFormat, path, holds);
            return single is null ? null : new JsonArray(single);
        }

        return Scalar(scalar, type, property?.Format, path, holds);
    }

    private static JsonNode? ConvertSet(JsonArray set, SchemaProperty? property, string path, List<string> holds)
    {
        var type = property?.Type ?? SchemaType.Any;
        if (type is SchemaType.Array && property?.ItemScalarType is { } itemType && itemType != SchemaType.Object)
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
