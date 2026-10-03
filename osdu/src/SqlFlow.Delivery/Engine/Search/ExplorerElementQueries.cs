using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Search;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Engine.Search;

/// <summary>What the explorer asks the queries of: an element of a record of a kind, by the path the record inspector names it by.</summary>
public sealed record ExplorerElementRequest
{
    /// <summary>The longest path asked about.</summary>
    public const int MaxPathLength = 1024;

    /// <summary>The longest value a query is written for: far past the 256 characters the index keeps of a value whole.</summary>
    public const int MaxValueLength = 4096;

    /// <summary>The record's kind (a kind pattern is read as the kinds it matches).</summary>
    public required string Kind { get; init; }

    /// <summary>The element's path in the record, a list's items by their place: <c>data.GeoContexts[1].GeoPoliticalEntityID</c>.</summary>
    public required string Path { get; init; }

    /// <summary>Whether the element is a section (an object, a list, an item of a list) rather than a value.</summary>
    public bool Section { get; init; }

    /// <summary>The value the record holds there, for a value: a text, a number, a boolean, or null.</summary>
    public JsonElement? Value { get; init; }

    /// <summary>Why the request cannot be read; null when it can.</summary>
    public string? Problem()
    {
        if (string.IsNullOrWhiteSpace(Kind))
        {
            return "Name the kind of the record the element is in.";
        }

        if (ExplorerKinds.Problem(Kind.Trim()) is { } wrong)
        {
            return wrong;
        }

        if (string.IsNullOrWhiteSpace(Path))
        {
            return "Name the element's path in the record, such as data.FacilityName.";
        }

        if (Path.Length > MaxPathLength)
        {
            return string.Create(CultureInfo.InvariantCulture, $"The path is longer than the {MaxPathLength:N0} characters an element's path may be.");
        }

        return Value is { ValueKind: JsonValueKind.Object or JsonValueKind.Array }
            ? "A value is a text, a number, a boolean or null; an object or a list is a section."
            : null;
    }
}

/// <summary>A query that finds records by an element, and what it finds, in words.</summary>
/// <param name="Purpose"><c>equal</c> (the whole value), <c>words</c> (the words of a text, their case aside) or <c>exists</c> (any value there).</param>
/// <param name="Query">The Lucene query, as the search service's <c>query</c> takes it.</param>
/// <param name="Says">What the query finds, in words.</param>
public sealed record ExplorerElementQuery(string Purpose, string Query, string Says);

/// <summary>How the index holds a value: its path, how it is kept, and the nested or flattened list it sits in.</summary>
public sealed record ExplorerElementField(string Path, string Index, string? NestedPath, string? FlattenedPath);

/// <summary>
/// The queries that find records by an element of a record, with how the index holds it, in words; the saved template that
/// says so (null where none is saved and the index is guessed); why no query finds it, where none can; and notes.
/// </summary>
public sealed record ExplorerElementAnswer(
    string Kind, string Path, BlueprintTemplate? Template, ExplorerElementField? Field, string? Reading,
    IReadOnlyList<ExplorerElementQuery> Queries, string? Problem, IReadOnlyList<string> Notes);

/// <summary>
/// The Lucene queries that find records by an element of a record the explorer shows (osdu/docs/explorer.md, The query of
/// an element), so a person can see how a property or a section of a schema is searched without knowing how the platform
/// indexes it. How it is indexed is read from the saved template of the record's kind, as every search the module writes
/// reads it (<see cref="SearchFields"/>): text with a keyword sub-field for its whole value, a keyword, a number, a boolean
/// or a date, inside a nested list (reached with <c>nested(...)</c>) or a flattened one, or not indexed at all. Each query
/// is written by <see cref="OsduQuery"/>, as the module's own lookups and filters are, and said in words. Nothing is read
/// from OSDU.
/// </summary>
public static partial class ExplorerElementQueries
{
    /// <summary>The queries of <paramref name="request"/>'s element, its kind's template read from <paramref name="templates"/>.</summary>
    /// <exception cref="DeliveryException">The request cannot be read.</exception>
    public static async Task<ExplorerElementAnswer> DescribeAsync(ExplorerElementRequest request, ITemplateStore templates, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(templates);
        if (request.Problem() is { } problem)
        {
            throw new DeliveryException(problem);
        }

        var kind = request.Kind.Trim();
        var saved = await templates.ListAsync(ct).ConfigureAwait(false);
        var notes = new List<string>();
        static TemplateInfo? Newest(IReadOnlyList<TemplateInfo> matching) => matching.Count > 0 ? matching[0] : null;
        var match = Newest(DimensionBlueprints.Matching(kind, saved));
        if (match is null && ExplorerKinds.EntityTypeOf(kind) is { } type)
        {
            // Another version of the type describes the property as near as anything saved does, and says so.
            match = Newest(DimensionBlueprints.Matching($"*:*:{type}:*", saved));
            if (match is not null)
            {
                notes.Add($"No template of {kind} is saved, so it is read by {match.Kind}, the newest saved of its type.");
            }
        }

        var schema = match is null ? null : await templates.LoadAsync(match.Reference, ct).ConfigureAwait(false);
        return Describe(request, schema, notes);
    }

    /// <summary>The queries of <paramref name="request"/>'s element by <paramref name="schema"/>, or by its value where no template is saved.</summary>
    public static ExplorerElementAnswer Describe(ExplorerElementRequest request, SchemaSnapshot? schema, IReadOnlyList<string>? notesSoFar = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var kind = request.Kind.Trim();
        var notes = new List<string>(notesSoFar ?? []);
        var template = schema is null ? null : new BlueprintTemplate(schema.Kind, schema.Version);
        ExplorerElementAnswer Refused(string problem, string? at = null) => new(kind, at ?? request.Path, template, null, null, [], problem, notes);

        if (ElementPath.Parse(request.Path) is not { } element)
        {
            return Refused($"'{request.Path}' is not the path of an element of a record, such as data.FacilityName or data.GeoContexts[1].GeoTypeID.");
        }

        var path = element.Plain;
        var name = element.Names[^1];
        if (request.Section || element.EndsInItem && request.Value is null)
        {
            return DescribeSection(kind, element, schema, template, notes);
        }

        var value = ValueText(request.Value);
        if (value is { Length: > ExplorerElementRequest.MaxValueLength })
        {
            return Refused(string.Create(CultureInfo.InvariantCulture,
                $"This value is {value.Length:N0} characters, and a query is written for a value of at most {ExplorerElementRequest.MaxValueLength:N0}: the index keeps at most 256 characters of a value whole."), path);
        }

        // How the index holds the value: the record's own properties as the indexer maps them for every kind; data as the
        // kind's template says, or, with no template saved, as most properties are.
        OsduField field;
        var repeats = element.CrossesObjectList || element.EndsInItem;
        if (!SearchFields.IsDataPath(path))
        {
            var own = SearchFields.RecordProperty(path);
            if (own.Field is not { } recordField)
            {
                return Refused(own.Problem!, path);
            }

            field = recordField;
            repeats = own.Repeats;
        }
        else if (schema is not null)
        {
            var shape = SearchFields.ClassifyValue(schema, path);
            if (shape.Field is not { } schemaField)
            {
                return Refused(Sentence(shape.Problem!), path);
            }

            field = schemaField;
            repeats = shape.Repeats;
        }
        else
        {
            notes.Add($"No saved template of {kind} says how {path} is indexed, so it is read the way most properties are; save the kind's template on the Templates page to know for sure.");
            if (element.ObjectList is { } list)
            {
                notes.Add($"{list} is a list of objects. Without a template it cannot be told whether the platform indexes it nested (reached with nested(...)), flattened, or not at all, so these queries read it as a plain object and may find nothing.");
            }

            field = OsduField.Of(path, Guess(request.Value));
        }

        var described = new ExplorerElementField(field.Path, IndexName(field.Index), field.NestedPath, FlattenedOf(schema, path));
        var reading = Reading(described, field);
        var queries = new List<ExplorerElementQuery>();
        var held = value is null ? null : Clipped(value);

        if (value is null)
        {
            notes.Add("This record holds null here, which the index holds no value of; only whether a record holds one is asked.");
        }
        else
        {
            try
            {
                var says = repeats
                    ? $"The records holding {held} as one of their {name} values, the whole value as written."
                    : $"The records whose {name} is exactly {held}, the whole value as written.";
                queries.Add(new ExplorerElementQuery("equal", OsduQuery.Equal(field, value).Text, says));
            }
            catch (OsduQueryException ex)
            {
                notes.Add($"No query finds this whole value: {Sentence(ex.Message)}");
            }

            // A record's id is found by its whole value; its words (the parts of an id) find nothing the whole value does not.
            if (field.Index == OsduFieldIndex.Text && !TargetId.IsRecordReference(value.Trim()))
            {
                try
                {
                    var phrase = OsduQuery.Phrase(field.QueryPath, value);
                    var words = field.NestedPath is { } nestedPath ? OsduQuery.Nested(nestedPath, phrase) : phrase;
                    queries.Add(new ExplorerElementQuery("words", words.Text, $"The records whose {name} holds the words {held}, in this order and in any case: a value that contains them answers as well."));
                }
                catch (OsduQueryException ex)
                {
                    notes.Add($"No query finds these words: {Sentence(ex.Message)}");
                }
            }
        }

        if (field.NestedPath is null)
        {
            queries.Add(new ExplorerElementQuery("exists", $"_exists_:{field.Path}", $"The records that hold any {name}."));
        }
        else
        {
            notes.Add($"{name} sits in the nested list {field.NestedPath}, which a search reaches only inside nested({field.NestedPath}, (...)), one comparison at a time; whether a record holds one at all is not asked of it.");
        }

        return new ExplorerElementAnswer(kind, path, template, described, reading, queries, null, notes);
    }

    /// <summary>The queries of a section: whether a record holds it, where a query reaches it.</summary>
    private static ExplorerElementAnswer DescribeSection(string kind, ElementPath element, SchemaSnapshot? schema, BlueprintTemplate? template, List<string> notes)
    {
        var path = element.Plain;
        var name = element.Names[^1];
        ExplorerElementAnswer Refused(string problem) => new(kind, path, template, null, null, [], problem, notes);
        if (path == SearchFields.DataRoot)
        {
            return Refused("Every record holds data: search one of its properties, or one of its sections, rather than data itself.");
        }

        string? nested = null;
        string? flattened = null;
        if (SearchFields.IsDataPath(path))
        {
            if (schema is not null)
            {
                var shape = SearchFields.Section(schema, path);
                if (shape.Problem is { } problem)
                {
                    return Refused(Sentence(problem));
                }

                (nested, flattened) = (shape.NestedPath, shape.FlattenedPath);
            }
            else
            {
                notes.Add($"No saved template of {kind} says how {path} is indexed; a list of objects may be nested or not indexed at all, which this query cannot know. Save the kind's template on the Templates page to know for sure.");
            }
        }

        var itemOf = element.EndsInItem ? $"An item of {path}: " : string.Empty;
        if (nested is not null)
        {
            var where = nested == path ? $"{path} is a nested list" : $"{path} is inside the nested list {nested}";
            notes.Add($"{itemOf}{where}: a search reaches its values only inside nested({nested}, (...)), one value compared at a time, so open one of its values for its query.");
            var reading = nested == path
                ? $"{path} is a nested list: the index keeps each of its items apart, so a query compares the values of one item together."
                : $"{path} sits in the nested list {nested}.";
            return new ExplorerElementAnswer(kind, path, template, new ExplorerElementField(path, "section", nested, flattened), reading, [], null, notes);
        }

        var queries = new List<ExplorerElementQuery>
        {
            new("exists", $"_exists_:{path}", $"The records that hold {name}, whatever it holds."),
        };
        if (element.EndsInItem)
        {
            notes.Add($"{itemOf}the query finds the records that hold the list; the query of a value inside the item narrows it to that value.");
        }

        var described = flattened is null
            ? $"{path} is a section of the record; the index keeps its values under their dotted paths."
            : $"{path} is in the flattened list {flattened}: the index keeps its values as keywords under their dotted paths, not item by item.";
        return new ExplorerElementAnswer(kind, path, template, new ExplorerElementField(path, "section", null, flattened), described, queries, null, notes);
    }

    /// <summary>How the index holds a value, in words.</summary>
    private static string Reading(ExplorerElementField described, OsduField field)
    {
        var how = field.Index switch
        {
            OsduFieldIndex.Text => $"{field.Path} is text: its whole value is kept in {field.Path}.keyword, which an exact match asks, and its words in {field.Path}, which a phrase asks.",
            OsduFieldIndex.Keyword => $"{field.Path} is a keyword: the whole value, which a match asks as written.",
            OsduFieldIndex.Number => $"{field.Path} is a number, compared as one.",
            OsduFieldIndex.Boolean => $"{field.Path} is a boolean, true or false.",
            _ => $"{field.Path} is a date, compared as the instant it names.",
        };
        if (field.NestedPath is { } nested)
        {
            how += $" It sits in the nested list {nested}, whose items the index keeps apart, so a search reaches it inside nested({nested}, (...)) and names it there as {field.QueryPath}.";
        }
        else if (described.FlattenedPath is { } flattened)
        {
            how += $" It sits in the flattened list {flattened}, whose values the index keeps as keywords under their dotted paths.";
        }

        return how;
    }

    /// <summary>The flattened list a path of data sits in, by the template; null for none or without a template.</summary>
    private static string? FlattenedOf(SchemaSnapshot? schema, string path)
    {
        if (schema is null || !SearchFields.IsDataPath(path))
        {
            return null;
        }

        var parent = path[..path.LastIndexOf('.')];
        return parent == SearchFields.DataRoot ? null : SearchFields.Section(schema, parent).FlattenedPath;
    }

    /// <summary>How the indexer keeps a value with no template to say: a number, a boolean, a date written as one, or text.</summary>
    private static OsduFieldIndex Guess(JsonElement? value) => value?.ValueKind switch
    {
        JsonValueKind.Number => OsduFieldIndex.Number,
        JsonValueKind.True or JsonValueKind.False => OsduFieldIndex.Boolean,
        JsonValueKind.String when IsoInstant().IsMatch(value.Value.GetString()!) => OsduFieldIndex.Date,
        _ => OsduFieldIndex.Text,
    };

    /// <summary>The value as a query compares it: a text as written, a number's digits, true or false; null for null or none.</summary>
    private static string? ValueText(JsonElement? value) => value?.ValueKind switch
    {
        JsonValueKind.String => value.Value.GetString(),
        JsonValueKind.Number => value.Value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    private static string IndexName(OsduFieldIndex index) => index switch
    {
        OsduFieldIndex.Text => "text",
        OsduFieldIndex.Keyword => "keyword",
        OsduFieldIndex.Number => "number",
        OsduFieldIndex.Boolean => "boolean",
        _ => "date",
    };

    /// <summary>A value as words quote it: in quotes, clipped where it is long.</summary>
    private static string Clipped(string value) => "\"" + (value.Length > 80 ? value[..80] + "..." : value) + "\"";

    /// <summary>A problem as a sentence: its first letter capitalised and a full stop at its end.</summary>
    private static string Sentence(string problem)
    {
        var trimmed = problem.Trim();
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        var capital = char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
        return capital.EndsWith('.') ? capital : capital + ".";
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:?\d{2})$")]
    private static partial Regex IsoInstant();

    /// <summary>An element's path as the record inspector names it, read: its property names, and where it steps into a list.</summary>
    private sealed record ElementPath(IReadOnlyList<string> Names, string Plain, bool CrossesObjectList, string? ObjectList, bool EndsInItem)
    {
        public static ElementPath? Parse(string text)
        {
            var trimmed = text.Trim();
            var names = new List<string>();
            var canonical = new System.Text.StringBuilder(trimmed.Length);
            var crosses = false;
            string? objectList = null;
            var endsInItem = false;
            foreach (Match part in PathPart().Matches(trimmed))
            {
                if (part.Groups["item"].Success)
                {
                    if (names.Count == 0)
                    {
                        return null;
                    }

                    canonical.Append(part.Value);
                    endsInItem = true;
                    continue;
                }

                if (endsInItem)
                {
                    // A name after an item steps into a list of objects.
                    crosses = true;
                    objectList ??= string.Join('.', names);
                    endsInItem = false;
                }

                if (names.Count > 0)
                {
                    canonical.Append('.');
                }

                canonical.Append(part.Value);
                names.Add(part.Groups["name"].Value);
            }

            // The path is read only when it is exactly its parts: a name, then dotted names and items, nothing else between.
            var plain = string.Join('.', names);
            return names.Count == 0 || !string.Equals(canonical.ToString(), trimmed, StringComparison.Ordinal) || !OsduPath.IsPath(plain)
                ? null
                : new ElementPath(names, plain, crosses, objectList, endsInItem);
        }
    }

    [GeneratedRegex(@"(?<item>\[\d+\])|(?<name>[A-Za-z0-9_]+)")]
    private static partial Regex PathPart();
}
