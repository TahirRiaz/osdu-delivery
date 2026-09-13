using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
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

    public MappingRenderer(MappingDefinition mapping, SchemaSnapshot schema, ReferenceSnapshot references, RenderContext context)
        : this(mapping, schema, references, context, requireParameters: true)
    {
    }

    private MappingRenderer(MappingDefinition mapping, SchemaSnapshot schema, ReferenceSnapshot references, RenderContext context, bool requireParameters)
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
        _schema = schema;
        _references = references;
        _context = context;

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
                notes.Add($"parameter '{name}' has no value, so the shape shows {{param.{name}}} where the mapping uses it");
            }
        }

        var partition = renderer.ParameterValue(RenderContext.DataPartitionParameter);
        if (string.IsNullOrWhiteSpace(partition))
        {
            partition = "{param." + RenderContext.DataPartitionParameter + "}";
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
            m => row.GetString(m.Groups["column"].Value[(DatasetColumn.Prefix.Length + 1)..]) ?? string.Empty).Trim();
        return label.Length == 0 ? null : label.Length <= 400 ? label : label[..400];
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

        var key = DeriveKey(record.Row, out var keyValues);
        var sourceKey = SourceKey.Display(_mapping.Dataset.System, keyValues);
        if (key is null)
        {
            holds.Add($"dataset key incomplete ({sourceKey}): every key column must be non-empty");
        }

        if (key is { } k && record.DeclaredDeliveryKey is { } declared && declared != k.Value)
        {
            holds.Add($"drop declares deliveryKey {declared:D} but the mapping derives {k.Value:D} from {sourceKey}; the two halves disagree on identity");
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

        Assemble(document, new RowValues(this, record, holds, usages), holds);

        var normalized = (JsonObject)CanonicalJson.Normalize(document)!;
        var canonical = CanonicalJson.ToString(normalized);
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
        };
    }

    internal ReferenceSnapshot References => _references;

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
    /// Writes every entry's value into <paramref name="document"/>: the record's own entries at their targets, then each
    /// repeater's array with one item per row, then the check that the data the schema requires is there. What a value
    /// cannot be written for is added to <paramref name="holds"/>. The one assembly a render and a shape share.
    /// </summary>
    private void Assemble(JsonObject document, IRecordValues values, List<string> holds)
    {
        foreach (var entry in _recordEntries)
        {
            if (values.Value(entry, item: null) is { } value)
            {
                SetPath(document, entry.Target.Segments.Select(s => s.Name).ToList(), value);
            }
        }

        foreach (var (repeater, items) in _repeaters)
        {
            if (!values.Applies(repeater))
            {
                continue;
            }

            var array = new JsonArray();
            foreach (var row in values.Items(repeater))
            {
                var item = new JsonObject();
                foreach (var entry in items)
                {
                    if (values.Value(entry, row) is { } value)
                    {
                        SetPath(item, entry.Target.WithinItem, value);
                    }
                }

                if (item.Count > 0)
                {
                    array.Add(item);
                }
            }

            if (array.Count > 0)
            {
                SetPath(document, repeater.Target.Segments.Select(s => s.Name).ToList(), array);
            }
            else if (repeater.Required)
            {
                holds.Add($"{repeater.Target.Text}: {repeater.Source} has no rows with values, and the entry is required");
            }
        }

        if (document["data"] is JsonObject data)
        {
            foreach (var required in _requiredData)
            {
                if (data[required] is null)
                {
                    holds.Add($"schema-required property data.{required} rendered empty");
                }
            }
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
        /// <summary>The entry's value, for the record's row or for one item of a repeater; null leaves the variable out.</summary>
        JsonNode? Value(MappingEntry entry, SourceRow? item);

        /// <summary>Whether the repeater's condition lets it write its array.</summary>
        bool Applies(MappingEntry repeater);

        /// <summary>The rows the repeater writes one item for.</summary>
        IEnumerable<SourceRow?> Items(MappingEntry repeater);
    }

    /// <summary>A source record's values, as a delivery renders them.</summary>
    private sealed class RowValues(MappingRenderer renderer, SourceRecord record, List<string> holds, List<CacheUsage> usages) : IRecordValues
    {
        public JsonNode? Value(MappingEntry entry, SourceRow? item) => EntryValues.Evaluate(entry, record.Row, item, renderer, holds, usages);

        public bool Applies(MappingEntry repeater) => repeater.AppliesWhen is not { } condition || EntryValues.Applies(condition, record.Row, item: null);

        public IEnumerable<SourceRow?> Items(MappingEntry repeater) => record.ScopeRows(repeater.Source!.Child!);
    }

    /// <summary>Placeholders in place of a record's values: one item per repeater, whatever its condition, with a note saying how many a record takes.</summary>
    private sealed class ShapeValues(MappingRenderer renderer, List<string> notes) : IRecordValues
    {
        public JsonNode? Value(MappingEntry entry, SourceRow? item) => EntryValues.Describe(entry, renderer, notes);

        public bool Applies(MappingEntry repeater) => true;

        public IEnumerable<SourceRow?> Items(MappingEntry repeater)
        {
            var when = repeater.AppliesWhen is { } condition ? $", only when {condition}" : string.Empty;
            var none = repeater.Required ? "a record without any is held" : "left out when there are none";
            notes.Add($"{repeater.Target.Text}: one item per row of {repeater.Source}{when}; {none}");
            return [null];
        }
    }
}

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

    public bool IsHeld => Holds.Count > 0 || Key is null;
}

/// <summary>
/// The shape of the records a mapping renders (<see cref="MappingRenderer.Shape"/>): the record with placeholders where
/// values come from a row or the cache, laid out envelope first, and what the placeholders cannot say.
/// </summary>
public sealed record MappingShape(JsonObject Document, IReadOnlyList<string> Notes);
