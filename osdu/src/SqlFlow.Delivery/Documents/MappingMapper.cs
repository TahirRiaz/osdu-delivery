using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;
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

        // What each search.<name> source searches. Declared once here rather than on every entry, so two entries
        // resolving against the same set cannot disagree about the kind they search or the schema it is read by.
        var searches = (y.Searches ?? []).ToDictionary(
            kv => kv.Key,
            kv => ParseSearch(kv.Key, kv.Value, source),
            StringComparer.Ordinal);

        var written = y.Mappings is { Count: > 0 } m ? m : throw new FlowValidationException($"{source}: 'mappings' must list at least one entry.");
        var parsed = written.Select((entry, i) => ParseEntry(entry, i, source)).ToList();
        var entries = ResolveRepeaters(parsed, source);
        Validate(entries, parameters, source);
        ValidateSearches(entries, searches, source);

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
            Searches = searches,
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
                Searches = FixtureSearches(f.Searches, searches, entries, $"{source}: fixtures[{i}]"),
                Expected = FlowMapper.Require(f.Expected, $"fixtures[{i}].expected", source),
            }).ToList(),
        };
    }

    /// <summary>
    /// A fixture's assumed search answers, each naming a search the mapping declares, a property one of its findBy lines
    /// compares, and at most one answer per question, so a fixture cannot say two things about the same lookup.
    /// </summary>
    private static List<FixtureSearchAnswer> FixtureSearches(
        List<MappingFixtureSearchYaml>? written,
        IReadOnlyDictionary<string, MappingSearch> searches,
        IReadOnlyList<MappingEntry> entries,
        string where)
    {
        var answers = new List<FixtureSearchAnswer>();
        var seen = new HashSet<(string, string, string)>();
        foreach (var (y, i) in (written ?? []).Select((y, i) => (y, i)))
        {
            var at = $"{where} searches[{i}]";
            var name = y?.Search?.Trim();
            if (string.IsNullOrEmpty(name) || !searches.TryGetValue(name, out var search))
            {
                throw new FlowValidationException(
                    searches.Count == 0
                        ? $"{at} answers a search, and the mapping declares none."
                        : $"{at} names search '{name}', which the mapping does not declare; it declares {string.Join(", ", searches.Keys.Order(StringComparer.Ordinal))}.");
            }

            var field = y!.Field?.Trim();
            var compared = entries
                .Where(e => e.Source?.Kind == MappingSourceKind.Search && string.Equals(e.Source.CacheType, name, StringComparison.Ordinal))
                .SelectMany(e => e.FindBy.Select(f => f.Field))
                .ToHashSet(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(field) || !compared.Contains(field))
            {
                throw new FlowValidationException(
                    $"{at} answers search '{name}' on '{field}', which no findBy line of that search compares; they compare {string.Join(", ", compared.Order(StringComparer.Ordinal))}.");
            }

            var value = y.Value?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                throw new FlowValidationException($"{at} gives no value; an answer is for one value of {field}, which a render never searches for when it is empty.");
            }

            if (!seen.Add((name, field, value)))
            {
                throw new FlowValidationException($"{at} answers search '{name}' on {field} '{value}' a second time; a fixture says one thing about each lookup.");
            }

            var id = y.Id?.Trim();
            if (string.IsNullOrEmpty(id))
            {
                id = null;
            }
            else
            {
                var segments = id.Split(':');
                var searched = search.Kind.Split(':')[2];
                if (segments.Length < 3 || !string.Equals(segments[1], searched, StringComparison.Ordinal))
                {
                    throw new FlowValidationException(
                        $"{at} answers with '{id}', which is not the id of a {searched} record; search '{name}' looks in {search.Kind}.");
                }
            }

            answers.Add(new FixtureSearchAnswer(name, field, value, id));
        }

        return answers;
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
        if (parsedSource.Kind == MappingSourceKind.Search && !parsedSource.ReadsRecordId)
        {
            // A search asks the platform for the id of the record that matches and nothing else, so an answer is one id
            // whatever the mapping reads. What else a record needs comes from the dataset, the cache or a static value.
            throw new FlowValidationException(
                $"{where}: {parsedSource} reads '{parsedSource.CacheField}' of the record a search finds, and a search returns only the record's id; write search.{parsedSource.CacheType}.id.");
        }

        if (!parsedSource.Resolves && y.FindBy is not null)
        {
            throw new FlowValidationException($"{where}: 'findBy' only applies to a cache or a search source; {parsedSource} reads the dataset.");
        }

        if (parsedSource.Kind != MappingSourceKind.Cache && y.IgnoreSeparators is not null)
        {
            throw new FlowValidationException($"{where}: 'ignoreSeparators' loosens how a value is matched against the cache, so it only applies to a cache source.");
        }

        var findBy = parsedSource.Resolves ? FindByLines(y.FindBy, parsedSource.CacheType!, where, parsedSource.Prefix) : [];
        if (parsedSource.Resolves && findBy.Count == 0)
        {
            throw new FlowValidationException(
                $"{where}: a {parsedSource.Prefix} source needs 'findBy', which says which record to read, such as findBy: {parsedSource.Prefix}.{parsedSource.CacheType}.Code = dataset.column.");
        }

        var modifiers = (y.Modifiers ?? []).Select((modifier, i) => ParseModifier(modifier, $"{where} modifiers[{i}]")).ToList();
        if (modifiers.Count > 0 && parsedSource.Resolves && findBy.All(f => f.Column is null))
        {
            var found = parsedSource.Kind == MappingSourceKind.Search ? "what a search finds is" : "cache values are";
            throw new FlowValidationException($"{where}: modifiers change incoming dataset values, and this entry's findBy reads none; {found} never modified.");
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

        if (parts[0] == MappingSource.SearchPrefix)
        {
            if (parts.Length < 3 || !NamePattern().IsMatch(parts[1]) || parts.Skip(2).Any(p => !FieldPattern().IsMatch(p)))
            {
                throw new FlowValidationException($"{where}: source '{text}' must be search.<name>.<field>, such as search.Wellbore.id.");
            }

            return new MappingSource { Kind = MappingSourceKind.Search, CacheType = parts[1], CacheField = string.Join('.', parts.Skip(2)) };
        }

        throw new FlowValidationException(
            $"{where}: source '{text}' must start with 'dataset.' (the incoming dataset), 'cache.' (the partition's cache) or 'search.' (a record found on the platform as the render needs it); a fixed value is written with 'static'.");
    }

    /// <summary>The part of a record a search compares: its data, as the schema of the kind searched describes it.</summary>
    private const string SearchDataPrefix = "data.";

    /// <summary>
    /// A <c>searches</c> entry: the kind searched and the saved schema that says how its properties are indexed. The
    /// kind names one entity type, since one schema describes everything its query can reach, and the schema is a
    /// version of that entity type: the one searched, when the kind names a version.
    /// </summary>
    private static MappingSearch ParseSearch(string name, MappingSearchYaml? y, string source)
    {
        var where = $"{source}: search '{name}'";
        if (!NamePattern().IsMatch(name))
        {
            throw new FlowValidationException(
                $"{where} is named with something other than letters, digits, underscores and hyphens; entries write the name in search.<name>.id.");
        }

        var kind = y?.Kind?.Trim();
        if (string.IsNullOrEmpty(kind))
        {
            throw new FlowValidationException($"{where} declares no kind; a search names the OSDU kind it looks in, such as osdu:wks:master-data--Wellbore:*.");
        }

        var segments = kind.Split(':');
        if (!OsduKind.IsValid(kind) || segments.Length != 4)
        {
            throw new FlowValidationException(
                $"{where}: kind '{kind}' is not an OSDU kind; a kind is authority:source:entityType:version, such as osdu:wks:master-data--Wellbore:*.");
        }

        if (segments.Take(3).Any(s => s.Contains('*', StringComparison.Ordinal)))
        {
            throw new FlowValidationException(
                $"{where}: kind '{kind}' leaves its authority, source or entity type open; a search looks in one entity type, whose schema says how the properties it compares are indexed, so only the version may be '*'.");
        }

        var schema = y!.Schema ?? throw new FlowValidationException(
            $"{where} pins no schema; a search pins the saved template of the kind it searches (schema: {{ kind: {segments[0]}:{segments[1]}:{segments[2]}:<version>, version: <template version> }}), which says how each property it compares is indexed. Save the kind's schema on the Templates page, or with 'sqlflow template import'.");
        var schemaKind = FlowMapper.Require(schema.Kind, $"searches.{name}.schema.kind", source);
        if (!FlowMapper.IsRecordKind(schemaKind))
        {
            throw new FlowValidationException(
                $"{where}: schema.kind '{schemaKind}' must be 'authority:source:entityType:major.minor.patch', the kind of a saved template.");
        }

        var schemaSegments = schemaKind.Split(':');
        if (!segments.Take(3).SequenceEqual(schemaSegments.Take(3), StringComparer.Ordinal))
        {
            throw new FlowValidationException(
                $"{where} searches {kind} and pins the schema of {schemaKind}, another entity type; the schema has to describe the records the query reaches.");
        }

        if (!segments[3].Contains('*', StringComparison.Ordinal) && !string.Equals(segments[3], schemaSegments[3], StringComparison.Ordinal))
        {
            throw new FlowValidationException(
                $"{where} searches version {segments[3]} and pins the schema of version {schemaSegments[3]}; pin the schema of the version it searches.");
        }

        var schemaVersion = FlowMapper.Require(schema.Version, $"searches.{name}.schema.version", source);
        if (!TemplateVersionPattern().IsMatch(schemaVersion))
        {
            throw new FlowValidationException(
                $"{where}: schema.version '{schemaVersion}' is not a template version. A version is the 16 hexadecimal characters the Templates page shows for a saved template, such as 58d6bdbd9d066a06.");
        }

        var description = y.Description?.Trim();
        return new MappingSearch(name, kind, new TemplateReference(schemaKind, schemaVersion), string.IsNullOrEmpty(description) ? null : description);
    }

    /// <summary>
    /// Every <c>search.&lt;name&gt;</c> an entry reads names a set the mapping declares, and every set declared is read
    /// by something. A name that is not declared would otherwise fail at render, one row at a time, against a platform.
    /// </summary>
    private static void ValidateSearches(
        IReadOnlyList<MappingEntry> entries, IReadOnlyDictionary<string, MappingSearch> searches, string source)
    {
        foreach (var entry in entries.Where(e => e.Source?.Kind == MappingSourceKind.Search))
        {
            var name = entry.Source!.CacheType!;
            if (!searches.ContainsKey(name))
            {
                throw new FlowValidationException(
                    searches.Count == 0
                        ? $"{source}: {entry.Target.Text} reads search.{name}, and the mapping declares no searches; add a 'searches' block naming the kind each one looks in."
                        : $"{source}: {entry.Target.Text} reads search.{name}, which the mapping does not declare; it declares {string.Join(", ", searches.Keys.Order(StringComparer.Ordinal))}.");
            }
        }

        // One declaration per kind: entries that look in the same kind read the same search, so a question and its answer
        // always belong to one search.
        foreach (var shared in searches.Values.GroupBy(s => s.Kind, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            throw new FlowValidationException(
                $"{source}: searches {string.Join(" and ", shared.Select(s => $"'{s.Name}'").Order(StringComparer.Ordinal))} both look in {shared.Key}; declare it once and have every entry that looks there read it.");
        }

        var read = entries.Where(e => e.Source?.Kind == MappingSourceKind.Search).Select(e => e.Source!.CacheType!).ToHashSet(StringComparer.Ordinal);
        foreach (var unread in searches.Keys.Where(k => !read.Contains(k)).Order(StringComparer.Ordinal))
        {
            throw new FlowValidationException(
                $"{source}: search '{unread}' is declared and nothing reads it; a search costs a call to the platform for every value it is asked about, so one nothing reads is a mistake rather than spare capacity.");
        }
    }

    private static List<FindBy> FindByLines(object? value, string type, string where, string prefix)
    {
        List<string> lines = value switch
        {
            null => [],
            string one => [one],
            IEnumerable<object> many => many.Select(line => line as string
                ?? throw new FlowValidationException($"{where}: each findBy line is text, such as {prefix}.{type}.Code = dataset.column.")).ToList(),
            _ => throw new FlowValidationException($"{where}: findBy is one line or a list of lines, such as {prefix}.{type}.Code = dataset.column."),
        };

        var result = new List<FindBy>(lines.Count);
        foreach (var line in lines)
        {
            var match = FindByPattern().Match(line);
            if (!match.Success)
            {
                throw new FlowValidationException($"{where}: findBy '{line}' must read {prefix}.<name>.<field> = dataset.<column>, or = 'text' for a fixed text.");
            }

            if (!string.Equals(match.Groups["prefix"].Value, prefix, StringComparison.Ordinal)
                || !string.Equals(match.Groups["type"].Value, type, StringComparison.Ordinal))
            {
                throw new FlowValidationException(
                    $"{where}: findBy '{line}' compares {match.Groups["prefix"].Value}.{match.Groups["type"].Value}, but the entry reads {prefix}.{type}; findBy selects a record of the set the entry reads.");
            }

            var field = match.Groups["field"].Value;
            if (field.Split('.').Any(p => !FieldPattern().IsMatch(p)))
            {
                throw new FlowValidationException($"{where}: findBy '{line}' names an invalid field '{field}'.");
            }

            // A search compares a property of the records' data, named the way a query names it: unquoted, so only
            // letters, digits and underscores, and never the record's own metadata, which the indexer maps apart from
            // the schema and a lookup has no business comparing.
            if (prefix == MappingSource.SearchPrefix
                && (!field.StartsWith(SearchDataPrefix, StringComparison.Ordinal) || !OsduPath.IsPath(field)))
            {
                throw new FlowValidationException(
                    $"{where}: findBy '{line}' compares '{field}', and a search compares a property under data, named by dotted names of letters, digits and underscores, such as {prefix}.{type}.data.FacilityName.");
            }

            var operand = match.Groups["operand"].Value.Trim();
            if (Quoted(operand) is { } literal)
            {
                if (literal.Length == 0)
                {
                    throw new FlowValidationException($"{where}: findBy '{line}' compares with empty text.");
                }

                result.Add(new FindBy(type, field, null, literal) { Prefix = prefix });
                continue;
            }

            result.Add(new FindBy(type, field, Column(operand, $"{where}: findBy '{line}'"), null) { Prefix = prefix });
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

    /// <summary>The modifiers a mapping entry takes, by the name it writes them with.</summary>
    internal static readonly IReadOnlyList<string> ModifierNames = ["trim", "upper", "lower", "split", "replace", "equals", "date", "number"];

    /// <summary>The settings a replace takes beside its table: what an unlisted value becomes, and a cached table's fields.</summary>
    internal static readonly IReadOnlyList<string> ReplaceSettings = ["otherwise", "match", "field"];

    /// <summary>The settings of split.</summary>
    internal static readonly IReadOnlyList<string> SplitSettings = ["separator", "part"];

    /// <summary>The settings of number.</summary>
    internal static readonly IReadOnlyList<string> NumberSettings = ["decimal", "group"];

    /// <summary>The modifiers, as a message lists them.</summary>
    private static string ModifierList => string.Join(", ", ModifierNames.Take(ModifierNames.Count - 1)) + " and " + ModifierNames[^1];

    private static string SettingList(IReadOnlyList<string> settings)
        => string.Join(", ", settings.Take(settings.Count - 1).Select(s => $"'{s}'")) + $" and '{settings[^1]}'";

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
                    "split" or "equals" => throw new FlowValidationException($"{where}: '{name}' needs settings, such as {Example(name)}."),
                    "replace" => throw new FlowValidationException($"{where}: 'replace' needs a table, such as {Example(name)}."),
                    _ => throw new FlowValidationException($"{where}: '{name}' is not a modifier. The modifiers are {ModifierList}."),
                };

            // A replace takes its settings beside it rather than inside its table, so no incoming value is ever read as a
            // setting: every key of the table is a value to replace.
            case IDictionary<object, object> map when map.Keys.Any(k => KeyText(k) == "replace"):
                return Replace(map, where);

            case IDictionary<object, object> map when map.Count == 1:
                {
                    var (key, settings) = map.First();
                    var modifier = KeyText(key);
                    return modifier switch
                    {
                        "split" => Split(settings, where),
                        "equals" => settings is null or IDictionary<object, object> or IList<object>
                            ? throw new FlowValidationException($"{where}: equals compares with one text, such as {Example("equals")}.")
                            : new Modifier { Kind = ModifierKind.Equals, Text = Convert.ToString(settings, CultureInfo.InvariantCulture) },
                        "date" => Date(settings, where),
                        "number" => Number(settings, where),
                        _ => throw new FlowValidationException($"{where}: '{modifier}' is not a modifier. The modifiers are {ModifierList}."),
                    };
                }

            case IDictionary<object, object> map when map.Count > 1:
                throw new FlowValidationException(
                    $"{where}: a modifier is one setting, such as {Example("split")}; {string.Join(" and ", map.Keys.Select(KeyText))} are written as one. Only replace takes a setting beside it (otherwise).");

            default:
                throw new FlowValidationException($"{where}: a modifier is a name, such as trim, or one setting, such as {Example("split")}.");
        }
    }

    private static string KeyText(object key) => Convert.ToString(key, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;

    /// <summary>
    /// A scalar as the text the document wrote: a boolean as <c>true</c> or <c>false</c>, which is how YAML spells it, and a
    /// number in the invariant culture.
    /// </summary>
    private static string ScalarText(object value) => value switch
    {
        bool flag => flag ? "true" : "false",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

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
            if (option is null || !NumberSettings.Contains(option))
            {
                throw new FlowValidationException($"{where}: number takes {SettingList(NumberSettings)}, not '{option}'.");
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
            if (option is null || !SplitSettings.Contains(option))
            {
                throw new FlowValidationException($"{where}: split takes {SettingList(SplitSettings)}, not '{option}'.");
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

    /// <summary>
    /// A replace: its table (<c>replace: { M: m, NONE: ~ }</c>) and, beside it, what a value the table does not list becomes
    /// (<c>otherwise: ~</c>, or a text). A key is matched trimmed, so two keys that are the same once trimmed, or a key that
    /// is empty once trimmed, are refused here rather than leaving a render to choose between them.
    /// </summary>
    private static Modifier Replace(IDictionary<object, object> item, string where)
    {
        object? table = null;
        var fallback = ReplaceFallback.Keep;
        string? match = null;
        string? field = null;
        foreach (var (key, value) in item)
        {
            switch (KeyText(key))
            {
                case "replace":
                    table = value;
                    break;
                case "otherwise":
                    fallback = value switch
                    {
                        null => ReplaceFallback.Empty,
                        IDictionary<object, object> or IList<object> => throw new FlowValidationException(
                            $"{where}: replace's otherwise is one text, or ~ for no value, such as otherwise: ~."),
                        _ => ReplaceFallback.Of(ScalarText(value)),
                    };
                    break;
                case "match":
                    match = ReplaceField(value, "match", where);
                    break;
                case "field":
                    field = ReplaceField(value, "field", where);
                    break;
                case var other:
                    throw new FlowValidationException($"{where}: replace takes {SettingList(ReplaceSettings)} beside it, not '{other}'.");
            }
        }

        // A table read from the cache is named, not written: replace: cache.<Type>.
        if (table is string named)
        {
            return new Modifier { Kind = ModifierKind.Replace, Table = CachedTable(named, match, field, where), Otherwise = fallback };
        }

        if (match is not null || field is not null)
        {
            throw new FlowValidationException(
                $"{where}: 'match' and 'field' choose the fields of a table read from the cache (replace: cache.<Type>); a table written in the mapping matches on its keys and replaces with what each lists.");
        }

        if (table is not IDictionary<object, object> pairs || pairs.Count == 0)
        {
            throw new FlowValidationException($"{where}: replace lists incoming values and what each becomes, such as {Example("replace")}.");
        }

        var replacements = new Dictionary<string, string?>(StringComparer.Ordinal);
        var trimmed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (from, to) in pairs)
        {
            var fromText = ScalarText(from);
            var key = fromText.Trim();
            if (key.Length == 0)
            {
                throw new FlowValidationException($"{where}: replace lists an empty incoming value; an empty value is never replaced, it stays empty.");
            }

            if (!trimmed.TryAdd(key, fromText))
            {
                throw new FlowValidationException(
                    $"{where}: replace lists '{trimmed[key]}' and '{fromText}', which are the same value once surrounding spaces are removed, and a value is matched trimmed.");
            }

            replacements[fromText] = to switch
            {
                null => null,
                IDictionary<object, object> or IList<object> => throw new FlowValidationException(
                    $"{where}: replace maps '{fromText}' to something that is not text; write a text, or ~ for no value."),
                _ => ScalarText(to),
            };
        }

        return new Modifier { Kind = ModifierKind.Replace, Replacements = replacements, Otherwise = fallback };
    }

    /// <summary>The cached table a replace names: <c>cache.&lt;Type&gt;</c>, with the fields it matches on and replaces by.</summary>
    private static CachedReplaceTable CachedTable(string named, string? match, string? field, string where)
    {
        var parts = named.Trim().Split('.');
        if (parts.Length != 2 || parts[0] != MappingSource.CachePrefix || !NamePattern().IsMatch(parts[1]))
        {
            throw new FlowValidationException(
                $"{where}: replace names '{named}', and a table read from the cache is named cache.<Type>, such as replace: cache.RecallUnits; a table written here is a map, such as {Example("replace")}.");
        }

        return new CachedReplaceTable(parts[1], match, field);
    }

    /// <summary>A replace's match or field: the name of a cached field, or a path into one.</summary>
    private static string ReplaceField(object? value, string setting, string where)
    {
        var text = value is null or IDictionary<object, object> or IList<object> ? null : ScalarText(value).Trim();
        if (string.IsNullOrEmpty(text) || text.Split('.').Any(part => !FieldPattern().IsMatch(part)))
        {
            throw new FlowValidationException($"{where}: replace's {setting} names a field of the cached table, such as {setting}: {(setting == "match" ? "mnemonic" : "family")}.");
        }

        return text;
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

    [GeneratedRegex(@"^\s*(?<prefix>cache|search)\.(?<type>[A-Za-z0-9_\-]+)\.(?<field>[^=\s]+)\s*=\s*(?<operand>.+?)\s*$")]
    private static partial Regex FindByPattern();

    [GeneratedRegex(@"^(?<column>dataset\.\S+)\s+is\s+(?<not>not\s+)?(?<rest>.+)$")]
    private static partial Regex ConditionPattern();

    [GeneratedRegex(@"\{(?<column>dataset\.[A-Za-z0-9_\-\.]+)\}")]
    internal static partial Regex LabelToken();
}
