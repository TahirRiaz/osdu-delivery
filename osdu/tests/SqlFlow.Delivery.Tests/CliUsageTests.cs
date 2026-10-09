using System.Text.RegularExpressions;
using SqlFlow.Cli.Hosting;
using SqlFlow.Delivery.Cli;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The usage lines of the module's verbs (<c>sqlflow --help</c>, and what a usage error prints) agree with the options the
/// verbs read: every option a line names is one the verb declares or one of SQLFlow's, every option a verb declares is in
/// its lines, and each subcommand's lines name what that subcommand reads.
/// </summary>
public sealed partial class CliUsageTests
{
    private static readonly IReadOnlyList<CliVerb> Verbs = new DeliveryCliModule().Verbs;

    private static CliVerb Verb(string name) => Assert.Single(Verbs, v => v.Name == name);

    /// <summary>The options a set of usage lines names.</summary>
    private static HashSet<string> Named(IEnumerable<string> lines)
        => lines.SelectMany(line => OptionToken().Matches(line).Select(m => m.Value)).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The lines of each subcommand of <paramref name="verb"/>: a line beginning <c>sqlflow &lt;verb&gt; &lt;subcommand&gt;</c>
    /// and every indented line under it, up to the next subcommand's.
    /// </summary>
    private static Dictionary<string, List<string>> Blocks(CliVerb verb)
    {
        var blocks = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? current = null;
        foreach (var line in verb.Usage)
        {
            var head = Regex.Match(line, $@"^sqlflow {Regex.Escape(verb.Name)} +(?<sub>[a-z][a-z-]*)");
            if (head.Success && verb.Subcommands.Contains(head.Groups["sub"].Value))
            {
                current = blocks.TryGetValue(head.Groups["sub"].Value, out var existing) ? existing : blocks[head.Groups["sub"].Value] = [];
            }

            current?.Add(line);
        }

        return blocks;
    }

    public static TheoryData<string> VerbNames
    {
        get
        {
            var names = new TheoryData<string>();
            foreach (var verb in Verbs)
            {
                names.Add(verb.Name);
            }

            return names;
        }
    }

    [Theory]
    [MemberData(nameof(VerbNames))]
    public void Every_option_a_usage_line_names_is_one_the_verb_reads_and_every_option_it_reads_is_named(string name)
    {
        var verb = Verb(name);
        var declared = verb.ValueOptions.Concat(verb.Flags).ToHashSet(StringComparer.Ordinal);
        var named = Named(verb.Usage);

        var unknown = named.Where(o => !declared.Contains(o) && !CliArguments.SqlFlowOptions.Contains(o)).ToList();
        Assert.True(unknown.Count == 0, $"'sqlflow {name}' usage names options it does not read: {string.Join(", ", unknown)}");

        var unnamed = declared.Where(o => !named.Contains(o)).ToList();
        Assert.True(unnamed.Count == 0, $"'sqlflow {name}' reads options its usage never names: {string.Join(", ", unnamed)}");
    }

    [Theory]
    [MemberData(nameof(VerbNames))]
    public void Every_subcommand_has_usage_lines(string name)
    {
        // A subcommand begins its own line, or follows another's on one line after a '|' (template list | show ...).
        var verb = Verb(name);
        var missing = verb.Subcommands
            .Where(s => !verb.Usage.Any(line =>
                Regex.IsMatch(line, $@"^sqlflow {Regex.Escape(name)} +{Regex.Escape(s)}\b") || Regex.IsMatch(line, $@"\| +{Regex.Escape(s)}\b")))
            .ToList();
        Assert.True(missing.Count == 0, $"'sqlflow {name}' has no usage line for: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// <c>records</c>, <c>dimensions</c> and <c>assertions</c> bind the flow to the partition <c>--partition</c> names before
    /// they dispatch to a subcommand (<c>CliPartitions</c>), so every subcommand reads it, and every subcommand's lines say so.
    /// </summary>
    [Theory]
    [InlineData("records")]
    [InlineData("dimensions")]
    [InlineData("assertions")]
    public void A_verb_that_binds_the_partition_for_every_subcommand_names_it_for_every_subcommand(string name)
    {
        var verb = Verb(name);
        var without = Blocks(verb).Where(b => !Named(b.Value).Contains("--partition")).Select(b => b.Key).ToList();
        Assert.True(without.Count == 0, $"'sqlflow {name}' reads --partition for every subcommand, and these lines leave it out: {string.Join(", ", without)}");
    }

    [Theory]
    [InlineData("values")]
    [InlineData("keys")]
    public void The_dimension_listings_that_take_removed_say_so(string subcommand)
        => Assert.Contains("--removed", Named(Blocks(Verb("dimensions"))[subcommand]));

    [Fact]
    public void Release_names_every_status_it_releases()
    {
        var release = string.Join(' ', Blocks(Verb("records"))["release"]);
        foreach (var status in new[] { "held", "failed", "deleted", "reverted" })
        {
            Assert.Contains(status, release, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void No_usage_line_carries_an_em_dash_or_a_tab()
        => Assert.All(Verbs.SelectMany(v => v.Usage), line =>
        {
            Assert.DoesNotContain((char)0x2014, line);
            Assert.DoesNotContain('\t', line);
        });

    [GeneratedRegex(@"(?<![\w-])--[a-z][a-z-]*[a-z]")]
    private static partial Regex OptionToken();
}
