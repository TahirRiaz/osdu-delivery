using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The addresses the delivery nodes may reach (docs/go-live-map.md, SEC-1): link-local, metadata and platform addresses
/// never, loopback and private ranges only when allowed, whether an address is written in a URL, carried inside an IPv6
/// address, or what a host name resolves to when the connection opens.
/// </summary>
public sealed class NetworkPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1", "a loopback address")]
    [InlineData("::1", "a loopback address")]
    [InlineData("169.254.169.254", "a link-local address")]
    [InlineData("fe80::1", "a link-local address")]
    [InlineData("0.0.0.0", "an unspecified address")]
    [InlineData("::", "an unspecified address")]
    [InlineData("224.0.0.1", "a multicast or reserved address")]
    [InlineData("255.255.255.255", "a multicast or reserved address")]
    [InlineData("ff02::1", "a multicast address")]
    [InlineData("168.63.129.16", "an address of the cloud platform itself")]
    [InlineData("100.100.100.200", "an address of the cloud platform itself")]
    [InlineData("fd00:ec2::254", "an address of the cloud platform itself")]
    [InlineData("10.0.0.5", "a private address")]
    [InlineData("172.20.1.1", "a private address")]
    [InlineData("192.168.1.1", "a private address")]
    [InlineData("100.64.0.1", "a private address")]
    [InlineData("fd12:3456::1", "a private address")]
    [InlineData("::ffff:10.0.0.5", "a private address")]
    [InlineData("::ffff:169.254.169.254", "a link-local address")]
    [InlineData("64:ff9b::a9fe:a9fe", "a link-local address")]
    [InlineData("2002:a9fe:a9fe::1", "a link-local address")]
    [InlineData("20.50.1.1", null)]
    [InlineData("172.32.0.1", null)]
    [InlineData("2603:1030:20e:3::1", null)]
    public void Addresses_the_nodes_may_not_reach_are_refused_with_the_reason(string address, string? refusal)
    {
        var reason = new NetworkPolicy().Refusal(IPAddress.Parse(address));
        if (refusal is null)
        {
            Assert.Null(reason);
        }
        else
        {
            Assert.NotNull(reason);
            Assert.StartsWith(refusal, reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Loopback_and_the_listed_private_ranges_are_reachable_and_nothing_unblocks_metadata()
    {
        var local = new NetworkPolicy { AllowLoopback = true };
        Assert.Null(local.Refusal(IPAddress.Loopback));
        Assert.Null(local.Refusal(IPAddress.IPv6Loopback));
        Assert.NotNull(local.Refusal(IPAddress.Parse("10.0.0.5")));

        var vnet = new NetworkPolicy { PrivateNetworks = NetworkPolicy.ParseNetworks("10.20.0.0/16, fd12:3456::/48", "test") };
        Assert.Null(vnet.Refusal(IPAddress.Parse("10.20.7.9")));
        Assert.Null(vnet.Refusal(IPAddress.Parse("::ffff:10.20.7.9")));
        Assert.Null(vnet.Refusal(IPAddress.Parse("fd12:3456::9")));
        Assert.NotNull(vnet.Refusal(IPAddress.Parse("10.21.0.1")));
        Assert.NotNull(vnet.Refusal(IPAddress.Loopback));

        var everything = new NetworkPolicy { PrivateNetworks = NetworkPolicy.ParseNetworks("0.0.0.0/0;::/0 169.254.0.0/16", "test") };
        Assert.Null(everything.Refusal(IPAddress.Parse("192.168.3.3")));
        Assert.NotNull(everything.Refusal(IPAddress.Parse("169.254.169.254")));
        Assert.NotNull(everything.Refusal(IPAddress.Parse("168.63.129.16")));
        Assert.NotNull(everything.Refusal(IPAddress.Loopback));
    }

    [Fact]
    public void A_listed_range_is_a_CIDR_range_or_an_address()
    {
        var parsed = NetworkPolicy.ParseNetworks("10.20.0.0/16,\tfd12:3456::/48 ; 192.168.5.7", NetworkPolicy.PrivateNetworksVariable);
        Assert.Equal(["10.20.0.0/16", "fd12:3456::/48", "192.168.5.7/32"], parsed.Select(n => n.ToString()));
        Assert.Empty(NetworkPolicy.ParseNetworks("  ", NetworkPolicy.PrivateNetworksVariable));
        Assert.Empty(NetworkPolicy.ParseNetworks(null, NetworkPolicy.PrivateNetworksVariable));

        var refused = Assert.Throws<DeliveryException>(() => NetworkPolicy.ParseNetworks("10.20.0.0/16, vnet-a", NetworkPolicy.PrivateNetworksVariable));
        Assert.Equal(
            "SQLFLOW_DELIVERY_PRIVATE_NETWORKS lists 'vnet-a', which is not a CIDR range such as 10.20.0.0/16 or fd12:3456::/48, nor an address.",
            refused.Message);
        Assert.Throws<DeliveryException>(() => NetworkPolicy.ParseNetworks("10.20.0.0/40", NetworkPolicy.PrivateNetworksVariable));
    }

    [Fact]
    public void A_name_connects_only_to_the_addresses_it_resolves_to_that_the_policy_reaches()
    {
        var addresses = HttpClientBuilder.Reachable(
            "osdu.example.com", [IPAddress.Parse("10.0.0.5"), IPAddress.Parse("20.50.1.1"), IPAddress.Parse("169.254.169.254")], new NetworkPolicy());
        Assert.Equal([IPAddress.Parse("20.50.1.1")], addresses);

        var refused = Assert.Throws<UrlRefusedException>(() => HttpClientBuilder.Reachable(
            "rebind.example.com", [IPAddress.Parse("169.254.169.254"), IPAddress.Parse("10.0.0.5")], new NetworkPolicy()));
        Assert.StartsWith("'rebind.example.com' resolves only to addresses the delivery nodes may not reach: 169.254.169.254 (a link-local address", refused.Message, StringComparison.Ordinal);
        Assert.Contains("10.0.0.5 (a private address, which the nodes reach only when the deployment lists its range under SQLFLOW_DELIVERY_PRIVATE_NETWORKS)", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_connection_opens_only_to_an_address_the_policy_reaches()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var reliability = new FlowReliability();

        using (var refusing = HttpClientBuilder.Build(reliability, new NetworkPolicy()))
        {
            var failure = await Assert.ThrowsAsync<HttpRequestException>(() => refusing.GetAsync(new Uri($"http://127.0.0.1:{port}/probe")));
            Assert.IsType<UrlRefusedException>(failure.InnerException);
            Assert.False(listener.Pending());
        }

        using var allowing = HttpClientBuilder.Build(reliability, new NetworkPolicy { AllowLoopback = true });
        var serve = ServeOnceAsync(listener);
        using var response = await allowing.GetAsync(new Uri($"http://127.0.0.1:{port}/probe"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
        Assert.Equal("GET /probe HTTP/1.1", await serve);
    }

    [Fact]
    public void A_flow_that_turns_tls_verification_off_needs_the_deployment_to_allow_it()
    {
        var insecure = new FlowReliability { VerifyTls = false };
        var original = Environment.GetEnvironmentVariable(HttpClientBuilder.AllowInsecureTlsVariable);
        try
        {
            Environment.SetEnvironmentVariable(HttpClientBuilder.AllowInsecureTlsVariable, null);
            var refused = Assert.Throws<UrlRefusedException>(() => HttpClientBuilder.Build(insecure, new NetworkPolicy()));
            Assert.Contains("reliability.verifyTls: false", refused.Message, StringComparison.Ordinal);
            Assert.Contains(HttpClientBuilder.AllowInsecureTlsVariable, refused.Message, StringComparison.Ordinal);

            // The deployment agrees, as it does for loopback and private ranges, and the client is built.
            Environment.SetEnvironmentVariable(HttpClientBuilder.AllowInsecureTlsVariable, "true");
            using var built = HttpClientBuilder.Build(insecure, new NetworkPolicy());
            Assert.NotNull(built);

            // A flow that verifies needs no switch at all.
            Environment.SetEnvironmentVariable(HttpClientBuilder.AllowInsecureTlsVariable, null);
            using var verifying = HttpClientBuilder.Build(new FlowReliability(), new NetworkPolicy());
            Assert.NotNull(verifying);
        }
        finally
        {
            Environment.SetEnvironmentVariable(HttpClientBuilder.AllowInsecureTlsVariable, original);
        }
    }

    /// <summary>Answers one request with 200 and returns its request line.</summary>
    private static async Task<string> ServeOnceAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        var received = new StringBuilder();
        var buffer = new byte[1024];
        while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
            {
                break;
            }

            received.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        var answer = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
        await stream.WriteAsync(answer);
        await stream.FlushAsync();
        return received.ToString().Split("\r\n")[0];
    }
}

/// <summary>
/// The URL guard on what a flow and a service name (docs/go-live-map.md, SEC-1 and SEC-2): names that mean loopback,
/// addresses inside IPv6 literals, the private ranges a deployment lists, and redirects, which the executor follows itself,
/// checking every hop and keeping credentials to the host they were meant for.
/// </summary>
public sealed class RedirectGuardTests
{
    private static HttpRuntime Runtime(FakeHttpHandler handler, IReadOnlyList<string>? allowlist = null, int attempts = 1)
        => new(
            new FlowReliability { Retry = new FlowRetry { Attempts = attempts, BaseDelayMs = 1, MaxDelayMs = 1 }, UrlAllowlist = allowlist ?? [] },
            new SecretResolver([new EnvSecretProvider()]),
            new TestClock(),
            handler,
            privateNetworks: []);

    private static Func<HttpRequestMessage> Request(HttpMethod method, string url, string? body = null) => () =>
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");
        request.Headers.TryAddWithoutValidation("data-partition-id", "opendes");
        request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", "gateway-key");
        request.Headers.TryAddWithoutValidation(OsduCorrelation.HeaderName, "corr-1");
        return request;
    };

    private static HttpResponseMessage Redirect(int status, string location)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static FakeHttpHandler At(FakeHttpHandler handler, string host, HttpMethod method, string path, Func<int, HttpResponseMessage> respond)
        => handler.OnMatch(r => r.RequestUri!.Host == host && r.Method == method && r.RequestUri.AbsolutePath == path, respond);

    [Fact]
    public void Names_and_addresses_that_mean_loopback_or_a_private_host_are_refused_unless_allowed()
    {
        var guard = new UrlGuard([]);
        Assert.Throws<UrlRefusedException>(() => guard.Check(new Uri("http://localhost:8080/x")));
        Assert.Throws<UrlRefusedException>(() => guard.Check(new Uri("http://api.localhost/x")));
        Assert.Throws<UrlRefusedException>(() => guard.Check(new Uri("http://[::1]/x")));
        Assert.Throws<UrlRefusedException>(() => guard.Check(new Uri("http://[::ffff:10.0.0.5]/x")));
        Assert.Throws<UrlRefusedException>(() => guard.Check(new Uri("http://[fe80::1]/x")));
        new UrlGuard([], allowLoopback: true).Check(new Uri("http://[::1]:5000/x"));

        var vnet = new UrlGuard([], new NetworkPolicy { PrivateNetworks = NetworkPolicy.ParseNetworks("10.20.0.0/16", "test") });
        vnet.Check(new Uri("https://10.20.1.5/api/storage/v2/records"));
        Assert.Throws<UrlRefusedException>(() => vnet.Check(new Uri("https://10.21.0.1/")));
        Assert.Throws<UrlRefusedException>(() => vnet.Check(new Uri("http://169.254.169.254/latest")));

        // A signed URL's credential sits in its query string, which no refusal repeats.
        var signed = Assert.Throws<UrlRefusedException>(() => guard.Check(new Uri("http://10.0.0.9/container/blob?sv=2024&sig=SECRETSIG")));
        Assert.DoesNotContain("SECRETSIG", signed.Message, StringComparison.Ordinal);
        Assert.Contains("a private address", signed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_303_on_the_same_host_turns_a_post_into_a_get_and_keeps_the_credentials()
    {
        var handler = new FakeHttpHandler();
        At(handler, "a.example.com", HttpMethod.Post, "/start", _ => Redirect(303, "/next"));
        At(handler, "a.example.com", HttpMethod.Get, "/next", _ => FakeHttpHandler.Json(HttpStatusCode.OK, """{"ok":true}"""));
        using var runtime = Runtime(handler);

        var result = await runtime.Data.SendAsync(Request(HttpMethod.Post, "https://a.example.com/start", """{"a":1}"""));

        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.Equal(2, handler.Calls.Count);
        var followed = handler.Calls[1];
        Assert.Equal(HttpMethod.Get, followed.Method);
        Assert.Null(followed.Body);
        Assert.Equal("Bearer secret-token", followed.Headers["Authorization"]);
        Assert.Equal("opendes", followed.Headers["data-partition-id"]);
    }

    [Fact]
    public async Task A_307_keeps_the_method_and_sends_the_body_again()
    {
        var handler = new FakeHttpHandler();
        At(handler, "a.example.com", HttpMethod.Put, "/upload", _ => Redirect(307, "https://a.example.com/upload-2"));
        At(handler, "a.example.com", HttpMethod.Put, "/upload-2", _ => FakeHttpHandler.Json(HttpStatusCode.Created, "{}"));
        using var runtime = Runtime(handler);

        var result = await runtime.Data.SendAsync(Request(HttpMethod.Put, "https://a.example.com/upload", """{"rows":[1,2]}"""));

        Assert.Equal(HttpStatusCode.Created, result.Status);
        Assert.Equal(HttpMethod.Put, handler.Calls[1].Method);
        Assert.Equal("""{"rows":[1,2]}""", handler.Calls[1].Body);
    }

    [Fact]
    public async Task A_redirect_to_another_host_carries_no_credentials_or_flow_headers()
    {
        var handler = new FakeHttpHandler();
        At(handler, "a.example.com", HttpMethod.Get, "/file", _ => Redirect(302, "https://cdn.example.net/file"));
        At(handler, "cdn.example.net", HttpMethod.Get, "/file", _ => FakeHttpHandler.Json(HttpStatusCode.OK, "{}"));
        using var runtime = Runtime(handler);

        await runtime.Data.SendAsync(Request(HttpMethod.Get, "https://a.example.com/file"));

        var followed = handler.Calls[1].Headers;
        Assert.False(followed.ContainsKey("Authorization"));
        Assert.False(followed.ContainsKey("data-partition-id"));
        Assert.False(followed.ContainsKey("Ocp-Apim-Subscription-Key"));
        Assert.Equal("corr-1", followed[OsduCorrelation.HeaderName]);
    }

    [Fact]
    public async Task An_upgrade_to_https_on_the_same_host_keeps_the_credentials_and_a_relative_location_resolves_against_the_hop()
    {
        var handler = new FakeHttpHandler();
        handler.OnMatch(r => r.RequestUri!.Scheme == "http", _ => Redirect(301, "https://a.example.com/api/v1/items"));
        At(handler, "a.example.com", HttpMethod.Get, "/api/v1/items", _ => Redirect(302, "../v2/items"));
        At(handler, "a.example.com", HttpMethod.Get, "/api/v2/items", _ => FakeHttpHandler.Json(HttpStatusCode.OK, "[]"));
        using var runtime = Runtime(handler);

        await runtime.Data.SendAsync(Request(HttpMethod.Get, "http://a.example.com/api/v1/items"));

        Assert.Equal(["http://a.example.com/api/v1/items", "https://a.example.com/api/v1/items", "https://a.example.com/api/v2/items"], handler.Calls.Select(c => c.Uri.ToString()));
        Assert.All(handler.Calls, c => Assert.Equal("Bearer secret-token", c.Headers["Authorization"]));
    }

    [Theory]
    [InlineData("https://169.254.169.254/latest/meta-data", "a link-local address", null)]
    [InlineData("https://10.1.2.3/internal", "a private address", null)]
    [InlineData("https://localhost/admin", "a loopback address", null)]
    [InlineData("https://evil.example.org/x", "is not in the url allowlist", "*.example.com")]
    [InlineData("http://a.example.com/plain", "a redirect from https to http is refused", null)]
    [InlineData("ftp://a.example.com/x", "only http and https are allowed", null)]
    public async Task A_redirect_the_guard_refuses_is_not_followed(string location, string reason, string? allowed)
    {
        var handler = new FakeHttpHandler();
        At(handler, "a.example.com", HttpMethod.Get, "/start", _ => Redirect(302, location));
        handler.OnMatch(_ => true, _ => FakeHttpHandler.Json(HttpStatusCode.OK, "{}"));
        using var runtime = Runtime(handler, allowed is null ? null : [allowed], attempts: 3);

        var refused = await Assert.ThrowsAsync<UrlRefusedException>(() => runtime.Data.SendAsync(Request(HttpMethod.Get, "https://a.example.com/start")));

        Assert.Contains(reason, refused.Message, StringComparison.Ordinal);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Redirects_stop_after_five_hops()
    {
        var handler = new FakeHttpHandler();
        At(handler, "a.example.com", HttpMethod.Get, "/loop", _ => Redirect(302, "/loop"));
        using var runtime = Runtime(handler);

        var failure = await Assert.ThrowsAsync<DeliveryException>(() => runtime.Data.SendAsync(Request(HttpMethod.Get, "https://a.example.com/loop")));

        Assert.Equal("https://a.example.com/loop was redirected more than 5 times; the last redirect named https://a.example.com/loop.", failure.Message);
        Assert.Equal(HttpExecutor.MaxRedirects + 1, handler.Calls.Count);
    }

    [Fact]
    public async Task A_redirect_status_the_caller_takes_as_an_answer_is_returned_and_one_without_a_location_fails()
    {
        var handler = new FakeHttpHandler();
        At(handler, "storage.example.com", HttpMethod.Put, "/resumable", _ =>
        {
            var incomplete = Redirect(308, "https://storage.example.com/elsewhere");
            incomplete.Headers.TryAddWithoutValidation("Range", "bytes=0-99");
            return incomplete;
        });
        At(handler, "a.example.com", HttpMethod.Get, "/odd", _ => new HttpResponseMessage(HttpStatusCode.Redirect));
        using var runtime = Runtime(handler);

        var answered = await runtime.Data.SendAsync(Request(HttpMethod.Put, "https://storage.example.com/resumable", "part"), new HashSet<int> { 308 });
        Assert.Equal((HttpStatusCode)308, answered.Status);
        Assert.Single(handler.Calls);

        var failed = await Assert.ThrowsAsync<OsduStatusException>(() => runtime.Data.SendAsync(Request(HttpMethod.Get, "https://a.example.com/odd")));
        Assert.Equal(302, failed.StatusCode);
    }

    [Fact]
    public async Task A_connection_the_policy_refused_is_not_retried()
    {
        var handler = new FakeHttpHandler();
        handler.OnMatch(_ => true, _ => throw new HttpRequestException(
            "connect failed", new UrlRefusedException("'rebind.example.com' resolves only to addresses the delivery nodes may not reach: 169.254.169.254 (a link-local address)")));
        using var runtime = Runtime(handler, attempts: 4);

        var refused = await Assert.ThrowsAsync<UrlRefusedException>(() => runtime.Data.SendAsync(Request(HttpMethod.Get, "https://rebind.example.com/x"), idempotent: true));

        Assert.StartsWith("'rebind.example.com' resolves only to addresses", refused.Message, StringComparison.Ordinal);
        Assert.Single(handler.Calls);
    }
}
