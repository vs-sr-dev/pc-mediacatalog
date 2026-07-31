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
    // Every way people write a season and episode together:
    //   S01E02, s1e2, S01.E02, S01 E02, "S04 E 01",
    //   "Season 1 Episode 01", "Series 1 Episode 1", "S1 Episode 1", "Season 2 Ep 3".
    // The word forms are matched at a word boundary so "Friends 1 e 2" cannot look like
    // season 1 episode 2 on the strength of a trailing "s".
    private static readonly Regex SeasonEpisode = new(
        @"\b(?:s|se|season|series)\s*\.?\s*(?<s>\d{1,3})\s*[._\-]?\s*(?:e|ep|eps|episode|episodes|pt|part)\s*\.?\s*(?<e>\d{1,3})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 1x02 / 01x02
    private static readonly Regex XFormat = new(
        @"(?<![a-zA-Z0-9])(?<s>\d{1,2})x(?<e>\d{1,3})(?![a-zA-Z0-9])",
        RegexOptions.Compiled);

    // "Season 1", "Series 1"
    private static readonly Regex SeasonWord = new(
        @"\b(?:season|series)\s*\d{1,2}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // A bare 3-digit number: "123" => season 1, episode 23 (a common compact scheme).
    private static readonly Regex ThreeDigit = new(
        @"(?<![0-9])(?<s>[1-9])(?<e>[0-9]{2})(?![0-9])",
        RegexOptions.Compiled);

    // 3-digit numbers that are really resolutions, not S/E codes.
    private static readonly HashSet<int> ResolutionNumbers = new() { 240, 360, 480, 540, 576, 720 };

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

        // Start from scratch: classifying an existing entry again (a catalogue refresh)
        // must not leave values behind that the current rules no longer produce.
        file.Year = null;
        file.Season = null;
        file.Episode = null;

        var name = Path.GetFileNameWithoutExtension(file.FileName);

        // Year applies to both movies and TV; capture the first plausible one.
        var yearMatch = Year.Match(name);
        if (yearMatch.Success && int.TryParse(yearMatch.Groups["y"].Value, out var y))
            file.Year = y;

        var se = SeasonEpisode.Match(name);
        var xf = XFormat.Match(name);

        if (file.Kind != MediaKind.Video)
        {
            // Not a video by extension, but an explicit episode code still identifies the
            // content — record it so the category can pick it up.
            if (se.Success)
            {
                file.Season = ParseInt(se.Groups["s"].Value);
                file.Episode = ParseInt(se.Groups["e"].Value);
                file.ParsedTitle = CleanTitle(name, se.Index);
                return;
            }
            if (xf.Success)
            {
                file.Season = ParseInt(xf.Groups["s"].Value);
                file.Episode = ParseInt(xf.Groups["e"].Value);
                file.ParsedTitle = CleanTitle(name, xf.Index);
                return;
            }

            // Audio and friends: just derive a cleaned title.
            file.ParsedTitle = CleanTitle(name, cutAt: -1);
            return;
        }

        int titleCut = -1;

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
        else if (TryThreeDigitEpisode(name, out var tdSeason, out var tdEpisode, out var tdIndex))
        {
            // No explicit markers and no year: a bare 3-digit number like "123" is
            // read as season 1, episode 23.
            file.VideoCategory = VideoCategory.TvShow;
            file.Season = tdSeason;
            file.Episode = tdEpisode;
            titleCut = tdIndex;
        }
        else
        {
            file.VideoCategory = VideoCategory.Unknown;
        }

        // Specials/featurettes keep whatever season/episode was parsed, but are filed as
        // extras so they can travel with the film or show they belong to.
        if (ExtraDetector.Detect(file) is { } extra)
            file.VideoCategory = extra;

        file.ParsedTitle = CleanTitle(name, titleCut);
    }

    private static int? ParseInt(string s) =>
        int.TryParse(s, out var v) ? v : null;

    /// <summary>
    /// Find a bare 3-digit number to read as SxEyy. Rejects resolutions (720, 480, …)
    /// and episode 00. Returns the season, episode and where the match starts.
    /// </summary>
    private static bool TryThreeDigitEpisode(string name, out int season, out int episode, out int index)
    {
        season = episode = 0; index = -1;
        foreach (Match m in ThreeDigit.Matches(name))
        {
            var value = int.Parse(m.Value);
            if (ResolutionNumbers.Contains(value)) continue;
            var e = int.Parse(m.Groups["e"].Value);
            if (e == 0) continue; // "100" -> episode 00 is not meaningful
            season = int.Parse(m.Groups["s"].Value);
            episode = e;
            index = m.Index;
            return true;
        }
        return false;
    }

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
