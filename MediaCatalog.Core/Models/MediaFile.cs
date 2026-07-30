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

    // --- User overrides / enrichment (all optional; default empty for old catalogues) ---
    /// <summary>Explicit per-file category set by the user; overrides <see cref="VideoCategory"/>.</summary>
    public string CategoryOverride { get; set; } = string.Empty;

    /// <summary>Set once a TV title has been confirmed against TMDb (cached).</summary>
    public bool TmdbVerified { get; set; }

    /// <summary>The canonical name TMDb returned, if validated.</summary>
    public string TmdbName { get; set; } = string.Empty;

    // --- Content analysis (populated on demand via external tools) ---
    /// <summary>Media duration in seconds, from ffprobe. 0 if unknown.</summary>
    public double DurationSeconds { get; set; }

    /// <summary>Chromaprint raw fingerprint (comma-separated uint32) for audio matching.</summary>
    public string AudioFingerprint { get; set; } = string.Empty;

    /// <summary>Perceptual video signature: hex of per-keyframe 64-bit dHashes.</summary>
    public string VideoFingerprint { get; set; } = string.Empty;

    /// <summary>When this entry was last (re)scanned.</summary>
    public DateTime IndexedUtc { get; set; }

    [XmlIgnore]
    public bool HasHash => !string.IsNullOrEmpty(Sha256);
}
