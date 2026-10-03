using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Operations;

/// <summary>
/// The connections to OSDU a person's reads keep between calls (a record read back, a probe, the explorer's searches): one
/// HTTP runtime per flow target, as the call resolves it, reused while the same target is asked again, so an access token is
/// fetched once in its lifetime and the connections stay open, rather than both for every read. A run keeps its own
/// connection for its own length and never shares one of these.
/// </summary>
/// <remarks>
/// <para>
/// A target is the same only when everything that reaches it is: the flow and its partition, its endpoint, credentials and
/// headers as the document writes them, its reliability, and the central configuration the call resolves its references
/// with. A change to any of them is another target, so an edited flow or configuration is never read through the old one.
/// A connection unused for <see cref="IdleLifetime"/>, or older than <see cref="MaxLifetime"/>, is retired, and disposed once
/// the last read holding it lets it go; a rotated secret is therefore read again within that time at the latest, and at
/// once on the 401 the old token draws.
/// </para>
/// <para>
/// A person is waiting on these reads, so their retries are a person's, not a run's (<see cref="Interactive"/>): at most two
/// attempts, a short backoff, no wait on a Retry-After, and a bounded timeout. An OSDU that does not answer is said in
/// seconds rather than after the minutes a delivery would keep trying.
/// </para>
/// </remarks>
public sealed class TargetClients : IDisposable
{
    /// <summary>How long a connection nobody reads through is kept.</summary>
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(10);

    /// <summary>How long a connection is kept at most, however often it is read through.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(30);

    /// <summary>The most targets kept at once; past it the one read longest ago is retired first.</summary>
    public const int MaxTargets = 64;

    /// <summary>The longest a person's read waits on OSDU for one answer.</summary>
    public const int InteractiveTimeoutSeconds = 60;

    private static readonly JsonSerializerOptions KeyOptions = new() { WriteIndented = false };

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly HttpMessageHandler? _transport;
    private readonly bool _allowLoopback;
    private bool _disposed;

    public TargetClients(TimeProvider time)
        : this(time, null, EngineContext.LoopbackAllowed)
    {
    }

    /// <summary>Connections sending through <paramref name="transport"/> instead of the network (the tests).</summary>
    internal TargetClients(TimeProvider time, HttpMessageHandler? transport, bool allowLoopback)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _transport = transport;
        _allowLoopback = allowLoopback;
    }

    /// <summary>
    /// <paramref name="reliability"/> as a person's read uses it: at most two attempts, a backoff of at most two seconds, no
    /// wait on a Retry-After, and at most <see cref="InteractiveTimeoutSeconds"/> for an answer. Everything else (the rate
    /// limit, TLS, the allowlist, the response ceiling) is the flow's own.
    /// </summary>
    public static FlowReliability Interactive(FlowReliability reliability)
    {
        ArgumentNullException.ThrowIfNull(reliability);
        return reliability with
        {
            Retry = reliability.Retry with
            {
                Attempts = Math.Clamp(reliability.Retry.Attempts, 1, 2),
                BaseDelayMs = Math.Min(reliability.Retry.BaseDelayMs, 500),
                MaxDelayMs = Math.Min(reliability.Retry.MaxDelayMs, 2_000),
                HonorRetryAfter = false,
            },
            TimeoutSeconds = Math.Clamp(reliability.TimeoutSeconds, 1, InteractiveTimeoutSeconds),
        };
    }

    /// <summary>
    /// A connection to <paramref name="flow"/>'s target, with its references resolved by <paramref name="context"/>: the one
    /// kept for that target, or a new one kept from now on. Dispose the lease when the read is done; the connection stays.
    /// </summary>
    public async Task<TargetLease> OpenAsync(EngineContext context, FlowDefinition flow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(flow);
        ObjectDisposedException.ThrowIf(_disposed, this);
        Sweep();

        if (KeyOf(context, flow) is not { } key)
        {
            // A target that cannot be told apart from another is not kept: this read has a connection of its own.
            return OneOff(context, flow, _transport, _allowLoopback);
        }

        while (true)
        {
            var entry = _entries.GetOrAdd(key, _ => new Entry(NewHttp(context, flow), _time.GetUtcNow()));
            if (!entry.TryAcquire(_time.GetUtcNow()))
            {
                // Retired between being found and being taken: it leaves the table, and the next pass makes a fresh one.
                _entries.TryRemove(KeyValuePair.Create(key, entry));
                continue;
            }

            return new TargetLease(entry, context, flow);
        }
    }

    /// <summary>A connection kept for no one: a read whose target is used once (a test's transport) disposes it with the lease.</summary>
    internal static TargetLease OneOff(EngineContext context, FlowDefinition flow, HttpMessageHandler? transport, bool allowLoopback)
    {
        var entry = new Entry(new HttpRuntime(Interactive(flow.Reliability), context.Secrets, context.Time, transport, allowLoopback), context.Time.GetUtcNow());
        entry.TryAcquire(context.Time.GetUtcNow());
        entry.Retire();
        return new TargetLease(entry, context, flow);
    }

    private HttpRuntime NewHttp(EngineContext context, FlowDefinition flow)
        => new(Interactive(flow.Reliability), context.Secrets, _time, _transport, _allowLoopback);

    /// <summary>Retires the connections idle or old past their lifetime, and the oldest past <see cref="MaxTargets"/>.</summary>
    private void Sweep()
    {
        var now = _time.GetUtcNow();
        foreach (var (key, entry) in _entries)
        {
            if (now - entry.LastUsed > IdleLifetime || now - entry.Created > MaxLifetime)
            {
                Retire(key, entry);
            }
        }

        var over = _entries.Count - MaxTargets;
        if (over > 0)
        {
            foreach (var (key, entry) in _entries.OrderBy(e => e.Value.LastUsed).Take(over).ToList())
            {
                Retire(key, entry);
            }
        }
    }

    private void Retire(string key, Entry entry)
    {
        if (_entries.TryRemove(KeyValuePair.Create(key, entry)))
        {
            entry.Retire();
        }
    }

    /// <summary>
    /// What makes two calls reach the same target: the flow and its partition, its target and reliability as the document
    /// writes them, and the central configuration its references resolve with, hashed so no value is kept in a key. Null
    /// for a target whose description cannot be written down, which is then never shared.
    /// </summary>
    private static string? KeyOf(EngineContext context, FlowDefinition flow)
    {
        try
        {
            return Describe(context, flow);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static string Describe(EngineContext context, FlowDefinition flow)
    {
        var configuration = string.Join('\n', context.Supplied.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));
        var described = string.Join(
            '\n',
            flow.Label,
            flow.Partition ?? string.Empty,
            JsonSerializer.Serialize(flow.Target, KeyOptions),
            JsonSerializer.Serialize(flow.Reliability, KeyOptions),
            configuration);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(described)));
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var (key, entry) in _entries)
        {
            Retire(key, entry);
        }
    }

    /// <summary>One kept connection: its runtime, the protocol and client made over it once, and who holds it now.</summary>
    internal sealed class Entry
    {
        private readonly Lock _gate = new();
        private int _holders;
        private bool _retired;
        private bool _disposed;
        private Task<IDeliveryProtocol>? _protocol;
        private Task<OsduHttpClient>? _client;

        public Entry(HttpRuntime http, DateTimeOffset created)
        {
            Http = http;
            Created = created;
            LastUsed = created;
        }

        public HttpRuntime Http { get; }

        public DateTimeOffset Created { get; }

        public DateTimeOffset LastUsed { get; private set; }

        /// <summary>Takes the connection for one read, unless it was retired.</summary>
        public bool TryAcquire(DateTimeOffset now)
        {
            lock (_gate)
            {
                if (_retired)
                {
                    return false;
                }

                _holders++;
                LastUsed = now;
                return true;
            }
        }

        /// <summary>Lets the connection go after a read; a retired one is disposed with its last holder.</summary>
        public void Release()
        {
            lock (_gate)
            {
                _holders--;
                DisposeIfDone();
            }
        }

        /// <summary>Takes the connection out of use: no new read takes it, and it is disposed once the last one lets it go.</summary>
        public void Retire()
        {
            lock (_gate)
            {
                _retired = true;
                DisposeIfDone();
            }
        }

        /// <summary>The flow's protocol over this connection, made by the first read that needs it and kept after.</summary>
        public Task<IDeliveryProtocol> ProtocolAsync(EngineContext context, FlowDefinition flow, CancellationToken ct)
        {
            lock (_gate)
            {
                // A protocol whose making failed is not kept, so the next read tries again rather than inherit the failure.
                if (_protocol is null || _protocol.IsFaulted || _protocol.IsCanceled)
                {
                    _protocol = context.Protocols.CreateAsync(flow, Http, context.Loggers, CancellationToken.None);
                }

                return _protocol.WaitAsync(ct);
            }
        }

        /// <summary>The client for the flow's endpoint, credentials and headers over this connection, made once.</summary>
        public Task<OsduHttpClient> ClientAsync(EngineContext context, FlowDefinition flow, CancellationToken ct)
        {
            lock (_gate)
            {
                if (_client is null || _client.IsFaulted || _client.IsCanceled)
                {
                    _client = ProtocolFactory.ClientAsync(Http, flow.Target.Endpoint, flow.Target.Auth, flow.Target.Headers, context.Secrets, CancellationToken.None);
                }

                return _client.WaitAsync(ct);
            }
        }

        private void DisposeIfDone()
        {
            if (_retired && _holders <= 0 && !_disposed)
            {
                _disposed = true;
                Http.Dispose();
            }
        }
    }
}

/// <summary>One read's hold on a kept connection to a flow's target: the protocol and the client over it, let go on dispose.</summary>
public sealed class TargetLease : IDisposable
{
    private readonly TargetClients.Entry _entry;
    private readonly EngineContext _context;
    private readonly FlowDefinition _flow;
    private bool _released;

    internal TargetLease(TargetClients.Entry entry, EngineContext context, FlowDefinition flow)
    {
        _entry = entry;
        _context = context;
        _flow = flow;
    }

    /// <summary>The flow's own protocol over the connection: what a read back and a probe go through.</summary>
    public Task<IDeliveryProtocol> ProtocolAsync(CancellationToken ct) => _entry.ProtocolAsync(_context, _flow, ct);

    /// <summary>The client for the flow's endpoint, credentials and headers: what a search of the platform goes through.</summary>
    public Task<OsduHttpClient> ClientAsync(CancellationToken ct) => _entry.ClientAsync(_context, _flow, ct);

    public void Dispose()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        _entry.Release();
    }
}
