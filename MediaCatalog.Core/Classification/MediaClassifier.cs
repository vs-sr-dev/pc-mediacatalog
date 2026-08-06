using System.Text.RegularExpressions;
using MediaCatalog.Core.Models;
using MediaCatalog.Core.Naming;
using MediaCatalog.Core.Scanning;
using MediaCatalog.Core.Storage;

namespace MediaCatalog.Core.Classification;

/// <summary>
/// Best-effort classification and metadata extraction from a file name alone
/// (no online lookups). Good enough to separate the common cases; ambiguous
/// files fall through to <see cref="VideoCategory.Unknown"/>.
/// </summary>
public static class MediaClassifier
{
    /// <summary>
    /// The characters that stand between a season and an episode when somebody writes the
    /// two apart: a hyphen, either of the typographic dashes — and the replacement character,
    /// which is what a dash comes back as once a name has been through an encoding that could
    /// not carry it. "Dexter (s8 ? 1)" is not a file anybody meant to create; it is
    /// "Dexter (s8 – 1)" after a round trip, and it still says season 8 episode 1.
    /// </summary>
    private const string Dashes = @"\-–—�";

    // Every way people write a season and episode together:
    //   S01E02, s1e2, S01.E02, S01 E02, "S04 E 01",
    //   "Season 1 Episode 01", "Series 1 Episode 1", "S1 Episode 1", "Season 2 Ep 3",
    //   and the forms that leave the "E" out altogether: "(s8 – 1)", "(s8 – ep 3)".
    // The word forms are matched at a word boundary so "Friends 1 e 2" cannot look like
    // season 1 episode 2 on the strength of a trailing "s".
    //
    // Leaving out the episode marker is only allowed after a dash, and deliberately: "s8 1"
    // is two numbers side by side and could be anything, while "s8 – 1" is somebody writing
    // season 8, episode 1 with a dash where the E would go.
    //
    // A trailing second episode is picked up too: a file holding a double episode writes it
    // "S06E11E12" or "S01E01-E02", and calling that episode 11 alone loses half of what the
    // name says — and makes it look like a duplicate of the real episode 11.
    private static readonly Regex SeasonEpisode = new(
        @"\b(?:s|se|season|series)\s*\.?\s*(?<s>\d{1,3})\s*" +
        $@"(?:[._{Dashes}]?\s*(?:e|ep|eps|episode|episodes|pt|part)|[{Dashes}]\s*(?:e|ep|eps|episode|episodes)?)" +
        @"\s*\.?\s*(?<e>\d{1,3})" +
        @"(?:\s*[._\-]?\s*(?:e|ep|episode)?\s*\.?\s*(?<e2>\d{1,3}))?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // "Home Improvement 5-26 Games Flames And Automobiles": the season and the episode
    // joined by a dash, with nothing at all to mark either of them.
    //
    // On its own a pair of numbers around a dash is a range, a date or a track listing, so
    // this only fires on the shape that means an episode code: a one- or two-digit season, a
    // two-digit episode, and a word on each side of the pair. That last condition is what
    // keeps it off the episode prefix consolidation writes — "11-12 - Name.mkv" opens with
    // its numbering and so has no word in front of it.
    private static readonly Regex DashedSeasonEpisode = new(
        $@"(?<=[^\s\d][ ._])(?<s>\d{{1,2}})[{Dashes}](?<e>\d{{2}})(?=[ ._][^\s\d])",
        RegexOptions.Compiled);

    // The same in words: "Season Three Episode One". Kept apart from the pattern above so
    // the common all-digits form stays cheap and unambiguous.
    private static readonly Regex SeasonEpisodeWords = new(
        $@"\b(?:season|series)\s*[._\-]?\s*(?<s>{NumberWords.NumberPattern})\s*[._\-]?\s*" +
        $@"(?:e|ep|eps|episode|episodes|part|pt)\s*[._\-]?\s*(?<e>{NumberWords.NumberPattern})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 1x02 / 01x02, and the double-episode forms 1x01x02 and 1x01-02.
    private static readonly Regex XFormat = new(
        @"(?<![a-zA-Z0-9])(?<s>\d{1,2})x(?<e>\d{1,3})(?:\s*[-._]?\s*x?(?<e2>\d{1,3}))?(?![a-zA-Z0-9])",
        RegexOptions.Compiled);

    // "The Dead Zone - 04 01 - Broken Circle (2)": the season and the episode written as two
    // plain numbers side by side, with no letter marking either of them.
    //
    // Two bare numbers are far too common to read as an episode code on their own — a year,
    // a track number, a running time — so this only fires on the shape that means it: the
    // pair fenced off by a dash on each side, in the slot between the programme's name and
    // the episode's. Everything looser is left to the compact forms below, which know what
    // they are looking at.
    private static readonly Regex SpacedSeasonEpisode = new(
        @"[-–—]\s*(?<s>\d{1,2})\s*[\s._]\s*(?<e>\d{1,3})(?:\s*[\s._]\s*(?<e2>\d{1,3}))?\s*[-–—]",
        RegexOptions.Compiled);

    // An episode marker with no season beside it: "E07", "Ep 7", "Episode 12", "E07E08".
    // Only ever consulted when a "Season NN" folder has already supplied the season, which
    // is what keeps "Part 2" in a film title from being read as an episode number.
    private static readonly Regex EpisodeOnly = new(
        @"(?<![a-z0-9])(?:e|ep|episode|part|pt)\s*\.?\s*(?<e>\d{1,3})(?:\s*[-._]?\s*(?:e|ep|episode)?\s*\.?\s*(?<e2>\d{1,3}))?(?![0-9])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The furthest apart two episode numbers in one file can plausibly be. A double —
    /// occasionally a triple — episode is what this is for; "S01E01" followed by a number
    /// forty higher is two unrelated numbers that happened to end up side by side.
    /// </summary>
    private const int MaxEpisodeRun = 8;

    // "Season 1", "Series 1", "Season Three" — a season with no episode beside it.
    private static readonly Regex SeasonWord = new(
        $@"\b(?:season|series)\s*[._\-]?\s*(?<s>{NumberWords.NumberPattern})\b",
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

    /// <param name="settings">
    /// The user's preferences, for the handful of choices classification exposes (title
    /// capitalisation). Null keeps the defaults, which is what a caller with no settings
    /// to hand would want anyway.
    /// </param>
    public static void Classify(MediaFile file, AppSettings? settings = null)
    {
        // Numbering somebody typed in is not a guess to be made again. Everything below
        // re-derives what the name says; this puts back what the user said, whatever that
        // turns out to be — a rescan or a catalogue refresh must not undo a correction.
        var typed = file.NumberingManuallySet
            ? (file.Season, file.Episode, file.EpisodeEnd)
            : ((int?, int?, int?)?)null;

        Derive(file, settings);

        if (typed is not { } numbering) return;
        (file.Season, file.Episode, file.EpisodeEnd) = numbering;
    }

    /// <summary>Everything the file's name and folders imply, read from scratch.</summary>
    private static void Derive(MediaFile file, AppSettings? settings)
    {
        file.Kind = MediaExtensions.Classify(file.Extension);

        // Start from scratch: classifying an existing entry again (a catalogue refresh)
        // must not leave values behind that the current rules no longer produce.
        file.Year = null;
        file.Season = null;
        file.Episode = null;
        file.EpisodeEnd = null;

        var capitalise = settings?.CapitaliseTitles ?? true;
        var name = Path.GetFileNameWithoutExtension(file.FileName);

        // Where the encoding details sit in the name. Numbers inside them describe the
        // file, not the programme, so nothing below is allowed to read them: "x264" is a
        // codec rather than season 2 episode 64, and the 1920 in "1920x1080" is not a year.
        var noise = NoiseSpans(name);

        // Year applies to both movies and TV; capture the first plausible one.
        var yearMatch = FirstPlausibleYear(name, noise);
        if (yearMatch != null && int.TryParse(yearMatch.Groups["y"].Value, out var y))
            file.Year = y;

        var se = SeasonEpisode.Match(name);
        if (!se.Success) se = SeasonEpisodeWords.Match(name);
        var xf = XFormat.Match(name);
        var spaced = FirstOutsideNoise(SpacedSeasonEpisode, name, noise)
                     ?? FirstOutsideNoise(DashedSeasonEpisode, name, noise);

        if (file.Kind != MediaKind.Video)
        {
            // Not a video by extension, but an explicit episode code still identifies the
            // content — record it so the category can pick it up.
            if (se.Success)
            {
                file.Season = ParseNumber(se.Groups["s"].Value);
                file.Episode = ParseNumber(se.Groups["e"].Value);
                file.EpisodeEnd = ParseEpisodeEnd(se, file.Episode);
                file.ParsedTitle = CleanTitle(name, se.Index, capitalise);
                return;
            }
            if (xf.Success)
            {
                file.Season = ParseNumber(xf.Groups["s"].Value);
                file.Episode = ParseNumber(xf.Groups["e"].Value);
                file.EpisodeEnd = ParseEpisodeEnd(xf, file.Episode);
                file.ParsedTitle = CleanTitle(name, xf.Index, capitalise);
                return;
            }

            // Audio and friends: just derive a cleaned title.
            file.ParsedTitle = CleanTitle(name, cutAt: -1, capitalise);
            return;
        }

        int titleCut = -1;

        // What the surrounding folders say. A well-filed library carries the season in a
        // "Season 04" folder — or in a "Yes Minister, Season Three" one — and the show name
        // in that same folder or the one above it, which is often everything the file name
        // itself leaves out.
        var pathSeason = PathMetadata.SeasonFromPath(file.FullPath);

        // The episode when the name gives only that and no season: a bare "1" or "12", a
        // leading "01. Equal Opportunities", or an "E07"-style marker. Longer runs of digits
        // are compact season/episode codes and are left to TryCompactEpisode, which reads
        // the season out of them.
        var leadingEpisode = pathSeason is null
            ? null
            : PathMetadata.EpisodeFromLeadingNumber(name);
        var marker = pathSeason is null ? null : EpisodeOnly.Match(name);
        var markerEpisode = marker is { Success: true } ? ParseNumber(marker.Groups["e"].Value) : null;
        var pathEpisode = pathSeason is null
            ? null
            : PathMetadata.EpisodeFromBareName(name) ?? leadingEpisode ?? markerEpisode;

        // Set when the numbering came from the folder rather than the name. A name that
        // opens with its episode number goes on to give the *episode's* title, not the
        // programme's — "01. Equal Opportunities" is an episode of something the folders
        // name — so the title is taken from the path below.
        var titleIsEpisodeName = false;

        if (se.Success)
        {
            file.VideoCategory = VideoCategory.TvShow;
            file.Season = ParseNumber(se.Groups["s"].Value);
            file.Episode = ParseNumber(se.Groups["e"].Value);
            file.EpisodeEnd = ParseEpisodeEnd(se, file.Episode);
            titleCut = se.Index;
        }
        else if (xf.Success)
        {
            file.VideoCategory = VideoCategory.TvShow;
            file.Season = ParseNumber(xf.Groups["s"].Value);
            file.Episode = ParseNumber(xf.Groups["e"].Value);
            file.EpisodeEnd = ParseEpisodeEnd(xf, file.Episode);
            titleCut = xf.Index;
        }
        else if (spaced is { Success: true })
        {
            // "Show - 04 01 - Episode name", or "Show 5-26 Episode name".
            file.VideoCategory = VideoCategory.TvShow;
            file.Season = ParseNumber(spaced.Groups["s"].Value);
            file.Episode = ParseNumber(spaced.Groups["e"].Value);
            file.EpisodeEnd = ParseEpisodeEnd(spaced, file.Episode);
            titleCut = spaced.Index;
        }
        else if (pathSeason is { } folderSeason && pathEpisode is { } folderEpisode)
        {
            // "…\Season 04\1.avi": the folder is the season, the name is the episode.
            // Checked ahead of the year rule, since a numeric name under a season folder
            // is an episode even when the number happens to look like a year.
            file.VideoCategory = VideoCategory.TvShow;
            file.Season = folderSeason;
            file.Episode = folderEpisode;
            // Only the "E07E08" form carries a second episode; a bare or leading number
            // says one episode and nothing more.
            if (marker is { Success: true } && markerEpisode == folderEpisode)
                file.EpisodeEnd = ParseEpisodeEnd(marker, folderEpisode);
            titleIsEpisodeName = leadingEpisode == folderEpisode;
        }
        else if (SeasonWord.Match(name) is { Success: true } sw)
        {
            file.VideoCategory = VideoCategory.TvShow;
            file.Season = NumberWords.Parse(sw.Groups["s"].Value);
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

        file.ParsedTitle = CleanTitle(name, titleCut, capitalise);

        // A name that was all episode code — or all episode *name* — leaves nothing to call
        // the show by, so fall back to the folder it lives in.
        if ((titleIsEpisodeName || !HasUsefulTitle(file.ParsedTitle)) &&
            PathMetadata.TitleFromPath(file.FullPath) is { } fromPath)
            file.ParsedTitle = capitalise ? TitleCase.Apply(fromPath) : fromPath;

        // A season/episode on something that is not a programme was read out of a number
        // that meant something else, so it goes.
        MetadataNormaliser.StripNonTvNumbering(file, CategoryResolver.Auto(file));
    }

    /// <summary>
    /// True when a parsed title says something — anything that is not empty and not just
    /// the episode number we already extracted.
    /// </summary>
    private static bool HasUsefulTitle(string title) =>
        !string.IsNullOrWhiteSpace(title) &&
        !title.All(c => char.IsDigit(c) || char.IsWhiteSpace(c));

    /// <summary>A season or episode number written in digits or in words.</summary>
    private static int? ParseNumber(string s) => NumberWords.Parse(s);

    /// <summary>
    /// The second episode number of a double episode — the 12 in "S06E11E12" — or null when
    /// the file holds one episode.
    ///
    /// It has to follow the first and stay close to it: a number that is smaller, equal, or
    /// several episodes away is not the other half of a double, it is a number that happened
    /// to be sitting next to an episode code.
    /// </summary>
    private static int? ParseEpisodeEnd(Match match, int? first)
    {
        if (first is not { } start) return null;
        var group = match.Groups["e2"];
        if (!group.Success) return null;

        var last = ParseNumber(group.Value);
        return last is { } end && end > start && end - start <= MaxEpisodeRun ? end : null;
    }

    /// <summary>
    /// Where the encoding/quality tokens sit in a name, as half-open character ranges.
    /// </summary>
    private static List<(int Start, int End)> NoiseSpans(string name) =>
        Noise.Matches(name).Select(m => (m.Index, m.Index + m.Length)).ToList();

    /// <summary>
    /// The year the file is actually from. Release names carry more than one four-digit
    /// number often enough to matter — "Blade.Runner.2049.2017.1080p" is a 2017 film about
    /// the year 2049 — and the giveaway is that a year in the future has not happened yet.
    /// So candidates that cannot be release years are passed over, and only if every one of
    /// them is impossible is the first taken anyway, on the grounds that a wrong year still
    /// beats no year at all.
    /// </summary>
    private static Match? FirstPlausibleYear(string name, IReadOnlyList<(int Start, int End)> noise)
    {
        // One year's grace: a film announced for next year is dated next year.
        var latest = DateTime.Now.Year + 1;

        Match? first = null;
        foreach (Match m in Year.Matches(name))
        {
            if (Overlaps(m, noise)) continue;
            first ??= m;
            if (int.TryParse(m.Groups["y"].Value, out var value) && value <= latest) return m;
        }
        return first;
    }

    /// <summary>
    /// The first match that is not sitting inside a codec or resolution token, or null when
    /// every one of them is. The numbers in "1920x1080" describe the file rather than the
    /// programme, and nothing here is allowed to read them as an episode code.
    /// </summary>
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
    /// season/episode/year marker and stripping separators and release noise. Every word
    /// then gets its initial capital, so a title read out of a lower-case release name
    /// reads like a title.
    /// </summary>
    private static string CleanTitle(string name, int cutAt, bool capitalise)
    {
        var head = cutAt > 0 ? name[..cutAt] : name;
        head = head.Replace('.', ' ').Replace('_', ' ').Replace('-', ' ');
        head = Noise.Replace(head, " ");
        head = Regex.Replace(head, @"\s+", " ").Trim(' ', '(', ')', '[', ']');
        return capitalise ? TitleCase.Apply(head) : head;
    }
}
