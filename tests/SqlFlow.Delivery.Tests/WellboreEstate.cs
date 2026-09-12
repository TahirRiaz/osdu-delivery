using System.Text.Json;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The estate the inline submission suites run against: the sample Wellbore master-data mapping and the snapshots it
/// renders with, copied to a temp repository, plus a flow that delivers wellbores through the storage service. Manual
/// submission is wellbore master data, so this is the shape the suites exercise. A flow here streams no payload files,
/// which is what lets it take records in the request at all.
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
    /// a test needs to add to its source or target blocks (a work location, payload files and their protocol options).
    /// </summary>
    public string Flow(
        string name, string mapping, string? sourceExtra = null, string protocol = "osduRecord", string? targetExtra = null, bool manualSubmission = true) => $$"""
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

    public string WriteFlow(string name, string yaml)
    {
        var path = Path.Combine(Root, "flows", name + ".yaml");
        File.WriteAllText(path, yaml);
        return path;
    }

    /// <summary>Adds a mapping to this copy of the estate (a promoted version, a variant that resolves a reference).</summary>
    public void WriteMapping(string reference, string yaml)
        => File.WriteAllText(Path.Combine(Root, "mappings", reference + ".yaml"), yaml);

    /// <summary>The delivery key both halves derive for a wellbore, from the mapping's natural key.</summary>
    public static DeliveryKey Key(string facilityName) => DeliveryKey.Derive("recall", [facilityName]);

    /// <summary>One wellbore as a source sends it: the columns the mapping reads, the flow's version column, and its aliases.</summary>
    public static object Wellbore(string? facilityName, string description, string updated, params string[] aliases) => new
    {
        record = Row(facilityName, description, updated),
        scopes = new Dictionary<string, object> { ["aliases"] = aliases.Select(a => new Dictionary<string, object?> { ["alias_name"] = a }).ToArray() },
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
