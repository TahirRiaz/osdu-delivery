using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>
/// The link a record of a Wellbore DDMS bulk collection keeps to its bulk data. The service writes
/// <c>data.ExtensionProperties.wdms.bulkURI</c> on every bulk write and session commit, and refuses a record write whose
/// bulkURI differs from the one the latest stored version holds, including a write that leaves it out
/// (osdu/specs/wellbore-ddms/INTEGRATION.md section 5.3, <c>app/routers/ddms_v3/ddms_v3_utils.py</c>). It also appends
/// <c>urn://wdms-1/uuid:&lt;bulk id&gt;</c> to <c>data.DDMSDatasets</c>, which it never checks. A rendered record knows
/// neither, so a metadata update carries both over from the version the DDMS holds.
/// </summary>
internal static partial class WellboreDdmsBulkLink
{
    private const string Extension = "ExtensionProperties";
    private const string Wdms = "wdms";
    private const string BulkUri = "bulkURI";
    private const string DdmsDatasets = "DDMSDatasets";

    /// <summary>The bulk link a record holds, or null when it holds none (an empty value is none, as the service reads it).</summary>
    public static string? Of(JsonObject? record)
        => record?["data"] is JsonObject data
            && data[Extension] is JsonObject extension
            && extension[Wdms] is JsonObject wdms
            && wdms[BulkUri] is JsonValue value
            && value.GetValueKind() == JsonValueKind.String
            && value.TryGetValue<string>(out var uri)
            && uri.Length > 0
                ? uri
                : null;

    /// <summary>
    /// Writes the bulk link <paramref name="stored"/> holds into <paramref name="document"/>, or removes the document's own
    /// when the stored version has none (or there is no stored version), and adds the stored version's service-written
    /// <c>DDMSDatasets</c> entries the document lacks. Returns false when the document rendered a bulk link of its own that
    /// differs from the stored one: the DDMS manages the value, so the document's is replaced. A document whose
    /// <c>ExtensionProperties</c> or <c>wdms</c> is not an object is left for the service's schema check to refuse.
    /// </summary>
    public static bool Carry(JsonObject? stored, JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var storedUri = Of(stored);
        var renderedUri = Of(document);
        var agreed = renderedUri is null || string.Equals(renderedUri, storedUri, StringComparison.Ordinal);

        if (document["data"] is not JsonObject data)
        {
            if (document["data"] is not null || storedUri is null)
            {
                return agreed;
            }

            data = new JsonObject();
            document["data"] = data;
        }

        if (storedUri is not null)
        {
            if (data[Extension] is null)
            {
                data[Extension] = new JsonObject();
            }

            if (data[Extension] is JsonObject extension)
            {
                if (extension[Wdms] is null)
                {
                    extension[Wdms] = new JsonObject();
                }

                if (extension[Wdms] is JsonObject wdms)
                {
                    wdms[BulkUri] = storedUri;
                }
            }
        }
        else if (data[Extension] is JsonObject extension && extension[Wdms] is JsonObject wdms)
        {
            wdms.Remove(BulkUri);
        }

        CarryDatasets(stored, data);
        return agreed;
    }

    private static void CarryDatasets(JsonObject? stored, JsonObject data)
    {
        if (stored?["data"] is not JsonObject storedData || storedData[DdmsDatasets] is not JsonArray storedList)
        {
            return;
        }

        var written = storedList.Select(Text).OfType<string>().Where(entry => ServiceEntry().IsMatch(entry)).ToList();
        if (written.Count == 0)
        {
            return;
        }

        if (data[DdmsDatasets] is null)
        {
            data[DdmsDatasets] = new JsonArray();
        }

        if (data[DdmsDatasets] is not JsonArray list)
        {
            return;
        }

        var present = list.Select(Text).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var entry in written.Where(present.Add))
        {
            list.Add(entry);
        }
    }

    private static string? Text(JsonNode? node)
        => node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.TryGetValue<string>(out var text) ? text : null;

    // BulkURI.encode_for_ddms_datasets (app/bulk_persistence/bulk_uri.py): urn://wdms-1/uuid:<id>, or urn://uuid:<id> for
    // bulk storage version 0.
    [GeneratedRegex(@"^urn://(?:wdms-[0-9]+/)?uuid:[0-9A-Fa-f-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex ServiceEntry();
}
