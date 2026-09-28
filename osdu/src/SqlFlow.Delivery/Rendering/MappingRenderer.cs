using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using ContentHash = SqlFlow.Delivery.Hashing.ContentHash;

namespace SqlFlow.Delivery.Rendering;

/// <summary>
/// Renders one record of the incoming dataset into an OSDU record (docs/delivery/mapping-templates.md). The engine writes
/// <c>id</c> from the dataset key and <c>kind</c> from the template, then writes each entry's value at its template
/// variable, taking the type from the template. The output is a pure function of the source record and the
/// <see cref="RenderContext"/>.
/// </summary>
public sealed class MappingRenderer
{
    private readonly MappingDefinition _mapping;
    private readonly SchemaSnapshot _schema;
    private readonly ReferenceSnapshot _references;
    private readonly RenderContext _context;
    private readonly IReadOnlyList<string> _requiredData;
    private readonly IReadOnlyList<MappingEntry> _recordEntries;
    private readonly IReadOnlyList<(MappingEntry Repeater, IReadOnlyList<MappingEntry> Items)> _repeaters;
    private readonly IReadOnlyList<string>? _owned;
    private readonly ResolvedSearches _searches;
    private readonly IRecordSearch _search;
    private readonly IReadOnlyDictionary<(int Index, string? EntityType), (IdTemplate? Template, string? Problem)> _referenceTemplates;
    private readonly IReadOnlyDictionary<string, ReferenceSnapshot> _fixtureCaches;

    private static readonly IReadOnlyDictionary<string, ReferenceSnapshot> NoFixtureCaches = new Dictionary<string, ReferenceSnapshot>(StringComparer.Ordinal);

    /// <param name="mapping">The mapping rendered.</param>
    /// <param name="schema">The template the mapping pins.</param>
    /// <param name="references">The version of the partition's cache a <c>cache.</c> source reads.</param>
    /// <param name="context">What the render is pinned to, and the mapping's parameters.</param>
    /// <param name="searches">
    /// The mapping's searches resolved against the schemas they pin, which say how each property a <c>search.</c> source
    /// compares is indexed. A mapping that searches nothing needs none; a search source whose property is not resolved
    /// here holds its record rather than guess at a query.
    /// </param>
    /// <param name="search">
    /// Where a <c>search.</c> source is answered. A mapping with no search never consults it, so a render of one may be
    /// given none; a mapping that searches and is given none finds nothing and holds.
    /// </param>
    /// <param name="fixtureCaches">
    /// The caches of the other partitions the mapping's fixtures are written for, by partition, each at its current version
    /// (<see cref="Validation.Preflight.FixturePartitions"/>): a fixture renders against the cache of the partition it names,
    /// wherever the mapping runs. Only a fixture render reads them.
    /// </param>
    public MappingRenderer(
        MappingDefinition mapping,
        SchemaSnapshot schema,
        ReferenceSnapshot references,
        RenderContext context,
        ResolvedSearches? searches = null,
        IRecordSearch? search = null,
        IReadOnlyDictionary<string, ReferenceSnapshot>? fixtureCaches = null)
        : this(mapping, schema, references, context, requireParameters: true, searches, search, fixtureCaches)
    {
    }

    private MappingRenderer(
        MappingDefinition mapping,
        SchemaSnapshot schema,
        ReferenceSnapshot references,
        RenderContext context,
        bool requireParameters,
        ResolvedSearches? searches = null,
        IRecordSearch? search = null,
        IReadOnlyDictionary<string, ReferenceSnapshot>? fixtureCaches = null)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(references);
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(schema.Kind, mapping.Template.Kind, StringComparison.Ordinal) || !string.Equals(schema.Version, mapping.Template.Version, StringComparison.Ordinal))
        {
            throw new FlowValidationException(
                $"{Where(mapping)}: the mapping fills template {mapping.Template}, but it was given template {schema.Kind} version {schema.Version}.");
        }

        _mapping = mapping;
        _searches = searches ?? ResolvedSearches.None;
        _search = search ?? NoRecordSearch.Instance;
        _schema = schema;
        _references = references;
        _context = context;
        _fixtureCaches = fixtureCaches ?? NoFixtureCaches;

        if (requireParameters)
        {
            foreach (var (name, parameter) in mapping.Parameters)
            {
                if (parameter.Required && !context.Parameters.ContainsKey(name) && parameter.Default is null)
                {
                    throw new FlowValidationException($"{Where(mapping)}: parameter '{name}' is required but the flow supplies no value under render.parameters.");
                }
            }
        }

        _requiredData = schema.RequiredAt("data");
        _recordEntries = mapping.Entries.Where(e => !e.IsRepeater && !e.Target.IsRepeated).ToList();
        _repeaters = mapping.Entries.Where(e => e.IsRepeater).Select(r => (r, (IReadOnlyList<MappingEntry>)mapping.ItemEntries(r).ToList())).ToList();
        // A ref resolves by the variable it fills and the entity type it names, so every alternative of a $coalesce node
        // that writes one shares the node's variable and is told apart by what it names.
        _referenceTemplates = mapping.Entries
            .SelectMany(e => e.ValueNodes.Select(node => (Entry: e, Ref: node.Modifiers.LastOrDefault(m => m.Kind == ModifierKind.Ref))))
            .Where(r => r.Ref is not null)
            .DistinctBy(r => (r.Entry.Index, r.Ref!.EntityType))
            .ToDictionary(r => (r.Entry.Index, r.Ref!.EntityType), r => ResolveReference(r.Entry, r.Ref!, schema));

        // A DSPDM business object row lists the attributes its mapping fills, which are the ones a save may clear.
        _owned = DspdmKinds.Is(mapping.Kind)
            ? mapping.Entries
                .Where(e => e.Target.Segments.Count >= 2 && e.Target.Segments[0].Name == "data")
                .Select(e => e.Target.Segments[1].Name.ToUpperInvariant())
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList()
            : null;
    }

    public MappingDefinition Mapping => _mapping;

    public RenderContext Context => _context;

    /// <summary>
    /// The shape of the records <paramref name="mapping"/> renders, drawn without a source record or a cache: the record
    /// <see cref="Render"/> assembles, with every value read from a row or the cache replaced by a placeholder naming the
    /// type the template gives it and where it comes from, static values as they render, and one item in each repeated
    /// array. The notes say what the placeholders cannot: how many items an array takes, parameters without a value, and
    /// what a render would hold for whatever the row.
    /// </summary>
    public static MappingShape Shape(MappingDefinition mapping, SchemaSnapshot schema, IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(parameters);
        var context = new RenderContext
        {
            MappingReference = mapping.Reference,
            CacheVersion = ReferenceSnapshot.Empty.Version,
            SchemaSnapshotVersion = schema.Version,
            Parameters = parameters,
        };
        var renderer = new MappingRenderer(mapping, schema, ReferenceSnapshot.Empty, context, requireParameters: false);
        var notes = new List<string>();

        foreach (var name in mapping.Parameters.Keys)
        {
            if (string.IsNullOrWhiteSpace(renderer.ParameterValue(name)))
            {
                notes.Add($"parameter '{name}' has no value, so the shape shows {{$param.{name}}} where the mapping uses it");
            }
        }

        var partition = renderer.ParameterValue(RenderContext.DataPartitionParameter);
        if (string.IsNullOrWhiteSpace(partition))
        {
            partition = "{$param." + RenderContext.DataPartitionParameter + "}";
        }
        else
        {
            try
            {
                partition = context.DataPartition;
            }
            catch (FlowValidationException ex)
            {
                // The id is still drawn with the value given, so the note and the id it would refuse read side by side.
                notes.Add(ex.Message);
            }
        }

        var key = string.Join(", ", mapping.Dataset.Key.Select(column => $"{DatasetColumn.Prefix}.{column}"));
        var document = new JsonObject
        {
            ["id"] = $"{partition}:{mapping.EntityType}:<delivery key from {mapping.Dataset.System}, {key}>",
            ["kind"] = mapping.Kind,
            ["data"] = new JsonObject(),
        };
        renderer.Assemble(document, new ShapeValues(renderer, notes), notes);

        // The envelope reads before the data it guards, as OSDU's own examples lay a record out.
        var shaped = new JsonObject();
        foreach (var (name, node) in document.Where(p => p.Key != "data"))
        {
            shaped[name] = node?.DeepClone();
        }

        shaped["data"] = document["data"]!.DeepClone();
        return new MappingShape(shaped, notes);
    }

    /// <summary>
    /// The display label for a row from the mapping's <c>dataset.label</c>. Display only: it is stored on the ledger record
    /// for search and never enters the record or its hash.
    /// </summary>
    public string? Label(SourceRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (string.IsNullOrWhiteSpace(_mapping.Dataset.Label))
        {
            return null;
        }

        var label = MappingMapper.LabelToken().Replace(
            _mapping.Dataset.Label,
            m => row.GetString(m.Groups["column"].Value) ?? string.Empty).Trim();
        return label.Length == 0 ? null : label.Length <= 400 ? label : label[..400];
    }

    /// <summary>
    /// The values of the mapping's <c>dataset.identity</c> columns for a row: what an operator holds when they come
    /// looking for the record. Search only, like the label: the values are indexed by the ledger and never enter the
    /// record or its hash. Empty values are left out, since nothing is found by them.
    /// </summary>
    public IReadOnlyList<string> Identities(SourceRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (_mapping.Dataset.Identity.Count == 0)
        {
            return [];
        }

        var values = new List<string>(_mapping.Dataset.Identity.Count);
        foreach (var column in _mapping.Dataset.Identity)
        {
            if (row.GetString(column) is { } value && value.Trim() is { Length: > 0 } trimmed)
            {
                values.Add(trimmed.Length <= RecordIdentityLimits.MaxTokenLength ? trimmed : trimmed[..RecordIdentityLimits.MaxTokenLength]);
            }
        }

        return values;
    }

    /// <summary>Derives the delivery key from a row without rendering anything else.</summary>
    public DeliveryKey? DeriveKey(SourceRow row, out IReadOnlyList<string?> values)
    {
        ArgumentNullException.ThrowIfNull(row);
        var list = new List<string?>(_mapping.Dataset.Key.Count);
        foreach (var column in _mapping.Dataset.Key)
        {
            list.Add(row.GetString(column));
        }

        values = list;
        return list.Any(string.IsNullOrWhiteSpace) ? null : DeliveryKey.Derive(_mapping.Dataset.System, list);
    }

    public RenderResult Render(SourceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var holds = new List<string>();
        var usages = new List<CacheUsage>();

        // What this render asks of the platform, and the alternatives it takes, are its own: the plan renders on several
        // threads over one renderer.
        var searched = new RenderTrail();

        var (document, key, sourceKey, targetId) = Start(record, holds);
        Assemble(document, new RowValues(this, record, holds, usages, searched), holds);

        var normalized = (JsonObject)CanonicalJson.Normalize(document)!;
        string canonical;
        try
        {
            canonical = CanonicalJson.ToString(normalized);
        }
        catch (InvalidOperationException ex)
        {
            // Every value a row, the cache or a static entry gives is checked before it reaches the document, so this is an
            // engine defect; the failure names the record so it can be found from the log line alone.
            throw new DeliveryException($"{sourceKey}: the rendered record{(targetId is null ? string.Empty : " " + targetId)} cannot be written as canonical JSON ({ex.Message})", ex);
        }
        // The hash is of the document alone (design.md section 6.3). The render context decides when a record is rendered
        // again, since a moved mapping version or cache version re-renders it; the document decides whether it is sent.
        var metadataHash = ContentHash.Of(canonical);

        return new RenderResult
        {
            Key = key,
            SourceKey = sourceKey,
            TargetId = targetId,
            Document = normalized,
            Canonical = canonical,
            MetadataHash = metadataHash,
            Holds = holds,
            CacheUsages = Distinct(usages),
            SearchUsages = searched.Used,
            Unanswered = searched.Unanswered,
            Choices = searched.Chosen,
        };
    }

    /// <summary>
    /// Renders one record the way <see cref="Render"/> does, entry by entry, and says what each entry of
    /// <paramref name="selection"/> gave: the value it wrote, that it left the variable out and why, that its <c>$when</c>
    /// does not hold, or the reasons it holds the record. The entries not selected are not evaluated at all, so a check of
    /// one variable reads only what that variable needs. The record is assembled by the one pass a delivery assembles it
    /// by; with every entry selected its document and holds are the ones <see cref="Render"/> writes and holds for.
    /// </summary>
    /// <remarks>
    /// An entry waiting on a search the platform has not been asked yet is reported as waiting, with the question; the
    /// caller asks <see cref="RecordInspection.Unanswered"/> and inspects the record again, as a plan renders it again.
    /// </remarks>
    public RecordInspection Inspect(SourceRecord record, EntrySelection selection)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(selection);
        var holds = new List<string>();
        var searched = new RenderTrail();
        var trail = new InspectionTrail(selection);
        var (document, key, sourceKey, _) = Start(record, holds);
        Assemble(document, new RowValues(this, record, holds, [], searched), holds, trail);
        return new RecordInspection
        {
            Key = key,
            SourceKey = sourceKey,
            Document = document,
            Outcomes = trail.Outcomes,
            Holds = holds,
            Unanswered = searched.Unanswered,
        };
    }

    /// <summary>
    /// Why a record whose search never got an answer is held: the platform was asked and said nothing, so the references
    /// that depend on the answer are unknown. A plan holds the record with it, and a check reports the entry that asked.
    /// </summary>
    public static string Unanswerable(SearchQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        return $"searching {question.Kind} for {question.Field} '{question.Value}' got no answer from the platform";
    }

    /// <summary>
    /// What every render of a record starts from: the delivery key (a hold when a key part is empty), the display key, and
    /// the document with its kind, an empty data block and, when the key is complete, its id.
    /// </summary>
    private (JsonObject Document, DeliveryKey? Key, string SourceKey, string? TargetId) Start(SourceRecord record, List<string> holds)
    {
        var key = DeriveKey(record.Row, out var keyValues);
        var sourceKey = SourceKey.Display(_mapping.Dataset.System, keyValues);
        if (key is null)
        {
            holds.Add($"dataset key incomplete ({sourceKey}): every key column must be non-empty");
        }

        var document = new JsonObject
        {
            ["kind"] = _mapping.Kind,
            ["data"] = new JsonObject(),
        };

        string? targetId = null;
        if (key is { } dk)
        {
            targetId = TargetId.Compose(_context.DataPartition, _mapping.EntityType, dk);
            document["id"] = targetId;
        }

        return (document, key, sourceKey, targetId);
    }

    /// <summary>
    /// This renderer over the same mapping and template with <paramref name="context"/> in place of its context,
    /// <paramref name="search"/> in place of its search and <paramref name="references"/> in place of its cache, where
    /// given: a fixture's own parameters, the answers a fixture assumes the platform gives, or the cache of the partition a
    /// fixture is written for.
    /// </summary>
    internal MappingRenderer With(RenderContext? context = null, IRecordSearch? search = null, ReferenceSnapshot? references = null)
        => new(_mapping, _schema, references ?? _references, context ?? _context, requireParameters: true, _searches, search ?? _search, _fixtureCaches);

    internal ReferenceSnapshot References => _references;

    /// <summary>The caches of the other partitions the mapping's fixtures are written for, by partition; see the constructor.</summary>
    internal IReadOnlyDictionary<string, ReferenceSnapshot> FixtureCaches => _fixtureCaches;


    /// <summary>The mapping's searches, resolved against the schemas they pin.</summary>
    public ResolvedSearches Searches => _searches;

    /// <summary>Where a search source is answered.</summary>
    public IRecordSearch Search => _search;

    internal SchemaSnapshot Schema => _schema;

    internal string DataPartition => _context.DataPartition;

    internal string? ParameterValue(string name)
    {
        if (_context.Parameters.TryGetValue(name, out var value))
        {
            return value;
        }

        return _mapping.Parameters.TryGetValue(name, out var declared) ? declared.Default : null;
    }

    internal static string Where(MappingDefinition mapping) => mapping.SourcePath ?? mapping.Reference;

    /// <summary>
    /// The id template the entry's ref modifier builds with, <c>{$param.dataPartition}:&lt;group--Entity&gt;:{$value}:</c>,
    /// or null with why the template does not say which entity type the reference is of. The render holds the record with
    /// that reason and the preflight refuses the mapping with it, so the two never disagree.
    /// </summary>
    internal IdTemplate? ReferenceTemplate(MappingEntry entry, out string? problem)
    {
        var reference = entry.Modifiers.LastOrDefault(m => m.Kind == ModifierKind.Ref);
        if (reference is not null && _referenceTemplates.TryGetValue((entry.Index, reference.EntityType), out var resolved))
        {
            problem = resolved.Problem;
            return resolved.Template;
        }

        problem = $"{entry.Target.Text} has no ref modifier";
        return null;
    }

    /// <summary>
    /// Which entity type a ref modifier references. Written in full, it is the one written. Written bare, it is the one
    /// entity type the variable's relationship names. Written by the entity's name alone, it is the one of the variable's
    /// relationships with that name, or the name in the group a relationship names without an entity.
    /// </summary>
    private static (IdTemplate? Template, string? Problem) ResolveReference(MappingEntry entry, Modifier modifier, SchemaSnapshot schema)
    {
        if (modifier.Id is { } written)
        {
            return (written, null);
        }

        var target = entry.Target.Text;
        var relationships = schema.Resolve(entry.Target.SchemaPath) is { } property ? IdValues.Relationships(property) : [];
        var named = modifier.EntityType;
        var candidates = named is null
            ? relationships.Where(r => r.Contains("--", StringComparison.Ordinal)).ToList()
            : relationships
                .Select(r => r.Contains("--", StringComparison.Ordinal)
                    ? (r.EndsWith("--" + named, StringComparison.Ordinal) ? r : null)
                    : $"{r}--{named}")
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList();

        if (candidates.Count == 1)
        {
            try
            {
                return (IdTemplate.Reference(candidates[0]), null);
            }
            catch (ArgumentException ex)
            {
                return (null, $"ref cannot reference {candidates[0]}, which the template names for {target}: {ex.Message}");
            }
        }

        if (relationships.Count == 0)
        {
            return (null,
                $"ref builds a reference of the entity type {target} points to, and the template names none for it; write the type in full, such as ref: reference-data--UnitOfMeasure");
        }

        var listed = string.Join(" or ", relationships);
        if (named is not null)
        {
            return (null, candidates.Count == 0
                ? $"{target} points to {listed}, and none of them is a {named}; name one of them, or write the type in full"
                : $"{target} points to {listed}, and {named} is more than one of them ({string.Join(", ", candidates)}); write the one meant in full");
        }

        var example = relationships.FirstOrDefault(r => r.Contains("--", StringComparison.Ordinal)) is { } first ? first[(first.IndexOf("--", StringComparison.Ordinal) + 2)..] : "<Entity>";
        return (null, $"{target} points to {listed}, so a bare ref cannot tell which the value is a code of; name it, such as ref: {example}");
    }

    /// <summary>
    /// Writes every entry's value into <paramref name="document"/>: the list of attributes a DSPDM business object row owns
    /// (<see cref="DspdmKinds.OwnedProperty"/>), the record's own entries at their targets, then each repeater's array with one
    /// item per row, then the check that the data the schema requires is there. What a value cannot be written for is added
    /// to <paramref name="holds"/>. The one assembly a render, an inspection and a shape share.
    /// </summary>
    /// <param name="document">The record being written.</param>
    /// <param name="values">Where the values come from: a source record's rows, or a shape's placeholders.</param>
    /// <param name="holds">What the record is held for.</param>
    /// <param name="trail">
    /// For an inspection: the entries to evaluate, and where what each gave is noted. Null evaluates every entry and notes
    /// nothing, which is a render and a shape.
    /// </param>
    private void Assemble(JsonObject document, IRecordValues values, List<string> holds, InspectionTrail? trail = null)
    {
        if (_owned is not null)
        {
            document[DspdmKinds.OwnedProperty] = new JsonArray(_owned.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray());
        }

        var selection = trail?.Selection ?? EntrySelection.All;
        foreach (var entry in _recordEntries)
        {
            if (!selection.Includes(entry.Target.Text))
            {
                continue;
            }

            var held = holds.Count;
            var asked = values.Asked.Count;
            var value = values.Value(entry, item: null, out var applied);
            if (value is not null)
            {
                SetPath(document, entry.Target.Segments.Select(s => s.Name).ToList(), value);
            }

            trail?.Add(Outcome(entry, null, null, value, applied, holds, held, values, asked));
        }

        foreach (var (repeater, items) in _repeaters)
        {
            var selected = selection.IsAll ? items : items.Where(item => selection.Includes(item.Target.Text)).ToList();
            var whole = selection.Covers(repeater.Target.Text);
            if (!whole && selected.Count == 0 && !selection.Includes(repeater.Target.Text))
            {
                continue;
            }

            // What the repeater itself is held for (a condition or a row filter it cannot test, no item where one is
            // required), apart from what the entries of its items hold the record for.
            var own = new List<string>();
            var start = holds.Count;
            if (!values.Applies(repeater))
            {
                own.AddRange(holds.Skip(start));
                if (trail is not null)
                {
                    var why = own.Count > 0 ? own : [$"$when {repeater.AppliesWhen} does not hold"];
                    if (whole)
                    {
                        trail.Add(new EntryOutcome
                        {
                            Target = repeater.Target.Text,
                            Entry = repeater,
                            Kind = own.Count > 0 ? EntryOutcomeKind.Held : EntryOutcomeKind.NotApplicable,
                            Reasons = why,
                        });
                    }

                    NoItem(trail, selected, $"{repeater.Target.Text} writes no item for the row: {string.Join("; ", why)}");
                }

                continue;
            }

            var array = new JsonArray();
            var ordinal = -1;
            var kept = 0;
            foreach (var row in values.Items(repeater))
            {
                ordinal++;
                var filtered = holds.Count;
                if (!values.Keeps(repeater, row))
                {
                    own.AddRange(holds.Skip(filtered));
                    continue;
                }

                kept++;
                var item = new JsonObject();
                foreach (var entry in selected)
                {
                    var held = holds.Count;
                    var asked = values.Asked.Count;
                    var value = values.Value(entry, row, out var applied);
                    if (value is not null)
                    {
                        SetPath(item, entry.Target.WithinItem, value);
                    }

                    trail?.Add(Outcome(entry, ordinal, row, value, applied, holds, held, values, asked));
                }

                if (item.Count > 0)
                {
                    array.Add(item);
                }
            }

            // An entry of the items met none because the row has no child row the repeater takes, or because testing its
            // $where held the record; what the array as a whole is held for is the array's, not theirs.
            if (trail is not null && kept == 0)
            {
                NoItem(trail, selected, own.Count > 0
                    ? $"{repeater.Target.Text} writes no item for the row: {string.Join("; ", own)}"
                    : $"the row has no child row in {repeater.Source}{(repeater.RowFilter is { } filter ? $" where {filter}" : string.Empty)}");
            }

            if (array.Count > 0)
            {
                SetPath(document, repeater.Target.Segments.Select(s => s.Name).ToList(), array);
            }
            else if (repeater.Required && whole)
            {
                // Whether the array is empty is known only when every entry of its items was evaluated.
                var none = $"{repeater.Target.Text}: {repeater.Source} has no rows with values, and the entry is required";
                holds.Add(none);
                own.Add(none);
            }

            if (trail is null)
            {
                continue;
            }

            if (whole)
            {
                trail.Add(new EntryOutcome
                {
                    Target = repeater.Target.Text,
                    Entry = repeater,
                    Kind = own.Count > 0 ? EntryOutcomeKind.Held : array.Count > 0 ? EntryOutcomeKind.Value : EntryOutcomeKind.Empty,
                    Value = own.Count == 0 && array.Count > 0 ? array : null,
                    Reasons = own.Count > 0 || array.Count > 0
                        ? own
                        : [kept == 0 ? $"{repeater.Source} has no rows{(repeater.RowFilter is { } where ? $" where {where}" : string.Empty)}" : $"no row of {repeater.Source} gives a value to any property of its item"],
                });
            }
        }

        if (document["data"] is JsonObject data)
        {
            foreach (var required in _requiredData)
            {
                var target = $"{TemplatePath.Prefix}.data.{required}";
                if (!selection.Covers(target) || data[required] is not null)
                {
                    continue;
                }

                var reason = $"schema-required property data.{required} rendered empty";
                holds.Add(reason);
                trail?.Required(target, reason);
            }
        }
    }

    /// <summary>
    /// What one entry gave, from what its evaluation left behind: a question it asked makes it wait, a hold it added holds
    /// it, a condition that did not hold makes it not apply, and no value leaves the variable out, with why.
    /// </summary>
    private static EntryOutcome Outcome(
        MappingEntry entry, int? item, SourceRow? row, JsonNode? value, bool applied, List<string> holds, int held, IRecordValues values, int asked)
    {
        var target = entry.Target.Text;
        if (values.Asked.Count > asked)
        {
            return new EntryOutcome
            {
                Target = target,
                Entry = entry,
                Item = item,
                Kind = EntryOutcomeKind.Waiting,
                Reasons = values.Asked.Skip(asked).Select(Unanswerable).ToList(),
            };
        }

        if (holds.Count > held)
        {
            return new EntryOutcome { Target = target, Entry = entry, Item = item, Kind = EntryOutcomeKind.Held, Reasons = holds.Skip(held).ToList() };
        }

        if (!applied)
        {
            return new EntryOutcome { Target = target, Entry = entry, Item = item, Kind = EntryOutcomeKind.NotApplicable, Reasons = [$"$when {entry.AppliesWhen} does not hold"] };
        }

        return value is null
            ? new EntryOutcome { Target = target, Entry = entry, Item = item, Kind = EntryOutcomeKind.Empty, Reasons = [values.WhyEmpty(entry, row)] }
            : new EntryOutcome { Target = target, Entry = entry, Item = item, Kind = EntryOutcomeKind.Value, Value = value };
    }

    /// <summary>Notes, for every selected entry of a repeated item, that the row gives it no item to write, and why.</summary>
    private static void NoItem(InspectionTrail trail, IReadOnlyList<MappingEntry> selected, string reason)
    {
        foreach (var entry in selected)
        {
            trail.Add(new EntryOutcome { Target = entry.Target.Text, Entry = entry, Kind = EntryOutcomeKind.NotApplicable, Reasons = [reason] });
        }
    }

    /// <summary>One row per cached path a record actually consumed; a value read twice is one dependency.</summary>
    private static IReadOnlyList<CacheUsage> Distinct(List<CacheUsage> usages)
    {
        if (usages.Count <= 1)
        {
            return usages;
        }

        var seen = new HashSet<(string, string, string, CacheUsageKind)>();
        var unique = new List<CacheUsage>(usages.Count);
        foreach (var usage in usages)
        {
            if (seen.Add((usage.TypeName, usage.ItemId, usage.Path, usage.Kind)))
            {
                unique.Add(usage);
            }
        }

        return unique;
    }

    private static void SetPath(JsonObject root, IReadOnlyList<string> segments, JsonNode value)
    {
        var current = root;
        for (var i = 0; i < segments.Count - 1; i++)
        {
            if (current[segments[i]] is not JsonObject next)
            {
                next = new JsonObject();
                current[segments[i]] = next;
            }

            current = next;
        }

        current[segments[^1]] = value;
    }

    /// <summary>Where an assembled record's values come from: a source record's rows, or the placeholders of a shape.</summary>
    private interface IRecordValues
    {
        /// <summary>
        /// The entry's value, for the record's row or for one item of a repeater; null leaves the variable out.
        /// <paramref name="applied"/> is false when the entry's <c>$when</c> does not hold for the row.
        /// </summary>
        JsonNode? Value(MappingEntry entry, SourceRow? item, out bool applied);

        /// <summary>Whether the repeater's condition lets it write its array.</summary>
        bool Applies(MappingEntry repeater);

        /// <summary>The child rows the repeater reads, each of which <see cref="Keeps"/> decides on.</summary>
        IEnumerable<SourceRow?> Items(MappingEntry repeater);

        /// <summary>Whether the repeater's <c>$where</c> lets <paramref name="row"/> become an item.</summary>
        bool Keeps(MappingEntry repeater, SourceRow? row);

        /// <summary>The questions the values met so far are waiting on the platform for, in the order they were asked.</summary>
        IReadOnlyList<SearchQuestion> Asked { get; }

        /// <summary>Why an entry that wrote nothing gave no value for the row, as a render holding it for that would say.</summary>
        string WhyEmpty(MappingEntry entry, SourceRow? item);
    }

    /// <summary>A source record's values, as a delivery renders them.</summary>
    private sealed class RowValues(MappingRenderer renderer, SourceRecord record, List<string> holds, List<CacheUsage> usages, RenderTrail searched) : IRecordValues
    {
        public JsonNode? Value(MappingEntry entry, SourceRow? item, out bool applied)
            => EntryValues.Evaluate(entry, record.Row, item, renderer, holds, usages, searched, out applied);

        public bool Applies(MappingEntry repeater)
            => repeater.AppliesWhen is not { } condition || EntryValues.Applies(condition, record.Row, item: null, renderer, holds, repeater.Target.Text);

        public IEnumerable<SourceRow?> Items(MappingEntry repeater) => record.ScopeRows(repeater.Source!.Child!);

        public bool Keeps(MappingEntry repeater, SourceRow? row)
            => repeater.RowFilter is not { } filter || EntryValues.Applies(filter, record.Row, row, renderer, holds, repeater.Target.Text);

        public IReadOnlyList<SearchQuestion> Asked => searched.Unanswered;

        public string WhyEmpty(MappingEntry entry, SourceRow? item) => EntryValues.WhyEmpty(entry, record.Row, item, renderer);
    }

    /// <summary>Placeholders in place of a record's values: one item per repeater, whatever its condition, with a note saying how many a record takes.</summary>
    private sealed class ShapeValues(MappingRenderer renderer, List<string> notes) : IRecordValues
    {
        public JsonNode? Value(MappingEntry entry, SourceRow? item, out bool applied)
        {
            applied = true;
            return EntryValues.Describe(entry, renderer, notes);
        }

        public bool Applies(MappingEntry repeater) => true;

        public IEnumerable<SourceRow?> Items(MappingEntry repeater)
        {
            var when = repeater.AppliesWhen is { } condition ? $", only when {condition}" : string.Empty;
            var where = repeater.RowFilter is { } filter ? $" where {filter}" : string.Empty;
            var none = repeater.Required ? "a record without any is held" : "left out when there are none";
            notes.Add($"{repeater.Target.Text}: one item per row of {repeater.Source}{where}{when}; {none}");
            return [null];
        }

        public bool Keeps(MappingEntry repeater, SourceRow? row) => true;

        public IReadOnlyList<SearchQuestion> Asked => [];

        public string WhyEmpty(MappingEntry entry, SourceRow? item) => "a shape reads no row";
    }
}

/// <summary>
/// The alternative of a <c>$coalesce</c> node that gave the value it wrote.
/// </summary>
/// <param name="Target">The variable the node fills.</param>
/// <param name="Alternative">Which alternative gave it, counting from one in the order they are tried.</param>
/// <param name="Of">How many alternatives the node lists.</param>
/// <param name="Origin">Where that alternative reads its value, as a record's shape names it.</param>
/// <param name="Values">How many values it gave: one, or one per item of a repeated array that took it.</param>
/// <param name="Unverified">True when a value it gave is an id the cache holds no record under, written because the alternative says <c>$unverified</c>.</param>
public sealed record CoalesceChoice(string Target, int Alternative, int Of, string Origin, int Values, bool Unverified);

/// <summary>The outcome of rendering one record.</summary>
public sealed record RenderResult
{
    public DeliveryKey? Key { get; init; }

    public required string SourceKey { get; init; }

    public string? TargetId { get; init; }

    public required JsonObject Document { get; init; }

    /// <summary>The canonical JSON of <see cref="Document"/>.</summary>
    public required string Canonical { get; init; }

    /// <summary>The hash of the canonical document.</summary>
    public required string MetadataHash { get; init; }

    /// <summary>Reasons the record cannot be delivered as it stands. Empty means deliverable.</summary>
    public required IReadOnlyList<string> Holds { get; init; }

    /// <summary>What the render consumed from the cache: the dependency trail a later cache version is checked against.</summary>
    public IReadOnlyList<CacheUsage> CacheUsages { get; init; } = [];

    /// <summary>What the render resolved by searching the platform, beside what it read from the cache.</summary>
    public IReadOnlyList<SearchUsage> SearchUsages { get; init; } = [];

    /// <summary>
    /// Questions the render needed answered and the run had not asked yet. A result carrying any of these is not
    /// finished: the caller answers them and renders the row again, and the second render finds them known. It is never
    /// delivered as it stands, because the values that depend on them are not in it.
    /// </summary>
    public IReadOnlyList<SearchQuestion> Unanswered { get; init; } = [];

    /// <summary>True when the render could not finish because the platform has not been asked yet.</summary>
    public bool IsIncomplete => Unanswered.Count > 0;

    /// <summary>
    /// Which alternative each <c>$coalesce</c> node took the value it wrote from. It is not part of the document, which the
    /// ledger hashes, and follows from the same row, mapping version and cache version, so rendering the record again from
    /// what the ledger records tells it again.
    /// </summary>
    public IReadOnlyList<CoalesceChoice> Choices { get; init; } = [];

    public bool IsHeld => Holds.Count > 0 || Key is null;
}

/// <summary>
/// The shape of the records a mapping renders (<see cref="MappingRenderer.Shape"/>): the record with placeholders where
/// values come from a row or the cache, laid out envelope first, and what the placeholders cannot say.
/// </summary>
public sealed record MappingShape(JsonObject Document, IReadOnlyList<string> Notes);
