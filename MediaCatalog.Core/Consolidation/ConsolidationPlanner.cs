using System.Text.RegularExpressions;
using MediaCatalog.Core.Classification;
using MediaCatalog.Core.Models;
using MediaCatalog.Core.Naming;
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

        var title = SortName(Title(file), settings);

        // Whether this category's files are sorted A–Z. Films and programmes always were;
        // any category can be now, and either of them can be told to stop.
        var buckets = settings.UseLetterFoldersFor(category);

        if (category is CategoryResolver.TvShow or CategoryResolver.TvExtra)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;
            var showFolder = FindExistingShowFolder(dir, title, buckets)
                             ?? ShowFolder(dir, title, buckets);

            // Extras live beside the show — inside their season when one is known,
            // otherwise in a show-level "Extras" folder.
            if (category == CategoryResolver.TvExtra)
                return file.Season is > 0
                    ? Path.Combine(showFolder, SeasonFolder(file.Season.Value), ExtrasFolder)
                    : Path.Combine(showFolder, ExtrasFolder);

            return Path.Combine(showFolder, SeasonFolder(file.Season ?? 1));
        }

        if (category is CategoryResolver.Movie or CategoryResolver.MovieExtra)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;
            var folder = file.Year is { } y ? $"{title} ({y})" : title;
            var movieFolder = Path.Combine(Under(dir, title, buckets), Sanitize(folder));
            return category == CategoryResolver.MovieExtra
                ? Path.Combine(movieFolder, ExtrasFolder)
                : movieFolder;
        }

        // Any other category: straight into its consolidation folder, unless the user has
        // asked for the A–Z folders films and programmes have always had. Sorted on the
        // title when there is one and on the file's own name when there is not, since a
        // category with no titles is exactly the sort that fills one folder to bursting.
        var sortOn = string.IsNullOrWhiteSpace(title)
            ? Path.GetFileNameWithoutExtension(file.FileName)
            : title;
        return Under(dir, sortOn, buckets);
    }

    /// <summary>
    /// <paramref name="dir"/>, or its first-letter subfolder when the category is sorted
    /// that way.
    /// </summary>
    private static string Under(string dir, string name, bool buckets) =>
        buckets ? Path.Combine(dir, Bucket(name)) : dir;

    /// <summary>Where specials and featurettes are filed inside a show/film folder.</summary>
    public const string ExtrasFolder = "Extras";

    /// <summary>
    /// True when the file sits somewhere under one of the configured consolidation
    /// folders. That it is in the library at all — not that it is in the right place in it.
    /// </summary>
    public static bool IsUnderConsolidationRoot(MediaFile file, AppSettings settings)
    {
        if (string.IsNullOrEmpty(file.FullPath)) return false;

        foreach (var folder in settings.CategoryFolders.Select(c => c.Folder)
                     .Concat(new[] { settings.TvConsolidationDir, settings.FilmConsolidationDir }))
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            var root = folder.TrimEnd('\\', '/');
            if (file.FullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                file.FullPath.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>True when the file is already at the exact path the layout dictates.</summary>
    public static bool IsAtPlannedPath(MediaFile file, string category, AppSettings settings) =>
        PlanPath(file, category, settings) is { } planned && PathsEqual(planned, file.FullPath);

    /// <summary>
    /// True when the file has been filed correctly — it is in the library, at the place
    /// its category, title, year and numbering say it belongs.
    ///
    /// The distinction matters after a title is corrected: the file is still under the
    /// consolidation root, but under the old title's folder, so it is *not* filed and
    /// consolidating it again should move it rather than report it as already done.
    /// </summary>
    public static bool IsCorrectlyFiled(MediaFile file, string category, AppSettings settings)
    {
        if (!IsUnderConsolidationRoot(file, settings)) return false;

        // Nothing to plan — no title yet, or a category filed straight into its folder —
        // means being under the root is the most that can be said about it.
        var planned = PlanPath(file, category, settings);
        if (planned == null || PathsEqual(planned, file.FullPath)) return true;

        return IsExtraAlreadyBeside(file, category, planned);
    }

    /// <summary>
    /// True when a special or featurette is already sitting in an Extras folder belonging to
    /// the show or film it is an extra of.
    ///
    /// Nothing about a featurette says which season it belongs to, so the layout offers it two
    /// homes: the show's own Extras folder, and the Extras folder inside a season. Both are
    /// right, and a file in either of them is filed. Insisting on the exact one the plan
    /// happens to name meant an extra the user had put in the season's folder was reported as
    /// unfiled for ever, and consolidating it shuffled it from one correct place to another.
    /// </summary>
    private static bool IsExtraAlreadyBeside(MediaFile file, string category, string planned)
    {
        if (!CategoryResolver.IsExtra(category)) return false;

        var here = Path.GetDirectoryName(file.FullPath) ?? string.Empty;
        var there = Path.GetDirectoryName(planned) ?? string.Empty;
        if (here.Length == 0 || there.Length == 0) return false;

        if (!string.Equals(Path.GetFileName(here), ExtrasFolder, StringComparison.OrdinalIgnoreCase))
            return false;

        // Same name too: a featurette filed under the right show but called something else
        // still wants renaming, and that is a different question from where it lives.
        if (!string.Equals(Path.GetFileName(file.FullPath), Path.GetFileName(planned),
                StringComparison.OrdinalIgnoreCase))
            return false;

        return PathsEqual(OwnerFolderOf(here), OwnerFolderOf(there));
    }

    /// <summary>
    /// The show or film folder an Extras folder hangs off: one level up, or two when the
    /// Extras folder is inside a season.
    /// </summary>
    private static string OwnerFolderOf(string extrasFolder)
    {
        var parent = Path.GetDirectoryName(extrasFolder) ?? extrasFolder;
        return Classification.PathMetadata.IsSeasonFolder(Path.GetFileName(parent))
            ? Path.GetDirectoryName(parent) ?? parent
            : parent;
    }

    /// <summary>Same file, whatever the two paths look like as text.</summary>
    public static bool PathsEqual(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    /// <summary>
    /// The file name to use at the destination.
    ///
    /// A name pattern set for the category decides it outright — see
    /// <see cref="ConsolidationNaming"/>. Failing that, episodes are prefixed with their
    /// episode number ("01 - Name.mkv", "11-12 - Name.mkv" for a double) so a season folder
    /// sorts into broadcast order, and everything else keeps the name it has. Re-consolidating
    /// an already-prefixed file is a no-op either way.
    /// </summary>
    public static string PlanFileName(MediaFile file, string category, AppSettings? settings = null)
    {
        if (settings != null &&
            ConsolidationNaming.Apply(file, settings.NameTemplateFor(category)) is { Length: > 0 } patterned)
            // A pattern that opens with {episode} and goes on to {name} numbers a file that
            // its name may already number. Written once is what was asked for.
            return EpisodePrefixes.Collapse(patterned);

        if (category != CategoryResolver.TvShow || file.Episode is not { } episode || episode < 0)
            return file.FileName;

        // Already numbered — by an earlier run, or by whoever named it in the first place.
        // "01 - Name.mkv" does not want to become "01 - 01 - Name.mkv".
        if (EpisodePrefix.IsMatch(file.FileName) ||
            EpisodePrefixes.StartsWithEpisode(file.FileName, episode))
            return file.FileName;

        var prefix = Pad(episode);
        if (file.EpisodeEnd is { } last && last > episode) prefix += "-" + Pad(last);
        return $"{prefix} - {file.FileName}";

        static string Pad(int n) => n < 100 ? n.ToString("D2") : n.ToString();
    }

    /// <summary>The full destination path (directory + planned file name), or null.</summary>
    public static string? PlanPath(MediaFile file, string category, AppSettings settings)
    {
        var dir = PlanDirectory(file, category, settings);
        return dir == null ? null : Path.Combine(dir, PlanFileName(file, category, settings));
    }

    // "01 - Name.mkv" / "101 - Name.mkv" / "11-12 - Name.mkv": already numbered for sorting.
    private static readonly Regex EpisodePrefix = new(@"^\d{2,3}(-\d{2,3})?\s*-\s+", RegexOptions.Compiled);

    /// <summary>The canonical show folder: &lt;TvDir&gt;\&lt;bucket&gt;\&lt;Show&gt;.</summary>
    public static string ShowFolder(string tvDir, string title, bool buckets = true) =>
        Path.Combine(Under(tvDir, title, buckets), Sanitize(title));

    /// <summary>
    /// Look for an existing folder matching the show title (exact path first, then any
    /// case-insensitive match inside the bucket), so files join an existing library
    /// folder instead of creating a slightly different one. Returns null if none exists.
    /// </summary>
    public static string? FindExistingShowFolder(string tvDir, string title, bool buckets = true)
    {
        var clean = Sanitize(title);
        var bucketDir = Under(tvDir, title, buckets);

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

    // The articles a library catalogue files under the following word instead.
    private static readonly Regex LeadingArticle = new(
        @"^(?<article>the|a|an)\s+(?<rest>\S.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The name a folder is filed under. Normally the title as it reads; with the article
    /// setting on, "The Simpsons" becomes "Simpsons (The)" so it sits under S beside
    /// *Seinfeld* rather than under T with every other programme beginning "The".
    ///
    /// Only the folder is affected — the file inside it keeps the title the naming scheme
    /// gives it, because a file name is read rather than sorted.
    /// </summary>
    public static string SortName(string title, AppSettings settings)
    {
        if (!settings.SortLeadingArticleLast || string.IsNullOrWhiteSpace(title)) return title;

        var match = LeadingArticle.Match(title.Trim());
        return match.Success
            ? $"{match.Groups["rest"].Value.Trim()} ({match.Groups["article"].Value.Trim()})"
            : title;
    }

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
