using System.Globalization;
using System.Net;
using SqlFlow.Acquire.Engine;
using SqlFlow.Core.Acquire;
using SqlFlow.Core.Runs;
using Xunit;

namespace SqlFlow.Acquire.Tests;

public sealed class EngineTests
{
    private const string BaseUrl = "https://api.test.local";
    private static readonly DateTimeOffset Now = new(2026, 7, 9, 12, 0, 0, TimeSpan.Zero);

    private static AcquireFlow Flow(AcquireSource source, string landingDir, string pathTemplate = "data/{page}", AcquireIncremental? incremental = null)
        => new()
        {
            Name = "Test_Flow",
            Items = [new AcquireItem
            {
                Source = source,
                Landing = new AcquireLanding { Target = landingDir, PathTemplate = pathTemplate },
            }],
            Incremental = incremental,
        };

    private static string Query(Uri uri, string key)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            var name = eq < 0 ? pair : pair[..eq];
            if (Uri.UnescapeDataString(name) == key)
            {
                return eq < 0 ? string.Empty : Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }

        return string.Empty;
    }

    private static async Task<(AcquireRunResult Result, IReadOnlyList<string> Files, StubHttpHandler Handler)> RunAsync(
        StubHttpHandler handler, AcquireSource source, string pathTemplate = "data/{page}", AcquireIncremental? incremental = null)
    {
        var engine = TestEngine.Create(handler, new FakeSecrets(("token", "SECRET123")), new FixedClock(Now), out var dir);
        var flow = Flow(source, dir, pathTemplate, incremental);
        var result = await engine.RunAsync(flow, Guid.NewGuid(), NullRunEventSink.Instance, null, CancellationToken.None);
        return (result, TestEngine.LandedFiles(dir), handler);
    }

    [Fact]
    public async Task Backfill_reprocess_relands_an_unchanged_payload()
    {
        // The API backfill: an acquire flow re-fetches and RE-LANDS its payloads even when byte-identical, so the
        // landed file's timestamp is bumped and the downstream file-ingestion and silver flows re-read it. A normal
        // re-run keeps skip-unchanged (an idempotent poll writes nothing); a reprocess run disables it.
        const string payload = """[{"id":1,"name":"a"}]""";
        var handler = new StubHttpHandler().Json("/orders", _ => payload);
        var source = new AcquireSource
        {
            BaseUrl = BaseUrl,
            Auth = new AcquireAuth { Type = AcquireAuthType.Bearer, SecretRef = "${test:token}" },
            Request = new AcquireRequest { Path = "/orders" },
        };
        var engine = TestEngine.Create(handler, new FakeSecrets(("token", "SECRET123")), new FixedClock(Now), out var dir);
        var flow = new AcquireFlow
        {
            Name = "Test_Flow",
            Items = [new AcquireItem
            {
                Source = source,
                Landing = new AcquireLanding
                {
                    Target = dir, PathTemplate = "orders", Overwrite = true, SkipUnchanged = true,
                },
            }],
        };

        // First fetch lands the file.
        Assert.True((await engine.RunAsync(flow, Guid.NewGuid(), NullRunEventSink.Instance, null, CancellationToken.None)).Success);
        var file = TestEngine.LandedFiles(dir).Single();
        var landed = File.GetLastWriteTimeUtc(file);

        // A normal re-run of the identical payload does not rewrite it: the timestamp is unchanged.
        Assert.True((await engine.RunAsync(flow, Guid.NewGuid(), NullRunEventSink.Instance, null, CancellationToken.None)).Success);
        Assert.Equal(landed, File.GetLastWriteTimeUtc(file));

        // A reprocess (ReprocessFiles) re-lands the identical payload, bumping its timestamp.
        Assert.True((await engine.RunAsync(
            flow, Guid.NewGuid(), NullRunEventSink.Instance, null, CancellationToken.None,
            new AcquireRunOverrides { ReprocessFiles = true })).Success);
        Assert.True(File.GetLastWriteTimeUtc(file) > landed);
    }

    [Fact]
    public async Task Bearer_auth_lands_raw_json_verbatim()
    {
        const string payload = """[{"id":1,"name":"a"}]""";
        var handler = new StubHttpHandler().Json("/orders", _ => payload);
        var source = new AcquireSource
        {
            BaseUrl = BaseUrl,
            Auth = new AcquireAuth { Type = AcquireAuthType.Bearer, SecretRef = "${test:token}" },
            Request = new AcquireRequest { Path = "/orders" },
        };

        var (result, files, h) = await RunAsync(handler, source, "orders");

        Assert.True(result.Success);
        Assert.Equal(1, result.FilesWritten);
        Assert.Equal(payload, await File.ReadAllTextAsync(files[0]));
        Assert.EndsWith(".json", files[0], StringComparison.Ordinal);
        Assert.Equal("Bearer SECRET123", h.Requests[0].Headers["Authorization"]);
    }

    [Fact]
    public async Task Multi_item_flow_fetches_every_endpoint_and_aggregates_one_result()
    {
        // One flow, two endpoints over the shared envelope: each item fetches its own path and lands to its own
        // folder, and the run result sums the per-item counters (mirroring a cpy flow's multi-step aggregate).
        var handler = new StubHttpHandler()
            .Json("/bikes", _ => """[{"BikeId":1}]""")
            .Json("/alert", _ => """[{"AlertId":9}]""");
        var engine = TestEngine.Create(handler, new FakeSecrets(), new FixedClock(Now), out var dir);
        AcquireItem Item(string name, string path) => new()
        {
            Name = name,
            Source = new AcquireSource { BaseUrl = BaseUrl, Request = new AcquireRequest { Path = path } },
            Landing = new AcquireLanding { Target = Path.Combine(dir, name), PathTemplate = name },
        };
        var flow = new AcquireFlow
        {
            Name = "Multi_Flow",
            Items = [Item("bikes", "/bikes"), Item("alert", "/alert")],
        };

        var result = await engine.RunAsync(flow, Guid.NewGuid(), NullRunEventSink.Instance, null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, result.Iterations);
        Assert.Equal(2, result.FilesWritten);
        Assert.Equal(2, result.Files.Count);
        Assert.Contains(handler.Requests, r => r.Uri.AbsolutePath == "/bikes");
        Assert.Contains(handler.Requests, r => r.Uri.AbsolutePath == "/alert");
        var landed = TestEngine.LandedFiles(dir);
        Assert.Contains(landed, f => f.Contains("bikes", StringComparison.Ordinal) && f.EndsWith(".json", StringComparison.Ordinal));
        Assert.Contains(landed, f => f.Contains("alert", StringComparison.Ordinal) && f.EndsWith(".json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Page_pagination_walks_until_empty_page()
    {
        var handler = new StubHttpHandler().Json("/items", req =>
        {
            var page = int.Parse(Query(req.RequestUri!, "page"), CultureInfo.InvariantCulture);
            return page <= 2 ? $"[{{\"id\":{page}}}]" : "[]";
        });
        var source = new AcquireSource
        {
            BaseUrl = BaseUrl,
            Request = new AcquireRequest { Path = "/items" },
            Pagination = new AcquirePagination { Strategy = AcquirePaginationStrategy.Page },
        };

        var (result, files, _) = await RunAsync(handler, source);

        Assert.Equal(2, result.FilesWritten);   // pages 1 and 2 land; page 3 is empty and ends the loop
        Assert.Equal(3, result.PagesFetched);   // fetched 1, 2, and the empty 3
        Assert.Equal(2, files.Count);
    }

    [Fact]
    public async Task Offset_pagination_advances_by_limit()
    {
        var seen = new List<int>();
        var handler = new StubHttpHandler().Json("/rows", req =>
        {
            var offset = int.Parse(Query(req.RequestUri!, "offset"), CultureInfo.InvariantCulture);
            seen.Add(offset);
            return offset < 20 ? """[{"x":1}]""" : "[]";
        });
        var source = new AcquireSource
        {
            BaseUrl = BaseUrl,
            Request = new AcquireRequest { Path = "/rows" },
            Pagination = new AcquirePagination { Strategy = AcquirePaginationStrategy.Offset, Limit = 10 },
        };

        var (result, _, _) = await RunAsync(handler, source);

        Assert.Equal([0, 10, 20], seen);
        Assert.Equal(2, result.FilesWritten);
    }

    [Fact]
    public async Task Link_header_pagination_follows_next()
    {
        var handler = new StubHttpHandler();
        handler.Route((req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.EndsWith("/feed", StringComparison.Ordinal))
            {
                var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""[{"n":1}]""") };
                r.Headers.TryAddWithoutValidation("Link", $"<{BaseUrl}/feed?cursor=2>; rel=\"next\"");
                return r;
            }

            if (url.Contains("cursor=2", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""[{"n":2}]""") };
            }

            return null;
        });
        var source = new AcquireSource
        {
            BaseUrl = BaseUrl,
            Request = new AcquireRequest { Path = "/feed" },
            Pagination = new AcquirePagination { Strategy = AcquirePaginationStrategy.LinkHeader },
        };

        var (result, _, _) = await RunAsync(handler, source);

        Assert.Equal(2, result.FilesWritten);
    }

    [Fact]
    public async Task Cursor_body_pagination_follows_next_cursor()
    {
        var handler = new StubHttpHandler().Json("/search", req =>
        {
            var cursor = Query(req.RequestUri!, "cursor");
            return cursor switch
            {
                "" => """{"items":[{"id":1}],"next":"c2"}""",
                "c2" => """{"items":[{"id":2}],"next":null}""",
                _ => """{"items":[]}""",
            };
        });
        var source = new AcquireSource
        {
            BaseUrl = BaseUrl,
            Request = new AcquireRequest { Path = "/search" },
            Pagination = new AcquirePagination
            {
                Strategy = AcquirePaginationStrategy.CursorBody,
                CursorPath = "$.next",
                RecordsPath = "$.items",
            },
        };

        var (result, _, _) = await RunAsync(handler, source);

        Assert.Equal(2, result.FilesWritten);
    }

    [Fact]
    public async Task List_iteration_fans_out_one_request_per_value()
    {
        var handler = new StubHttpHandler().Json("/parkings", req => $"[{{\"op\":\"{Query(req.RequestUri!, "operatorId")}\"}}]");
        var source = new AcquireSource
        {
            BaseUrl = BaseUrl,
            Request = new AcquireRequest
            {
                Path = "/parkings",
                Query = new Dictionary<string, string> { ["operatorId"] = "{operatorId}" },
            },
            Iterations =
            [
                new AcquireIteration { Kind = AcquireIterationKind.List, Variable = "operatorId", Values = ["14", "1295", "1251"] },
            ],
        };

        var (result, _, h) = await RunAsync(handler, source, "data/{operatorId}");

        Assert.Equal(3, result.Iterations);
        Assert.Equal(3, result.FilesWritten);
        Assert.Equal(["1251", "1295", "14"], h.Requests.Select(r => Query(r.Uri, "operatorId")).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Date_window_iteration_steps_day_by_day()
    {
        var handler = new StubHttpHandler().Json("/trips", _ => """[{"t":1}]""");
        var source = new AcquireSource
        {
            BaseUrl = BaseUrl,
            Request = new AcquireRequest
            {
                Path = "/trips",
                Query = new Dictionary<string, string> { ["from"] = "{window.from:yyyy-MM-dd}", ["to"] = "{window.to:yyyy-MM-dd}" },
            },
            Iterations =
            [
                new AcquireIteration { Kind = AcquireIterationKind.DateWindow, Granularity = AcquireWindowGranularity.Day, From = "now-3d", To = "now" },
            ],
        };

        var (result, _, h) = await RunAsync(handler, source, "data/{window.from:yyyyMMdd}");

        Assert.Equal(3, result.Iterations);     // 3 day steps across a 3-day window
        Assert.Contains(h.Requests, r => Query(r.Uri, "from") == "2026-07-06");
        Assert.Contains(h.Requests, r => Query(r.Uri, "to") == "2026-07-07");
    }

    [Fact]
    public async Task IdsFrom_iteration_discovers_then_fetches_each_id()
    {
        var handler = new StubHttpHandler()
            .Json("/bikes", _ => """[{"BikeId":"b1"},{"BikeId":"b2"}]""")
            .Json("/alert", req => $"[{{\"bike\":\"{Query(req.RequestUri!, "bikeId")}\"}}]");
        var source = new AcquireSource
        {
            BaseUrl = BaseUrl,
            Request = new AcquireRequest { Path = "/alert", Query = new Dictionary<string, string> { ["bikeId"] = "{bikeId}" } },
            Iterations =
            [
                new AcquireIteration
                {
                    Kind = AcquireIterationKind.IdsFrom,
                    Variable = "bikeId",
                    IdRequest = new AcquireRequest { Path = "/bikes" },
                    IdPath = "$[*].BikeId",
                },
            ],
        };

        var (result, _, h) = await RunAsync(handler, source, "data/{bikeId}");

        Assert.Equal(2, result.Iterations);
        Assert.Equal(2, result.FilesWritten);
        Assert.Contains(h.Requests, r => r.Uri.AbsolutePath.EndsWith("/alert", StringComparison.Ordinal) && Query(r.Uri, "bikeId") == "b1");
    }

    [Theory]
    [InlineData(8)]   // the default: the fan-out lands concurrently through the shared, thread-safe sinks
    [InlineData(1)]   // pinned sequential: the same set must land, exercising the non-parallel path
    public async Task Fan_out_lands_every_file_at_any_concurrency(int concurrency)
    {
        // A wide per-id fan-out (25 bikes -> 25 files) run through one shared LandingPipeline. Under the concurrent
        // default the lands race on the name-reservation set, the counters, and the manifest; every id must still
        // land exactly once, with no lost request and no lost file. Pinning concurrency to 1 must produce the same
        // set, so the two code paths agree.
        var ids = Enumerable.Range(1, 25).Select(i => $"b{i}").ToList();
        var bikesJson = "[" + string.Join(",", ids.Select(id => $"{{\"BikeId\":\"{id}\"}}")) + "]";
        var handler = new StubHttpHandler()
            .Json("/bikes", _ => bikesJson)
            .Json("/alert", req => $"[{{\"bike\":\"{Query(req.RequestUri!, "bikeId")}\"}}]");
        var engine = TestEngine.Create(handler, new FakeSecrets(("token", "SECRET123")), new FixedClock(Now), out var dir);
        var flow = new AcquireFlow
        {
            Name = "Test_Flow",
            Items =
            [
                new AcquireItem
                {
                    // AcquireFlow.Source (the shared envelope, where reliability/concurrency lives) is Items[0].Source.
                    Source = new AcquireSource
                    {
                        BaseUrl = BaseUrl,
                        Reliability = new AcquireReliability { Concurrency = concurrency },
                        Request = new AcquireRequest { Path = "/alert", Query = new Dictionary<string, string> { ["bikeId"] = "{bikeId}" } },
                        Iterations =
                        [
                            new AcquireIteration
                            {
                                Kind = AcquireIterationKind.IdsFrom,
                                Variable = "bikeId",
                                IdRequest = new AcquireRequest { Path = "/bikes" },
                                IdPath = "$[*].BikeId",
                            },
                        ],
                    },
                    Landing = new AcquireLanding { Target = dir, PathTemplate = "data/{bikeId}" },
                },
            ],
        };

        var result = await engine.RunAsync(flow, Guid.NewGuid(), NullRunEventSink.Instance, null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(25, result.Iterations);
        Assert.Equal(25, result.FilesWritten);
        Assert.Equal(25, TestEngine.LandedFiles(dir).Count);
        var landedIds = handler.Requests
            .Where(r => r.Uri.AbsolutePath.EndsWith("/alert", StringComparison.Ordinal))
            .Select(r => Query(r.Uri, "bikeId"))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(ids.OrderBy(x => x, StringComparer.Ordinal), landedIds);
    }

    [Fact]
    public async Task Token_exchange_auth_acquires_then_applies_bearer()
    {
        var handler = new StubHttpHandler()
            .Json("/oauth/token", _ => """{"access_token":"AT-999"}""")
            .Json("/secure", _ => """[{"ok":true}]""");
        var source = new AcquireSource
        {
            BaseUrl = BaseUrl,
            Auth = new AcquireAuth
            {
                Type = AcquireAuthType.OAuth2ClientCredentials,
                Token = new AcquireTokenEndpoint
                {
                    Url = $"{BaseUrl}/oauth/token",
                    BodyKind = AcquireBodyKind.Form,
                    Body = new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["client_secret"] = "${test:token}" },
                },
            },
            Request = new AcquireRequest { Path = "/secure" },
        };

        var (result, _, h) = await RunAsync(handler, source, "secure");

        Assert.True(result.Success);
        Assert.Contains(h.Requests, r => r.Uri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal) && r.Body.Contains("client_credentials", StringComparison.Ordinal));
        var secure = h.Requests.Single(r => r.Uri.AbsolutePath.EndsWith("/secure", StringComparison.Ordinal));
        Assert.Equal("Bearer AT-999", secure.Headers["Authorization"]);
    }

    [Fact]
    public async Task Skip_empty_does_not_land_empty_page()
    {
        var handler = new StubHttpHandler().Json("/empty", _ => "[]");
        var source = new AcquireSource { BaseUrl = BaseUrl, Request = new AcquireRequest { Path = "/empty" } };

        var (result, files, _) = await RunAsync(handler, source, "data/x");

        Assert.Equal(0, result.FilesWritten);
        Assert.Equal(1, result.Skipped);
        Assert.Empty(files);
    }

    [Fact]
    public async Task Keyset_pagination_resumes_and_advances_watermark()
    {
        var handler = new StubHttpHandler().Json("/events", req =>
        {
            var after = Query(req.RequestUri!, "idAfter");
            return after switch
            {
                "" or "0" => """[{"id":10},{"id":20}]""",
                "20" => """[{"id":30}]""",
                _ => "[]",
            };
        });
        var source = new AcquireSource
        {
            BaseUrl = BaseUrl,
            Request = new AcquireRequest { Path = "/events" },
            Pagination = new AcquirePagination { Strategy = AcquirePaginationStrategy.Keyset, KeysetIdPath = "id" },
        };
        var incremental = new AcquireIncremental { Source = AcquireWatermarkSource.Response, Column = "id" };

        var (result, _, _) = await RunAsync(handler, source, "data/{page}", incremental);

        Assert.Equal(2, result.FilesWritten);   // pages with id 10/20 and id 30 land; the empty follow-up page ends the loop
        Assert.Equal("30", result.WatermarkAfter);
    }
}
