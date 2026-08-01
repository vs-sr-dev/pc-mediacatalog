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
    // "Season 04", "Series 2", "S03", "Season_10".
    private static readonly Regex SeasonFolder = new(
        @"^(?:season|series|s)\s*[._\-]?\s*(?<s>\d{1,3})$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // A file name that is nothing but a short number: "1.avi", "01.avi", "12.avi".
    // Capped at two digits on purpose — three or four are a compact season/episode code
    // ("104" is S01E04, "1102" is S11E02) and carry a season of their own, so they are
    // read by MediaClassifier instead of being taken for a bare episode number.
    private static readonly Regex BareNumber = new(
        @"^\d{1,2}$", RegexOptions.Compiled);

    /// <summary>The season number named by the nearest ancestor folder, or null.</summary>
    public static int? SeasonFromPath(string fullPath)
    {
        foreach (var folder in ExtraDetector.AncestorNames(fullPath))
            if (SeasonFolder.Match(folder) is { Success: true } m &&
                int.TryParse(m.Groups["s"].Value, out var season))
                return season;
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
    /// The programme or film name the folders imply: the nearest ancestor that isn't a
    /// season folder, an extras folder or a one-letter A–Z bucket. Null when the path
    /// offers nothing better than a drive root.
    /// </summary>
    public static string? TitleFromPath(string fullPath)
    {
        foreach (var folder in ExtraDetector.AncestorNames(fullPath))
        {
            if (folder.Length <= 1) continue;                     // "K" bucket folder
            if (SeasonFolder.IsMatch(folder)) continue;
            if (ExtraDetector.IsExtraFolderName(folder)) continue;

            var cleaned = Clean(folder);
            if (cleaned.Length > 0) return cleaned;
        }
        return null;
    }

    /// <summary>True when the folder is a "Season NN" / "Series N" folder.</summary>
    public static bool IsSeasonFolder(string folderName) =>
        !string.IsNullOrEmpty(folderName) && SeasonFolder.IsMatch(folderName);

    private static string Clean(string folder)
    {
        var t = folder.Replace('.', ' ').Replace('_', ' ');
        return Regex.Replace(t, @"\s+", " ").Trim();
    }
}
