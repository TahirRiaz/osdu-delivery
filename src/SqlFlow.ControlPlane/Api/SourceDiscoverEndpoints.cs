using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Sources;

namespace SqlFlow.ControlPlane.Api;

/// <summary>The body to discover a source: a file or folder location the deployment can reach, plus the optional
/// format (blank = auto-detect), folder pattern/recursion, record-grain override, and sample bounds. The location is
/// scanned read-only; nothing is written to the catalog.</summary>
public sealed record SourceDiscoverRequest(
    string Location, string? Format, string? Pattern, bool? Recursive, string? RootPath,
    int? MaxFiles, int? MaxRecords, int? MaxDepth, string? DefaultColumnType);

/// <summary>One discovered path (nested sources): its address, what it points at, the column it becomes, and how many
/// of the scanned records carried it (<see cref="Present"/> is false when it is missing from some records).</summary>
public sealed record DiscoveredPathDto(string Path, string Kind, string Column, int RecordCount, bool Present);

/// <summary>One discovered output column: its name, target SQL type, nullability, and (for a flattened nested source)
/// the source path it came from.</summary>
public sealed record DiscoveredColumnDto(string Name, string SqlType, bool Nullable, string? SourcePath);

/// <summary>A key/value pair for the generated <c>source.options</c> block.</summary>
public sealed record SourceOptionDto(string Key, string? Value);

/// <summary>The discovery outcome. <see cref="Mode"/> is "flatten" (JSON/XML: <see cref="Paths"/> + a record grain)
/// or "columnar" (CSV/Excel/Parquet: <see cref="Columns"/>). Both carry the runnable ingestion <see cref="GeneratedYaml"/>
/// and how the format was decided (<see cref="DetectionConfidence"/> + <see cref="DetectionEvidence"/>).</summary>
public sealed record SourceDiscoverResult(
    string Mode, string SourceType, string DetectionConfidence, IReadOnlyList<string> DetectionEvidence,
    string? AutoDetectedGrain, int FilesScanned, int RecordsScanned, bool SchemaDrift,
    IReadOnlyList<DiscoveredPathDto> Paths, IReadOnlyList<DiscoveredColumnDto> Columns,
    IReadOnlyList<SourceOptionDto> Options, string GeneratedYaml);

/// <summary>
/// Exposes unified source discovery over HTTP: given any supported file source (JSON, XML, CSV, Excel, Parquet) the
/// deployment can reach, detect its format, report its structure, and generate the ingestion flow YAML. Read-only, so
/// it maps under the same "operate" scope as datasource introspection and repo discover (it reads an external
/// location using the deployment's identity, but never mutates the catalog).
/// </summary>
public static class SourceDiscoverEndpoints
{
    // Sample bounds, clamped so a request cannot ask the node to walk an unbounded lake or parse to an unbounded
    // nesting depth. The defaults match the CLI's 'discover' verb (100 files, all records, depth 10).
    private const int DefaultMaxFiles = 100;
    private const int MaxAllowedFiles = 1000;
    private const int DefaultMaxRecords = 0;
    private const int MaxAllowedRecords = 1_000_000;
    private const int DefaultMaxDepth = 10;
    private const int MaxAllowedDepth = 50;

    public static RouteGroupBuilder MapSourceDiscoverEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.MapPost("/sources/discover", DiscoverSourceAsync).WithTags("Sources").WithName("DiscoverSource");
        return group;
    }

    private static async Task<Results<Ok<SourceDiscoverResult>, ProblemHttpResult>> DiscoverSourceAsync(
        SourceDiscoverRequest request, SourceDiscoveryService discovery, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Location))
        {
            return TypedResults.Problem(
                detail: "A discover requires a non-blank location (a file, folder, or URI the deployment can reach).",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        var serviceRequest = new SourceDiscoveryRequest(
            request.Location.Trim(),
            request.Format,
            string.IsNullOrWhiteSpace(request.Pattern) ? null : request.Pattern.Trim(),
            request.Recursive ?? false,
            request.RootPath,
            Clamp(request.MaxFiles ?? DefaultMaxFiles, 1, MaxAllowedFiles),
            Clamp(request.MaxRecords ?? DefaultMaxRecords, 0, MaxAllowedRecords),
            Clamp(request.MaxDepth ?? DefaultMaxDepth, 1, MaxAllowedDepth),
            string.IsNullOrWhiteSpace(request.DefaultColumnType) ? null : request.DefaultColumnType.Trim());

        try
        {
            var result = await discovery.DiscoverAsync(serviceRequest, ct).ConfigureAwait(false);

            var paths = result.Paths
                .Select(p => new DiscoveredPathDto(
                    p.Path,
                    p.Kind.ToString().ToLowerInvariant(),
                    p.Kind == SchemaPathKind.Container ? "-" : p.Column,
                    p.RecordCount,
                    p.RecordCount >= result.RecordsScanned))
                .ToList();

            var columns = result.Columns
                .Select(c => new DiscoveredColumnDto(c.Name, c.SqlType, c.Nullable, c.SourcePath))
                .ToList();

            var options = result.Options
                .Select(o => new SourceOptionDto(o.Key, o.Value))
                .ToList();

            return TypedResults.Ok(new SourceDiscoverResult(
                result.Mode, result.SourceType, result.DetectionConfidence, result.DetectionEvidence,
                result.AutoDetectedGrain, result.FilesScanned, result.RecordsScanned, result.SchemaDrift,
                paths, columns, options, result.GeneratedYaml));
        }
        catch (SqlFlowException ex)
        {
            // A bad location, an empty selection, an unknown format, or a malformed sample are the caller's to fix, so
            // they surface as a 400 with a clean (secret-redacted) message rather than an opaque 500.
            return TypedResults.Problem(
                detail: SecretHygiene.RedactedMessage(ex),
                statusCode: StatusCodes.Status400BadRequest, title: "Discover failed");
        }
    }

    private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
}
