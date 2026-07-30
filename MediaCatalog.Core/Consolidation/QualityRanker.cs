using System.Text.RegularExpressions;
using MediaCatalog.Core.Models;

namespace MediaCatalog.Core.Consolidation;

/// <summary>
/// Scores a file's video quality from its name (and size as a tiebreaker), so that when
/// several copies of the same film/episode exist the best one can be preferred for
/// consolidation. Higher score = better.
/// </summary>
public static class QualityRanker
{
    private static readonly Regex Resolution = new(
        @"(?<![0-9])(2160|1440|1080|720|576|480|360|240)\s*[pi]?(?![0-9])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FourK = new(@"\b(4k|uhd|2160p)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Resolution score in vertical pixels (2160, 1080, …), or 0 if unknown.</summary>
    public static int ResolutionScore(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return 0;
        var best = 0;
        if (FourK.IsMatch(fileName)) best = 2160;
        foreach (Match m in Resolution.Matches(fileName))
            best = Math.Max(best, int.Parse(m.Groups[1].Value));
        return best;
    }

    /// <summary>
    /// Compare two files by quality; the larger is "better". Uses resolution first, then
    /// file size as a tiebreaker (a bigger file at the same resolution is usually higher
    /// bitrate). Returns &gt;0 if <paramref name="a"/> is better, &lt;0 if worse, 0 if equal.
    /// </summary>
    public static int Compare(MediaFile a, MediaFile b)
    {
        var ra = ResolutionScore(a.FileName);
        var rb = ResolutionScore(b.FileName);
        if (ra != rb) return ra.CompareTo(rb);
        return a.SizeBytes.CompareTo(b.SizeBytes);
    }

    /// <summary>The highest-quality file among the given set.</summary>
    public static MediaFile? Best(IEnumerable<MediaFile> files)
    {
        MediaFile? best = null;
        foreach (var f in files)
            if (best == null || Compare(f, best) > 0)
                best = f;
        return best;
    }
}
