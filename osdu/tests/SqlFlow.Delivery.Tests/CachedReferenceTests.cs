using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// OSDU record ids held in cached fields and written to relationships: the translations OSDU already models
/// (<c>ExternalUnitOfMeasure.UnitOfMeasureID</c>) cache the id of the platform record a source value stands for, and a
/// mapping writes it as the reference. The gate refuses values that are no reference or name the wrong kind of record, and
/// a render holds a reference to a record the cache says is not there. A record id is looked for by the id itself, never by
/// a captured field that shares its name.
/// </summary>
public sealed class CachedReferenceTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteOsdu _db = new();
    private readonly TestClock _clock = new();

    public void Dispose() => _db.Dispose();

    /// <summary>The partition's units, caching a field of their own called ID beside their code and name, as OSDU's do.</summary>
    private static ReferenceType Units(string metreName = "metre") => new("UnitOfMeasure", "reference-data--UnitOfMeasure",
    [
        ReferenceItem.FromText("dev:reference-data--UnitOfMeasure:m", new Dictionary<string, string> { ["Code"] = "m", ["Name"] = metreName, ["ID"] = "m" }),
        ReferenceItem.FromText("dev:reference-data--UnitOfMeasure:ft", new Dictionary<string, string> { ["Code"] = "ft", ["Name"] = "foot", ["ID"] = "ft" }),
    ]);

    /// <summary>
    /// The source's unit spellings as ExternalUnitOfMeasure records, each caching the id of the unit it stands for: with and
    /// without the version colon, one naming a unit the partition does not hold, one naming a record of another entity
    /// type, and one that is no id at all.
    /// </summary>
    private static ReferenceType Aliases(bool clean = false)
    {
        var rows = new List<(string Code, string Unit)>
        {
            ("M", "dev:reference-data--UnitOfMeasure:m:"),
            ("FT", "dev:reference-data--UnitOfMeasure:ft"),
        };
        if (!clean)
        {
            rows.Add(("GAPI", "dev:reference-data--UnitOfMeasure:gAPI:"));
            rows.Add(("HIGH", "dev:reference-data--LogCurveBusinessValue:High:"));
            rows.Add(("JUNK", "not an id"));
        }

        return new ReferenceType(
            "RecallUnitAliases",
            "reference-data--ExternalUnitOfMeasure",
            rows.Select(r => ReferenceItem.FromText(
                $"dev:reference-data--ExternalUnitOfMeasure:Recall.{r.Code}",
                new Dictionary<string, string> { ["Code"] = r.Code, ["UnitOfMeasureID"] = r.Unit })));
    }

    private static ReferenceSnapshot Cache(params ReferenceType[] types) => new("refs-1", T0, types);

    private const string ThroughAliases = """
          - target: osdu.data.Unit
            source: cache.RecallUnitAliases.UnitOfMeasureID
            findBy: cache.RecallUnitAliases.Code = dataset.unit
        """;

    private static MappingRenderer Renderer(ReferenceSnapshot cache, string entries = ThroughAliases)
        => new(TestSchema.Mapping(entries), TestSchema.Build(), cache, TestSchema.Context());

    private static SourceRecord Record(string unit) => new()
    {
        Row = SourceRow.FromStrings(new Dictionary<string, string?> { ["name"] = "well-1", ["depth"] = "1", ["unit"] = unit }),
        Scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase),
    };

    private static string? Unit(RenderResult result) => result.Document["data"]!["Unit"]?.GetValue<string>();

    [Fact]
    public void A_cached_field_holding_a_record_id_is_written_as_the_reference_with_its_version_colon()
    {
        var renderer = Renderer(Cache(Units(), Aliases()));

        var metre = renderer.Render(Record("M"));
        Assert.False(metre.IsHeld, string.Join("; ", metre.Holds));
        Assert.Equal("dev:reference-data--UnitOfMeasure:m:", Unit(metre));

        // What found the alias, what it gave, and the unit record the reference names are all dependencies of the record.
        Assert.Contains(metre.CacheUsages, u => u.TypeName == "RecallUnitAliases" && u.Kind == CacheUsageKind.Match && u.Path == "Code" && u.Value == "M");
        Assert.Contains(metre.CacheUsages, u => u.TypeName == "RecallUnitAliases" && u.Kind == CacheUsageKind.Value && u.Path == "UnitOfMeasureID");
        Assert.Contains(new CacheUsage("UnitOfMeasure", "dev:reference-data--UnitOfMeasure:m", "id", "dev:reference-data--UnitOfMeasure:m", CacheUsageKind.Match), metre.CacheUsages);

        // An id cached without the colon that separates a version is written with it, as a relationship takes it.
        Assert.Equal("dev:reference-data--UnitOfMeasure:ft:", Unit(renderer.Render(Record("FT"))));
    }

    [Fact]
    public void A_reference_to_a_record_the_cache_does_not_hold_or_to_no_record_holds_the_record_whatever_required_says()
    {
        const string Optional = """
              - target: osdu.data.Unit
                source: cache.RecallUnitAliases.UnitOfMeasureID
                findBy: cache.RecallUnitAliases.Code = dataset.unit
                required: false
            """;
        var renderer = Renderer(Cache(Units(), Aliases()), Optional);

        var missing = renderer.Render(Record("GAPI"));
        Assert.True(missing.IsHeld);
        Assert.Contains(
            "osdu.data.Unit: RecallUnitAliases 'dev:reference-data--ExternalUnitOfMeasure:Recall.GAPI' names dev:reference-data--UnitOfMeasure:gAPI at 'UnitOfMeasureID', and version refs-1 of the cache of partition 'dev' holds no such reference-data--UnitOfMeasure record in UnitOfMeasure, so the reference would point at nothing",
            Assert.Single(missing.Holds),
            StringComparison.Ordinal);

        var junk = renderer.Render(Record("JUNK"));
        Assert.True(junk.IsHeld);
        Assert.Contains("holds \"not an id\" at 'UnitOfMeasureID' in version refs-1 of the cache of partition 'dev', which is not an OSDU record id, and osdu.data.Unit takes a reference to a record", Assert.Single(junk.Holds), StringComparison.Ordinal);

        // A partition whose cache holds no units cannot say whether the unit exists, so the reference is written as cached.
        var unchecked_ = Renderer(Cache(Aliases()), Optional).Render(Record("GAPI"));
        Assert.False(unchecked_.IsHeld, string.Join("; ", unchecked_.Holds));
        Assert.Equal("dev:reference-data--UnitOfMeasure:gAPI:", Unit(unchecked_));
        Assert.DoesNotContain(unchecked_.CacheUsages, u => u.TypeName == "UnitOfMeasure");
    }

    [Fact]
    public void The_gate_refuses_values_that_are_no_reference_or_name_another_kind_of_record_and_lists_records_it_does_not_hold()
    {
        var schema = TestSchema.Build();
        IReadOnlyList<ValidationIssue> Check(ReferenceSnapshot cache)
            => Preflight.Check(TestSchema.Mapping(ThroughAliases), schema, cache, TestSchema.Context(), sourceColumns: null);

        var issues = Check(Cache(Units(), Aliases()));
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Target == "osdu.data.Unit" && i.Message.Contains(
            "writes 'UnitOfMeasureID' of RecallUnitAliases to osdu.data.Unit, which points to reference-data--UnitOfMeasure, and 1 of the 5 value(s) it holds in cache version 'refs-1' are not OSDU record ids: 'not an id'",
            StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Message.Contains(
            "1 of the ids it holds in cache version 'refs-1' name records of another entity type (reference-data--LogCurveBusinessValue): 'dev:reference-data--LogCurveBusinessValue:High:'",
            StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.Message.Contains(
            "1 of the ids it holds name records cache version 'refs-1' does not hold: 'dev:reference-data--UnitOfMeasure:gAPI:'",
            StringComparison.Ordinal));

        // Clean translations pass; without the units cached nothing says whether a unit exists, so nothing is listed.
        Assert.DoesNotContain(Check(Cache(Units(), Aliases(clean: true))), i => i.Target == "osdu.data.Unit");
        Assert.DoesNotContain(Check(Cache(Aliases(clean: true))), i => i.Target == "osdu.data.Unit");
    }

    [Fact]
    public void A_record_id_is_found_by_the_id_even_where_the_type_caches_a_field_called_id()
    {
        var cache = Cache(Units());

        // A static reference to a unit the cache holds is not refused because units cache an ID of their own.
        var staticIssues = Preflight.Check(
            TestSchema.Mapping("""  - { target: osdu.data.Unit, static: "dev:reference-data--UnitOfMeasure:m:" }"""),
            TestSchema.Build(), cache, TestSchema.Context(), sourceColumns: null);
        Assert.DoesNotContain(staticIssues, i => i.Severity == IssueSeverity.Error);
        var unknown = Preflight.Check(
            TestSchema.Mapping("""  - { target: osdu.data.Unit, static: "dev:reference-data--UnitOfMeasure:yd:" }"""),
            TestSchema.Build(), cache, TestSchema.Context(), sourceColumns: null);
        Assert.Contains(unknown, i => i.Severity == IssueSeverity.Error && i.Message.Contains("is not in cache version 'refs-1'", StringComparison.Ordinal));

        // A value that already is a unit's id names that unit, and the render records the unit it found.
        var byId = Renderer(cache, """
              - target: osdu.data.Unit
                source: cache.UnitOfMeasure.id
                findBy: cache.UnitOfMeasure.Code = dataset.unit
            """).Render(Record("dev:reference-data--UnitOfMeasure:ft:"));
        Assert.Equal("dev:reference-data--UnitOfMeasure:ft:", Unit(byId));
        Assert.Contains(byId.CacheUsages, u => u.TypeName == "UnitOfMeasure" && u.ItemId == "dev:reference-data--UnitOfMeasure:ft" && u.Kind == CacheUsageKind.Match && u.Path == "id");
    }

    [Fact]
    public async Task A_record_that_wrote_a_units_id_is_not_tagged_when_another_field_of_that_unit_changes()
    {
        var ledger = _db.Ledger(_clock);
        var renderer = Renderer(Cache(Units()), """
              - target: osdu.data.Unit
                source: cache.UnitOfMeasure.id
                findBy: cache.UnitOfMeasure.Code = dataset.unit
            """);
        var usages = renderer.Render(Record("m")).CacheUsages;
        Assert.Contains(new CacheUsage("UnitOfMeasure", "dev:reference-data--UnitOfMeasure:m", "id", "dev:reference-data--UnitOfMeasure:m", CacheUsageKind.Value), usages);
        await DeliveredAsync(ledger, 3, usages);
        var analyzer = new CacheImpactAnalyzer(ledger, _clock, NullLogger.Instance);

        // The unit's name moves; the id the records wrote does not, whatever the unit caches under ID.
        var renamed = await analyzer.AnalyzeAsync("dev", Units(), Units("meter"), CacheChangeMode.Approve, "v1", "v2");
        Assert.Equal((1, 0L), (renamed.ChangedItems, renamed.AffectedRecords));
        Assert.Empty(await ledger.ListTagsAsync("pending", 10, 0));

        // A version without the unit reaches them: what they found it by and the id they wrote are both gone.
        var gone = new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure", [Units().Items[1]]);
        var removed = await analyzer.AnalyzeAsync("dev", Units(), gone, CacheChangeMode.Approve, "v2", "v3");
        Assert.Equal(2, removed.Changes);
        var tags = await ledger.ListTagsAsync("pending", 10, 0);
        Assert.Equal(["Code", "id"], tags.Where(t => t.Change == "removed" && t.ItemId == "dev:reference-data--UnitOfMeasure:m").Select(t => t.Path).Order(StringComparer.Ordinal));
        Assert.All(tags, t => Assert.Equal(3, t.AffectedRecords));
    }

    [Fact]
    public void The_translation_example_in_the_documents_renders_in_a_fixture()
    {
        var key = DeliveryKey.Derive("test", ["w"]).Value.ToString("N");
        var mapping = TestSchema.Mapping(ThroughAliases, $$$"""
            fixtures:
              - name: a Recall unit spelling, translated by OSDU's own ExternalUnitOfMeasure records
                record: { name: w, depth: "1", unit: M }
                expected: |
                  {"id":"dev:work-product-component--Thing:{{{key}}}","kind":"test:wks:work-product-component--Thing:1.0.0","acl":{"owners":["owners@x"],"viewers":["viewers@x"]},"legal":{"legaltags":["tag"],"otherRelevantDataCountries":["NO"]},"data":{"Name":"w","Depth":1,"Unit":"dev:reference-data--UnitOfMeasure:m:"}}
            """);

        var issues = Preflight.Check(mapping, TestSchema.Build(), Cache(Units(), Aliases(clean: true)), TestSchema.Context(), sourceColumns: null);
        Assert.Empty(issues);
    }

    [Fact]
    public void A_cached_id_is_read_with_or_without_its_version_and_written_with_the_colon_a_relationship_takes()
    {
        Assert.Equal(new CachedReference("dev:reference-data--UnitOfMeasure:m", "reference-data--UnitOfMeasure"), CachedReferences.Parse(" dev:reference-data--UnitOfMeasure:m: "));
        Assert.Equal(new CachedReference("dev:reference-data--UnitOfMeasure:m", "reference-data--UnitOfMeasure"), CachedReferences.Parse("dev:reference-data--UnitOfMeasure:m:12"));
        Assert.Equal(new CachedReference("dev:reference-data--UnitOfMeasure:g%2Fcm3", "reference-data--UnitOfMeasure"), CachedReferences.Parse("dev:reference-data--UnitOfMeasure:g%2Fcm3"));
        Assert.Null(CachedReferences.Parse("m"));
        Assert.Null(CachedReferences.Parse("dev:UnitOfMeasure:m"));
        Assert.Null(CachedReferences.Parse(null));

        Assert.Equal("dev:reference-data--UnitOfMeasure:m:", CachedReferences.Written("dev:reference-data--UnitOfMeasure:m"));
        Assert.Equal("dev:reference-data--UnitOfMeasure:m:", CachedReferences.Written("dev:reference-data--UnitOfMeasure:m:"));
        Assert.Equal("dev:reference-data--UnitOfMeasure:m:12", CachedReferences.Written("dev:reference-data--UnitOfMeasure:m:12"));
    }

    /// <summary>Delivers <paramref name="count"/> records that were built from <paramref name="usages"/>.</summary>
    private async Task DeliveredAsync(OsduLedger ledger, int count, IReadOnlyList<CacheUsage> usages)
    {
        var flow = FlowId.Of("reference-flow");
        var submission = Guid.NewGuid();
        await ledger.RegisterSubmissionAsync(new SubmissionState
        {
            SubmissionId = submission,
            FlowId = flow,
            FlowName = "reference-flow",
            MappingReference = "Thing@1.0.0",
            RenderContext = "{}",
            RecordCount = count,
            Status = SubmissionStatus.Planned,
            ReceivedUtc = _clock.GetUtcNow().UtcDateTime,
        });

        var setId = await ledger.EnsureCacheSetAsync("dev", usages);
        var records = Enumerable.Range(0, count).Select(i => new RecordState
        {
            DeliveryKey = DeliveryKey.Derive("test", [$"unit-{i}"]),
            FlowId = flow,
            SourceKey = $"unit-{i}",
            MappingName = "Thing",
            TargetId = $"dev:x:unit-{i}",
            LastSubmissionId = submission,
            PendingDocumentRef = "0:0:10",
            PendingRenderContext = "{}",
            PendingMetadataHash = "mh",
            PendingMetadata = true,
            CacheSetId = setId,
        }).ToList();
        await ledger.UpsertPendingAsync(flow, records);
    }
}
