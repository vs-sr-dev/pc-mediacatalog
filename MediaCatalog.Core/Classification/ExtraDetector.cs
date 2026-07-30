using System.Text.RegularExpressions;
using MediaCatalog.Core.Models;

namespace MediaCatalog.Core.Classification;

/// <summary>
/// Recognises specials, featurettes and other bonus material that belongs to a film or
/// TV show, from the folder it sits in and from the file name (Plex/Kodi style suffixes
/// such as <c>-behindthescenes</c>, or a season-zero episode code).
/// </summary>
public static class ExtraDetector
{
    /// <summary>Folder names that mark everything beneath them as bonus material.</summary>
    private static readonly HashSet<string> ExtraFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "extra", "extras", "special", "specials", "featurette", "featurettes",
        "behind the scenes", "behind-the-scenes", "deleted scenes", "deleted",
        "bonus", "bonus features", "bonus disc", "interviews", "making of",
        "making-of", "outtakes", "bloopers", "shorts", "trailers", "bts"
    };

    // Plex/Kodi extra suffixes: "Film Name-featurette.mkv".
    private static readonly Regex NameSuffix = new(
        @"-(behindthescenes|deleted|deletedscene|featurette|interview|scene|short|" +
        @"trailer|other|blooper|outtake|makingof)s?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // The same words spelled out anywhere in the name.
    private static readonly Regex NameWords = new(
        @"\b(behind[\s._-]?the[\s._-]?scenes|featurette|deleted[\s._-]scenes?|" +
        @"making[\s._-]of|bloopers?|outtakes?|gag[\s._-]reel|bonus[\s._-]features?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // "Season 00" / "S00E01" — the conventional home of TV specials.
    private static readonly Regex SeasonZero = new(
        @"(?<![0-9])(?:[Ss]\s*0+\s*[Ee]\s*\d{1,3}|(?:season|series)\s*0+)(?![0-9])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The extras category for this file, or null when it is normal content. TV vs film
    /// is a first guess from the surrounding folders; <see cref="ExtraLinker"/> corrects
    /// it once the owning film/episode is known.
    /// </summary>
    public static VideoCategory? Detect(MediaFile file)
    {
        var name = Path.GetFileNameWithoutExtension(file.FileName);
        var folders = AncestorNames(file.FullPath).ToList();

        var isExtra =
            file.Season == 0 ||
            SeasonZero.IsMatch(name) ||
            NameSuffix.IsMatch(name) ||
            NameWords.IsMatch(name) ||
            folders.Any(ExtraFolders.Contains) ||
            folders.Any(SeasonZero.IsMatch);

        if (!isExtra) return null;

        // A season/episode code, a "Season NN" folder or an existing TV classification
        // all point at a show; otherwise assume the extra belongs to a film.
        var looksTv =
            file.Season.HasValue ||
            file.VideoCategory == VideoCategory.TvShow ||
            folders.Any(f => Regex.IsMatch(f, @"^(season|series)\s*\d+$", RegexOptions.IgnoreCase));

        return looksTv ? VideoCategory.TvExtra : VideoCategory.MovieExtra;
    }

    /// <summary>Directory names above a file, nearest first.</summary>
    public static IEnumerable<string> AncestorNames(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) yield break;
        var dir = Path.GetDirectoryName(fullPath);
        while (!string.IsNullOrEmpty(dir))
        {
            var name = Path.GetFileName(dir);
            dir = Path.GetDirectoryName(dir);
            if (!string.IsNullOrEmpty(name)) yield return name;
        }
    }

    /// <summary>True if the folder itself is a bonus-material folder (e.g. "Featurettes").</summary>
    public static bool IsExtraFolderName(string folderName) =>
        !string.IsNullOrEmpty(folderName) &&
        (ExtraFolders.Contains(folderName) || SeasonZero.IsMatch(folderName));
}
