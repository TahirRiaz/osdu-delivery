using System.Text.RegularExpressions;
using SqlFlow.Core.Model;

namespace SqlFlow.Sources;

/// <summary>
/// The discovery-time file selector: the optional path mask and the date window, applied during listing so an
/// out-of-window partition folder is pruned before it is walked and out-of-window files never enter the result.
/// The date is read where the fileDate spec says (path or name); a null spec keeps the legacy behavior of
/// reading the file's modified timestamp. The window bounds come from the flow's init date window and the injected
/// incremental watermark, so init-load and incremental share this one selection path.
/// </summary>
internal sealed class FileDateFilter : IFileDiscoveryFilter
{
    private readonly FileDateSpec? _spec;
    private readonly DateTime? _from;
    private readonly DateTime? _to;
    private readonly DateTime? _after;
    private readonly Regex? _pathMask;

    public FileDateFilter(FileDateSpec? spec, DateTime? from, DateTime? to, DateTime? after, Regex? pathMask)
    {
        _spec = spec;
        _from = from;
        _to = to;
        _after = after;
        _pathMask = pathMask;
    }

    /// <summary>True when at least one of the filters is active; a filter with nothing to enforce is not built.</summary>
    public bool IsActive => _spec is not null || _from is not null || _to is not null || _after is not null || _pathMask is not null;

    public bool ShouldEnterDirectory(string directoryPath)
    {
        // Only a path-derived date can rule a whole folder out; a name/modified date, or a folder that carries no
        // date tokens yet, is not prunable, so descend and let the per-file test decide.
        if (_spec is null || _spec.Source != FileDateSource.Path)
        {
            return true;
        }

        if (!HasWindow)
        {
            return true;
        }

        var interval = _spec.Extract(directoryPath);
        return interval is null || Overlaps(interval.Value);
    }

    public bool Includes(FileRef file)
    {
        if (_pathMask is not null && !_pathMask.IsMatch(file.Path))
        {
            return false;
        }

        if (!HasWindow)
        {
            return true;
        }

        if (_spec is null)
        {
            var modified = (file.Modified ?? DateTimeOffset.UtcNow).UtcDateTime;
            return Overlaps(new DateInterval(modified, modified));
        }

        var text = _spec.Source == FileDateSource.Path ? file.Path : file.Name;
        var interval = _spec.Extract(text);

        // A file that carries no parsable date is not part of a date-scoped selection: exclude it rather than
        // guess. With no window set (HasWindow false) we returned true above, so an undated file still loads then.
        return interval is not null && Overlaps(interval.Value);
    }

    private bool HasWindow => _from is not null || _to is not null || _after is not null;

    private bool Overlaps(DateInterval interval)
        => (_from is null || interval.Hi >= _from)
            && (_to is null || interval.Lo <= _to)
            && (_after is null || interval.Hi > _after);
}
