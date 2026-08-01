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

    // An episode marker with no season beside it: "E07", "Ep 7", "Episode 12". Only ever
    // consulted when a "Season NN" folder has already supplied the season, which is what
    // keeps "Part 2" in a film title from being read as an episode number.
    private static readonly Regex EpisodeOnly = new(
        @"(?<![a-z0-9])(?:e|ep|episode|part|pt)\s*\.?\s*(?<e>\d{1,3})(?![0-9])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // "Season 1", "Series 1"
    private static readonly Regex SeasonWord = new(
        @"\b(?:season|series)\s*\d{1,2}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // A bare 3-digit number: "123" => season 1, episode 23 (a common compact scheme).
    private static readonly Regex ThreeDigit = new(
        @"(?<![0-9])(?<s>[1-9])(?<e>[0-9]{2})(?![0-9])",
        RegexOptions.Compiled);

    // A bare 4-digit number: "1102" => season 11, episode 02. Read as two-and-two rather
    // than one-and-three, because shows run to eleven seasons far more often than to a
    // hundred-and-two episodes in one.
    private static readonly Regex FourDigit = new(
        @"(?<![0-9])(?<s>[1-9][0-9])(?<e>[0-9]{2})(?![0-9])",
        RegexOptions.Compiled);

    // 3-digit numbers that are really resolutions, not S/E codes.
    private static readonly HashSet<int> ResolutionNumbers = new() { 240, 360, 480, 540, 576, 720 };

    // The same for 4-digit numbers: widths, heights and bitrates, not episodes.
    private static readonly HashSet<int> ResolutionNumbers4 = new()
    {
        1080, 1440, 2160, 4320, 1280, 1920, 2560, 3840, 7680
    };

    // A plausible release year in brackets or standalone: 1900-2099
    private static readonly Regex Year = new(
        @"(?<![0-9])(?<y>(?:19|20)\d{2})(?![0-9])",
        RegexOptions.Compiled);

    // Common release-group / quality noise we strip from titles. Also does duty as the
    // guard against reading a codec or a resolution as a season/episode code: any number
    // inside one of these tokens is about the encoding, not about the programme.
    private static readonly Regex Noise = new(
        @"\b(1080p|720p|2160p|480p|4k|x\.?26[45]|h\.?26[45]|avc|hevc|vp9|av1|xvid|divx|" +
        @"10bit|8bit|\d{3,4}x\d{3,4}|" +
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

        // Where the encoding details sit in the name. Numbers inside them describe the
        // file, not the programme, so nothing below is allowed to read them: "x264" is a
        // codec rather than season 2 episode 64, and the 1920 in "1920x1080" is not a year.
        var noise = NoiseSpans(name);

        // Year applies to both movies and TV; capture the first plausible one.
        var yearMatch = FirstOutsideNoise(Year, name, noise);
        if (yearMatch != null && int.TryParse(yearMatch.Groups["y"].Value, out var y))
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

        // What the surrounding folders say. A well-filed library carries the season in a
        // "Season 04" folder and the show name in the folder above it, which is often
        // everything the file name itself leaves out.
        var pathSeason = PathMetadata.SeasonFromPath(file.FullPath);

        // The episode when the name gives only that and no season: a bare "1" or "12", or
        // an "E07"-style marker. Longer runs of digits are compact season/episode codes
        // and are left to TryCompactEpisode, which reads the season out of them.
        var pathEpisode = pathSeason is null
            ? null
            : PathMetadata.EpisodeFromBareName(name) ?? EpisodeOnlyNumber(name);

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
        else if (pathSeason is { } folderSeason && pathEpisode is { } folderEpisode)
        {
            // "…\Season 04\1.avi": the folder is the season, the name is the episode.
            // Checked ahead of the year rule, since a numeric name under a season folder
            // is an episode even when the number happens to look like a year.
            file.VideoCategory = VideoCategory.TvShow;
            file.Season = folderSeason;
            file.Episode = folderEpisode;
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
            titleCut = yearMatch?.Index ?? -1;
        }
        else if (TryCompactEpisode(name, noise, out var cSeason, out var cEpisode, out var cIndex))
        {
            // No explicit markers and no year: a bare 3- or 4-digit number like "123" or
            // "1102" is read as season 1 episode 23, or season 11 episode 02.
            file.VideoCategory = VideoCategory.TvShow;
            file.Season = cSeason;
            file.Episode = cEpisode;
            titleCut = cIndex;
        }
        else
        {
            file.VideoCategory = VideoCategory.Unknown;
        }

        // A season folder fills a gap the name left — "E05.mkv" in "Season 03" — but never
        // overrules it. Whatever the name says about the season stands, so "1102" in a
        // "Season 04" folder is S11E02: the file was named deliberately, the folder it
        // happens to be sitting in may just be where someone dropped it.
        if (pathSeason is { } seasonFolder && file.Episode.HasValue && file.Season is null)
        {
            file.Season = seasonFolder;
            file.VideoCategory = VideoCategory.TvShow;
        }

        // Specials/featurettes keep whatever season/episode was parsed, but are filed as
        // extras so they can travel with the film or show they belong to.
        if (ExtraDetector.Detect(file) is { } extra)
            file.VideoCategory = extra;

        file.ParsedTitle = CleanTitle(name, titleCut);

        // A name that was all episode code leaves nothing to call the show by, so fall
        // back to the folder it lives in — "King Of The Hill" for the example above.
        if (!HasUsefulTitle(file.ParsedTitle) &&
            PathMetadata.TitleFromPath(file.FullPath) is { } fromPath)
            file.ParsedTitle = fromPath;
    }

    /// <summary>
    /// True when a parsed title says something — anything that is not empty and not just
    /// the episode number we already extracted.
    /// </summary>
    private static bool HasUsefulTitle(string title) =>
        !string.IsNullOrWhiteSpace(title) &&
        !title.All(c => char.IsDigit(c) || char.IsWhiteSpace(c));

    private static int? ParseInt(string s) =>
        int.TryParse(s, out var v) ? v : null;

    /// <summary>
    /// The number in a lone episode marker — "E07", "Ep 7", "Episode 12" — or null.
    /// Only meaningful once a season folder has supplied the season.
    /// </summary>
    private static int? EpisodeOnlyNumber(string name) =>
        EpisodeOnly.Match(name) is { Success: true } m ? ParseInt(m.Groups["e"].Value) : null;

    /// <summary>
    /// Where the encoding/quality tokens sit in a name, as half-open character ranges.
    /// </summary>
    private static List<(int Start, int End)> NoiseSpans(string name) =>
        Noise.Matches(name).Select(m => (m.Index, m.Index + m.Length)).ToList();

    /// <summary>The first match of <paramref name="pattern"/> that is not inside a noise token.</summary>
    private static Match? FirstOutsideNoise(
        Regex pattern, string name, IReadOnlyList<(int Start, int End)> noise)
    {
        foreach (Match m in pattern.Matches(name))
            if (!Overlaps(m, noise)) return m;
        return null;
    }

    /// <summary>True when a match falls inside a codec/resolution token.</summary>
    private static bool Overlaps(Match m, IReadOnlyList<(int Start, int End)> noise)
    {
        foreach (var (start, end) in noise)
            if (m.Index < end && m.Index + m.Length > start) return true;
        return false;
    }

    /// <summary>
    /// Find a bare 3- or 4-digit number to read as a compact season/episode code: "123"
    /// is S01E23, "1102" is S11E02. Rejects resolutions (720, 1080, …), episode 00, and
    /// anything inside an encoding token — the 264 in "x264" is a codec, not S02E64.
    /// The 4-digit form is tried first, so "1102" is not read as the "102" inside it.
    /// </summary>
    private static bool TryCompactEpisode(
        string name, IReadOnlyList<(int Start, int End)> noise,
        out int season, out int episode, out int index)
    {
        if (TryDigits(FourDigit, ResolutionNumbers4, name, noise, out season, out episode, out index))
            return true;
        return TryDigits(ThreeDigit, ResolutionNumbers, name, noise, out season, out episode, out index);
    }

    private static bool TryDigits(
        Regex pattern, HashSet<int> reject, string name,
        IReadOnlyList<(int Start, int End)> noise,
        out int season, out int episode, out int index)
    {
        season = episode = 0; index = -1;
        foreach (Match m in pattern.Matches(name))
        {
            if (Overlaps(m, noise)) continue;
            var value = int.Parse(m.Value);
            if (reject.Contains(value)) continue;
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
