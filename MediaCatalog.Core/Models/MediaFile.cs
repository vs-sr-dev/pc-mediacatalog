using System.Xml.Serialization;

namespace MediaCatalog.Core.Models;

/// <summary>
/// A single catalogued media file. Kept as a flat, XML-serialisable record so the
/// whole catalogue round-trips cleanly through <see cref="Persistence.CatalogStore"/>.
/// </summary>
public class MediaFile
{
    /// <summary>Stable identity for the file within the catalogue.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string FullPath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;

    public long SizeBytes { get; set; }
    public DateTime LastModifiedUtc { get; set; }

    public MediaKind Kind { get; set; } = MediaKind.Unknown;
    public VideoCategory VideoCategory { get; set; } = VideoCategory.Unknown;
    public IntegrityStatus Integrity { get; set; } = IntegrityStatus.NotChecked;

    /// <summary>SHA-256 of the file contents, lower-case hex. Empty until hashed.</summary>
    public string Sha256 { get; set; } = string.Empty;

    // --- Parsed metadata (filename-only for now; enriched in later phases) ---
    public string ParsedTitle { get; set; } = string.Empty;
    public int? Year { get; set; }
    public int? Season { get; set; }
    public int? Episode { get; set; }

    /// <summary>
    /// The last episode in a file that holds more than one — "S06E11E12" is episodes 11
    /// *and* 12 of season 6, and "S01E01-E02" is episodes 1 and 2 of season 1. Null for the
    /// usual single-episode file. <see cref="Episode"/> is always the first of the run, so
    /// everything that reads one episode number keeps working.
    /// </summary>
    public int? EpisodeEnd { get; set; }

    /// <summary>
    /// True when the season/episode was typed in by hand rather than read out of the name.
    ///
    /// Numbering the user entered is never cleared. A film does not have a season and an
    /// episode — but somebody typing one into a file filed as a film is telling us the file
    /// was filed wrongly, not asking for their correction to be thrown away.
    /// </summary>
    public bool NumberingManuallySet { get; set; }

    // --- User overrides / enrichment (all optional; default empty for old catalogues) ---
    /// <summary>Explicit per-file category set by the user; overrides <see cref="VideoCategory"/>.</summary>
    public string CategoryOverride { get; set; } = string.Empty;

    /// <summary>Set once a TV title has been confirmed against TMDb (cached).</summary>
    public bool TmdbVerified { get; set; }

    /// <summary>
    /// Set once the title has been found in the local IMDb extract. Checked before TMDb,
    /// since a local lookup costs nothing and has no rate limit.
    /// </summary>
    public bool ImdbVerified { get; set; }

    /// <summary>The canonical name TMDb returned, if validated.</summary>
    public string TmdbName { get; set; } = string.Empty;

    /// <summary>
    /// True when <see cref="TmdbName"/> was typed by the user rather than returned by
    /// TMDb. Manually corrected titles count as validated for consolidation purposes.
    /// </summary>
    public bool TitleManuallySet { get; set; }

    /// <summary>
    /// For extras (specials/featurettes): the <see cref="Id"/> of the film or episode
    /// they belong to, so they travel with it. Empty when unlinked.
    /// </summary>
    public string LinkedFileId { get; set; } = string.Empty;

    /// <summary>
    /// Which generation of catalogue features this entry has been processed by. Older
    /// entries (0) are brought up to date by a catalogue refresh without re-hashing.
    /// </summary>
    public int FeatureVersion { get; set; }

    /// <summary>
    /// True once the file lives in its consolidation location. Kept on the entry so the
    /// results can be filtered by what has and hasn't been filed yet.
    /// </summary>
    public bool Consolidated { get; set; }

    /// <summary>
    /// Set for files the watcher picked up that may still have been downloading, so their
    /// hash (and size) cannot be trusted until they are re-checked.
    /// </summary>
    public bool AwaitingDownload { get; set; }

    // --- Content analysis (populated on demand via external tools) ---
    /// <summary>Media duration in seconds, from ffprobe. 0 if unknown.</summary>
    public double DurationSeconds { get; set; }

    /// <summary>
    /// How good the file is, in the one number that means something for its kind: the
    /// picture height in pixels for video (720, 1080, 2160) and the bitrate in kbps for
    /// audio. 0 until something has looked. See <see cref="QualityDisplay"/> for how it
    /// is written out, which depends on <see cref="Kind"/>.
    /// </summary>
    public int Quality { get; set; }

    /// <summary>Chromaprint raw fingerprint (comma-separated uint32) for audio matching.</summary>
    public string AudioFingerprint { get; set; } = string.Empty;

    /// <summary>Perceptual video signature: hex of per-keyframe 64-bit dHashes.</summary>
    public string VideoFingerprint { get; set; } = string.Empty;

    /// <summary>When this entry was last (re)scanned.</summary>
    public DateTime IndexedUtc { get; set; }

    /// <summary>
    /// Set when hashing was attempted and failed (unreadable, locked, refused). Distinct
    /// from simply not having been hashed yet, so a scan can offer these to the user.
    /// </summary>
    public bool HashFailed { get; set; }

    [XmlIgnore]
    public bool HasHash => !string.IsNullOrEmpty(Sha256);

    /// <summary>
    /// True when the title came from somewhere authoritative — the local IMDb extract,
    /// TMDb, or the user's own hand. Everything else is a guess from the file name and is
    /// re-checked on a catalogue refresh.
    /// </summary>
    [XmlIgnore]
    public bool TitleVerified => ImdbVerified || TmdbVerified || TitleManuallySet;

    /// <summary>The title to show and to file under: the validated/edited one if set.</summary>
    [XmlIgnore]
    public string EffectiveTitle =>
        !string.IsNullOrWhiteSpace(TmdbName) ? TmdbName : ParsedTitle;

    /// <summary>
    /// The numbering as it reads: "S01E02", or "S06E11-E12" for a double episode. Blank
    /// when the file has none.
    /// </summary>
    [XmlIgnore]
    public string NumberingDisplay =>
        this is { Season: { } s, Episode: { } e }
            ? EpisodeEnd is { } last && last > e
                ? $"S{s:00}E{e:00}-E{last:00}"
                : $"S{s:00}E{e:00}"
            : string.Empty;

    /// <summary>
    /// Every episode number this file holds — one for an ordinary episode, two or more for
    /// a double. Empty when it has no numbering at all.
    /// </summary>
    [XmlIgnore]
    public IReadOnlyList<int> Episodes
    {
        get
        {
            if (Episode is not { } first) return Array.Empty<int>();
            var last = EpisodeEnd is { } end && end > first ? end : first;
            return Enumerable.Range(first, last - first + 1).ToList();
        }
    }

    /// <summary>True for specials/featurettes attached to a film or show.</summary>
    [XmlIgnore]
    public bool IsExtra =>
        VideoCategory is VideoCategory.TvExtra or VideoCategory.MovieExtra;

    /// <summary>
    /// The duration as "1:42:07" / "3:58", or blank when nothing has measured it yet.
    /// </summary>
    [XmlIgnore]
    public string LengthDisplay
    {
        get
        {
            if (DurationSeconds <= 0) return string.Empty;
            var span = TimeSpan.FromSeconds(Math.Round(DurationSeconds));
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
                : $"{span.Minutes}:{span.Seconds:00}";
        }
    }

    /// <summary>
    /// The quality with the unit its kind is measured in — "1080p" for video, "320 kbps"
    /// for audio — or blank when it has not been measured.
    /// </summary>
    [XmlIgnore]
    public string QualityDisplay =>
        Quality <= 0 ? string.Empty
        : Kind == MediaKind.Audio ? $"{Quality} kbps"
        : $"{Quality}p";
}
