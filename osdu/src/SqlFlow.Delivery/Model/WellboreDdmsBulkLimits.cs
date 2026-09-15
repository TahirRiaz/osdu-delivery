using System.Globalization;

namespace SqlFlow.Delivery.Model;

/// <summary>
/// The wellbore DDMS bulk write ceilings, in one place with their provenance. Every other ceiling on the payload
/// path (the Kestrel request body limit, the ingress body size, the API gateway limit) belongs to the estate and
/// can be raised; these two belong to the service and cannot (design.md section 14.3).
/// </summary>
/// <remarks>
/// <para>
/// Source: the wellbore DDMS OpenAPI description of <c>POST /ddms/v3/welllogs/{record_id}/data</c>, which says a
/// bulk payload is "big enough to be sent with chunking APIs" when it is "&gt; 10 millions values or &gt; 3000
/// columns", and that each chunk should carry as many columns as fit "until upper limits are reached". Verified
/// against <c>openapi_specs/wellbore_ddms/openapi.json</c> in the sibling <c>osdu-csharp-client</c> checkout and
/// against the specification Microsoft publishes for Azure Data Manager for Energy at
/// <c>microsoft.github.io/adme-samples/rest-apis/M26/wellbore_ddms_openapi.yaml</c>.
/// </para>
/// <para>
/// The numbers come from the service's own constants (<c>app/bulk_persistence/constants.py</c> in the OSDU
/// wellbore-domain-services repository): <c>WRITE_MAX_TOTAL_VALUES_COUNT = 10_000_000</c>, annotated "restrict
/// chunk to ~100MB", and <c>WRITE_MAX_COLUMNS_COUNT = READ_MAX_COLUMNS_COUNT = 3_000</c>. They are a memory
/// budget for the frame the service materialises, not a request body size, so a chunk can satisfy the estate's
/// byte ceiling and still be too large for the service.
/// </para>
/// <para>
/// The column ceiling is milestone-dependent: the M23 and M25 specifications say 500, M26 says 3000. The default
/// here is the current one; a flow whose target runs an earlier milestone declares
/// <c>target.protocolOptions.maxChunkColumns: 500</c>. The value ceiling has been 10 million throughout.
/// </para>
/// <para>
/// The service does not reliably reject an oversized write: on the current upstream, the write-side value ceiling
/// is unreferenced and the column validator is not wired into any route, so exceeding either one shows up as a
/// slow write or an out-of-memory worker rather than a clean 4xx. That is why the delivery side checks it before
/// sending, and holds the record instead of writing metadata it cannot follow with a payload.
/// </para>
/// </remarks>
public static class WellboreDdmsBulkLimits
{
    /// <summary>Cells (rows times columns) one chunk may carry. The service reads a chunk into one frame.</summary>
    public const long MaxChunkValues = 10_000_000;

    /// <summary>Columns (the index column plus the curves) one chunk may carry, from OSDU M26 onwards.</summary>
    public const int MaxChunkColumns = 3_000;

    /// <summary>The same ceiling as the M23 and M25 specifications state it, for targets on those milestones.</summary>
    public const int MaxChunkColumnsThroughM25 = 500;

    /// <summary>
    /// The reason one chunk cannot be delivered as it stands, or null when it is within both ceilings. A ceiling
    /// of zero or less is not checked, which is how a flow opts out for a target that has raised it.
    /// </summary>
    /// <param name="chunkIndex">The chunk's ordinal within the payload, as the ledger records it.</param>
    /// <param name="chunkPath">The chunk file, so the message names the file to re-chunk.</param>
    /// <param name="rows">Rows in the chunk.</param>
    /// <param name="columns">Top-level columns in the chunk.</param>
    /// <param name="maxValues">The value ceiling in force for the target.</param>
    /// <param name="maxColumns">The column ceiling in force for the target.</param>
    public static string? Exceeded(int chunkIndex, string chunkPath, long rows, int columns, long maxValues, int maxColumns)
    {
        var name = string.IsNullOrEmpty(chunkPath) ? "chunk " + Text(chunkIndex) : Path.GetFileName(chunkPath);
        if (maxColumns > 0 && columns > maxColumns)
        {
            return $"payload chunk {Text(chunkIndex)} ({name}) has {Text(columns)} columns, above the wellbore DDMS ceiling of {Text(maxColumns)} columns per chunk (openapi wellbore_ddms, POST /ddms/v3/welllogs/{{record_id}}/data; target.protocolOptions.maxChunkColumns); split the curves across chunks in prepare";
        }

        if (maxValues > 0 && Values(rows, columns) > maxValues)
        {
            return $"payload chunk {Text(chunkIndex)} ({name}) has {Text(Values(rows, columns))} values ({Text(rows)} rows by {Text(columns)} columns), above the wellbore DDMS ceiling of {Text(maxValues)} values per chunk (openapi wellbore_ddms, POST /ddms/v3/welllogs/{{record_id}}/data; target.protocolOptions.maxChunkValues); re-chunk in prepare";
        }

        return null;
    }

    /// <summary>Cells in a chunk, saturating rather than overflowing on a shape no file can actually hold.</summary>
    public static long Values(long rows, int columns)
    {
        if (rows <= 0 || columns <= 0)
        {
            return 0;
        }

        return rows > long.MaxValue / columns ? long.MaxValue : rows * columns;
    }

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
}
