using SqlFlow.Core;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// Reads a mapping the repository sync kept in the module database, where the engine has no repository tree to read it from:
/// the explorer and an assertion flow judge a mapping's assertions on what OSDU holds this way
/// (osdu/docs/reference/flow/mapping-assertions.md). A mapping is read by its synced id, or by its reference among every
/// repository that declares it; each answer is the mapping, or why it cannot be had, worded for the page or report that
/// shows it.
/// </summary>
public static class SyncedMappings
{
    /// <summary>The mapping synced under <paramref name="id"/>, or why it cannot be read.</summary>
    public static async Task<(MappingDefinition? Mapping, string? Problem)> ByIdAsync(EngineContext context, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Ledger is not { } ledger)
        {
            return (null, "the host keeps no module database, so no synced mapping can be read");
        }

        return await ledger.SyncedMappingAsync(id, ct).ConfigureAwait(false) is { } synced
            ? Parse(context, synced)
            : (null, $"no mapping is synced under {id:D}; its repository may no longer declare it");
    }

    /// <summary>
    /// The mapping synced under <paramref name="reference"/> (<c>Name@version</c>), or why it cannot be read: none is synced,
    /// or several repositories declare the reference with documents that differ, which leaves no one mapping to read.
    /// </summary>
    public static async Task<(MappingDefinition? Mapping, string? Problem)> ByReferenceAsync(EngineContext context, string reference, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        if (context.Ledger is not { } ledger)
        {
            return (null, $"the host keeps no module database, so mapping {reference} cannot be read");
        }

        var synced = await ledger.SyncedMappingsAsync(reference, ct).ConfigureAwait(false);
        if (synced.Count == 0)
        {
            return (null, $"no mapping {reference} is synced; sync the repository that declares it, then run again");
        }

        var documents = synced.GroupBy(s => s.ContentHash, StringComparer.Ordinal).ToList();
        if (documents.Count > 1)
        {
            return (null,
                $"mapping {reference} is synced from {synced.Select(s => s.RepoId).Distinct().Count()} repositories whose documents differ "
                + $"({string.Join(", ", documents.Select(d => d.First().RelativePath))}), so there is no one mapping to read; give each its own version");
        }

        return Parse(context, synced[0]);
    }

    private static (MappingDefinition? Mapping, string? Problem) Parse(EngineContext context, SyncedMapping synced)
    {
        if (!synced.Valid)
        {
            return (null, $"mapping {synced.Reference} ({synced.RelativePath}) does not load: {synced.Message ?? "the sync recorded no reason"}");
        }

        try
        {
            return (context.Documents.ParseMapping(synced.Yaml, synced.RelativePath), null);
        }
        catch (FlowValidationException ex)
        {
            // The document loaded when it was synced and does not now: this build reads mappings more strictly.
            return (null, $"mapping {synced.Reference} ({synced.RelativePath}) does not load: {ex.Message}");
        }
    }
}
