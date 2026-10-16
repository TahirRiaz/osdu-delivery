using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Amazon.Runtime.Internal.Auth;
using Amazon.Util;
using SqlFlow.Delivery.Hashing;
using SqlFlow.Delivery.Http;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What the object stores behind Seismic Store check a request and an object by: SigV4 signatures, compared with the
/// ones the AWS SDK makes from its own canonical forms, and the CRC-32C Google Cloud Storage keeps for an object,
/// compared with the specification's check value and a bitwise reference.
/// </summary>
public sealed class ObjectStoreProtocolTests
{
    private const string Region = "us-east-1";

    private static readonly AwsSigV4.Credentials Keys = new("AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY", "session-token");

    private static readonly DateTimeOffset At = new(2026, 9, 17, 8, 30, 15, TimeSpan.Zero);

    private static readonly byte[] Part = [1, 2, 3];

    /// <summary>The canonical forms the AWS SDK makes for its own signers.</summary>
    private sealed class Sdk : AWS4Signer
    {
        public static string Query(string decoded) => CanonicalizeQueryParameters(decoded);

        public static (string Block, string Names) Headers(IEnumerable<KeyValuePair<string, string>> headers)
        {
            var sorted = SortAndPruneHeaders(headers);
            return (CanonicalizeHeaders(sorted), CanonicalizeHeaderNames(sorted));
        }
    }

    [SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "Content-MD5 is the checksum S3 checks a part against; the test signs it as the store reads it.")]
    private static byte[] Md5(byte[] bytes) => MD5.HashData(bytes);

    [Theory]
    [InlineData("PUT", "http://localhost:9000/bucket/folder/a b(1).sgy", true)]
    [InlineData("POST", "http://minio.example.com/bucket/key/0?uploads", false)]
    [InlineData("PUT", "https://s3.example.com/bucket/key/0?uploadId=a%2Fb%3D&partNumber=10", true)]
    [InlineData("DELETE", "https://s3.example.com/bucket/%D0%BA%D0%BB%D1%8E%D1%87/0%2A", false)]
    [InlineData("POST", "https://s3.example.com/bucket/key/0?uploadId=x~y", false)]
    public void A_request_is_signed_as_the_aws_sdk_signs_its_canonical_form(string method, string url, bool md5)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (md5)
        {
            request.Content = new ByteArrayContent(Part);
            request.Content.Headers.ContentMD5 = Md5(Part);
        }

        AwsSigV4.Sign(request, Keys, Region, "s3", At);

        var authorization = request.Headers.Authorization!;
        Assert.Equal("AWS4-HMAC-SHA256", authorization.Scheme);
        var fields = authorization.Parameter!.Split(", ").Select(f => f.Split('=', 2)).ToDictionary(f => f[0], f => f[1], StringComparer.Ordinal);
        Assert.Equal(["Credential", "SignedHeaders", "Signature"], fields.Keys);
        Assert.Equal("AKIDEXAMPLE/20260917/us-east-1/s3/aws4_request", fields["Credential"]);
        Assert.Equal("20260917T083015Z", request.Headers.GetValues(AwsSigV4.DateHeader).Single());
        Assert.Equal("UNSIGNED-PAYLOAD", request.Headers.GetValues(AwsSigV4.ContentHashHeader).Single());
        Assert.Equal("session-token", request.Headers.GetValues(AwsSigV4.SessionTokenHeader).Single());

        // The canonical request from the SDK's pieces: its encoding of each path segment, and its query and header forms.
        var uri = request.RequestUri!;
        var host = uri.IsDefaultPort ? uri.Host : uri.Authority;
        var headers = new List<KeyValuePair<string, string>>
        {
            new("Host", host),
            new("X-Amz-Date", "20260917T083015Z"),
            new("X-Amz-Content-SHA256", "UNSIGNED-PAYLOAD"),
            new("X-Amz-Security-Token", "  session-token "),
        };
        if (md5)
        {
            headers.Add(new("Content-MD5", Convert.ToBase64String(Md5(Part))));
        }

        var (block, names) = Sdk.Headers(headers);
        var path = string.Join('/', uri.AbsolutePath.Split('/').Select(s => AWSSDKUtils.UrlEncode(Uri.UnescapeDataString(s), false)));
        var decoded = string.Join('&', uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => string.Join('=', p.Split('=', 2).Select(Uri.UnescapeDataString))));
        var canonical = $"{method}\n{path}\n{Sdk.Query(decoded)}\n{block}\n{names}\nUNSIGNED-PAYLOAD";

        Assert.Equal(names, fields["SignedHeaders"]);
        Assert.Equal(AWS4Signer.ComputeSignature(Keys.AccessKey, Keys.SecretKey, Region, At.UtcDateTime, "s3", names, canonical).Signature, fields["Signature"]);

        var signed = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = host,
            ["x-amz-content-sha256"] = "UNSIGNED-PAYLOAD",
            ["x-amz-date"] = "20260917T083015Z",
            ["x-amz-security-token"] = "session-token",
        };
        if (md5)
        {
            signed["content-md5"] = Convert.ToBase64String(Md5(Part));
        }

        Assert.Equal(canonical, AwsSigV4.CanonicalRequest(method, uri, signed, AwsSigV4.UnsignedPayload));
    }

    [Fact]
    public void A_request_signed_again_carries_one_signature_and_temporary_keys_their_session()
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "https://s3.example.com/bucket/key/0");
        AwsSigV4.Sign(request, Keys, Region, "s3", At);
        AwsSigV4.Sign(request, Keys with { SessionToken = "renewed" }, Region, "s3", At.AddMinutes(1));
        Assert.Equal("20260917T083115Z", request.Headers.GetValues(AwsSigV4.DateHeader).Single());
        Assert.Equal("renewed", request.Headers.GetValues(AwsSigV4.SessionTokenHeader).Single());
        Assert.Single(request.Headers.GetValues(AwsSigV4.ContentHashHeader));

        // Long-term keys sign no session.
        var plain = new HttpRequestMessage(HttpMethod.Put, "https://s3.example.com/bucket/key/0");
        AwsSigV4.Sign(plain, new AwsSigV4.Credentials("AK", "SK", null), Region, "s3", At);
        Assert.False(plain.Headers.Contains(AwsSigV4.SessionTokenHeader));
        Assert.Contains("SignedHeaders=host;x-amz-content-sha256;x-amz-date,", plain.Headers.Authorization!.Parameter, StringComparison.Ordinal);

        // Neither the secret nor the session shows where a credential could be logged.
        Assert.Equal("(S3 credentials)", Keys.ToString());
        Assert.DoesNotContain(Keys.SecretKey, request.ToString(), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => AwsSigV4.Sign(new HttpRequestMessage(), Keys, Region, "s3", At));
    }

    [Fact]
    public void The_signing_key_and_the_encoding_are_the_ones_the_aws_sdk_uses()
    {
        Assert.Equal(AWS4Signer.ComposeSigningKey(Keys.SecretKey, Region, "20260917", "s3"), AwsSigV4.SigningKey(Keys.SecretKey, "20260917", Region, "s3"));
        foreach (var value in new[] { "a b+c/d", "ключ", "*!'()", "~-_.", "%", "0", string.Empty, "line 001 (copy).sgy" })
        {
            Assert.Equal(AWSSDKUtils.UrlEncode(value, false), AwsSigV4.Encode(value));
        }

        // Names and values are encoded once and sorted by name, then value; a name without a value takes an empty one.
        Assert.Equal("a=0&a=1&b=2&c=x%20y&uploads=", AwsSigV4.CanonicalQuery(new Uri("http://h/p?b=2&a=1&a=0&uploads&c=x%20y")));
        Assert.Equal(string.Empty, AwsSigV4.CanonicalQuery(new Uri("http://h/p")));
        Assert.Equal("/bucket/a%20b/%D0%BA%2A", AwsSigV4.CanonicalPath(new Uri("http://h/bucket/a b/%D0%BA%2A")));
    }

    [Fact]
    public void The_crc32c_is_the_castagnoli_checksum_google_cloud_storage_keeps()
    {
        // The check value of CRC-32/ISCSI, whole and appended in pieces.
        var check = Encoding.ASCII.GetBytes("123456789");
        Assert.Equal(0xE3069283u, Crc32C.Append(0, check));
        Assert.Equal(0xE3069283u, Crc32C.Append(Crc32C.Append(0, check.AsSpan(0, 4)), check.AsSpan(4)));
        Assert.Equal(0u, Crc32C.Append(0, []));
        Assert.Equal("AAAAAA==", Crc32C.ToBase64(0));
        Assert.Equal("4waSgw==", Crc32C.ToBase64(0xE3069283u));

        // Every length and every split agrees with a bitwise reference of the reflected polynomial.
        var data = new byte[300];
        new Random(5).NextBytes(data);
        for (var length = 0; length <= data.Length; length += 7)
        {
            var whole = data.AsSpan(0, length);
            var expected = Bitwise(whole);
            Assert.Equal(expected, Crc32C.Append(0, whole));
            for (var split = 0; split <= length; split += 13)
            {
                Assert.Equal(expected, Crc32C.Append(Crc32C.Append(0, whole[..split]), whole[split..]));
            }
        }
    }

    private static uint Bitwise(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0x82F63B78u : crc >> 1;
            }
        }

        return ~crc;
    }
}
