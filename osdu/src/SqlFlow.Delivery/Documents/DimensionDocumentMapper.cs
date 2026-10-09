using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SqlFlow.Core;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Search;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// Maps a parsed dimension document onto <see cref="DimensionFlowDefinition"/> and validates what YAML cannot
/// (docs/dimension-plan.md): every dimension reads one attribute search can match exactly, every clean step is one the
/// cleaner runs, and every pattern compiles for the engine that runs it. What needs the kind's templates (how a property of
/// data is indexed, in every version the partition holds) is checked when a build or a plan reads them.
/// </summary>
internal static class DimensionMapper
{
    private static readonly JsonSerializerOptions HashJson = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>The clean steps written as a name alone, by that name.</summary>
    private static readonly IReadOnlyDictionary<string, CleanStepKind> PlainSteps = new[]
    {
        CleanStepKind.Trim, CleanStepKind.CollapseSpaces, CleanStepKind.Upper, CleanStepKind.Lower, CleanStepKind.Nfc, CleanStepKind.Nfkc, CleanStepKind.FoldSeparators,
    }.ToDictionary(CleanStep.Name, k => k, StringComparer.Ordinal);

    /// <summary>Every clean step a document may write, as the census documents them.</summary>
    internal static IReadOnlyList<string> StepNames { get; } = [.. PlainSteps.Keys, "replace", "map"];

    /// <summary>The settings a <c>replace</c> step takes.</summary>
    internal static IReadOnlyList<string> ReplaceSettings { get; } = ["pattern", "with"];

    /// <summary>The settings a <c>map</c> step takes.</summary>
    internal static IReadOnlyList<string> MapSettings { get; } = ["dictionary", "field", "otherwise"];

    public static DimensionFlowDefinition Map(DimensionFlowYaml y, string source)
    {
        var name = FlowMapper.Require(y.Name, "name", source);
        var src = y.Source ?? throw FlowMapper.Missing("source", source);
        var headers = new Dictionary<string, string>(src.Headers ?? [], StringComparer.OrdinalIgnoreCase);
        var partitions = MapPartitions(y.Partitions, source);
        if (partitions.Count > 0)
        {
            if (headers.ContainsKey(FlowMapper.PartitionHeader))
            {
                throw new FlowValidationException(
                    $"{source}: source.headers names '{FlowMapper.PartitionHeader}', and the flow names its partitions: every run sets the header to the partition it builds in. Remove the header.");
            }
        }
        else if (headers.TryGetValue(FlowMapper.PartitionHeader, out var partition))
        {
            if (string.IsNullOrWhiteSpace(partition))
            {
                throw new FlowValidationException(
                    $"{source}: source.headers.{FlowMapper.PartitionHeader} is empty. Name the partition the flow builds in, or take the header out so the flow builds in every partition registered with the catalog.");
            }

            _ = CacheScope.Normalize(partition, $"{source}: source.headers");
        }

        var followsRegistry = partitions.Count == 0 && !headers.ContainsKey(FlowMapper.PartitionHeader);
        var parameters = (y.Parameters ?? []).ToDictionary(
            kv => kv.Key,
            kv => new FlowParameter { Required = kv.Value.Required, Default = kv.Value.Default, Description = kv.Value.Description },
            StringComparer.Ordinal);
        if (parameters.ContainsKey(PartitionNames.RunValue))
        {
            throw new FlowValidationException(
                $"{source}: parameters declares '{PartitionNames.RunValue}', which a dimension flow keeps for the partition a run builds in: {{partition}} in a query is always that partition. Rename the parameter.");
        }

        var aggregationSize = src.AggregationSize ?? DimensionSource.DefaultAggregationSize;
        if (aggregationSize is < DistinctReadOptions.MinAggregationSize or > DistinctReadOptions.MaxAggregationSize)
        {
            throw new FlowValidationException(
                $"{source}: source.aggregationSize is {aggregationSize}; it is how many groups the platform's aggregation returns (its AGGREGATION_SIZE, {DimensionSource.DefaultAggregationSize} unless its operators changed it), between {DistinctReadOptions.MinAggregationSize} and {DistinctReadOptions.MaxAggregationSize}.");
        }

        var flow = new DimensionFlowDefinition
        {
            SourcePath = source == "<inline>" ? null : source,
            Name = name,
            Description = Optional(y.Description),
            Batch = Optional(y.Batch),
            Parameters = parameters,
            Source = new DimensionSource
            {
                Endpoint = FlowMapper.Require(src.Endpoint, "source.endpoint", source),
                Auth = FlowMapper.MapAuth(src.Auth, source, "source.auth"),
                Headers = headers,
                QueryPath = ServicePath(src.QueryPath, DimensionSource.DefaultQueryPath, "source.queryPath", source),
                SearchPath = ServicePath(src.SearchPath, DimensionSource.DefaultSearchPath, "source.searchPath", source),
                AggregationSize = aggregationSize,
            },
            Partitions = partitions,
            FollowsRegistry = followsRegistry,
            Dimensions = MapDimensions(y.Dimensions, parameters, partitions, followsRegistry, source),
            Reliability = FlowMapper.MapReliability(y.Reliability, source),
        };

        if (flow.Reliability.Concurrency < 1)
        {
            throw new FlowValidationException($"{source}: reliability.concurrency must be at least 1.");
        }

        return flow;
    }

    private static IReadOnlyList<string> MapPartitions(IReadOnlyList<string>? declared, string source)
    {
        if (declared is null)
        {
            return [];
        }

        var names = declared.Select((n, i) => PartitionNames.Check(n, string.Create(CultureInfo.InvariantCulture, $"{source}: partitions[{i}]"))).ToList();
        PartitionNames.CheckList(names, $"{source}: partitions");
        return names;
    }

    private static string ServicePath(string? declared, string fallback, string key, string source)
    {
        if (string.IsNullOrWhiteSpace(declared))
        {
            return fallback;
        }

        var path = declared.Trim();
        if (!path.StartsWith('/') || path.Any(char.IsWhiteSpace) || path.Contains('?', StringComparison.Ordinal) || path.Contains('#', StringComparison.Ordinal))
        {
            throw new FlowValidationException($"{source}: {key} '{path}' must be a path under the endpoint, starting with '/', with no whitespace, query or fragment.");
        }

        return path.TrimEnd('/').Length == 0 ? path : path.TrimEnd('/');
    }

    private static IReadOnlyList<DimensionSpec> MapDimensions(
        List<DimensionYaml>? declared, IReadOnlyDictionary<string, FlowParameter> parameters, IReadOnlyList<string> partitions, bool followsRegistry, string source)
    {
        if (declared is null || declared.Count == 0)
        {
            throw new FlowValidationException($"{source}: dimensions must list at least one dimension; a dimension flow without dimensions has nothing to build.");
        }

        if (declared.Count > DimensionFlowDefinition.MaxDimensions)
        {
            throw new FlowValidationException(
                $"{source}: dimensions lists {declared.Count} dimensions; one flow holds at most {DimensionFlowDefinition.MaxDimensions}. Split them over several flows.");
        }

        var dimensions = new List<DimensionSpec>(declared.Count);
        for (var i = 0; i < declared.Count; i++)
        {
            var dimension = MapDimension(declared[i], i, parameters, partitions, followsRegistry, source);
            if (dimensions.Any(d => string.Equals(d.Name, dimension.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new FlowValidationException(
                    $"{source}: dimensions[{i}] is named '{dimension.Name}', as an earlier dimension is; a run, a page and a filter name each dimension by its name, so each needs its own.");
            }

            // Each dimension has its own table, named with the letters, digits and underscores of its name.
            if (dimensions.FirstOrDefault(d => string.Equals(DimensionTables.NameOf(d.Name), DimensionTables.NameOf(dimension.Name), StringComparison.OrdinalIgnoreCase)) is { } alike)
            {
                throw new FlowValidationException(
                    $"{source}: dimensions[{i}] '{dimension.Name}' would share its table with '{alike.Name}': a table's name keeps the letters, digits and underscores of a dimension's name, and theirs are the same. Rename one of them.");
            }

            dimensions.Add(dimension);
        }

        return dimensions;
    }

    private static DimensionSpec MapDimension(
        DimensionYaml? d, int index, IReadOnlyDictionary<string, FlowParameter> parameters, IReadOnlyList<string> partitions, bool followsRegistry, string source)
    {
        var at = string.Create(CultureInfo.InvariantCulture, $"dimensions[{index}]");
        if (d is null)
        {
            throw new FlowValidationException($"{source}: {at} is empty.");
        }

        var name = FlowMapper.Require(d.Name, at + ".name", source);
        if (!SelectableNames.IsName(name))
        {
            throw new FlowValidationException($"{source}: {at}.name '{name}' is not a dimension name: {SelectableNames.Rule}.");
        }

        var where = $"{at} '{name}'";
        var kind = FlowMapper.Require(d.Kind, at + ".kind", source);
        if (!OsduKind.IsValid(kind))
        {
            throw new FlowValidationException($"{source}: {where}: kind '{kind}' is not authority:source:entityType:version (wildcards allowed per segment).");
        }

        var query = Optional(d.Query);
        foreach (var token in FlowMapper.Tokens(query ?? string.Empty))
        {
            if (token != PartitionNames.RunValue && !parameters.ContainsKey(token))
            {
                throw new FlowValidationException($"{source}: {where}: query uses '{{{token}}}', which is not declared under parameters.");
            }
        }

        var path = FlowMapper.Require(d.Path, at + ".path", source);
        CheckPath(path, where, source);

        var maxValues = d.MaxValues ?? DimensionSpec.DefaultMaxValues;
        if (maxValues is < 1 or > DimensionSpec.MaxValuesCeiling)
        {
            throw new FlowValidationException(
                $"{source}: {where}: maxValues is {maxValues}; it is between 1 and {DimensionSpec.MaxValuesCeiling.ToString("N0", CultureInfo.InvariantCulture)}, since a build holds a dimension's values in memory while it groups them.");
        }

        var label = MapLabel(d.Label, where, source);
        var attributes = MapAttributes(d.Attributes, where, source);
        var (keyColumn, valueColumn) = MapColumns(d.Columns, name, path, label, attributes, where, source);
        var dimension = new DimensionSpec
        {
            Name = name,
            Description = Optional(d.Description),
            Kind = kind,
            Query = query,
            Path = path,
            Label = label,
            Unlabelled = MapUnlabelled(d.Unlabelled, label.Count > 0 || d.Attributes is { Count: > 0 }, where, source),
            Attributes = attributes,
            KeyColumn = keyColumn,
            ValueColumn = valueColumn,
            Clean = MapClean(d.Clean, where, source),
            CountRecords = d.CountRecords ?? false,
            MaxValues = maxValues,
            Partitions = MapDimensionPartitions(d.Partitions, partitions, followsRegistry, where, source),
        };
        return dimension with { DefinitionHash = Hash(dimension) };
    }

    /// <summary>
    /// A path search can name: a property of data, which the kind's templates are asked about when the dimension is built, or
    /// a property of the record itself, which the indexer maps the same way for every kind and is checked here.
    /// </summary>
    private static void CheckPath(string path, string where, string source)
    {
        if (!OsduPath.IsPath(path))
        {
            throw new FlowValidationException(
                $"{source}: {where}: path '{path}' is not a property path: segments of letters, digits and underscores separated by dots, such as data.FacilityName or legal.legaltags. A path goes into a query unquoted, so nothing else can be written there.");
        }

        if (SearchFields.IsDataPath(path))
        {
            return;
        }

        if (SearchFields.RecordProperty(path).Problem is { } problem)
        {
            throw new FlowValidationException($"{source}: {where}: path {problem}");
        }
    }

    /// <summary>
    /// Where a key's label is read: one path of the record the key names (<c>label: data.FacilityName</c>), or a list of paths,
    /// each but the last reading the reference the next record is found by (<c>label: [data.GeoContexts.FieldID,
    /// data.FieldName]</c>), at most <see cref="DimensionSpec.MaxLabelSteps"/>. A path is read from the record as the search
    /// returns it, so it is any path of the record: <c>data.</c> and its properties, or the record's own.
    /// </summary>
    private static IReadOnlyList<string> MapLabel(object? declared, string where, string source)
        => MapSteps(declared, "label", where, source);

    /// <summary>
    /// The value of a key whose label is not read: a text, at most <see cref="DimensionSpec.MaxCleanLength"/> characters,
    /// for a dimension that reads a label.
    /// </summary>
    private static string? MapUnlabelled(string? declared, bool labelled, string where, string source)
    {
        if (declared is null)
        {
            return null;
        }

        var text = declared.Trim();
        if (text.Length == 0)
        {
            throw new FlowValidationException(
                $"{source}: {where}: unlabelled is empty; give the value a key without a label takes (unlabelled: Not specified), or leave it out to value such a key by its id's code.");
        }

        if (text.Length > DimensionSpec.MaxCleanLength)
        {
            throw new FlowValidationException(
                string.Create(CultureInfo.InvariantCulture, $"{source}: {where}: unlabelled is {text.Length} characters; a value is at most {DimensionSpec.MaxCleanLength}."));
        }

        return labelled
            ? text
            : throw new FlowValidationException($"{source}: {where}: unlabelled is the value of a key whose label or attribute is not read, and the dimension reads neither.");
    }

    /// <summary>
    /// The attributes of a dimension's keys, each by its name and read as a label is (<see cref="MapLabel"/>): at most
    /// <see cref="DimensionSpec.MaxAttributes"/>, each named by a letter, then letters, digits and underscores, unique
    /// ignoring case, and none a name a cached dimension's rows hold already.
    /// </summary>
    private static IReadOnlyList<DimensionAttributeSpec> MapAttributes(Dictionary<string, object?>? declared, string where, string source)
    {
        if (declared is null || declared.Count == 0)
        {
            return [];
        }

        if (declared.Count > DimensionSpec.MaxAttributes)
        {
            throw new FlowValidationException(
                $"{source}: {where}: attributes names {declared.Count}; a dimension reads at most {DimensionSpec.MaxAttributes}, a search per attribute step for every key.");
        }

        var attributes = new List<DimensionAttributeSpec>(declared.Count);
        foreach (var (name, steps) in declared)
        {
            var trimmed = name.Trim();
            if (!DimensionAttributeSpec.IsName(trimmed))
            {
                throw new FlowValidationException(
                    $"{source}: {where}: attributes.{name} is not an attribute name: a letter, then letters, digits and underscores, at most {DimensionAttributeSpec.MaxNameLength}.");
            }

            if (DimensionAttributeSpec.Reserved.Contains(trimmed))
            {
                throw new FlowValidationException(
                    $"{source}: {where}: attributes.{trimmed} takes a name a dimension's rows hold already ({string.Join(", ", DimensionAttributeSpec.Reserved.Order(StringComparer.Ordinal))}); name it after what it holds.");
            }

            if (attributes.Any(a => string.Equals(a.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                throw new FlowValidationException($"{source}: {where}: attributes.{trimmed} is named twice, ignoring case.");
            }

            if (steps is IDictionary<object, object?> settings)
            {
                var (collect, read, keepKey) = MapAttributeSettings(settings, $"attributes.{trimmed}", where, source);
                if (collect is not null)
                {
                    // Two collected attributes would pair values no record holds together (one log's source with another's
                    // type), so a row of the dimension's table is a key and one collected value, counted exactly.
                    if (attributes.FirstOrDefault(a => a.IsCollected) is { } first)
                    {
                        throw new FlowValidationException(
                            $"{source}: {where}: attributes.{trimmed} is a second collected attribute beside {first.Name}; a dimension collects one, so each row of its table is a key and one value its records hold. Collect {trimmed} in a dimension of its own with the same path.");
                    }

                    attributes.Add(new DimensionAttributeSpec(trimmed, [], collect, keepKey));
                }
                else
                {
                    attributes.Add(new DimensionAttributeSpec(trimmed, read!, null, keepKey));
                }

                continue;
            }

            var paths = MapSteps(steps, $"attributes.{trimmed}", where, source);
            if (paths.Count == 0)
            {
                throw new FlowValidationException(
                    $"{source}: {where}: attributes.{trimmed} reads nothing; give the path of the record the key names it is read from (attributes: {{ {trimmed}: data.Name }}).");
            }

            attributes.Add(new DimensionAttributeSpec(trimmed, paths));
        }

        return attributes;
    }

    /// <summary>
    /// The names of the two columns of the dimension's table that hold its key and its value
    /// (<see cref="DimensionColumnNames"/>): the ones the document gives (<c>columns: { key, value }</c>), else the property
    /// the path ends with and the property the label ends with, or the dimension's name when it reads no label; a key's
    /// column the document does not name takes <c>Key</c> at its end where the value's has its name. Each is a column
    /// name, none of the columns every table has, and unlike the other and every attribute, ignoring case; a name the
    /// path, the label or the dimension gives that is not is refused, saying how to name it.
    /// </summary>
    private static (string Key, string Value) MapColumns(
        DimensionColumnsYaml? declared, string dimension, string path, IReadOnlyList<string> label, IReadOnlyList<DimensionAttributeSpec> attributes, string where,
        string source)
    {
        var keyNamed = ColumnName(declared?.Key, "key", where, source);
        var valueNamed = ColumnName(declared?.Value, "value", where, source);
        var value = valueNamed ?? DimensionColumnNames.ValueOf(label, dimension);
        var key = keyNamed ?? DimensionColumnNames.KeyOf(path, value);
        var valueFrom = label.Count > 0 ? "the property its label ends with" : "the dimension (it reads no label)";
        var keyFrom = keyNamed is null && !string.Equals(key, DimensionColumnNames.PropertyOf(path), StringComparison.Ordinal)
            ? $"the property its path ends with and the {DimensionColumnNames.KeySuffix} it takes beside a value's column of that name"
            : "the property its path ends with";

        // What a column is named after, for a name the document did not give: so a refusal says where the name came from.
        string Described(string name, bool isValue) => (isValue ? valueNamed : keyNamed) is not null
            ? $"columns.{(isValue ? "value" : "key")} '{name}'"
            : $"the {(isValue ? "value" : "key")}'s column would be named '{name}', after {(isValue ? valueFrom : keyFrom)}, which";
        string NameIt(bool isValue) => (isValue ? valueNamed : keyNamed) is not null
            ? "Give it another name."
            : $"Name it in the document: columns: {{ {(isValue ? "value" : "key")}: <name> }}.";

        foreach (var (name, isValue) in new[] { (key, false), (value, true) })
        {
            if (DimensionColumnNames.Problem(name, isValue) is { } problem)
            {
                throw new FlowValidationException($"{source}: {where}: {Described(name, isValue)} {problem}. {NameIt(isValue)}");
            }
        }

        // Only a name the document gives the key's column can be the value's: one it does not give steps aside.
        if (string.Equals(key, value, StringComparison.OrdinalIgnoreCase))
        {
            throw new FlowValidationException(
                $"{source}: {where}: columns.key '{key}' is the name of the value's column ({(valueNamed is null ? "named after " + valueFrom : "columns.value")}), and a table has one column of a name. Give the key's column another name, or leave it out for the name the path gives.");
        }

        foreach (var (name, isValue) in new[] { (key, false), (value, true) })
        {
            if (attributes.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) is { } attribute)
            {
                throw new FlowValidationException(
                    $"{source}: {where}: {Described(name, isValue)} is the name of its attribute {attribute.Name}, and a table has one column of a name. {NameIt(isValue).TrimEnd('.')}, or rename the attribute.");
            }
        }

        return (key, value);
    }

    /// <summary>A column name the document gives under <c>columns</c>, trimmed; null when it gives none.</summary>
    private static string? ColumnName(string? declared, string which, string where, string source)
    {
        if (declared is null)
        {
            return null;
        }

        var name = declared.Trim();
        return name.Length > 0
            ? name
            : throw new FlowValidationException(
                $"{source}: {where}: columns.{which} is empty; give the name of the column that holds each {(which == "key" ? "key" : "key's value")}, or leave it out for the name the {(which == "key" ? "path" : "label or the dimension")} gives.");
    }

    /// <summary>
    /// An attribute written as settings: either <c>{ collect: data.Source }</c>, the path of the dimension's own records
    /// whose values each key collects, or <c>{ path: data.WellboreID }</c>, the paths it is read through as the bare form
    /// gives them; either with <c>keep: key</c>, which keeps each value exactly as the record holds it so the column joins
    /// to the dimension keyed by it. A path is one a search matches exactly, as the dimension's own path is.
    /// </summary>
    private static (string? Collect, IReadOnlyList<string>? Steps, bool KeepKey) MapAttributeSettings(
        IDictionary<object, object?> settings, string at, string where, string source)
    {
        var name = at.Split('.')[1];
        var unknown = settings.Keys.Select(k => k?.ToString()).Where(k => k is not ("collect" or "path" or "keep")).ToList();
        var collects = settings.TryGetValue("collect", out var declaredCollect);
        var reads = settings.TryGetValue("path", out var declaredPath);
        if (unknown.Count > 0 || collects == reads)
        {
            throw new FlowValidationException(
                $"{source}: {where}: {at} is a path or a list of paths read from the record the key names ({name}: data.Name), or settings naming one of path and collect ({name}: {{ path: data.WellboreID, keep: key }}, {name}: {{ collect: data.Source }})"
                + (unknown.Count > 0 ? $"; it names {string.Join(", ", unknown)}" : collects ? "; it names both" : "; it names neither") + ".");
        }

        var keepKey = MapKeep(settings, at, where, source);
        if (collects)
        {
            if (declaredCollect is not string collect || string.IsNullOrWhiteSpace(collect))
            {
                throw new FlowValidationException($"{source}: {where}: {at}.collect is the path of the dimension's own records whose values each key collects ({name}: {{ collect: data.Source }}).");
            }

            var path = collect.Trim();
            CheckPath(path, $"{where}: {at}.collect", source);
            return (path, null, keepKey);
        }

        var steps = MapSteps(declaredPath, $"{at}.path", where, source);
        return steps.Count > 0
            ? (null, steps, keepKey)
            : throw new FlowValidationException(
                $"{source}: {where}: {at}.path reads nothing; give the path of the record the key names it is read from ({name}: {{ path: data.WellboreID }}).");
    }

    /// <summary>
    /// How an attribute's values are kept: <c>key</c>, exactly as the record holds them, so a reference stays a whole id and
    /// the column joins to the dimension keyed by it; <c>value</c> (the default), as a value shows them.
    /// </summary>
    private static bool MapKeep(IDictionary<object, object?> settings, string at, string where, string source)
    {
        if (!settings.TryGetValue("keep", out var declared))
        {
            return false;
        }

        return declared?.ToString()?.Trim() switch
        {
            "key" => true,
            "value" => false,
            var other => throw new FlowValidationException(
                $"{source}: {where}: {at}.keep is '{other}'; it is key, to keep each value exactly as the record holds it so the column joins to the dimension keyed by it, or value, as a value shows it."),
        };
    }

    /// <summary>
    /// The paths a label or an attribute is read through: one path, or a list of them, at most
    /// <see cref="DimensionSpec.MaxLabelSteps"/>, each a property path.
    /// </summary>
    private static IReadOnlyList<string> MapSteps(object? declared, string what, string where, string source)
    {
        List<string> steps = declared switch
        {
            null => [],
            string one => [one],
            IEnumerable<object?> many => many.Select((step, i) => step as string
                ?? throw new FlowValidationException($"{source}: {where}: {what}[{i}] is not a path; each step of a {Kind(what)} is a path, such as data.FacilityName.")).ToList(),
            _ => throw new FlowValidationException(
                $"{source}: {where}: {what} is a path ({what}: data.FacilityName) or a list of paths ({what}: [data.GeoContexts.FieldID, data.FieldName]); it is neither."),
        };

        if (steps.Count > DimensionSpec.MaxLabelSteps)
        {
            throw new FlowValidationException(
                $"{source}: {where}: {what} reads through {steps.Count} records; a {Kind(what)} reads through at most {DimensionSpec.MaxLabelSteps}, a search per step for every key.");
        }

        var trimmed = new List<string>(steps.Count);
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i].Trim();
            var (path, problem) = DimensionPath.Parse(step);
            if (path is null || !OsduPath.IsPath(string.Join('.', path.Segments.Select(s => s.Name))))
            {
                throw new FlowValidationException(
                    $"{source}: {where}: {what}{(steps.Count > 1 ? string.Create(CultureInfo.InvariantCulture, $"[{i}]") : string.Empty)} '{step}' is not a property path{(problem is null ? string.Empty : $" ({problem})")}: segments of letters, digits and underscores separated by dots, such as data.FacilityName, a segment holding objects filtered by [Property=text], [Property*=text] or [Property$=text], such as data[GeoPoliticalEntityTypeID$=:Country:].GeoPoliticalEntityName.");
            }

            trimmed.Add(step);
        }

        return trimmed;
    }

    private static IReadOnlyList<string> MapDimensionPartitions(
        IReadOnlyList<string>? declared, IReadOnlyList<string> partitions, bool followsRegistry, string where, string source)
    {
        if (declared is null)
        {
            return [];
        }

        if (partitions.Count == 0 && !followsRegistry)
        {
            throw new FlowValidationException(
                $"{source}: {where}: partitions narrows the partitions of its flow, and the flow's one partition is its header's; name the partitions under 'partitions' at the top of the document, or take the header out so the flow builds in every registered partition.");
        }

        var names = new List<string>(declared.Count);
        for (var i = 0; i < declared.Count; i++)
        {
            var at = string.Create(CultureInfo.InvariantCulture, $"{source}: {where}: partitions[{i}]");
            var partition = PartitionNames.Check(declared[i], at);
            names.Add(followsRegistry
                ? partition
                : partitions.FirstOrDefault(p => string.Equals(p, partition, StringComparison.OrdinalIgnoreCase))
                    ?? throw new FlowValidationException($"{at} '{partition}' is not a partition of the flow; it names {PartitionNames.Listed(partitions)}."));
        }

        PartitionNames.CheckList(names, $"{source}: {where}: partitions");
        return names;
    }

    /// <summary>
    /// The clean steps, each written as its name (<c>trim</c>) or as a map of one step to its settings
    /// (<c>{ replace: { pattern, with } }</c>, <c>{ map: CurveAliases }</c>, <c>{ map: { dictionary, field, otherwise } }</c>).
    /// </summary>
    private static IReadOnlyList<CleanStep> MapClean(List<object>? declared, string where, string source)
    {
        if (declared is null)
        {
            return [];
        }

        if (declared.Count > DimensionSpec.MaxCleanSteps)
        {
            throw new FlowValidationException($"{source}: {where}: clean lists {declared.Count} steps; a dimension applies at most {DimensionSpec.MaxCleanSteps}.");
        }

        var steps = new List<CleanStep>(declared.Count);
        for (var i = 0; i < declared.Count; i++)
        {
            var at = string.Create(CultureInfo.InvariantCulture, $"{source}: {where}: clean[{i}]");
            steps.Add(declared[i] switch
            {
                string written => PlainStep(written.Trim(), at),
                IDictionary<object, object?> map => SettingStep(map, at),
                null => throw new FlowValidationException($"{at} is empty; write a step's name, such as trim, or a step with its settings."),
                _ => throw new FlowValidationException($"{at} is neither a step's name nor a step with its settings."),
            });
        }

        return steps;
    }

    private static CleanStep PlainStep(string written, string at)
    {
        if (PlainSteps.TryGetValue(written, out var kind))
        {
            return new CleanStep { Kind = kind };
        }

        throw new FlowValidationException(written is "replace" or "map"
            ? $"{at}: {written} takes settings; write it as {{ {written}: {(written == "replace" ? "{ pattern: ..., with: ... }" : "<dictionary>")} }}."
            : $"{at}: '{written}' is not a clean step; the steps are {string.Join(", ", StepNames)}.");
    }

    private static CleanStep SettingStep(IDictionary<object, object?> map, string at)
    {
        if (map.Count != 1)
        {
            throw new FlowValidationException(
                $"{at} holds {map.Count} keys; a step is one key, its name, with its settings under it, so each step is its own item of the list.");
        }

        var (key, settings) = map.Single();
        var name = key.ToString() ?? string.Empty;
        switch (name)
        {
            case "replace":
                return ReplaceStep(settings, at);
            case "map":
                return MapStep(settings, at);
            default:
                throw new FlowValidationException(PlainSteps.ContainsKey(name)
                    ? $"{at}: {name} takes no settings; write it as - {name}."
                    : $"{at}: '{name}' is not a clean step; the steps are {string.Join(", ", StepNames)}.");
        }
    }

    private static CleanStep ReplaceStep(object? settings, string at)
    {
        if (settings is not IDictionary<object, object?> values)
        {
            throw new FlowValidationException($"{at}: replace takes {{ pattern: <regular expression>, with: <text> }}.");
        }

        string? pattern = null;
        string? with = null;
        foreach (var (key, value) in values)
        {
            switch (key.ToString())
            {
                case "pattern":
                    pattern = Scalar(value, at + ".pattern");
                    break;
                case "with":
                    with = value is null ? string.Empty : Scalar(value, at + ".with");
                    break;
                default:
                    throw new FlowValidationException($"{at}: replace has no '{key}' setting; it takes {string.Join(" and ", ReplaceSettings)}.");
            }
        }

        if (string.IsNullOrEmpty(pattern))
        {
            throw new FlowValidationException($"{at}: replace needs a pattern: the regular expression whose matches are replaced.");
        }

        if (!values.Keys.Any(k => string.Equals(k.ToString(), "with", StringComparison.Ordinal)))
        {
            throw new FlowValidationException($"{at}: replace needs with: the text each match is replaced by (write with: '' to remove the matches).");
        }

        try
        {
            _ = DimensionCleaner.CompilePattern(pattern);
        }
        catch (ArgumentException ex)
        {
            throw new FlowValidationException($"{at}: the pattern /{pattern}/ cannot be used: {ex.Message}", ex);
        }

        return new CleanStep { Kind = CleanStepKind.Replace, Pattern = pattern, With = with };
    }

    private static CleanStep MapStep(object? settings, string at)
    {
        string? dictionary;
        string? field = null;
        var otherwise = MapOtherwise.Keep;
        string? otherwiseText = null;
        switch (settings)
        {
            case string named:
                dictionary = named.Trim();
                break;
            case IDictionary<object, object?> values:
                dictionary = null;
                foreach (var (key, value) in values)
                {
                    switch (key.ToString())
                    {
                        case "dictionary":
                            dictionary = Scalar(value, at + ".dictionary")?.Trim();
                            break;
                        case "field":
                            field = Scalar(value, at + ".field")?.Trim();
                            break;
                        case "otherwise":
                            // As a mapping's replace reads it: left out keeps the value, ~ gives no value, text gives that text.
                            if (value is null)
                            {
                                otherwise = MapOtherwise.LeaveOut;
                            }
                            else
                            {
                                otherwise = MapOtherwise.Text;
                                otherwiseText = Scalar(value, at + ".otherwise");
                                if (string.IsNullOrWhiteSpace(otherwiseText))
                                {
                                    throw new FlowValidationException(
                                        $"{at}: otherwise is empty text; leave it out to keep an unlisted value, write ~ to leave the key out of every value, or give the text it becomes.");
                                }
                            }

                            break;
                        default:
                            throw new FlowValidationException($"{at}: map has no '{key}' setting; it takes {string.Join(", ", MapSettings)}.");
                    }
                }

                break;
            default:
                throw new FlowValidationException($"{at}: map takes a dictionary's name, or {{ dictionary: <name>, field: <field>, otherwise: <text or ~> }}.");
        }

        if (!DictionaryMapper.IsDictionaryName(dictionary))
        {
            throw new FlowValidationException(
                $"{at}: map needs a dictionary: the name of a dictionary document of the repository, a letter followed by letters, digits, '_' or '-', found as dictionaries/<name>.yaml above the flow.");
        }

        if (field is not null && !DictionaryMapper.IsFieldName(field))
        {
            throw new FlowValidationException($"{at}: field '{field}' is not a dictionary field name: a letter or underscore followed by letters, digits and underscores.");
        }

        return new CleanStep { Kind = CleanStepKind.Map, Dictionary = dictionary, Field = field, Otherwise = otherwise, OtherwiseText = otherwiseText };
    }

    /// <summary>A setting's value as text: a scalar as the document wrote it, a number or a boolean in its invariant form.</summary>
    private static string? Scalar(object? value, string at) => value switch
    {
        null => null,
        string text => text,
        bool flag => flag ? "true" : "false",
        IFormattable formattable when value is not IDictionary and not IList => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => throw new FlowValidationException($"{at} must be text, not {(value is IList ? "a list" : "a map")}."),
    };

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>What a list of steps is read for, as a message names it: a label, or an attribute.</summary>
    private static string Kind(string what) => what == "label" ? "label" : "attribute";

    /// <summary>
    /// The hash of what a dimension declares. A label, a value for unlabelled keys and attributes it does not declare are
    /// left out, and so is the name of a column the document does not give (the one its path, its label or its name
    /// gives), so a dimension declared before any of them existed keeps the hash it was built with.
    /// </summary>
    private static string Hash(DimensionSpec dimension)
    {
        var node = JsonSerializer.SerializeToNode(dimension with { DefinitionHash = string.Empty }, HashJson)!.AsObject();
        foreach (var name in new[] { nameof(DimensionSpec.Label), nameof(DimensionSpec.Attributes), nameof(DimensionSpec.Unlabelled) })
        {
            if (node.TryGetPropertyValue(name, out var value) && (value is null || value is JsonArray { Count: 0 }))
            {
                node.Remove(name);
            }
        }

        if (string.Equals(dimension.KeyColumn, DimensionColumnNames.KeyOf(dimension.Path, dimension.ValueColumn), StringComparison.Ordinal))
        {
            node.Remove(nameof(DimensionSpec.KeyColumn));
        }

        if (string.Equals(dimension.ValueColumn, DimensionColumnNames.ValueOf(dimension.Label, dimension.Name), StringComparison.Ordinal))
        {
            node.Remove(nameof(DimensionSpec.ValueColumn));
        }

        return Hashing.ContentHash.Of(CanonicalJson.ToBytes(node))[..16];
    }
}
