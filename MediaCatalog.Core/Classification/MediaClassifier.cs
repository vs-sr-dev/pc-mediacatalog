using System.Text.RegularExpressions;
using MediaCatalog.Core.Models;
using MediaCatalog.Core.Scanning;

namespace MediaCatalog.Core.Classification;

/// <summary>
/// Best-effort classification and metadata extraction from a file name alone
/// (no online lookups). Good enough to separate the common cases; ambiguous
/// files fall through to <see cref="VideoCategory.Unknown"/>.
/// </summary>
public static class MediaClassifier
{
    // S01E02 / s1e2 / S01.E02 / S01 E02
    private static readonly Regex SeasonEpisode = new(
        @"[Ss](?<s>\d{1,2})[\s._-]*[Ee](?<e>\d{1,3})",
        RegexOptions.Compiled);

    // 1x02 / 01x02
    private static readonly Regex XFormat = new(
        @"(?<![a-zA-Z0-9])(?<s>\d{1,2})x(?<e>\d{1,3})(?![a-zA-Z0-9])",
        RegexOptions.Compiled);

    // "Season 1", "Series 1"
    private static readonly Regex SeasonWord = new(
        @"\b(?:season|series)\s*\d{1,2}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // A plausible release year in brackets or standalone: 1900-2099
    private static readonly Regex Year = new(
        @"(?<![0-9])(?<y>(?:19|20)\d{2})(?![0-9])",
        RegexOptions.Compiled);

    // Common release-group / quality noise we strip from titles.
    private static readonly Regex Noise = new(
        @"\b(1080p|720p|2160p|480p|4k|x264|x265|h264|h265|hevc|xvid|divx|" +
        @"bluray|brrip|bdrip|dvdrip|webrip|web-?dl|hdtv|aac|ac3|dts|hdr|" +
        @"remux|proper|repack|internal|extended|uncut)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static void Classify(MediaFile file)
    {
        file.Kind = MediaExtensions.Classify(file.Extension);

        var name = Path.GetFileNameWithoutExtension(file.FileName);

        // Year applies to both movies and TV; capture the first plausible one.
        var yearMatch = Year.Match(name);
        if (yearMatch.Success && int.TryParse(yearMatch.Groups["y"].Value, out var y))
            file.Year = y;

        if (file.Kind != MediaKind.Video)
        {
            // Audio: just derive a cleaned title.
            file.ParsedTitle = CleanTitle(name, cutAt: -1);
            return;
        }

        int titleCut = -1;
        var se = SeasonEpisode.Match(name);
        var xf = XFormat.Match(name);

        if (se.Success)
        {
            file.VideoCategory = VideoCategory.TvShow;
            file.Season = ParseInt(se.Groups["s"].Value);
            file.Episode = ParseInt(se.Groups["e"].Value);
            titleCut = se.Index;
        }
        else if (xf.Success)
        {
            file.VideoCategory = VideoCategory.TvShow;
            file.Season = ParseInt(xf.Groups["s"].Value);
            file.Episode = ParseInt(xf.Groups["e"].Value);
            titleCut = xf.Index;
        }
        else if (SeasonWord.Match(name) is { Success: true } sw)
        {
            file.VideoCategory = VideoCategory.TvShow;
            titleCut = sw.Index;
        }
        else if (file.Year.HasValue)
        {
            // A year with no episode markers is the classic movie signature.
            file.VideoCategory = VideoCategory.Movie;
            titleCut = yearMatch.Index;
        }
        else
        {
            file.VideoCategory = VideoCategory.Unknown;
        }

        file.ParsedTitle = CleanTitle(name, titleCut);
    }

    private static int? ParseInt(string s) =>
        int.TryParse(s, out var v) ? v : null;

    /// <summary>
    /// Turn "The.Movie.2010.1080p.BluRay" into "The Movie" by cutting at the first
    /// season/episode/year marker and stripping separators and release noise.
    /// </summary>
    private static string CleanTitle(string name, int cutAt)
    {
        var head = cutAt > 0 ? name[..cutAt] : name;
        head = head.Replace('.', ' ').Replace('_', ' ').Replace('-', ' ');
        head = Noise.Replace(head, " ");
        head = Regex.Replace(head, @"\s+", " ").Trim(' ', '(', ')', '[', ']');
        return head;
    }
}
