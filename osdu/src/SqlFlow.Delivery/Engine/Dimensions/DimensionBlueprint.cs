using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Engine.Dimensions;

/// <summary>A saved template a blueprint describes a part of the dimension by: its kind and content version.</summary>
public sealed record BlueprintTemplate(string Kind, string Version);

/// <summary>
/// What a read of a blueprint is for: the dimension's key, its label, an attribute read through the record a key names, or
/// one collected from the dimension's own records (<see cref="BlueprintRoles"/>); the attribute's name; the step of its
/// chain the read is, from 0; and whether it is that chain's last, whose value the dimension keeps.
/// </summary>
public sealed record BlueprintUse(string Role, string? Attribute, int Step, bool Last);

/// <summary>How a build's search reads a path of the dimension's own records: the index's type, the nested array it sits in, the field aggregated, and whether a record holds several.</summary>
public sealed record BlueprintIndex(string Index, string? NestedPath, string AggregateBy, bool Repeats);

/// <summary>
/// One path a build reads, in the records it reads it from: an id of its own, the path as the flow writes it, what it is
/// read for, what the template says of each of its segments (null where no template describes the records), the entity
/// types its value names, the records read next through it (a <see cref="BlueprintRecords.Id"/>), how the search reads it
/// for a path of the dimension's own records, and what keeps it from being read as the flow means it.
/// </summary>
public sealed record BlueprintRead(
    string Id, string Path, IReadOnlyList<BlueprintUse> Uses, SchemaPathReading? Schema, IReadOnlyList<string> References, string? LeadsTo,
    BlueprintIndex? Index, string? Problem);

/// <summary>
/// Records a build reads by id, one step from the key or from records read before: their depth (1 for the records the keys
/// name), the entity types they are of (none where no template says), the read whose ids name them, the template they are
/// described by, why there is none, and the paths read from them.
/// </summary>
public sealed record BlueprintRecords(
    string Id, int Depth, IReadOnlyList<string> EntityTypes, string From, BlueprintTemplate? Template, string? Missing, IReadOnlyList<BlueprintRead> Reads);

/// <summary>A kind of the dimension's own records: the kind, its records where a build counted them, and the version of its saved template.</summary>
public sealed record BlueprintKind(string Kind, long? Records, string? Template);

/// <summary>
/// The dimension's own records: the kind pattern and query that choose them, whether the kinds are those its last build read
/// or the saved templates the pattern matches, the template the reads are described by, why there is none, and the reads:
/// the key, then each collected attribute.
/// </summary>
public sealed record BlueprintSource(
    string Kind, string? Query, bool Built, IReadOnlyList<BlueprintKind> Kinds, BlueprintTemplate? Template, string? Missing, IReadOnlyList<BlueprintRead> Reads);

/// <summary>A column of the dimension's table, in the table's order: its name, what it holds (<see cref="BlueprintRoles"/>), the attribute it is, and the reads it is written from.</summary>
public sealed record BlueprintColumn(string Name, string Role, string? Attribute, IReadOnlyList<string> From);

/// <summary>
/// How a dimension is built, as its declaration and the saved templates of the records it reads say (docs/dimension-plan.md,
/// The blueprint): the dimension's own records and the paths read from them, the records read through each key by id, step by
/// step, grouped as a build's searches group them (one entity type at one depth), and the table's columns with the reads each
/// is written from.
/// </summary>
public sealed record DimensionBlueprint(BlueprintSource Source, IReadOnlyList<BlueprintRecords> Records, IReadOnlyList<BlueprintColumn> Columns);

/// <summary>The words a blueprint names what a read is for, and what a column holds, by.</summary>
public static class BlueprintRoles
{
    public const string Key = "key";
    public const string Label = "label";
    public const string Attribute = "attribute";
    public const string Collect = "collect";

    public const string Id = "id";
    public const string Partition = "partition";
    public const string KeyId = "keyId";
    public const string Value = "value";
    public const string Records = "records";
    public const string Filter = "filter";

    /// <summary>The id of the read of a dimension's key.</summary>
    public const string KeyRead = "key";

    /// <summary>The id of the read of a collected attribute.</summary>
    public static string CollectRead(string attribute) => "collect:" + attribute;
}

/// <summary>
/// Lays out a dimension's blueprint. It reads the declaration and the saved templates only, never OSDU and never a record:
/// the key and a collected attribute are described as a build's search reads them (<see cref="SearchFields"/>, the same
/// classification the build settles its field by), and each step of a label or an attribute in the template of the
/// entity type the step before names (<c>x-osdu-relationship</c>), the newest saved of it, the way a person reads it
/// (<see cref="SchemaPathReader"/>). A template that is not saved leaves its records undescribed and says so; nothing is
/// refused here, since a build reads a label's records as they are.
/// </summary>
public static class DimensionBlueprints
{
    /// <summary>
    /// The blueprint of <paramref name="dimension"/>, its own records described by the kinds its last build read
    /// (<paramref name="built"/>, with the template each was read against) or, before a build, by the saved templates its
    /// kind pattern matches.
    /// </summary>
    public static async Task<DimensionBlueprint> DescribeAsync(
        DimensionSpec dimension, ITemplateStore templates, IReadOnlyList<DimensionKind> built, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dimension);
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(built);
        var saved = await templates.ListAsync(ct).ConfigureAwait(false);
        var schemas = new Dictionary<BlueprintTemplate, SchemaSnapshot?>();

        async Task<SchemaSnapshot?> LoadAsync(BlueprintTemplate template)
        {
            if (!schemas.TryGetValue(template, out var schema))
            {
                schema = await templates.LoadAsync(new TemplateReference(template.Kind, template.Version), ct).ConfigureAwait(false);
                schemas[template] = schema;
            }

            return schema;
        }

        // The dimension's own records: the kinds its last build read, or the saved kinds its pattern matches.
        List<BlueprintKind> kinds;
        BlueprintTemplate? sourceTemplate;
        string? sourceMissing = null;
        if (built.Count > 0)
        {
            kinds = built.OrderByDescending(k => k.Records).ThenBy(k => k.Kind, StringComparer.Ordinal)
                .Select(k => new BlueprintKind(k.Kind, k.Records, k.Template)).ToList();
            sourceTemplate = kinds.Where(k => k.Template is not null).Select(k => new BlueprintTemplate(k.Kind, k.Template!)).FirstOrDefault();
            if (sourceTemplate is null)
            {
                sourceMissing = $"The last build read {dimension.Path} without a saved template of {string.Join(", ", kinds.Select(k => k.Kind).Take(5))}.";
            }
        }
        else
        {
            var matching = saved.Where(t => KindPatterns.Matches(dimension.Kind, t.Kind))
                .GroupBy(t => t.Kind, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(t => t.CapturedUtc).First())
                .OrderByDescending(t => KindPatterns.VersionOf(t.Kind)).ThenBy(t => t.Kind, StringComparer.Ordinal)
                .ToList();
            kinds = matching.Select(t => new BlueprintKind(t.Kind, null, t.Version)).ToList();
            sourceTemplate = matching.Select(t => new BlueprintTemplate(t.Kind, t.Version)).FirstOrDefault();
            if (sourceTemplate is null)
            {
                sourceMissing = SearchFields.IsDataPath(dimension.Path)
                    ? $"No saved template matches {dimension.Kind}, and a build reads a path of data only by the template of every kind it matches. Save the kind's template on the Templates page."
                    : $"No saved template matches {dimension.Kind}; the paths read from its records are not described.";
            }
        }

        var sourceSchema = sourceTemplate is null ? null : await LoadAsync(sourceTemplate).ConfigureAwait(false);
        if (sourceTemplate is not null && sourceSchema is null)
        {
            sourceMissing = $"The template {sourceTemplate.Kind} version {sourceTemplate.Version} is no longer saved, so the paths read from the records are not described.";
            sourceTemplate = null;
        }

        var follows = dimension.Label.Count > 0 || dimension.Attributes.Any(a => !a.IsCollected);
        var key = SourceRead(BlueprintRoles.KeyRead, dimension.Path, new BlueprintUse(BlueprintRoles.Key, null, 0, true), sourceSchema);
        if (follows && key.Problem is null && key.Schema is { Complete: true } && key.References.Count == 0)
        {
            key.Problem = $"The template does not declare {dimension.Path} a reference to another record: a key's label and attributes are read only where the key is a record id.";
        }

        var sourceReads = new List<ReadDraft> { key };
        foreach (var attribute in dimension.Attributes.Where(a => a.IsCollected))
        {
            sourceReads.Add(SourceRead(
                BlueprintRoles.CollectRead(attribute.Name), attribute.Collect!, new BlueprintUse(BlueprintRoles.Collect, attribute.Name, 0, true), sourceSchema));
        }

        // The records read by id, step by step: a step's records are those the read before it names, grouped by entity type and
        // depth as a build groups its searches, so two attributes reading the wellbore read it in the same node.
        var nodes = new List<NodeDraft>();
        var chains = new List<(string Role, string? Attribute, IReadOnlyList<string> Steps)>();
        if (dimension.Label.Count > 0)
        {
            chains.Add((BlueprintRoles.Label, null, dimension.Label));
        }

        chains.AddRange(dimension.Attributes.Where(a => !a.IsCollected).Select(a => (BlueprintRoles.Attribute, (string?)a.Name, a.Steps)));
        var lastReads = new Dictionary<(string Role, string? Attribute), string>();
        foreach (var (role, attribute, steps) in chains)
        {
            var parent = key;
            for (var step = 0; step < steps.Count; step++)
            {
                var types = parent.References;
                var depth = step + 1;
                var nodeId = types.Count > 0 ? $"{depth}:{string.Join('|', types)}" : $"{depth}:from:{parent.Id}";
                var node = nodes.FirstOrDefault(n => n.Id == nodeId);
                if (node is null)
                {
                    node = await NodeAsync(nodeId, depth, types, parent, saved, LoadAsync).ConfigureAwait(false);
                    nodes.Add(node);
                }

                parent.LeadsTo ??= node.Id;
                var last = step == steps.Count - 1;
                var read = node.Read(steps[step], role, attribute, step, last);
                if (!last && read.Problem is null && read.Schema is { Complete: true } && read.References.Count == 0)
                {
                    read.Problem = $"The template does not declare {read.Path} a reference to another record, so the next step reads records only where it holds a record id.";
                }

                if (last && read.Problem is null && read.Schema?.Leaf?.Type == "object")
                {
                    read.Problem = $"{read.Path} holds an object, not a value; a build keeps only text it finds there, so name one of its properties.";
                }

                parent = read;
            }

            lastReads[(role, attribute)] = parent.Id;
        }

        var columns = new List<BlueprintColumn>
        {
            new("id", BlueprintRoles.Id, null, []),
            new("partition", BlueprintRoles.Partition, null, []),
            new("key_id", BlueprintRoles.KeyId, null, [BlueprintRoles.KeyRead]),
            new(dimension.KeyColumn, BlueprintRoles.Key, null, [BlueprintRoles.KeyRead]),
            new(dimension.ValueColumn, BlueprintRoles.Value, null, [lastReads.GetValueOrDefault((BlueprintRoles.Label, null)) ?? BlueprintRoles.KeyRead]),
            new("records", BlueprintRoles.Records, null, [BlueprintRoles.KeyRead, .. dimension.Attributes.Where(a => a.IsCollected).Select(a => BlueprintRoles.CollectRead(a.Name))]),
            new("filter", BlueprintRoles.Filter, null, [BlueprintRoles.KeyRead]),
        };
        columns.AddRange(dimension.Attributes.Select(a => new BlueprintColumn(
            a.Name, BlueprintRoles.Attribute, a.Name,
            [a.IsCollected ? BlueprintRoles.CollectRead(a.Name) : lastReads.GetValueOrDefault((BlueprintRoles.Attribute, a.Name)) ?? BlueprintRoles.KeyRead])));

        return new DimensionBlueprint(
            new BlueprintSource(dimension.Kind, dimension.Query, built.Count > 0, kinds, sourceTemplate, sourceMissing, sourceReads.Select(r => r.Freeze()).ToList()),
            nodes.Select(n => n.Freeze()).ToList(),
            columns);
    }

    /// <summary>A path of the dimension's own records, described by their template and classified as the build's search reads it.</summary>
    private static ReadDraft SourceRead(string id, string path, BlueprintUse use, SchemaSnapshot? schema)
    {
        var (parsed, _) = DimensionPath.Parse(path);
        var reading = schema is not null && parsed is not null ? SchemaPathReader.Read(schema, parsed) : null;
        BlueprintIndex? index = null;
        string? problem = null;
        if (SearchFields.IsDataPath(path))
        {
            if (schema is not null)
            {
                var shape = SearchFields.ClassifyValue(schema, path);
                index = shape.Field is { } field ? new BlueprintIndex(DimensionRunner.Index(field.Index), field.NestedPath, field.AggregateBy, shape.Repeats) : null;
                problem = shape.Problem is null ? null : $"A build cannot read {path}: {shape.Problem}";
            }
        }
        else
        {
            var shape = SearchFields.RecordProperty(path);
            index = shape.Field is { } field ? new BlueprintIndex(DimensionRunner.Index(field.Index), field.NestedPath, field.AggregateBy, shape.Repeats) : null;
            problem = shape.Problem is null ? null : $"A build cannot read {path}: {shape.Problem}";
        }

        var draft = new ReadDraft(id, path, reading) { Index = index, Problem = problem };
        draft.Uses.Add(use);
        return draft;
    }

    /// <summary>
    /// The records a read's ids name at <paramref name="depth"/>, described by the newest saved template of the first of their
    /// entity types that has one.
    /// </summary>
    private static async Task<NodeDraft> NodeAsync(
        string id, int depth, IReadOnlyList<string> types, ReadDraft from, IReadOnlyList<TemplateInfo> saved, Func<BlueprintTemplate, Task<SchemaSnapshot?>> load)
    {
        if (types.Count == 0)
        {
            var why = from.Schema is null
                ? $"No template describes the records {from.Path} is read from, so which records its ids name is not known: a build reads whatever records they name."
                : $"The template does not say which records {from.Path} names: a build reads whatever records its ids name.";
            return new NodeDraft(id, depth, types, from.Id, null, null, why);
        }

        foreach (var type in types)
        {
            var newest = saved.Where(t => string.Equals(KindPatterns.EntityTypeOf(t.Kind), type, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(t => KindPatterns.VersionOf(t.Kind)).ThenByDescending(t => t.CapturedUtc)
                .FirstOrDefault();
            if (newest is null)
            {
                continue;
            }

            var template = new BlueprintTemplate(newest.Kind, newest.Version);
            if (await load(template).ConfigureAwait(false) is { } schema)
            {
                return new NodeDraft(id, depth, types, from.Id, template, schema, null);
            }
        }

        return new NodeDraft(
            id, depth, types, from.Id, null, null,
            $"No template of {string.Join(" or ", types)} is saved, so the paths read from these records are read as written, unchecked. Save one on the Templates page to check them.");
    }

    /// <summary>A read while the blueprint is laid out: what it is for and where it leads gather as chains reach it.</summary>
    private sealed class ReadDraft(string id, string path, SchemaPathReading? schema)
    {
        public string Id { get; } = id;

        public string Path { get; } = path;

        public SchemaPathReading? Schema { get; } = schema;

        public List<BlueprintUse> Uses { get; } = [];

        public IReadOnlyList<string> References => Schema?.References ?? [];

        public string? LeadsTo { get; set; }

        public BlueprintIndex? Index { get; init; }

        public string? Problem { get; set; }

        public BlueprintRead Freeze() => new(Id, Path, Uses, Schema, References, LeadsTo, Index, Problem ?? Schema?.Problem);
    }

    /// <summary>Records read by id while the blueprint is laid out, with the reads the chains reaching them add.</summary>
    private sealed class NodeDraft(string id, int depth, IReadOnlyList<string> types, string from, BlueprintTemplate? template, SchemaSnapshot? schema, string? missing)
    {
        private readonly List<ReadDraft> _reads = [];

        public string Id { get; } = id;

        /// <summary>The read of <paramref name="path"/> in these records, made when no chain has read it before.</summary>
        public ReadDraft Read(string path, string role, string? attribute, int step, bool last)
        {
            var read = _reads.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.Ordinal));
            if (read is null)
            {
                var (parsed, _) = DimensionPath.Parse(path);
                read = new ReadDraft($"{Id}/{path}", path, schema is not null && parsed is not null ? SchemaPathReader.Read(schema, parsed) : null);
                _reads.Add(read);
            }

            read.Uses.Add(new BlueprintUse(role, attribute, step, last));
            return read;
        }

        public BlueprintRecords Freeze() => new(Id, depth, types, from, template, missing, _reads.Select(r => r.Freeze()).ToList());
    }
}

/// <summary>Kinds as a dimension names them: a pattern with a wildcard per segment, matched against the saved kinds.</summary>
public static class KindPatterns
{
    /// <summary>
    /// Whether <paramref name="pattern"/> (<c>osdu:wks:work-product-component--WellLog:1.*</c>) matches <paramref name="kind"/>:
    /// four segments each, each the same ignoring case or matched by its wildcards, a star standing for any text.
    /// </summary>
    public static bool Matches(string pattern, string kind)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(kind);
        var want = pattern.Split(':');
        var have = kind.Split(':');
        return want.Length == 4 && have.Length == 4 && want.Zip(have).All(s => Glob(s.First, s.Second));
    }

    /// <summary>The entity type of an exact kind (<c>master-data--Wellbore</c>), or null for one without four segments.</summary>
    public static string? EntityTypeOf(string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        var segments = kind.Split(':');
        return segments.Length == 4 ? segments[2] : null;
    }

    /// <summary>The version of an exact kind (<c>1.3.0</c>), for ordering; zero when it does not parse.</summary>
    public static Version VersionOf(string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        var segments = kind.Split(':');
        return segments.Length == 4 && Version.TryParse(segments[3], out var version) ? version : new Version(0, 0);
    }

    /// <summary>
    /// A star matching any text, every other character itself ignoring case. It never recurses, and only ever returns to the
    /// last star, so a segment costs at most the product of the two lengths.
    /// </summary>
    private static bool Glob(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] != '*' && char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t]))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}
