using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// What External Data Services needs of the records that configure it, checked on each rendered record before anything is
/// sent, so a record EDS could not use is held with the rules it breaks (osdu/specs/eds-dms/INTEGRATION.md sections 2.1,
/// 2.3 and 5, from eds-dms's code and the workflow project's sources at the pinned commits). EDS reads three types of
/// record: a connected source registry entry (the external source: where eds-dms asks for retrieval instructions, and the
/// security schemes whose secrets it reads), a connected source data job (one fetch the scheduler runs), and a proxy
/// dataset (a local record naming a dataset that stays in the source). eds-dms builds every scheme of a registry entry on
/// every retrieval and fails the whole request on one it cannot build, and fails a request that includes a proxy record
/// missing one of its ids, so one bad record breaks retrieval for every dataset that shares a request with it (section 7.2).
/// <para>
/// Not checked, because nothing the engine can read decides them: whether the secrets a registry entry names exist (no
/// Secret service contract is pinned); whether a job's <c>SecuritySchemeName</c> names a scheme of its registry entry (the
/// brief leaves it open, section 10); and whether the proxy records of one registry entry name one source partition, which
/// spans records the engine does not deliver (EDS writes a proxy for each dataset it fetches, with that job's partition)
/// and matters only to a retrieval request that mixes them.
/// </para>
/// </summary>
public static partial class EdsRecordRules
{
    public const string RegistryEntry = "master-data--ConnectedSourceRegistryEntry";

    public const string DataJob = "master-data--ConnectedSourceDataJob";

    public const string ExternalDataset = "dataset--External";

    public const string ConnectedSourceDataset = "dataset--ConnectedSource.Generic";

    /// <summary>The flow eds-dms refuses after reading its keys (section 5.2).</summary>
    public const string ImplicitFlow = "Implicit";

    /// <summary>The flow only the gc build of eds-dms takes (section 5.2).</summary>
    public const string ServiceAccountFlow = "GcpServiceAccount";

    /// <summary>The handler a data job's FETCH entry names: the workflow the scheduler starts for it (section 4.3).</summary>
    public const string FetchHandler = "eds_ingest";

    private const string FetchTag = "FETCH";

    private const string FlowTypeMarker = "reference-data--OAuth2FlowType:";

    private const string Brief = "osdu/specs/eds-dms/INTEGRATION.md";

    private const string BuiltFlows = "ClientCredentials, PasswordCredentials, RefreshToken, AuthorizationCode and GcpServiceAccount";

    /// <summary>The registry entry type as eds-dms's own integration tests name it, without its group (section 1.2).</summary>
    private const string BareRegistryEntry = "ConnectedSourceRegistryEntry";

    private const string BareDataJob = "ConnectedSourceDataJob";

    /// <summary>
    /// The data keys EDS writes on a data job after a run: its incremental-fetch watermark, the records that failed and the
    /// newest creation time it fetched (section 2.1, which infers them from the legacy update method and its arguments). A
    /// redelivery carries them from the version OSDU holds rather than overwriting them, and a version that changed only
    /// them is not drift.
    /// </summary>
    public static IReadOnlyList<string> RunStateKeys { get; } = ["LastSuccessfulRunDateUTC", "FailedRecords", "CreateTimeMax"];

    /// <summary>
    /// The flows eds-dms builds a security scheme for, each with the keys it requires beside <c>Name</c>, <c>TypeID</c> and
    /// <c>FlowTypeID</c> (section 5.2, from <c>SecurityScheme.java</c>).
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Flows { get; } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        ["ClientCredentials"] = ["TokenUrl", "ClientIDKeyName", "ClientSecretKeyName", "ScopesKeyName"],
        ["PasswordCredentials"] = ["TokenUrl", "ClientIDKeyName", "ClientSecretKeyName", "UsernameKeyName", "PasswordKeyName", "ScopesKeyName"],
        ["RefreshToken"] = ["TokenUrl", "ClientIDKeyName", "ClientSecretKeyName", "ScopesKeyName", "RefreshTokenKeyName"],
        ["AuthorizationCode"] = ["TokenUrl", "CallbackUrl", "ClientIDKeyName", "ClientSecretKeyName", "ScopesKeyName", "RefreshTokenKeyName"],
        [ServiceAccountFlow] = ["GcpServiceAccountKey", "TokenUrl"],
        [ImplicitFlow] = ["AuthorizationUrl", "CallbackUrl", "ClientIDKeyName", "ScopesKeyName"],
    };

    /// <summary>A connected source registry entry: the type EDS's workflows write, or the bare name eds-dms's tests use.</summary>
    public static bool IsRegistryEntry(string? entityType) => entityType is RegistryEntry or BareRegistryEntry;

    public static bool IsDataJob(string? entityType) => entityType is DataJob or BareDataJob;

    /// <summary>A proxy dataset eds-dms serves: the type its README describes, or the one eds_ingest writes for fetched files.</summary>
    public static bool IsProxyDataset(string? entityType) => entityType is ExternalDataset or ConnectedSourceDataset;

    /// <summary>The data keys EDS writes on records of <paramref name="entityType"/>: a data job's run state, and nothing on any other type.</summary>
    public static IReadOnlyList<string> WrittenByEds(string? entityType) => IsDataJob(entityType) ? RunStateKeys : [];

    /// <summary>The entity type of a rendered record, from its kind; null when it has none.</summary>
    public static string? EntityTypeOf(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document["kind"] is JsonValue value && value.TryGetValue<string>(out var kind) ? OsduKind.EntityType(kind) : null;
    }

    /// <summary>
    /// Why EDS could not use <paramref name="document"/>, as one message naming every rule it breaks; null when it breaks
    /// none, when the flow turned the checks off, and for a record of any other type.
    /// </summary>
    public static string? Hold(EdsTarget settings, JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(document);
        if (!settings.Checks)
        {
            return null;
        }

        var entityType = EntityTypeOf(document);
        var data = document["data"] as JsonObject ?? new JsonObject();
        var problems = new List<string>();
        string what;
        if (IsRegistryEntry(entityType))
        {
            what = "connected source registry entry";
            RegistryEntryProblems(settings, data, problems);
        }
        else if (IsDataJob(entityType))
        {
            what = "connected source data job";
            DataJobProblems(data, problems);
        }
        else if (IsProxyDataset(entityType))
        {
            what = "proxy dataset";
            ProxyProblems(data, problems);
        }
        else
        {
            return null;
        }

        return problems.Count == 0
            ? null
            : $"External Data Services could not use this {what}: {string.Join("; ", problems)} ({Brief})";
    }

    /// <summary>
    /// The flow a scheme's <c>FlowTypeID</c> names, read as eds-dms reads it: everything from the third colon on removed,
    /// then the text after <c>reference-data--OAuth2FlowType:</c>; null when the reference does not name that type.
    /// </summary>
    public static string? FlowOf(string flowTypeId)
    {
        ArgumentNullException.ThrowIfNull(flowTypeId);
        var cut = VersionStripped(flowTypeId);
        var at = cut.IndexOf(FlowTypeMarker, StringComparison.Ordinal);
        return at < 0 ? null : cut[(at + FlowTypeMarker.Length)..];
    }

    /// <summary>A record id as eds-dms reads a reference: everything from its third colon on removed (section 6, <c>RecordIdUtil</c>).</summary>
    public static string VersionStripped(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var colons = 0;
        for (var i = 0; i < id.Length; i++)
        {
            if (id[i] == ':' && ++colons == 3)
            {
                return id[..i];
            }
        }

        return id;
    }

    /// <summary>
    /// The id eds-dms sends the source for a proxy's source record: the reference with its version removed, and the source
    /// partition put in front when it does not start with it (section 6).
    /// </summary>
    public static string SentSourceRecordId(string sourceRecordId, string sourcePartition)
    {
        ArgumentNullException.ThrowIfNull(sourceRecordId);
        ArgumentNullException.ThrowIfNull(sourcePartition);
        var stripped = VersionStripped(sourceRecordId);
        return stripped.StartsWith(sourcePartition + ":", StringComparison.Ordinal) ? stripped : sourcePartition + ":" + stripped;
    }

    private static void RegistryEntryProblems(EdsTarget settings, JsonObject data, List<string> problems)
    {
        switch (data["DatasetURL"])
        {
            case null when settings.Retrieval:
                problems.Add("data.DatasetURL is missing, and eds-dms answers 500 to every retrieval of the entry's datasets without it "
                    + "(target.eds.retrieval false lets through an entry whose jobs fetch no files)");
                break;
            case null:
                break;
            case var node when Text(node) is null:
                problems.Add("data.DatasetURL is not a string");
                break;
            case var node when string.IsNullOrWhiteSpace(Text(node)):
                if (settings.Retrieval)
                {
                    problems.Add("data.DatasetURL is empty, and eds-dms leaves the entry's datasets out of every retrieval without saying so");
                }

                break;
            case var node when !IsHttpUrl(Text(node)!):
                problems.Add($"data.DatasetURL '{Text(node)}' is not an absolute http(s) URL, and eds-dms posts to {{DatasetURL}}/retrievalInstructions");
                break;
        }

        var declared = data["SecuritySchemes"];
        if (declared is not JsonArray schemes)
        {
            problems.Add(declared is null
                ? "data.SecuritySchemes is missing, and eds-dms answers 500 to every retrieval of the entry's datasets without it"
                : "data.SecuritySchemes is not a list");
            return;
        }

        if (schemes.Count == 0)
        {
            problems.Add("data.SecuritySchemes is empty, and eds-dms takes the scheme of every retrieval from its first entry");
            return;
        }

        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < schemes.Count; i++)
        {
            var at = string.Create(CultureInfo.InvariantCulture, $"data.SecuritySchemes[{i}]");
            if (schemes[i] is not JsonObject scheme)
            {
                problems.Add($"{at} is not an object");
                continue;
            }

            var name = Text(scheme["Name"]);
            if (string.IsNullOrWhiteSpace(name))
            {
                problems.Add($"{at} has no Name");
            }
            else if (names.TryGetValue(name, out var first))
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{at} is named '{name}' like data.SecuritySchemes[{first}], and eds-dms takes the first scheme of a name"));
            }
            else
            {
                names[name] = i;
            }

            // eds-dms requires TypeID and does not read its value.
            if (Text(scheme["TypeID"]) is null)
            {
                problems.Add($"{at} has no TypeID");
            }

            var flowType = Text(scheme["FlowTypeID"]);
            if (string.IsNullOrWhiteSpace(flowType))
            {
                problems.Add($"{at} has no FlowTypeID");
                continue;
            }

            if (FlowOf(flowType) is not { } flow)
            {
                problems.Add($"{at}.FlowTypeID '{flowType}' is not a {FlowTypeMarker.TrimEnd(':')} reference");
                continue;
            }

            if (!Flows.TryGetValue(flow, out var required))
            {
                problems.Add($"{at}.FlowTypeID names the flow '{flow}', and eds-dms builds only {BuiltFlows}");
                continue;
            }

            if (flow == ImplicitFlow)
            {
                problems.Add($"{at} uses the Implicit flow, which eds-dms refuses ('Implicit flow not supported')");
                continue;
            }

            if (flow == ServiceAccountFlow && settings.Build != EdsBuild.Gc)
            {
                problems.Add(settings.Build is { } build
                    ? $"{at} uses the GcpServiceAccount flow, which only the gc build of eds-dms takes, and target.eds.build is {BuildName(build)}"
                    : $"{at} uses the GcpServiceAccount flow, which only the gc build of eds-dms takes (target.eds.build names the partition's build)");
            }

            foreach (var key in required)
            {
                var value = Text(scheme[key]);
                if (string.IsNullOrWhiteSpace(value))
                {
                    problems.Add($"{at} ({flow}) has no {key}");
                }
                else if (key == "TokenUrl" && !IsHttpUrl(value))
                {
                    problems.Add($"{at}.TokenUrl '{value}' is not an absolute http(s) URL");
                }
            }
        }
    }

    private static void DataJobProblems(JsonObject data, List<string> problems)
    {
        var registry = Text(data["ConnectedSourceRegistryEntryID"]);
        if (string.IsNullOrWhiteSpace(registry))
        {
            problems.Add("data.ConnectedSourceRegistryEntryID is missing");
        }
        else if (!RegistryReference().IsMatch(registry))
        {
            problems.Add($"data.ConnectedSourceRegistryEntryID '{registry}' is not a reference to a connected source registry entry (<partition>:{RegistryEntry}:<id>:, as every EDS workflow source writes it)");
        }

        switch (data["ActiveIndicator"]?.GetValueKind())
        {
            case JsonValueKind.True or JsonValueKind.False:
                break;
            case null:
                problems.Add("data.ActiveIndicator is missing, and the scheduler runs only the jobs where it is true");
                break;
            default:
                problems.Add("data.ActiveIndicator is not true or false");
                break;
        }

        var fetchKind = Text(data["FetchKind"]);
        if (string.IsNullOrWhiteSpace(fetchKind))
        {
            problems.Add("data.FetchKind is missing");
        }
        else if (!SearchKind().IsMatch(fetchKind))
        {
            problems.Add($"data.FetchKind '{fetchKind}' is not a kind the source's search takes (authority:source:type:version, each part allowing wildcards)");
        }

        switch (data["Filter"])
        {
            case null:
                problems.Add("data.Filter is missing (an empty string fetches every record of the kind)");
                break;
            case var node when Text(node) is null:
                problems.Add("data.Filter is not a string");
                break;
        }

        Partition(data, "ConnectedSourceDataPartitionID", problems);
        Partition(data, "OnIngestionDataPartitionID", problems);
        LegalTags(data["OnIngestionLegalTags"], problems);
        Acl(data["OnIngestionAcl"], problems);

        var schedule = Text(data["ScheduleUTC"]);
        if (string.IsNullOrWhiteSpace(schedule))
        {
            problems.Add("data.ScheduleUTC is missing, and the scheduler runs a job on its schedule");
        }
        else if (schedule.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length is not (5 or 6))
        {
            problems.Add($"data.ScheduleUTC '{schedule}' is not a cron expression of five or six fields");
        }

        if (data["LimitRecords"] is { } limit && !IsCount(limit))
        {
            problems.Add("data.LimitRecords is not a whole number above zero");
        }

        FetchEntry(data["Workflows"], problems);
    }

    private static void FetchEntry(JsonNode? declared, List<string> problems)
    {
        if (declared is not JsonArray workflows)
        {
            problems.Add(declared is null
                ? "data.Workflows is missing, and eds_ingest reads the source's search URL from its FETCH entry"
                : "data.Workflows is not a list");
            return;
        }

        var fetch = new List<JsonObject>();
        for (var i = 0; i < workflows.Count; i++)
        {
            if (workflows[i] is not JsonObject entry)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"data.Workflows[{i}] is not an object"));
            }
            else if (Text(entry["Tag"]) == FetchTag)
            {
                fetch.Add(entry);
            }
        }

        if (fetch.Count != 1)
        {
            problems.Add(fetch.Count == 0
                ? "data.Workflows has no FETCH entry, and eds_ingest reads the source's search URL from it"
                : string.Create(CultureInfo.InvariantCulture, $"data.Workflows has {fetch.Count} FETCH entries, and eds_ingest reads one"));
            return;
        }

        var only = fetch[0];
        var handler = Text(only["Handler"]);
        if (handler != FetchHandler)
        {
            problems.Add($"the FETCH entry of data.Workflows names the handler '{handler ?? "none"}', and the scheduler starts {FetchHandler} for it");
        }

        var url = Text(only["Url"]);
        if (string.IsNullOrWhiteSpace(url))
        {
            problems.Add("the FETCH entry of data.Workflows has no Url (the source's search endpoint)");
        }
        else if (!IsHttpUrl(url))
        {
            problems.Add($"the FETCH entry's Url '{url}' is not an absolute http(s) URL");
        }

        if (string.IsNullOrWhiteSpace(Text(only["SecuritySchemeName"])))
        {
            problems.Add("the FETCH entry of data.Workflows has no SecuritySchemeName");
        }
    }

    private static void ProxyProblems(JsonObject data, List<string> problems)
    {
        if (data["DatasetProperties"] is not JsonObject properties)
        {
            problems.Add("data.DatasetProperties is missing, and eds-dms fails every retrieval request that includes the record without its ids");
            return;
        }

        var registry = Property(properties, "ConnectedSourceRegistryEntry", problems);
        var job = Property(properties, "ConnectedSourceDataJob", problems);
        var partition = Property(properties, "SourceDataPartition", problems);
        var source = Property(properties, "SourceRecord", problems);
        if (registry is not null)
        {
            if (!IsReference(registry))
            {
                problems.Add($"data.DatasetProperties.ConnectedSourceRegistryEntryId '{registry}' is not a record id eds-dms can read (partition:type:id, with an optional version after a colon)");
            }
            else if (!IsRegistryEntry(VersionStripped(registry).Split(':')[1]))
            {
                problems.Add($"data.DatasetProperties.ConnectedSourceRegistryEntryId '{registry}' does not name a connected source registry entry");
            }
        }

        if (job is not null && !IsReference(job))
        {
            problems.Add($"data.DatasetProperties.ConnectedSourceDataJobId '{job}' is not a record id eds-dms can read (partition:type:id, with an optional version after a colon)");
        }

        if (partition is null)
        {
            return;
        }

        if (!PartitionId().IsMatch(partition))
        {
            problems.Add($"data.DatasetProperties.SourceDataPartitionId '{partition}' is not a partition id");
            return;
        }

        if (source is not null)
        {
            var sent = SentSourceRecordId(source, partition);
            if (!IsRecordId(sent) || !sent.StartsWith(partition + ":", StringComparison.Ordinal))
            {
                problems.Add($"data.DatasetProperties.SourceRecordId '{source}' reaches the source as '{sent}', which is not a record id of partition '{partition}'");
            }
        }
    }

    /// <summary>One of the four proxy properties, which eds-dms reads with an <c>Id</c> or an <c>ID</c> suffix (section 5.4).</summary>
    private static string? Property(JsonObject properties, string stem, List<string> problems)
    {
        var mixed = properties[stem + "Id"];
        var upper = properties[stem + "ID"];
        if ((mixed is not null && Text(mixed) is null) || (upper is not null && Text(upper) is null))
        {
            problems.Add($"data.DatasetProperties.{stem}Id is not a string");
            return null;
        }

        var a = Text(mixed);
        var b = Text(upper);
        if (!string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && !string.Equals(a, b, StringComparison.Ordinal))
        {
            problems.Add($"data.DatasetProperties names {stem}Id '{a}' and {stem}ID '{b}', and eds-dms reads one of them");
            return null;
        }

        var value = string.IsNullOrWhiteSpace(a) ? b : a;
        if (string.IsNullOrWhiteSpace(value))
        {
            problems.Add($"data.DatasetProperties.{stem}Id is missing, and eds-dms fails every retrieval request that includes the record without it");
            return null;
        }

        return value;
    }

    private static void Partition(JsonObject data, string key, List<string> problems)
    {
        var value = Text(data[key]);
        if (string.IsNullOrWhiteSpace(value))
        {
            problems.Add($"data.{key} is missing");
        }
        else if (!PartitionId().IsMatch(value))
        {
            problems.Add($"data.{key} '{value}' is not a partition id");
        }
    }

    /// <summary>The legal block EDS gives every record it fetches, which Storage refuses without both lists (openapi storage v2, Legal).</summary>
    private static void LegalTags(JsonNode? declared, List<string> problems)
    {
        if (declared is not JsonObject legal)
        {
            problems.Add(declared is null
                ? "data.OnIngestionLegalTags is missing, and EDS gives the records it fetches its legal tags"
                : "data.OnIngestionLegalTags is not an object");
            return;
        }

        Strings(legal["legaltags"], "data.OnIngestionLegalTags.legaltags", null, problems);
        Strings(legal["otherRelevantDataCountries"], "data.OnIngestionLegalTags.otherRelevantDataCountries", null, problems);
    }

    /// <summary>The access block EDS gives every record it fetches, whose groups Storage takes only in its ACL form (openapi storage v2, Acl).</summary>
    private static void Acl(JsonNode? declared, List<string> problems)
    {
        if (declared is not JsonObject acl)
        {
            problems.Add(declared is null
                ? "data.OnIngestionAcl is missing, and EDS gives the records it fetches its owners and viewers"
                : "data.OnIngestionAcl is not an object");
            return;
        }

        Strings(acl["owners"], "data.OnIngestionAcl.owners", AclGroup(), problems);
        Strings(acl["viewers"], "data.OnIngestionAcl.viewers", AclGroup(), problems);
    }

    /// <summary>A list of at least one string, each matching <paramref name="pattern"/> when one is given.</summary>
    private static void Strings(JsonNode? declared, string at, Regex? pattern, List<string> problems)
    {
        if (declared is not JsonArray list || list.Count == 0)
        {
            problems.Add(declared is null ? $"{at} is missing" : $"{at} is not a list of at least one entry");
            return;
        }

        foreach (var item in list)
        {
            var text = Text(item);
            if (string.IsNullOrWhiteSpace(text))
            {
                problems.Add($"{at} holds an empty entry, or one that is not a string");
                return;
            }

            if (pattern is not null && !pattern.IsMatch(text))
            {
                problems.Add($"{at} holds '{text}', which is not a group Storage takes in an ACL (data.<name>@<domain>)");
                return;
            }
        }
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool IsHttpUrl(string text)
        => Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host);

    /// <summary>A JSON number that is a whole number above zero, whatever form the render gave it.</summary>
    private static bool IsCount(JsonNode node)
        => node.GetValueKind() == JsonValueKind.Number
           && decimal.TryParse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
           && number >= 1
           && decimal.Truncate(number) == number;

    /// <summary>A reference eds-dms can cut and read: a record id, optionally followed by a colon and a version number.</summary>
    private static bool IsReference(string reference)
    {
        var stripped = VersionStripped(reference);
        var rest = reference[stripped.Length..];
        return IsRecordId(stripped) && (rest.Length == 0 || (rest[0] == ':' && rest.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0));
    }

    private static bool IsRecordId(string id) => RecordId().IsMatch(id);

    private static string BuildName(EdsBuild build) => build switch
    {
        EdsBuild.CorePlus => "corePlus",
        EdsBuild.Azure => "azure",
        EdsBuild.Gc => "gc",
        _ => build.ToString(),
    };

    /// <summary>A record id with no colon in its last part, which is what survives eds-dms's version cut (openapi storage v2, Record.id).</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_\-\.]+:[A-Za-z0-9_\-\.]+:[A-Za-z0-9_\-\.%]+$", RegexOptions.CultureInvariant)]
    private static partial Regex RecordId();

    /// <summary>The reference a data job names its registry entry by: the entry's id and a colon, with an optional version.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_\-\.]+:(?:master-data--)?ConnectedSourceRegistryEntry:[A-Za-z0-9_\-\.%]+:[0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex RegistryReference();

    [GeneratedRegex(@"^[A-Za-z0-9_\-\.]+$", RegexOptions.CultureInvariant)]
    private static partial Regex PartitionId();

    /// <summary>A kind as the search service takes it, wildcards allowed (openapi search v2, QueryRequest.kind).</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_.*-]+:[A-Za-z0-9_.*-]+:[A-Za-z0-9_.*-]+:[0-9.*]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SearchKind();

    /// <summary>A group Storage takes in an ACL (openapi storage v2, Acl.owners and Acl.viewers).</summary>
    [GeneratedRegex(@"^data\.[a-zA-Z0-9_+&*-]+(?:\.[a-zA-Z0-9_+&*-]+)*@(?:[a-zA-Z](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)+[a-zA-Z](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex AclGroup();
}
