using System.IO.Compression;

namespace SqlFlow.Delivery.Engine.Protocols.Etp;

/// <summary>
/// The bits an ETP message header's <c>messageFlags</c> is a set of (osdu/specs/reservoir-ddms/INTEGRATION.md
/// section 2.2).
/// </summary>
public static class EtpMessageBits
{
    /// <summary>The final, or only, part of a message.</summary>
    public const int Final = 0x02;

    /// <summary>The body is gzip compressed (section 2.5).</summary>
    public const int CompressedBody = 0x08;

    /// <summary>The sender asks for an <c>Acknowledge</c>.</summary>
    public const int AcknowledgeReceipt = 0x10;

    /// <summary>An extension header follows, which the server does not support (section 2.6).</summary>
    public const int HeaderExtension = 0x20;
}

/// <summary>One ETP message as it came off the wire: its header and the body it carried.</summary>
public sealed record EtpFrame(MessageHeader Header, IEtpMessage? Body)
{
    public bool IsFinal => (Header.MessageFlags & EtpMessageBits.Final) != 0;

    /// <summary>The message's name, whether or not this client implements its body.</summary>
    public string Name => Body?.MessageName ?? EtpMessages.Name(Header.Protocol, Header.MessageType);
}

/// <summary>
/// Frames ETP messages: one WebSocket binary message carries the Avro header and then the body, with the body gzipped
/// when the session negotiated compression (osdu/specs/reservoir-ddms/INTEGRATION.md sections 2.1, 2.2 and 2.5). The
/// header is never compressed.
/// </summary>
public static class EtpFraming
{
    /// <summary>
    /// Bodies below this stay uncompressed, which is the rule the ETP server itself applies to what it sends
    /// (INTEGRATION.md section 2.5).
    /// </summary>
    public const int CompressAtBytes = 256;

    /// <summary>How much larger than the wire limit an inflated body may be, which is the server's own ratio.</summary>
    private const int InflateFactor = 10;

    /// <summary>
    /// Writes one message into <paramref name="writer"/>: the header, then the body, compressed when
    /// <paramref name="compress"/> and worth it. Returns the flags the header ended up with.
    /// </summary>
    public static int Write(EtpWriter writer, IEtpMessage message, long messageId, long correlationId, bool final, bool compress)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(message);

        using var body = new EtpWriter(writer.CeilingBytes);
        message.Write(body);

        var flags = final ? EtpMessageBits.Final : 0;
        byte[]? compressed = null;
        if (compress && body.Length >= CompressAtBytes)
        {
            compressed = Deflate(body.Written);
            if (compressed.Length < body.Length)
            {
                flags |= EtpMessageBits.CompressedBody;
            }
            else
            {
                compressed = null;
            }
        }

        new MessageHeader
        {
            Protocol = message.Protocol,
            MessageType = message.MessageType,
            CorrelationId = correlationId,
            MessageId = messageId,
            MessageFlags = flags,
        }.Write(writer);

        writer.WriteFixed(compressed is null ? body.Written : compressed);
        return flags;
    }

    /// <summary>
    /// Reads one message: the header, then the body, inflated when the header says so. A body this client does not
    /// implement comes back with a null body and its header, which is what lets a session ignore it.
    /// </summary>
    public static EtpFrame Read(ReadOnlySpan<byte> message, int maxBodyBytes)
    {
        var reader = new EtpReader(message);
        var header = MessageHeader.Read(ref reader);
        var body = message[reader.Position..];
        if ((header.MessageFlags & EtpMessageBits.CompressedBody) != 0)
        {
            var inflated = Inflate(body, maxBodyBytes);
            var inner = new EtpReader(inflated);
            return new EtpFrame(header, EtpMessages.Read(header.Protocol, header.MessageType, ref inner));
        }

        var plain = new EtpReader(body);
        return new EtpFrame(header, EtpMessages.Read(header.Protocol, header.MessageType, ref plain));
    }

    /// <summary>The gzip wrapper the ETP server deflates and inflates with (RFC 1952, window bits 15 | 16).</summary>
    private static byte[] Deflate(ReadOnlySpan<byte> body)
    {
        using var output = new MemoryStream(body.Length);
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(body);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Inflates a compressed body, refusing one that expands past what this session could ever carry, so a message
    /// crafted to expand without bound is a failed delivery rather than an exhausted node.
    /// </summary>
    private static byte[] Inflate(ReadOnlySpan<byte> body, int maxBodyBytes)
    {
        var ceiling = (long)maxBodyBytes * InflateFactor;
        using var source = new MemoryStream(body.ToArray(), writable: false);
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = gzip.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > ceiling)
            {
                throw new EtpFormatException($"A compressed ETP message inflated past {ceiling} bytes, which this session cannot carry.");
            }

            output.Write(buffer, 0, read);
        }
    }
}
