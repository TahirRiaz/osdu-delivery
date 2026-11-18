using SqlFlow.ControlPlane.Configuration;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The environments the title bar switches between (<c>ControlPlane:Environments</c>): nothing configured is the default and
/// shows nothing, the current one may be named alone, and a list the title bar could not show stops the control plane at
/// start, naming the setting and never repeating an address.
/// </summary>
public sealed class EnvironmentsOptionsTests
{
    private static EnvironmentLinkOptions Link(string? name, string? url, string? description = null)
        => new() { Name = name, Url = url, Description = description };

    private static EnvironmentsOptions Estate(string? current = "test") => new()
    {
        Current = current,
        Links =
        [
            Link("dev", "https://sqlflow-dev.example.com", "Development"),
            Link("test", "https://sqlflow-test.example.com/", "Acceptance testing"),
            Link("prod", "https://sqlflow.example.com/workbench", "Production"),
        ],
    };

    private static string Refusal(EnvironmentsOptions options)
        => Assert.Throws<InvalidOperationException>(options.Validate).Message;

    [Fact]
    public void NothingConfigured_IsTheDefault_AndNamesNoEnvironment()
    {
        var options = new EnvironmentsOptions();

        options.Validate();

        Assert.Null(options.CurrentName());
        Assert.Empty(options.Links);
    }

    [Fact]
    public void AListWithTheCurrentOneAmongIt_IsTaken()
    {
        var options = Estate();

        options.Validate();

        Assert.Equal("test", options.CurrentName());
    }

    [Fact]
    public void TheCurrentOne_IsSpelledAsTheListSpellsIt()
    {
        var options = Estate(" TEST ");

        options.Validate();

        Assert.Equal("test", options.CurrentName());
    }

    [Fact]
    public void TheCurrentOneAlone_IsTaken_SoTheTitleBarNamesItAndOffersNoOther()
    {
        var options = new EnvironmentsOptions { Current = "prod" };

        options.Validate();

        Assert.Equal("prod", options.CurrentName());
    }

    [Fact]
    public void AListThatNamesNoCurrentOne_IsRefused_NamingTheSettingAndTheList()
    {
        var message = Refusal(Estate(current: null));

        Assert.Contains("ControlPlane:Environments:Current", message, StringComparison.Ordinal);
        Assert.Contains("dev, test, prod", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACurrentOneTheListDoesNotHold_IsRefused()
    {
        var message = Refusal(Estate("stage"));

        Assert.Contains("ControlPlane:Environments:Current is 'stage'", message, StringComparison.Ordinal);
        Assert.Contains("(dev, test, prod)", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pre prod")]
    [InlineData("-dev")]
    [InlineData("dev/1")]
    [InlineData("utvikling\u00e6")]
    public void ACurrentNameThatIsNoName_IsRefused(string current)
    {
        var message = Refusal(new EnvironmentsOptions { Current = current });

        Assert.Contains("ControlPlane:Environments:Current", message, StringComparison.Ordinal);
        Assert.Contains("is not an environment name", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("pre prod")]
    [InlineData(".dev")]
    public void AListedNameThatIsNoName_IsRefused_NamingItsEntry(string? name)
    {
        var options = Estate();
        options.Links[1] = Link(name, "https://sqlflow-test.example.com");

        var message = Refusal(options);

        Assert.Contains("ControlPlane:Environments:Links:1:Name", message, StringComparison.Ordinal);
        Assert.Contains("is not an environment name", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANameOfTheLongestLength_IsTaken_AndOneLonger_IsRefused()
    {
        var longest = new string('e', EnvironmentsOptions.MaxNameLength);
        new EnvironmentsOptions { Current = longest, Links = [Link(longest, "https://e.example.com")] }.Validate();

        var longer = longest + "e";
        var message = Refusal(new EnvironmentsOptions { Current = longest, Links = [Link(longer, "https://e.example.com")] });

        Assert.Contains("ControlPlane:Environments:Links:0:Name", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEnvironmentListedTwice_IgnoringCase_IsRefused()
    {
        var options = Estate();
        options.Links.Add(Link("PROD", "https://sqlflow-prod-2.example.com"));

        var message = Refusal(options);

        Assert.Contains("ControlPlane:Environments:Links:3:Name 'PROD'", message, StringComparison.Ordinal);
        Assert.Contains("list each environment once", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyEntry_IsRefused()
    {
        var options = Estate();
        options.Links.Insert(0, null);

        var message = Refusal(options);

        Assert.Contains("ControlPlane:Environments:Links:0 is empty", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void AMissingAddress_IsRefused(string? url)
    {
        var options = Estate();
        options.Links[2] = Link("prod", url);

        var message = Refusal(options);

        Assert.Contains("ControlPlane:Environments:Links:2:Url is required", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sqlflow-prod.example.com")]
    [InlineData("/workbench")]
    [InlineData("ftp://sqlflow-prod.example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///c:/estate")]
    public void AnAddressThatIsNoHttpAddress_IsRefused_WithoutRepeatingIt(string url)
    {
        var options = Estate();
        options.Links[2] = Link("prod", url);

        var message = Refusal(options);

        Assert.Contains("ControlPlane:Environments:Links:2:Url is not an absolute http or https address", message, StringComparison.Ordinal);
        Assert.DoesNotContain(url, message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAddressCarryingACredential_IsRefused_WithoutRepeatingIt()
    {
        var options = Estate();
        options.Links[0] = Link("dev", "https://operator:s3cret-Pa55@sqlflow-dev.example.com");

        var message = Refusal(options);

        Assert.Contains("ControlPlane:Environments:Links:0:Url carries a user name or a password", message, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret-Pa55", message, StringComparison.Ordinal);
        Assert.DoesNotContain("operator", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://sqlflow-dev.example.com/?partition=dev")]
    [InlineData("https://sqlflow-dev.example.com/#records")]
    public void AnAddressWithAQueryOrAFragment_IsRefused(string url)
    {
        var options = Estate();
        options.Links[0] = Link("dev", url);

        var message = Refusal(options);

        Assert.Contains("ControlPlane:Environments:Links:0:Url carries a query or a fragment", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnHttpAddress_IsTaken_ForAnEstateServedWithoutTls()
    {
        var options = Estate();
        options.Links[0] = Link("dev", "http://localhost:5173");

        options.Validate();
    }

    [Fact]
    public void AnAddressLongerThanTheLongest_IsRefused()
    {
        var options = Estate();
        options.Links[0] = Link("dev", "https://sqlflow-dev.example.com/" + new string('a', EnvironmentsOptions.MaxUrlLength));

        var message = Refusal(options);

        Assert.Contains("ControlPlane:Environments:Links:0:Url is not an absolute http or https address", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADescriptionOfTheLongestLength_IsTaken_AndOneLongerOrOnTwoLines_IsRefused()
    {
        var options = Estate();
        options.Links[0] = Link("dev", "https://sqlflow-dev.example.com", new string('d', EnvironmentsOptions.MaxDescriptionLength));
        options.Validate();

        options.Links[0] = Link("dev", "https://sqlflow-dev.example.com", new string('d', EnvironmentsOptions.MaxDescriptionLength + 1));
        Assert.Contains("ControlPlane:Environments:Links:0:Description", Refusal(options), StringComparison.Ordinal);

        options.Links[0] = Link("dev", "https://sqlflow-dev.example.com", "Development\nand experiments");
        Assert.Contains("describe the environment in one line", Refusal(options), StringComparison.Ordinal);
    }

    [Fact]
    public void AListOfTheMostEnvironments_IsTaken_AndOneMore_IsRefused()
    {
        var options = new EnvironmentsOptions { Current = "env0" };
        for (var i = 0; i < EnvironmentsOptions.MaxLinks; i++)
        {
            options.Links.Add(Link($"env{i}", $"https://env{i}.example.com"));
        }

        options.Validate();

        options.Links.Add(Link("one-more", "https://one-more.example.com"));
        Assert.Contains($"at most {EnvironmentsOptions.MaxLinks} are listed", Refusal(options), StringComparison.Ordinal);
    }

    [Fact]
    public void TheControlPlanesOwnValidation_ChecksTheEnvironments()
    {
        var options = new ControlPlaneOptions
        {
            Jwt = new JwtOptions { SigningKey = new string('k', 64) },
            Environments = Estate("stage"),
        };

        var message = Assert.Throws<InvalidOperationException>(options.Validate).Message;

        Assert.Contains("ControlPlane:Environments:Current is 'stage'", message, StringComparison.Ordinal);
    }
}
