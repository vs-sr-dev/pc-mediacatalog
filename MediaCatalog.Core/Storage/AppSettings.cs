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

/// <summary>A rule giving everything under a folder the same programme/film title.</summary>
public class FolderTitleRule
{
    public string Path { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public bool IncludeSubdirectories { get; set; } = true;
}

/// <summary>Maps a category to the consolidation folder its files should end up in.</summary>
public class CategoryConsolidation
{
    public string Category { get; set; } = string.Empty;
    public string Folder { get; set; } = string.Empty;
}

/// <summary>Remembered width/visibility of a results-grid column.</summary>
public class ColumnLayout
{
    public string Header { get; set; } = string.Empty;
    public double Width { get; set; }
    public bool Visible { get; set; } = true;
}

/// <summary>A results filter as persisted between runs.</summary>
public class SavedFilter
{
    public string Column { get; set; } = string.Empty;
    public string Pattern { get; set; } = string.Empty;
    public bool Negate { get; set; }
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

    /// <summary>
    /// Drives to watch for new files. Empty means "every drive that was scanned", which
    /// is what earlier versions did.
    /// </summary>
    [XmlArray("WatchedDrives"), XmlArrayItem("Drive")]
    public List<string> WatchedDrives { get; set; } = new();

    // --- Exclusions ---
    [XmlArray("IgnoredExtensions"), XmlArrayItem("Extension")]
    public List<string> IgnoredExtensions { get; set; } = new();

    [XmlArray("ExcludedFolders"), XmlArrayItem("Folder")]
    public List<ExcludedFolder> ExcludedFolders { get; set; } = new();

    /// <summary>Skip Windows, Program Files, $Recycle.Bin and friends (on by default).</summary>
    public bool ExcludeSystemDirectories { get; set; } = true;

    // --- Results grid ---
    [XmlArray("Columns"), XmlArrayItem("Column")]
    public List<ColumnLayout> ColumnLayouts { get; set; } = new();

    /// <summary>Restore the previous session's filters when the app starts.</summary>
    public bool RememberFilters { get; set; } = true;

    /// <summary>The view (All/Video/Movies/…) that was selected when the app last closed.</summary>
    public string LastFilterMode { get; set; } = string.Empty;

    // The filter box as it was left: the column, what was typed in it, and whether it
    // was negated. Stored apart from the committed clauses below.
    public string LastFilterColumn { get; set; } = string.Empty;
    public string LastFilterPattern { get; set; } = string.Empty;
    public bool LastFilterNegate { get; set; }

    [XmlArray("SavedFilters"), XmlArrayItem("Filter")]
    public List<SavedFilter> SavedFilters { get; set; } = new();

    // --- Extra locations ---
    /// <summary>
    /// Folders scanned in addition to whole drives — a downloads folder, say — so new
    /// arrivals can be picked up without re-walking a drive.
    /// </summary>
    [XmlArray("ScanFolders"), XmlArrayItem("Folder")]
    public List<string> AdditionalScanFolders { get; set; } = new();

    /// <summary>Start hidden in the notification area when launched at Windows sign-in.</summary>
    public bool StartInTray { get; set; } = true;

    // --- Categories ---
    [XmlArray("CustomCategories"), XmlArrayItem("Category")]
    public List<string> CustomCategories { get; set; } = new();

    [XmlArray("FolderCategoryRules"), XmlArrayItem("Rule")]
    public List<FolderCategoryRule> FolderCategoryRules { get; set; } = new();

    /// <summary>Titles applied to everything under a folder — a whole show in one go.</summary>
    [XmlArray("FolderTitleRules"), XmlArrayItem("Rule")]
    public List<FolderTitleRule> FolderTitleRules { get; set; } = new();

    // --- Helpers -----------------------------------------------------------

    public bool IsExtensionIgnored(string extension) =>
        IgnoredExtensions.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase));

    public void IgnoreExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return;
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        if (!IsExtensionIgnored(ext)) IgnoredExtensions.Add(ext.ToLowerInvariant());
    }

    /// <summary>
    /// The consolidation folder for a category, or null if none is configured. Extras
    /// follow the film/show they belong to, and the legacy TV/Film settings act as a
    /// fallback for catalogues saved before per-category folders existed.
    /// </summary>
    public string? ConsolidationDirFor(string category)
    {
        if (string.IsNullOrWhiteSpace(category)) return null;

        var m = CategoryFolders.FirstOrDefault(c =>
            string.Equals(c.Category, category, StringComparison.OrdinalIgnoreCase));
        if (m != null && !string.IsNullOrWhiteSpace(m.Folder)) return m.Folder;

        if (string.Equals(category, "TvShow", StringComparison.OrdinalIgnoreCase))
            return Blank(TvConsolidationDir);
        if (string.Equals(category, "Movie", StringComparison.OrdinalIgnoreCase))
            return Blank(FilmConsolidationDir);

        // Extras have no folder of their own: they live with their show or film.
        if (string.Equals(category, "TvExtra", StringComparison.OrdinalIgnoreCase))
            return ConsolidationDirFor("TvShow");
        if (string.Equals(category, "MovieExtra", StringComparison.OrdinalIgnoreCase))
            return ConsolidationDirFor("Movie");

        return null;

        static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
    }

    /// <summary>
    /// Fold the legacy TV/Film folder settings into <see cref="CategoryFolders"/> so the
    /// settings UI can present one uniform, unbounded list.
    /// </summary>
    public void NormaliseCategoryFolders()
    {
        void Seed(string category, string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return;
            if (CategoryFolders.Any(c => string.Equals(c.Category, category, StringComparison.OrdinalIgnoreCase)))
                return;
            CategoryFolders.Add(new CategoryConsolidation { Category = category, Folder = folder });
        }

        Seed("TvShow", TvConsolidationDir);
        Seed("Movie", FilmConsolidationDir);
        CategoryFolders.RemoveAll(c => string.IsNullOrWhiteSpace(c.Category) ||
                                       string.IsNullOrWhiteSpace(c.Folder));
        SyncLegacyFolders();
    }

    /// <summary>Keep the old TV/Film fields in step with the category list.</summary>
    public void SyncLegacyFolders()
    {
        TvConsolidationDir = ConsolidationDirFor("TvShow") ?? string.Empty;
        FilmConsolidationDir = ConsolidationDirFor("Movie") ?? string.Empty;
    }

    /// <summary>True when at least one category has somewhere to be consolidated to.</summary>
    public bool HasAnyConsolidationFolder =>
        CategoryFolders.Any(c => !string.IsNullOrWhiteSpace(c.Folder)) ||
        !string.IsNullOrWhiteSpace(TvConsolidationDir) ||
        !string.IsNullOrWhiteSpace(FilmConsolidationDir);

    /// <summary>True when a rule is a pattern rather than a specific folder path.</summary>
    public static bool HasWildcard(string s) =>
        !string.IsNullOrEmpty(s) && (s.IndexOf('*') >= 0 || s.IndexOf('?') >= 0);

    /// <summary>
    /// True when an exclusion path is a plain folder that doesn't exist — worth
    /// confirming, since it is more likely a typo than a deliberate rule. Patterns are
    /// exempt: <c>*\Windows\*</c> is meant to match many folders and exists as none.
    /// </summary>
    public static bool IsQuestionablePath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !HasWildcard(path) &&
        !Directory.Exists(path.TrimEnd('\\', '/'));

    /// <summary>
    /// Folders that are never worth cataloguing. Wildcard rules, so they cover every
    /// drive: <c>?:</c> is any drive letter.
    /// </summary>
    public static readonly string[] SystemDirectoryPatterns =
    {
        @"?:\Windows",
        @"?:\Windows.old",
        @"?:\Program Files",
        @"?:\Program Files (x86)",
        @"?:\ProgramData",
        @"?:\$Recycle.Bin",
        @"?:\$RECYCLE.BIN",
        @"?:\System Volume Information",
        @"?:\Recovery",
        @"?:\$WinREAgent",
        @"?:\MSOCache",
        @"?:\PerfLogs",
        @"*\AppData\Local\Temp",
        @"*\AppData\Local\Packages"
    };

    private static readonly ExcludedFolder[] SystemExclusions = SystemDirectoryPatterns
        .Select(p => new ExcludedFolder { Path = p, IncludeSubdirectories = true })
        .ToArray();

    /// <summary>Every rule in force: the user's, plus the system folders when enabled.</summary>
    private IEnumerable<ExcludedFolder> EffectiveExclusions() =>
        ExcludeSystemDirectories ? ExcludedFolders.Concat(SystemExclusions) : ExcludedFolders;

    /// <summary>True if <paramref name="fullPath"/> is excluded (literal or wildcard rule).</summary>
    public bool IsPathExcluded(string fullPath) =>
        EffectiveExclusions().Any(ex => ExcludesFile(ex, fullPath));

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
        EffectiveExclusions().Any(ex => ex.IncludeSubdirectories && BlocksDescent(ex, dir));

    private static bool BlocksDescent(ExcludedFolder ex, string dir)
    {
        if (string.IsNullOrEmpty(ex.Path)) return false;
        if (HasWildcard(ex.Path))
        {
            var pat = ex.Path.TrimEnd('\\', '/');
            // A rule written to match files — "*\Windows\*" — must also prune the folder
            // itself ("C:\Windows"), or the whole subtree gets walked for nothing.
            var folderPat = pat.EndsWith(@"\*", StringComparison.Ordinal) ? pat[..^2] : pat;
            return Filtering.WildcardMatcher.IsMatch(dir, pat)
                || Filtering.WildcardMatcher.IsMatch(dir, pat + @"\*")
                || Filtering.WildcardMatcher.IsMatch(dir, folderPat)
                || Filtering.WildcardMatcher.IsMatch(dir, folderPat + @"\*");
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
    public string? CategoryForPath(string fullPath) =>
        BestRule(FolderCategoryRules.Select(r => (r.Path, r.IncludeSubdirectories, Value: r.Category)), fullPath);

    /// <summary>Title rule matching a path, preferring the deepest (most specific) folder.</summary>
    public string? TitleForPath(string fullPath) =>
        BestRule(FolderTitleRules.Select(r => (r.Path, r.IncludeSubdirectories, Value: r.Title)), fullPath);

    /// <summary>
    /// The value of the deepest folder rule covering <paramref name="fullPath"/>: a rule
    /// on a season folder beats one on the show, which beats one on the whole library.
    /// </summary>
    private static string? BestRule(
        IEnumerable<(string Path, bool IncludeSubdirectories, string Value)> rules, string fullPath)
    {
        string? best = null;
        var bestLen = -1;
        foreach (var rule in rules)
        {
            if (string.IsNullOrEmpty(rule.Path) || string.IsNullOrWhiteSpace(rule.Value)) continue;
            var matches = rule.IncludeSubdirectories
                ? IsUnder(fullPath, rule.Path)
                : string.Equals(System.IO.Path.GetDirectoryName(fullPath),
                    rule.Path.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            if (matches && rule.Path.Length > bestLen)
            {
                best = rule.Value;
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
            var settings = (AppSettings?)Serializer.Deserialize(reader) ?? new AppSettings();
            settings.NormaliseCategoryFolders();
            return settings;
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
