namespace SqlFlow.Acquire.Landing;

/// <summary>
/// The write side of raw landing: persists a fetched payload verbatim to a location, so a downstream SQLFlow file
/// flow can ingest it exactly as if a runbook had uploaded it. Complements the read-only <c>IFileStore</c>. A store
/// advertises the locations it handles (a local/UNC path or an Azure Storage URI) and is selected by location.
/// </summary>
public interface IRawLandingStore
{
    /// <summary>True if this store handles the given base location (a local/UNC path or an Azure Storage URI).</summary>
    bool CanHandle(string location);

    /// <summary>Joins a base location and a relative path into a full location this store can address.</summary>
    string Combine(string baseLocation, string relativePath);

    /// <summary>True if an object already exists at the location (used to honor a no-overwrite policy).</summary>
    Task<bool> ExistsAsync(string location, CancellationToken ct = default);

    /// <summary>Writes the payload, creating any parent directories. Overwrites only when <paramref name="overwrite"/> is set.</summary>
    Task PutAsync(string location, ReadOnlyMemory<byte> content, bool overwrite, CancellationToken ct = default);
}
