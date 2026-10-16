using System.Text.Json.Nodes;

namespace SqlFlow.Delivery.Validation;

/// <summary>
/// An example record of an OSDU kind as the OSDU data definitions publish it (their <c>Examples</c> folder, one record per
/// kind version), which shows how each property of the kind is written.
/// </summary>
/// <param name="Kind">The kind the example is of.</param>
/// <param name="Release">The release of the data definitions it was read from.</param>
/// <param name="Path">Its path in the repository, such as <c>Examples/work-product-component/WellLog.1.4.0.json</c>.</param>
/// <param name="WebUrl">Where a person reads it.</param>
/// <param name="Record">The example record.</param>
public sealed record OfficialExample(string Kind, string Release, string Path, Uri WebUrl, JsonObject Record);

/// <summary>Where the example record the OSDU data definitions publish for a kind is read from.</summary>
public interface IOfficialExamples
{
    /// <summary>
    /// The example the data definitions publish for <paramref name="kind"/> (an <c>osdu</c> kind), or null when they publish
    /// none for it. A repository that cannot be reached throws, so a caller can say why no example is shown.
    /// </summary>
    Task<OfficialExample?> ExampleAsync(string kind, CancellationToken ct = default);
}
