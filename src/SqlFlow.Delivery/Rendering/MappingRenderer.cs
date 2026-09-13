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

        foreach (var (name, parameter) in mapping.Parameters)
        {
            if (parameter.Required && !context.Parameters.ContainsKey(name) && parameter.Default is null)
            {
                throw new FlowValidationException($"{Where(mapping)}: parameter '{name}' is required but the flow supplies no value under render.parameters.");
            }
        }

        _requiredData = schema.RequiredAt("data");
        _recordEntries = mapping.Entries.Where(e => !e.IsRepeater && !e.Target.IsRepeated).ToList();
        _repeaters = mapping.Entries.Where(e => e.IsRepeater).Select(r => (r, (IReadOnlyList<MappingEntry>)mapping.ItemEntries(r).ToList())).ToList();
    }

    public MappingDefinition Mapping => _mapping;

    public RenderContext Context => _context;

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

        foreach (var entry in _recordEntries)
        {
            if (EntryValues.Evaluate(entry, record.Row, item: null, this, holds, usages) is { } value)
            {
                SetPath(document, entry.Target.Segments.Select(s => s.Name).ToList(), value);
            }
        }

        foreach (var (repeater, items) in _repeaters)
        {
            if (repeater.AppliesWhen is { } condition && !EntryValues.Applies(condition, record.Row, item: null))
            {
                continue;
            }

            var child = repeater.Source!.Child!;
            var array = new JsonArray();
            foreach (var row in record.ScopeRows(child))
            {
                var item = new JsonObject();
                foreach (var entry in items)
                {
                    if (EntryValues.Evaluate(entry, record.Row, row, this, holds, usages) is { } value)
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
