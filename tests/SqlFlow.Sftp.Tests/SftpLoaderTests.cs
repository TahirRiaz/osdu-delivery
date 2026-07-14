using SqlFlow.Core;
using SqlFlow.Core.Sftp;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Sftp.Tests;

/// <summary>
/// The sftp YAML surface (flowType: sftp): a document maps to a validated SftpFlow with the documented defaults, the
/// direction enum normalizes, and every cross-field requirement (server host/username, local, a credential) fails
/// with the field path. The transfer engine itself needs a live server, so it is exercised separately.
/// </summary>
public sealed class SftpLoaderTests
{
    private static readonly YamlSftpFlowLoader Loader = new();

    private const string Minimal = """
        flowType: sftp
        name: Vendor_Download
        batch: BB
        server:
          host: sftp.vendor.com
          username: svc
          passwordRef: ${keyvault:v/sftp-pwd}
        remotePath: /outbound
        local: abfss://lake@acct.dfs.core.windows.net/raw/vendor
        pattern: "*.xml"
        modifiedWithinDays: 3
        """;

    [Fact]
    public void Minimal_MapsWithDefaults()
    {
        var flow = Loader.Parse(Minimal);

        Assert.Equal("Vendor_Download", flow.Name);
        Assert.Equal("BB", flow.Batch);
        Assert.Equal(SftpDirection.Download, flow.Direction);       // default
        Assert.Equal("sftp.vendor.com", flow.Server.Host);
        Assert.Equal(22, flow.Server.Port);                         // default
        Assert.Equal("svc", flow.Server.Username);
        Assert.Equal("${keyvault:v/sftp-pwd}", flow.Server.PasswordRef);
        Assert.Equal("/outbound", flow.RemotePath);
        Assert.Equal("abfss://lake@acct.dfs.core.windows.net/raw/vendor", flow.Local);
        Assert.Equal("*.xml", flow.Pattern);
        Assert.Equal(3, flow.ModifiedWithinDays);
        Assert.True(flow.Recursive);                                // default
        Assert.True(flow.Overwrite);                                // default
        Assert.True(flow.PreserveStructure);                        // default
    }

    [Theory]
    [InlineData("upload", SftpDirection.Upload)]
    [InlineData("download", SftpDirection.Download)]
    [InlineData("UPLOAD", SftpDirection.Upload)]
    public void Direction_ParsesCaseInsensitively(string token, SftpDirection expected)
    {
        var flow = Loader.Parse(Minimal.Replace("direction:", "x:", StringComparison.Ordinal) + $"\ndirection: {token}\n");
        Assert.Equal(expected, flow.Direction);
    }

    [Fact]
    public void PrivateKey_IsAccepted()
    {
        var flow = Loader.Parse(Minimal.Replace(
            "  passwordRef: ${keyvault:v/sftp-pwd}",
            "  privateKeyRef: ${keyvault:v/sftp-key}\n  passphraseRef: ${keyvault:v/sftp-pass}", StringComparison.Ordinal));
        Assert.Equal("${keyvault:v/sftp-key}", flow.Server.PrivateKeyRef);
        Assert.Equal("${keyvault:v/sftp-pass}", flow.Server.PassphraseRef);
    }

    [Theory]
    [InlineData("name: Vendor_Download", "name: ''", "'name' is required for an sftp flow.")]
    [InlineData("host: sftp.vendor.com", "host: ''", "'server.host' is required.")]
    [InlineData("username: svc", "username: ''", "'server.username' is required.")]
    [InlineData("local: abfss://lake@acct.dfs.core.windows.net/raw/vendor", "local: ''", "'local' is required.")]
    [InlineData("direction: download", "direction: sideways", "not a valid value for 'direction'")]
    public void Requirements_FailWithThePath(string find, string replace, string expected)
    {
        var yaml = ("direction: download\n" + Minimal).Replace(find, replace, StringComparison.Ordinal);
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(yaml));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingServer_Fails()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse("flowType: sftp\nname: x\nlocal: ./out\n"));
        Assert.Contains("'server' is required", ex.Message, StringComparison.Ordinal);
    }
}
