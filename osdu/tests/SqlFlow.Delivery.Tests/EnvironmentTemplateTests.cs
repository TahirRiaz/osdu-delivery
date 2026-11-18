using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The estate's environments (dev, test and prod, each a deployment of its own) reach each control plane from the
/// deployment templates, so the GUI's title bar names the environment and opens the others
/// (sqlflow/docs/reference/concepts/environments.md). These tests read the templates as committed: the Bicep template
/// takes them and its compiled form, which a deployment sends, carries them too, and every Radix environment names itself
/// beside one list of all three. The Terraform estate's own tests (osdu/deploy/terraform/tests) check what it writes.
/// </summary>
public sealed class EnvironmentTemplateTests
{
    private static readonly string[] Environments = ["dev", "test", "prod"];

    [Fact]
    public void TheBicepTemplate_TakesTheEnvironments_AndItsCompiledFormCarriesThem()
    {
        var bicep = Template("bicep", "main.bicep");
        Assert.Contains("param environments environmentsOptions?", bicep, StringComparison.Ordinal);
        Assert.Contains("  environmentSettings,", bicep, StringComparison.Ordinal);

        var json = Template("bicep", "main.json");
        Assert.Contains("\"environments\": {", json, StringComparison.Ordinal);
        foreach (var setting in new[]
        {
            "ControlPlane__Environments__Current",
            "ControlPlane__Environments__Links__{0}__Name",
            "ControlPlane__Environments__Links__{0}__Url",
            "ControlPlane__Environments__Links__{0}__Description",
        })
        {
            Assert.Contains(setting, json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryRadixEnvironment_NamesItself_BesideOneListOfAll()
    {
        var lines = Template("radix", "radixconfig.yaml").Split('\n').Select(line => line.Trim()).ToArray();

        for (var i = 0; i < Environments.Length; i++)
        {
            var environment = Environments[i];
            Assert.Single(lines, line => line == $"ControlPlane__Environments__Current: {environment}");
            Assert.Single(lines, line => line == $"ControlPlane__Environments__Links__{i}__Name: {environment}");
            Assert.Single(lines, line => line == $"ControlPlane__Environments__Links__{i}__Url: https://gui-osdu-delivery-{environment}.<radix cluster domain>");
        }
    }

    private static string Template(params string[] path)
        => File.ReadAllText(RepositoryRoot.Combine(["osdu", "deploy", .. path]));
}
