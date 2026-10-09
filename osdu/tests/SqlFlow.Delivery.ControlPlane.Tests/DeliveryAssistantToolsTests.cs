using System.Text.RegularExpressions;
using SqlFlow.Delivery.ControlPlane.Configuration;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The chat assistant's tool list (<see cref="DeliveryAssistantTools"/>) against the tools the product's MCP server
/// actually offers. The server is Rust and the list is C#, so there is no shared symbol to bind them: the tool names are
/// read out of the server's sources, which is what keeps a contract across two languages honest.
/// </summary>
/// <remarks>
/// The invariant is coverage, as it is for SQLFlow's own list: every delivery tool is explicitly allowed or explicitly
/// excluded. A tool added to the server without deciding fails here, instead of leaving the assistant to tell a user the
/// product cannot do something it can.
/// </remarks>
public sealed class DeliveryAssistantToolsTests
{
    /// <summary>A tool is an async fn immediately preceded by a <c>#[tool(...)]</c> attribute; the first group is the attribute's text.</summary>
    private static readonly Regex Tool = new(
        @"#\[tool\(((?:[^\]]|\][^\)])*?)\)\]\s*(?:pub\s+)?async\s+fn\s+(\w+)", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>The MCP server's sources, read from the repository the suite was built from.</summary>
    private static string HostCrate => SqlFlow.Delivery.Tests.RepositoryRoot.Combine("osdu", "hosts", "osdu-delivery-mcp", "src");

    /// <summary>The delivery tools, by name, each with the text of its attribute (its description).</summary>
    private static IReadOnlyDictionary<string, string> DeliveryTools()
    {
        var folder = Path.Combine(HostCrate, "tools");
        Assert.True(Directory.Exists(folder), $"Expected the delivery tools' sources at {folder}.");
        var found = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(folder, "*.rs"))
        {
            foreach (Match match in Tool.Matches(File.ReadAllText(file)))
            {
                found[match.Groups[2].Value] = match.Groups[1].Value;
            }
        }

        Assert.True(found.Count > 20, $"Only found {found.Count} delivery tools; the parse is probably wrong.");
        return found;
    }

    /// <summary>The tools SQLFlow's own server defines.</summary>
    private static IReadOnlySet<string> SqlFlowTools()
    {
        var path = SqlFlow.Delivery.Tests.RepositoryRoot.Combine("sqlflow", "tools", "sqlflow-mcp", "src", "server.rs");
        Assert.True(File.Exists(path), $"Expected SQLFlow's MCP server source at {path}.");
        var names = Tool.Matches(File.ReadAllText(path)).Select(m => m.Groups[2].Value).ToHashSet(StringComparer.Ordinal);
        Assert.True(names.Count > 40, $"Only found {names.Count} SQLFlow tools; the parse is probably wrong.");
        return names;
    }

    /// <summary>The tools of SQLFlow's the product's server leaves out: the <c>WITHHELD</c> list of the host crate.</summary>
    private static IReadOnlySet<string> Withheld()
    {
        var source = File.ReadAllText(Path.Combine(HostCrate, "lib.rs"));
        var list = Regex.Match(source, @"pub const WITHHELD: &\[&str\] = &\[(.*?)\];", RegexOptions.Singleline);
        Assert.True(list.Success, "The host crate's lib.rs no longer declares WITHHELD as a list of names.");
        var names = Regex.Matches(list.Groups[1].Value, "^\\s*\"(\\w+)\",", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(names.Count >= 5, $"Only found {names.Count} withheld tools; the parse is probably wrong.");
        return names;
    }

    [Fact]
    public void EveryDeliveryTool_IsAllowedOrExplicitlyExcluded()
    {
        var allowed = DeliveryAssistantTools.Delivery.ToHashSet(StringComparer.Ordinal);
        var excluded = DeliveryAssistantTools.Excluded.ToHashSet(StringComparer.Ordinal);

        var undecided = DeliveryTools().Keys.Where(name => !allowed.Contains(name) && !excluded.Contains(name)).ToList();

        Assert.True(
            undecided.Count == 0,
            "These delivery tools are in neither the assistant's allowlist nor its exclusion list, so the assistant cannot " +
            "see them and will report the capability as missing: " + string.Join(", ", undecided) +
            ". Add each to DeliveryAssistantTools.Delivery or to DeliveryAssistantTools.Excluded.");
    }

    [Fact]
    public void TheListsNameNoDeliveryToolTheServerDoesNotHave_AndNoToolTwice()
    {
        var shipped = DeliveryTools().Keys.ToHashSet(StringComparer.Ordinal);

        var phantom = DeliveryAssistantTools.Delivery.Concat(DeliveryAssistantTools.Excluded).Where(name => !shipped.Contains(name)).ToList();
        Assert.True(phantom.Count == 0, "The lists name delivery tools the server does not ship (renamed or removed): " + string.Join(", ", phantom));

        var both = DeliveryAssistantTools.Delivery.Intersect(DeliveryAssistantTools.Excluded, StringComparer.Ordinal).ToList();
        Assert.True(both.Count == 0, "Listed as both allowed and excluded: " + string.Join(", ", both));

        var twice = DeliveryAssistantTools.Allowed.GroupBy(name => name, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(twice.Count == 0, "Allowed more than once: " + string.Join(", ", twice));
    }

    [Fact]
    public void ThePlatformTools_AreOnesTheProductsServerOffers()
    {
        var sqlflow = SqlFlowTools();
        var withheld = Withheld();

        // What the host leaves out has to exist to be left out; the host itself refuses to start otherwise.
        var unknown = withheld.Where(name => !sqlflow.Contains(name)).ToList();
        Assert.True(unknown.Count == 0, "WITHHELD names tools SQLFlow's server does not have: " + string.Join(", ", unknown));

        var missing = DeliveryAssistantTools.Platform.Where(name => !sqlflow.Contains(name)).ToList();
        Assert.True(missing.Count == 0, "The assistant is allowed SQLFlow tools that do not exist (renamed or removed): " + string.Join(", ", missing));

        var notOffered = DeliveryAssistantTools.Platform.Where(withheld.Contains).ToList();
        Assert.True(
            notOffered.Count == 0,
            "The assistant is allowed tools the product's MCP server leaves out because they read data: " + string.Join(", ", notOffered));
    }

    [Fact]
    public void TheAssistantIsGivenNoToolThatStartsWork()
    {
        // An operator's action says it is one in its description, and nothing else does: that is what the
        // exclusion list is checked against, so an action added to the server cannot be allowed by being forgotten.
        var tools = DeliveryTools();
        var actions = tools.Where(tool => tool.Value.Contains("(operator action)", StringComparison.Ordinal)).Select(tool => tool.Key).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(actions, DeliveryAssistantTools.Excluded.Order(StringComparer.Ordinal).ToList());
        Assert.DoesNotContain(DeliveryAssistantTools.Allowed, actions.Contains);

        // And SQLFlow's own stay off, as they are on SQLFlow's list.
        foreach (var starter in new[] { "trigger_run", "cancel_run", "propose_pipelines" })
        {
            Assert.DoesNotContain(starter, DeliveryAssistantTools.Allowed);
        }
    }

    [Fact]
    public void TheToolsThatAnswerAboutARecord_AreAllowed_BecauseThatIsTheProduct()
    {
        // Pinned by name: without these the assistant cannot say whether a record was delivered, or why not.
        foreach (var reader in new[] { "delivery_find_records", "delivery_record", "delivery_flow", "delivery_flow_records", "delivery_activities" })
        {
            Assert.Contains(reader, DeliveryAssistantTools.Allowed);
        }
    }

    [Fact]
    public void ADeploymentsOwnList_IsKept()
    {
        var shipped = new SqlFlow.ControlPlane.Configuration.ControlPlaneOptions();
        DeliveryAssistantTools.Apply(shipped);
        Assert.Equal(DeliveryAssistantTools.Allowed, shipped.Assistant.Mcp.AllowedTools);

        var configured = new SqlFlow.ControlPlane.Configuration.ControlPlaneOptions();
        configured.Assistant.Mcp.AllowedTools = ["search_docs", "delivery_record"];
        DeliveryAssistantTools.Apply(configured);
        Assert.Equal(["search_docs", "delivery_record"], configured.Assistant.Mcp.AllowedTools);
    }
}
