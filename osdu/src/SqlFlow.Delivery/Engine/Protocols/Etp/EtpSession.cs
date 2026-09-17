using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Reflection;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Diagnostics;
using SqlFlow.Delivery.Http;

namespace SqlFlow.Delivery.Engine.Protocols.Etp;

/// <summary>What one ETP session announces about itself and how long it waits (INTEGRATION.md sections 3.2 and 8.6).</summary>
public sealed record EtpSessionOptions
{
    /// <summary>The WebSocket subprotocol an ETP 1.2 endpoint answers on; an endpoint that refuses it is not one.</summary>
    public const string SubProtocol = "etp12.energistics.org";

    /// <summary>The largest WebSocket payload this side will send or accept, before the server's own maximum narrows it.</summary>
    public int MaxMessageBytes { get; init; } = EtpWriter.DefaultCeilingBytes;

    /// <summary>How long one request waits for the part of its reply that carries FIN.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(300);

    /// <summary>How long the session may sit idle before it pings, which keeps a long transaction's session alive.</summary>
    public TimeSpan KeepAlive { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Whether to offer gzip. Offering it also lets the server compress what it sends back (section 2.5).</summary>
    public bool Compression { get; init; } = true;

    /// <summary>The data object types the session declares; the server keeps only those naming <c>resqml20.</c> or <c>eml20.</c>.</summary>
    public IReadOnlyList<string> DataObjectTypes { get; init; } = ["resqml20.*", "eml20.*"];

    /// <summary>The protocols the session requests, beyond Core, which it always requests.</summary>
    public IReadOnlyList<int> Protocols { get; init; } = [EtpProtocols.Discovery, EtpProtocols.Store, EtpProtocols.DataArray, EtpProtocols.Transaction, EtpProtocols.Dataspace, EtpProtocols.DataspaceOsdu];
}

/// <summary>The ETP protocol numbers this client speaks (INTEGRATION.md section 4.1).</summary>
public static class EtpProtocols
{
    public const int Core = 0;
    public const int Discovery = 3;
    public const int Store = 4;
    public const int DataArray = 9;
    public const int Transaction = 18;
    public const int Dataspace = 24;
    public const int DataspaceOsdu = 2424;
}

/// <summary>
/// Everything one request got back: the response parts, in the order they arrived, and the errors the server reported,
/// which accumulate across the parts of a multi-part reply (INTEGRATION.md section 8.1).
/// </summary>
public sealed record EtpReply(string Request, IReadOnlyList<IEtpMessage> Parts, ErrorInfo? Error, IReadOnlyDictionary<string, ErrorInfo> Errors)
{
    /// <summary>The single response part of the expected type, or the server's error when it sent none.</summary>
    public T Part<T>()
        where T : class, IEtpMessage
    {
        Failed();
        foreach (var part in Parts)
        {
            if (part is T typed)
            {
                return typed;
            }
        }

        throw new DeliveryException($"The Reservoir DDMS answered {Request} with {Described()}, and not with a {typeof(T).Name}.");
    }

    /// <summary>Every response part of a type, which is how a multi-part reply is read.</summary>
    public IEnumerable<T> All<T>()
        where T : class, IEtpMessage
        => Parts.OfType<T>();

    /// <summary>Throws when the server failed the request as a whole; per-item errors are the caller's to read.</summary>
    public void Failed()
    {
        if (Error is { } error)
        {
            throw new EtpProtocolException(error.Code, error.Message, Request);
        }

        if (Parts.Count == 0 && Errors.Count > 0)
        {
            var first = Errors.First();
            throw new EtpProtocolException(first.Value.Code, $"{first.Key}: {first.Value.Message}", Request);
        }
    }

    private string Described()
        => Parts.Count == 0 ? "nothing" : string.Join(", ", Parts.Select(p => p.MessageName));
}

/// <summary>
/// One ETP 1.2 session over a WebSocket: the upgrade with the subprotocol and headers the endpoint requires, the
/// negotiated session, one receive loop that correlates every reply to its request until the part carrying FIN, the
/// keep-alive ping, and the close (osdu/specs/reservoir-ddms/INTEGRATION.md sections 1.3, 2 and 3).
/// </summary>
public sealed class EtpSession : IAsyncDisposable
{
    /// <summary>One id per process, as the session's client instance (section 3.2), never a fixed value.</summary>
    private static readonly Guid Instance = Guid.NewGuid();

    private static readonly string Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1";

    private readonly ConcurrentDictionary<long, Pending> _pending = new();
    private readonly SemaphoreSlim _sending = new(1, 1);
    private readonly CancellationTokenSource _stopped = new();
    private readonly WebSocket _socket;
    private readonly EtpSessionOptions _options;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly Uri _endpoint;

    private Task _pump = Task.CompletedTask;
    private Task _keepAlive = Task.CompletedTask;
    private long _lastId;
    private long _lastSentTicks;
    private Exception? _broken;

    private EtpSession(WebSocket socket, Uri endpoint, EtpSessionOptions options, ILogger log, TimeProvider time)
    {
        _socket = socket;
        _endpoint = endpoint;
        _options = options;
        _log = log;
        _time = time;
        MaxMessageBytes = options.MaxMessageBytes;
        Protocols = new HashSet<int>();
    }

    /// <summary>The size limit this session settled on: the smaller of what each side allows (section 3.2).</summary>
    public int MaxMessageBytes { get; private set; }

    /// <summary>Whether the server took the gzip offer, which is what lets a body go compressed.</summary>
    public bool Compressed { get; private set; }

    /// <summary>The session id the server minted, which its own logs are keyed by.</summary>
    public Guid SessionId { get; private set; }

    /// <summary>The server's name and version, as it introduced itself.</summary>
    public string Server { get; private set; } = string.Empty;

    /// <summary>The protocols the server accepted. A protocol not in here is one this session must not use.</summary>
    public IReadOnlySet<int> Protocols { get; private set; }

    /// <summary>Whether the session is still usable: neither closed nor broken by the transport.</summary>
    public bool IsOpen => _broken is null && !_stopped.IsCancellationRequested && _socket.State == WebSocketState.Open;

    /// <summary>
    /// Connects, negotiates and returns an open session. <paramref name="headers"/> carries the endpoint's own headers,
    /// already resolved, including <c>Authorization</c> and <c>data-partition-id</c>.
    /// </summary>
    public static async Task<EtpSession> OpenAsync(
        Uri endpoint,
        IReadOnlyDictionary<string, string> headers,
        HttpMessageInvoker invoker,
        UrlGuard guard,
        EtpSessionOptions options,
        ILogger log,
        TimeProvider? time = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(invoker);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);
        var clock = time ?? TimeProvider.System;

        // The guard's rules are about hosts and addresses, so a WebSocket URL is checked as the HTTP URL it upgrades from.
        guard.Check(AsHttp(endpoint));

        var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(EtpSessionOptions.SubProtocol);
        socket.Options.CollectHttpResponseDetails = true;
        foreach (var (name, value) in headers)
        {
            socket.Options.SetRequestHeader(name, value);
        }

        var began = clock.GetTimestamp();
        try
        {
            await socket.ConnectAsync(endpoint, invoker, ct).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            socket.Dispose();
            DeliveryMetrics.RequestEnded("WSS", endpoint.Host, Result(socket.HttpStatusCode), clock.GetElapsedTime(began));
            throw Upgrade(endpoint, socket.HttpStatusCode, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            socket.Dispose();
            DeliveryMetrics.RequestEnded("WSS", endpoint.Host, "transport", clock.GetElapsedTime(began));
            throw new DeliveryException($"The Reservoir DDMS at {endpoint} could not be reached: {ex.Message}", ex);
        }

        DeliveryMetrics.RequestEnded("WSS", endpoint.Host, "2xx", clock.GetElapsedTime(began));
        if (!string.Equals(socket.SubProtocol, EtpSessionOptions.SubProtocol, StringComparison.Ordinal))
        {
            socket.Dispose();
            throw new DeliveryException(
                $"The endpoint at {endpoint} accepted the connection under subprotocol '{socket.SubProtocol}' instead of '{EtpSessionOptions.SubProtocol}', so it is not an ETP 1.2 endpoint.");
        }

        var session = new EtpSession(socket, endpoint, options, log, clock);
        session.Start();
        try
        {
            await session.NegotiateAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return session;
    }

    /// <summary>Sends one request and waits for every part of its reply, up to the part carrying FIN.</summary>
    public Task<EtpReply> CallAsync(IEtpMessage request, CancellationToken ct = default)
        => CallAsync(request, [], ct);

    /// <summary>
    /// Sends one request whose body continues in further messages (the <c>Store.Chunk</c> sequence of a chunked put,
    /// section 4.5): the request goes without FIN, each continuation correlates to it, and the last carries FIN.
    /// </summary>
    public async Task<EtpReply> CallAsync(IEtpMessage request, IReadOnlyList<IEtpMessage> continuation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(continuation);
        var pending = new Pending(request.MessageName);
        var id = 0L;
        await _sending.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Usable(request.MessageName);
            id = Interlocked.Add(ref _lastId, 2);
            _pending[id] = pending;
            await SendAsync(request, id, correlationId: 0, final: continuation.Count == 0, ct).ConfigureAwait(false);
            for (var i = 0; i < continuation.Count; i++)
            {
                await SendAsync(continuation[i], Interlocked.Add(ref _lastId, 2), id, final: i == continuation.Count - 1, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }
        finally
        {
            _sending.Release();
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopped.Token);
        try
        {
            return await pending.Completion.WaitAsync(_options.RequestTimeout, _time, linked.Token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _pending.TryRemove(id, out _);
            throw new DeliveryException(
                $"The Reservoir DDMS did not answer {request.MessageName} within {_options.RequestTimeout.TotalSeconds:0} seconds. The transaction it belongs to is rolled back when the session ends.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _pending.TryRemove(id, out _);
            throw Broken(request.MessageName);
        }
    }

    /// <summary>Ends the session the way the server expects, then closes the socket (section 3.4).</summary>
    public async Task CloseAsync(string reason, CancellationToken ct = default)
    {
        if (!IsOpen)
        {
            return;
        }

        try
        {
            await _sending.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await SendAsync(new CloseSession { Reason = reason }, Interlocked.Add(ref _lastId, 2), 0, final: true, ct).ConfigureAwait(false);
            }
            finally
            {
                _sending.Release();
            }

            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
            // The session is ending either way; a server that closed first is not a failure of this delivery.
            _log.LogDebug("The ETP session at {Endpoint} could not be closed cleanly: {Reason}", _endpoint, ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopped.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_pump, _keepAlive).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogDebug("The ETP session at {Endpoint} ended with {Reason}", _endpoint, ex.Message);
        }

        Fail(_broken ?? new DeliveryException("The ETP session was closed."));
        _socket.Dispose();
        _sending.Dispose();
        _stopped.Dispose();
    }

    /// <summary>The HTTP URL a WebSocket URL upgrades from, which is what the address rules are written against.</summary>
    private static Uri AsHttp(Uri endpoint) => endpoint.Scheme switch
    {
        "ws" => new UriBuilder(endpoint) { Scheme = "http" }.Uri,
        "wss" => new UriBuilder(endpoint) { Scheme = "https" }.Uri,
        _ => endpoint,
    };

    private static string Result(System.Net.HttpStatusCode? status)
        => status is null ? "transport" : $"{(int)status / 100}xx";

    /// <summary>What an upgrade that did not become a WebSocket means (section 8.5).</summary>
    private static DeliveryException Upgrade(Uri endpoint, System.Net.HttpStatusCode? status, WebSocketException inner) => (int?)status switch
    {
        401 or 403 => new DeliveryException(
            $"The Reservoir DDMS at {endpoint} refused the connection with HTTP {(int)status!}: the token the flow presents is missing, expired, or lacks the entitlements this partition requires.", inner),
        412 => new DeliveryException(
            $"The Reservoir DDMS at {endpoint} refused the connection with HTTP 412: the upgrade did not carry the '{EtpSessionOptions.SubProtocol}' subprotocol, or the endpoint is not an ETP 1.2 endpoint.", inner),
        400 => new DeliveryException(
            $"The Reservoir DDMS at {endpoint} refused the connection with HTTP 400: the data partition header is missing or names a partition this deployment does not serve.", inner),
        404 => new DeliveryException($"The Reservoir DDMS at {endpoint} answered HTTP 404: the endpoint path is not the ETP one.", inner),
        null => new DeliveryException($"The Reservoir DDMS at {endpoint} could not be reached: {inner.Message}", inner),
        _ => new DeliveryException($"The Reservoir DDMS at {endpoint} refused the connection with HTTP {(int)status!}.", inner),
    };

    private void Start()
    {
        _pump = Task.Run(() => PumpAsync(_stopped.Token));
        _keepAlive = Task.Run(() => KeepAliveAsync(_stopped.Token));
    }

    private async Task NegotiateAsync(CancellationToken ct)
    {
        var protocols = new List<SupportedProtocol>
        {
            Requested(EtpProtocols.Core, "server"),
        };
        foreach (var protocol in _options.Protocols.Distinct().Where(p => p != EtpProtocols.Core))
        {
            protocols.Add(Requested(protocol, "store"));
        }

        var now = EtpTime.Microseconds(_time.GetUtcNow());
        var request = new RequestSession
        {
            ApplicationName = "OSDU Delivery",
            ApplicationVersion = Version,
            ClientInstanceId = Instance,
            RequestedProtocols = protocols,
            SupportedDataObjects = [.. _options.DataObjectTypes.Select(type => new SupportedDataObject { QualifiedType = type })],
            SupportedCompression = _options.Compression ? ["gzip"] : [],
            SupportedFormats = ["xml"],
            CurrentDateTime = now,
            EarliestRetainedChangeTime = now,
            ServerAuthorizationRequired = false,
            EndpointCapabilities = new Dictionary<string, DataValue>(StringComparer.Ordinal)
            {
                ["MaxWebSocketMessagePayloadSize"] = DataValue.Of((long)_options.MaxMessageBytes),
            },
        };

        var opened = (await CallAsync(request, ct).ConfigureAwait(false)).Part<OpenSession>();
        SessionId = opened.SessionId;
        Server = $"{opened.ApplicationName} {opened.ApplicationVersion}".Trim();
        Compressed = opened.SupportedCompression.Equals("gzip", StringComparison.OrdinalIgnoreCase);
        Protocols = new HashSet<int>(opened.SupportedProtocols.Select(p => p.Protocol));
        if (opened.EndpointCapabilities.TryGetValue("MaxWebSocketMessagePayloadSize", out var size) && size.Number is { } negotiated && negotiated > 0)
        {
            MaxMessageBytes = (int)Math.Min(negotiated, _options.MaxMessageBytes);
        }

        var missing = _options.Protocols.Distinct().Where(p => !Protocols.Contains(p)).ToList();
        if (missing.Count > 0)
        {
            throw new DeliveryException(
                $"The Reservoir DDMS at {_endpoint} does not serve ETP protocol(s) {string.Join(", ", missing)}, which this route needs. It serves {string.Join(", ", Protocols.Order())}.");
        }

        _log.LogInformation(
            "ETP session {Session} open at {Endpoint} with {Server}: {Bytes} byte messages, compression {Compression}",
            SessionId, _endpoint, Server, MaxMessageBytes, Compressed ? "gzip" : "off");
    }

    private static SupportedProtocol Requested(int protocol, string role) => new()
    {
        Protocol = protocol,
        ProtocolVersion = new ProtocolVersion { Major = 1, Minor = 2 },
        Role = role,
    };

    private async Task SendAsync(IEtpMessage message, long id, long correlationId, bool final, CancellationToken ct)
    {
        using var writer = new EtpWriter(MaxMessageBytes);
        EtpFraming.Write(writer, message, id, correlationId, final, Compressed);
        var bytes = ArrayPool<byte>.Shared.Rent(writer.Length);
        try
        {
            writer.Written.CopyTo(bytes);
            await _socket.SendAsync(bytes.AsMemory(0, writer.Length), WebSocketMessageType.Binary, endOfMessage: true, ct).ConfigureAwait(false);
            Interlocked.Exchange(ref _lastSentTicks, _time.GetTimestamp());
        }
        catch (WebSocketException ex)
        {
            throw Break(ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var received = await ReceiveAsync(ct).ConfigureAwait(false);
                if (received is null)
                {
                    break;
                }

                var (buffer, length) = received.Value;
                try
                {
                    Dispatch(EtpFraming.Read(buffer.AsSpan(0, length), MaxMessageBytes), ct);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            Fail(_broken ?? new DeliveryException($"The Reservoir DDMS at {_endpoint} closed the session."));
        }
        catch (OperationCanceledException)
        {
            Fail(new DeliveryException($"The ETP session at {_endpoint} was cancelled."));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Fail(Break(ex));
        }
    }

    /// <summary>One whole WebSocket message, reassembled from its frames, or null when the peer closed.</summary>
    private async Task<(byte[] Buffer, int Length)?> ReceiveAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(MaxMessageBytes, 64 * 1024));
        var length = 0;
        try
        {
            while (true)
            {
                if (length == buffer.Length)
                {
                    if (length >= MaxMessageBytes)
                    {
                        throw new DeliveryException(
                            $"The Reservoir DDMS sent a message past the {MaxMessageBytes} bytes this session negotiated.");
                    }

                    var grown = ArrayPool<byte>.Shared.Rent(Math.Min(MaxMessageBytes, buffer.Length * 2));
                    buffer.AsSpan(0, length).CopyTo(grown);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = grown;
                }

                var result = await _socket.ReceiveAsync(buffer.AsMemory(length), ct).ConfigureAwait(false);
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
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    private void Dispatch(EtpFrame frame, CancellationToken ct)
    {
        switch (frame.Body)
        {
            case Ping ping:
                _ = Task.Run(() => PongAsync(ping, frame.Header.MessageId, ct), CancellationToken.None);
                return;
            case CloseSession closed:
                _broken ??= new DeliveryException(
                    $"The Reservoir DDMS at {_endpoint} closed the session: {(string.IsNullOrEmpty(closed.Reason) ? "no reason given" : closed.Reason)}");
                _stopped.Cancel();
                return;
            case Acknowledge:
                return;
        }

        if (frame.Header.CorrelationId == 0)
        {
            if (frame.Body is ProtocolFailure fatal)
            {
                Fail(Failure(fatal, frame.Name));
            }
            else
            {
                _log.LogDebug("The ETP session at {Endpoint} received {Message}, which answers no request", _endpoint, frame.Name);
            }

            return;
        }

        if (!_pending.TryGetValue(frame.Header.CorrelationId, out var pending))
        {
            _log.LogDebug(
                "The ETP session at {Endpoint} received {Message} for request {Id}, which is no longer waiting", _endpoint, frame.Name, frame.Header.CorrelationId);
            return;
        }

        pending.Add(frame);
        if (frame.IsFinal && _pending.TryRemove(frame.Header.CorrelationId, out _))
        {
            pending.Complete();
        }
    }

    private async Task PongAsync(Ping ping, long correlationId, CancellationToken ct)
    {
        try
        {
            await _sending.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await SendAsync(new Pong { CurrentDateTime = ping.CurrentDateTime }, Interlocked.Add(ref _lastId, 2), correlationId, final: true, ct).ConfigureAwait(false);
            }
            finally
            {
                _sending.Release();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogDebug("The ETP session at {Endpoint} could not answer a ping: {Reason}", _endpoint, ex.Message);
        }
    }

    /// <summary>
    /// Pings when the session has been idle, so a transaction held open while the engine reads or renders does not
    /// age out of the server or an intervening gateway (section 8.6).
    /// </summary>
    private async Task KeepAliveAsync(CancellationToken ct)
    {
        if (_options.KeepAlive <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            using var timer = new PeriodicTimer(_options.KeepAlive, _time);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (!IsOpen || _time.GetElapsedTime(Interlocked.Read(ref _lastSentTicks)) < _options.KeepAlive)
                {
                    continue;
                }

                await CallAsync(new Ping { CurrentDateTime = EtpTime.Microseconds(_time.GetUtcNow()) }, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The session is ending.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogDebug("The ETP keep-alive at {Endpoint} stopped: {Reason}", _endpoint, ex.Message);
        }
    }

    private void Usable(string request)
    {
        if (_broken is not null)
        {
            throw Broken(request);
        }

        ObjectDisposedException.ThrowIf(_stopped.IsCancellationRequested, this);
        if (_socket.State != WebSocketState.Open)
        {
            throw new DeliveryException($"The ETP session at {_endpoint} is {_socket.State} and cannot send {request}.");
        }
    }

    private DeliveryException Broken(string request)
        => new($"The ETP session at {_endpoint} ended before {request} was answered: {_broken?.Message}", _broken!);

    private DeliveryException Break(Exception ex)
    {
        var failure = ex as DeliveryException ?? new DeliveryException($"The ETP session at {_endpoint} failed: {ex.Message}", ex);
        _broken ??= failure;
        return failure;
    }

    /// <summary>Ends every request still waiting, so a dropped connection surfaces at once rather than at the timeout.</summary>
    private void Fail(Exception failure)
    {
        _broken ??= failure;
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var pending))
            {
                pending.Break(_broken);
            }
        }
    }

    private static EtpProtocolException Failure(ProtocolFailure exception, string request)
    {
        if (exception.Error is { } error)
        {
            return new EtpProtocolException(error.Code, error.Message, request);
        }

        var first = exception.Errors.FirstOrDefault();
        return new EtpProtocolException(
            first.Value?.Code ?? EtpErrorCodes.InvalidState,
            first.Value is null ? "the server reported a failure without naming it" : $"{first.Key}: {first.Value.Message}",
            request);
    }

    /// <summary>One request waiting for its reply, collecting the parts and the errors until the part carrying FIN.</summary>
    private sealed class Pending
    {
        private readonly TaskCompletionSource<EtpReply> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<IEtpMessage> _parts = [];
        private readonly Dictionary<string, ErrorInfo> _errors = new(StringComparer.Ordinal);
        private readonly string _request;
        private ErrorInfo? _error;

        public Pending(string request) => _request = request;

        public Task<EtpReply> Completion => _completion.Task;

        public void Add(EtpFrame frame)
        {
            if (frame.Body is ProtocolFailure failure)
            {
                _error ??= failure.Error;
                foreach (var (key, info) in failure.Errors)
                {
                    _errors[key] = info;
                }

                return;
            }

            if (frame.Body is not null)
            {
                _parts.Add(frame.Body);
            }
        }

        public void Complete() => _completion.TrySetResult(new EtpReply(_request, _parts, _error, _errors));

        public void Break(Exception failure) => _completion.TrySetException(failure);
    }
}
