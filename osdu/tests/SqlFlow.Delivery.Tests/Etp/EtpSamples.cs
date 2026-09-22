using System.Globalization;
using System.Text;
using SqlFlow.Delivery.Engine.Protocols.Etp;

namespace SqlFlow.Delivery.Tests.Etp;

/// <summary>
/// The RESQML content the ETP tests send: a small object whose XML carries what the server reads out of it (a root
/// uuid, a schema version, an <c>xsi:type</c> and a Citation directly under the root,
/// osdu/specs/reservoir-ddms/INTEGRATION.md section 5.1).
/// </summary>
internal static class EtpSamples
{
    public const string Resqml20 = "http://www.energistics.org/energyml/data/resqmlv2";

    public const string Eml20 = "http://www.energistics.org/energyml/data/commonv2";

    /// <summary>A RESQML 2.0.1 object of <paramref name="type"/>, optionally naming an array path and padded to a size.</summary>
    public static string Object(Guid uuid, string title, string type = "obj_Grid2dRepresentation", string? arrayPath = null, int padding = 0)
    {
        var xml = new StringBuilder();
        xml.Append(CultureInfo.InvariantCulture, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <resqml:{type} xmlns:resqml="{Resqml20}" xmlns:eml="{Eml20}" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:type="resqml:{type}" uuid="{uuid:D}" schemaVersion="2.0">
              <eml:Citation>
                <eml:Title>{title}</eml:Title>
                <eml:Originator>OSDU Delivery</eml:Originator>
                <eml:Creation>2026-09-17T00:00:00Z</eml:Creation>
                <eml:Format>osdu-delivery</eml:Format>
              </eml:Citation>
            """);
        if (arrayPath is not null)
        {
            xml.Append(CultureInfo.InvariantCulture, $"""

              <resqml:Points xsi:type="resqml:DoubleHdf5Array">
                <resqml:Values>
                  <eml:PathInHdfFile>{arrayPath}</eml:PathInHdfFile>
                  <eml:HdfProxy>
                    <eml:ContentType>application/x-eml+xml;version=2.0;type=obj_EpcExternalPartReference</eml:ContentType>
                    <eml:Title>Hdf proxy</eml:Title>
                    <eml:UUID>1e3e8b21-2f21-4a41-8f17-9d5e2a39a001</eml:UUID>
                  </eml:HdfProxy>
                </resqml:Values>
              </resqml:Points>
            """);
        }

        if (padding > 0)
        {
            xml.Append(CultureInfo.InvariantCulture, $"\n  <resqml:Comment>{new string('x', padding)}</resqml:Comment>");
        }

        xml.Append(CultureInfo.InvariantCulture, $"\n</resqml:{type}>");
        return xml.ToString();
    }

    /// <summary>The dataspace a test writes into, with the legal tags and ACLs the server requires (section 4.3).</summary>
    public static Dataspace Dataspace(string path) => new()
    {
        Uri = $"eml:///dataspace('{path}')",
        Path = path,
        StoreCreated = 0,
        StoreLastWrite = 0,
        CustomData = new Dictionary<string, DataValue>(StringComparer.Ordinal)
        {
            ["viewers"] = DataValue.Of(["data.default.viewers@dev.example.com"]),
            ["owners"] = DataValue.Of(["data.default.owners@dev.example.com"]),
            ["legaltags"] = DataValue.Of(["dev-public-usa-dataset-1"]),
            ["otherRelevantDataCountries"] = DataValue.Of(["US"]),
        },
    };
}
