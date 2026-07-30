using System.Xml.Serialization;

namespace MediaCatalog.Core.Storage;

/// <summary>A folder the user has excluded from results/scans.</summary>
public class ExcludedFolder
{
    public string Path { get; set; } = string.Empty;
    /// <summary>When true, everything beneath the folder is excluded too.</summary>
    public bool IncludeSubdirectories { get; set; } = true;
}

/// <summary>A rule assigning a category to everything under a folder.</summary>
public class FolderCategoryRule
{
    public string Path { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool IncludeSubdirectories { get; set; } = true;
}

/// <summary>Maps a category to the consolidation folder its files should end up in.</summary>
public class CategoryConsolidation
{
    public string Category { get; set; } = string.Empty;
    public string Folder { get; set; } = string.Empty;
}

/// <summary>
/// User preferences persisted to <c>settings.xml</c> in the app folder. Separate from
/// <see cref="Tools.ToolSettings"/> (tools.xml) so existing installs keep working; this
/// file is simply created on first use.
/// </summary>
[XmlRoot("AppSettings")]
public class AppSettings
{
    // --- TMDb (either credential works; the v4 read token is preferred when present) ---
    public string TmdbApiKey { get; set; } = string.Empty;              // v3 API Key
    public string TmdbReadAccessToken { get; set; } = string.Empty;     // v4 Read Access Token

    // --- Consolidation targets ---
    public string TvConsolidationDir { get; set; } = string.Empty;
    public string FilmConsolidationDir { get; set; } = string.Empty;

    /// <summary>Per-category consolidation folders (for custom categories, etc.).</summary>
    [XmlArray("CategoryFolders"), XmlArrayItem("CategoryFolder")]
    public List<CategoryConsolidation> CategoryFolders { get; set; } = new();

    // --- Startup / watching ---
    public bool StartWithWindows { get; set; }
    public bool WatchForNewFiles { get; set; }

    // --- Exclusions ---
    [XmlArray("IgnoredExtensions"), XmlArrayItem("Extension")]
    public List<string> IgnoredExtensions { get; set; } = new();

    [XmlArray("ExcludedFolders"), XmlArrayItem("Folder")]
    public List<ExcludedFolder> ExcludedFolders { get; set; } = new();

    // --- Categories ---
    [XmlArray("CustomCategories"), XmlArrayItem("Category")]
    public List<string> CustomCategories { get; set; } = new();

    [XmlArray("FolderCategoryRules"), XmlArrayItem("Rule")]
    public List<FolderCategoryRule> FolderCategoryRules { get; set; } = new();

    // --- Helpers -----------------------------------------------------------

    public bool IsExtensionIgnored(string extension) =>
        IgnoredExtensions.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase));

    public void IgnoreExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return;
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        if (!IsExtensionIgnored(ext)) IgnoredExtensions.Add(ext.ToLowerInvariant());
    }

    /// <summary>The consolidation folder for a category, or null if none is configured.</summary>
    public string? ConsolidationDirFor(string category)
    {
        var m = CategoryFolders.FirstOrDefault(c =>
            string.Equals(c.Category, category, StringComparison.OrdinalIgnoreCase));
        if (m != null && !string.IsNullOrWhiteSpace(m.Folder)) return m.Folder;
        if (string.Equals(category, "TvShow", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(TvConsolidationDir) ? null : TvConsolidationDir;
        if (string.Equals(category, "Movie", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(FilmConsolidationDir) ? null : FilmConsolidationDir;
        return null;
    }

    private static bool HasWildcard(string s) => s.IndexOf('*') >= 0 || s.IndexOf('?') >= 0;

    /// <summary>True if <paramref name="fullPath"/> is excluded (literal or wildcard rule).</summary>
    public bool IsPathExcluded(string fullPath) =>
        ExcludedFolders.Any(ex => ExcludesFile(ex, fullPath));

    private static bool ExcludesFile(ExcludedFolder ex, string fullPath)
    {
        if (string.IsNullOrEmpty(ex.Path)) return false;
        if (HasWildcard(ex.Path))
        {
            var pat = ex.Path.TrimEnd('\\', '/');
            if (Filtering.WildcardMatcher.IsMatch(fullPath, pat)) return true;
            return ex.IncludeSubdirectories && Filtering.WildcardMatcher.IsMatch(fullPath, pat + @"\*");
        }
        var root = ex.Path.TrimEnd('\\', '/');
        if (ex.IncludeSubdirectories) return IsUnder(fullPath, root);
        return string.Equals(System.IO.Path.GetDirectoryName(fullPath), root, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True if descent into <paramref name="dir"/> should be pruned (subtree excluded).</summary>
    public bool IsDescentBlocked(string dir) =>
        ExcludedFolders.Any(ex => ex.IncludeSubdirectories && BlocksDescent(ex, dir));

    private static bool BlocksDescent(ExcludedFolder ex, string dir)
    {
        if (string.IsNullOrEmpty(ex.Path)) return false;
        if (HasWildcard(ex.Path))
        {
            var pat = ex.Path.TrimEnd('\\', '/');
            return Filtering.WildcardMatcher.IsMatch(dir, pat)
                || Filtering.WildcardMatcher.IsMatch(dir, pat + @"\*");
        }
        return IsUnder(dir, ex.Path.TrimEnd('\\', '/'));
    }

    /// <summary>
    /// Existing literal exclusions made redundant by <paramref name="candidate"/> (a
    /// broader subtree rule that already covers them). Used to offer pruning old rules.
    /// </summary>
    public List<ExcludedFolder> FindSupersededBy(ExcludedFolder candidate)
    {
        if (HasWildcard(candidate.Path) || !candidate.IncludeSubdirectories)
            return new List<ExcludedFolder>();
        var root = candidate.Path.TrimEnd('\\', '/');
        return ExcludedFolders.Where(ex =>
            !ReferenceEquals(ex, candidate) &&
            !HasWildcard(ex.Path) &&
            !string.Equals(ex.Path.TrimEnd('\\', '/'), root, StringComparison.OrdinalIgnoreCase) &&
            IsUnder(ex.Path.TrimEnd('\\', '/'), root)).ToList();
    }

    /// <summary>Category rule matching a path, preferring the deepest (most specific) folder.</summary>
    public string? CategoryForPath(string fullPath)
    {
        string? best = null;
        var bestLen = -1;
        foreach (var rule in FolderCategoryRules)
        {
            if (string.IsNullOrEmpty(rule.Path)) continue;
            var matches = rule.IncludeSubdirectories
                ? IsUnder(fullPath, rule.Path)
                : string.Equals(System.IO.Path.GetDirectoryName(fullPath),
                    rule.Path.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            if (matches && rule.Path.Length > bestLen)
            {
                best = rule.Category;
                bestLen = rule.Path.Length;
            }
        }
        return best;
    }

    private static bool IsUnder(string path, string folder)
    {
        var root = folder.TrimEnd('\\', '/');
        return path.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root + System.IO.Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, root, StringComparison.OrdinalIgnoreCase);
    }

    // --- Persistence -------------------------------------------------------

    public static string DefaultPath => AppPaths.SettingsPath;

    private static readonly XmlSerializer Serializer = new(typeof(AppSettings));

    public static AppSettings Load(string path)
    {
        if (!File.Exists(path)) return new AppSettings();
        try
        {
            using var reader = new StreamReader(path);
            return (AppSettings?)Serializer.Deserialize(reader) ?? new AppSettings();
        }
        catch { return new AppSettings(); }
    }

    public void Save(string path)
    {
        try
        {
            var tmp = path + ".tmp";
            using (var writer = new StreamWriter(tmp))
                Serializer.Serialize(writer, this);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
        catch { /* settings are best-effort */ }
    }
}
