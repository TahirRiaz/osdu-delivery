namespace SqlFlow.Delivery.Model;

/// <summary>
/// The eds-dms builds at the pinned commit (osdu/specs/eds-dms/INTEGRATION.md section 3.1): the community build and the
/// Azure and Google Cloud providers.
/// </summary>
public enum EdsBuild
{
    /// <summary><c>eds-dms-core-plus</c>, the community build.</summary>
    CorePlus,

    /// <summary><c>provider/eds-dms-azure</c>.</summary>
    Azure,

    /// <summary><c>provider/eds-dms-gc</c>, the only build that takes <c>GcpServiceAccount</c> schemes (section 5.2).</summary>
    Gc,
}

/// <summary>
/// What a flow says about the External Data Services deployment behind its target (<c>target.eds</c>). EDS takes no
/// pushed data: the records that configure it go through the storage route (or the manifest or workflow route), and
/// these settings decide how they are checked before they are sent (osdu/specs/eds-dms/INTEGRATION.md section 2).
/// </summary>
public sealed record EdsTarget
{
    /// <summary>
    /// Whether connected source registry entries, data jobs and proxy datasets are checked for what EDS needs before
    /// they are sent, and held when EDS could not use them. Default true.
    /// </summary>
    public bool Checks { get; init; } = true;

    /// <summary>
    /// Whether the partition's proxy datasets are retrieved through eds-dms, which reads every registry entry's
    /// <c>DatasetURL</c>: a registry entry without one fails every retrieval of its datasets (section 7.2). Default true;
    /// false lets through registry entries whose jobs fetch no files.
    /// </summary>
    public bool Retrieval { get; init; } = true;

    /// <summary>The eds-dms build the partition runs; null when the flow does not say, which refuses the schemes only one build takes.</summary>
    public EdsBuild? Build { get; init; }
}
