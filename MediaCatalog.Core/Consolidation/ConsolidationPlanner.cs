using System.Text.RegularExpressions;
using MediaCatalog.Core.Classification;
using MediaCatalog.Core.Models;
using MediaCatalog.Core.Storage;

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
    /// configured folders, or null when it doesn't apply (no target, no title, etc.).
    /// If an existing show folder matches the title it is reused (Burn Notice folder that
    /// already exists is preferred over creating a near-duplicate).
    /// </summary>
    public static string? PlanDirectory(MediaFile file, string category, AppSettings settings)
    {
        var dir = settings.ConsolidationDirFor(category);
        if (string.IsNullOrWhiteSpace(dir)) return null;

        var title = Title(file);

        if (category == CategoryResolver.TvShow)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;
            var showFolder = FindExistingShowFolder(dir, title) ?? ShowFolder(dir, title);
            return Path.Combine(showFolder, SeasonFolder(file.Season ?? 1));
        }

        if (category == CategoryResolver.Movie)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;
            var folder = file.Year is { } y ? $"{title} ({y})" : title;
            return Path.Combine(dir, Bucket(title), Sanitize(folder));
        }

        // Custom category: files go straight into its consolidation folder.
        return dir;
    }

    /// <summary>The canonical show folder: &lt;TvDir&gt;\&lt;bucket&gt;\&lt;Show&gt;.</summary>
    public static string ShowFolder(string tvDir, string title) =>
        Path.Combine(tvDir, Bucket(title), Sanitize(title));

    /// <summary>
    /// Look for an existing folder matching the show title (exact path first, then any
    /// case-insensitive match inside the bucket), so files join an existing library
    /// folder instead of creating a slightly different one. Returns null if none exists.
    /// </summary>
    public static string? FindExistingShowFolder(string tvDir, string title)
    {
        var clean = Sanitize(title);
        var bucketDir = Path.Combine(tvDir, Bucket(title));

        var exact = Path.Combine(bucketDir, clean);
        if (Directory.Exists(exact)) return exact;

        try
        {
            if (Directory.Exists(bucketDir))
                foreach (var d in Directory.GetDirectories(bucketDir))
                    if (string.Equals(Path.GetFileName(d), clean, StringComparison.OrdinalIgnoreCase))
                        return d;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

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
