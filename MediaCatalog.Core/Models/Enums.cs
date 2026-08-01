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

/// <summary>Which kinds of media a scan should pick up.</summary>
public enum ScanMediaFilter
{
    /// <summary>Everything: audio and video alike (the default).</summary>
    All = 0,
    /// <summary>Only video files.</summary>
    VideoOnly,
    /// <summary>Only audio files.</summary>
    AudioOnly
}

/// <summary>Where the currently-processed file name appears in the progress message.</summary>
public enum ProgressNamePosition
{
    /// <summary>After the phase and counter, as it has always been.</summary>
    Right = 0,
    /// <summary>Before the phase, so "Hashing &amp; classifying" holds still.</summary>
    Left,
    /// <summary>Not shown at all — the steadiest of the three.</summary>
    Hidden
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
