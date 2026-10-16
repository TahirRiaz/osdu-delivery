using SqlFlow.Delivery.Engine.Protocols.Etp;
using SqlFlow.Delivery.Tests.Etp;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The module's ETP records against the pinned protocol (osdu/specs/reservoir-ddms/etp-1.2.avpr): for every type the
/// route uses, a value built from the schema is encoded by a codec that reads the schema as data, decoded by the
/// module's record, encoded again by that record, and decoded back by the schema-driven codec. A field in the wrong
/// place, of the wrong type, or missing shows up as a difference here, so the checked-in records and the pinned schema
/// cannot drift apart (osdu/specs/reservoir-ddms/INTEGRATION.md section 2.4).
/// </summary>
public class EtpSchemaTests
{
    /// <summary>Seeds enough to reach every union branch and both an empty and a filled block of every array and map.</summary>
    private static readonly int[] Seeds = [1, 2, 3, 5, 8, 13, 21, 34];

    public static TheoryData<string> EveryType()
    {
        var data = new TheoryData<string>();
        foreach (var name in EtpTypeCodecs.Names)
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryType))]
    public void A_record_of_the_module_reads_and_writes_exactly_what_the_pinned_schema_says(string name)
    {
        var schema = AvroProtocol.Etp12.Named(name);
        foreach (var seed in Seeds)
        {
            var sample = AvroCodec.Sample(schema, new Random(seed));

            using var fromSchema = new EtpWriter();
            AvroCodec.Write(schema, sample, fromSchema);
            var expected = fromSchema.Written.ToArray();

            var reader = new EtpReader(expected);
            var value = EtpTypeCodecs.Read(name, ref reader);
            Assert.True(reader.AtEnd, $"{name} (seed {seed}) left {reader.Remaining} of {expected.Length} bytes unread.");

            using var fromRecord = new EtpWriter();
            EtpTypeCodecs.Write(name, value, fromRecord);
            var written = fromRecord.Written.ToArray();

            var back = new EtpReader(written);
            var round = AvroCodec.Read(schema, ref back);
            Assert.True(back.AtEnd, $"{name} (seed {seed}) wrote {written.Length} bytes with {back.Remaining} left over.");
            Assert.True(AvroCodec.Same(schema, sample, round), $"{name} (seed {seed}) did not come back as it went in.");
        }
    }

    [Fact]
    public void Every_message_of_the_pinned_protocol_this_client_speaks_is_reachable_by_its_header()
    {
        foreach (var name in EtpTypeCodecs.Names.Where(n => n.Contains(".Protocol.", StringComparison.Ordinal)))
        {
            var schema = (AvroRecord)AvroProtocol.Etp12.Named(name);
            var sample = AvroCodec.Sample(schema, new Random(7));
            using var writer = new EtpWriter();
            AvroCodec.Write(schema, sample, writer);
            var bytes = writer.Written.ToArray();

            var message = Typed(name, bytes);
            var found = Read(message.Protocol, message.MessageType, bytes);
            Assert.NotNull(found);
            Assert.Equal(message.MessageName, found.MessageName);
            Assert.Equal(name.Split(".Protocol.")[^1], message.MessageName);
            Assert.Equal(message.MessageName, EtpMessages.Name(message.Protocol, message.MessageType));
        }
    }

    [Fact]
    public void A_header_this_client_does_not_implement_is_ignored_rather_than_failing_the_session()
    {
        Assert.Null(Read(4, 4242, []));
        Assert.Equal("protocol 4 message 4242", EtpMessages.Name(4, 4242));
    }

    private static IEtpMessage? Read(int protocol, int messageType, byte[] body)
    {
        var reader = new EtpReader(body);
        return EtpMessages.Read(protocol, messageType, ref reader);
    }

    /// <summary>The named message, read from <paramref name="bytes"/> by the module's own record.</summary>
    private static IEtpMessage Typed(string name, byte[] bytes)
    {
        var reader = new EtpReader(bytes);
        return (IEtpMessage)EtpTypeCodecs.Read(name, ref reader);
    }
}
