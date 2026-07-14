using SqlFlow.Core.Files;

namespace SqlFlow.Core.Invoke;

/// <summary>
/// The file(s) an invoke's external compute (an ADF pipeline or Automation runbook) lands, declared so lineage can
/// connect the invoke to the downstream file ingestion that reads them. The shape mirrors a file source's selection
/// spec on purpose: <see cref="Location"/> is the folder (or a full file path) the run writes to, <see cref="SrcFile"/>
/// is the file-name glob within it (defaulted from the location's extension when absent), and <see cref="SrcPathMask"/>
/// is an optional regex over the full path. From this, <see cref="SqlFlow.Core.Files.FileSelection.Feeds"/> decides,
/// with engine parity, which file ingestion the invoke feeds. Immutable.
/// </summary>
public sealed record InvokeOutput
{
    /// <summary>The folder or full file path the external run lands data at: a local path (normalized against the
    /// estate root for lineage identity) or a cloud URL (kept verbatim). Required whenever an output is declared.</summary>
    public required string Location { get; init; }

    /// <summary>The file-name glob the run produces within <see cref="Location"/> (e.g. <c>orders_*.csv</c>); when
    /// absent it is inferred from the location's file name, or left to match any file in the folder.</summary>
    public string? SrcFile { get; init; }

    /// <summary>An optional regex over the full landing path, matched the way a file source's <c>srcPathMask</c> is,
    /// for cases the folder prefix cannot express.</summary>
    public string? SrcPathMask { get; init; }

    /// <summary>This output as a file-selection spec, so the shared matcher treats an invoke's output and a file
    /// source identically.</summary>
    public FileSelectionSpec ToSelectionSpec()
        => new() { Location = Location, Glob = SrcFile, Mask = SrcPathMask };
}
