using System.Reflection;
using SqlFlow.Delivery.Documents;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The guard against the drop era coming back (docs/stage4-design.md section 6). A flow's records come from its
/// ingestion tables: there is no drop, no drop manifest, no replica of the metadata rows, no published known state, no
/// inline drop and no records sent through an API of the module's own. Those names are gone from the module, and this
/// suite is what keeps them gone: a reintroduction is a failing test here rather than a second way to deliver a record.
/// </summary>
public class ArchitectureTests
{
    /// <summary>The names no type or member of the module carries any more, and what replaced each of them.</summary>
    public static TheoryData<string, string> Retired => new()
    {
        { "Drop", "a run reads the flow's ingestion tables (source.record and source.datasets)" },
        { "DropManifest", "a submission records its own selection, window and slices" },
        { "DropOff", "source files are placed where a pre flow reads them, and the flow chain loads them" },
        { "Replica", "the ingestion tables are the flow's own copy of the metadata rows" },
        { "KnownState", "the ledger is what the delivered state is read from" },
        { "InlineDrop", "records reach the ingestion tables through the pre and ingestion flows" },
        { "InlineSubmission", "records reach the ingestion tables through the pre and ingestion flows" },
        { "InlineRecord", "a record is a row of the flow's ingestion tables" },
        { "SubmissionLanding", "a pre flow reads the files placed where its source.location points" },
        { "SubmissionChain", "lineage orders the pre, ingestion and delivery flows like any other flows" },
        { "Reland", "a pre flow reads the files placed where its source.location points" },
        { "PayloadChunk", "PayloadFile names one file of a record's payload" },
    };

    private static IReadOnlyList<Assembly> Module { get; } =
    [
        typeof(DeliveryDocumentLoader).Assembly,
        typeof(SqlFlow.Delivery.Data.OsduDbContext).Assembly,
    ];

    [Theory]
    [MemberData(nameof(Retired))]
    public void No_type_or_member_of_the_module_carries_a_retired_name(string retired, string instead)
    {
        var found = new List<string>();
        foreach (var assembly in Module)
        {
            foreach (var type in assembly.GetTypes())
            {
                // A nested or generic type reports a decorated name; the declared name is what an author wrote.
                if (Carries(type.Name, retired))
                {
                    found.Add($"{assembly.GetName().Name}: type {type.FullName}");
                }

                foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    // A property's accessors and a record's compiler-generated members repeat the property's own name.
                    if (member is MethodInfo { IsSpecialName: true } || Carries(member.Name, retired) is false)
                    {
                        continue;
                    }

                    found.Add($"{assembly.GetName().Name}: {type.FullName}.{member.Name}");
                }
            }
        }

        Assert.True(
            found.Count == 0,
            $"'{retired}' is retired: {instead}. Reintroduced by {found.Count} declaration(s): {string.Join("; ", found.Take(10))}.");
    }

    /// <summary>
    /// Whether a declared name carries the retired one as a word: the name itself, or a word of it in PascalCase. It is
    /// a word boundary rather than a substring so that ordinary English keeps working, and the plural counts too, which
    /// is what a collection of them would be called.
    /// </summary>
    private static bool Carries(string name, string retired)
    {
        var at = 0;
        while ((at = name.IndexOf(retired, at, StringComparison.Ordinal)) >= 0)
        {
            var before = at == 0 || char.IsUpper(name[at]);
            var after = at + retired.Length;
            var ends = after == name.Length
                || name[after] is 's' or 'S' && (after + 1 == name.Length || char.IsUpper(name[after + 1]))
                || char.IsUpper(name[after]);
            if (before && ends)
            {
                return true;
            }

            at += retired.Length;
        }

        return false;
    }
}
