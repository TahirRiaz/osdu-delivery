using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// Reads a mapping document (docs/delivery/mapping-templates.md) into a <see cref="MappingDefinition"/>. Everything
/// that can be checked without the template is checked here, each error naming the file and the entry: the header, every
/// entry's input, sources, findBy lines, modifiers and conditions, repeaters, and the envelope lists.
/// </summary>
internal static partial class MappingMapper
{
    /// <summary>The access list and legal block targets every mapping gives as static lists.</summary>
    private static readonly (string Target, string Name)[] EnvelopeTargets =
    [
        ("osdu.acl.owners", "owners"),
        ("osdu.acl.viewers", "viewers"),
        ("osdu.legal.legaltags", "legal tags"),
        ("osdu.legal.otherRelevantDataCountries", "countries"),
    ];

    public static MappingDefinition Map(MappingYaml y, string source)
    {
        var name = FlowMapper.Require(y.Name, "name", source);
        var version = FlowMapper.Require(y.Version, "version", source);
        var template = y.Template ?? throw FlowMapper.Missing("template", source);
        var kind = FlowMapper.Require(template.Kind, "template.kind", source);
        if (!FlowMapper.IsRecordKind(kind))
        {
            throw new FlowValidationException(
                $"{source}: template.kind '{kind}' must be 'authority:source:entityType:major.minor.patch', each of the first three made of letters, digits, underscore, hyphen and dot, as the storage service requires of every record it accepts.");
        }

        var templateVersion = FlowMapper.Require(template.Version, "template.version", source);
        if (!TemplateVersionPattern().IsMatch(templateVersion))
        {
            throw new FlowValidationException(
                $"{source}: template.version '{templateVersion}' is not a template version. A version is the 16 hexadecimal characters the Templates page shows for a saved template, such as 26a3c3441882db4f.");
        }

        var dataset = y.Dataset ?? throw FlowMapper.Missing("dataset", source);
        var declaredKey = dataset.Key is { Count: > 0 } k
            ? k
            : throw new FlowValidationException($"{source}: dataset.key must name at least one column, such as dataset.log_id.");
        var key = declaredKey.Select((column, i) => RootColumn(column, $"dataset.key[{i}]", source)).ToList();
        if (key.Distinct(StringComparer.OrdinalIgnoreCase).Count() != key.Count)
        {
            throw new FlowValidationException($"{source}: dataset.key names a column more than once.");
        }

        var label = string.IsNullOrWhiteSpace(dataset.Label) ? null : dataset.Label.Trim();
        if (label is not null)
        {
            foreach (Match token in LabelToken().Matches(label))
            {
                RootColumn(token.Groups["column"].Value, "dataset.label", source);
            }
        }

        // The columns an operator holds a record by, indexed by the ledger for the lookup: the dataset's own columns,
        // like the key, and each named once.
        var identity = (dataset.Identity ?? []).Select((column, i) => RootColumn(column, $"dataset.identity[{i}]", source)).ToList();
        if (identity.Distinct(StringComparer.OrdinalIgnoreCase).Count() != identity.Count)
        {
            throw new FlowValidationException($"{source}: dataset.identity names a column more than once.");
        }

        var parameters = (y.Parameters ?? []).ToDictionary(
            kv => kv.Key,
            kv => new MappingParameter { Required = kv.Value?.Required ?? false, Default = kv.Value?.Default, Description = kv.Value?.Description },
            StringComparer.Ordinal);
        if (!parameters.ContainsKey(Snapshots.RenderContext.DataPartitionParameter))
        {
            throw new FlowValidationException(
                $"{source}: the mapping must declare the '{Snapshots.RenderContext.DataPartitionParameter}' parameter; record ids and references are minted in that partition.");
        }

        var written = y.Mappings is { Count: > 0 } m ? m : throw new FlowValidationException($"{source}: 'mappings' must list at least one entry.");
        var parsed = written.Select((entry, i) => ParseEntry(entry, i, source)).ToList();
        var entries = ResolveRepeaters(parsed, source);
        Validate(entries, parameters, source);

        return new MappingDefinition
        {
            SourcePath = source == "<inline>" ? null : source,
            Name = name,
            Version = version,
            Template = new TemplateReference(kind, templateVersion),
            Description = y.Description,
            Dataset = new MappingDataset
            {
                System = FlowMapper.Require(dataset.System, "dataset.system", source),
                Key = key,
                Label = label,
                Identity = identity,
            },
            Parameters = parameters,
            Entries = entries,
            Envelope = Envelope(entries, kind, source),
            Fixtures = (y.Fixtures ?? []).Select((f, i) => new MappingFixture
            {
                Name = FlowMapper.Require(f.Name, $"fixtures[{i}].name", source),
                Record = f.Record ?? throw FlowMapper.Missing($"fixtures[{i}].record", source),
                Datasets = (f.Datasets ?? []).ToDictionary(
                    kv => kv.Key,
                    kv => (IReadOnlyList<IReadOnlyDictionary<string, string?>>)(kv.Value ?? []).Select(r => (IReadOnlyDictionary<string, string?>)r).ToList(),
                    StringComparer.Ordinal),
                Parameters = f.Parameters ?? new Dictionary<string, string>(StringComparer.Ordinal),
                Expected = FlowMapper.Require(f.Expected, $"fixtures[{i}].expected", source),
            }).ToList(),
        };
    }

    /// <summary>Every text inside a static value, where parameter tokens can appear.</summary>
    internal static IEnumerable<string> StaticTexts(JsonNode? node)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                yield return text;
                break;
            case JsonObject obj:
                foreach (var (_, child) in obj)
                {
                    foreach (var text in StaticTexts(child))
                    {
                        yield return text;
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    foreach (var text in StaticTexts(item))
                    {
                        yield return text;
                    }
                }

                break;
        }
    }

    private static MappingEntry ParseEntry(MappingEntryYaml y, int index, string source)
    {
        if (!TemplatePath.TryParse(y.Target, out var target, out var targetError))
        {
            throw new FlowValidationException($"{source}: mappings[{index}]: {targetError}.");
        }

        var where = $"{source}: mappings[{index}] ({target!.Text})";
        var hasSource = !string.IsNullOrWhiteSpace(y.Source);
        if (hasSource == y.HasStatic)
        {
            throw new FlowValidationException(hasSource
                ? $"{where} declares both 'source' and 'static'; an entry takes its value from one of them."
                : $"{where} declares neither 'source' nor 'static'; say where the value comes from, such as source: dataset.column.");
        }

        if (y.HasStatic)
        {
            var value = StaticValue(y.Static, where)
                ?? throw new FlowValidationException($"{where}: 'static' has no value. Leave the variable out by removing the entry.");
            if (y.FindBy is not null || y.Modifiers is not null || y.Required is not null || y.IgnoreSeparators is not null)
            {
                throw new FlowValidationException(
                    $"{where}: a static entry takes only 'appliesWhen' and 'description' besides its value; 'findBy', 'modifiers', 'required' and 'ignoreSeparators' belong to entries that read the dataset or the cache.");
            }

            return new MappingEntry
            {
                Index = index,
                Target = target,
                Static = value,
                AppliesWhen = y.AppliesWhen is null ? null : Condition(y.AppliesWhen, where),
                Description = y.Description,
            };
        }

        var parsedSource = Source(y.Source!.Trim(), where);
        if (parsedSource.Kind != MappingSourceKind.Cache && y.FindBy is not null)
        {
            throw new FlowValidationException($"{where}: 'findBy' only applies to a cache source; {parsedSource} reads the dataset.");
        }

        if (parsedSource.Kind != MappingSourceKind.Cache && y.IgnoreSeparators is not null)
        {
            throw new FlowValidationException($"{where}: 'ignoreSeparators' loosens how a value is matched against the cache, so it only applies to a cache source.");
        }

        var findBy = parsedSource.Kind == MappingSourceKind.Cache ? FindByLines(y.FindBy, parsedSource.CacheType!, where) : [];
        if (parsedSource.Kind == MappingSourceKind.Cache && findBy.Count == 0)
        {
            throw new FlowValidationException(
                $"{where}: a cache source needs 'findBy', which says which cached record to read, such as findBy: cache.{parsedSource.CacheType}.Code = dataset.column.");
        }

        var modifiers = (y.Modifiers ?? []).Select((modifier, i) => ParseModifier(modifier, $"{where} modifiers[{i}]")).ToList();
        if (modifiers.Count > 0 && parsedSource.Kind == MappingSourceKind.Cache && findBy.All(f => f.Column is null))
        {
            throw new FlowValidationException($"{where}: modifiers change incoming dataset values, and this entry's findBy reads none; cache values are never modified.");
        }

        return new MappingEntry
        {
            Index = index,
            Target = target,
            Source = parsedSource,
            FindBy = findBy,
            Modifiers = modifiers,
            AppliesWhen = y.AppliesWhen is null ? null : Condition(y.AppliesWhen, where),
            Required = y.Required ?? true,
            IgnoreSeparators = y.IgnoreSeparators ?? false,
            Description = y.Description,
        };
    }

    /// <summary>
    /// A two-part dataset source (<c>dataset.curves</c>) on a target other entries step into (<c>osdu.data.Curves[].CurveID</c>)
    /// is a repeater; everywhere else it is a column of the dataset's row.
    /// </summary>
    private static List<MappingEntry> ResolveRepeaters(List<MappingEntry> entries, string source)
    {
        var steppedInto = entries.Select(e => e.Target.Repeater).OfType<TemplatePath>().ToHashSet();
        var resolved = new List<MappingEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (!steppedInto.Contains(entry.Target))
            {
                resolved.Add(entry);
                continue;
            }

            if (entry.Source is not { Kind: MappingSourceKind.DatasetColumn, Column: { Child: null } column })
            {
                throw new FlowValidationException(
                    $"{source}: {entry.Where}: other entries fill properties inside {entry.Target.Text}[], so this entry is its repeater, and a repeater's source names a child dataset, such as source: dataset.curves.");
            }

            if (entry.Modifiers.Count > 0)
            {
                throw new FlowValidationException($"{source}: {entry.Where}: a repeater only names the child dataset whose rows become the array's items; it takes no modifiers.");
            }

            resolved.Add(entry with { Source = new MappingSource { Kind = MappingSourceKind.DatasetRows, Child = column.Column } });
        }

        return resolved;
    }

    private static void Validate(List<MappingEntry> entries, IReadOnlyDictionary<string, MappingParameter> parameters, string source)
    {
        var seen = new Dictionary<TemplatePath, MappingEntry>();
        foreach (var entry in entries)
        {
            if (!seen.TryAdd(entry.Target, entry))
            {
                throw new FlowValidationException($"{source}: {entry.Where} fills the same variable as mappings[{seen[entry.Target].Index}]; each variable has one entry.");
            }
        }

        var repeaters = entries.Where(e => e.IsRepeater).ToDictionary(e => e.Target);
        foreach (var entry in entries)
        {
            string? child = null;
            if (entry.Target.Repeater is { } array)
            {
                if (!repeaters.TryGetValue(array, out var repeater))
                {
                    throw new FlowValidationException(
                        $"{source}: {entry.Where} fills a property inside {array.Text}[], but no entry repeats {array.Text}; add one with target {array.Text} and source dataset.<child dataset>.");
                }

                child = repeater.Source!.Child;
            }

            if (entry.IsRepeater && entry.AppliesWhen?.Column.Child is not null)
            {
                throw new FlowValidationException($"{source}: {entry.Where}: a repeater's appliesWhen decides for the whole array, so it reads the dataset's own row, not {entry.AppliesWhen.Column}.");
            }

            foreach (var column in entry.Columns)
            {
                if (column.Child is null)
                {
                    continue;
                }

                if (child is null)
                {
                    throw new FlowValidationException(
                        $"{source}: {entry.Where} reads {column}, a column of child dataset '{column.Child}', but only an entry inside a repeater (a target with []) reads child rows.");
                }

                if (!string.Equals(column.Child, child, StringComparison.OrdinalIgnoreCase))
                {
                    throw new FlowValidationException(
                        $"{source}: {entry.Where} is inside the repeater over dataset.{child}, so it reads dataset.{child}.<column>, not {column}.");
                }
            }

            foreach (var name in StaticTexts(entry.Static).SelectMany(MappingEntry.ParameterNames))
            {
                if (!parameters.ContainsKey(name))
                {
                    throw new FlowValidationException($"{source}: {entry.Where} uses {{param.{name}}}, but the mapping declares no parameter '{name}'.");
                }
            }
        }
    }

    private static MappingEnvelope Envelope(List<MappingEntry> entries, string kind, string source)
    {
        if (DspdmKinds.Is(kind))
        {
            // A DSPDM business object row is no OSDU record: it has no access or legal block, and its business object's
            // attributes are all a mapping fills (osdu/specs/production-dspdm/INTEGRATION.md section 3.1).
            if (entries.FirstOrDefault(e => EnvelopeTargets.Any(t => t.Target == e.Target.Text)) is { } envelope)
            {
                throw new FlowValidationException(
                    $"{source}: {envelope.Where} fills {envelope.Target.Text}, and {kind} is a row of a DSPDM business object, which has no access or legal block. Remove the entry.");
            }

            // The row is its data block: each attribute of the business object is a property of data, and nothing else is sent.
            if (entries.FirstOrDefault(e => e.Target.Segments.Count < 2 || e.Target.Segments[0].Name != "data") is { } outside)
            {
                throw new FlowValidationException(
                    $"{source}: {outside.Where} fills {outside.Target.Text}, and {kind} is a row of a DSPDM business object, whose attributes are the properties of "
                    + $"{TemplatePath.Prefix}.data ({TemplatePath.Prefix}.data.UWI). Fill an attribute, or remove the entry.");
            }

            return new MappingEnvelope([], [], [], []);
        }

        var lists = new List<IReadOnlyList<string>>();
        foreach (var (target, name) in EnvelopeTargets)
        {
            var entry = entries.FirstOrDefault(e => e.Target.Text == target)
                ?? throw new FlowValidationException($"{source}: every OSDU record carries {name}, so the mapping needs an entry with target {target} and a static list.");
            if (entry.Static is not JsonArray array || array.Count == 0
                || array.Any(v => v is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text)))
            {
                throw new FlowValidationException($"{source}: {entry.Where} must be a static list of at least one text value, such as static: [value].");
            }

            if (entry.AppliesWhen is not null)
            {
                throw new FlowValidationException($"{source}: {entry.Where} applies to every record, so it takes no appliesWhen.");
            }

            var values = array.Select(v => v!.GetValue<string>().Trim()).ToList();
            var repeated = values.GroupBy(v => v, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
            if (repeated is not null)
            {
                // The legal and access lists are sets to the services that store them (openapi storage v2, Legal and Acl:
                // uniqueItems), so a repeat is a record the target may refuse; it is refused here, once.
                throw new FlowValidationException($"{source}: {entry.Where} lists '{repeated.Key}' more than once; the list is a set.");
            }

            lists.Add(values);
        }

        return new MappingEnvelope(lists[0], lists[1], lists[2], lists[3]);
    }

    private static MappingSource Source(string text, string where)
    {
        var parts = text.Split('.');
        if (parts[0] == DatasetColumn.Prefix)
        {
            if (parts.Length is < 2 or > 3 || parts.Skip(1).Any(p => !NamePattern().IsMatch(p)))
            {
                throw new FlowValidationException(
                    $"{where}: source '{text}' must be dataset.<column>, dataset.<child dataset>.<column>, or dataset.<child dataset> on a repeated array.");
            }

            return new MappingSource
            {
                Kind = MappingSourceKind.DatasetColumn,
                Column = parts.Length == 2 ? new DatasetColumn(null, parts[1]) : new DatasetColumn(parts[1], parts[2]),
            };
        }

        if (parts[0] == MappingSource.CachePrefix)
        {
            if (parts.Length < 3 || !NamePattern().IsMatch(parts[1]) || parts.Skip(2).Any(p => !FieldPattern().IsMatch(p)))
            {
                throw new FlowValidationException($"{where}: source '{text}' must be cache.<type>.<field>, such as cache.UnitOfMeasure.id.");
            }

            return new MappingSource { Kind = MappingSourceKind.Cache, CacheType = parts[1], CacheField = string.Join('.', parts.Skip(2)) };
        }

        throw new FlowValidationException(
            $"{where}: source '{text}' must start with 'dataset.' (the incoming dataset) or 'cache.' (the metadata cache); a fixed value is written with 'static'.");
    }

    private static List<FindBy> FindByLines(object? value, string type, string where)
    {
        List<string> lines = value switch
        {
            null => [],
            string one => [one],
            IEnumerable<object> many => many.Select(line => line as string
                ?? throw new FlowValidationException($"{where}: each findBy line is text, such as cache.{type}.Code = dataset.column.")).ToList(),
            _ => throw new FlowValidationException($"{where}: findBy is one line or a list of lines, such as cache.{type}.Code = dataset.column."),
        };

        var result = new List<FindBy>(lines.Count);
        foreach (var line in lines)
        {
            var match = FindByPattern().Match(line);
            if (!match.Success)
            {
                throw new FlowValidationException($"{where}: findBy '{line}' must read cache.<type>.<field> = dataset.<column>, or = 'text' for a fixed text.");
            }

            if (!string.Equals(match.Groups["type"].Value, type, StringComparison.Ordinal))
            {
                throw new FlowValidationException(
                    $"{where}: findBy '{line}' compares cache.{match.Groups["type"].Value}, but the entry reads cache.{type}; findBy selects a record of the type the entry reads.");
            }

            var field = match.Groups["field"].Value;
            if (field.Split('.').Any(p => !FieldPattern().IsMatch(p)))
            {
                throw new FlowValidationException($"{where}: findBy '{line}' names an invalid cached field '{field}'.");
            }

            var operand = match.Groups["operand"].Value.Trim();
            if (Quoted(operand) is { } literal)
            {
                if (literal.Length == 0)
                {
                    throw new FlowValidationException($"{where}: findBy '{line}' compares with empty text.");
                }

                result.Add(new FindBy(type, field, null, literal));
                continue;
            }

            result.Add(new FindBy(type, field, Column(operand, $"{where}: findBy '{line}'"), null));
        }

        return result;
    }

    private static EntryCondition Condition(string text, string where)
    {
        var match = ConditionPattern().Match(text.Trim());
        if (!match.Success)
        {
            throw new FlowValidationException($"{where}: appliesWhen '{text}' must read dataset.<column> is <text>, is not <text>, is empty, or is not empty.");
        }

        var column = Column(match.Groups["column"].Value, $"{where}: appliesWhen '{text}'");
        var negated = match.Groups["not"].Success;
        var rest = match.Groups["rest"].Value.Trim();
        if (rest == "empty")
        {
            return new EntryCondition(column, negated ? ConditionOperator.IsNotEmpty : ConditionOperator.IsEmpty, null);
        }

        var value = Quoted(rest) ?? rest;
        if (value.Length == 0)
        {
            throw new FlowValidationException($"{where}: appliesWhen '{text}' compares with empty text; write 'is empty' instead.");
        }

        return new EntryCondition(column, negated ? ConditionOperator.IsNot : ConditionOperator.Is, value);
    }

    private static Modifier ParseModifier(object value, string where)
    {
        switch (value)
        {
            case string name:
                return name.Trim() switch
                {
                    "trim" => new Modifier { Kind = ModifierKind.Trim },
                    "upper" => new Modifier { Kind = ModifierKind.Upper },
                    "lower" => new Modifier { Kind = ModifierKind.Lower },
                    "date" => new Modifier { Kind = ModifierKind.Date },
                    "number" => new Modifier { Kind = ModifierKind.Number, DecimalSeparator = Rendering.NumberValues.DecimalPoint },
                    "split" or "replace" or "equals" => throw new FlowValidationException($"{where}: '{name}' needs settings, such as {Example(name)}."),
                    _ => throw new FlowValidationException($"{where}: '{name}' is not a modifier. The modifiers are trim, upper, lower, split, replace, equals, date and number."),
                };

            case IDictionary<object, object> map when map.Count == 1:
                {
                    var (key, settings) = map.First();
                    var modifier = Convert.ToString(key, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                    return modifier switch
                    {
                        "split" => Split(settings, where),
                        "replace" => Replace(settings, where),
                        "equals" => settings is null or IDictionary<object, object> or IList<object>
                            ? throw new FlowValidationException($"{where}: equals compares with one text, such as {Example("equals")}.")
                            : new Modifier { Kind = ModifierKind.Equals, Text = Convert.ToString(settings, CultureInfo.InvariantCulture) },
                        "date" => Date(settings, where),
                        "number" => Number(settings, where),
                        _ => throw new FlowValidationException($"{where}: '{modifier}' is not a modifier. The modifiers are trim, upper, lower, split, replace, equals, date and number."),
                    };
                }

            default:
                throw new FlowValidationException($"{where}: a modifier is a name, such as trim, or one setting, such as {Example("split")}.");
        }
    }

    /// <summary>A date modifier: no setting reads ISO 8601, and a format is refused when it could not read a whole date.</summary>
    private static Modifier Date(object? settings, string where)
    {
        if (settings is IDictionary<object, object> or IList<object>)
        {
            throw new FlowValidationException($"{where}: date takes the input format as text, such as date: dd.MM.yyyy.");
        }

        var format = settings is null ? string.Empty : Convert.ToString(settings, CultureInfo.InvariantCulture) ?? string.Empty;
        if (format.Length == 0)
        {
            return new Modifier { Kind = ModifierKind.Date };
        }

        return Rendering.DateValues.FormatProblem(format) is { } problem
            ? throw new FlowValidationException($"{where}: the date format '{format}' {problem}.")
            : new Modifier { Kind = ModifierKind.Date, Text = format };
    }

    /// <summary>
    /// A number modifier: no setting reads '.' before the decimals and no group separator. The separators are checked here,
    /// when the mapping is read, so a mapping that could never read a number is refused before any row is rendered.
    /// </summary>
    private static Modifier Number(object? settings, string where)
    {
        if (settings is null)
        {
            return new Modifier { Kind = ModifierKind.Number, DecimalSeparator = Rendering.NumberValues.DecimalPoint };
        }

        var options = settings as IDictionary<object, object>
            ?? throw new FlowValidationException($"{where}: number takes its separators, such as {Example("number")}.");
        foreach (var option in options.Keys.Select(o => Convert.ToString(o, CultureInfo.InvariantCulture)))
        {
            if (option is not ("decimal" or "group"))
            {
                throw new FlowValidationException($"{where}: number takes 'decimal' and 'group', not '{option}'.");
            }
        }

        var point = options.TryGetValue("decimal", out var d) && d is not null
            ? Convert.ToString(d, CultureInfo.InvariantCulture) ?? string.Empty
            : Rendering.NumberValues.DecimalPoint;
        var group = options.TryGetValue("group", out var g) && g is not null ? Convert.ToString(g, CultureInfo.InvariantCulture) ?? string.Empty : null;
        if (Rendering.NumberValues.SeparatorsProblem(point, group) is { } problem)
        {
            throw new FlowValidationException($"{where}: number {problem}.");
        }

        return new Modifier { Kind = ModifierKind.Number, DecimalSeparator = point, GroupSeparator = group };
    }

    private static Modifier Split(object? settings, string where)
    {
        var options = settings as IDictionary<object, object>
            ?? throw new FlowValidationException($"{where}: split takes a separator and a part, such as {Example("split")}.");
        foreach (var option in options.Keys.Select(o => Convert.ToString(o, CultureInfo.InvariantCulture)))
        {
            if (option is not ("separator" or "part"))
            {
                throw new FlowValidationException($"{where}: split takes 'separator' and 'part', not '{option}'.");
            }
        }

        var separator = options.TryGetValue("separator", out var s) ? Convert.ToString(s, CultureInfo.InvariantCulture) : null;
        if (string.IsNullOrEmpty(separator))
        {
            throw new FlowValidationException($"{where}: split needs a separator, such as {Example("split")}.");
        }

        if (!options.TryGetValue("part", out var p)
            || !int.TryParse(Convert.ToString(p, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var part)
            || part < 1)
        {
            throw new FlowValidationException($"{where}: split needs the part to keep, counting from one, such as {Example("split")}.");
        }

        return new Modifier { Kind = ModifierKind.Split, Separator = separator, Part = part };
    }

    private static Modifier Replace(object? settings, string where)
    {
        if (settings is not IDictionary<object, object> pairs || pairs.Count == 0)
        {
            throw new FlowValidationException($"{where}: replace lists incoming values and what each becomes, such as {Example("replace")}.");
        }

        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (from, to) in pairs)
        {
            var fromText = Convert.ToString(from, CultureInfo.InvariantCulture) ?? string.Empty;
            if (to is null or IDictionary<object, object> or IList<object>)
            {
                throw new FlowValidationException($"{where}: replace maps '{fromText}' to something that is not text.");
            }

            replacements[fromText] = Convert.ToString(to, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        return new Modifier { Kind = ModifierKind.Replace, Replacements = replacements };
    }

    private static string Example(string modifier) => modifier switch
    {
        "split" => "split: { separator: \",\", part: 1 }",
        "replace" => "replace: { GAPI: gAPI }",
        "number" => "number: { decimal: \",\", group: \" \" }",
        _ => "equals: REGULAR",
    };

    private static DatasetColumn Column(string text, string where)
    {
        var parts = text.Trim().Split('.');
        if (parts[0] != DatasetColumn.Prefix || parts.Length is < 2 or > 3 || parts.Skip(1).Any(p => !NamePattern().IsMatch(p)))
        {
            throw new FlowValidationException($"{where}: '{text}' must be dataset.<column> or dataset.<child dataset>.<column>.");
        }

        return parts.Length == 2 ? new DatasetColumn(null, parts[1]) : new DatasetColumn(parts[1], parts[2]);
    }

    private static string RootColumn(string text, string key, string source)
    {
        var column = Column(text, $"{source}: {key}");
        return column.Child is null
            ? column.Column
            : throw new FlowValidationException($"{source}: {key} names {column}, a column of a child dataset; it reads the dataset's own row, such as dataset.log_id.");
    }

    private static string? Quoted(string text)
        => text.Length >= 2 && ((text[0] == '\'' && text[^1] == '\'') || (text[0] == '"' && text[^1] == '"')) ? text[1..^1] : null;

    /// <summary>
    /// Converts a YAML value to JSON: mappings to objects, sequences to arrays, and scalars keeping the type YAML read them
    /// as. YAML reads an unquoted whole number into the smallest integer type that holds it, so every integer type is a number.
    /// </summary>
    private static JsonNode? StaticValue(object? value, string where) => value switch
    {
        null => null,
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        byte or sbyte or short or ushort or int or uint or long => JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        ulong number when number <= long.MaxValue => JsonValue.Create((long)number),
        ulong number => JsonValue.Create(number),
        // YAML reads .nan and .inf as floating-point values, and a record cannot carry either, inside a list or an object included.
        double number when !double.IsFinite(number) => throw NonFiniteStatic(number, where),
        double number => JsonValue.Create(number),
        float number when !float.IsFinite(number) => throw NonFiniteStatic(number, where),
        float number => JsonValue.Create(Rendering.NumberValues.Widen(number)),
        decimal number => JsonValue.Create((double)number),
        IDictionary<object, object> map => new JsonObject(map.Select(kv => new KeyValuePair<string, JsonNode?>(
            Convert.ToString(kv.Key, CultureInfo.InvariantCulture) ?? string.Empty,
            StaticValue(kv.Value, where) ?? throw new FlowValidationException($"{where}: static value '{kv.Key}' is empty.")))),
        IEnumerable<object> list => new JsonArray(list.Select(item => StaticValue(item, where)
            ?? throw new FlowValidationException($"{where}: a static list holds an empty item.")).ToArray()),
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
    };

    private static FlowValidationException NonFiniteStatic(object number, string where)
        => new($"{where}: static value {Convert.ToString(number, CultureInfo.InvariantCulture)} is NaN or Infinity, which a JSON record cannot carry (RFC 8259).");

    [GeneratedRegex(@"^[0-9a-f]{16}$")]
    private static partial Regex TemplateVersionPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_\-]+$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9_\-\$]+$")]
    private static partial Regex FieldPattern();

    [GeneratedRegex(@"^\s*cache\.(?<type>[A-Za-z0-9_\-]+)\.(?<field>[^=\s]+)\s*=\s*(?<operand>.+?)\s*$")]
    private static partial Regex FindByPattern();

    [GeneratedRegex(@"^(?<column>dataset\.\S+)\s+is\s+(?<not>not\s+)?(?<rest>.+)$")]
    private static partial Regex ConditionPattern();

    [GeneratedRegex(@"\{(?<column>dataset\.[A-Za-z0-9_\-\.]+)\}")]
    internal static partial Regex LabelToken();
}
