using SqlFlow.Azure;
using SqlFlow.Core;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Tests the Azure Storage URI parser behind the cloud file store: the two location families SQLFlow users
/// write (the abfss/wasbs authority form and the https REST-URL form), account/container/path extraction, the
/// per-blob URI round trip that keeps provenance in the caller's own scheme, the blob-endpoint the SDK targets,
/// and the rejection of non-Azure or malformed locations. Pure parsing, no network.
/// </summary>
public sealed class AzureBlobLocationTests
{
    [Theory]
    [InlineData("abfss://data@acct.dfs.core.windows.net/landing/events", "acct", "data", "landing/events", "dfs")]
    [InlineData("abfs://data@acct.dfs.core.windows.net/landing", "acct", "data", "landing", "dfs")]
    [InlineData("wasbs://raw@acct.blob.core.windows.net/x/y/z.json", "acct", "raw", "x/y/z.json", "blob")]
    [InlineData("abfss://c@acct.dfs.core.windows.net/", "acct", "c", "", "dfs")]
    public void Parse_AuthorityForm_ExtractsAccountContainerPath(
        string uri, string account, string container, string blobPath, string endpointKind)
    {
        var loc = AzureBlobLocation.Parse(uri);

        Assert.Equal(account, loc.Account);
        Assert.Equal(container, loc.Container);
        Assert.Equal(blobPath, loc.BlobPath);
        Assert.Equal(endpointKind, loc.EndpointKind);
        Assert.True(loc.ContainerInAuthority);
        Assert.Equal("https://acct.blob.core.windows.net/", loc.BlobServiceEndpoint.ToString());
    }

    [Theory]
    [InlineData("https://acct.blob.core.windows.net/data/landing/events.json", "acct", "data", "landing/events.json", "blob")]
    [InlineData("https://acct.dfs.core.windows.net/fs/part/file.xml", "acct", "fs", "part/file.xml", "dfs")]
    [InlineData("https://acct.blob.core.windows.net/data", "acct", "data", "", "blob")]
    public void Parse_UrlForm_ExtractsAccountContainerPath(
        string uri, string account, string container, string blobPath, string endpointKind)
    {
        var loc = AzureBlobLocation.Parse(uri);

        Assert.Equal(account, loc.Account);
        Assert.Equal(container, loc.Container);
        Assert.Equal(blobPath, loc.BlobPath);
        Assert.Equal(endpointKind, loc.EndpointKind);
        Assert.False(loc.ContainerInAuthority);
    }

    [Fact]
    public void UriFor_RoundTripsInTheAuthorityScheme()
    {
        var loc = AzureBlobLocation.Parse("abfss://data@acct.dfs.core.windows.net/landing");

        var rebuilt = loc.UriFor("landing/2024/01/part-0.json");

        Assert.Equal("abfss://data@acct.dfs.core.windows.net/landing/2024/01/part-0.json", rebuilt);
        // The rebuilt URI parses back to the same coordinates (so OpenReadAsync can re-resolve a listed blob).
        var reparsed = AzureBlobLocation.Parse(rebuilt);
        Assert.Equal("acct", reparsed.Account);
        Assert.Equal("data", reparsed.Container);
        Assert.Equal("landing/2024/01/part-0.json", reparsed.BlobPath);
    }

    [Fact]
    public void UriFor_RoundTripsInTheUrlScheme()
    {
        var loc = AzureBlobLocation.Parse("https://acct.blob.core.windows.net/data/landing");

        Assert.Equal(
            "https://acct.blob.core.windows.net/data/orders/o.xml",
            loc.UriFor("orders/o.xml"));
    }

    [Theory]
    [InlineData("abfss://data@acct.dfs.core.windows.net/x", true)]
    [InlineData("wasbs://c@acct.blob.core.windows.net/x", true)]
    [InlineData("https://acct.blob.core.windows.net/c/x.json", true)]
    [InlineData("https://acct.dfs.core.windows.net/c/x.json", true)]
    [InlineData("https://example.com/data/x.json", false)]
    [InlineData("s3://bucket/key.json", false)]
    [InlineData("/local/path/x.json", false)]
    [InlineData("C:\\data\\x.json", false)]
    [InlineData("az://container/path", false)]
    [InlineData("", false)]
    public void IsAzureStorageUri_ClassifiesLocations(string location, bool expected)
    {
        Assert.Equal(expected, AzureBlobLocation.IsAzureStorageUri(location));
    }

    [Theory]
    [InlineData("abfss://acct.dfs.core.windows.net/nocontainer")]     // missing container@ in the authority
    [InlineData("https://acct.blob.core.windows.net/")]               // URL with no container
    [InlineData("https://example.com/data/x.json")]                   // not an Azure host
    [InlineData("az://container/path")]                               // no account to address
    public void Parse_RejectsUnsupportedLocations(string uri)
    {
        Assert.Throws<SqlFlowException>(() => AzureBlobLocation.Parse(uri));
    }
}
