using System.Text.RegularExpressions;
using MediaCatalog.Core.Classification;
using MediaCatalog.Core.Models;

namespace MediaCatalog.Core.Consolidation;

/// <summary>
/// Works out where a TV episode or film should live under the consolidation roots.
///
/// TV:    &lt;TvDir&gt;\&lt;bucket&gt;\&lt;show name&gt;\Season NN\
/// Film:  &lt;FilmDir&gt;\&lt;bucket&gt;\&lt;title (year)&gt;\
///
/// where <c>bucket</c> is the first character of the name — a letter A–Z, or <c>#</c>
/// when it starts with a digit (27 buckets total). Season numbers are left-padded to
/// at least two digits (three when the season number itself needs three).
/// </summary>
public static class ConsolidationPlanner
{
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    /// <summary>
    /// The destination *directory* for a file given the effective category and the
    /// configured roots, or null when it doesn't apply (missing root, no title, etc.).
    /// </summary>
    public static string? PlanDirectory(MediaFile file, string category, string tvDir, string filmDir)
    {
        var title = Title(file);
        if (string.IsNullOrWhiteSpace(title)) return null;

        if (category == CategoryResolver.TvShow && !string.IsNullOrWhiteSpace(tvDir))
        {
            var season = file.Season ?? 1;
            return Path.Combine(tvDir, Bucket(title), Sanitize(title), SeasonFolder(season));
        }

        if (category == CategoryResolver.Movie && !string.IsNullOrWhiteSpace(filmDir))
        {
            var folder = file.Year is { } y ? $"{title} ({y})" : title;
            return Path.Combine(filmDir, Bucket(title), Sanitize(folder));
        }

        return null;
    }

    /// <summary>Prefer a TMDb-validated name over the filename-parsed one.</summary>
    private static string Title(MediaFile file) =>
        !string.IsNullOrWhiteSpace(file.TmdbName) ? file.TmdbName : file.ParsedTitle;

    /// <summary>First-letter bucket: A–Z, or '#' for a digit / anything else.</summary>
    public static string Bucket(string name)
    {
        var trimmed = name.TrimStart();
        if (trimmed.Length == 0) return "#";
        var c = trimmed[0];
        if (char.IsLetter(c)) return char.ToUpperInvariant(c).ToString();
        return "#";
    }

    /// <summary>"Season 01" … "Season 09" … "Season 100" (min width two, left-padded).</summary>
    public static string SeasonFolder(int season)
    {
        var number = season < 100 ? season.ToString("D2") : season.ToString();
        return "Season " + number;
    }

    private static string Sanitize(string input)
    {
        var cleaned = new string(input.Select(c => InvalidChars.Contains(c) ? ' ' : c).ToArray());
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        return cleaned.TrimEnd('.', ' ');
    }
}
