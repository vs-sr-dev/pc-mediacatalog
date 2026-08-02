using System.Text.RegularExpressions;

namespace MediaCatalog.Core.Classification;

/// <summary>
/// Reads what the folders around a file say about it. A well-filed library already
/// carries everything needed — <c>T:\TV\K\King Of The Hill\Season 04\1.avi</c> is
/// "King Of The Hill", season 4, episode 1 — even though the file name alone says
/// almost nothing.
/// </summary>
public static class PathMetadata
{
    // "Season 04", "Series 2", "S03", "Season_10", "Season Three", "Series twenty one".
    // The number may be digits or words: a folder that says the season in words carries
    // exactly as much as one that says it in digits.
    private static readonly Regex SeasonFolder = new(
        $@"^(?:season|series|s)\s*[._\-]?\s*(?<s>{NumberWords.NumberPattern})$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // A folder that names the programme *and* the season together, which is how a lot of
    // hand-filed libraries are laid out: "Yes Minister, Season Three", "Blackadder - S02".
    // The show's name is whatever comes before the marker.
    private static readonly Regex SeasonSuffix = new(
        $@"[\s,;:\-–—_]+(?:season|series)\s*[._\-]?\s*(?<s>{NumberWords.NumberPattern})\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // A file name that is nothing but a short number: "1.avi", "01.avi", "12.avi".
    // Capped at two digits on purpose — three or four are a compact season/episode code
    // ("104" is S01E04, "1102" is S11E02) and carry a season of their own, so they are
    // read by MediaClassifier instead of being taken for a bare episode number.
    private static readonly Regex BareNumber = new(
        @"^\d{1,2}$", RegexOptions.Compiled);

    // A name that opens with its episode number and then gives the episode's own title:
    // "01. Equal Opportunities", "03 - The Grand Design", "12_Party Games". The separator
    // is required, so "1917" and "2001 A Space Odyssey" are not read as episodes.
    private static readonly Regex LeadingEpisode = new(
        @"^(?<e>\d{1,3})\s*[.\-–—_)]\s*\S", RegexOptions.Compiled);

    /// <summary>The season number named by the nearest ancestor folder, or null.</summary>
    public static int? SeasonFromPath(string fullPath)
    {
        foreach (var folder in ExtraDetector.AncestorNames(fullPath))
            if (SeasonOf(folder) is { } season)
                return season;
        return null;
    }

    /// <summary>
    /// The season a folder name states, whether it is the whole name ("Season 04") or the
    /// tail of one that also names the show ("Yes Minister, Season Three"). Null when the
    /// folder says nothing about a season.
    /// </summary>
    public static int? SeasonOf(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName)) return null;
        var name = folderName.Trim();

        if (SeasonFolder.Match(name) is { Success: true } whole)
            return NumberWords.Parse(whole.Groups["s"].Value);
        if (SeasonSuffix.Match(name) is { Success: true } tail)
            return NumberWords.Parse(tail.Groups["s"].Value);
        return null;
    }

    /// <summary>
    /// The episode a bare-numbered file name stands for — "1.avi" is episode 1 — for the
    /// case where the season comes from the folder instead. Null for anything longer than
    /// two digits, which is a compact code carrying its own season.
    /// </summary>
    public static int? EpisodeFromBareName(string nameWithoutExtension)
    {
        var name = nameWithoutExtension.Trim();
        if (!BareNumber.IsMatch(name)) return null;
        return int.TryParse(name, out var value) && value > 0 ? value : null;
    }

    /// <summary>
    /// The episode number a name opens with before naming the episode itself — the 1 in
    /// "01. Equal Opportunities". Null when the name does not start that way.
    ///
    /// What follows the number is the episode's own title, not the programme's, so a
    /// caller that uses this should take the show's name from the folders instead.
    /// </summary>
    public static int? EpisodeFromLeadingNumber(string nameWithoutExtension)
    {
        var match = LeadingEpisode.Match(nameWithoutExtension.Trim());
        if (!match.Success) return null;
        return int.TryParse(match.Groups["e"].Value, out var value) && value > 0 ? value : null;
    }

    /// <summary>
    /// The programme or film name the folders imply: the nearest ancestor that isn't a
    /// season folder, an extras folder or a one-letter A–Z bucket. A folder that names the
    /// show and its season together gives up the show's half — "Yes Minister, Season Three"
    /// is *Yes Minister*. Null when the path offers nothing better than a drive root.
    /// </summary>
    public static string? TitleFromPath(string fullPath)
    {
        foreach (var folder in ExtraDetector.AncestorNames(fullPath))
        {
            if (folder.Length <= 1) continue;                     // "K" bucket folder
            if (ExtraDetector.IsExtraFolderName(folder)) continue;

            // "Season 04" on its own says nothing about the programme; "Yes Minister,
            // Season Three" says a great deal, once the season is taken off the end.
            var name = StripSeasonSuffix(folder);
            if (name.Length == 0) continue;

            var cleaned = Clean(name);
            if (cleaned.Length > 0) return cleaned;
        }
        return null;
    }

    /// <summary>True when the folder is a "Season NN" / "Series N" / "Season Three" folder.</summary>
    public static bool IsSeasonFolder(string folderName) =>
        !string.IsNullOrEmpty(folderName) && SeasonFolder.IsMatch(folderName.Trim());

    /// <summary>
    /// The folder name with any trailing season marker removed. Empty when the folder was
    /// nothing but the marker, which is the caller's cue that it names no programme.
    /// </summary>
    public static string StripSeasonSuffix(string folderName)
    {
        var name = (folderName ?? string.Empty).Trim();
        if (name.Length == 0) return string.Empty;
        if (SeasonFolder.IsMatch(name)) return string.Empty;

        var stripped = SeasonSuffix.Replace(name, string.Empty);
        return stripped.Trim().TrimEnd(',', ';', ':', '-', '–', '—', '_', ' ');
    }

    private static string Clean(string folder)
    {
        var t = folder.Replace('.', ' ').Replace('_', ' ');
        return Regex.Replace(t, @"\s+", " ").Trim();
    }
}
