using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using Xunit;
using Doc = SqlFlow.Delivery.Tests.IndexedSearch.Doc;
using Fault = SqlFlow.Delivery.Tests.IndexedSearch.Fault;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The read in createTime order (<see cref="SearchPaging.CreateTime"/>) against a search service that evaluates the queries
/// it is sent over an index that changes under the read: every page is a plain search at offset 0 from the last record handed
/// out, every record the index holds from the start of the read to its end is handed out once, and a read that cannot be
/// whole ends in an exception, whatever fails and wherever.
/// </summary>
public sealed class OsduSearchOrderedTests
{
    private const string Kind = "osdu:wks:master-data--Wellbore:1.*";

    /// <summary>When the first record was created; the others follow a second apart, in runs that share one instant.</summary>
    private static readonly long Start = DateTimeOffset.Parse("2024-03-01T08:00:00Z", CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

    [Fact]
    public async Task A_search_is_read_whole_in_createTime_then_id_order_each_record_once_every_page_at_offset_0()
    {
        // Seven records share each instant, as the records one storage request creates do, so page boundaries fall among
        // records sharing a createTime and only the id tells them apart.
        var service = new IndexedSearch(Docs(2500));
        var (search, runtime) = Reader(service);
        using (runtime)
        {
            var read = await ReadAsync(search, Query());

            Assert.Equal(service.InOrder(), read.Ids);
            Assert.Equal(2500, read.Total);
            Assert.Equal([1000, 1000, 500], read.PageSizes);

            // The newest record first, which the read reads no further than.
            var posts = service.Posts();
            var newest = posts[0];
            Assert.Equal(["createTime", "id"], Names(newest["sort"]!["field"]));
            Assert.Equal(["DESC", "DESC"], Names(newest["sort"]!["order"]));
            Assert.Equal(1, newest["limit"]!.GetValue<int>());
            Assert.Equal(["id", "createTime"], Names(newest["returnedFields"]));
            Assert.True(newest["trackTotalCount"]!.GetValue<bool>());

            // Every page at offset 0, sorted on createTime and id; the first counts what it reads, and each after it starts
            // past the last record the one before handed out.
            var pages = service.Pages();
            Assert.Equal(3, pages.Count);
            Assert.All(pages, p =>
            {
                Assert.Null(p["offset"]);
                Assert.Equal(["createTime", "id"], Names(p["sort"]!["field"]));
            });
            var end = IndexedSearch.Time(Start + (2499 / 7 * 1000L));
            Assert.Equal($"(data.FacilityName:*) AND (createTime:[* TO {end}])", pages[0]["query"]!.GetValue<string>());
            Assert.True(pages[0]["trackTotalCount"]!.GetValue<bool>());
            Assert.All(pages.Skip(1), p => Assert.Null(p["trackTotalCount"]));
            var last = service.InOrder()[999];
            var at = IndexedSearch.Time(Start + (999 / 7 * 1000L));
            Assert.Equal(
                $"(data.FacilityName:*) AND ((createTime:{{{at} TO {end}] OR (createTime:[{at} TO {at}] AND id:{{\"{last}\" TO *])))",
                pages[1]["query"]!.GetValue<string>());

            // The newest, three pages and one count of the whole; nothing through the cursor.
            Assert.Equal(5, posts.Count);
            Assert.Empty(service.CursorPosts());
        }
    }

    [Fact]
    public async Task A_read_of_one_page_asks_for_the_newest_record_and_the_page_and_nothing_else()
    {
        var service = new IndexedSearch(Docs(5));
        var (search, runtime) = Reader(service);
        using (runtime)
        {
            var read = await ReadAsync(search, Query());

            Assert.Equal(service.InOrder(), read.Ids);
            Assert.Equal(5, read.Total);
            Assert.Equal(2, service.Posts().Count);
        }
    }

    [Fact]
    public async Task An_empty_search_is_whole_with_one_empty_page()
    {
        var service = new IndexedSearch([]);
        var (search, runtime) = Reader(service);
        using (runtime)
        {
            var read = await ReadAsync(search, Query());

            Assert.Empty(read.Ids);
            Assert.Equal(0, read.Total);
            Assert.Equal([0], read.PageSizes);
            Assert.Single(service.Posts());
        }
    }

    [Fact]
    public async Task Records_created_while_the_read_runs_are_left_to_the_next_read()
    {
        var docs = Docs(2500);
        var before = docs.OrderBy(d => d.Created).ThenBy(d => d.Id, StringComparer.Ordinal).Select(d => d.Id).ToList();
        var service = new IndexedSearch(docs)
        {
            Before = (call, body, index) =>
            {
                if (call == 3)
                {
                    index.Add(new Doc("dev:master-data--Wellbore:new-1", Start + 10_000_000, Facility("new 1")));
                    index.Add(new Doc("dev:master-data--Wellbore:new-2", Start + 10_000_000, Facility("new 2")));
                }
            },
        };
        var (search, runtime) = Reader(service);
        using (runtime)
        {
            var read = await ReadAsync(search, Query());

            Assert.Equal(before, read.Ids);
        }
    }

    [Theory]
    [InlineData(nameof(Fault.Unavailable), 3)]
    [InlineData(nameof(Fault.Dropped), 3)]
    [InlineData(nameof(Fault.Garbled), 1)]
    [InlineData(nameof(Fault.Nameless), 1)]
    public async Task A_page_that_fails_is_asked_again_from_the_last_record_handed_out_and_nothing_is_lost_or_repeated(string failure, int attempts)
    {
        // The third page fails for as many attempts as the retry policy makes of it (a status or a dropped connection is
        // repeated by the policy; an answer that came back is not), so the first pass ends there.
        var fault = Enum.Parse<Fault>(failure);
        var asked = 0;
        var service = new IndexedSearch(Docs(3500))
        {
            Faults = (_, body) =>
            {
                if (!IndexedSearch.IsPage(body))
                {
                    return Fault.None;
                }

                asked++;
                return asked >= 3 && asked < 3 + attempts ? fault : Fault.None;
            },
        };
        var (search, runtime) = Reader(service);
        using (runtime)
        {
            var read = await ReadAsync(search, Query());

            Assert.Equal(service.InOrder(), read.Ids);
            Assert.Equal(read.Ids.Count, read.Ids.Distinct(StringComparer.Ordinal).Count());

            // The second pass asks the failed page again, from the same record, and never the pages before it.
            var pages = service.Pages();
            Assert.Equal(pages[2]["query"]!.GetValue<string>(), pages[2 + attempts]["query"]!.GetValue<string>());
            Assert.All(pages.Skip(1), p => Assert.Contains("id:{", p["query"]!.GetValue<string>(), StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Two_passes_that_fail_end_the_read_naming_where_each_stopped()
    {
        var asked = 0;
        var service = new IndexedSearch(Docs(3500)) { Faults = (_, body) => IndexedSearch.IsPage(body) && ++asked >= 3 ? Fault.Unavailable : Fault.None };
        var (search, runtime) = Reader(service);
        using (runtime)
        {
            var handedOut = new List<string>();
            var error = await Assert.ThrowsAsync<DeliveryException>(async () =>
            {
                await foreach (var page in search.PagesAsync(Query(), OsduSearch.MaxPage, CancellationToken.None))
                {
                    handedOut.AddRange(page.Hits.Select(h => OsduSearch.IdOf(h)!));
                }
            });

            var last = service.InOrder()[1999];
            Assert.Contains($"The search of kind {Kind} for 'data.FacilityName:*' could not be read whole in createTime order in 2 passes", error.Message, StringComparison.Ordinal);
            Assert.Contains($"pass 1: page 3 failed after record {last} (createTime", error.Message, StringComparison.Ordinal);
            Assert.Contains($"pass 2: page 3 failed after record {last} (createTime", error.Message, StringComparison.Ordinal);
            Assert.Contains("HTTP 503", error.Message, StringComparison.Ordinal);
            Assert.Equal(service.InOrder().Take(2000), handedOut);
        }
    }

    [Fact]
    public async Task A_record_indexed_late_behind_the_read_is_found_by_reading_again_only_the_batch_it_fell_in()
    {
        // 25,000 records make three batches; while the read is past the first, a record created long ago is indexed into
        // it, as one whose indexing was held up would be.
        const string Late = "dev:master-data--Wellbore:late";
        var lateCreated = Start + (500 * 1000L) + 1;
        var asked = 0;
        var service = new IndexedSearch(Docs(25_000))
        {
            Before = (_, body, index) =>
            {
                if (IndexedSearch.IsPage(body) && ++asked == 16)
                {
                    index.Add(new Doc(Late, lateCreated, Facility("late")));
                }
            },
        };
        var (search, runtime) = Reader(service);
        using (runtime)
        {
            var read = await ReadAsync(search, Query());

            Assert.Equal(25_001, read.Ids.Count);
            Assert.Equal(25_001, read.Ids.Distinct(StringComparer.Ordinal).Count());
            Assert.Single(read.Ids, id => id == Late);
            Assert.Equal(Late, read.Ids[^1]);

            // The first batch closes after the 10,000th record, at the end of the run of records sharing its createTime.
            var firstBatchEnd = IndexedSearch.Time(Start + (10_002 / 7 * 1000L));
            var counts = service.Posts().Where(p => !IndexedSearch.IsPage(p) && p["sort"] is null).Select(p => p["query"]!.GetValue<string>()).ToList();
            Assert.Equal(5, counts.Count);
            Assert.Contains($"(data.FacilityName:*) AND (createTime:[* TO {firstBatchEnd}])", counts);

            // The second pass reads the first batch alone: 10,004 records now, in eleven pages.
            var pages = service.Pages();
            Assert.Equal(26 + 11, pages.Count);
            Assert.All(pages.Skip(26), p => Assert.Contains($"TO {firstBatchEnd}]", p["query"]!.GetValue<string>(), StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Records_deleted_while_the_read_runs_leave_it_whole()
    {
        var order = Docs(3000).OrderBy(d => d.Created).ThenBy(d => d.Id, StringComparer.Ordinal).Select(d => d.Id).ToList();
        var aheadOfTheRead = order.Skip(2500).Take(10).ToList();
        var alreadyRead = order.Take(10).ToList();
        var deleting = new IndexedSearch(Docs(3000))
        {
            Before = (call, _, index) =>
            {
                if (call == 3)
                {
                    foreach (var id in aheadOfTheRead.Concat(alreadyRead))
                    {
                        index.Remove(id);
                    }
                }
            },
        };
        var (search, runtime) = Reader(deleting);
        using (runtime)
        {
            var read = await ReadAsync(search, Query());

            Assert.Equal(order.Except(aheadOfTheRead), read.Ids);
        }
    }

    [Fact]
    public async Task A_service_that_refuses_the_order_fails_the_read_with_its_words()
    {
        var service = new IndexedSearch(Docs(10)) { Faults = (call, _) => call == 1 ? Fault.Refused : Fault.None };
        var (search, runtime) = Reader(service);
        using (runtime)
        {
            var error = await Assert.ThrowsAsync<DeliveryException>(() => ReadAsync(search, Query()));

            Assert.Contains($"The search service refused the search of kind {Kind} for 'data.FacilityName:*' in createTime order", error.Message, StringComparison.Ordinal);
            Assert.Contains("sort on id is not supported here", error.Message, StringComparison.Ordinal);
            Assert.Contains("source.paging: cursor", error.Message, StringComparison.Ordinal);
            Assert.Single(service.Posts());
        }
    }

    [Fact]
    public async Task A_service_that_does_not_keep_to_the_order_fails_the_read_rather_than_guess_what_it_skipped()
    {
        // The service holds the records newest first and answers in that order whatever it is asked.
        var docs = Docs(2500);
        docs.Reverse();
        var service = new IndexedSearch(docs) { IgnoreSort = true };
        var (search, runtime) = Reader(service);
        using (runtime)
        {
            var error = await Assert.ThrowsAsync<DeliveryException>(() => ReadAsync(search, Query()));

            Assert.Contains($"The search of kind {Kind} for 'data.FacilityName:*' came back out of createTime order: record", error.Message, StringComparison.Ordinal);
            Assert.Contains("source.paging: cursor", error.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(3000, 5)]
    [InlineData(5, 2)]
    [InlineData(0, 3)]
    public async Task Records_without_a_createTime_fail_the_read_rather_than_be_left_out(int dated, int undated)
    {
        var docs = Docs(dated);
        docs.AddRange(Enumerable.Range(0, undated).Select(i => new Doc("dev:master-data--Wellbore:undated-" + i.ToString(CultureInfo.InvariantCulture), null, Facility("undated"))));
        var service = new IndexedSearch(docs);
        var (search, runtime) = Reader(service);
        using (runtime)
        {
            var error = await Assert.ThrowsAsync<DeliveryException>(() => ReadAsync(search, Query()));

            Assert.Contains(
                $"{undated} record(s) the search of kind {Kind} for 'data.FacilityName:*' matches hold no createTime",
                error.Message, StringComparison.Ordinal);
            Assert.Contains("source.paging: cursor", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_narrowed_search_asks_for_the_id_and_createTime_it_is_ordered_by_and_hands_out_only_what_was_asked()
    {
        var service = new IndexedSearch(Docs(1500));
        var (search, runtime) = Reader(service);
        using (runtime)
        {
            var narrowed = await ReadAsync(search, Query() with { ReturnedFields = ["data.FacilityName"] });

            Assert.All(service.Pages(), p => Assert.Equal(["data.FacilityName", "id", "createTime"], Names(p["returnedFields"])));
            Assert.Equal(service.InOrder(), narrowed.Ids);
            Assert.All(narrowed.Hits, hit =>
            {
                var record = JsonNode.Parse(hit)!.AsObject();
                Assert.Equal(["data", "id"], record.Select(p => p.Key).Order(StringComparer.Ordinal));
                Assert.NotNull(record["data"]!["FacilityName"]);
            });

            var keeping = await ReadAsync(search, Query() with { ReturnedFields = ["createTime", "data.FacilityName"] });
            Assert.All(keeping.Hits, hit => Assert.NotNull(JsonNode.Parse(hit)!["createTime"]));
        }
    }

    [Fact]
    public async Task A_search_in_an_order_the_caller_chose_is_read_through_the_cursor()
    {
        var service = new IndexedSearch(Docs(10));
        var (search, runtime) = Reader(service);
        using (runtime)
        {
            await ReadAsync(search, Query() with { Sort = [("data.FacilityName", false)] });

            Assert.Single(service.CursorPosts());
            Assert.Empty(service.Posts());
        }
    }

    [Fact]
    public async Task A_cancelled_read_asks_for_nothing_more()
    {
        var service = new IndexedSearch(Docs(3500));
        var (search, runtime) = Reader(service);
        using (runtime)
        using (var cancel = new CancellationTokenSource())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (var page in search.PagesAsync(Query(), OsduSearch.MaxPage, cancel.Token))
                {
                    await cancel.CancelAsync();
                }
            });

            Assert.Equal(2, service.Posts().Count);
        }
    }

    [Fact]
    public void Ids_are_ordered_as_the_index_orders_a_keyword_by_code_point()
    {
        // U+E000 comes before U+1F600 by code point and by UTF-8 bytes, while UTF-16 puts the surrogate pair first.
        var privateUse = "a" + (char)0xE000;
        var emoji = "a" + char.ConvertFromUtf32(0x1F600);
        Assert.True(string.CompareOrdinal(privateUse, emoji) > 0);
        Assert.True(OsduSearch.CompareIds(privateUse, emoji) < 0);
        Assert.True(OsduSearch.CompareIds("dev:a:1", "dev:a:10") < 0);
        Assert.True(OsduSearch.CompareIds("dev:a:2", "dev:a:10") > 0);
        Assert.Equal(0, OsduSearch.CompareIds("dev:a:1", "dev:a:1"));
    }

    private static OsduSearchQuery Query() => new() { Kind = Kind, Query = "data.FacilityName:*" };

    private static Dictionary<string, string> Facility(string name) => new(StringComparer.Ordinal) { ["FacilityName"] = name };

    /// <summary>
    /// Records created a second apart in runs of seven that share an instant, their ids in no order of their creation, so a
    /// read that kept to the createTime alone would mix them up.
    /// </summary>
    private static List<Doc> Docs(int count)
        => Enumerable.Range(0, count)
            .Select(i => new Doc(
                "dev:master-data--Wellbore:" + (i * 7919L % 1_000_003).ToString("D7", CultureInfo.InvariantCulture),
                Start + (i / 7 * 1000L),
                Facility("WB " + i.ToString(CultureInfo.InvariantCulture))))
            .ToList();

    private static List<string> Names(JsonNode? array) => array!.AsArray().Select(n => n!.GetValue<string>()).ToList();

    private static (OsduSearch Search, HttpRuntime Runtime) Reader(IndexedSearch service)
    {
        // Three attempts with the shortest backoff, so the retry policy's own repeats show.
        var runtime = new HttpRuntime(
            new FlowReliability { Retry = new FlowRetry { Attempts = 3, BaseDelayMs = 1, MaxDelayMs = 1 } },
            new SecretResolver([new EnvSecretProvider()]), handler: service, allowLoopback: true);
        var client = new OsduHttpClient(
            runtime, "http://localhost", new TargetAuth { Type = TargetAuthType.None },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
        return (new OsduSearch(client, "/api/search/v2/query", "/api/search/v2/query_with_cursor", NullLogger.Instance, SearchPaging.CreateTime), runtime);
    }

    private sealed record Read(List<string> Ids, long? Total, List<int> PageSizes, List<string> Hits);

    private static async Task<Read> ReadAsync(OsduSearch search, OsduSearchQuery query)
    {
        var ids = new List<string>();
        var sizes = new List<int>();
        var hits = new List<string>();
        long? total = null;
        var first = true;
        await foreach (var page in search.PagesAsync(query, OsduSearch.MaxPage, CancellationToken.None))
        {
            if (first)
            {
                total = page.TotalCount;
                first = false;
            }

            sizes.Add(page.Hits.Count);
            ids.AddRange(page.Hits.Select(h => OsduSearch.IdOf(h)!));
            hits.AddRange(page.Hits.Select(h => h.GetRawText()));
        }

        return new Read(ids, total, sizes, hits);
    }
}
