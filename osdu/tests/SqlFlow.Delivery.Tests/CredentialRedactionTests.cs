using System.Net;
using System.Text;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What a message, a log line or a stored error may say about a URL or a credential: a signed URL of any of the three
/// clouds loses its signature and its signer, a URL its user info, and a free-text message every value named as a secret.
/// </summary>
public class CredentialRedactionTests
{
    private const string S3Signed =
        "https://bucket.s3.amazonaws.com/landing/a.las?X-Amz-Algorithm=AWS4-HMAC-SHA256&X-Amz-Credential=AKIAEXAMPLEKEY%2F20261009%2Fus-east-1%2Fs3%2Faws4_request"
        + "&X-Amz-Date=20261009T120000Z&X-Amz-Expires=3600&X-Amz-Security-Token=FwoGZXIvYXdzEXAMPLETOKEN&X-Amz-SignedHeaders=host&X-Amz-Signature=0123abcdefsignature";

    private const string GcsSigned =
        "https://storage.googleapis.com/bucket/landing/a.las?X-Goog-Algorithm=GOOG4-RSA-SHA256&X-Goog-Credential=loader%40project.iam.gserviceaccount.com%2F20261009%2Fauto%2Fstorage%2Fgoog4_request"
        + "&X-Goog-Date=20261009T120000Z&X-Goog-Expires=900&X-Goog-SignedHeaders=host&X-Goog-Signature=9876fedcbagoogsignature";

    private const string AzureSigned =
        "https://account.blob.core.windows.net/landing/a.las?sv=2023-11-03&se=2026-10-09T13%3A00%3A00Z&sr=b&sp=cw&sig=AzureSignatureValue%3D";

    [Theory]
    [InlineData(S3Signed, "AKIAEXAMPLEKEY", "FwoGZXIvYXdzEXAMPLETOKEN", "0123abcdefsignature")]
    [InlineData(GcsSigned, "loader%40project", "9876fedcbagoogsignature", "loader%40project")]
    [InlineData(AzureSigned, "AzureSignatureValue", "2023-11-03", "cw")]
    public void A_signed_url_in_a_message_keeps_no_signature_or_signer(string url, string first, string second, string third)
    {
        var message = HeaderRedaction.RedactMessage($"HTTP 403 Forbidden from PUT {url}: the signature does not match");

        Assert.DoesNotContain(first, message, StringComparison.Ordinal);
        Assert.DoesNotContain(second, message, StringComparison.Ordinal);
        Assert.DoesNotContain(third, message, StringComparison.Ordinal);
        Assert.Contains("the signature does not match", message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_parts_of_a_signed_url_that_hold_no_credential_are_kept()
    {
        var message = HeaderRedaction.RedactMessage(S3Signed);

        Assert.Contains("X-Amz-Algorithm=AWS4-HMAC-SHA256", message, StringComparison.Ordinal);
        Assert.Contains("X-Amz-Expires=3600", message, StringComparison.Ordinal);
        Assert.Contains("X-Amz-Signature=***", message, StringComparison.Ordinal);
        Assert.Contains("X-Amz-Credential=***", message, StringComparison.Ordinal);
        Assert.Contains("X-Amz-Security-Token=***", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Version_two_signed_urls_lose_their_signature_and_signer()
    {
        var message = HeaderRedaction.RedactMessage(
            "GET https://bucket.s3.amazonaws.com/a?AWSAccessKeyId=AKIAOLDSTYLE&Expires=1700000000&Signature=v2signature and "
            + "https://storage.googleapis.com/b/a?GoogleAccessId=loader@project.iam.gserviceaccount.com&Expires=1700000000&Signature=gcsv2signature");

        Assert.DoesNotContain("AKIAOLDSTYLE", message, StringComparison.Ordinal);
        Assert.DoesNotContain("v2signature", message, StringComparison.Ordinal);
        Assert.DoesNotContain("loader@project", message, StringComparison.Ordinal);
        Assert.DoesNotContain("gcsv2signature", message, StringComparison.Ordinal);
        Assert.Contains("Expires=1700000000", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_password_in_a_url_an_authorization_header_or_a_connection_string_is_redacted()
    {
        var message = HeaderRedaction.RedactMessage(
            "The proxy tunnel request to proxy 'http://proxyuser:proxypass@proxy.example.org:8080/' failed; "
            + "sent Authorization: Basic dXNlcjpwYXNzd29yZA== and Server=db;Password=dbpass;Database=delivery");

        Assert.DoesNotContain("proxypass", message, StringComparison.Ordinal);
        Assert.DoesNotContain("dXNlcjpwYXNzd29yZA", message, StringComparison.Ordinal);
        Assert.DoesNotContain("dbpass", message, StringComparison.Ordinal);
        Assert.Contains("http://***@proxy.example.org:8080/", message, StringComparison.Ordinal);
        Assert.Contains("Authorization: Basic ***", message, StringComparison.Ordinal);
        Assert.Contains("Database=delivery", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_fields_named_as_secrets_are_redacted_and_the_rest_kept()
    {
        var message = HeaderRedaction.RedactMessage(
            "{\"token_type\":\"Bearer\",\"id_token\":\"idtok\",\"SecretAccessKey\":\"awssecret\",\"SessionToken\":\"awssession\",\"client_assertion\":\"jwt\\\"quoted\",\"kind\":\"osdu:wks:x:1.0.0\"}");

        Assert.DoesNotContain("idtok", message, StringComparison.Ordinal);
        Assert.DoesNotContain("awssecret", message, StringComparison.Ordinal);
        Assert.DoesNotContain("awssession", message, StringComparison.Ordinal);
        Assert.DoesNotContain("quoted", message, StringComparison.Ordinal);
        Assert.Contains("\"token_type\":\"Bearer\"", message, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"osdu:wks:x:1.0.0\"", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://user:pass@osdu.example.org:8443/api/storage/v2/records?sig=abc#frag", "https://osdu.example.org:8443/api/storage/v2/records")]
    [InlineData(S3Signed, "https://bucket.s3.amazonaws.com/landing/a.las")]
    [InlineData("https://osdu.example.org/api/storage/v2/records", "https://osdu.example.org/api/storage/v2/records")]
    [InlineData("wss://osdu.example.org/api/reservoir-ddms-etp/v2/?token=abc", "wss://osdu.example.org/api/reservoir-ddms-etp/v2/")]
    [InlineData("ftp://exporter:pass@collector:4317/v1/metrics?api_key=keyvalue#frag", "ftp://collector:4317/v1/metrics")]
    public void A_url_is_described_without_its_user_info_query_or_fragment(string url, string described)
    {
        Assert.Equal(described, HeaderRedaction.DescribeUrl(url));
        Assert.Equal(described, HeaderRedaction.DescribeUrl(new Uri(url)));
    }

    [Theory]
    [InlineData("/api/storage/v2/records?sig=abc", "/api/storage/v2/records")]
    [InlineData("${env:OSDU_URL}/api", "${env:OSDU_URL}/api")]
    [InlineData("", "")]
    public void A_relative_url_keeps_what_comes_before_its_query(string url, string described)
    {
        Assert.Equal(described, HeaderRedaction.DescribeUrl(url));
        if (url.Length > 0)
        {
            Assert.Equal(described, HeaderRedaction.DescribeUrl(new Uri(url, UriKind.Relative)));
        }
    }

    [Fact]
    public void Nothing_is_described_for_no_url()
    {
        Assert.Equal(string.Empty, HeaderRedaction.DescribeUrl((Uri?)null));
        Assert.Equal(string.Empty, HeaderRedaction.DescribeUrl((string?)null));
    }

    [Theory]
    [InlineData("client_secret")]
    [InlineData("clientSecret")]
    [InlineData("password")]
    [InlineData("refresh_token")]
    [InlineData("subject_token")]
    [InlineData("client_assertion")]
    [InlineData("api_key")]
    [InlineData("AccountKey")]
    [InlineData("x-amz-security-token")]
    [InlineData("X-Goog-Signature")]
    [InlineData("Proxy-Authorization")]
    public void A_name_that_holds_a_secret_is_one(string name) => Assert.True(HeaderRedaction.IsSecretName(name));

    [Theory]
    [InlineData("client_id")]
    [InlineData("username")]
    [InlineData("scope")]
    [InlineData("grant_type")]
    [InlineData("token_type")]
    [InlineData("subject_token_type")]
    [InlineData("audience")]
    [InlineData("data-partition-id")]
    [InlineData("AppKey")]
    [InlineData("x-ms-blob-type")]
    [InlineData("")]
    public void An_identifier_or_a_setting_is_not_a_secret(string name) => Assert.False(HeaderRedaction.IsSecretName(name));

    [Theory]
    [InlineData("Authorization", true)]
    [InlineData("X-Access-Token", true)]
    [InlineData("api-key", true)]
    [InlineData("x-amz-security-token", true)]
    [InlineData("Ocp-Apim-Subscription-Key", true)]
    [InlineData("data-partition-id", false)]
    [InlineData("x-ms-blob-type", false)]
    [InlineData("correlation-id", false)]
    public void A_header_named_as_a_secret_is_sensitive(string name, bool sensitive)
        => Assert.Equal(sensitive, HeaderRedaction.IsSensitive(name));
}

/// <summary>Every error the HTTP layer raises names its URL without the query string a signed URL carries its credential in.</summary>
public class HttpErrorRedactionTests
{
    private static HttpRuntime Runtime(FakeHttpHandler handler, long maxResponseBytes = 64L * 1024 * 1024)
        => new(
            new FlowReliability { Retry = new FlowRetry { Attempts = 2, BaseDelayMs = 1, MaxDelayMs = 2 }, TimeoutSeconds = 5, MaxResponseBytes = maxResponseBytes },
            new SecretResolver([new EnvSecretProvider()]), new TestClock(), handler, allowLoopback: true);

    [Fact]
    public async Task An_oversized_response_names_its_url_without_the_signature()
    {
        var body = new string('x', (int)(1.5 * 1024 * 1024));
        var handler = new FakeHttpHandler().On(HttpMethod.Get, "/landing/a.las", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/plain") });
        using var runtime = Runtime(handler, maxResponseBytes: 1024 * 1024);

        var ex = await Assert.ThrowsAsync<DeliveryException>(() => runtime.Data.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, "http://localhost/landing/a.las?X-Amz-Credential=AKIAEXAMPLEKEY&X-Amz-Signature=0123abcdefsignature")));

        Assert.Contains("Response from http://localhost/landing/a.las exceeds the 1 MB limit", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("AKIAEXAMPLEKEY", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("0123abcdefsignature", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("?", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_transport_failure_quoting_a_proxy_password_is_redacted()
    {
        var handler = new FakeHttpHandler().On(
            HttpMethod.Get, "/records",
            _ => throw new HttpRequestException("The proxy tunnel request to proxy 'http://proxyuser:proxypass@proxy.example.org:8080/' failed with status code '407'."));
        using var runtime = Runtime(handler);

        var ex = await Assert.ThrowsAsync<DeliveryException>(() => runtime.Data.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "http://localhost/records?sig=abc")));

        Assert.Contains("HTTP transport failure calling GET http://localhost/records", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("proxypass", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sig=abc", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_token_endpoint_that_answers_no_json_is_named_without_its_query()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/token", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>login</html>", Encoding.UTF8, "text/html") });
        using var runtime = new HttpRuntime(new FlowReliability(), new SecretResolver([new EnvSecretProvider()]), new TestClock(), handler, allowLoopback: true);
        var auth = new TargetAuth
        {
            Type = TargetAuthType.OAuth2ClientCredentials,
            SecondarySecretRef = "client-1",
            SecretRef = "${env:OSDU_TEST_REDACTION_SECRET}",
            Token = new TargetTokenEndpoint { Url = "http://localhost/token?code=functionkeyvalue" },
        };
        Environment.SetEnvironmentVariable("OSDU_TEST_REDACTION_SECRET", "s3cret");

        var ex = await Assert.ThrowsAsync<DeliveryException>(() => runtime.AuthResolver.ResolveAsync(auth, runtime.Auth));

        Assert.Contains("Token endpoint 'http://localhost/token'", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("functionkeyvalue", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_url_is_named_without_its_user_info_or_query()
    {
        var guard = new UrlGuard([]);
        var ex = Assert.Throws<UrlRefusedException>(() => guard.Check(new Uri("http://user:pass@10.0.0.5/records?sig=abc")));

        Assert.Contains("http://10.0.0.5/records", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("user:pass", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sig=abc", ex.Message, StringComparison.Ordinal);
    }
}
