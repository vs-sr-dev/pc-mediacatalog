using MediaCatalog.Core.Models;
using MediaCatalog.Core.Tools;

namespace MediaCatalog.Core.Fingerprinting;

/// <summary>
/// Decides whether two files that claim to be the same thing really are, when they do not
/// run to the same length.
///
/// A video fingerprint is sixteen frames spread evenly across the whole file, which is what
/// makes it comparable between two encodings of one film. It is also what makes it useless
/// the moment the two files are of different lengths: put a minute of credits on the end of
/// one and every one of its sixteen samples lands at a different moment, so the two
/// fingerprints disagree about a film they both hold in full. That is why copies of
/// different lengths were never consolidated automatically — not because anything had
/// decided they were different, but because nothing could tell.
///
/// The answer is to compare them over the stretch they have in common. Fingerprinting both
/// files as though each were only as long as the shorter one puts every sample back on the
/// same moment, and the comparison means what it says again. It costs sixteen frames of
/// decoding per file, and it is only paid where the ordinary comparison has already failed
/// and the lengths explain why.
///
/// Audio needs none of this: an acoustic fingerprint is taken from the first two minutes by
/// the clock, so it is unaffected by how long the file runs.
/// </summary>
public static class ContentSameness
{
    /// <summary>
    /// How far apart two files' lengths are, in seconds, or null when one of them has never
    /// been measured — which is not the same as their agreeing.
    /// </summary>
    public static double? LengthGap(MediaFile a, MediaFile b) =>
        a.DurationSeconds > 0 && b.DurationSeconds > 0
            ? Math.Abs(a.DurationSeconds - b.DurationSeconds)
            : null;

    /// <summary>
    /// True when the lengths are close enough to be the same thing, by the tolerance set for
    /// the category. Unmeasured lengths count as agreeing: not knowing is not a disagreement,
    /// and the fingerprints are the real test either way.
    /// </summary>
    public static bool LengthsAgree(MediaFile a, MediaFile b, int toleranceSeconds) =>
        LengthGap(a, b) is not { } gap || gap <= toleranceSeconds;

    /// <summary>The longest gap between any two of a set of copies, or null if none is known.</summary>
    public static double? WidestGap(IReadOnlyList<MediaFile> files)
    {
        var measured = files.Where(f => f.DurationSeconds > 0).Select(f => f.DurationSeconds).ToList();
        return measured.Count < 2 ? null : measured.Max() - measured.Min();
    }

    /// <summary>
    /// True when these two look like the same content, allowing for one running longer than
    /// the other.
    ///
    /// The ordinary comparison is tried first and costs nothing; only when it fails <em>and</em>
    /// the two are of different lengths is the common stretch re-fingerprinted, because that
    /// is the one case where a disagreement has an innocent explanation.
    /// </summary>
    public static async Task<bool> LooksLikeSameContentAsync(
        MediaFile a, MediaFile b, ExternalTools tools, CancellationToken ct = default)
    {
        if (FingerprintMatcher.LooksLikeSameContent(a, b)) return true;

        // An acoustic fingerprint is already taken from the same two minutes of each file,
        // so a disagreement there is a real one.
        if (a.Kind != MediaKind.Video || b.Kind != MediaKind.Video) return false;
        if (!tools.CanDoVideo) return false;

        var common = Math.Min(a.DurationSeconds, b.DurationSeconds);
        if (common <= 1) return false;                       // nothing measured, nothing to align
        if (LengthGap(a, b) is not { } gap || gap < 1) return false;   // same length: no excuse

        var fingerprinter = new VideoFingerprinter(tools);
        var left = await fingerprinter.ComputeAsync(a.FullPath, common, ct);
        if (left.Length == 0) return false;
        var right = await fingerprinter.ComputeAsync(b.FullPath, common, ct);
        if (right.Length == 0) return false;

        // Deliberately not written back to the files: a fingerprint taken over part of a
        // file is only comparable with another taken over the same part, and storing it
        // would quietly poison every later comparison against a third copy.
        var similarity = FingerprintMatcher.BestVideoSimilarity(
            VideoFingerprinter.Parse(left), VideoFingerprinter.Parse(right));
        return similarity >= FingerprintMatcher.VideoThreshold;
    }

    /// <summary>
    /// How the difference reads in a sentence — "1:03 longer", "4 seconds shorter" — for
    /// telling the user why something was or was not settled on its own.
    /// </summary>
    public static string Describe(double seconds)
    {
        var whole = (int)Math.Round(Math.Abs(seconds));
        if (whole < 60) return $"{whole} second{(whole == 1 ? "" : "s")}";
        var span = TimeSpan.FromSeconds(whole);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }
}
