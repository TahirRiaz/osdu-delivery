using System.Text;
using SqlFlow.Delivery;
using SqlFlow.Delivery.Engine.Protocols.Etp;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The Avro binary layer under the ETP records (osdu/specs/reservoir-ddms/INTEGRATION.md section 2.4): the edges of
/// the variable-length integers, the byte order a UUID goes on the wire in, and the guards that make a truncated or
/// hostile message fail with a message rather than an allocation.
/// </summary>
public class EtpBinaryTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(1L)]
    [InlineData(63L)]
    [InlineData(64L)]
    [InlineData(-64L)]
    [InlineData(-65L)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void A_long_comes_back_as_it_went_in(long value)
    {
        using var writer = new EtpWriter();
        writer.WriteLong(value);
        var reader = new EtpReader(writer.Written);
        Assert.Equal(value, reader.ReadLong());
        Assert.True(reader.AtEnd);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void An_int_comes_back_as_it_went_in(int value)
    {
        using var writer = new EtpWriter();
        writer.WriteInt(value);
        var reader = new EtpReader(writer.Written);
        Assert.Equal(value, reader.ReadInt());
    }

    [Fact]
    public void A_long_that_does_not_fit_an_int_is_refused_rather_than_truncated()
    {
        using var writer = new EtpWriter();
        writer.WriteLong((long)int.MaxValue + 1);
        Assert.Throws<EtpFormatException>(() =>
        {
            var reader = new EtpReader(writer.Written);
            return reader.ReadInt();
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("dev")]
    [InlineData("eml:///dataspace('demo/study')")]
    [InlineData("éΩ中🚀")]
    public void A_string_crosses_as_utf8_with_its_byte_length_in_front(string value)
    {
        using var writer = new EtpWriter();
        writer.WriteString(value);
        Assert.Equal(Encoding.UTF8.GetByteCount(value), writer.Length - 1);
        var reader = new EtpReader(writer.Written);
        Assert.Equal(value, reader.ReadString());
        Assert.True(reader.AtEnd);
    }

    [Fact]
    public void A_uuid_crosses_in_the_rfc_4122_order_and_not_the_dotnet_one()
    {
        var uuid = new Guid("00112233-4455-6677-8899-aabbccddeeff");
        using var writer = new EtpWriter();
        writer.WriteUuid(uuid);

        Assert.Equal(
            new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff },
            writer.Written.ToArray());
        var reader = new EtpReader(writer.Written);
        Assert.Equal(uuid, reader.ReadUuid());
    }

    [Fact]
    public void An_empty_array_is_the_terminator_alone_and_a_filled_one_carries_its_count()
    {
        using var empty = new EtpWriter();
        empty.WriteDoubleArray(ReadOnlyMemory<double>.Empty);
        Assert.Equal(1, empty.Length);

        using var writer = new EtpWriter();
        double[] values = [1.5, -2.25, double.MaxValue];
        writer.WriteDoubleArray(values);
        var reader = new EtpReader(writer.Written);
        Assert.Equal(values, reader.ReadDoubleArray().ToArray());
        Assert.True(reader.AtEnd);
    }

    [Fact]
    public void A_block_the_server_sizes_in_bytes_is_read_like_any_other()
    {
        // A negative count carries the block's byte size, which the reader drops (section 2.4).
        using var writer = new EtpWriter();
        writer.WriteLong(-2);
        writer.WriteLong(16);
        writer.WriteDouble(1);
        writer.WriteDouble(2);
        writer.WriteBlockEnd();

        var reader = new EtpReader(writer.Written);
        Assert.Equal(new double[] { 1, 2 }, reader.ReadDoubleArray().ToArray());
    }

    [Fact]
    public void A_message_that_ends_early_is_refused_before_anything_is_allocated()
    {
        using var writer = new EtpWriter();
        writer.WriteLong(int.MaxValue);
        var truncated = writer.Written.ToArray();

        var byLength = Assert.Throws<EtpFormatException>(() =>
        {
            var reader = new EtpReader(truncated);
            return reader.ReadString();
        });
        Assert.Contains("bytes left to read", byLength.Message, StringComparison.Ordinal);

        var byCount = Assert.Throws<EtpFormatException>(() =>
        {
            var reader = new EtpReader(truncated);
            return reader.ReadBlockCount();
        });
        Assert.Contains("items with", byCount.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_block_of_fixed_width_elements_is_checked_against_the_bytes_that_are_left()
    {
        // Three elements is within the bytes that are left, and three doubles are not.
        using var writer = new EtpWriter();
        writer.WriteLong(3);
        writer.WriteDouble(1);
        var truncated = writer.Written.ToArray();

        var failure = Assert.Throws<EtpFormatException>(() =>
        {
            var reader = new EtpReader(truncated);
            return reader.ReadDoubleArray();
        });
        Assert.Contains("elements of 8 bytes", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_union_branch_and_an_enum_symbol_the_schema_does_not_define_are_refused()
    {
        using var writer = new EtpWriter();
        writer.WriteUnion(9);
        var bytes = writer.Written.ToArray();

        var union = Assert.Throws<EtpFormatException>(() =>
        {
            var reader = new EtpReader(bytes);
            return reader.ReadUnion(2);
        });
        Assert.Contains("branch 9", union.Message, StringComparison.Ordinal);

        var symbol = Assert.Throws<EtpFormatException>(() =>
        {
            var reader = new EtpReader(bytes);
            return reader.ReadEnum(2);
        });
        Assert.Contains("symbol 9", symbol.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_boolean_that_is_neither_zero_nor_one_is_refused()
    {
        var failure = Assert.Throws<EtpFormatException>(() =>
        {
            var reader = new EtpReader(new byte[] { 2 });
            return reader.ReadBoolean();
        });
        Assert.Contains("neither 0 nor 1", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_message_past_the_size_this_session_may_send_fails_with_the_size_in_the_message()
    {
        using var writer = new EtpWriter(ceilingBytes: 64);
        var failure = Assert.Throws<DeliveryException>(() => writer.WriteBytes(new byte[128]));
        Assert.Contains("64 bytes this session may send", failure.Message, StringComparison.Ordinal);
        Assert.Contains("slice the array", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_writer_grows_past_its_first_buffer_and_keeps_every_byte()
    {
        using var writer = new EtpWriter();
        var text = new string('x', 20_000);
        writer.WriteString(text);
        writer.WriteLong(42);

        var reader = new EtpReader(writer.Written);
        Assert.Equal(text, reader.ReadString());
        Assert.Equal(42, reader.ReadLong());
        Assert.True(reader.AtEnd);
    }

    [Fact]
    public void A_writer_starts_again_on_reset_and_refuses_to_write_once_returned()
    {
        var writer = new EtpWriter();
        writer.WriteLong(7);
        writer.Reset();
        Assert.Equal(0, writer.Length);
        writer.WriteLong(9);
        Assert.Equal(1, writer.Length);

        writer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => writer.WriteLong(1));
        writer.Dispose();
    }

    [Fact]
    public void A_negative_block_header_is_a_programming_error_rather_than_a_message()
    {
        using var writer = new EtpWriter();
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteBlockHeader(-1));
    }
}
