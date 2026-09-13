using System.Text.Json;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The estate the inline submission suites run against: the sample Wellbore master-data mapping and the snapshots it
/// renders with, copied to a temp repository, plus a flow that delivers wellbores through the storage service. Manual
/// submission is wellbore master data, so this is the shape the suites exercise. The default flow streams no payload
/// files; <see cref="PayloadFlow"/> is the same estate delivering files, where each record points at where its files
/// already sit instead of carrying them.
/// </summary>
internal sealed class WellboreEstate : IDisposable
{
    public const string FlowName = "wellbore-records";

    public const string MappingReference = "Wellbore@1.0.0";

    /// <summary>The flow's one declared parameter, so every suite also covers a flow that cannot run without values.</summary>
    public static IReadOnlyDictionary<string, string> Values { get; } = new Dictionary<string, string> { ["site"] = "north" };

    public WellboreEstate()
    {
        Root = Samples.NewTempDirectory();
        CopyDirectory(Samples.Mappings, Path.Combine(Root, "mappings"));
        CopyDirectory(Samples.Snapshots, Path.Combine(Root, "snapshots"));
        Directory.CreateDirectory(Path.Combine(Root, "flows"));
        FlowFile = WriteFlow(FlowName, Flow(FlowName, MappingReference));
    }

    public string Root { get; }

    /// <summary>The default flow: wellbore master data through the storage service, no payload files.</summary>
    public string FlowFile { get; }

    public FlowDefinition Definition => Load(FlowFile);

    public static FlowDefinition Load(string flowFile) => new DeliveryDocumentLoader().LoadFlow(flowFile);

    /// <summary>
    /// The flow YAML of a wellbore flow: the mapping it renders with, whether it offers manual submission, and whatever
    /// a test needs to add to its source, change or target blocks (a work location, payload files and their protocol
    /// options, how payload changes are decided).
    /// </summary>
    public string Flow(
        string name, string mapping, string? sourceExtra = null, string protocol = "osduRecord", string? targetExtra = null, bool manualSubmission = true,
        string? changeExtra = null) => $$"""
        flowType: delivery
        name: {{name}}
        parameters:
          site:
            required: true
            description: The site the wellbores belong to.
        source:
          location: {{Root.Replace('\\', '/')}}/drops/{site}
          lastModified: update_date
        {{(manualSubmission ? "  manualSubmission: true" : string.Empty)}}
        {{sourceExtra ?? string.Empty}}
        render:
          mapping: {{mapping}}
          references: pinned
          parameters:
            dataPartition: opendes
        change:
          detect: renderedHash
          onUnchanged: skip
        {{changeExtra ?? string.Empty}}
        target:
          endpoint: https://osdu.example.test
          headers:
            data-partition-id: opendes
          protocol: {{protocol}}
        {{targetExtra ?? string.Empty}}
        reliability:
          concurrency: 1
          renderParallelism: 1
        """;

    /// <summary>The payload flow's name, the payload it streams, and the lake folder its records may point inside.</summary>
    public const string PayloadFlowName = "wellbore-files";

    public const string PayloadName = "files";

    public string Lake => Path.Combine(Root, "lake").Replace('\\', '/');

    /// <summary>
    /// A wellbore flow that streams payload files and offers manual submission: a record says where its files already
    /// sit, and the node opens that location when it delivers. The files' modified times are the payload's watermark
    /// unless <paramref name="hashDetect"/>, where each record has to carry the payload's content hash instead.
    /// </summary>
    public string PayloadFlow(string name = PayloadFlowName, string? roots = null, bool hashDetect = false, bool manualSubmission = true, string? mapping = null) => Flow(
        name,
        mapping ?? MappingReference,
        // Roots belong to a flow that offers manual submission; a flow that offers none declares none.
        sourceExtra: "  payloads:\n    files: files/{deliveryKey}/*.csv"
            + (manualSubmission ? "\n  manualSubmissionFileRoots:\n    - " + (roots ?? Lake) : string.Empty),
        protocol: "osduFile",
        targetExtra: "  protocolOptions:\n    payload: files\n    payloadContentType: text/csv",
        manualSubmission: manualSubmission,
        changeExtra: hashDetect ? null : "  payloadDetect: lastModified");

    /// <summary>Puts chunk files where a record can point at them, and answers with the folder holding them.</summary>
    public string WritePayloadFiles(string folder, params string[] chunks)
    {
        var path = Path.Combine(Lake, folder);
        Directory.CreateDirectory(path);
        for (var i = 0; i < Math.Max(chunks.Length, 1); i++)
        {
            File.WriteAllText(Path.Combine(path, $"chunk-{i:D5}.csv"), chunks.Length == 0 ? "depth,value\n0,1\n" : chunks[i]);
        }

        return path.Replace('\\', '/');
    }

    public string WriteFlow(string name, string yaml)
    {
        var path = Path.Combine(Root, "flows", name + ".yaml");
        File.WriteAllText(path, yaml);
        return path;
    }

    /// <summary>Adds a mapping to this copy of the estate (a promoted version, a variant that resolves a reference).</summary>
    public void WriteMapping(string reference, string yaml)
        => File.WriteAllText(Path.Combine(Root, "mappings", reference + ".yaml"), yaml);

    /// <summary>The delivery key both halves derive for a wellbore, from the mapping's dataset key.</summary>
    public static DeliveryKey Key(string facilityName) => DeliveryKey.Derive("recall", [facilityName]);

    /// <summary>One wellbore as a source sends it: the columns the mapping reads, the flow's version column, and its aliases.</summary>
    public static object Wellbore(string? facilityName, string description, string updated, params string[] aliases) => new
    {
        record = Row(facilityName, description, updated),
        datasets = new Dictionary<string, object> { ["aliases"] = aliases.Select(a => new Dictionary<string, object?> { ["alias_name"] = a }).ToArray() },
    };

    /// <summary>
    /// One wellbore pointing at where its payload files already sit, as a source sends it to a flow that streams them:
    /// the location alone, or with the content hash a flow deciding payload changes by hash needs.
    /// </summary>
    public static object WellboreWithFiles(string facilityName, string description, string updated, string location, string? hash = null) => new
    {
        record = Row(facilityName, description, updated),
        files = new Dictionary<string, object> { [PayloadName] = hash is null ? location : new { location, hash } },
    };

    /// <summary>A wellbore with no alias rows at all, so a record without child rows is covered too.</summary>
    public static object WellboreWithoutAliases(string facilityName, string description, string updated)
        => new { record = Row(facilityName, description, updated) };

    public static Dictionary<string, object?> Row(string? facilityName, string description, string updated) => new()
    {
        ["facility_name"] = facilityName,
        ["facility_description"] = description,
        ["facility_id"] = facilityName is null ? null : "srn:master-data/Wellbore:" + facilityName,
        ["update_date"] = updated,
    };

    public static string Records(params object[] records) => JsonSerializer.Serialize(records);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A file a run still holds open is left to the temp folder's own cleanup.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
