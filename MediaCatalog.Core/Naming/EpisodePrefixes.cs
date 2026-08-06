using System.Text.RegularExpressions;

namespace MediaCatalog.Core.Naming;

/// <summary>
/// The episode number a consolidated file carries at the front of its name, so a season
/// folder sorts into broadcast order: "01 - Equal Opportunities.mkv".
///
/// Adding one is easy. The whole of the difficulty is in not adding it twice — to a file
/// that arrived already numbered, or to one this program numbered on a previous run — and
/// in undoing the ones that were added twice before anybody noticed.
/// </summary>
public static class EpisodePrefixes
{
    /// <summary>
    /// The number a name opens with, if it opens with one: the 01 in "01 - Name", the 11
    /// and 12 in "11-12 - Name". Anything longer than three digits is a year or a title and
    /// is not a match.
    /// </summary>
    private static readonly Regex Leading = new(
        @"^\s*(?<e>\d{1,3})(?:\s*[-–—]\s*(?<e2>\d{1,3}))?(?![0-9])",
        RegexOptions.Compiled);

    /// <summary>
    /// The same number written twice at the front of a name — "01 - 01 - Name" — with the
    /// second copy captured, so the first can simply be cut off.
    ///
    /// Both copies have to be the same number. "05 - 01 - Name" is a season and an episode,
    /// or somebody's own way of naming things, and is none of our business.
    /// </summary>
    private static readonly Regex Doubled = new(
        @"^\s*(?<p>\d{1,3}(?:\s*[-–—]\s*\d{1,3})?)\s*[-–—]\s+(?<keep>\k<p>\s*[-–—]\s+)",
        RegexOptions.Compiled);

    /// <summary>
    /// True when <paramref name="fileName"/> already opens with <paramref name="episode"/>,
    /// however it is written — "01 - Name", "1. Name", "01 Name", "11-12 - Name".
    ///
    /// This is what the request comes down to: a file that already starts with 01 does not
    /// want another 01 in front of it, whether this program put the first one there or the
    /// file arrived that way.
    /// </summary>
    public static bool StartsWithEpisode(string fileName, int episode)
    {
        var match = Leading.Match(Path.GetFileNameWithoutExtension(fileName ?? string.Empty));
        return match.Success &&
               int.TryParse(match.Groups["e"].Value, out var first) &&
               first == episode;
    }

    /// <summary>
    /// <paramref name="fileName"/> with any repeated leading episode number taken off:
    /// "01 - 01 - Name.mkv" becomes "01 - Name.mkv". Names that were only numbered once come
    /// back exactly as they went in, so this is safe to run over a whole folder.
    ///
    /// The loop is for the file that has been through it three times.
    /// </summary>
    public static string Collapse(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return fileName ?? string.Empty;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        for (var guard = 0; guard < 8; guard++)
        {
            var match = Doubled.Match(stem);
            if (!match.Success) break;
            stem = stem[match.Groups["keep"].Index..];
        }

        return stem + extension;
    }

    /// <summary>True when a name carries the same episode number more than once at the front.</summary>
    public static bool IsDoubled(string fileName) =>
        !string.IsNullOrEmpty(fileName) &&
        Doubled.IsMatch(Path.GetFileNameWithoutExtension(fileName));
}
