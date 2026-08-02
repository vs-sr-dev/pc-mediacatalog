using System.Xml.Serialization;
using MediaCatalog.Core.Models;

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

    /// <summary>
    /// Start hidden in the notification area however the app was launched, not only when
    /// Windows started it. Off by default: launching something by hand should show it.
    /// </summary>
    public bool AlwaysStartMinimised { get; set; }

    /// <summary>
    /// Minimising sends the window to the notification area rather than the taskbar.
    /// Off by default, since it surprises people who expect the taskbar button.
    /// </summary>
    public bool MinimiseToTray { get; set; }

    // --- Scan limits ---
    /// <summary>Smallest file worth cataloguing, in bytes. 0 = no lower limit (the default).</summary>
    public long MinFileSizeBytes { get; set; }

    /// <summary>Largest file worth cataloguing, in bytes. 0 = no upper limit (the default).</summary>
    public long MaxFileSizeBytes { get; set; }

    /// <summary>
    /// Which kinds of media a scan picks up. An audio-only scan followed by a video-only
    /// one leaves both in the one catalogue: a filtered scan never prunes what it wasn't
    /// looking for.
    /// </summary>
    public ScanMediaFilter ScanMediaFilter { get; set; } = ScanMediaFilter.All;

    /// <summary>Where the current file name sits in the progress message (see the enum).</summary>
    public ProgressNamePosition ProgressNamePosition { get; set; } = ProgressNamePosition.Left;

    // --- IMDb ---
    /// <summary>
    /// Hold the IMDb extract in memory instead of re-reading it from disk for every
    /// lookup. On by default; roughly 100–200 MB for the current dataset.
    /// </summary>
    public bool ImdbInMemory { get; set; } = true;

    /// <summary>Consult the local IMDb extract before falling back to TMDb.</summary>
    public bool UseImdbFirst { get; set; } = true;

    /// <summary>
    /// Where <c>title.basics.tsv.gz</c> is fetched from when the user asks for it to be
    /// downloaded. Kept as a setting so a changed address can be corrected without a new
    /// build; blank falls back to <see cref="DefaultImdbDownloadUrl"/>.
    /// </summary>
    public string ImdbDownloadUrl { get; set; } = DefaultImdbDownloadUrl;

    /// <summary>IMDb's published location for the titles dataset.</summary>
    public const string DefaultImdbDownloadUrl = "https://datasets.imdbws.com/title.basics.tsv.gz";

    /// <summary>The download address to actually use, falling back to the built-in one.</summary>
    [XmlIgnore]
    public string EffectiveImdbDownloadUrl =>
        string.IsNullOrWhiteSpace(ImdbDownloadUrl) ? DefaultImdbDownloadUrl : ImdbDownloadUrl.Trim();

    // --- Housekeeping ------------------------------------------------------

    /// <summary>
    /// What to do when a new exclusion covers rules that already exist. Asking is the
    /// default; the alternatives are to prune them silently or to leave them be.
    /// </summary>
    public RedundantRuleAction RedundantExclusions { get; set; } = RedundantRuleAction.Ask;

    /// <summary>
    /// Rename the file on disk when its title changes, following the naming scheme for its
    /// category. On by default: a corrected title that leaves the old name on disk is only
    /// half a correction.
    /// </summary>
    public bool RenameOnTitleChange { get; set; } = true;

    /// <summary>
    /// Open the Delete files dialog with "Skip the Recycle Bin" already ticked.
    ///
    /// Off by default, and deliberately so: the bin is the only thing standing between a
    /// mis-click and a file that is simply gone. The option exists because people with very
    /// large libraries ask for it, not because it is a good idea.
    /// </summary>
    public bool SkipRecycleBinByDefault { get; set; }

    /// <summary>
    /// After deleting the last file in a folder, offer to remove the folder too. On by
    /// default: an empty folder left behind is litter from an operation the user asked for.
    /// </summary>
    public bool OfferRemoveEmptyFolders { get; set; } = true;

    /// <summary>
    /// Give every word of a parsed title its initial capital, so a title read out of
    /// "the.matrix.1999.mkv" reads *The Matrix*. On by default; confirmed titles are left
    /// exactly as their source spelled them either way.
    /// </summary>
    public bool CapitaliseTitles { get; set; } = true;

    /// <summary>
    /// File "The Simpsons" under S as "Simpsons (The)" rather than under T. Off by default:
    /// it is how a library catalogue sorts, but not how most people expect a folder tree to
    /// look, and turning it on moves existing folders the next time they are consolidated.
    /// </summary>
    public bool SortLeadingArticleLast { get; set; }

    /// <summary>What double-clicking a row does: play the file, or edit its details.</summary>
    public DoubleClickAction DoubleClickAction { get; set; } = DoubleClickAction.Play;

    /// <summary>
    /// Read each file's length and quality during a scan, using ffprobe. On by default,
    /// and near-free: it reads the container header rather than the file, and entries that
    /// already know are skipped. Without external tools it does nothing at all.
    /// </summary>
    public bool ProbeDuringScan { get; set; } = true;

    /// <summary>
    /// Drives the scan wizard last ran over, so it opens on the same choice next time.
    /// </summary>
    [XmlArray("ScanDrives"), XmlArrayItem("Drive")]
    public List<string> ScanDrives { get; set; } = new();

    /// <summary>
    /// Set once the scan wizard has been through. Until then it is offered unprompted,
    /// since an empty catalogue is not much use and the options are worth seeing once.
    /// </summary>
    public bool ScanWizardCompleted { get; set; }

    /// <summary>
    /// True when a file of this size should be catalogued, given the configured limits.
    /// Zero-length files always pass: they are reported as corrupt rather than hidden.
    /// </summary>
    public bool IsSizeInRange(long bytes)
    {
        if (bytes <= 0) return true;
        if (MinFileSizeBytes > 0 && bytes < MinFileSizeBytes) return false;
        if (MaxFileSizeBytes > 0 && bytes > MaxFileSizeBytes) return false;
        return true;
    }

    /// <summary>True when a media kind is in scope for the current scan filter.</summary>
    public bool IsKindScanned(MediaKind kind) => ScanMediaFilter switch
    {
        Models.ScanMediaFilter.VideoOnly => kind == MediaKind.Video,
        Models.ScanMediaFilter.AudioOnly => kind == MediaKind.Audio,
        _ => true
    };

    /// <summary>True when an extension is in scope for the current scan filter.</summary>
    public bool IsExtensionScanned(string extension)
    {
        if (ScanMediaFilter == Models.ScanMediaFilter.All) return true;
        // Half-downloaded files have no meaningful kind yet, so a filtered scan leaves
        // them alone rather than guessing wrong in either direction.
        if (Scanning.MediaExtensions.IsIncompleteMarker(extension)) return false;
        return IsKindScanned(Scanning.MediaExtensions.Classify(extension));
    }

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
    /// What is wrong with a folder chosen as a consolidation target, or null when it is
    /// fine. A drive that is not there cannot be created and is almost always a typo or an
    /// unplugged disk; a folder on a drive that *is* there is simply made.
    /// </summary>
    public static string? ValidateConsolidationFolder(string folder, bool create = true)
    {
        var path = (folder ?? string.Empty).Trim();
        if (path.Length == 0) return null;   // an empty row is not a mistake, it is unset

        string root;
        try { root = Path.GetPathRoot(Path.GetFullPath(path)) ?? string.Empty; }
        catch (Exception ex) { return $"'{path}' is not a usable folder path: {ex.Message}"; }

        if (root.Length == 0)
            return $"'{path}' has no drive or share to sit on — give the full path.";
        if (!Directory.Exists(root))
            return $"Drive {root.TrimEnd('\\', '/')} is not there. Connect it, or correct the path.";

        if (Directory.Exists(path) || !create) return null;

        try
        {
            Directory.CreateDirectory(path);
            return null;
        }
        catch (Exception ex)
        {
            return $"'{path}' could not be created: {ex.Message}";
        }
    }

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
    /// Existing exclusions made redundant by <paramref name="candidate"/> — rules whose
    /// every effect the candidate already has. Used to offer pruning the old ones.
    ///
    /// Both plain paths and patterns are considered: <c>*\Windows\*</c> supersedes
    /// <c>C:\Windows</c> just as <c>D:\Media</c> supersedes <c>D:\Media\Films</c>.
    /// </summary>
    public List<ExcludedFolder> FindSupersededBy(ExcludedFolder candidate) =>
        ExcludedFolders
            .Where(ex => !ReferenceEquals(ex, candidate) && Supersedes(candidate, ex))
            .ToList();

    /// <summary>
    /// User rules the built-in system-folder list already covers, when that list is
    /// switched on. Worth offering to prune for the same reason as any other redundancy.
    /// </summary>
    public List<ExcludedFolder> FindCoveredBySystemRules() =>
        ExcludeSystemDirectories
            ? ExcludedFolders.Where(ex => SystemExclusions.Any(sys => Supersedes(sys, ex))).ToList()
            : new List<ExcludedFolder>();

    /// <summary>
    /// Add <paramref name="candidate"/>'s effect to <paramref name="rules"/> by removing
    /// whatever it makes redundant, following <paramref name="policy"/>. Returns the rules
    /// dropped, so the caller can say what happened.
    ///
    /// One implementation for both places a rule can be added — the settings list and the
    /// results grid's "exclude this folder" — so the policy means the same thing wherever
    /// it is applied.
    /// </summary>
    /// <param name="ask">
    /// Put the redundant rules to the user and hand back the ones they chose to drop —
    /// which may be all of them, some of them, or none. Only consulted under
    /// <see cref="RedundantRuleAction.Ask"/>; removing some but not all is a perfectly
    /// reasonable answer, so the choice is a list rather than a yes or no.
    /// </param>
    public static List<ExcludedFolder> PruneSuperseded(
        IList<ExcludedFolder> rules,
        ExcludedFolder candidate,
        RedundantRuleAction policy,
        Func<IReadOnlyList<ExcludedFolder>, IReadOnlyList<ExcludedFolder>>? ask = null)
    {
        var none = new List<ExcludedFolder>();
        if (policy == RedundantRuleAction.Keep) return none;

        var superseded = rules
            .Where(r => !ReferenceEquals(r, candidate) && Supersedes(candidate, r))
            .ToList();
        if (superseded.Count == 0) return none;

        // Asking is the default; the other policies decide without stopping.
        var chosen = policy == RedundantRuleAction.Ask
            ? ask?.Invoke(superseded)?.ToList() ?? none
            : superseded;

        foreach (var rule in chosen) rules.Remove(rule);
        return chosen;
    }

    /// <summary>
    /// True when <paramref name="outer"/> already excludes everything
    /// <paramref name="inner"/> would — so keeping <paramref name="inner"/> changes nothing.
    /// </summary>
    public static bool Supersedes(ExcludedFolder outer, ExcludedFolder inner)
    {
        if (string.IsNullOrWhiteSpace(outer.Path) || string.IsNullOrWhiteSpace(inner.Path))
            return false;

        var outerPath = outer.Path.TrimEnd('\\', '/');
        var innerPath = inner.Path.TrimEnd('\\', '/');
        var same = string.Equals(outerPath, innerPath, StringComparison.OrdinalIgnoreCase);

        // The identical path twice: redundant unless the survivor covers less than the
        // rule it would replace.
        if (same) return outer.IncludeSubdirectories || !inner.IncludeSubdirectories;

        // A rule that only covers one folder cannot stand in for a rule about another.
        if (!outer.IncludeSubdirectories) return false;

        if (HasWildcard(outer.Path))
        {
            // A pattern covers a plain path when it matches that folder; a pattern covering
            // another pattern is not something we can decide, so we leave it alone.
            if (HasWildcard(inner.Path)) return false;
            return BlocksDescent(outer, innerPath);
        }

        // A plain subtree rule covers anything beneath it — but not a pattern, which may
        // well match folders on other drives entirely.
        return !HasWildcard(inner.Path) && IsUnder(innerPath, outerPath);
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
