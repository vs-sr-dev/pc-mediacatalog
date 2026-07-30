namespace MediaCatalog.Core.Models;

/// <summary>High-level kind of media a file represents.</summary>
public enum MediaKind
{
    Unknown = 0,
    Audio,
    Video
}

/// <summary>For video files, our best guess at what the content is.</summary>
public enum VideoCategory
{
    Unknown = 0,
    Movie,
    TvShow,
    Other,
    /// <summary>A special/featurette that belongs to a TV show.</summary>
    TvExtra,
    /// <summary>A special/featurette that belongs to a film.</summary>
    MovieExtra
}

/// <summary>Result of the lightweight integrity check performed during a scan.</summary>
public enum IntegrityStatus
{
    /// <summary>Not yet checked (deep checks are a later phase).</summary>
    NotChecked = 0,
    Ok,
    /// <summary>File looks like an in-progress download (.part/.crdownload/etc.).</summary>
    IncompleteDownload,
    /// <summary>Zero-byte or otherwise obviously broken.</summary>
    Corrupt
}
