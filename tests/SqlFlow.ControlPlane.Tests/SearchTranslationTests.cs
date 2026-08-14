using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Every search surface has to run entirely in SQL Server: the token predicates are built by chaining
/// <c>Where</c> in a loop and the statement surface groups a join, both of which fail at RUNTIME (not at compile
/// time) if EF Core cannot translate them, and a client-side evaluation of a LIKE over the statement table would
/// pull the whole catalog into memory. Rendering each query to its SQL proves translation without needing a
/// database: <c>ToQueryString</c> compiles the expression tree but never opens a connection.
/// </summary>
public sealed class SearchTranslationTests
{
    // Never connected to: ToQueryString compiles the query, it does not execute it.
    private const string OfflineConnectionString =
        "Server=(local);Database=sqlflow_translation_probe;Integrated Security=true;TrustServerCertificate=true";

    private static CatalogDbContext Context() => CatalogDatabase.Create(OfflineConnectionString);

    private static SearchQuery Query(string raw)
    {
        var parsed = SearchQuery.Parse(raw);
        Assert.NotNull(parsed);
        return parsed;
    }

    [Fact]
    public void ObjectsQuery_TranslatesAndAndsOnePredicatePerToken()
    {
        using var db = Context();

        var sql = SearchEndpoints.ObjectsQuery(db, Query("ferry passengers")).ToQueryString();

        Assert.Contains("[Object]", sql, StringComparison.Ordinal);
        // One LIKE per token in the WHERE, ANDed, plus one in the phrase-first ORDER BY: the AND semantics and the
        // exact-phrase ranking are both in the SQL, not applied after the fact in memory. The term travels as a
        // parameter with ESCAPE, so a name containing % or _ is matched literally.
        Assert.Equal(3, CountOccurrences(sql, "LIKE "));
        Assert.Contains(" AND ", sql, StringComparison.Ordinal);
        Assert.Contains("ESCAPE", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY CASE", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ColumnsQuery_TranslatesAndMatchesTokensAgainstColumnOrOwningObject()
    {
        using var db = Context();

        var sql = SearchEndpoints.ColumnsQuery(db, Query("ferrypassengers sourcerank")).ToQueryString();

        Assert.Contains("[ObjectColumn]", sql, StringComparison.Ordinal);
        Assert.Contains("[Object]", sql, StringComparison.Ordinal);
        // Two tokens, each allowed to land on either the column or the object it sits on, plus the ranking probe.
        Assert.Equal(5, CountOccurrences(sql, "LIKE "));
    }

    [Fact]
    public void DefinitionRows_TranslatesOverBothTheModuleBodyAndTheEmittedScript()
    {
        using var db = Context();

        var sql = SearchEndpoints.DefinitionRows(db, Query("SourceRank")).ToQueryString();

        Assert.Contains("[Definition]", sql, StringComparison.Ordinal);
        Assert.Contains("[Script]", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void FilesQuery_TranslatesAndKeepsIdsLowercasedInSql()
    {
        using var db = Context();

        var sql = SearchEndpoints.FilesQuery(db, Query("orders csv")).ToQueryString();

        Assert.Contains("[RunFile]", sql, StringComparison.Ordinal);
        // The GUI matches graph node ids by exact string, so the lowercasing must happen server-side in the
        // projection rather than being lost.
        Assert.Contains("LOWER(", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void FlowRows_TranslatesAndSearchesTheYamlBody()
    {
        using var db = Context();

        var sql = SearchEndpoints.FlowRows(db, Query("SourceRank")).ToQueryString();

        Assert.Contains("[Pipeline]", sql, StringComparison.Ordinal);
        Assert.Contains("[Yaml]", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void FlowColumnRows_TranslatesAndSearchesNameSourceAndExpression()
    {
        using var db = Context();

        var sql = SearchEndpoints.FlowColumnRows(db, Query("SourceRank")).ToQueryString();

        Assert.Contains("[PipelineColumn]", sql, StringComparison.Ordinal);
        Assert.Contains("[ColumnName]", sql, StringComparison.Ordinal);
        Assert.Contains("[SourceColumn]", sql, StringComparison.Ordinal);
        Assert.Contains("[Expression]", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void StatementGroups_TranslatesTheGroupedJoinServerSide()
    {
        using var db = Context();
        var cutoff = SearchEndpoints.StatementCutoff(TimeProvider.System, 90);

        var sql = SearchEndpoints.StatementGroups(db, Query("SourceRank"), cutoff).ToQueryString();

        Assert.Contains("[RunStatement]", sql, StringComparison.Ordinal);
        // The collapse to one row per (flow, step) and the occurrence count must be the database's work: the
        // statement table is the largest in the catalog and cannot be grouped in memory.
        Assert.Contains("GROUP BY", sql, StringComparison.Ordinal);
        Assert.Contains("COUNT", sql, StringComparison.Ordinal);
        Assert.Contains("MAX(", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void StatementGroups_WithoutAWindow_TranslatesWithNoTimeFilter()
    {
        using var db = Context();

        var windowed = SearchEndpoints.StatementGroups(
            db, Query("SourceRank"), SearchEndpoints.StatementCutoff(TimeProvider.System, 90)).ToQueryString();
        var unbounded = SearchEndpoints.StatementGroups(db, Query("SourceRank"), since: null).ToQueryString();

        Assert.NotEqual(windowed, unbounded);
        Assert.Contains("[TimestampUtc]", windowed, StringComparison.Ordinal);
    }

    [Fact]
    public void LatestRunsQuery_TranslatesTheGroupedTopOneServerSide()
    {
        using var db = Context();

        var sql = LineageEndpoints.LatestRunsQuery(db, [Guid.NewGuid(), Guid.NewGuid()]).ToQueryString();

        // The grouped top-1 ("newest run per pipeline") has to be the database's work: EF renders it as a
        // windowed/correlated query, and a translation failure here would surface as a runtime 500 on the
        // objects/refresh endpoint.
        Assert.Contains("[Run]", sql, StringComparison.Ordinal);
        Assert.Contains("[StartUtc]", sql, StringComparison.Ordinal);
        Assert.Contains("[PipelineId]", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void StatementCutoff_TreatsZeroDaysAsAllHistory()
    {
        Assert.Null(SearchEndpoints.StatementCutoff(TimeProvider.System, 0));
        Assert.NotNull(SearchEndpoints.StatementCutoff(TimeProvider.System, 1));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var at = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
