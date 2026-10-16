using System.Text.RegularExpressions;
using SqlFlow.Delivery.ControlPlane.Configuration;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Which <c>delivery_*</c> tools an assistant run may reach (<see cref="DeliveryAssistantAccess.ReaderTools"/>) and which
/// it may not (<see cref="DeliveryAssistantAccess.ActionTools"/>), against the tools the product's MCP server actually
/// offers. The server is Rust and the lists are C#, so the tool names are read out of the server's sources, which is what
/// keeps a contract across two languages honest.
/// </summary>
/// <remarks>
/// The invariant is coverage: every delivery tool is a reader, whose routes the fence opens, or an operator action, whose
/// writes it keeps closed. A tool added to the server without deciding fails here, instead of being refused in the chat
/// with no one having chosen that, or reaching what it should not.
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
    public void EveryDeliveryTool_IsAReaderOrAnAction()
    {
        var readers = DeliveryAssistantAccess.ReaderTools.ToHashSet(StringComparer.Ordinal);
        var actions = DeliveryAssistantAccess.ActionTools.ToHashSet(StringComparer.Ordinal);

        var undecided = DeliveryTools().Keys.Where(name => !readers.Contains(name) && !actions.Contains(name)).ToList();

        Assert.True(
            undecided.Count == 0,
            "These delivery tools are neither a reader nor an operator action, so nobody decided whether the chat may reach "
            + "them: " + string.Join(", ", undecided)
            + ". Add each to DeliveryAssistantAccess.ReaderTools or to DeliveryAssistantAccess.ActionTools.");
    }

    [Fact]
    public void TheListsNameNoDeliveryToolTheServerDoesNotHave_AndNoToolTwice()
    {
        var shipped = DeliveryTools().Keys.ToHashSet(StringComparer.Ordinal);

        var listed = DeliveryAssistantAccess.ReaderTools.Concat(DeliveryAssistantAccess.ActionTools).ToList();
        var phantom = listed.Where(name => !shipped.Contains(name)).ToList();
        Assert.True(phantom.Count == 0, "The lists name delivery tools the server does not ship (renamed or removed): " + string.Join(", ", phantom));

        var twice = listed.GroupBy(name => name, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(twice.Count == 0, "Listed more than once, or as both a reader and an action: " + string.Join(", ", twice));
    }

    [Fact]
    public void WhatTheProductsServerWithholds_IsSqlFlowsAndLeavesAQuestionsQueryOffered()
    {
        var sqlflow = SqlFlowTools();
        var withheld = Withheld();

        // What the host leaves out has to exist to be left out; the host itself refuses to start otherwise.
        var unknown = withheld.Where(name => !sqlflow.Contains(name)).ToList();
        Assert.True(unknown.Count == 0, "WITHHELD names tools SQLFlow's server does not have: " + string.Join(", ", unknown));

        // A `!` question runs its query over what the approved AI contexts open, so its tools are offered.
        foreach (var query in new[] { "find_similar_questions", "prepare_query", "run_query", "auto_run_trusted_match", "confirm_question" })
        {
            Assert.DoesNotContain(query, withheld);
        }
    }

    [Fact]
    public void TheActions_AreExactlyTheToolsThatSayTheyAreOne()
    {
        // An operator's action says it is one in its description, and nothing else does: that is what the action list
        // is checked against, so an action added to the server cannot be opened to the chat by being forgotten.
        var actions = DeliveryTools()
            .Where(tool => tool.Value.Contains("(operator action)", StringComparison.Ordinal))
            .Select(tool => tool.Key)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(actions, DeliveryAssistantAccess.ActionTools.Order(StringComparer.Ordinal).ToList());
        Assert.DoesNotContain(DeliveryAssistantAccess.ReaderTools, actions.Contains);
    }

    [Fact]
    public void TheToolsThatAnswerAboutARecord_AreReaders_BecauseThatIsTheProduct()
    {
        // Pinned by name: without these the assistant cannot say whether a record was delivered, or why not.
        foreach (var reader in new[] { "delivery_find_records", "delivery_record", "delivery_flow", "delivery_flow_records", "delivery_activities" })
        {
            Assert.Contains(reader, DeliveryAssistantAccess.ReaderTools);
        }
    }
}
