using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using SqlFlow.Delivery.Engine.Protocols.Etp;

namespace SqlFlow.Delivery.Tests.Etp;

/// <summary>
/// The Reservoir DDMS's ETP server as osdu/specs/reservoir-ddms/INTEGRATION.md reads its code, over a real WebSocket on
/// the loopback interface: the upgrade with its subprotocol and headers (section 1.3), the session negotiation
/// (section 3), and the framing, correlation and per-item error shapes (sections 2 and 8.1). The dataspace,
/// transaction, store and array behaviour is in the other half of this class.
/// </summary>
internal sealed partial class FakeEtpServer : IAsyncDisposable
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopped = new();
    private readonly List<Task> _connections = [];
    private readonly Task _accepting;

    public FakeEtpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Uri = new Uri($"ws://127.0.0.1:{port}/api/reservoir-ddms-etp/v2/");
        _accepting = Task.Run(() => AcceptAsync(_stopped.Token));
    }

    /// <summary>Where this server answers, as a flow would name it.</summary>
    public Uri Uri { get; }

    /// <summary>An HTTP status to fail the upgrade with instead of accepting it (section 8.5).</summary>
    public int? RefuseUpgradeWith { get; set; }

    /// <summary>The subprotocol to answer with; null answers with none, which is not an ETP endpoint.</summary>
    public string? SubProtocol { get; set; } = EtpSessionOptions.SubProtocol;

    /// <summary>The headers of the last upgrade this server accepted, by name.</summary>
    public IReadOnlyDictionary<string, string> UpgradeHeaders => _upgradeHeaders;

    /// <summary>The server's own wire limit, which narrows the session's (section 3.2).</summary>
    public int MaxMessageBytes { get; set; } = 1_000_000;

    /// <summary>Whether the server takes a gzip offer.</summary>
    public bool Compression { get; set; } = true;

    /// <summary>The protocols the server serves; one left out is one the session must refuse to open over.</summary>
    public IReadOnlyList<int> Served { get; set; } =
        [EtpProtocols.Core, EtpProtocols.Discovery, EtpProtocols.Store, EtpProtocols.DataArray, EtpProtocols.Transaction, EtpProtocols.Dataspace, EtpProtocols.DataspaceOsdu];

    /// <summary>Every message the server received, in order, for a test to assert the sequence a route sends.</summary>
    public IReadOnlyList<EtpFrame> Received => _received;

    /// <summary>Set to answer the next message of a kind with this failure instead of handling it.</summary>
    public ConcurrentDictionary<string, ErrorInfo> FailNext { get; } = new(StringComparer.Ordinal);

    /// <summary>Whether a body arrived compressed, which is what proves the negotiation reached the wire.</summary>
    public bool SawCompressedBody { get; private set; }

    /// <summary>The client connected right now, for a test that has the server speak first (a ping, a close).</summary>
    public Peer? Connected { get; private set; }

    private readonly ConcurrentDictionary<string, string> _upgradeHeaders = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<EtpFrame> _received = [];

    public async ValueTask DisposeAsync()
    {
        await _stopped.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try
        {
            await _accepting.ConfigureAwait(false);
            await Task.WhenAll(_connections).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A test that ends mid-session leaves a socket half read; that is not a failure of the server.
        }

        _stopped.Dispose();
    }

    private async Task AcceptAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            lock (_connections)
            {
                _connections.Add(Task.Run(() => ServeAsync(client, ct), CancellationToken.None));
            }
        }
    }

    [SuppressMessage(
        "Security",
        "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "SHA-1 over the client key and the fixed GUID is the WebSocket opening handshake (RFC 6455 section 4.2.2); it is not used for security here or anywhere.")]
    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            var stream = client.GetStream();
            var request = await UpgradeRequestAsync(stream, ct).ConfigureAwait(false);
            if (request is null)
            {
                return;
            }

            foreach (var (name, value) in request)
            {
                _upgradeHeaders[name] = value;
            }

            if (RefuseUpgradeWith is { } status)
            {
                await WriteAsync(stream, $"HTTP/1.1 {status} Refused\r\nConnection: close\r\nContent-Length: 0\r\n\r\n", ct).ConfigureAwait(false);
                return;
            }

            if (!request.TryGetValue("Sec-WebSocket-Key", out var key))
            {
                await WriteAsync(stream, "HTTP/1.1 400 Bad Request\r\nConnection: close\r\nContent-Length: 0\r\n\r\n", ct).ConfigureAwait(false);
                return;
            }

            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
            var response = new StringBuilder("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n");
            response.Append("Sec-WebSocket-Accept: ").Append(accept).Append("\r\n");
            if (SubProtocol is { } subProtocol)
            {
                response.Append("Sec-WebSocket-Protocol: ").Append(subProtocol).Append("\r\n");
            }

            response.Append("\r\n");
            await WriteAsync(stream, response.ToString(), ct).ConfigureAwait(false);

            using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
            {
                IsServer = true,
                SubProtocol = SubProtocol,
                KeepAliveInterval = TimeSpan.Zero,
            });

            await PumpAsync(socket, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The upgrade request's headers, or null when the client sent nothing usable.</summary>
    private static async Task<Dictionary<string, string>?> UpgradeRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var text = new StringBuilder();
        var buffer = new byte[1];
        while (!text.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            text.Append((char)buffer[0]);
            if (text.Length > 16 * 1024)
            {
                return null;
            }
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.ToString().Split("\r\n").Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        return headers;
    }

    private static Task WriteAsync(NetworkStream stream, string text, CancellationToken ct)
        => stream.WriteAsync(Encoding.ASCII.GetBytes(text), ct).AsTask();

    private async Task PumpAsync(WebSocket socket, CancellationToken ct)
    {
        using var peer = new Peer(socket, this);
        Connected = peer;
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var message = await ReceiveAsync(socket, ct).ConfigureAwait(false);
            if (message is null)
            {
                return;
            }

            var (buffer, length) = message.Value;
            try
            {
                var frame = EtpFraming.Read(buffer.AsSpan(0, length), MaxMessageBytes);
                if ((frame.Header.MessageFlags & EtpMessageBits.CompressedBody) != 0)
                {
                    SawCompressedBody = true;
                }

                lock (_received)
                {
                    _received.Add(frame);
                }

                if (!await HandleAsync(frame, peer, ct).ConfigureAwait(false))
                {
                    return;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private static async Task<(byte[] Buffer, int Length)?> ReceiveAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var length = 0;
        try
        {
            while (true)
            {
                if (length == buffer.Length)
                {
                    var grown = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    buffer.AsSpan(0, length).CopyTo(grown);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = grown;
                }

                var result = await socket.ReceiveAsync(buffer.AsMemory(length), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    return null;
                }

                length += result.Count;
                if (result.EndOfMessage)
                {
                    return (buffer, length);
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            ArrayPool<byte>.Shared.Return(buffer);
            return null;
        }
    }

    /// <summary>The session messages every test needs; everything else is the store half of this class.</summary>
    private async Task<bool> HandleAsync(EtpFrame frame, Peer peer, CancellationToken ct)
    {
        switch (frame.Body)
        {
            case RequestSession request:
                await OpenAsync(request, frame, peer, ct).ConfigureAwait(false);
                return true;
            case Ping ping:
                await peer.SendAsync(new Pong { CurrentDateTime = ping.CurrentDateTime }, frame.Header.MessageId, ct).ConfigureAwait(false);
                return true;
            case CloseSession:
                await peer.CloseAsync(ct).ConfigureAwait(false);
                return false;
            case null:
                await peer.FailAsync(frame, EtpErrorCodes.InvalidMessageType, "Unknown message type", ct).ConfigureAwait(false);
                return true;
            default:
                return await HandleStoreAsync(frame, peer, ct).ConfigureAwait(false);
        }
    }

    private async Task OpenAsync(RequestSession request, EtpFrame frame, Peer peer, CancellationToken ct)
    {
        var accepted = request.RequestedProtocols.Where(p => Served.Contains(p.Protocol)).ToList();
        if (accepted.Count == 0)
        {
            await peer.FailAsync(frame, EtpErrorCodes.NoSupportedProtocols, "None of the requested protocols are supported.", ct).ConfigureAwait(false);
            return;
        }

        var objects = request.SupportedDataObjects
            .Where(o => o.QualifiedType.Contains("resqml20.", StringComparison.Ordinal) || o.QualifiedType.Contains("eml20.", StringComparison.Ordinal))
            .ToList();
        if (objects.Count == 0)
        {
            await peer.FailAsync(frame, EtpErrorCodes.NoSupportedDataObjectTypes, "None of the requested dataobject types are supported.", ct).ConfigureAwait(false);
            return;
        }

        var asked = request.EndpointCapabilities.TryGetValue("MaxWebSocketMessagePayloadSize", out var size) ? size.Number : null;
        var negotiated = asked is { } value ? Math.Min(value, MaxMessageBytes) : MaxMessageBytes;
        MaxMessageBytes = (int)negotiated;
        peer.Compressed = Compression && request.SupportedCompression.Contains("gzip", StringComparer.Ordinal);

        await peer.SendAsync(
            new OpenSession
            {
                ApplicationName = "open-etp-server",
                ApplicationVersion = "1.3.0",
                ServerInstanceId = Guid.NewGuid(),
                SupportedProtocols = accepted,
                SupportedDataObjects = objects,
                SupportedCompression = peer.Compressed ? "gzip" : string.Empty,
                SupportedFormats = ["xml"],
                CurrentDateTime = EtpTime.Microseconds(DateTimeOffset.UtcNow),
                EarliestRetainedChangeTime = 0,
                SessionId = Guid.NewGuid(),
                EndpointCapabilities = new Dictionary<string, DataValue>(StringComparer.Ordinal)
                {
                    ["MaxWebSocketMessagePayloadSize"] = DataValue.Of(negotiated),
                },
            },
            frame.Header.MessageId,
            ct).ConfigureAwait(false);
    }

    /// <summary>One connected client, and the ids and flags the server answers it with.</summary>
    internal sealed class Peer : IDisposable
    {
        private readonly WebSocket _socket;
        private readonly FakeEtpServer _server;
        private readonly SemaphoreSlim _sending = new(1, 1);
        private long _lastId = -1;

        public Peer(WebSocket socket, FakeEtpServer server)
        {
            _socket = socket;
            _server = server;
        }

        /// <summary>Whether this session negotiated gzip, which is what lets the server compress a body.</summary>
        public bool Compressed { get; set; }

        /// <summary>Sends one response part, correlated to the request, with FIN unless the caller says otherwise.</summary>
        public async Task SendAsync(IEtpMessage body, long correlationId, CancellationToken ct, bool final = true)
        {
            await _sending.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var writer = new EtpWriter(Math.Max(64, _server.MaxMessageBytes));
                EtpFraming.Write(writer, body, Interlocked.Add(ref _lastId, 2), correlationId, final, Compressed);
                await _socket.SendAsync(writer.Written.ToArray(), WebSocketMessageType.Binary, endOfMessage: true, ct).ConfigureAwait(false);
            }
            finally
            {
                _sending.Release();
            }
        }

        /// <summary>The whole request failed: one scalar error, with FIN and no response after it (section 8.1).</summary>
        public Task FailAsync(EtpFrame request, int code, string message, CancellationToken ct)
            => SendAsync(new ProtocolFailure { Error = new ErrorInfo { Code = code, Message = message } }, request.Header.MessageId, ct);

        /// <summary>Some keys failed: the errors go first, without FIN, and the response follows (section 8.1).</summary>
        public Task PartlyFailedAsync(EtpFrame request, IReadOnlyDictionary<string, ErrorInfo> errors, CancellationToken ct)
            => SendAsync(new ProtocolFailure { Errors = errors }, request.Header.MessageId, ct, final: false);

        public void Dispose() => _sending.Dispose();

        public async Task CloseAsync(CancellationToken ct)
        {
            try
            {
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
            {
                // The client may have gone first.
            }
        }
    }
}
