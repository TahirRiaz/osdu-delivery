using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Model;

/// <summary>
/// An assertion flow (<c>flowType: assertion</c>, docs/assertions-design.md): qualified tests of what an OSDU partition
/// holds, run after the flows that populate it. Each test reads one OSDU kind (through the search index, storage, or the
/// bulk data a DDMS keeps for its records) and holds what it reads to one or more assertions with a severity each; a run
/// records every test's outcome in the flow's ledger, so the state of a partition after a delivery is a report anyone can
/// open, and every earlier report stays comparable with it. A test reads; nothing is ever written to OSDU.
/// </summary>
public sealed record AssertionFlowDefinition
{
    public const string FlowTypeName = "assertion";

    /// <summary>The most tests one flow declares.</summary>
    public const int MaxTests = 500;

    /// <summary>Path of the file the flow was loaded from, for error messages. Null for inline documents.</summary>
    public string? SourcePath { get; init; }

    /// <summary>The flow's name: its pipeline identity, and the name its ledger is kept under.</summary>
    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>The platform batch the flow belongs to, from the envelope.</summary>
    public string? Batch { get; init; }

    /// <summary>Parameters the tests may use as <c>{name}</c> tokens in their queries, ids and expected text.</summary>
    public IReadOnlyDictionary<string, FlowParameter> Parameters { get; init; } = new Dictionary<string, FlowParameter>(StringComparer.Ordinal);

    /// <summary>The OSDU platform the tests read, and where its services are served.</summary>
    public required AssertionSource Source { get; init; }

    /// <summary>
    /// The partitions the flow tests (<c>partitions</c>, docs/partitions-design.md section 2), in document order; empty for a
    /// flow that names none, which tests the partition its <c>source.headers</c> name or every registered one.
    /// </summary>
    public IReadOnlyList<string> Partitions { get; init; } = [];

    /// <summary>
    /// The partition this definition is bound to (<see cref="ForPartition"/>): the one a run tests. Null for a flow whose
    /// partition is its header's, and for one that works in partitions before it is bound.
    /// </summary>
    public string? Partition { get; init; }

    /// <summary>True when the flow names the partitions it tests.</summary>
    public bool DeclaresPartitions => Partitions.Count > 0;

    /// <summary>True for a flow that names neither its partitions nor a <c>data-partition-id</c> header: it tests every registered partition.</summary>
    public bool FollowsRegistry { get; init; }

    /// <summary>True when the flow tests partitions, named or registered, rather than the one its header names.</summary>
    public bool Partitioned => DeclaresPartitions || FollowsRegistry;

    /// <summary>What a test takes when it says nothing itself.</summary>
    public AssertionDefaults Defaults { get; init; } = new();

    /// <summary>Which outcomes fail the platform run: an error-severity failure (the default), a warning too, or none.</summary>
    public FailRunOn FailRunOn { get; init; } = FailRunOn.Error;

    /// <summary>The tests, in the order the document declares them; names are unique within the flow.</summary>
    public required IReadOnlyList<AssertionTest> Tests { get; init; }

    public FlowReliability Reliability { get; init; } = new();

    /// <summary>
    /// The ledger identity the flow's results are kept under: derived from the name and the bound partition for a flow that
    /// works in partitions, so each partition keeps a report of its own, and from the name alone for a flow whose partition
    /// is its header's.
    /// </summary>
    /// <exception cref="InvalidOperationException">The flow works in partitions and is bound to none.</exception>
    [JsonIgnore]
    public Guid LedgerId => Partitioned
        ? Identity.FlowId.Of(Name, BoundPartition())
        : Identity.FlowId.Of(Name);

    /// <summary>The ledger's name as pages show it: <c>name@partition</c> for a flow that works in partitions, the name otherwise.</summary>
    [JsonIgnore]
    public string LedgerName => Partitioned ? $"{Name}@{BoundPartition()}" : Name;

    /// <summary>
    /// Whether settling the partition a run tests (<paramref name="requested"/>, or none) needs the registry: always for a
    /// flow that follows it, and for one that names several partitions when none is named.
    /// </summary>
    public bool NeedsRegistry(string? requested)
        => FollowsRegistry || (DeclaresPartitions && string.IsNullOrWhiteSpace(requested) && Partitions.Count > 1);

    /// <summary>The partitions the flow tests: those it names, every registered one for a flow that follows the registry, none for a header flow.</summary>
    public IReadOnlyList<string> Served(RegisteredPartitions registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return DeclaresPartitions ? Partitions : FollowsRegistry ? registry.Names : [];
    }

    /// <summary>
    /// This definition bound to <paramref name="partition"/>: its requests carry the partition as <c>data-partition-id</c>,
    /// its <c>{partition}</c> token is the partition, and its results are kept in the partition's ledger.
    /// </summary>
    /// <exception cref="DeliveryException">The flow's partition is its header's, or the flow does not test this partition.</exception>
    public AssertionFlowDefinition ForPartition(string partition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        var wanted = partition.Trim();
        if (!Partitioned)
        {
            throw new DeliveryException(
                $"Assertion flow '{Name}' names no partitions; it tests the partition its source.headers name, so it cannot be bound to '{wanted}'.");
        }

        var declared = DeclaresPartitions
            ? Partitions.FirstOrDefault(p => string.Equals(p, wanted, StringComparison.OrdinalIgnoreCase))
                ?? throw new DeliveryException($"Assertion flow '{Name}' does not test partition '{wanted}'; it names {PartitionNames.Listed(Partitions)}.")
            : CacheScope.IsPartitionId(wanted)
                ? wanted
                : throw new DeliveryException($"Assertion flow '{Name}' cannot test '{wanted}': a partition is a data-partition-id (letters, digits, underscore, hyphen and dot).");
        var headers = new Dictionary<string, string>(Source.Headers, StringComparer.OrdinalIgnoreCase)
        {
            [CacheScope.PartitionHeader] = declared,
        };
        return this with { Partition = declared, Source = Source with { Headers = headers } };
    }

    /// <summary>
    /// The flow as a run tests it (docs/partitions-design.md section 3): the partition the run names, which the flow has to
    /// serve; when it names none, the registry's default when the flow serves it, else the flow's only partition. A flow
    /// whose partition is its header's is tested as it is. A run tests one partition: <see cref="PartitionNames.Every"/> is
    /// refused, since a report describes one partition.
    /// </summary>
    /// <exception cref="DeliveryException">The partition cannot be settled, or is not one the flow tests.</exception>
    public AssertionFlowDefinition ForRun(string? requested, RegisteredPartitions registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var wanted = string.IsNullOrWhiteSpace(requested) ? null : requested.Trim();
        if (!Partitioned)
        {
            return wanted is null
                ? this
                : throw new DeliveryException(
                    $"Assertion flow '{Name}' names no partitions; it tests the partition its source.headers name, so a run cannot target '{wanted}'. Leave the partition out, or take the header out so the flow tests every registered partition.");
        }

        if (wanted == PartitionNames.Every)
        {
            throw new DeliveryException(
                $"Assertion flow '{Name}' tests one partition per run, since a report describes one partition; name the partition to test instead of '{PartitionNames.Every}'.");
        }

        if (wanted is not null)
        {
            return ForPartition(FollowsRegistry ? registry.Find(wanted) ?? throw new DeliveryException(registry.NotRegistered(wanted)) : wanted);
        }

        var served = Served(registry);
        if (registry.Default is { } fallback && served.Contains(fallback, StringComparer.OrdinalIgnoreCase))
        {
            return ForPartition(fallback);
        }

        if (DeclaresPartitions && Partitions.Count == 1)
        {
            return ForPartition(Partitions[0]);
        }

        throw new DeliveryException(FollowsRegistry && registry.All.Count == 0
            ? $"No partition is registered with the catalog, so assertion flow '{Name}' has none to test. Register one on the Partitions page or with 'sqlflow partition add <name>'."
            : $"Assertion flow '{Name}' tests {PartitionNames.Listed(served)}, and {(registry.Default is null ? "no partition is the default" : $"the default partition '{registry.Default}' is not one of them")}; name the partition this run tests.");
    }

    /// <summary>The test named <paramref name="name"/> (case-insensitively), or null.</summary>
    public AssertionTest? Test(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Tests.FirstOrDefault(t => string.Equals(t.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The tests a run runs: those <paramref name="names"/> names and those carrying one of <paramref name="tags"/>, in
    /// document order; every test when both are empty.
    /// </summary>
    /// <exception cref="DeliveryException">A name is not a test of the flow, or a tag is carried by none of its tests.</exception>
    public IReadOnlyList<AssertionTest> Select(IReadOnlyCollection<string> names, IReadOnlyCollection<string> tags)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(tags);
        if (names.Count == 0 && tags.Count == 0)
        {
            return Tests;
        }

        var unknown = names.Where(n => Test(n) is null).ToList();
        if (unknown.Count > 0)
        {
            throw new DeliveryException(
                $"Assertion flow '{Name}' has no test named {string.Join(", ", unknown.Select(n => $"'{n}'"))}; its tests are {Shown(Tests.Select(t => t.Name))}.");
        }

        var unused = tags.Where(tag => !Tests.Any(t => t.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))).ToList();
        if (unused.Count > 0)
        {
            var carried = Tests.SelectMany(t => t.Tags).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            throw new DeliveryException(
                $"No test of assertion flow '{Name}' carries the tag {string.Join(", ", unused.Select(n => $"'{n}'"))}; "
                + (carried.Count == 0 ? "its tests carry no tags." : $"its tests carry {Shown(carried)}."));
        }

        return Tests
            .Where(t => names.Contains(t.Name, StringComparer.OrdinalIgnoreCase) || t.Tags.Any(tag => tags.Contains(tag, StringComparer.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>The secret references the source declares, keyed by document path; references only, never values.</summary>
    public IEnumerable<KeyValuePair<string, string>> CredentialReferences()
    {
        if (Source.Auth.SecretRef is { } secret)
        {
            yield return new("source.auth.secretRef", secret);
        }

        if (Source.Auth.SecondarySecretRef is { } secondary)
        {
            yield return new("source.auth.secondarySecretRef", secondary);
        }

        if (IsReference(Source.Endpoint))
        {
            yield return new("source.endpoint", Source.Endpoint);
        }

        foreach (var (name, value) in Source.Headers.Where(kv => IsReference(kv.Value)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            yield return new($"source.headers.{name}", value);
        }

        if (Source.Auth.Token is { } token)
        {
            foreach (var (name, value) in token.Body.Where(kv => IsReference(kv.Value)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                yield return new($"source.auth.token.body.{name}", value);
            }
        }
    }

    private string BoundPartition() => Partition
        ?? throw new InvalidOperationException(
            DeclaresPartitions
                ? $"Assertion flow '{Name}' tests each of {PartitionNames.Listed(Partitions)}; bind it to the partition a run tests before asking for its ledger."
                : $"Assertion flow '{Name}' tests every registered partition; bind it to the partition a run tests before asking for its ledger.");

    private static bool IsReference(string value) => value.Contains("${", StringComparison.Ordinal);

    private static string Shown(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count <= 20 ? string.Join(", ", list) : string.Join(", ", list.Take(20)) + $" and {list.Count - 20} more";
    }
}

/// <summary>
/// The rule every name a run selects by keeps to (an assertion flow's tests and tags, a dimension flow's dimensions), so a
/// name travels in a URL, a payload and a report unchanged.
/// </summary>
public static partial class SelectableNames
{
    /// <summary>The longest name.</summary>
    public const int MaxLength = 100;

    /// <summary>The rule, as messages state it.</summary>
    public const string Rule = "a letter or digit followed by letters, digits, '.', '_' and '-', at most 100 characters";

    /// <summary>Whether <paramref name="name"/> keeps to <see cref="Rule"/>.</summary>
    public static bool IsName(string? name) => name is not null && Pattern().IsMatch(name);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

/// <summary>Where a flow's tests read the platform: its endpoint and credentials, and the path of each service.</summary>
public sealed record AssertionSource
{
    public const string DefaultQueryPath = "/api/search/v2/query";
    public const string DefaultSearchPath = "/api/search/v2/query_with_cursor";
    public const string DefaultRecordQueryPath = "/api/storage/v2/query/records";
    public const string DefaultLegalPath = "/api/legal/v1";

    /// <summary>The Wellbore DDMS's root under the platform, the ingress route it is deployed under (osdu/specs/wellbore-ddms).</summary>
    public const string DefaultDdmsRoot = "/api/os-wellbore-ddms";

    /// <summary>The platform base URL; ${env:NAME} and ${keyvault:NAME} references allowed.</summary>
    public required string Endpoint { get; init; }

    public TargetAuth Auth { get; init; } = new() { Type = TargetAuthType.None };

    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The offset search a count and a grouping ask (openapi search v2, POST /query).</summary>
    public string QueryPath { get; init; } = DefaultQueryPath;

    /// <summary>The cursor search records are paged through (openapi search v2, POST /query_with_cursor).</summary>
    public string SearchPath { get; init; } = DefaultSearchPath;

    /// <summary>The storage read of records by id (openapi storage v2, POST /query/records).</summary>
    public string RecordQueryPath { get; init; } = DefaultRecordQueryPath;

    /// <summary>The legal service's root (openapi legal v1), under which <c>/legaltags:validate</c> is asked.</summary>
    public string LegalPath { get; init; } = DefaultLegalPath;

    /// <summary>The Wellbore DDMS's root, under which <c>/ddms/v3/{collection}/{id}/data</c> reads a record's bulk data.</summary>
    public string DdmsRoot { get; init; } = DefaultDdmsRoot;
}

/// <summary>What a test takes when it says nothing itself.</summary>
public sealed record AssertionDefaults
{
    /// <summary>The most records a test reads unless it says otherwise.</summary>
    public const int DefaultMaxRecords = 10_000;

    /// <summary>The ceiling on what any test may read: a test reads records into bounded memory, never a partition whole.</summary>
    public const int MaxRecordsCeiling = 1_000_000;

    /// <summary>The failing examples an assertion names unless the flow says otherwise.</summary>
    public const int DefaultExamples = 20;

    /// <summary>The most failing examples an assertion names.</summary>
    public const int MaxExamples = 500;

    /// <summary>
    /// How long, in seconds, the search index is given to list a change before a test of the changed type is judged, unless
    /// the flow says otherwise: OSDU's indexer takes the change from a queue, which commonly lists it within a minute and,
    /// under a large delivery, within a few.
    /// </summary>
    public const int DefaultIndexSettleSeconds = 300;

    /// <summary>The longest settle window a flow or a test may give the index.</summary>
    public const int MaxIndexSettleSeconds = 3600;

    public int MaxRecords { get; init; } = DefaultMaxRecords;

    public int Examples { get; init; } = DefaultExamples;

    public AssertionRead Read { get; init; } = AssertionRead.Storage;

    /// <summary>
    /// The settle window a test takes unless it says otherwise (docs: osdu/docs/documents.md, Records the index may not list
    /// yet); 0 judges every test whatever changed.
    /// </summary>
    public int IndexSettleSeconds { get; init; } = DefaultIndexSettleSeconds;
}

/// <summary>Where a test reads the records its assertions look at.</summary>
public enum AssertionRead
{
    /// <summary>The records as storage holds them, read by the ids the search finds: every property, exactly as stored.</summary>
    Storage,

    /// <summary>The search hits themselves: only what the index holds of each record, but no storage read.</summary>
    Index,
}

/// <summary>Which outcomes fail the platform run of an assertion flow.</summary>
public enum FailRunOn
{
    /// <summary>A test that failed an error-severity assertion, or could not be evaluated.</summary>
    Error,

    /// <summary>As <see cref="Error"/>, and a test that failed a warning-severity assertion.</summary>
    Warning,

    /// <summary>Never: the run reports its tests and succeeds whenever it could run them.</summary>
    Never,
}

/// <summary>How much an assertion's failure matters. The order is the order of weight: an error outweighs a warning.</summary>
public enum AssertionSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>
/// One test of an assertion flow: what it reads from OSDU (one kind, found by a query or named by id, and optionally the
/// bulk data of each record) and the assertions it holds that to.
/// </summary>
public sealed record AssertionTest
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>Labels a run selects tests by, and the board groups them by.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>The partitions of the flow the test runs in, when not all of them; empty runs in every partition the flow tests.</summary>
    public IReadOnlyList<string> Partitions { get; init; } = [];

    /// <summary>The severity of the test's assertions that do not state one.</summary>
    public AssertionSeverity Severity { get; init; } = AssertionSeverity.Error;

    /// <summary>
    /// The one type the test reads, in one version (<c>authority:source:entityType:major.minor.patch</c>), as the mapping
    /// that delivers it names it in its <c>template.kind</c>.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>The saved template version the test's paths are checked against; null takes the newest saved for the kind.</summary>
    public string? Template { get; init; }

    /// <summary>A Lucene query narrowing the kind, with <c>{parameter}</c> and <c>{partition}</c> tokens; null matches the whole kind.</summary>
    public string? Query { get; init; }

    /// <summary>The records the test reads by id instead of by query, with tokens; empty for a test that searches.</summary>
    public IReadOnlyList<string> Ids { get; init; } = [];

    /// <summary>A spatial filter the search applies (openapi search v2, <c>spatialFilter</c>), as the document writes it.</summary>
    public JsonObject? Spatial { get; init; }

    /// <summary>The order the search returns records in, which an ordered record set compares against.</summary>
    public IReadOnlyList<AssertionSort> Sort { get; init; } = [];

    public AssertionRead Read { get; init; } = AssertionRead.Storage;

    /// <summary>The most records the test reads.</summary>
    public int MaxRecords { get; init; } = AssertionDefaults.DefaultMaxRecords;

    /// <summary>
    /// What a test does when more records match than <see cref="MaxRecords"/>: false (the default) says so and evaluates
    /// nothing, since an assertion over part of the records proves nothing of the rest; true evaluates the first
    /// <see cref="MaxRecords"/> and marks the result as a sample.
    /// </summary>
    public bool Sample { get; init; }

    /// <summary>
    /// How long, in seconds, the search index is given to list a change a delivery flow of this module made to records of the
    /// test's entity type before the test is judged: a run within that time of such a change skips the test, saying why,
    /// rather than judging what the index lists so far (docs: osdu/docs/documents.md, Records the index may not list yet).
    /// 0 judges it whatever changed. It says when the test is judged, not what it judges, so it stays out of
    /// <see cref="DefinitionHash"/>.
    /// </summary>
    [JsonIgnore]
    public int IndexSettleSeconds { get; init; } = AssertionDefaults.DefaultIndexSettleSeconds;

    /// <summary>The bulk data the test reads for each record, or null for a test that reads the records alone.</summary>
    public AssertionBulk? Bulk { get; init; }

    public required IReadOnlyList<TestAssertion> Assertions { get; init; }

    /// <summary>
    /// A hash of what the document says of the test, so a result can say whether the test has changed since it was
    /// recorded. Two documents stating the same test the same way hash the same, whatever their layout.
    /// </summary>
    public string DefinitionHash { get; init; } = string.Empty;

    /// <summary>True when the test reads records named by id rather than found by a search.</summary>
    public bool ByIds => Ids.Count > 0;

    /// <summary>Whether the test runs in <paramref name="partition"/>: every partition when it names none.</summary>
    public bool RunsIn(string? partition)
        => Partitions.Count == 0 || partition is null || Partitions.Contains(partition, StringComparer.OrdinalIgnoreCase);
}

/// <summary>One field the search sorts by.</summary>
public sealed record AssertionSort(string Field, bool Descending);

/// <summary>What a test reads of each record's bulk data, from the Wellbore DDMS collection its kind is served under.</summary>
public sealed record AssertionBulk
{
    /// <summary>The most rows read of one record's bulk data unless the test says otherwise.</summary>
    public const long DefaultMaxRows = 1_000_000;

    /// <summary>The ceiling on the rows read of one record's bulk data.</summary>
    public const long MaxRowsCeiling = 100_000_000;

    /// <summary>The collection the kind is served under (<c>welllogs</c>, <c>wellboretrajectories</c>).</summary>
    public required string Collection { get; init; }

    /// <summary>The columns (curves) read; empty reads the columns the assertions name, or every column when none does.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    public long MaxRows { get; init; } = DefaultMaxRows;
}

/// <summary>
/// One assertion of a test. Each names what it looks at and what it expects, with the severity its failure carries and a
/// label reports show it under.
/// </summary>
[JsonDerivedType(typeof(CountAssertion), "count")]
[JsonDerivedType(typeof(ValueAssertion), "value")]
[JsonDerivedType(typeof(AggregateAssertion), "aggregate")]
[JsonDerivedType(typeof(UniqueAssertion), "unique")]
[JsonDerivedType(typeof(GroupAssertion), "groupBy")]
[JsonDerivedType(typeof(RecordSetAssertion), "recordSet")]
[JsonDerivedType(typeof(ConformsAssertion), "conforms")]
[JsonDerivedType(typeof(IndexedAssertion), "indexed")]
[JsonDerivedType(typeof(LegalAssertion), "legal")]
[JsonDerivedType(typeof(DeliveredAssertion), "delivered")]
[JsonDerivedType(typeof(RowCountAssertion), "rowCount")]
[JsonDerivedType(typeof(ColumnsAssertion), "columns")]
[JsonDerivedType(typeof(MonotonicAssertion), "monotonic")]
public abstract record TestAssertion
{
    /// <summary>What reports call the assertion: the document's <c>name</c>, or a label read off what it asserts.</summary>
    public required string Label { get; init; }

    public string? Description { get; init; }

    public AssertionSeverity Severity { get; init; } = AssertionSeverity.Error;

    /// <summary>The assertion's type as documents and reports name it: count, field, aggregate, unique, groupBy, ...</summary>
    [JsonIgnore]
    public abstract string Type { get; }

    /// <summary>What the assertion expects, as reports quote it.</summary>
    [JsonIgnore]
    public abstract string Expected { get; }
}

/// <summary>How many records the test matches: the index's exact count, or the records its ids name that storage holds.</summary>
public sealed record CountAssertion(Comparison Comparison) : TestAssertion
{
    public override string Type => "count";

    public override string Expected => Comparison.ToString();
}

/// <summary>
/// A value every record (a field of the record) or every row of its bulk data (a column) holds: a condition on each value
/// the path yields, held for the share of records or rows <see cref="For"/> asks, optionally only of those
/// <see cref="Where"/> selects.
/// </summary>
public sealed record ValueAssertion : TestAssertion
{
    public required ValueTarget Target { get; init; }

    public required ValueCondition Condition { get; init; }

    /// <summary>The share of records (or rows) the condition has to hold for.</summary>
    public Quantifier For { get; init; } = Quantifier.All;

    /// <summary>
    /// For a path yielding several values in one record: true holds the record when one of them meets the condition, false
    /// (the default) only when every one does.
    /// </summary>
    public bool AnyValue { get; init; }

    /// <summary>True passes over a record (or row) that yields no value at all, instead of failing it.</summary>
    public bool Optional { get; init; }

    /// <summary>The conditions a record (or row) has to meet to be looked at: every one of them.</summary>
    public IReadOnlyList<ValueFilter> Where { get; init; } = [];

    public override string Type => Target.IsColumn ? "column" : "field";

    public override string Expected => Condition.ToString() + (For.Mode == QuantifierMode.All ? string.Empty : $" for {For}");
}

/// <summary>An aggregate of the values a field yields over every record, or a column over each record's rows, compared.</summary>
public sealed record AggregateAssertion : TestAssertion
{
    public required AggregateFunction Function { get; init; }

    public required ValueTarget Target { get; init; }

    public required Comparison Comparison { get; init; }

    /// <summary>How far the aggregate may be from a number it is compared with and still be equal to it, or within a range.</summary>
    public double? Tolerance { get; init; }

    public override string Type => "aggregate";

    public override string Expected => Comparison + (Tolerance is { } tolerance ? " (tolerance " + tolerance.ToString(CultureInfo.InvariantCulture) + ")" : string.Empty);
}

/// <summary>No two records carry the same values of the fields named, taken together.</summary>
public sealed record UniqueAssertion(IReadOnlyList<string> Fields) : TestAssertion
{
    public override string Type => "unique";

    public override string Expected => "no two records share " + string.Join(" + ", Fields);
}

/// <summary>
/// The records the test matches, grouped by the distinct values of one field (the search's <c>aggregateBy</c>): the groups
/// expected with their counts, the groups that must not be there, and how many groups there are.
/// </summary>
public sealed record GroupAssertion : TestAssertion
{
    public required string Field { get; init; }

    /// <summary>The groups expected, each with what its count has to be.</summary>
    public IReadOnlyList<ExpectedGroup> Groups { get; init; } = [];

    /// <summary>True when the groups expected are every group there is (<c>mode: exact</c>); false allows others.</summary>
    public bool Exact { get; init; }

    /// <summary>The groups that must not appear at all.</summary>
    public IReadOnlyList<string> Absent { get; init; } = [];

    /// <summary>What the number of groups has to be, or null.</summary>
    public Comparison? GroupCount { get; init; }

    public override string Type => "groupBy";

    public override string Expected
    {
        get
        {
            var parts = new List<string>();
            if (Groups.Count > 0)
            {
                parts.Add((Exact ? "exactly " : "includes ") + string.Join(", ", Groups.Select(g => $"{g.Key} {g.Count}")));
            }

            if (Absent.Count > 0)
            {
                parts.Add("none of " + string.Join(", ", Absent));
            }

            if (GroupCount is { } count)
            {
                parts.Add($"group count {count}");
            }

            return string.Join("; ", parts);
        }
    }
}

/// <summary>One group a grouping expects, with what its count has to be.</summary>
public sealed record ExpectedGroup(string Key, Comparison Count);

/// <summary>
/// The records the test reads, projected onto columns (fields), compared as a set of rows with the rows expected: the same
/// rows in any order, in order, including them, or excluding them (DeltaForge's RESULT SET, docs/assertions-design.md).
/// </summary>
public sealed record RecordSetAssertion : TestAssertion
{
    public required IReadOnlyList<string> Columns { get; init; }

    public required RecordSetMode Mode { get; init; }

    public required IReadOnlyList<IReadOnlyList<ExpectedValue?>> Rows { get; init; }

    public override string Type => "recordSet";

    public override string Expected => Mode switch
    {
        RecordSetMode.Exact => $"exactly {Rows.Count} row(s), in any order",
        RecordSetMode.Ordered => $"exactly {Rows.Count} row(s), in order",
        RecordSetMode.Includes => $"includes {Rows.Count} row(s)",
        _ => $"excludes {Rows.Count} row(s)",
    };
}

/// <summary>How a record set compares with the rows expected.</summary>
public enum RecordSetMode
{
    Exact,
    Ordered,
    Includes,
    Excludes,
}

/// <summary>Every record meets the schema of its kind: required properties, types, formats, patterns, enumerations.</summary>
public sealed record ConformsAssertion : TestAssertion
{
    public override string Type => "conforms";

    public override string Expected => "every record meets the template of its kind";
}

/// <summary>The search indexed every record the test matches cleanly: no record carries an index status other than 200.</summary>
public sealed record IndexedAssertion : TestAssertion
{
    public override string Type => "indexed";

    public override string Expected => "every record indexed with status 200";
}

/// <summary>Every legal tag the records carry is valid now (openapi legal v1, POST /legaltags:validate).</summary>
public sealed record LegalAssertion : TestAssertion
{
    public override string Type => "legal";

    public override string Expected => "every legal tag the records carry is valid";
}

/// <summary>
/// Every record a delivery flow's ledger holds as delivered in the partition is among the records the test matches; with
/// <see cref="Exact"/>, the test matches no record the ledger does not hold either.
/// </summary>
public sealed record DeliveredAssertion : TestAssertion
{
    /// <summary>The delivery flow whose ledger is read.</summary>
    public required string Flow { get; init; }

    /// <summary>The interface of that flow, for a source with interfaces; null for a flow in the single form.</summary>
    public string? Interface { get; init; }

    public bool Exact { get; init; }

    public override string Type => "delivered";

    public override string Expected => Exact
        ? $"exactly the records {Flow}{(Interface is null ? string.Empty : "/" + Interface)} delivered"
        : $"every record {Flow}{(Interface is null ? string.Empty : "/" + Interface)} delivered";
}

/// <summary>How many rows each record's bulk data holds.</summary>
public sealed record RowCountAssertion(Comparison Comparison) : TestAssertion
{
    public override string Type => "rowCount";

    public override string Expected => Comparison.ToString();
}

/// <summary>The columns (curves) each record's bulk data holds.</summary>
public sealed record ColumnsAssertion : TestAssertion
{
    public IReadOnlyList<string> Includes { get; init; } = [];

    public IReadOnlyList<string> Excludes { get; init; } = [];

    /// <summary>When not empty, the columns have to be exactly these, in any order.</summary>
    public IReadOnlyList<string> Exactly { get; init; } = [];

    public override string Type => "columns";

    public override string Expected
    {
        get
        {
            var parts = new List<string>();
            if (Exactly.Count > 0)
            {
                parts.Add("exactly " + string.Join(", ", Exactly));
            }

            if (Includes.Count > 0)
            {
                parts.Add("includes " + string.Join(", ", Includes));
            }

            if (Excludes.Count > 0)
            {
                parts.Add("excludes " + string.Join(", ", Excludes));
            }

            return string.Join("; ", parts);
        }
    }
}

/// <summary>A column's values rise (or fall) row after row in each record's bulk data, as an index curve does.</summary>
public sealed record MonotonicAssertion(string Column, MonotonicDirection Direction) : TestAssertion
{
    public override string Type => "monotonic";

    public override string Expected => $"{Column} {AssertionText.Of(Direction)}";
}

public enum MonotonicDirection
{
    Increasing,
    Decreasing,
    StrictlyIncreasing,
    StrictlyDecreasing,
}

/// <summary>An aggregate over the values of a field or a column.</summary>
public enum AggregateFunction
{
    Min,
    Max,
    Sum,
    Avg,

    /// <summary>The values there are.</summary>
    Count,

    /// <summary>The distinct values there are.</summary>
    Distinct,

    /// <summary>The records (or rows) that yield no value.</summary>
    Missing,
}

/// <summary>What a value assertion or an aggregate reads: a path into each record, or a column of each record's bulk data.</summary>
public sealed record ValueTarget(string Path, bool IsColumn)
{
    public override string ToString() => Path;
}

/// <summary>A condition a record (or row) has to meet to be looked at by an assertion.</summary>
public sealed record ValueFilter(ValueTarget Target, ValueCondition Condition)
{
    public override string ToString() => $"{Target} {Condition}";
}

/// <summary>What a condition compares a value with.</summary>
public enum ValueOperator
{
    EqualTo,
    NotEqualTo,
    In,
    NotIn,
    AtLeast,
    AtMost,
    GreaterThan,
    LessThan,
    Between,
    Matches,
    NotMatches,
    StartsWith,
    EndsWith,
    Contains,
    NotContains,
    Exists,
    Empty,
    Type,
    Length,
    Resolves,
}

/// <summary>The JSON type a <c>type</c> condition asks a value to be, which documents name by JSON Schema's words.</summary>
public enum JsonValueType
{
    /// <summary>string</summary>
    Text,

    /// <summary>number: any number, whole or not.</summary>
    Number,

    /// <summary>integer: a number without a fractional part.</summary>
    WholeNumber,

    /// <summary>boolean</summary>
    Boolean,

    /// <summary>object</summary>
    Mapping,

    /// <summary>array</summary>
    Array,

    /// <summary>null</summary>
    Null,
}

/// <summary>
/// One condition on a value: an operator and what it compares with. Text compares ordinally unless
/// <see cref="IgnoreCase"/>; numbers compare as numbers, within <see cref="Tolerance"/> for equality and ranges; dates written
/// in ISO 8601 compare as instants.
/// </summary>
public sealed record ValueCondition
{
    public required ValueOperator Operator { get; init; }

    /// <summary>The values compared with: one for most operators, two for <see cref="ValueOperator.Between"/>, a list for In and NotIn.</summary>
    public IReadOnlyList<ExpectedValue> Operands { get; init; } = [];

    /// <summary>The expression of Matches and NotMatches, compiled with a timeout.</summary>
    [JsonIgnore]
    public Regex? Pattern { get; init; }

    /// <summary>What a string's or an array's length has to be (<see cref="ValueOperator.Length"/>).</summary>
    public Comparison? Length { get; init; }

    /// <summary>For Exists and Empty: whether the value has to be there (or empty), or not.</summary>
    public bool Flag { get; init; } = true;

    /// <summary>For <see cref="ValueOperator.Type"/>: the JSON type the value has to be.</summary>
    public JsonValueType? JsonType { get; init; }

    /// <summary>For <see cref="ValueOperator.Resolves"/>: the entity type the reference has to point at, or null for any.</summary>
    public string? EntityType { get; init; }

    public bool IgnoreCase { get; init; }

    /// <summary>How far a number may be from the value compared with and still be equal to it, or within a range.</summary>
    public double? Tolerance { get; init; }

    public override string ToString()
    {
        var text = Operator switch
        {
            ValueOperator.Between => $"between {Operands[0]} and {Operands[1]}",
            ValueOperator.In => "in [" + string.Join(", ", Operands) + "]",
            ValueOperator.NotIn => "not in [" + string.Join(", ", Operands) + "]",
            ValueOperator.Exists => Flag ? "exists" : "does not exist",
            ValueOperator.Empty => Flag ? "is empty" : "is not empty",
            ValueOperator.Type => "is of type " + AssertionText.Of(JsonType ?? JsonValueType.Text),
            ValueOperator.Length => $"has length {Length}",
            ValueOperator.Resolves => EntityType is null ? "resolves to a record" : $"resolves to a {EntityType}",
            _ => AssertionText.Of(Operator) + " " + (Operands.Count > 0 ? Operands[0].ToString() : string.Empty),
        };
        if (Tolerance is { } tolerance)
        {
            text += " (tolerance " + tolerance.ToString(CultureInfo.InvariantCulture) + ")";
        }

        return IgnoreCase ? text + " ignoring case" : text;
    }
}

/// <summary>A value an assertion expects, as the document writes it: text, a number, a boolean, or null.</summary>
public sealed record ExpectedValue
{
    private ExpectedValue(ExpectedValueKind kind, string text, double? number, bool? boolean)
    {
        Kind = kind;
        Text = text;
        Number = number;
        Boolean = boolean;
    }

    public ExpectedValueKind Kind { get; }

    /// <summary>The value as text: the text itself, a number in invariant form, true or false, or null.</summary>
    public string Text { get; }

    public double? Number { get; }

    public bool? Boolean { get; }

    public static ExpectedValue Null { get; } = new(ExpectedValueKind.Null, "null", null, null);

    public static ExpectedValue OfText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new(ExpectedValueKind.Text, text, null, null);
    }

    public static ExpectedValue OfNumber(double number)
        => OfNumber(number, number.ToString("R", CultureInfo.InvariantCulture));

    /// <summary>
    /// A number as the document wrote it: <paramref name="text"/> keeps it exactly (an integer beyond what a double holds,
    /// a decimal a double only approximates), and equality compares by it; <paramref name="number"/> orders it.
    /// </summary>
    public static ExpectedValue OfNumber(double number, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!double.IsFinite(number))
        {
            throw new ArgumentOutOfRangeException(nameof(number), number, "An expected number is finite.");
        }

        return new(ExpectedValueKind.Number, text, number, null);
    }

    public static ExpectedValue OfBoolean(bool value) => new(ExpectedValueKind.Boolean, value ? "true" : "false", null, value);

    /// <summary>This value with <paramref name="substitute"/> applied to its text, when it is text.</summary>
    public ExpectedValue WithText(Func<string, string> substitute)
    {
        ArgumentNullException.ThrowIfNull(substitute);
        return Kind == ExpectedValueKind.Text ? OfText(substitute(Text)) : this;
    }

    public override string ToString() => Kind == ExpectedValueKind.Text ? "\"" + Text + "\"" : Text;
}

public enum ExpectedValueKind
{
    Text,
    Number,
    Boolean,
    Null,
}

/// <summary>
/// What a number (a count, an aggregate, a length) has to be: one or more terms, every one of which has to hold, such as
/// <c>atLeast: 5</c> together with <c>atMost: 10</c>.
/// </summary>
public sealed record Comparison(IReadOnlyList<ComparisonTerm> Terms)
{
    public override string ToString() => string.Join(" and ", Terms.Select(t => t.ToString()));
}

/// <summary>One term of a comparison.</summary>
public sealed record ComparisonTerm(ComparisonOperator Operator, ExpectedValue Value, ExpectedValue? Upper = null)
{
    public override string ToString() => Operator == ComparisonOperator.Between
        ? $"between {Value} and {Upper}"
        : AssertionText.Of(Operator) + " " + Value;
}

public enum ComparisonOperator
{
    EqualTo,
    NotEqualTo,
    AtLeast,
    AtMost,
    GreaterThan,
    LessThan,
    Between,
}

/// <summary>The share of records (or rows) an assertion has to hold for: all of them, at least one, none, or a percentage.</summary>
public sealed record Quantifier(QuantifierMode Mode, double Share)
{
    public static Quantifier All { get; } = new(QuantifierMode.All, 100);

    public static Quantifier Any { get; } = new(QuantifierMode.Any, 0);

    public static Quantifier None { get; } = new(QuantifierMode.None, 0);

    public static Quantifier AtLeast(double percent) => new(QuantifierMode.Share, percent);

    public override string ToString() => Mode switch
    {
        QuantifierMode.All => "all",
        QuantifierMode.Any => "any",
        QuantifierMode.None => "none",
        _ => Share.ToString("0.###", CultureInfo.InvariantCulture) + "%",
    };
}

public enum QuantifierMode
{
    All,
    Any,
    None,
    Share,
}

/// <summary>The words documents and reports use for the operators, directions and types.</summary>
public static class AssertionText
{
    public static string Of(ValueOperator op) => op switch
    {
        ValueOperator.EqualTo => "equals",
        ValueOperator.NotEqualTo => "notEquals",
        ValueOperator.In => "in",
        ValueOperator.NotIn => "notIn",
        ValueOperator.AtLeast => "atLeast",
        ValueOperator.AtMost => "atMost",
        ValueOperator.GreaterThan => "greaterThan",
        ValueOperator.LessThan => "lessThan",
        ValueOperator.Between => "between",
        ValueOperator.Matches => "matches",
        ValueOperator.NotMatches => "notMatches",
        ValueOperator.StartsWith => "startsWith",
        ValueOperator.EndsWith => "endsWith",
        ValueOperator.Contains => "contains",
        ValueOperator.NotContains => "notContains",
        ValueOperator.Exists => "exists",
        ValueOperator.Empty => "empty",
        ValueOperator.Type => "type",
        ValueOperator.Length => "length",
        _ => "resolves",
    };

    public static string Of(ComparisonOperator op) => op switch
    {
        ComparisonOperator.EqualTo => "equals",
        ComparisonOperator.NotEqualTo => "notEquals",
        ComparisonOperator.AtLeast => "atLeast",
        ComparisonOperator.AtMost => "atMost",
        ComparisonOperator.GreaterThan => "greaterThan",
        ComparisonOperator.LessThan => "lessThan",
        _ => "between",
    };

    public static string Of(MonotonicDirection direction) => direction switch
    {
        MonotonicDirection.Increasing => "increasing",
        MonotonicDirection.Decreasing => "decreasing",
        MonotonicDirection.StrictlyIncreasing => "strictlyIncreasing",
        _ => "strictlyDecreasing",
    };

    public static string Of(JsonValueType type) => type switch
    {
        JsonValueType.Text => "string",
        JsonValueType.Number => "number",
        JsonValueType.WholeNumber => "integer",
        JsonValueType.Boolean => "boolean",
        JsonValueType.Mapping => "object",
        JsonValueType.Array => "array",
        _ => "null",
    };

    public static string Of(AggregateFunction function) => function switch
    {
        AggregateFunction.Min => "min",
        AggregateFunction.Max => "max",
        AggregateFunction.Sum => "sum",
        AggregateFunction.Avg => "avg",
        AggregateFunction.Count => "count",
        AggregateFunction.Distinct => "distinct",
        _ => "missing",
    };

    public static string Of(AssertionSeverity severity) => severity switch
    {
        AssertionSeverity.Error => "error",
        AssertionSeverity.Warning => "warning",
        _ => "info",
    };

    public static string Of(RecordSetMode mode) => mode switch
    {
        RecordSetMode.Exact => "exact",
        RecordSetMode.Ordered => "ordered",
        RecordSetMode.Includes => "includes",
        _ => "excludes",
    };
}
