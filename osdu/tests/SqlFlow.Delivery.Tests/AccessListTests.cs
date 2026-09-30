using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A record's viewers widened by the access groups a data office maintains per field and per country: a lookup finds the
/// row's wellbore once in the cache, its field and country ids key a <c>$findAll</c> over the cached access group map, and
/// the access list is the flow's own group followed by every group those give, each once. Missing data never holds the
/// record: it goes out with what was found, and what was looked for is recorded so a later cache version reaches it.
/// </summary>
public sealed class AccessListTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    internal const string Norway = "dev:master-data--GeoPoliticalEntity:Norway:";
    internal const string UnitedKingdom = "dev:master-data--GeoPoliticalEntity:UnitedKingdom:";
    internal const string MartinLinge = "dev:master-data--Field:MARTINLINGE:";
    internal const string Alba = "dev:master-data--Field:ALBA:";

    /// <summary>
    /// The access list and WellboreID of a Recall-style well log: the flow's group, the groups of the wellbore's field, and
    /// the country groups of its country, which are the rows naming the country and no field.
    /// </summary>
    internal const string Access = """
        acl:
          owners: [owners@x]
          viewers:
            - viewers@x
            - $cache: AccessGroupMap.EntitlementGroupEmail
              $findAll: FieldIDList = $lookup.wellbore.GeoContexts.FieldID
              $required: false
            - $cache: AccessGroupMap.EntitlementGroupEmail
              $findAll:
                - GeoPoliticalEntityID = $lookup.wellbore.GeoContexts.GeoPoliticalEntityID
                - FieldList is empty
                - FieldIDList is empty
              $required: false
        legal:
          legaltags: [tag]
          otherRelevantDataCountries: [NO]
        """;

    internal const string Lookups = """
        lookups:
          wellbore:
            $cache: Wellbore
            $findBy:
              - FacilityName = wellbore_uwi
              - NameAliases.AliasName = wellbore_uwi
            $description: The wellbore the log belongs to.
        """;

    /// <summary>A mapping document over the test template with <paramref name="lookups"/>, the record <paramref name="record"/> and <paramref name="data"/>.</summary>
    internal static string Document(string record = Access, string lookups = Lookups, string data = "WellboreID: { $lookup: wellbore.id }", string fixtures = "")
        => $"""
            documentType: mapping
            name: Thing
            version: 1.0.0
            template:
              kind: {TestSchema.Kind}
              version: {TestSchema.Build().Version}
            dataset:
              system: test
              key: [name]
            parameters:
              dataPartition: {"{"} required: true {"}"}

            """ + lookups + "\nrecord:\n" + TestSchema.Indented(record, 2) + "  data:\n"
            + TestSchema.Indented(TestSchema.BaseData, 4) + TestSchema.Indented(data, 4) + "\n" + fixtures + "\n";

    internal static MappingDefinition Mapping(string record = Access, string lookups = Lookups, string data = "WellboreID: { $lookup: wellbore.id }", string fixtures = "")
        => new DeliveryDocumentLoader().ParseMapping(Document(record, lookups, data, fixtures), "thing.yaml");

    internal static ReferenceItem Item(string id, params (string Field, string Json)[] fields)
        => new(id, fields.ToDictionary(f => f.Field, f => ReferenceValue.From(JsonNode.Parse(f.Json)!), StringComparer.OrdinalIgnoreCase));

    internal static ReferenceType Wellbores(params ReferenceItem[] extra) => new("Wellbore", "master-data--Wellbore",
    [
        Item("dev:master-data--Wellbore:ml1",
            ("FacilityName", "\"NO 16/3-A-1\""),
            ("GeoContexts.FieldID", $"[\"{MartinLinge}\"]"),
            ("GeoContexts.GeoPoliticalEntityID", $"[\"{Norway}\"]")),
        Item("dev:master-data--Wellbore:uk1",
            ("FacilityName", "\"UK 9/8-A1\""),
            ("NameAliases.AliasName", "[\"ALBA A1\"]"),
            ("GeoContexts.FieldID", $"[\"{Alba}\"]"),
            ("GeoContexts.GeoPoliticalEntityID", $"[\"{UnitedKingdom}\"]")),
        Item("dev:master-data--Wellbore:nofield",
            ("FacilityName", "\"NO 1/1-X\""),
            ("GeoContexts.GeoPoliticalEntityID", $"[\"{Norway}\"]")),
        .. extra,
    ]);

    internal static ReferenceType Groups(params ReferenceItem[] extra) => new("AccessGroupMap", "data-governance--AccessGroupMap",
    [
        // Norway's two country groups: the country, and no field.
        Item("dev:data-governance--AccessGroupMap:no-contractors",
            ("GeoPoliticalEntityID", $"\"{Norway}\""), ("GeoPoliticalEntityName", "\"Norway\""),
            ("EntitlementGroupEmail", "\"data.office.norway.contractors.viewers@x\"")),
        Item("dev:data-governance--AccessGroupMap:no-permanent",
            ("GeoPoliticalEntityID", $"\"{Norway}\""), ("GeoPoliticalEntityName", "\"Norway\""),
            ("EntitlementGroupEmail", "\"data.office.norway.viewers@x\"")),
        // Martin Linge's field group names its field without the version separator, and its country too, but it is a field
        // group: it names a field.
        Item("dev:data-governance--AccessGroupMap:martin-linge",
            ("GeoPoliticalEntityID", $"\"{Norway}\""), ("GeoPoliticalEntityName", "\"Norway\""),
            ("FieldList", "[\"MARTIN LINGE\"]"), ("FieldIDList", $"[\"{MartinLinge.TrimEnd(':')}\"]"),
            ("EntitlementGroupEmail", "\"data.office.martin.linge.viewers@x\"")),
        // The United Kingdom's group is both its country group and the field group of Alba, written in two cases.
        Item("dev:data-governance--AccessGroupMap:uk-country",
            ("GeoPoliticalEntityID", $"\"{UnitedKingdom}\""), ("GeoPoliticalEntityName", "\"United Kingdom\""),
            ("EntitlementGroupEmail", "\"data.office.united.kingdom.viewers@x\"")),
        Item("dev:data-governance--AccessGroupMap:uk-fields",
            ("GeoPoliticalEntityID", $"\"{UnitedKingdom}\""), ("GeoPoliticalEntityName", "\"United Kingdom\""),
            ("FieldList", "[\"ALBA\", \"BRESSAY\"]"), ("FieldIDList", $"[\"{Alba}\", \"dev:master-data--Field:BRESSAY:\"]"),
            ("EntitlementGroupEmail", "\"DATA.OFFICE.UNITED.KINGDOM.VIEWERS@x\"")),
        .. extra,
    ]);

    internal static ReferenceSnapshot Cache(ReferenceType? wellbores = null, ReferenceType? groups = null)
        => new("refs-1", T0, [.. TestSchema.References().Types.Where(t => t.Name != "Wellbore"), wellbores ?? Wellbores(), groups ?? Groups()]);

    internal static MappingRenderer Renderer(MappingDefinition? mapping = null, ReferenceSnapshot? cache = null)
        => new(mapping ?? Mapping(), TestSchema.Build(), cache ?? Cache(), TestSchema.Context());

    internal static SourceRecord Record(string? uwi) => new()
    {
        Row = SourceRow.FromStrings(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["name"] = "log-1", ["depth"] = "1", ["wellbore_uwi"] = uwi }),
        Scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase),
    };

    internal static IReadOnlyList<string> Viewers(RenderResult result)
        => result.Document["acl"]!["viewers"]!.AsArray().Select(v => v!.GetValue<string>()).ToList();

    [Fact]
    public void A_lookup_is_read_where_the_record_needs_it_and_findAll_and_a_list_of_values_are_entries_of_their_own()
    {
        var mapping = Mapping();

        var lookup = Assert.Single(mapping.Lookups.Values);
        Assert.Equal("wellbore", lookup.Name);
        Assert.Equal("Wellbore", lookup.CacheType);
        Assert.Equal(["FacilityName", "NameAliases.AliasName"], lookup.FindBy.Select(f => f.Field));
        Assert.Equal("The wellbore the log belongs to.", lookup.Description);

        // $lookup is the lookup's cache node: its type, its findBy lines, the field it names.
        var wellbore = mapping.Entries.Single(e => e.Target.Text == "osdu.data.WellboreID");
        Assert.Equal(MappingSourceKind.Cache, wellbore.Source!.Kind);
        Assert.Equal("Wellbore", wellbore.Source.CacheType);
        Assert.Equal("id", wellbore.Source.CacheField);
        Assert.Equal("wellbore", wellbore.Source.Lookup);
        Assert.Equal("lookup.wellbore.id", wellbore.Source.ToString());
        Assert.Same(lookup.FindBy, wellbore.FindBy);

        // The viewers are a list of values: the literal every record carries, then two nodes reading every matching row.
        var viewers = mapping.Entries.Single(e => e.Target.Text == "osdu.acl.viewers");
        Assert.True(viewers.IsList);
        Assert.Equal(3, viewers.Parts.Count);
        Assert.Equal(["viewers@x"], viewers.Static!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Equal(["viewers@x"], mapping.Envelope.Viewers);
        Assert.Equal(3, viewers.ValueNodes.Count());

        var field = viewers.Parts[1].FindAll!;
        Assert.Equal("AccessGroupMap", field.Type);
        Assert.Equal("FieldIDList", field.Field);
        Assert.Same(lookup, field.Operand.Lookup);
        Assert.Equal("GeoContexts.FieldID", field.Operand.LookupPath);
        Assert.Empty(field.Empty);
        Assert.Equal("record.acl.viewers[1]", viewers.Parts[1].Location);

        var country = viewers.Parts[2].FindAll!;
        Assert.Equal("GeoPoliticalEntityID", country.Field);
        Assert.Equal(["FieldList", "FieldIDList"], country.Empty);
        Assert.Equal("GeoPoliticalEntityID = $lookup.wellbore.GeoContexts.GeoPoliticalEntityID and FieldList is empty and FieldIDList is empty", country.ToString());

        // What the mapping reads through the lookup counts wherever it is read: its column, and both types.
        Assert.Equal(["dataset.wellbore_uwi"], viewers.Columns.Select(c => c.ToString()).Distinct());
        Assert.Equal(["AccessGroupMap", "Wellbore"], mapping.CacheTypesRead().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_viewers_are_the_flow_s_group_then_the_field_s_groups_then_the_country_s_groups_each_once()
    {
        var renderer = Renderer();

        var linge = renderer.Render(Record("NO 16/3-A-1"));
        Assert.False(linge.IsHeld, string.Join("; ", linge.Holds));
        Assert.Equal("dev:master-data--Wellbore:ml1:", linge.Document["data"]!["WellboreID"]!.GetValue<string>());

        // The field group names the field without the version separator the wellbore's reference carries: one record.
        // Both Norwegian country groups are read, and the Martin Linge row, which names a field, is not one of them.
        Assert.Equal(
            ["viewers@x", "data.office.martin.linge.viewers@x", "data.office.norway.contractors.viewers@x", "data.office.norway.viewers@x"],
            Viewers(linge));

        // A group given as the field's and as the country's, in two cases, is one member of the list.
        var alba = renderer.Render(Record("UK 9/8-A1"));
        Assert.Equal(["viewers@x", "DATA.OFFICE.UNITED.KINGDOM.VIEWERS@x"], Viewers(alba));

        // The wellbore is found by an alias when no wellbore carries the name.
        Assert.Equal(Viewers(alba), Viewers(renderer.Render(Record("ALBA A1"))));
    }

    [Fact]
    public void A_wellbore_in_no_field_takes_its_country_s_groups_and_records_the_field_it_was_built_without()
    {
        var result = Renderer().Render(Record("NO 1/1-X"));

        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        Assert.Equal(["viewers@x", "data.office.norway.contractors.viewers@x", "data.office.norway.viewers@x"], Viewers(result));
        Assert.Contains(new CacheUsage("Wellbore", "dev:master-data--Wellbore:nofield", "GeoContexts.FieldID", string.Empty, CacheUsageKind.Empty), result.CacheUsages);
    }

    [Fact]
    public void A_wellbore_the_cache_does_not_hold_holds_the_record_that_needs_its_id_and_leaves_the_viewers_at_the_flow_s_group()
    {
        var result = Renderer().Render(Record("NO 99/9-Z-9"));

        Assert.True(result.IsHeld);
        Assert.Contains(result.Holds, h => h.StartsWith("osdu.data.WellboreID: no Wellbore matches 'NO 99/9-Z-9' by FacilityName/NameAliases.AliasName", StringComparison.Ordinal));
        Assert.Equal(["viewers@x"], Viewers(result));

        // Each value no wellbore answered to is recorded, so a capture that brings the wellbore in reaches the record.
        Assert.Contains(new CacheUsage("Wellbore", "NO 99/9-Z-9", "FacilityName", "NO 99/9-Z-9", CacheUsageKind.Unlisted), result.CacheUsages);
        Assert.Contains(new CacheUsage("Wellbore", "NO 99/9-Z-9", "NameAliases.AliasName", "NO 99/9-Z-9", CacheUsageKind.Unlisted), result.CacheUsages);

        // Where the id is optional too, nothing holds: the record goes out with the flow's group alone.
        var optional = Renderer(Mapping(data: "WellboreID: { $lookup: wellbore.id, $required: false }")).Render(Record("NO 99/9-Z-9"));
        Assert.False(optional.IsHeld, string.Join("; ", optional.Holds));
        Assert.Equal(["viewers@x"], Viewers(optional));
    }

    [Fact]
    public void Every_key_is_recorded_with_the_rows_it_found_none_included_and_every_field_a_row_was_judged_by()
    {
        var linge = Renderer().Render(Record("NO 16/3-A-1"));

        // The field id is asked for as the wellbore writes it and without its version separator.
        Assert.Contains(CacheUsage.Listing("AccessGroupMap", "FieldIDList", MartinLinge, []), linge.CacheUsages);
        Assert.Contains(CacheUsage.Listing("AccessGroupMap", "FieldIDList", MartinLinge.TrimEnd(':'), ["dev:data-governance--AccessGroupMap:martin-linge"]), linge.CacheUsages);

        // The country key found three rows; two passed as having no field, one did not, and each is recorded as judged.
        Assert.Contains(
            CacheUsage.Listing("AccessGroupMap", "GeoPoliticalEntityID", Norway,
                ["dev:data-governance--AccessGroupMap:martin-linge", "dev:data-governance--AccessGroupMap:no-contractors", "dev:data-governance--AccessGroupMap:no-permanent"]),
            linge.CacheUsages);
        Assert.Contains(new CacheUsage("AccessGroupMap", "dev:data-governance--AccessGroupMap:no-permanent", "FieldList", string.Empty, CacheUsageKind.Empty), linge.CacheUsages);
        Assert.Contains(new CacheUsage("AccessGroupMap", "dev:data-governance--AccessGroupMap:no-permanent", "FieldIDList", string.Empty, CacheUsageKind.Empty), linge.CacheUsages);
        Assert.Contains(new CacheUsage("AccessGroupMap", "dev:data-governance--AccessGroupMap:martin-linge", "FieldList", "MARTIN LINGE", CacheUsageKind.Value), linge.CacheUsages);
        Assert.Contains(new CacheUsage("AccessGroupMap", "dev:data-governance--AccessGroupMap:no-permanent", "EntitlementGroupEmail", "data.office.norway.viewers@x", CacheUsageKind.Value), linge.CacheUsages);

        // A field no access group lists yet is recorded as listing none, which is how a record built without its group is
        // found once the data office lists one.
        var newField = Renderer(cache: Cache(Wellbores(Item("dev:master-data--Wellbore:new1",
            ("FacilityName", "\"NO 2/2-N\""),
            ("GeoContexts.FieldID", "[\"dev:master-data--Field:NEW:\"]"))))).Render(Record("NO 2/2-N"));
        Assert.False(newField.IsHeld, string.Join("; ", newField.Holds));
        Assert.Equal(["viewers@x"], Viewers(newField));
        Assert.Contains(CacheUsage.Listing("AccessGroupMap", "FieldIDList", "dev:master-data--Field:NEW:", []), newField.CacheUsages);
        Assert.Contains(CacheUsage.Listing("AccessGroupMap", "FieldIDList", "dev:master-data--Field:NEW", []), newField.CacheUsages);
    }

    [Fact]
    public void A_required_findAll_that_finds_nothing_holds_naming_what_it_looked_for()
    {
        var mapping = Mapping(record: """
            acl:
              owners: [owners@x]
              viewers:
                - viewers@x
                - $cache: AccessGroupMap.EntitlementGroupEmail
                  $findAll: FieldIDList = $lookup.wellbore.GeoContexts.FieldID
            legal:
              legaltags: [tag]
              otherRelevantDataCountries: [NO]
            """);

        var result = Renderer(mapping).Render(Record("NO 1/1-X"));
        Assert.True(result.IsHeld);
        Assert.Contains("osdu.acl.viewers: $lookup.wellbore.GeoContexts.FieldID gives no value, so no AccessGroupMap row is found by FieldIDList", Assert.Single(result.Holds), StringComparison.Ordinal);
    }

    [Fact]
    public void A_findAll_keyed_by_a_column_or_a_text_reads_every_row_holding_it_whatever_its_case()
    {
        var mapping = Mapping(record: TestSchema.Envelope, lookups: string.Empty, data: """
            Aliases:
              $cache: AccessGroupMap.EntitlementGroupEmail
              $findAll: GeoPoliticalEntityName = country
              $modifiers: [trim]
            """);

        var result = Renderer(mapping).Render(new SourceRecord
        {
            Row = SourceRow.FromStrings(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["name"] = "log-1", ["depth"] = "1", ["country"] = " united KINGDOM " }),
            Scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase),
        });

        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        Assert.Equal(["data.office.united.kingdom.viewers@x"], result.Document["data"]!["Aliases"]!.AsArray().Select(v => v!.GetValue<string>()));
    }

    [Fact]
    public void A_mapping_s_shape_draws_each_item_of_a_list_of_values()
    {
        var shape = MappingRenderer.Shape(Mapping(), TestSchema.Build(), new Dictionary<string, string>(StringComparer.Ordinal) { ["dataPartition"] = "dev" });

        var viewers = shape.Document["acl"]!["viewers"]!.AsArray().Select(v => v!.GetValue<string>()).ToList();
        Assert.Equal(3, viewers.Count);
        Assert.Equal("viewers@x", viewers[0]);
        Assert.Equal("<string from cache.AccessGroupMap.EntitlementGroupEmail of every AccessGroupMap row where FieldIDList = $lookup.wellbore.GeoContexts.FieldID, optional>", viewers[1]);
        Assert.Contains("GeoPoliticalEntityID = $lookup.wellbore.GeoContexts.GeoPoliticalEntityID and FieldList is empty and FieldIDList is empty", viewers[2], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""
        acl:
          owners: [owners@x]
          viewers:
            - $cache: AccessGroupMap.EntitlementGroupEmail
              $findAll: FieldIDList = $lookup.wellbore.GeoContexts.FieldID
        legal:
          legaltags: [tag]
          otherRelevantDataCountries: [NO]
        """, "must list at least one literal text value, which every record carries")]
    [InlineData("""
        acl:
          owners: [owners@x]
          viewers: [viewers@x]
        legal:
          legaltags:
            - tag
            - $cache: AccessGroupMap.EntitlementGroupEmail
              $findAll: FieldIDList = $lookup.wellbore.GeoContexts.FieldID
          otherRelevantDataCountries: [NO]
        """, "the legal tags are a literal list: the legal service checks them before a run")]
    [InlineData("""
        acl:
          owners: [owners@x]
          viewers:
            - viewers@x
            - $coalesce:
                - $lookup: wellbore.FacilityName
                - $value: x
        legal:
          legaltags: [tag]
          otherRelevantDataCountries: [NO]
        """, "an item of a list gives what it reads")]
    [InlineData("""
        acl:
          owners: [owners@x]
          viewers:
            - viewers@x
            - [nested@x]
            - $lookup: wellbore.FacilityName
        legal:
          legaltags: [tag]
          otherRelevantDataCountries: [NO]
        """, "is a list, and an item of a list is a value or an object, never a list of its own")]
    public void A_list_of_values_is_refused_where_it_cannot_be_what_it_says(string record, string expected)
    {
        var ex = Assert.Throws<FlowValidationException>(() => Mapping(record: record));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("$findAll: FieldIDList = $lookup.wellbore.GeoContexts.FieldID\n$findBy: Code = unit", "a node does one of them")]
    [InlineData("$findAll: [FieldIDList = unit, GeoPoliticalEntityID = unit]", "it takes one such line")]
    [InlineData("$findAll: FieldList is empty", "needs the line the rows are found by")]
    [InlineData("$findAll: [FieldIDList = unit, FieldIDList is empty]", "no row holds both")]
    [InlineData("$findAll: [FieldIDList = unit, FieldList is empty, FieldList is empty]", "says it a second time")]
    [InlineData("$findAll: FieldIDList = $lookup.nowhere.GeoContexts.FieldID", "reads lookup 'nowhere', which the mapping does not declare")]
    [InlineData("$findAll: FieldIDList = $lookup.wellbore", "names a lookup and the field of its record it reads")]
    [InlineData("$findAll: FieldIDList = ''", "compares with empty text")]
    [InlineData("$findAll: FieldIDList = unit\n$ignoreSeparators: true", "remove $ignoreSeparators")]
    [InlineData("$findAll: FieldIDList = 'x'\n$modifiers: [trim]", "this node's $findAll reads none")]
    public void A_findAll_is_refused_where_it_cannot_say_which_rows_it_reads(string settings, string expected)
    {
        var data = "WellboreID: { $lookup: wellbore.id }\nAliases:\n  $cache: AccessGroupMap.EntitlementGroupEmail\n" + TestSchema.Indented(settings, 2);
        var ex = Assert.Throws<FlowValidationException>(() => Mapping(data: data));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_findAll_belongs_to_a_cache_node_and_finds_other_rows_than_the_lookup_it_reads()
    {
        var column = Assert.Throws<FlowValidationException>(() => Mapping(data: "WellboreID: { $lookup: wellbore.id }\nAliases:\n  $from: name\n  $findAll: FieldIDList = unit"));
        Assert.Contains("$findAll reads every row of a cached type that matches, so it belongs to a $cache node", column.Message, StringComparison.Ordinal);

        var self = Assert.Throws<FlowValidationException>(() => Mapping(data: "WellboreID: { $lookup: wellbore.id }\nAliases:\n  $cache: Wellbore.FacilityName\n  $findAll: FacilityName = $lookup.wellbore.FacilityName"));
        Assert.Contains("read that record's field with $lookup: wellbore.<field> instead", self.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("lookups:\n  wellbore:\n    $cache: Wellbore\n", "a lookup needs $findBy")]
    [InlineData("lookups:\n  wellbore:\n    $cache: Wellbore.id\n    $findBy: FacilityName = wellbore_uwi\n", "names the cached type the record is found in")]
    [InlineData("lookups:\n  wellbore:\n    $cache: Wellbore\n    $findBy: FacilityName = wellbore_uwi\n    $required: false\n", "decides for the node that reads a lookup, not for the lookup")]
    [InlineData("lookups:\n  wellbore:\n    $cache: Wellbore\n    $findBy: FacilityName = wellbore_uwi\n    $modifiers: [ref]\n", "a lookup finds a record in the cache")]
    [InlineData("lookups:\n  wellbore:\n    $cache: Wellbore\n    $findBy: FacilityName = 'NO 1'\n    $modifiers: [trim]\n", "this lookup's $findBy reads none")]
    [InlineData("lookups:\n  wellbore: Wellbore\n", "names the cached type its record is found in and how")]
    public void A_lookup_is_refused_where_it_cannot_say_which_record_it_finds(string lookups, string expected)
    {
        var ex = Assert.Throws<FlowValidationException>(() => Mapping(lookups: lookups));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lookup_nothing_reads_and_a_lookup_node_that_says_how_to_find_its_record_are_refused()
    {
        var unused = Assert.Throws<FlowValidationException>(() => Mapping(
            record: TestSchema.Envelope,
            lookups: Lookups + "\n  spare:\n    $cache: UnitOfMeasure\n    $findBy: Code = unit\n"));
        Assert.Contains("lookups.spare is read by no node", unused.Message, StringComparison.Ordinal);

        var findBy = Assert.Throws<FlowValidationException>(() => Mapping(data: "WellboreID: { $lookup: wellbore.id, $findBy: FacilityName = name }"));
        Assert.Contains("a $lookup node reads the record its lookup finds; write $findBy on the lookup under lookups", findBy.Message, StringComparison.Ordinal);

        var none = Assert.Throws<FlowValidationException>(() => Mapping(lookups: string.Empty));
        Assert.Contains("reads lookup 'wellbore', and the mapping declares no lookups", none.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_gate_refuses_a_findAll_whose_fields_the_cache_does_not_capture()
    {
        // No group row holds a FieldList: every row would pass as a country group, so the gate refuses rather than widen.
        var groups = new ReferenceType("AccessGroupMap", "data-governance--AccessGroupMap",
            Groups().Items.Select(i => new ReferenceItem(i.Id, i.Fields.Where(f => f.Key != "FieldList").ToDictionary(StringComparer.OrdinalIgnoreCase))));
        var issues = Preflight.Check(Mapping(), TestSchema.Build(), Cache(groups: groups), TestSchema.Context(), sourceColumns: null);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error
            && i.Message.Contains("reads only the AccessGroupMap rows whose FieldList is empty, and no row of cache version 'refs-1' holds a FieldList, so every row would pass", StringComparison.Ordinal));

        // A key field the cache holds nowhere, and a lookup path no wellbore holds.
        var keyless = Mapping(record: Access.Replace("FieldIDList = $lookup", "FieldIdentifiers = $lookup", StringComparison.Ordinal)
            .Replace("GeoContexts.GeoPoliticalEntityID", "GeoContexts.Country", StringComparison.Ordinal));
        var found = Preflight.Check(keyless, TestSchema.Build(), Cache(), TestSchema.Context(), sourceColumns: null);
        Assert.Contains(found, i => i.Severity == IssueSeverity.Error && i.Message.Contains("caches no FieldIdentifiers", StringComparison.Ordinal));
        Assert.Contains(found, i => i.Severity == IssueSeverity.Error && i.Message.Contains("no Wellbore record of cache version 'refs-1' holds 'GeoContexts.Country'", StringComparison.Ordinal));

        // The mapping as written passes.
        Assert.DoesNotContain(Preflight.Check(Mapping(), TestSchema.Build(), Cache(), TestSchema.Context(), sourceColumns: null), i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void The_gate_refuses_a_findAll_written_to_a_single_value()
    {
        var mapping = Mapping(data: """
            WellboreID: { $lookup: wellbore.id }
            Symbol:
              $cache: AccessGroupMap.EntitlementGroupEmail
              $findAll: FieldIDList = $lookup.wellbore.GeoContexts.FieldID
            """);
        var issues = Preflight.Check(mapping, TestSchema.Build(), Cache(), TestSchema.Context(), sourceColumns: null);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Message.Contains("with $findAll gives a list, one value from every row it finds", StringComparison.Ordinal));
    }

    /// <summary>A fixture that declares the wellbore and the access group it renders against, whatever the partition's cache holds.</summary>
    internal const string FieldFixture = """
        fixtures:
          - name: a wellbore of a field whose group the data office maintains
            row: { name: log-1, depth: "1", wellbore_uwi: NO 5/5-F }
            cache:
              Wellbore:
                - id: dev:master-data--Wellbore:f1
                  FacilityName: NO 5/5-F
                  GeoContexts.FieldID: [ "dev:master-data--Field:F:" ]
              AccessGroupMap:
                - id: dev:data-governance--AccessGroupMap:f
                  FieldIDList: [ "dev:master-data--Field:F:" ]
                  FieldList: [ F ]
                  EntitlementGroupEmail: data.office.f.viewers@x
            expected: |
              {
                "id": "dev:test--Thing:x",
                "kind": "test:wks:work-product-component--Thing:1.0.0",
                "acl": { "owners": ["owners@x"], "viewers": ["viewers@x", "data.office.f.viewers@x"] },
                "legal": { "legaltags": ["tag"], "otherRelevantDataCountries": ["NO"] },
                "data": { "Name": "log-1", "Depth": 1, "WellboreID": "dev:master-data--Wellbore:f1:" }
              }
        """;

    [Fact]
    public void A_fixture_renders_against_the_rows_it_declares_and_not_the_partition_s_rows_of_the_day()
    {
        var mapping = Mapping(fixtures: FieldFixture);
        var fixture = Assert.Single(mapping.Fixtures);
        Assert.Equal(["AccessGroupMap", "Wellbore"], fixture.Cache.Keys.Order(StringComparer.Ordinal));

        // The partition's cache holds neither the wellbore nor the group; the fixture renders against what it declares.
        var render = Assert.Single(Preflight.RenderFixtures(mapping, Renderer(mapping)));
        Assert.Null(render.Problem);
        Assert.False(render.Result!.IsHeld, string.Join("; ", render.Result.Holds));
        Assert.Equal(["viewers@x", "data.office.f.viewers@x"], Viewers(render.Result));
        Assert.Equal("dev:master-data--Wellbore:f1:", render.Result.Document["data"]!["WellboreID"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("Field: []", "the mapping reads no cached type 'Field'")]
    [InlineData("Wellbore: { id: x }", "lists the rows the fixture assumes")]
    [InlineData("Wellbore: [ { FacilityName: x } ]", "names no id")]
    [InlineData("Wellbore: [ { id: a }, { id: a } ]", "is 'a' again; a type holds a record once")]
    public void A_fixture_s_cache_block_is_refused_where_it_cannot_stand_in_for_the_cache(string cache, string expected)
    {
        var fixtures = "fixtures:\n  - name: f\n    row: { name: log-1, depth: \"1\", wellbore_uwi: x }\n    cache:\n" + TestSchema.Indented(cache, 6) + "    expected: \"{}\"\n";
        var ex = Assert.Throws<FlowValidationException>(() => Mapping(fixtures: fixtures));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_builder_opens_lookups_find_alls_lists_and_a_fixture_s_cached_rows_and_writes_back_the_same_mapping()
    {
        var original = Mapping(
            lookups: """
                lookups:
                  wellbore:
                    $cache: Wellbore
                    $findBy:
                      - FacilityName = wellbore_uwi
                      - NameAliases.AliasName = wellbore_uwi
                    $modifiers: [trim]
                    $ignoreSeparators: true
                    $description: The wellbore the log belongs to.
                """,
            record: """
                acl:
                  owners: [owners@x]
                  viewers:
                    - viewers@x
                    - $value: wellbores@x
                      $when: not empty(wellbore_uwi)
                    - $cache: AccessGroupMap.EntitlementGroupEmail
                      $findAll: FieldIDList = $lookup.wellbore.GeoContexts.FieldID
                      $required: false
                    - $cache: AccessGroupMap.EntitlementGroupEmail
                      $findAll:
                        - GeoPoliticalEntityID = $lookup.wellbore.GeoContexts.GeoPoliticalEntityID
                        - FieldList is empty
                        - FieldIDList is empty
                      $required: false
                    - $cache: AccessGroupMap.EntitlementGroupEmail
                      $findAll: GeoPoliticalEntityName = 'United Kingdom'
                      $when: not empty(wellbore_uwi)
                      $required: false
                legal:
                  legaltags: [tag]
                  otherRelevantDataCountries: [NO]
                """,
            data: """
                WellboreID: { $lookup: wellbore.id }
                Symbol:
                  $coalesce:
                    - $lookup: wellbore.FacilityName
                    - $value: unknown
                  $when: not empty(wellbore_uwi)
                Aliases:
                  - alias@x
                  - $lookup: wellbore.FacilityName
                    $required: false
                    $description: The wellbore's name, when the lookup finds it.
                  - $cache: AccessGroupMap.GeoPoliticalEntityName
                    $findAll: FieldList = wellbore_uwi
                    $modifiers: [upper]
                    $required: false
                """,
            fixtures: FieldFixture);

        var draft = MappingBuilder.FromDefinition(original);
        Assert.Empty(MappingBuilder.Incomplete(draft));

        // The draft holds what the document says, in the builder's terms.
        var lookup = Assert.Single(draft.Lookups);
        Assert.Equal(("wellbore", "Wellbore", true), (lookup.Name, lookup.CacheType, lookup.IgnoreSeparators));
        Assert.Equal(["FacilityName", "NameAliases.AliasName"], lookup.FindBy.Select(f => f.Field));
        Assert.Equal("trim", Assert.Single(lookup.Modifiers).Kind);

        var wellbore = draft.Entries.Single(e => e.Target == "osdu.data.WellboreID");
        Assert.Equal((MappingDraftInput.Lookup, "wellbore", "id"), (wellbore.Input, wellbore.Lookup, wellbore.CacheField));
        Assert.Empty(wellbore.FindBy);

        var viewers = draft.Entries.Single(e => e.Target == "osdu.acl.viewers");
        Assert.Equal(MappingDraftInput.List, viewers.Input);
        Assert.Equal(
            [MappingDraftInput.Static, MappingDraftInput.Static, MappingDraftInput.Cache, MappingDraftInput.Cache, MappingDraftInput.Cache],
            viewers.Items.Select(i => i.Input));
        Assert.Equal("not empty(wellbore_uwi)", viewers.Items[1].When);
        Assert.Equal("wellbore.GeoContexts.FieldID", viewers.Items[2].FindAll!.Lookup);
        Assert.Equal(["FieldList", "FieldIDList"], viewers.Items[3].FindAll!.Empty);
        Assert.Equal("United Kingdom", viewers.Items[4].FindAll!.Literal);

        var aliases = draft.Entries.Single(e => e.Target == "osdu.data.Aliases");
        Assert.Equal(MappingDraftInput.Lookup, aliases.Items[1].Input);
        Assert.Equal(("wellbore_uwi", "upper"), (aliases.Items[2].FindAll!.Column, Assert.Single(aliases.Items[2].Modifiers).Kind));
        Assert.Equal(MappingDraftInput.Lookup, draft.Entries.Single(e => e.Target == "osdu.data.Symbol").Alternatives[0].Input);
        Assert.Equal(["Wellbore", "AccessGroupMap"], Assert.Single(draft.Fixtures).Cache!.Keys);

        // Written back and read again, it is the same draft and the same document.
        var yaml = MappingBuilder.ToYaml(draft);
        var reread = new DeliveryDocumentLoader().ParseMapping(yaml, "thing.yaml");
        var again = MappingBuilder.FromDefinition(reread);
        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(draft, json), System.Text.Json.JsonSerializer.Serialize(again, json));
        Assert.Equal(yaml, MappingBuilder.ToYaml(again));

        // And it renders every row exactly as the document it was opened from.
        foreach (var uwi in new[] { "NO 16/3-A-1", "UK 9/8-A1", "ALBA A1", "NO 1/1-X", "nowhere", null })
        {
            var before = Renderer(original).Render(Record(uwi));
            var after = Renderer(reread).Render(Record(uwi));
            Assert.Equal(string.Join("; ", before.Holds), string.Join("; ", after.Holds));
            Assert.Equal(before.Document.ToJsonString(), after.Document.ToJsonString());
        }

        // The fixture keeps the rows it declares: its wellbore is in no cache but its own, so it renders unheld only with them.
        var fixtureBefore = Assert.Single(Preflight.RenderFixtures(original, Renderer(original)));
        var fixtureAfter = Assert.Single(Preflight.RenderFixtures(reread, Renderer(reread)));
        Assert.Null(fixtureAfter.Problem);
        Assert.False(fixtureAfter.Result!.IsHeld, string.Join("; ", fixtureAfter.Result.Holds));
        Assert.Equal("dev:master-data--Wellbore:f1:", fixtureAfter.Result.Document["data"]!["WellboreID"]!.GetValue<string>());
        Assert.Equal(fixtureBefore.Result!.Document.ToJsonString(), fixtureAfter.Result.Document.ToJsonString());
    }
}
