using MediaCatalog.Core.Models;
using MediaCatalog.Core.Tools;

namespace MediaCatalog.Core.Integrity;

/// <summary>
/// Writes what a header probe found onto a catalogue entry: how long the file runs and
/// how good it is.
///
/// One implementation for all three places that ask — a scan, a batch analysis, and a
/// single file the user has picked out to verify — so the Length and Quality columns mean
/// the same thing however they came to be filled in.
/// </summary>
public class MediaProbe
{
    private readonly MediaIntegrityChecker _checker;
    private readonly ExternalTools _tools;

    public MediaProbe(ExternalTools tools)
    {
        _tools = tools;
        _checker = new MediaIntegrityChecker(tools);
    }

    /// <summary>True when there is a tool to probe with at all.</summary>
    public bool IsAvailable => _tools.HasFfprobe;

    /// <summary>
    /// True when this entry still has something to learn from a probe. Lets a scan skip
    /// the files it already knows about, which is what keeps re-scanning cheap.
    /// </summary>
    public static bool NeedsProbe(MediaFile file) =>
        file.Kind is MediaKind.Audio or MediaKind.Video &&
        file.Integrity != IntegrityStatus.IncompleteDownload &&
        (file.DurationSeconds <= 0 || file.Quality <= 0);

    /// <summary>
    /// Probe <paramref name="file"/> and record the length, the quality and what the probe
    /// made of the container. Returns false when there was nothing to probe with, or the
    /// file has gone — neither of which is worth reporting as a failure.
    /// </summary>
    public async Task<bool> ApplyAsync(MediaFile file, CancellationToken ct = default)
    {
        if (!IsAvailable) return false;
        if (!File.Exists(file.FullPath)) return false;

        var probe = await _checker.ProbeAsync(file.FullPath, ct);
        Apply(file, probe);
        return true;
    }

    /// <summary>
    /// Copy a probe's findings onto an entry. Only ever adds: a probe that could not read
    /// the duration must not wipe a duration something else worked out.
    /// </summary>
    public static void Apply(MediaFile file, IntegrityCheckResult probe)
    {
        if (probe.DurationSeconds > 0) file.DurationSeconds = probe.DurationSeconds;
        if (probe.Status != IntegrityStatus.NotChecked) file.Integrity = probe.Status;

        // Video is measured in lines of picture, audio in kilobits a second. A video file
        // with no picture stream (an audio-only .mkv, say) falls back to its bitrate rather
        // than being left blank.
        var quality = file.Kind == MediaKind.Audio
            ? probe.BitrateKbps
            : probe.Height > 0 ? probe.Height : probe.BitrateKbps;
        if (quality > 0) file.Quality = quality;
    }
}
