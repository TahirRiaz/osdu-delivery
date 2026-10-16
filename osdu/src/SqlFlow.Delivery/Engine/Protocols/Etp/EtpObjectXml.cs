using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace SqlFlow.Delivery.Engine.Protocols.Etp;

/// <summary>
/// What an Energistics XML object says about itself: the identity and relations the ETP server takes from the XML
/// rather than from the message around it (osdu/specs/reservoir-ddms/INTEGRATION.md section 5.1), and the array paths
/// it names, which the commit checks were supplied (section 4.7).
/// </summary>
/// <param name="ObjectType">The type as the store holds it, <c>{ml}.{type}</c>, for example <c>resqml20.obj_Grid2dRepresentation</c>.</param>
/// <param name="Uuid">The root's <c>uuid</c>, which is the object's identity inside its dataspace.</param>
/// <param name="Title">The citation's title, which the resource carries.</param>
/// <param name="LastChanged">The citation's last change, which the resource carries.</param>
/// <param name="ArrayPaths">The array paths the object names, without a leading slash.</param>
public sealed record EtpObjectIdentity(string ObjectType, Guid Uuid, string Title, DateTimeOffset? LastChanged, IReadOnlyList<string> ArrayPaths);

/// <summary>
/// Reads and checks the XML of one Energistics object before it is sent, and composes the URIs both sides use
/// (osdu/specs/reservoir-ddms/INTEGRATION.md sections 4.2 and 5.1). Every refusal names what to fix in the source or
/// the mapping, because none of them can be fixed by sending the same record again.
/// </summary>
public static partial class EtpObjectXml
{
    /// <summary>The Energistics namespaces the store files objects under, by the schema version they carry.</summary>
    private static readonly (string Suffix, string Version, string Ml)[] MarkupLanguages =
    [
        ("/resqmlv2", "2.0", "resqml20"),
        ("/resqmlv2", "2.2", "resqml22"),
        ("/commonv2", "2.0", "eml20"),
        ("/commonv2", "2.3", "eml23"),
        ("/witsmlv2", "2.1", "witsml21"),
        ("/prodmlv2", "2.2", "prodml22"),
    ];

    /// <summary>The markup languages whose references name their target by an EML 2.0 content type, which needs the <c>obj_</c> spelling.</summary>
    private static readonly string[] ContentTypeMls = ["resqml20", "eml20"];

    /// <summary>The path an ETP dataspace may have (section 4.3), which is also what its Storage record's id is built from.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_/\-\.]{3,}$")]
    private static partial Regex DataspacePath();

    /// <summary>The URI of a dataspace, in the quoted form the server's own printer produces (section 4.2).</summary>
    public static string DataspaceUri(string path)
    {
        CheckPath(path);
        return $"eml:///dataspace('{path}')";
    }

    /// <summary>The URI of one object inside a dataspace (section 4.2).</summary>
    public static string ObjectUri(string path, string objectType, Guid uuid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectType);
        return $"{DataspaceUri(path)}/{objectType}({uuid.ToString("D", CultureInfo.InvariantCulture)})";
    }

    /// <summary>Refuses a dataspace path the server's own rule would refuse, before anything is sent.</summary>
    public static void CheckPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !DataspacePath().IsMatch(path))
        {
            throw new RecordHeldException(
                $"the dataspace path '{path}' is not one the Reservoir DDMS accepts: at least three characters of letters, digits, and _ - . or /, and two levels (project/study) are recommended");
        }
    }

    /// <summary>
    /// Reads one object's XML, refusing anything the server would refuse or file under an identity nobody can resolve.
    /// <paramref name="what"/> names the source of the XML in the refusal.
    /// </summary>
    public static EtpObjectIdentity Read(ReadOnlyMemory<byte> xml, string what)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(what);
        if (xml.IsEmpty)
        {
            throw new RecordHeldException($"{what} is empty, and an ETP object is its XML");
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(Encoding.UTF8.GetString(xml.Span), LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new RecordHeldException($"{what} is not XML the Reservoir DDMS can parse: {ex.Message}");
        }

        var root = document.Root ?? throw new RecordHeldException($"{what} holds no root element");
        if (!Guid.TryParse(root.Attribute("uuid")?.Value, CultureInfo.InvariantCulture, out var uuid) || uuid == Guid.Empty)
        {
            throw new RecordHeldException(
                $"{what} carries no usable uuid on its root element, which is the identity the Reservoir DDMS files it under");
        }

        var schemaVersion = root.Attribute("schemaVersion")?.Value;
        if (string.IsNullOrWhiteSpace(schemaVersion))
        {
            throw new RecordHeldException($"{what} carries no schemaVersion on its root element, which says which Energistics schema it follows");
        }

        var citation = root.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "Citation", StringComparison.Ordinal))
            ?? throw new RecordHeldException(
                $"{what} has no Citation element directly under its root; the Reservoir DDMS reads the object's identity only when it finds one");

        var xsiType = root.Attribute(XName.Get("type", "http://www.w3.org/2001/XMLSchema-instance"))?.Value;
        var type = xsiType is null ? root.Name.LocalName : xsiType[(xsiType.IndexOf(':', StringComparison.Ordinal) + 1)..];
        if (string.IsNullOrWhiteSpace(type))
        {
            throw new RecordHeldException($"{what} has an empty xsi:type, so the Reservoir DDMS has no type to file it under");
        }

        var ml = MarkupLanguage(root.Name.NamespaceName, schemaVersion)
            ?? throw new RecordHeldException(
                $"{what} is in the namespace {root.Name.NamespaceName} at schema version {schemaVersion}, which the Reservoir DDMS does not file: it takes RESQML 2.0 and 2.2, EML 2.0 and 2.3, WITSML 2.1 and PRODML 2.2");

        if (ContentTypeMls.Contains(ml, StringComparer.Ordinal) && !type.StartsWith("obj_", StringComparison.Ordinal))
        {
            throw new RecordHeldException(
                $"{what} is a {ml} object typed '{type}' rather than 'obj_{type}'; references to it name their target by content type, which resolves only against the obj_ spelling, so it would be stored unreachable");
        }

        return new EtpObjectIdentity(
            $"{ml}.{type}",
            uuid,
            Text(citation, "Title") ?? string.Empty,
            Moment(Text(citation, "LastUpdate") ?? Text(citation, "Creation")),
            ArrayPaths(document));
    }

    /// <summary>The markup language the store files an object under, from its namespace and schema version (section 5.1).</summary>
    private static string? MarkupLanguage(string namespaceName, string schemaVersion)
    {
        var parts = schemaVersion.Split('.');
        var version = parts.Length > 1 ? $"{parts[0]}.{parts[1]}" : schemaVersion;
        foreach (var (suffix, declared, ml) in MarkupLanguages)
        {
            if (namespaceName.EndsWith(suffix, StringComparison.Ordinal) && string.Equals(declared, version, StringComparison.Ordinal))
            {
                return ml;
            }
        }

        return null;
    }

    /// <summary>The array paths the object names, which the transaction must supply before it commits (sections 4.7 and 5.1).</summary>
    private static IReadOnlyList<string> ArrayPaths(XDocument document)
    {
        var paths = new List<string>();
        foreach (var element in document.Descendants())
        {
            if (element.Name.LocalName is "PathInHdfFile" or "PathInExternalFile" && !string.IsNullOrWhiteSpace(element.Value))
            {
                var path = element.Value.Trim().TrimStart('/');
                if (!paths.Contains(path, StringComparer.Ordinal))
                {
                    paths.Add(path);
                }
            }
        }

        return paths;
    }

    private static string? Text(XElement citation, string name)
        => citation.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, name, StringComparison.Ordinal))?.Value?.Trim();

    private static DateTimeOffset? Moment(string? value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var moment)
            ? moment
            : null;
}
