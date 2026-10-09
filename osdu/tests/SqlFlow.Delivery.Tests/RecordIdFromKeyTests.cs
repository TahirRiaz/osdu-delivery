using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Templates;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// An OSDU id made from the key's own values (<c>dataset.idFrom: key</c>) instead of the delivery key: how a value is
/// written into the id and read back out of it, what a mapping says, what a render writes and what it holds for, and that a
/// mapping that does not say so keeps the id it always had. The ledger's side (claims, conflicts, a record whose id would
/// move) is <see cref="SqlServerLedgerTests"/> and <see cref="RecordIdFromKeyDeliveryTests"/>.
/// </summary>
public sealed partial class RecordIdFromKeyTests
{
    private const string Units = "reference-data--ExternalUnitOfMeasure";

    private const string Things = "work-product-component--Thing";

    /// <summary>The record id the storage service takes (openapi storage v2), its <c>\w</c> read as the schema dialect reads it: ASCII.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_\-\.]+:[A-Za-z0-9_\-\.]+:[A-Za-z0-9_\-\.\:\%]+$", RegexOptions.CultureInvariant)]
    private static partial Regex StorageId();

    private static string? Id(params string?[] values) => TargetId.ComposeFromKey("dev", Units, values, out _);

    private static string Unique(string id) => id[$"dev:{Units}:".Length..];

    [Theory]
    [InlineData("WELLDB", "WELLDB")]
    [InlineData("WELLDB::GAPI", "WELLDB::GAPI")]
    [InlineData("LIS-LAS::REV", "LIS-LAS::REV")]
    [InlineData("WELLDB::-UNITLES", "WELLDB::-UNITLES")]
    [InlineData("WELLDB::G/CC", "WELLDB::G%2FCC")]
    [InlineData("WELLDB::KPA.S/M", "WELLDB::KPA.S%2FM")]
    [InlineData("WELLDB::%", "WELLDB::%25")]
    [InlineData("WELLDB::A%2FB", "WELLDB::A%252FB")]
    [InlineData("WELLDB::DEG C", "WELLDB::DEG%20C")]
    [InlineData("WELLDB::a+b?c#d&e\\f", "WELLDB::a%2Bb%3Fc%23d%26e%5Cf")]
    [InlineData("WELLDB::\u00B5S/FT", "WELLDB::%C2%B5S%2FFT")]
    [InlineData("WELLDB::\u00B0F", "WELLDB::%C2%B0F")]
    [InlineData("WELLDB::\U0001D510", "WELLDB::%F0%9D%94%90")]
    [InlineData("  WELLDB::GAPI\t", "WELLDB::GAPI")]
    public void One_key_value_is_the_code_as_osdu_catalogs_write_it_with_every_other_character_percent_encoded(string value, string unique)
    {
        var id = Id(value);

        Assert.Equal($"dev:{Units}:{unique}", id);
        Assert.Matches(StorageId(), id!);
    }

    [Fact]
    public void Ids_keep_the_case_of_the_key_as_osdu_and_the_delivery_key_do()
    {
        Assert.Equal("WELLDB::fraction", Unique(Id("WELLDB::fraction")!));
        Assert.NotEqual(Id("WELLDB::fraction"), Id("WELLDB::FRACTION"));
        Assert.NotEqual(DeliveryKey.Derive("welldb-external-units", ["WELLDB::fraction"]), DeliveryKey.Derive("welldb-external-units", ["WELLDB::FRACTION"]));
    }

    [Fact]
    public void A_percent_sign_is_always_encoded_so_a_value_that_looks_encoded_never_lands_on_another_values_id()
    {
        Assert.NotEqual(Id("A/B"), Id("A%2FB"));
        Assert.Equal("A%2FB", Unique(Id("A/B")!));
        Assert.Equal("A%252FB", Unique(Id("A%2FB")!));

        // A reference built from a value still reads an escape already in it as one, as it always has.
        Assert.Equal("g%2Fcm3", IdValues.Encode("g/cm3"));
        Assert.Equal("g%2Fcm3", IdValues.Encode("g%2fcm3"));
    }

    [Fact]
    public void Several_key_values_escape_their_own_colons_and_are_joined_by_one()
    {
        Assert.Equal("a%3Ab:c", Unique(Id("a:b", "c")!));
        Assert.Equal("a:b%3Ac", Unique(Id("a", "b:c")!));
        Assert.Equal("PROJECT_A:L%2F1001", Unique(Id("PROJECT_A", "L/1001")!));
        Assert.NotEqual(Id("a:b", "c"), Id("a", "b:c"));

        // Keys of different lengths can give one id. They are two mappings' keys, since a mapping's key has a fixed number
        // of columns, and the ledger refuses the second record to claim the id (SqlServerLedgerTests, RecordIdFromKeyDeliveryTests).
        Assert.Equal(Id("a", "b"), Id("a:b"));
    }

    [Fact]
    public void Two_keys_give_one_id_exactly_when_they_give_one_delivery_key_and_every_id_reads_back_as_its_key()
    {
        // Keys of one mapping, one to three values long, drawn from separators, escapes, spaces, cases and characters outside
        // ASCII: the id and the delivery key agree on which keys are one record, so a ledger record and its OSDU id are one.
        var alphabet = new[] { "a", "A", ":", "::", "%", "%2F", "/", " ", "-", ".", "_", "\u00E9", "\U0001D510", "1" };
        var random = new Random(20261007);
        var mappings = Enumerable.Range(1, 3).ToDictionary(count => count, _ => new Dictionary<string, (DeliveryKey Key, string[] Values)>(StringComparer.Ordinal));
        for (var sample = 0; sample < 6000; sample++)
        {
            var count = random.Next(1, 4);
            var byId = mappings[count];
            var values = Enumerable.Range(0, count)
                .Select(_ => string.Concat(Enumerable.Range(0, random.Next(1, 5)).Select(_ => alphabet[random.Next(alphabet.Length)])))
                .ToArray();
            if (values.Any(string.IsNullOrWhiteSpace))
            {
                Assert.Null(TargetId.ComposeFromKey("dev", Units, values, out var problem));
                Assert.Contains("empty", problem, StringComparison.Ordinal);
                continue;
            }

            var id = TargetId.ComposeFromKey("dev", Units, values, out var none);
            Assert.Null(none);
            Assert.Matches(StorageId(), id!);
            var key = DeliveryKey.Derive("welldb-external-units", values);
            if (byId.TryGetValue(id!, out var seen))
            {
                Assert.Equal(seen.Key, key);
            }
            else
            {
                Assert.DoesNotContain(byId.Values, v => v.Key == key);
                byId[id!] = (key, values);
            }

            Assert.Equal(values.Select(v => v.Trim()), TargetId.KeyValues(id!, "dev", Units, count));
        }
    }

    [Fact]
    public void A_key_that_gives_no_id_says_why()
    {
        Assert.Null(TargetId.ComposeFromKey("dev", Units, ["  "], out var empty));
        Assert.Equal("the key is empty, and the OSDU id is made from it", empty);
        Assert.Null(TargetId.ComposeFromKey("dev", Units, ["a", null], out var second));
        Assert.Equal("key value 2 of 2 is empty, and the OSDU id is made from every key value", second);
        Assert.Null(TargetId.ComposeFromKey("dev", Units, ["WELLDB::\uD800"], out var surrogate));
        Assert.Equal("key value 1 holds text that is not valid Unicode, which no OSDU id can carry", surrogate);
        Assert.Null(TargetId.ComposeFromKey("dev", Units, [], out var none));
        Assert.NotNull(none);
    }

    [Fact]
    public void An_id_longer_than_the_ledger_keeps_is_refused_never_cut()
    {
        var prefix = $"dev:{Units}:".Length;
        var longest = new string('A', TargetId.MaxLength - prefix);
        Assert.Equal(TargetId.MaxLength, Id(longest)!.Length);

        Assert.Null(TargetId.ComposeFromKey("dev", Units, [longest + "A"], out var problem));
        Assert.Contains($"{TargetId.MaxLength + 1} characters long", problem, StringComparison.Ordinal);
        Assert.Contains($"at most {TargetId.MaxLength}", problem, StringComparison.Ordinal);

        // Encoding is what makes a short key long: each of these 100 characters is written as six.
        Assert.Null(TargetId.ComposeFromKey("dev", Units, [new string('\u00E9', 100)], out var encoded));
        Assert.Contains($"{prefix + 600} characters long", encoded, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("test:reference-data--ExternalUnitOfMeasure:WELLDB::GAPI")]
    [InlineData("dev:reference-data--UnitOfMeasure:WELLDB::GAPI")]
    [InlineData("dev:reference-data--ExternalUnitOfMeasure:")]
    [InlineData("dev:reference-data--ExternalUnitOfMeasure:A%2")]
    [InlineData("dev:reference-data--ExternalUnitOfMeasure:A%ZZ")]
    [InlineData("dev:reference-data--ExternalUnitOfMeasure:A%FF")]
    [InlineData("dev:reference-data--ExternalUnitOfMeasure:A/B")]
    [InlineData("dev:reference-data--ExternalUnitOfMeasure:%20A")]
    public void Text_that_is_no_id_a_key_made_reads_back_as_no_key(string id)
    {
        Assert.Null(TargetId.KeyValues(id, "dev", Units, 1));
    }

    [Fact]
    public void An_id_reads_back_as_as_many_values_as_the_key_has_columns_or_none()
    {
        var id = Id("PROJECT_A", "L/1001")!;
        Assert.Equal(["PROJECT_A", "L/1001"], TargetId.KeyValues(id, "dev", Units, 2));
        Assert.Equal(["PROJECT_A:L/1001"], TargetId.KeyValues(id, "dev", Units, 1));
        Assert.Null(TargetId.KeyValues(id, "dev", Units, 3));
    }

    // --- The mapping -----------------------------------------------------------------------------------------------------

    private static string Document(string? idFrom) => idFrom is null
        ? TestSchema.MappingDocument()
        : TestSchema.MappingDocument().Replace("  key: [name]\n", $"  key: [name]\n  idFrom: {idFrom}\n", StringComparison.Ordinal);

    private static MappingDefinition Mapping(string? idFrom) => new DeliveryDocumentLoader().ParseMapping(Document(idFrom), "thing.yaml");

    [Theory]
    [InlineData(null, MappingIdSource.DeliveryKey)]
    [InlineData("deliveryKey", MappingIdSource.DeliveryKey)]
    [InlineData("key", MappingIdSource.Key)]
    public void A_mapping_makes_its_ids_from_the_delivery_key_unless_it_says_from_the_key(string? idFrom, MappingIdSource expected)
    {
        Assert.Equal(expected, Mapping(idFrom).Dataset.IdFrom);
    }

    [Theory]
    [InlineData("Key")]
    [InlineData("keys")]
    [InlineData("guid")]
    public void A_mapping_naming_anything_else_is_refused_with_the_two_words_it_takes(string idFrom)
    {
        var refused = Assert.Throws<FlowValidationException>(() => Mapping(idFrom));
        Assert.Contains($"dataset.idFrom is '{idFrom}'", refused.Message, StringComparison.Ordinal);
        Assert.Contains("write deliveryKey", refused.Message, StringComparison.Ordinal);
        Assert.Contains("or key for one made from the values of dataset.key", refused.Message, StringComparison.Ordinal);
    }

    // --- The render ------------------------------------------------------------------------------------------------------

    private static SourceRecord Row(string name)
    {
        var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["name"] = name, ["depth"] = "1" };
        return new SourceRecord { Row = SourceRow.FromStrings(row), Scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase) };
    }

    private static RenderResult Render(MappingDefinition mapping, string name, SchemaSnapshot? schema = null)
        => new MappingRenderer(mapping, schema ?? TestSchema.Build(), TestSchema.References(), TestSchema.Context()).Render(Row(name));

    [Fact]
    public void A_mapping_that_says_nothing_renders_the_delivery_keys_id_as_it_always_has()
    {
        foreach (var idFrom in new string?[] { null, "deliveryKey" })
        {
            var rendered = Render(Mapping(idFrom), "WELLDB::G/CC");

            var key = DeliveryKey.Derive("test", ["WELLDB::G/CC"]);
            Assert.False(rendered.IsHeld, string.Join("; ", rendered.Holds));
            Assert.Equal(key, rendered.Key);
            Assert.Equal(TargetId.Compose("dev", Things, key), rendered.TargetId);
            Assert.Equal($"dev:{Things}:{key.Value:N}", rendered.Document["id"]!.GetValue<string>());
        }
    }

    [Fact]
    public void A_mapping_that_makes_its_ids_from_the_key_renders_the_code_and_keeps_the_delivery_key_as_the_records_identity()
    {
        var byKey = Render(Mapping("key"), "WELLDB::G/CC");
        var byDeliveryKey = Render(Mapping(null), "WELLDB::G/CC");

        Assert.False(byKey.IsHeld, string.Join("; ", byKey.Holds));
        Assert.Equal($"dev:{Things}:WELLDB::G%2FCC", byKey.TargetId);
        Assert.Equal(byKey.TargetId, byKey.Document["id"]!.GetValue<string>());

        // The ledger keys the record by its delivery key whatever its id is made from, so a retry or an update of the row
        // finds the same record, and the document is the same but for its id.
        Assert.Equal(byDeliveryKey.Key, byKey.Key);
        Assert.Equal(byDeliveryKey.SourceKey, byKey.SourceKey);
        var withoutId = (JsonObject)byKey.Document.DeepClone();
        var otherWithoutId = (JsonObject)byDeliveryKey.Document.DeepClone();
        withoutId.Remove("id");
        otherWithoutId.Remove("id");
        Assert.True(JsonNode.DeepEquals(withoutId, otherWithoutId));

        // Rendering the row again gives the same id and the same document: the id is a function of the key alone.
        var again = Render(Mapping("key"), "WELLDB::G/CC");
        Assert.Equal((byKey.TargetId, byKey.MetadataHash), (again.TargetId, again.MetadataHash));
    }

    [Fact]
    public void A_key_that_gives_no_id_holds_the_record_and_writes_no_id()
    {
        var rendered = Render(Mapping("key"), new string('A', TargetId.MaxLength));

        Assert.True(rendered.IsHeld);
        Assert.Null(rendered.TargetId);
        Assert.Null(rendered.Document["id"]);
        var hold = Assert.Single(rendered.Holds);
        Assert.StartsWith("id: the mapping makes the OSDU id from the key (dataset.idFrom: key), and test:", hold, StringComparison.Ordinal);
        Assert.Contains($"at most {TargetId.MaxLength}", hold, StringComparison.Ordinal);
    }

    [Fact]
    public void An_id_the_templates_pattern_refuses_holds_the_record()
    {
        // A template whose ids are upper-case letters and digits alone: a code with a slash cannot be one of them.
        var root = (JsonObject)TestSchema.Build().Root.DeepClone();
        root["properties"]!["id"] = new JsonObject
        {
            ["type"] = "string",
            ["pattern"] = $"^[\\w\\-\\.]+:{Things.Replace("-", "\\-", StringComparison.Ordinal)}:[A-Z0-9]+$",
        };
        var strict = SchemaSnapshot.Parse(TestSchema.Kind, root.ToJsonString(), new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var mapping = new DeliveryDocumentLoader().ParseMapping(
            Document("key").Replace(TestSchema.Build().Version, strict.Version, StringComparison.Ordinal), "thing.yaml");

        Assert.False(Render(mapping, "GAPI", strict).IsHeld);
        var refused = Render(mapping, "G/CC", strict);
        Assert.True(refused.IsHeld);
        Assert.Null(refused.TargetId);
        Assert.Contains("does not match the pattern the template gives the variable", Assert.Single(refused.Holds), StringComparison.Ordinal);
    }

    [Fact]
    public void The_shape_of_a_mapping_says_what_its_ids_are_made_from()
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal) { [RenderContext.DataPartitionParameter] = "dev" };

        Assert.Equal(
            $"dev:{Things}:<dataset.name, percent-encoded>",
            MappingRenderer.Shape(Mapping("key"), TestSchema.Build(), parameters).Document["id"]!.GetValue<string>());
        Assert.Equal(
            $"dev:{Things}:<delivery key from test, dataset.name>",
            MappingRenderer.Shape(Mapping(null), TestSchema.Build(), parameters).Document["id"]!.GetValue<string>());
    }

    // --- The mapping builder ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_builder_keeps_what_the_ids_are_made_from_through_a_draft_and_back()
    {
        var draft = MappingBuilder.FromDefinition(Mapping("key"));
        Assert.Equal("key", draft.IdFrom);
        Assert.Empty(MappingBuilder.Incomplete(draft));
        var yaml = MappingBuilder.ToYaml(draft);
        Assert.Contains("\n  idFrom: key\n", yaml, StringComparison.Ordinal);
        Assert.Equal(MappingIdSource.Key, new DeliveryDocumentLoader().ParseMapping(yaml, "thing.yaml").Dataset.IdFrom);

        var plain = MappingBuilder.FromDefinition(Mapping(null));
        Assert.Null(plain.IdFrom);
        Assert.DoesNotContain("idFrom", MappingBuilder.ToYaml(plain), StringComparison.Ordinal);

        var issue = Assert.Single(MappingBuilder.Incomplete(plain with { IdFrom = "guid" }));
        Assert.Equal(MappingDraftIssue.ErrorSeverity, issue.Severity);
        Assert.Contains("(deliveryKey)", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_is_percent_encoded_as_utf8_with_upper_case_hex_and_decoded_back()
    {
        foreach (var value in new[] { "a b", "%", "\u00E9", "\U0001D510", "a:b", "~!*'()" })
        {
            var encoded = IdSegment.Encode(value, keepEscapes: false, keepColons: false)!;
            Assert.DoesNotContain(encoded, c => !IdSegment.IsIdCharacter(c) && c != '%');
            Assert.All(Regex.Matches(encoded, "%(..)"), escape => Assert.Matches("^[0-9A-F]{2}$", escape.Groups[1].Value));
            Assert.Equal(value, IdSegment.Decode(encoded));
        }

        var expected = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes("\u00E9"))
        {
            expected.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }

        Assert.Equal(expected.ToString(), IdSegment.Encode("\u00E9", keepEscapes: false));
    }
}
