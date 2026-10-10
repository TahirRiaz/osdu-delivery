using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SqlFlow.Core;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// Maps a parsed assertion document onto <see cref="AssertionFlowDefinition"/> and validates what YAML cannot
/// (docs/assertions-design.md section 3): every assertion names exactly one subject, carries only the keys that subject
/// takes, and every operand is of the type its operator compares. What needs the flow's templates (whether a path is a
/// property of the kind, whether a value fits the property's type) is checked when a run or a plan reads them.
/// </summary>
internal static partial class AssertionMapper
{
    /// <summary>The most assertions one test holds.</summary>
    public const int MaxAssertions = 100;

    /// <summary>The most tags one test carries.</summary>
    public const int MaxTags = 20;

    /// <summary>The most ids a test reads by id.</summary>
    public const int MaxIds = 1000;

    /// <summary>The most values an <c>in</c> or <c>notIn</c> lists.</summary>
    public const int MaxListed = ConditionReader.MaxListed;

    /// <summary>The most rows a record set expects.</summary>
    public const int MaxRows = 1000;

    /// <summary>The most columns a record set compares, and the most fields one unique assertion combines.</summary>
    public const int MaxColumns = 20;

    /// <summary>The most conditions one <c>where</c> holds.</summary>
    public const int MaxConditions = 10;

    /// <summary>The longest label an assertion is shown under.</summary>
    public const int MaxLabel = 200;

    /// <summary>How long a <c>matches</c> expression may take on one value before it is refused as a failure to evaluate.</summary>
    public static readonly TimeSpan PatternTimeout = ConditionReader.PatternTimeout;

    /// <summary>The properties an OSDU record has at its root, the only places a field path may start.</summary>
    public static readonly IReadOnlySet<string> RecordRoots = ConditionReader.RecordRoots;

    private static readonly JsonSerializerOptions HashJson = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static AssertionFlowDefinition Map(AssertionYaml y, string source)
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
                    $"{source}: source.headers names '{FlowMapper.PartitionHeader}', and the flow names its partitions: every run sets the header to the partition it tests. Remove the header.");
            }
        }
        else if (headers.TryGetValue(FlowMapper.PartitionHeader, out var partition))
        {
            if (string.IsNullOrWhiteSpace(partition))
            {
                throw new FlowValidationException(
                    $"{source}: source.headers.{FlowMapper.PartitionHeader} is empty. Name the partition the flow tests, or take the header out so the flow tests every partition registered with the catalog.");
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
                $"{source}: parameters declares '{PartitionNames.RunValue}', which an assertion flow keeps for the partition a run tests: {{partition}} in a test is always that partition. Rename the parameter.");
        }

        var defaults = MapDefaults(y.Defaults, source);
        var flow = new AssertionFlowDefinition
        {
            SourcePath = source == "<inline>" ? null : source,
            Name = name,
            Description = Optional(y.Description),
            Batch = Optional(y.Batch),
            Parameters = parameters,
            Source = new AssertionSource
            {
                Endpoint = FlowMapper.Require(src.Endpoint, "source.endpoint", source),
                Auth = FlowMapper.MapAuth(src.Auth, source, "source.auth"),
                Headers = headers,
                QueryPath = ServicePath(src.QueryPath, AssertionSource.DefaultQueryPath, "source.queryPath", source),
                SearchPath = ServicePath(src.SearchPath, AssertionSource.DefaultSearchPath, "source.searchPath", source),
                RecordQueryPath = ServicePath(src.RecordQueryPath, AssertionSource.DefaultRecordQueryPath, "source.recordQueryPath", source),
                LegalPath = ServicePath(src.LegalPath, AssertionSource.DefaultLegalPath, "source.legalPath", source),
                DdmsRoot = ServicePath(src.DdmsRoot, AssertionSource.DefaultDdmsRoot, "source.ddmsRoot", source),
            },
            Partitions = partitions,
            FollowsRegistry = followsRegistry,
            Defaults = defaults,
            FailRunOn = FlowMapper.ParseEnum(y.FailRunOn, FailRunOn.Error, "failRunOn", source),
            Tests = MapTests(y.Tests, parameters, partitions, followsRegistry, defaults, source),
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

    private static AssertionDefaults MapDefaults(AssertionDefaultsYaml? d, string source)
    {
        if (d is null)
        {
            return new AssertionDefaults();
        }

        var defaults = new AssertionDefaults
        {
            MaxRecords = d.MaxRecords ?? AssertionDefaults.DefaultMaxRecords,
            Examples = d.Examples ?? AssertionDefaults.DefaultExamples,
            Read = FlowMapper.ParseEnum(d.Read, AssertionRead.Storage, "defaults.read", source),
            IndexSettleSeconds = d.IndexSettleSeconds ?? AssertionDefaults.DefaultIndexSettleSeconds,
        };
        CheckMaxRecords(defaults.MaxRecords, "defaults.maxRecords", source);
        CheckIndexSettle(defaults.IndexSettleSeconds, "defaults.indexSettleSeconds", source);
        if (defaults.Examples is < 1 or > AssertionDefaults.MaxExamples)
        {
            throw new FlowValidationException($"{source}: defaults.examples must be between 1 and {AssertionDefaults.MaxExamples}.");
        }

        return defaults;
    }

    private static void CheckIndexSettle(int value, string key, string source)
    {
        if (value is < 0 or > AssertionDefaults.MaxIndexSettleSeconds)
        {
            throw new FlowValidationException(
                $"{source}: {key} must be between 0 and {AssertionDefaults.MaxIndexSettleSeconds} seconds: how long the search index is given to list a change before a test of the changed type is judged, 0 judging it whatever changed.");
        }
    }

    private static void CheckMaxRecords(int value, string key, string source)
    {
        if (value is < 1 or > AssertionDefaults.MaxRecordsCeiling)
        {
            throw new FlowValidationException(
                $"{source}: {key} must be between 1 and {AssertionDefaults.MaxRecordsCeiling.ToString("N0", CultureInfo.InvariantCulture)}; a test reads its records into bounded memory, never a partition whole.");
        }
    }

    private static IReadOnlyList<AssertionTest> MapTests(
        List<AssertionTestYaml>? declared, IReadOnlyDictionary<string, FlowParameter> parameters, IReadOnlyList<string> partitions, bool followsRegistry,
        AssertionDefaults defaults, string source)
    {
        if (declared is null || declared.Count == 0)
        {
            throw new FlowValidationException($"{source}: tests must list at least one test; an assertion flow without tests has nothing to run.");
        }

        if (declared.Count > AssertionFlowDefinition.MaxTests)
        {
            throw new FlowValidationException($"{source}: tests lists {declared.Count} tests; one flow holds at most {AssertionFlowDefinition.MaxTests}. Split them over several flows.");
        }

        var tests = new List<AssertionTest>(declared.Count);
        for (var i = 0; i < declared.Count; i++)
        {
            var test = MapTest(declared[i], i, parameters, partitions, followsRegistry, defaults, source);
            if (tests.Any(t => string.Equals(t.Name, test.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new FlowValidationException($"{source}: tests[{i}] is named '{test.Name}', as an earlier test is; a run and a report name each test by its name, so each needs its own.");
            }

            tests.Add(test);
        }

        return tests;
    }

    private static AssertionTest MapTest(
        AssertionTestYaml? t, int index, IReadOnlyDictionary<string, FlowParameter> parameters, IReadOnlyList<string> partitions, bool followsRegistry,
        AssertionDefaults defaults, string source)
    {
        var at = string.Create(CultureInfo.InvariantCulture, $"tests[{index}]");
        if (t is null)
        {
            throw new FlowValidationException($"{source}: {at} is empty.");
        }

        var name = FlowMapper.Require(t.Name, at + ".name", source);
        if (!SelectableNames.IsName(name))
        {
            throw new FlowValidationException($"{source}: {at}.name '{name}' is not a test name: {SelectableNames.Rule}.");
        }

        var where = $"{at} '{name}'";
        var kind = FlowMapper.Require(t.Kind, at + ".kind", source);
        if (!OsduKind.IsExact(kind))
        {
            throw new FlowValidationException(kind.Contains('*', StringComparison.Ordinal)
                ? $"{source}: {where}: kind '{kind}' has wildcards; a test reads one type in one version, the one a mapping delivers "
                    + "(its template.kind), so its records, the template its fields are checked against and its lineage are that type's. "
                    + "Name the kind whole, such as osdu:wks:work-product-component--WellLog:1.4.0."
                : $"{source}: {where}: kind '{kind}' is not authority:source:entityType:major.minor.patch.");
        }

        var ids = MapIds(t.Ids, where, source);
        var query = Optional(t.Query);
        var spatial = MapSpatial(t.Spatial, where, source);
        var sort = MapSort(t.Sort, where, source);
        if (ids.Count > 0 && (query is not null || spatial is not null || sort.Count > 0))
        {
            throw new FlowValidationException(
                $"{source}: {where} reads records by ids, and names {(query is not null ? "a query" : spatial is not null ? "a spatial filter" : "a sort")} as well: a test reads the records its ids name, or the records a search finds, not both.");
        }

        foreach (var (key, text) in ids.Select(id => ("ids", id)).Prepend(("query", query ?? string.Empty)))
        {
            foreach (var token in FlowMapper.Tokens(text))
            {
                if (token != PartitionNames.RunValue && !parameters.ContainsKey(token))
                {
                    throw new FlowValidationException(
                        $"{source}: {where}: {key} uses '{{{token}}}', which is neither {{{PartitionNames.RunValue}}} nor declared under parameters.");
                }
            }
        }

        var template = Optional(t.Template);
        if (template is not null && (template.Length > 64 || template.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))))
        {
            throw new FlowValidationException($"{source}: {where}: template '{template}' is not a template version (at most 64 characters, no whitespace).");
        }

        var read = FlowMapper.ParseEnum(t.Read, defaults.Read, at + ".read", source);
        var maxRecords = t.MaxRecords ?? defaults.MaxRecords;
        CheckMaxRecords(maxRecords, at + ".maxRecords", source);
        var indexSettle = t.IndexSettleSeconds ?? defaults.IndexSettleSeconds;
        CheckIndexSettle(indexSettle, at + ".indexSettleSeconds", source);
        if (ids.Count > maxRecords)
        {
            throw new FlowValidationException($"{source}: {where} names {ids.Count} ids and reads at most {maxRecords} records; raise maxRecords or name fewer ids.");
        }

        var severity = FlowMapper.ParseEnum(t.Severity, AssertionSeverity.Error, at + ".severity", source);
        var bulk = MapBulk(t.Bulk, kind, where, source);
        var context = new TestContext(where, source, kind, severity, ids.Count > 0, read, sort.Count > 0, bulk is not null);
        var assertions = MapAssertions(t.Assert, context);
        var test = new AssertionTest
        {
            Name = name,
            Description = Optional(t.Description),
            Tags = MapTags(t.Tags, where, source),
            Partitions = MapTestPartitions(t.Partitions, partitions, followsRegistry, where, source),
            Severity = severity,
            Kind = kind,
            Template = template,
            Query = query,
            Ids = ids,
            Spatial = spatial,
            Sort = sort,
            Read = read,
            MaxRecords = maxRecords,
            Sample = t.Sample ?? false,
            IndexSettleSeconds = indexSettle,
            Bulk = bulk is null ? null : bulk with { Columns = BulkColumns(bulk, assertions, where, source) },
            Assertions = assertions,
        };
        return test with { DefinitionHash = Hash(test) };
    }

    /// <summary>
    /// What a test's bulk read reads: the columns the document lists, or when it lists none, the columns its assertions name
    /// (empty, which reads every column, when none does). The columns the assertions name are always read
    /// (osdu/docs/reference/flow/assertion.md, Bulk data), so a list that leaves one out is refused here rather than leaving
    /// an assertion to read a column the read never asked for.
    /// </summary>
    private static IReadOnlyList<string> BulkColumns(AssertionBulk bulk, IReadOnlyList<TestAssertion> assertions, string where, string source)
    {
        var named = new List<(string Column, int Assertion)>();
        for (var i = 0; i < assertions.Count; i++)
        {
            var columns = assertions[i] switch
            {
                ValueAssertion { Target.IsColumn: true } value => value.Where.Select(w => w.Target.Path).Prepend(value.Target.Path),
                AggregateAssertion { Target.IsColumn: true } aggregate => [aggregate.Target.Path],
                MonotonicAssertion monotonic => [monotonic.Column],
                _ => [],
            };
            foreach (var column in columns)
            {
                if (!named.Exists(n => string.Equals(n.Column, column, StringComparison.Ordinal)))
                {
                    named.Add((column, i));
                }
            }
        }

        if (bulk.Columns.Count == 0)
        {
            return named.Select(n => n.Column).ToList();
        }

        var unread = named.Where(n => !bulk.Columns.Contains(n.Column, StringComparer.Ordinal)).ToList();
        if (unread.Count > 0)
        {
            throw new FlowValidationException(string.Create(CultureInfo.InvariantCulture,
                $"{source}: {where}: bulk.columns lists the only columns the test reads, and leaves out {string.Join(", ", unread.Select(u => $"'{u.Column}' (assert[{u.Assertion}])"))}; add {(unread.Count == 1 ? "it" : "them")} to bulk.columns, or leave bulk.columns out to read exactly the columns the assertions name."));
        }

        return bulk.Columns;
    }

    private static string Hash(AssertionTest test)
    {
        var node = JsonSerializer.SerializeToNode(test with { DefinitionHash = string.Empty }, HashJson);
        return Hashing.ContentHash.Of(CanonicalJson.ToBytes(node))[..16];
    }

    private static IReadOnlyList<string> MapIds(List<string>? declared, string where, string source)
    {
        if (declared is null)
        {
            return [];
        }

        if (declared.Count == 0 || declared.Count > MaxIds)
        {
            throw new FlowValidationException($"{source}: {where}: ids lists between 1 and {MaxIds} record ids.");
        }

        var ids = new List<string>(declared.Count);
        foreach (var raw in declared)
        {
            var id = raw?.Trim() ?? string.Empty;
            if (id.Length == 0 || id.Length > 1024 || id.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            {
                throw new FlowValidationException($"{source}: {where}: ids holds '{Shown(id)}', which is not a record id (1 to 1024 characters, no whitespace).");
            }

            if (ids.Contains(id, StringComparer.Ordinal))
            {
                throw new FlowValidationException($"{source}: {where}: ids names '{Shown(id)}' more than once.");
            }

            ids.Add(id);
        }

        return ids;
    }

    private static IReadOnlyList<string> MapTags(List<string>? declared, string where, string source)
    {
        if (declared is null)
        {
            return [];
        }

        if (declared.Count > MaxTags)
        {
            throw new FlowValidationException($"{source}: {where}: tags lists {declared.Count} tags; a test carries at most {MaxTags}.");
        }

        var tags = new List<string>(declared.Count);
        foreach (var raw in declared)
        {
            var tag = raw?.Trim() ?? string.Empty;
            if (!SelectableNames.IsName(tag))
            {
                throw new FlowValidationException($"{source}: {where}: tag '{Shown(tag)}' is not a tag: {SelectableNames.Rule}.");
            }

            if (tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                throw new FlowValidationException($"{source}: {where}: tags names '{tag}' more than once.");
            }

            tags.Add(tag);
        }

        return tags;
    }

    private static IReadOnlyList<string> MapTestPartitions(List<string>? declared, IReadOnlyList<string> partitions, bool followsRegistry, string where, string source)
    {
        if (declared is null)
        {
            return [];
        }

        if (partitions.Count == 0 && !followsRegistry)
        {
            throw new FlowValidationException(
                $"{source}: {where}: partitions narrows the partitions of its flow, and the flow's one partition is its header's; name the partitions under 'partitions' at the top of the document, or take the header out so the flow tests every registered partition.");
        }

        var names = new List<string>(declared.Count);
        for (var i = 0; i < declared.Count; i++)
        {
            var at = string.Create(CultureInfo.InvariantCulture, $"{source}: {where}: partitions[{i}]");
            var name = PartitionNames.Check(declared[i], at);
            names.Add(followsRegistry
                ? name
                : partitions.FirstOrDefault(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new FlowValidationException($"{at} '{name}' is not a partition of the flow; it names {PartitionNames.Listed(partitions)}."));
        }

        PartitionNames.CheckList(names, $"{source}: {where}: partitions");
        return names;
    }

    private static IReadOnlyList<AssertionSort> MapSort(List<AssertionSortYaml>? declared, string where, string source)
    {
        if (declared is null)
        {
            return [];
        }

        if (declared.Count is 0 or > 5)
        {
            throw new FlowValidationException($"{source}: {where}: sort lists between 1 and 5 fields.");
        }

        var sort = new List<AssertionSort>(declared.Count);
        foreach (var s in declared)
        {
            var field = ConditionReader.RecordPath(FlowMapper.Require(s?.Field, where + ".sort[].field", source), where + ": sort", source);
            var order = s!.Order?.Trim().ToLowerInvariant() ?? "asc";
            if (order is not ("asc" or "desc"))
            {
                throw new FlowValidationException($"{source}: {where}: sort order '{s.Order}' is not asc or desc.");
            }

            sort.Add(new AssertionSort(field, order == "desc"));
        }

        return sort;
    }

    private static AssertionBulk? MapBulk(AssertionBulkYaml? b, string kind, string where, string source)
    {
        if (b is null)
        {
            return null;
        }

        var entityType = OsduKind.EntityType(kind);
        var collection = entityType is null
            ? null
            : DdmsCatalog.WellboreDdmsCollections.FirstOrDefault(c => c.Bulk && string.Equals(c.EntityType, entityType, StringComparison.OrdinalIgnoreCase));
        if (collection is null)
        {
            throw new FlowValidationException(
                $"{source}: {where} reads bulk data, and its kind '{kind}' is not one the Wellbore DDMS keeps bulk data for; it serves {string.Join(", ", DdmsCatalog.WellboreDdmsCollections.Where(c => c.Bulk).Select(c => c.EntityType))}.");
        }

        var columns = new List<string>();
        foreach (var raw in b.Columns ?? [])
        {
            var column = CheckColumn(raw, where + ": bulk.columns", source);
            if (columns.Contains(column, StringComparer.Ordinal))
            {
                throw new FlowValidationException($"{source}: {where}: bulk.columns names '{column}' more than once.");
            }

            columns.Add(column);
        }

        var maxRows = b.MaxRows ?? AssertionBulk.DefaultMaxRows;
        if (maxRows is < 1 or > AssertionBulk.MaxRowsCeiling)
        {
            throw new FlowValidationException($"{source}: {where}: bulk.maxRows must be between 1 and {AssertionBulk.MaxRowsCeiling.ToString("N0", CultureInfo.InvariantCulture)}.");
        }

        return new AssertionBulk { Collection = collection.Segment, Columns = columns, MaxRows = maxRows };
    }

    /// <summary>
    /// The search's spatial filter as written (openapi search v2, <c>SpatialFilter</c>): a geo field, and exactly one of the
    /// filter types, each point with its latitude and longitude in range.
    /// </summary>
    private static JsonObject? MapSpatial(object? declared, string where, string source)
    {
        if (declared is null)
        {
            return null;
        }

        var at = where + ": spatial";
        if (ToJson(declared, at, source) is not JsonObject spatial)
        {
            throw new FlowValidationException($"{source}: {at} must be a mapping: a field and one filter (byBoundingBox, byDistance, byGeoPolygon, byIntersection, byWithinPolygon).");
        }

        string[] filters = ["byBoundingBox", "byDistance", "byGeoPolygon", "byIntersection", "byWithinPolygon"];
        foreach (var (key, _) in spatial)
        {
            if (key != "field" && !filters.Contains(key, StringComparer.Ordinal))
            {
                throw new FlowValidationException($"{source}: {at} has '{key}', which is not field or one of {string.Join(", ", filters)}.");
            }
        }

        if (spatial["field"] is not JsonValue fieldValue || !fieldValue.TryGetValue<string>(out var field) || string.IsNullOrWhiteSpace(field))
        {
            throw new FlowValidationException($"{source}: {at}.field is required: the geo field the filter applies to, such as data.SpatialLocation.Wgs84Coordinates.");
        }

        _ = ConditionReader.RecordPath(field, at + ".field", source);
        var named = filters.Where(f => spatial.ContainsKey(f)).ToList();
        if (named.Count != 1)
        {
            throw new FlowValidationException($"{source}: {at} names {(named.Count == 0 ? "no filter" : string.Join(" and ", named))}; the search takes exactly one of {string.Join(", ", filters)}.");
        }

        var filter = spatial[named[0]] as JsonObject
            ?? throw new FlowValidationException($"{source}: {at}.{named[0]} must be a mapping.");
        switch (named[0])
        {
            case "byBoundingBox":
                Point(filter["topLeft"], $"{at}.byBoundingBox.topLeft", source);
                Point(filter["bottomRight"], $"{at}.byBoundingBox.bottomRight", source);
                Only(filter, ["topLeft", "bottomRight"], $"{at}.byBoundingBox", source);
                break;
            case "byDistance":
                Point(filter["point"], $"{at}.byDistance.point", source);
                if (!IsNumber(filter["distance"], out var distance) || distance < 0)
                {
                    throw new FlowValidationException($"{source}: {at}.byDistance.distance must be a distance in metres, zero or more.");
                }

                Only(filter, ["point", "distance"], $"{at}.byDistance", source);
                break;
            case "byIntersection":
                if (filter["polygons"] is not JsonArray polygons || polygons.Count == 0)
                {
                    throw new FlowValidationException($"{source}: {at}.byIntersection.polygons must list at least one polygon.");
                }

                for (var i = 0; i < polygons.Count; i++)
                {
                    var polygonAt = string.Create(CultureInfo.InvariantCulture, $"{at}.byIntersection.polygons[{i}]");
                    var polygon = polygons[i] as JsonObject ?? throw new FlowValidationException($"{source}: {polygonAt} must be a mapping with points.");
                    Points(polygon["points"], polygonAt + ".points", source);
                    Only(polygon, ["points"], polygonAt, source);
                }

                Only(filter, ["polygons"], $"{at}.byIntersection", source);
                break;
            default:
                Points(filter["points"], $"{at}.{named[0]}.points", source);
                Only(filter, ["points"], $"{at}.{named[0]}", source);
                break;
        }

        return spatial;
    }

    /// <summary>
    /// A finite number the document wrote, whatever YAML read it as: <c>72</c> arrives as a whole number and <c>72.5</c> as a
    /// fraction, and each is read from the JSON it writes, so a whole-number latitude or distance counts as surely as a decimal.
    /// </summary>
    private static bool IsNumber(JsonNode? node, out double value)
    {
        value = 0;
        return node is JsonValue number
            && number.GetValueKind() == JsonValueKind.Number
            && double.TryParse(number.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value);
    }

    private static void Only(JsonObject node, string[] keys, string at, string source)
    {
        foreach (var (key, _) in node)
        {
            if (!keys.Contains(key, StringComparer.Ordinal))
            {
                throw new FlowValidationException($"{source}: {at} has '{key}', which is not one of {string.Join(", ", keys)}.");
            }
        }
    }

    private static void Points(JsonNode? node, string at, string source)
    {
        if (node is not JsonArray points || points.Count == 0)
        {
            throw new FlowValidationException($"{source}: {at} must list at least one point.");
        }

        for (var i = 0; i < points.Count; i++)
        {
            Point(points[i], string.Create(CultureInfo.InvariantCulture, $"{at}[{i}]"), source);
        }
    }

    private static void Point(JsonNode? node, string at, string source)
    {
        if (node is not JsonObject point
            || !IsNumber(point["latitude"], out var latitude)
            || !IsNumber(point["longitude"], out var longitude)
            || latitude is < -90 or > 90 || longitude is < -180 or > 180)
        {
            throw new FlowValidationException($"{source}: {at} must be a point: latitude between -90 and 90, longitude between -180 and 180.");
        }

        Only(point, ["latitude", "longitude"], at, source);
    }

    private sealed record TestContext(
        string Where, string Source, string Kind, AssertionSeverity Severity, bool ByIds, AssertionRead Read, bool Sorted, bool Bulk);

    private static IReadOnlyList<TestAssertion> MapAssertions(List<AssertionItemYaml>? declared, TestContext test)
    {
        if (declared is null || declared.Count == 0)
        {
            throw new FlowValidationException($"{test.Source}: {test.Where}: assert must list at least one assertion; a test without one asserts nothing.");
        }

        if (declared.Count > MaxAssertions)
        {
            throw new FlowValidationException($"{test.Source}: {test.Where}: assert lists {declared.Count} assertions; a test holds at most {MaxAssertions}. Split it into several tests.");
        }

        var assertions = new List<TestAssertion>(declared.Count);
        for (var i = 0; i < declared.Count; i++)
        {
            var at = string.Create(CultureInfo.InvariantCulture, $"{test.Where}: assert[{i}]");
            var item = declared[i] ?? throw new FlowValidationException($"{test.Source}: {at} is empty.");
            assertions.Add(MapAssertion(item, at, test));
        }

        return assertions;
    }

    /// <summary>The keys an assertion item sets, by the names the document writes them with.</summary>
    private static List<string> KeysOf(AssertionConditionYaml y)
    {
        var keys = new List<string>();
        void Add(bool present, string key)
        {
            if (present)
            {
                keys.Add(key);
            }
        }

        Add(y.Field is not null, "field");
        Add(y.Column is not null, "column");
        foreach (var (key, _) in OperatorsOf(y))
        {
            keys.Add(key);
        }

        Add(y.IgnoreCase is not null, "ignoreCase");
        Add(y.Tolerance is not null, "tolerance");
        if (y is AssertionItemYaml item)
        {
            Add(item.Count is not null, "count");
            Add(item.Aggregate is not null, "aggregate");
            Add(item.Unique is not null, "unique");
            Add(item.GroupBy is not null, "groupBy");
            Add(item.Groups is not null, "groups");
            Add(item.Absent is not null, "absent");
            Add(item.Mode is not null, "mode");
            Add(item.GroupCount is not null, "groupCount");
            Add(item.RecordSet is not null, "recordSet");
            Add(item.Conforms is not null, "conforms");
            Add(item.Indexed is not null, "indexed");
            Add(item.Legal is not null, "legal");
            Add(item.Delivered is not null, "delivered");
            Add(item.Interface is not null, "interface");
            Add(item.Exact is not null, "exact");
            Add(item.RowCount is not null, "rowCount");
            Add(item.Columns is not null, "columns");
            Add(item.Monotonic is not null, "monotonic");
            Add(item.For is not null, "for");
            Add(item.Values is not null, "values");
            Add(item.Optional is not null, "optional");
            Add(item.Where is not null, "where");
        }

        return keys;
    }

    /// <summary>The operators a condition sets, each with its operand as the document writes it.</summary>
    private static List<(string Key, object Operand)> OperatorsOf(AssertionConditionYaml y)
    {
        var operators = new List<(string, object)>();
        void Add(object? operand, string key)
        {
            if (operand is not null)
            {
                operators.Add((key, operand));
            }
        }

        Add(y.EqualTo, "equals");
        Add(y.NotEquals, "notEquals");
        Add(y.In, "in");
        Add(y.NotIn, "notIn");
        Add(y.AtLeast, "atLeast");
        Add(y.AtMost, "atMost");
        Add(y.GreaterThan, "greaterThan");
        Add(y.LessThan, "lessThan");
        Add(y.Between, "between");
        Add(y.Matches, "matches");
        Add(y.NotMatches, "notMatches");
        Add(y.StartsWith, "startsWith");
        Add(y.EndsWith, "endsWith");
        Add(y.Contains, "contains");
        Add(y.NotContains, "notContains");
        Add(y.Exists, "exists");
        Add(y.Empty, "empty");
        Add(y.Type, "type");
        Add(y.Length, "length");
        Add(y.Resolves, "resolves");
        return operators;
    }

    private static readonly string[] Common = ["name", "description", "severity"];

    private static TestAssertion MapAssertion(AssertionItemYaml y, string at, TestContext test)
    {
        var subjects = new List<string>();
        foreach (var (present, key) in new (bool, string)[]
                 {
                     (y.Count is not null, "count"), (y.Aggregate is not null, "aggregate"), (y.Monotonic is not null, "monotonic"),
                     (y.Unique is not null, "unique"), (y.GroupBy is not null, "groupBy"), (y.RecordSet is not null, "recordSet"),
                     (y.Conforms is not null, "conforms"), (y.Indexed is not null, "indexed"), (y.Legal is not null, "legal"),
                     (y.Delivered is not null, "delivered"), (y.RowCount is not null, "rowCount"), (y.Columns is not null, "columns"),
                 })
        {
            if (present)
            {
                subjects.Add(key);
            }
        }

        if (subjects.Count == 0)
        {
            if (y.Field is not null && y.Column is not null)
            {
                throw new FlowValidationException($"{test.Source}: {at} names both a field and a column; an assertion looks at a field of each record or a column of its bulk data, not both.");
            }

            if (y.Field is not null)
            {
                subjects.Add("field");
            }
            else if (y.Column is not null)
            {
                subjects.Add("column");
            }
        }

        if (subjects.Count != 1)
        {
            throw new FlowValidationException(subjects.Count == 0
                ? $"{test.Source}: {at} names no subject; an assertion is one of count, field, column, aggregate, unique, groupBy, recordSet, conforms, indexed, legal, delivered, rowCount, columns or monotonic."
                : $"{test.Source}: {at} names {string.Join(" and ", subjects)}; an assertion has one subject. Write one assertion for each.");
        }

        var subject = subjects[0];
        var severity = FlowMapper.ParseEnum(y.Severity, test.Severity, at + ".severity", test.Source);
        var description = Optional(y.Description);
        TestAssertion assertion = subject switch
        {
            "count" => MapCount(y, at, test),
            "field" or "column" => MapValue(y, subject == "column", at, test),
            "aggregate" => MapAggregate(y, at, test),
            "unique" => MapUnique(y, at, test),
            "groupBy" => MapGroup(y, at, test),
            "recordSet" => MapRecordSet(y, at, test),
            "conforms" => MapConforms(y, at, test),
            "indexed" => MapIndexed(y, at, test),
            "legal" => MapLegal(y, at, test),
            "delivered" => MapDelivered(y, at, test),
            "rowCount" => MapRowCount(y, at, test),
            "columns" => MapColumns(y, at, test),
            _ => MapMonotonic(y, at, test),
        };

        var name = Optional(y.Name);
        if (name is not null && name.Length > MaxLabel)
        {
            throw new FlowValidationException($"{test.Source}: {at}.name is longer than {MaxLabel} characters.");
        }

        return assertion with
        {
            Label = name ?? Clip(assertion.Label, MaxLabel),
            Description = description,
            Severity = severity,
        };
    }

    /// <summary>Refuses every key of <paramref name="y"/> that an assertion of <paramref name="subject"/> does not take.</summary>
    private static void OnlyKeys(AssertionConditionYaml y, string subject, IEnumerable<string> allowed, string at, TestContext test)
    {
        var accepted = new HashSet<string>(allowed.Concat(Common), StringComparer.Ordinal);
        var stray = KeysOf(y).Where(k => !accepted.Contains(k)).ToList();
        if (stray.Count > 0)
        {
            throw new FlowValidationException(
                $"{test.Source}: {at} is {Article(subject)} {subject} assertion, which does not take {string.Join(", ", stray)}.");
        }
    }

    private static string Article(string subject) => subject.Length > 0 && "aeiou".Contains(char.ToLowerInvariant(subject[0]), StringComparison.Ordinal) ? "an" : "a";

    private static CountAssertion MapCount(AssertionItemYaml y, string at, TestContext test)
    {
        OnlyKeys(y, "count", ["count"], at, test);
        var comparison = ConditionReader.CountComparison(y.Count!, at + ".count", test.Source);
        return new CountAssertion(comparison) { Label = $"count {comparison}" };
    }

    private static RowCountAssertion MapRowCount(AssertionItemYaml y, string at, TestContext test)
    {
        OnlyKeys(y, "rowCount", ["rowCount"], at, test);
        RequireBulk("rowCount", at, test);
        var comparison = ConditionReader.CountComparison(y.RowCount!, at + ".rowCount", test.Source);
        return new RowCountAssertion(comparison) { Label = $"rowCount {comparison}" };
    }

    private static ConformsAssertion MapConforms(AssertionItemYaml y, string at, TestContext test)
    {
        OnlyKeys(y, "conforms", ["conforms"], at, test);
        RequireTrue(y.Conforms, "conforms", at, test);
        if (test.Read == AssertionRead.Index)
        {
            throw new FlowValidationException(
                $"{test.Source}: {at} asserts that every record meets its template, which needs each record as storage holds it; the test reads the index (read: index), which holds only what it indexed. Take read: index out.");
        }

        return new ConformsAssertion { Label = "conforms to its template" };
    }

    private static IndexedAssertion MapIndexed(AssertionItemYaml y, string at, TestContext test)
    {
        OnlyKeys(y, "indexed", ["indexed"], at, test);
        RequireTrue(y.Indexed, "indexed", at, test);
        if (test.ByIds)
        {
            throw new FlowValidationException($"{test.Source}: {at} asks the search index how it indexed the records, and the test reads its records by id; give it a query instead.");
        }

        return new IndexedAssertion { Label = "indexed cleanly" };
    }

    private static void RequireTrue(bool? flag, string key, string at, TestContext test)
    {
        if (flag != true)
        {
            throw new FlowValidationException($"{test.Source}: {at}.{key} is written 'true'; to stop asserting it, remove the assertion.");
        }
    }

    private static LegalAssertion MapLegal(AssertionItemYaml y, string at, TestContext test)
    {
        OnlyKeys(y, "legal", ["legal"], at, test);
        if (!string.Equals(y.Legal?.Trim(), "valid", StringComparison.Ordinal))
        {
            throw new FlowValidationException($"{test.Source}: {at}.legal is written 'valid': every legal tag the records carry has to be valid.");
        }

        return new LegalAssertion { Label = "legal tags valid" };
    }

    private static DeliveredAssertion MapDelivered(AssertionItemYaml y, string at, TestContext test)
    {
        OnlyKeys(y, "delivered", ["delivered", "interface", "exact"], at, test);
        var flow = y.Delivered!.Trim();
        if (flow.Length == 0 || flow.Length > FlowDefinition.MaxLedgerNameLength || flow.Any(char.IsControl))
        {
            throw new FlowValidationException($"{test.Source}: {at}.delivered names the delivery flow whose ledger is read: 1 to {FlowDefinition.MaxLedgerNameLength} characters.");
        }

        var named = Optional(y.Interface);
        if (named is not null && !SourceDefinition.IsInterfaceName(named))
        {
            throw new FlowValidationException($"{test.Source}: {at}.interface '{named}' is not an interface name: a letter followed by letters, digits, '_' and '-'.");
        }

        return new DeliveredAssertion
        {
            Flow = flow,
            Interface = named,
            Exact = y.Exact ?? false,
            Label = $"delivered by {flow}{(named is null ? string.Empty : "/" + named)}",
        };
    }

    private static ColumnsAssertion MapColumns(AssertionItemYaml y, string at, TestContext test)
    {
        OnlyKeys(y, "columns", ["columns"], at, test);
        RequireBulk("columns", at, test);
        var c = y.Columns!;
        IReadOnlyList<string> List(List<string>? declared, string key)
        {
            var list = new List<string>();
            foreach (var raw in declared ?? [])
            {
                var column = CheckColumn(raw, $"{at}.columns.{key}", test.Source);
                if (!list.Contains(column, StringComparer.Ordinal))
                {
                    list.Add(column);
                }
            }

            return list;
        }

        var assertion = new ColumnsAssertion
        {
            Includes = List(c.Includes, "includes"),
            Excludes = List(c.Excludes, "excludes"),
            Exactly = List(c.Exactly, "equals"),
            Label = "columns",
        };
        if (assertion.Includes.Count + assertion.Excludes.Count + assertion.Exactly.Count == 0)
        {
            throw new FlowValidationException($"{test.Source}: {at}.columns names no column: list them under includes, excludes or equals.");
        }

        var both = assertion.Includes.Concat(assertion.Exactly).Intersect(assertion.Excludes, StringComparer.Ordinal).ToList();
        if (both.Count > 0)
        {
            throw new FlowValidationException($"{test.Source}: {at}.columns both expects and excludes {string.Join(", ", both)}.");
        }

        return assertion with { Label = "columns " + assertion.Expected };
    }

    private static MonotonicAssertion MapMonotonic(AssertionItemYaml y, string at, TestContext test)
    {
        OnlyKeys(y, "monotonic", ["monotonic", "column"], at, test);
        RequireBulk("monotonic", at, test);
        var column = CheckColumn(y.Column ?? throw new FlowValidationException($"{test.Source}: {at} asserts an order, and names no column to read it in."), at + ".column", test.Source);
        var direction = FlowMapper.ParseEnum<MonotonicDirection>(y.Monotonic!.Trim(), at + ".monotonic", test.Source);
        return new MonotonicAssertion(column, direction) { Label = $"{column} {AssertionText.Of(direction)}" };
    }

    private static UniqueAssertion MapUnique(AssertionItemYaml y, string at, TestContext test)
    {
        OnlyKeys(y, "unique", ["unique"], at, test);
        var fields = new List<string>();
        foreach (var raw in y.Unique!)
        {
            var path = ConditionReader.RecordPath(raw, at + ".unique", test.Source);
            if (fields.Contains(path, StringComparer.Ordinal))
            {
                throw new FlowValidationException($"{test.Source}: {at}.unique names '{path}' more than once.");
            }

            fields.Add(path);
        }

        if (fields.Count is 0 or > MaxColumns)
        {
            throw new FlowValidationException($"{test.Source}: {at}.unique lists between 1 and {MaxColumns} fields whose values, together, no two records may share.");
        }

        return new UniqueAssertion(fields) { Label = "unique " + string.Join(" + ", fields) };
    }

    private static GroupAssertion MapGroup(AssertionItemYaml y, string at, TestContext test)
    {
        OnlyKeys(y, "groupBy", ["groupBy", "groups", "absent", "mode", "groupCount"], at, test);
        if (test.ByIds)
        {
            throw new FlowValidationException($"{test.Source}: {at} groups what the search index holds, and the test reads its records by id; give it a query instead.");
        }

        var field = ConditionReader.RecordPath(y.GroupBy!, at + ".groupBy", test.Source);
        var groups = new List<ExpectedGroup>();
        foreach (var (key, value) in y.Groups ?? [])
        {
            var group = key?.Trim() ?? string.Empty;
            if (group.Length == 0)
            {
                throw new FlowValidationException($"{test.Source}: {at}.groups names a group with an empty key.");
            }

            groups.Add(new ExpectedGroup(group, ConditionReader.CountComparison(value, $"{at}.groups.{group}", test.Source)));
        }

        var absent = new List<string>();
        foreach (var raw in y.Absent ?? [])
        {
            var group = raw?.Trim() ?? string.Empty;
            if (group.Length == 0 || absent.Contains(group, StringComparer.Ordinal))
            {
                throw new FlowValidationException($"{test.Source}: {at}.absent lists an empty group, or one group twice.");
            }

            absent.Add(group);
        }

        var clash = groups.Select(g => g.Key).Intersect(absent, StringComparer.Ordinal).ToList();
        if (clash.Count > 0)
        {
            throw new FlowValidationException($"{test.Source}: {at} both expects and excludes the group {string.Join(", ", clash)}.");
        }

        var mode = y.Mode?.Trim().ToLowerInvariant();
        if (mode is not (null or "includes" or "exact"))
        {
            throw new FlowValidationException($"{test.Source}: {at}.mode '{y.Mode}' is not includes or exact.");
        }

        if (mode is not null && groups.Count == 0)
        {
            throw new FlowValidationException($"{test.Source}: {at}.mode says how the groups listed under groups compare with the ones found, and groups lists none.");
        }

        var groupCount = y.GroupCount is null ? null : ConditionReader.CountComparison(y.GroupCount, at + ".groupCount", test.Source);
        if (groups.Count == 0 && absent.Count == 0 && groupCount is null)
        {
            throw new FlowValidationException($"{test.Source}: {at} groups by {field} and expects nothing of the groups: name them under groups or absent, or bound their number with groupCount.");
        }

        return new GroupAssertion
        {
            Field = field,
            Groups = groups,
            Exact = mode == "exact",
            Absent = absent,
            GroupCount = groupCount,
            Label = $"groups of {field}",
        };
    }

    private static RecordSetAssertion MapRecordSet(AssertionItemYaml y, string at, TestContext test)
    {
        OnlyKeys(y, "recordSet", ["recordSet"], at, test);
        var set = y.RecordSet!;
        var columns = new List<string>();
        foreach (var raw in set.Columns ?? [])
        {
            columns.Add(ConditionReader.RecordPath(raw, at + ".recordSet.columns", test.Source));
        }

        if (columns.Count is 0 or > MaxColumns)
        {
            throw new FlowValidationException($"{test.Source}: {at}.recordSet.columns lists between 1 and {MaxColumns} fields each record is projected onto.");
        }

        var mode = FlowMapper.ParseEnum(set.Mode, RecordSetMode.Exact, at + ".recordSet.mode", test.Source);
        if (mode == RecordSetMode.Ordered && !test.Sorted)
        {
            throw new FlowValidationException(
                $"{test.Source}: {at} compares the records in order, and the test names no sort; the search returns records in no particular order without one. Give the test a sort.");
        }

        if (set.Rows is null || set.Rows.Count > MaxRows)
        {
            throw new FlowValidationException($"{test.Source}: {at}.recordSet.rows lists at most {MaxRows} rows, each a list of {columns.Count} value(s).");
        }

        var rows = new List<IReadOnlyList<ExpectedValue?>>(set.Rows.Count);
        for (var i = 0; i < set.Rows.Count; i++)
        {
            var rowAt = string.Create(CultureInfo.InvariantCulture, $"{at}.recordSet.rows[{i}]");
            var row = set.Rows[i] ?? throw new FlowValidationException($"{test.Source}: {rowAt} is empty.");
            if (row.Count != columns.Count)
            {
                throw new FlowValidationException($"{test.Source}: {rowAt} holds {row.Count} value(s) for {columns.Count} column(s).");
            }

            rows.Add(row.Select(cell => cell is null ? null : ConditionReader.Scalar(cell, rowAt, test.Source)).ToList());
        }

        if (rows.Count == 0 && mode is RecordSetMode.Includes or RecordSetMode.Excludes)
        {
            throw new FlowValidationException($"{test.Source}: {at}.recordSet.rows lists no row, so '{AssertionText.Of(mode)}' has nothing to compare; list the rows, or use mode exact to assert that no record matches.");
        }

        return new RecordSetAssertion
        {
            Columns = columns,
            Mode = mode,
            Rows = rows,
            Label = $"record set of {string.Join(", ", columns)}",
        };
    }

    private static AggregateAssertion MapAggregate(AssertionItemYaml y, string at, TestContext test)
    {
        var isColumn = y.Column is not null;
        OnlyKeys(y, "aggregate", ConditionReader.ComparisonKeys.Append("aggregate").Append(isColumn ? "column" : "field").Append("tolerance"), at, test);
        var function = FlowMapper.ParseEnum<AggregateFunction>(y.Aggregate!.Trim(), at + ".aggregate", test.Source);
        ValueTarget target;
        if (isColumn)
        {
            RequireBulk("column aggregate", at, test);
            target = new ValueTarget(CheckColumn(y.Column!, at + ".column", test.Source), IsColumn: true);
        }
        else
        {
            var field = y.Field ?? throw new FlowValidationException($"{test.Source}: {at} aggregates, and names no field (or column) to aggregate.");
            target = new ValueTarget(ConditionReader.RecordPath(field, at + ".field", test.Source), IsColumn: false);
        }

        // Min and max compare dates as well as numbers; every other aggregate is a number.
        var numbersOnly = function is not (AggregateFunction.Min or AggregateFunction.Max);
        var terms = OperatorsOf(y).Select(o => ConditionReader.Term(o.Key, o.Operand, $"{at}.{o.Key}", test.Source, numbersOnly)).ToList();
        if (terms.Count == 0)
        {
            throw new FlowValidationException($"{test.Source}: {at} aggregates {target} and compares it with nothing: give it one of {string.Join(", ", ConditionReader.ComparisonKeys)}.");
        }

        var tolerance = ConditionReader.Tolerance(y.Tolerance, at, test.Source);
        if (tolerance is not null && terms.All(t => t.Value.Kind != ExpectedValueKind.Number))
        {
            throw new FlowValidationException($"{test.Source}: {at}.tolerance applies to comparisons of numbers, and the aggregate is compared with a date.");
        }

        var assertion = new AggregateAssertion
        {
            Function = function,
            Target = target,
            Comparison = new Comparison(terms),
            Tolerance = tolerance,
            Label = string.Empty,
        };
        return assertion with { Label = $"{AssertionText.Of(function)}({target}) {assertion.Expected}" };
    }

    private static ValueAssertion MapValue(AssertionItemYaml y, bool isColumn, string at, TestContext test)
    {
        var subject = isColumn ? "column" : "field";
        var takes = OperatorsOf(y).Select(o => o.Key).ToList();
        takes.AddRange([subject, "ignoreCase", "tolerance", "for", "optional", "where"]);
        if (!isColumn)
        {
            takes.Add("values");
        }

        OnlyKeys(y, subject, takes, at, test);
        ValueTarget target;
        if (isColumn)
        {
            RequireBulk("column", at, test);
            target = new ValueTarget(CheckColumn(y.Column!, at + ".column", test.Source), IsColumn: true);
        }
        else
        {
            target = new ValueTarget(ConditionReader.RecordPath(y.Field!, at + ".field", test.Source), IsColumn: false);
        }

        var condition = MapCondition(y, isColumn, at, test);
        var quantifier = MapQuantifier(y.For, at, test.Source);
        var anyValue = (y.Values?.Trim().ToLowerInvariant()) switch
        {
            null or "all" => false,
            "any" => true,
            _ => throw new FlowValidationException($"{test.Source}: {at}.values '{y.Values}' is not all or any."),
        };
        if (condition.Operator == ValueOperator.Resolves && (y.For is not null || y.Values is not null))
        {
            throw new FlowValidationException(
                $"{test.Source}: {at} asserts that references resolve, which holds for every reference the records carry; for and values do not apply.");
        }

        var where = new List<ValueFilter>();
        foreach (var (filter, i) in (y.Where ?? []).Select((c, i) => (c, i)))
        {
            var whereAt = string.Create(CultureInfo.InvariantCulture, $"{at}.where[{i}]");
            if (filter is null)
            {
                throw new FlowValidationException($"{test.Source}: {whereAt} is empty.");
            }

            where.Add(MapFilter(filter, isColumn, whereAt, test));
        }

        if (y.Where is { Count: 0 or > MaxConditions })
        {
            throw new FlowValidationException($"{test.Source}: {at}.where lists between 1 and {MaxConditions} conditions.");
        }

        var assertion = new ValueAssertion
        {
            Target = target,
            Condition = condition,
            For = quantifier,
            AnyValue = anyValue,
            Optional = y.Optional ?? false,
            Where = where,
            Label = string.Empty,
        };
        var label = $"{target} {condition}"
            + (quantifier.Mode == QuantifierMode.All ? string.Empty : $" for {quantifier}")
            + (where.Count == 0 ? string.Empty : " where " + string.Join(" and ", where));
        return assertion with { Label = label };
    }

    private static ValueFilter MapFilter(AssertionConditionYaml y, bool isColumn, string at, TestContext test)
    {
        var subject = isColumn ? "column" : "field";
        var keys = KeysOf(y);
        var allowed = OperatorsOf(y).Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
        allowed.UnionWith([subject, "ignoreCase", "tolerance"]);
        var stray = keys.Where(k => !allowed.Contains(k)).ToList();
        if (stray.Count > 0)
        {
            throw new FlowValidationException(
                $"{test.Source}: {at} selects the {(isColumn ? "rows" : "records")} the assertion looks at by a {subject}, and does not take {string.Join(", ", stray)}.");
        }

        var target = isColumn
            ? new ValueTarget(CheckColumn(y.Column ?? throw new FlowValidationException($"{test.Source}: {at} names no column; a condition on a column assertion selects rows by a column."), at + ".column", test.Source), IsColumn: true)
            : new ValueTarget(ConditionReader.RecordPath(y.Field ?? throw new FlowValidationException($"{test.Source}: {at} names no field; a condition on a field assertion selects records by a field."), at + ".field", test.Source), IsColumn: false);
        var condition = MapCondition(y, isColumn, at, test);
        if (condition.Operator == ValueOperator.Resolves)
        {
            throw new FlowValidationException($"{test.Source}: {at} selects by whether a reference resolves, which only an assertion can ask.");
        }

        return new ValueFilter(target, condition);
    }

    private static ValueCondition MapCondition(AssertionConditionYaml y, bool isColumn, string at, TestContext test)
        => ConditionReader.Read(
            OperatorsOf(y), y.IgnoreCase, y.Tolerance, isColumn ? "asks whether a reference resolves, and a column of bulk data holds no references." : null, at, test.Source);

    private static Quantifier MapQuantifier(string? declared, string at, string source)
    {
        var text = declared?.Trim().ToLowerInvariant();
        switch (text)
        {
            case null or "all":
                return Quantifier.All;
            case "any":
                return Quantifier.Any;
            case "none":
                return Quantifier.None;
        }

        if (text.EndsWith('%')
            && double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
            && percent is > 0 and <= 100)
        {
            return percent == 100 ? Quantifier.All : Quantifier.AtLeast(percent);
        }

        throw new FlowValidationException($"{source}: {at}.for '{declared}' is all, any, none, or a share such as 95%.");
    }

    private static void RequireBulk(string subject, string at, TestContext test)
    {
        if (!test.Bulk)
        {
            throw new FlowValidationException(
                $"{test.Source}: {at} is {Article(subject)} {subject} assertion, which reads each record's bulk data; give the test a bulk block (bulk: {{ columns: [...] }}).");
        }
    }

    /// <summary>A column (curve) of a record's bulk data as the DDMS names it; a comma would split the read's curve list.</summary>
    private static string CheckColumn(string? declared, string at, string source)
    {
        var column = declared?.Trim() ?? string.Empty;
        if (column.Length is 0 or > 256 || column.Contains(',', StringComparison.Ordinal) || column.Any(char.IsControl))
        {
            throw new FlowValidationException($"{source}: {at} '{Shown(column)}' is not a column name: 1 to 256 characters, without commas or control characters.");
        }

        return column;
    }

    /// <summary>A YAML value (scalars, mappings, sequences) as JSON, for what the document passes to the platform as written.</summary>
    private static JsonNode? ToJson(object? value, string at, string source) => value switch
    {
        null => null,
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        int or long or short or byte or sbyte or uint or ushort => JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        ulong u => JsonValue.Create(u),
        double d when double.IsFinite(d) => JsonValue.Create(d),
        float f when float.IsFinite(f) => JsonValue.Create(double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)),
        decimal m => JsonValue.Create(m),
        IDictionary map => ToJsonObject(map, at, source),
        IList list => new JsonArray(list.Cast<object?>().Select(item => ToJson(item, at, source)).ToArray()),
        _ => throw new FlowValidationException($"{source}: {at} holds a value that is not text, a number, true/false, a mapping or a list."),
    };

    private static JsonObject ToJsonObject(IDictionary map, string at, string source)
    {
        var node = new JsonObject();
        foreach (System.Collections.DictionaryEntry entry in map)
        {
            var key = entry.Key as string ?? throw new FlowValidationException($"{source}: {at} has a key that is not text.");
            node[key] = ToJson(entry.Value, $"{at}.{key}", source);
        }

        return node;
    }

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 3)] + "...";

    private static string Shown(string value) => value.Length > 80 ? value[..80] + "..." : value;
}
