using System.Text;
using System.Text.Json.Nodes;
using SqlFlow.Delivery;
using SqlFlow.Delivery.Engine.Protocols.Etp;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Storage;
using SqlFlow.Delivery.Tests.Etp;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What the etp route checks before it opens a session: the identity an Energistics object's XML carries and the
/// references it names (osdu/specs/reservoir-ddms/INTEGRATION.md sections 4.2 and 5.1), and the arrays a record
/// declares with the slices a large one crosses in (sections 4.6 and 5.4). Every refusal has to name what to fix,
/// because none of them can be fixed by sending the record again.
/// </summary>
public class EtpObjectTests
{
    [Fact]
    public void An_object_says_its_type_uuid_title_and_the_arrays_it_names()
    {
        var uuid = Guid.NewGuid();
        var identity = Read(EtpSamples.Object(uuid, "Top Volve", arrayPath: "/RESQML/points"));

        Assert.Equal("resqml20.obj_Grid2dRepresentation", identity.ObjectType);
        Assert.Equal(uuid, identity.Uuid);
        Assert.Equal("Top Volve", identity.Title);
        Assert.Equal(new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero), identity.LastChanged);
        Assert.Equal(["RESQML/points"], identity.ArrayPaths);
    }

    [Theory]
    [InlineData("commonv2", "2.0", "eml20")]
    [InlineData("commonv2", "2.3", "eml23")]
    [InlineData("resqmlv2", "2.2", "resqml22")]
    [InlineData("witsmlv2", "2.1", "witsml21")]
    [InlineData("prodmlv2", "2.2", "prodml22")]
    public void The_namespace_and_the_schema_version_say_which_store_the_object_belongs_to(string space, string version, string ml)
    {
        var identity = Read(Xml(Guid.NewGuid(), $"http://www.energistics.org/energyml/data/{space}", version, "obj_Thing"));
        Assert.Equal($"{ml}.obj_Thing", identity.ObjectType);
    }

    [Theory]
    [InlineData("not xml at all", "is not XML")]
    [InlineData("<root/>", "no usable uuid")]
    public void Xml_the_store_cannot_file_is_refused_with_what_to_fix(string xml, string expected)
    {
        var held = Assert.Throws<RecordHeldException>(() => Read(xml));
        Assert.Contains(expected, held.Message, StringComparison.Ordinal);
        Assert.StartsWith("the object of L-1", held.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_object_without_a_schema_version_a_citation_or_a_known_namespace_is_refused()
    {
        var uuid = Guid.NewGuid();
        Assert.Contains(
            "no schemaVersion",
            Assert.Throws<RecordHeldException>(() => Read(Xml(uuid, EtpSamples.Resqml20, version: null, "obj_Thing"))).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "no Citation element directly under its root",
            Assert.Throws<RecordHeldException>(() => Read(Xml(uuid, EtpSamples.Resqml20, "2.0", "obj_Thing", citation: false))).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "does not file: it takes RESQML 2.0 and 2.2",
            Assert.Throws<RecordHeldException>(() => Read(Xml(uuid, "http://example.com/other", "2.0", "obj_Thing"))).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_resqml_object_typed_without_obj_would_be_stored_unreachable_and_is_refused()
    {
        var held = Assert.Throws<RecordHeldException>(() => Read(Xml(Guid.NewGuid(), EtpSamples.Resqml20, "2.0", "Grid2dRepresentation")));
        Assert.Contains("obj_Grid2dRepresentation", held.Message, StringComparison.Ordinal);
        Assert.Contains("stored unreachable", held.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("demo study")]
    [InlineData("demo/study?x")]
    public void A_dataspace_path_the_server_would_refuse_is_refused_here(string path)
    {
        var held = Assert.Throws<RecordHeldException>(() => EtpObjectXml.CheckPath(path));
        Assert.Contains(path, held.Message, StringComparison.Ordinal);
        Assert.Contains("project/study", held.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_uris_are_the_ones_the_server_prints()
    {
        var uuid = new Guid("11111111-2222-3333-4444-555555555555");
        Assert.Equal("eml:///dataspace('demo/study')", EtpObjectXml.DataspaceUri("demo/study"));
        Assert.Equal(
            "eml:///dataspace('demo/study')/resqml20.obj_Grid2dRepresentation(11111111-2222-3333-4444-555555555555)",
            EtpObjectXml.ObjectUri("demo/study", "resqml20.obj_Grid2dRepresentation", uuid));
    }

    [Theory]
    [InlineData("""{"Type":"arrayOfDouble","Dimensions":[1],"Values":[1]}""", "names no Path")]
    [InlineData("""{"Path":"a","Dimensions":[1],"Values":[1]}""", "names no Type")]
    [InlineData("""{"Path":"a","Type":"arrayOfWhat","Dimensions":[1],"Values":[1]}""", "does not store")]
    [InlineData("""{"Path":"a","Type":"arrayOfDouble","Values":[1]}""", "names no Dimensions")]
    [InlineData("""{"Path":"a","Type":"arrayOfDouble","Dimensions":[1,1,1,1,1],"Values":[1]}""", "at most 4")]
    [InlineData("""{"Path":"a","Type":"arrayOfDouble","Dimensions":[1]}""", "nothing to send")]
    [InlineData("""{"Path":"a","Type":"arrayOfDouble","Dimensions":[1],"Column":"c","Values":[1]}""", "one of the two")]
    public void An_array_declaration_the_route_cannot_send_is_refused_with_what_to_fix(string array, string expected)
    {
        var data = new JsonObject { ["Arrays"] = new JsonArray(JsonNode.Parse(array)) };
        var held = Assert.Throws<RecordHeldException>(() => EtpArrays.Read(data, "the object of L-1"));
        Assert.Contains(expected, held.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_arrays_of_one_record_may_not_share_a_path()
    {
        var data = new JsonObject
        {
            ["Arrays"] = new JsonArray(
                JsonNode.Parse("""{"Path":"a","Type":"arrayOfDouble","Dimensions":[1],"Values":[1]}"""),
                JsonNode.Parse("""{"Path":"/a","Type":"arrayOfInt","Dimensions":[1],"Values":[1]}""")),
        };
        Assert.Contains("twice", Assert.Throws<RecordHeldException>(() => EtpArrays.Read(data, "the object of L-1")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_array_that_fits_a_message_crosses_in_one_slice_and_a_larger_one_in_slices_of_whole_rows()
    {
        var whole = EtpArrays.Slices([4, 3], elementBytes: 8, budgetBytes: 1000);
        var one = Assert.Single(whole);
        Assert.Equal([0, 0], one.Starts);
        Assert.Equal([4, 3], one.Counts);

        // 24 bytes a slice is one row of three doubles; four rows are four slices.
        var rows = EtpArrays.Slices([4, 3], elementBytes: 8, budgetBytes: 24);
        Assert.Equal(4, rows.Count);
        Assert.Equal([1, 3], rows[1].Counts);
        Assert.Equal([1, 0], rows[1].Starts);
        Assert.Equal(3, rows[1].Offset);
        Assert.Equal(3, rows[1].Length);
    }

    [Fact]
    public void A_slab_too_large_for_a_message_is_cut_along_the_next_dimension_instead()
    {
        // Each step of the first dimension is 100 doubles, which is past a 400 byte budget, so the cut moves inward.
        var slices = EtpArrays.Slices([3, 10, 10], elementBytes: 8, budgetBytes: 400);

        Assert.Equal(3 * 2, slices.Count);
        Assert.All(slices, slice => Assert.Equal(1, slice.Counts[0]));
        Assert.All(slices, slice => Assert.Equal(5, slice.Counts[1]));
        Assert.All(slices, slice => Assert.Equal(10, slice.Counts[2]));

        // Together they cover the array exactly once, in order, each a contiguous run.
        var at = 0L;
        foreach (var slice in slices)
        {
            Assert.Equal(at, slice.Offset);
            at += slice.Length;
        }

        Assert.Equal(300, at);
    }

    [Fact]
    public async Task An_array_reads_its_values_from_the_column_of_the_bulk_payload_it_names()
    {
        var declaration = Assert.Single(EtpArrays.Read(
            new JsonObject { ["Arrays"] = new JsonArray(JsonNode.Parse("""{"Path":"RESQML/points","Type":"arrayOfDouble","Dimensions":[3],"Column":"depth"}""")) },
            "the object of L-1"));

        var values = await EtpArrays.FromParquetAsync(declaration, await BulkAsync("depth", 10.5, 11.5, 12.5), 1 << 20, "the object of L-1");

        Assert.Equal(AnyArrayType.ArrayOfDouble, values.Kind);
        Assert.Equal([10.5, 11.5, 12.5], values.Doubles.ToArray());
    }

    [Fact]
    public async Task An_array_whose_column_holds_the_wrong_number_of_values_is_refused_rather_than_sent()
    {
        var declaration = Assert.Single(EtpArrays.Read(
            new JsonObject { ["Arrays"] = new JsonArray(JsonNode.Parse("""{"Path":"RESQML/points","Type":"arrayOfDouble","Dimensions":[2,2],"Column":"depth"}""")) },
            "the object of L-1"));

        var bulk = await BulkAsync("depth", 1.0, 2.0, 3.0);
        var held = await Assert.ThrowsAsync<RecordHeldException>(
            () => EtpArrays.FromParquetAsync(declaration, bulk, 1 << 20, "the object of L-1"));
        Assert.Contains("2 x 2, which is 4 elements, and its column holds 3", held.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_array_past_what_one_delivery_reads_into_memory_is_refused_before_the_file_is_opened()
    {
        var declaration = Assert.Single(EtpArrays.Read(
            new JsonObject { ["Arrays"] = new JsonArray(JsonNode.Parse("""{"Path":"RESQML/points","Type":"arrayOfDouble","Dimensions":[1000000],"Column":"depth"}""")) },
            "the object of L-1"));

        var bulk = await BulkAsync("depth", 1.0);
        var held = await Assert.ThrowsAsync<RecordHeldException>(
            () => EtpArrays.FromParquetAsync(declaration, bulk, 1024, "the object of L-1"));
        Assert.Contains("past the 1024 bytes one delivery reads into memory", held.Message, StringComparison.Ordinal);
        Assert.Contains("deliver it as several arrays", held.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_dataspace_record_id_is_the_one_the_server_will_register()
    {
        Assert.Equal("dev:dataset--ETPDataspace:demo-study", EtpDataspaceRecord.Id(new Uri("wss://osdu.example.com/x"), "demo/study", "dev"));

        // A path the encoder cannot spell keeps its bytes as the server writes them.
        Assert.Equal("demo-study.2", EtpDataspaceRecord.UrlId("demo/study.2"));

        // Past 64 characters the server hashes the whole id behind its first 32 characters.
        var long_ = EtpDataspaceRecord.UrlId(new string('a', 40) + "/" + new string('b', 40));
        Assert.Equal(64, long_.Length);
        Assert.StartsWith(new string('a', 32), long_, StringComparison.Ordinal);
    }

    private static EtpObjectIdentity Read(string xml) => EtpObjectXml.Read(Encoding.UTF8.GetBytes(xml), "the object of L-1");

    private static string Xml(Guid uuid, string space, string? version, string type, bool citation = true) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <x:{type} xmlns:x="{space}" xmlns:eml="{EtpSamples.Eml20}" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:type="x:{type}" uuid="{uuid:D}"{(version is null ? string.Empty : $" schemaVersion=\"{version}\"")}>
          {(citation ? "<eml:Citation><eml:Title>Thing</eml:Title></eml:Citation>" : string.Empty)}
        </x:{type}>
        """;

    /// <summary>A bulk payload of one parquet file with one column of doubles.</summary>
    private static async Task<IPayloadSource> BulkAsync(string column, params double[] values)
    {
        var buffer = new MemoryStream();
        await ParquetFiles.WriteAsync(
            buffer,
            [(column, typeof(double))],
            values.Select(v => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { [column] = v }).ToList());
        return new MemoryBulk(buffer.ToArray());
    }

    private sealed class MemoryBulk(byte[] bytes) : IPayloadSource
    {
        public Task<IReadOnlyList<PayloadFile>> ListChunksAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PayloadFile>>([new PayloadFile(0, "bulk.parquet", bytes.Length)]);

        public Task<Stream> OpenAsync(PayloadFile file, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }
}
