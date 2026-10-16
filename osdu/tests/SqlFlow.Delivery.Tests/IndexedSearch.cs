using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A search service over an index of records, answering the plain search (openapi search v2, POST /query) as the service
/// does: it evaluates the query, sorts on the fields asked with a missing value last whichever the order, pages from the
/// offset, and counts exactly when asked to track the total. The query language is the part of Lucene the module writes:
/// AND, OR, NOT and parentheses, ranges with inclusive, exclusive and open bounds (quoted or not), <c>_exists_:field</c>,
/// <c>field:*</c> and <c>field:value</c>. The index can change between requests, and a fault can be decided for any request;
/// requests are counted from 1 across every POST to the plain search.
/// </summary>
internal sealed class IndexedSearch : HttpMessageHandler
{
    private readonly Lock _gate = new();
    private readonly List<Doc> _docs = [];
    private readonly List<JsonObject> _posts = [];
    private readonly List<JsonObject> _cursorPosts = [];

    public IndexedSearch(IEnumerable<Doc> docs)
    {
        _docs.AddRange(docs);
    }

    /// <summary>One record of the index: its id, its createTime in milliseconds (null when it holds none) and its data.</summary>
    public sealed record Doc(string Id, long? Created, IReadOnlyDictionary<string, string>? Data = null)
    {
        public string Kind { get; init; } = "osdu:wks:master-data--Wellbore:1.0.0";
    }

    /// <summary>What becomes of one request.</summary>
    public enum Fault
    {
        /// <summary>Answered as the service would.</summary>
        None,

        /// <summary>A 503 from a gateway, which the retry policy repeats.</summary>
        Unavailable,

        /// <summary>The connection drops before the answer arrives.</summary>
        Dropped,

        /// <summary>An answer that is not JSON.</summary>
        Garbled,

        /// <summary>The answer, with one more hit that carries no id.</summary>
        Nameless,

        /// <summary>The service refuses the query (400).</summary>
        Refused,
    }

    /// <summary>What becomes of the request numbered so, with its body; none by default.</summary>
    public Func<int, JsonObject, Fault> Faults { get; init; } = (_, _) => Fault.None;

    /// <summary>Called before the request numbered so is answered, with its body, to change the index under the read.</summary>
    public Action<int, JsonObject, IndexedSearch>? Before { get; init; }

    /// <summary>Whether the service ignores the order it is asked for and answers in the order it holds the records.</summary>
    public bool IgnoreSort { get; init; }

    /// <summary>The bodies of the plain searches, in order.</summary>
    public List<JsonObject> Posts()
    {
        lock (_gate)
        {
            return [.. _posts];
        }
    }

    /// <summary>The bodies of the cursor searches, in order; the cursor answers that it matches nothing.</summary>
    public List<JsonObject> CursorPosts()
    {
        lock (_gate)
        {
            return [.. _cursorPosts];
        }
    }

    /// <summary>The pages of a read in createTime order: the requests sorted ascending.</summary>
    public List<JsonObject> Pages() => Posts().Where(IsPage).ToList();

    public static bool IsPage(JsonObject body)
        => body["sort"]?["order"]?.AsArray().FirstOrDefault()?.GetValue<string>() == "ASC";

    /// <summary>Adds a record, as the indexer would; called from <see cref="Before"/>, under the service's lock.</summary>
    public void Add(Doc doc) => _docs.Add(doc);

    /// <summary>Removes a record, as a delete would; called from <see cref="Before"/>, under the service's lock.</summary>
    public void Remove(string id) => _docs.RemoveAll(d => d.Id == id);

    /// <summary>The ids of the records in the order a read in createTime order meets them.</summary>
    public List<string> InOrder()
    {
        lock (_gate)
        {
            return _docs.Where(d => d.Created is not null).OrderBy(d => d.Created).ThenBy(d => d.Id, StringComparer.Ordinal).Select(d => d.Id).ToList();
        }
    }

    public static string Time(long millis)
        => DateTimeOffset.FromUnixTimeMilliseconds(millis).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var text = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (_gate)
        {
            if (request.Method == HttpMethod.Post && path == "/api/search/v2/query_with_cursor")
            {
                _cursorPosts.Add(JsonNode.Parse(text!)!.AsObject());
                return Ok(new JsonObject { ["results"] = new JsonArray(), ["totalCount"] = 0 });
            }

            if (request.Method != HttpMethod.Post || path != "/api/search/v2/query")
            {
                return Status(HttpStatusCode.NotFound, "no route for " + path);
            }

            var body = JsonNode.Parse(text!)!.AsObject();
            _posts.Add(body);
            var call = _posts.Count;
            var fault = Faults(call, body);
            switch (fault)
            {
                case Fault.Unavailable:
                    return Status(HttpStatusCode.ServiceUnavailable, "busy");
                case Fault.Dropped:
                    throw new HttpRequestException("The response ended prematurely.");
                case Fault.Garbled:
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>proxy error</html>", Encoding.UTF8, "text/html") };
                case Fault.Refused:
                    return Status(HttpStatusCode.BadRequest, "Failed to create query: sort on id is not supported here");
            }

            Before?.Invoke(call, body, this);
            var query = body["query"]?.GetValue<string>();
            var condition = string.IsNullOrWhiteSpace(query) ? (Func<Doc, bool>)(_ => true) : Lucene.Parse(query);
            var matched = _docs.Where(condition).ToList();
            if (!IgnoreSort && body["sort"] is JsonObject sort)
            {
                var fields = sort["field"]!.AsArray().Select(f => f!.GetValue<string>()).ToList();
                var orders = sort["order"]!.AsArray().Select(o => o!.GetValue<string>()).ToList();
                matched.Sort((a, b) => Compare(a, b, fields, orders));
            }

            var offset = body["offset"]?.GetValue<int>() ?? 0;
            var limit = body["limit"]?.GetValue<int>() ?? 10;
            var page = matched.Skip(offset).Take(limit).ToList();
            var returned = body["returnedFields"]?.AsArray().Select(f => f!.GetValue<string>()).ToList();
            var results = new JsonArray(page.Select(d => (JsonNode?)Hit(d, returned)).ToArray());
            if (fault == Fault.Nameless)
            {
                results.Add(new JsonObject { ["kind"] = "osdu:wks:master-data--Wellbore:1.0.0" });
            }

            var tracked = body["trackTotalCount"]?.GetValue<bool>() ?? false;
            return Ok(new JsonObject { ["results"] = results, ["totalCount"] = tracked ? matched.Count : Math.Min(matched.Count, 10_000), ["aggregations"] = null });
        }
    }

    private static int Compare(Doc a, Doc b, List<string> fields, List<string> orders)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            var descending = orders[i] == "DESC";
            int compared;
            if (fields[i] == "createTime")
            {
                if (a.Created is null || b.Created is null)
                {
                    // A missing value sorts last whichever the order (the sort parser's missing: _last).
                    compared = (a.Created is null).CompareTo(b.Created is null);
                    if (compared != 0)
                    {
                        return compared;
                    }

                    continue;
                }

                compared = a.Created.Value.CompareTo(b.Created.Value);
            }
            else if (fields[i] == "id")
            {
                compared = string.CompareOrdinal(a.Id, b.Id);
            }
            else
            {
                throw new InvalidOperationException("The fake sorts on createTime and id only, not " + fields[i]);
            }

            if (compared != 0)
            {
                return descending ? -compared : compared;
            }
        }

        return 0;
    }

    private static JsonObject Hit(Doc doc, List<string>? returned)
    {
        var whole = new JsonObject { ["id"] = doc.Id, ["kind"] = doc.Kind };
        if (doc.Created is { } created)
        {
            whole["createTime"] = Time(created);
        }

        var data = new JsonObject();
        foreach (var (name, value) in doc.Data ?? new Dictionary<string, string>())
        {
            data[name] = value;
        }

        whole["data"] = data;
        if (returned is null || returned.Count == 0)
        {
            return whole;
        }

        var hit = new JsonObject();
        foreach (var field in returned)
        {
            if (field.StartsWith("data.", StringComparison.Ordinal))
            {
                var name = field["data.".Length..];
                if (data[name] is { } value)
                {
                    var projected = hit["data"] as JsonObject ?? new JsonObject();
                    projected[name] = value.DeepClone();
                    hit["data"] = projected;
                }
            }
            else if (whole[field] is { } value)
            {
                hit[field] = value.DeepClone();
            }
        }

        return hit;
    }

    private static HttpResponseMessage Ok(JsonObject body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Status(HttpStatusCode status, string message)
        => new(status) { Content = new StringContent(new JsonObject { ["code"] = (int)status, ["message"] = message }.ToJsonString(), Encoding.UTF8, "application/json") };

    /// <summary>The part of Lucene's query syntax the module writes, compiled to a test of one record.</summary>
    private static class Lucene
    {
        public static Func<Doc, bool> Parse(string query)
        {
            var tokens = Tokens(query);
            var at = 0;
            var condition = Or(tokens, ref at);
            return at == tokens.Count ? condition : throw new FormatException($"Unexpected '{tokens[at]}' in {query}");
        }

        private static Func<Doc, bool> Or(List<string> tokens, ref int at)
        {
            var left = And(tokens, ref at);
            while (at < tokens.Count && tokens[at] == "OR")
            {
                at++;
                var (a, b) = (left, And(tokens, ref at));
                left = d => a(d) || b(d);
            }

            return left;
        }

        private static Func<Doc, bool> And(List<string> tokens, ref int at)
        {
            var left = Not(tokens, ref at);
            while (at < tokens.Count && tokens[at] == "AND")
            {
                at++;
                var (a, b) = (left, Not(tokens, ref at));
                left = d => a(d) && b(d);
            }

            return left;
        }

        private static Func<Doc, bool> Not(List<string> tokens, ref int at)
        {
            if (tokens[at] != "NOT")
            {
                return Primary(tokens, ref at);
            }

            at++;
            var inner = Not(tokens, ref at);
            return d => !inner(d);
        }

        private static Func<Doc, bool> Primary(List<string> tokens, ref int at)
        {
            if (tokens[at] == "(")
            {
                at++;
                var inner = Or(tokens, ref at);
                return tokens[at++] == ")" ? inner : throw new FormatException("Unbalanced parentheses");
            }

            var term = tokens[at++];
            if (!term.EndsWith(':'))
            {
                throw new FormatException($"'{term}' names no field");
            }

            var field = term[..^1];
            var next = tokens[at++];
            if (field == "_exists_")
            {
                return d => Value(d, next) is not null;
            }

            if (next is "[" or "{")
            {
                var from = Bound(tokens[at++]);
                if (tokens[at++] != "TO")
                {
                    throw new FormatException("A range names TO between its bounds");
                }

                var to = Bound(tokens[at++]);
                var closing = tokens[at++];
                return d => InRange(Value(d, field), from, next == "[", to, closing == "]");
            }

            if (next == "*")
            {
                return d => Value(d, field) is not null;
            }

            var phrase = Bound(next);
            return d => Value(d, field) is { } value && Same(value, phrase, field);
        }

        private static string? Bound(string token)
            => token == "*" ? null : token.StartsWith('"') ? Unquote(token) : token;

        private static string Unquote(string token)
        {
            var text = new StringBuilder();
            for (var i = 1; i < token.Length - 1; i++)
            {
                if (token[i] == '\\')
                {
                    i++;
                }

                text.Append(token[i]);
            }

            return text.ToString();
        }

        private static object? Value(Doc doc, string field) => field switch
        {
            "id" => doc.Id,
            "kind" => doc.Kind,
            "createTime" => doc.Created,
            _ when field.StartsWith("data.", StringComparison.Ordinal) => doc.Data is { } data && data.TryGetValue(field["data.".Length..], out var value) ? value : null,
            _ => null,
        };

        private static bool Same(object value, string? text, string field)
            => text is not null && (field == "createTime" ? (long)value == Millis(text) : string.Equals((string)value, text, StringComparison.Ordinal));

        private static bool InRange(object? value, string? from, bool fromIncluded, string? to, bool toIncluded)
        {
            if (value is null)
            {
                return false;
            }

            int Against(string bound) => value is long millis ? millis.CompareTo(Millis(bound)) : string.CompareOrdinal((string)value, bound);
            return (from is null || (fromIncluded ? Against(from) >= 0 : Against(from) > 0))
                && (to is null || (toIncluded ? Against(to) <= 0 : Against(to) < 0));
        }

        private static long Millis(string text)
            => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal).ToUnixTimeMilliseconds();

        private static List<string> Tokens(string query)
        {
            // Outside a range a term ends after its field's colon; inside one, a bound runs to the next space or bracket,
            // colons and all, as a date's do.
            var tokens = new List<string>();
            var inRange = false;
            for (var i = 0; i < query.Length;)
            {
                var c = query[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                }
                else if (c is '(' or ')' or '[' or ']' or '{' or '}')
                {
                    inRange = c is '[' or '{' || (inRange && c is not (']' or '}'));
                    tokens.Add(c.ToString());
                    i++;
                }
                else if (c == '"')
                {
                    var start = i++;
                    while (query[i] != '"')
                    {
                        i += query[i] == '\\' ? 2 : 1;
                    }

                    tokens.Add(query[start..++i]);
                }
                else
                {
                    var start = i;
                    while (i < query.Length && !char.IsWhiteSpace(query[i]) && query[i] is not ('(' or ')' or '[' or ']' or '{' or '}' or '"'))
                    {
                        i++;
                        if (!inRange && query[i - 1] == ':')
                        {
                            break;
                        }
                    }

                    tokens.Add(query[start..i]);
                }
            }

            return tokens;
        }
    }
}
