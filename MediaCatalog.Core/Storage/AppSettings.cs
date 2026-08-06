using System.Xml.Serialization;
using MediaCatalog.Core.Consolidation;
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

    /// <summary>
    /// The name pattern consolidated files of this category are filed under — see
    /// <see cref="Consolidation.ConsolidationNaming"/>. Blank means the built-in naming,
    /// which is what every existing catalogue has been using.
    /// </summary>
    public string NameTemplate { get; set; } = string.Empty;

    /// <summary>
    /// Whether this category's files are sorted into a first-letter folder — A–Z, or # for
    /// a title beginning with a digit — inside its consolidation folder.
    ///
    /// Null means "whatever this category has always done", which is the only answer that
    /// leaves an existing library where it is: films and programmes have been bucketed since
    /// the beginning, and everything else has gone straight into its folder. Set it either
    /// way and that is what happens, for any category.
    ///
    /// The consolidation rules the user builds are stored beside it — see
    /// <see cref="ConsolidationRule"/> — because both are answers to the same question:
    /// what this category's filing means.
    /// </summary>
    public bool? UseLetterFolders { get; set; }

    /// <summary>
    /// The steps that decide, for this category, which of two copies of one thing is kept.
    /// Empty means the built-in judgement, which is what every existing catalogue uses.
    /// </summary>
    [XmlArray("Rules"), XmlArrayItem("Rule")]
    public List<ConsolidationRule> Rules { get; set; } = new();

    /// <summary>What counts as two copies of one thing for this category.</summary>
    public DuplicateMatch MatchBy { get; set; } = DuplicateMatch.SameContentOrTitle;

    /// <summary>
    /// Facts about the copies that have to be measured before the rules can be applied:
    /// their length and quality, their fingerprints, a full decode. Only what the rules
    /// actually ask for is worth the time, so the user says.
    /// </summary>
    public bool DeepCheckBeforeConsolidating { get; set; }

    public bool FingerprintBeforeConsolidating { get; set; }
}

/// <summary>
/// How little a folder may be left holding before it counts as scraps rather than content,
/// for one category.
///
/// The figure has to be per category because the same three megabytes mean opposite things:
/// left behind after a film has been filed it is a sample, a thumbnail or a readme, while in
/// a music folder it is very probably a track somebody wants.
/// </summary>
public class LeftoverThreshold
{
    public string Category { get; set; } = string.Empty;

    /// <summary>Bytes. 0 means only a genuinely empty folder is ever offered for removal.</summary>
    public long Bytes { get; set; }
}

/// <summary>
/// How far two copies of one thing may disagree about their length and still be treated as
/// the same thing, for one category.
///
/// The figure has to be per category because a minute means opposite things in each: sixty
/// seconds between two rips of a film is the credits, or a distributor's ident, and nobody
/// cares which copy has them. Sixty seconds between two copies of a song is a different
/// recording.
/// </summary>
public class DurationTolerance
{
    public string Category { get; set; } = string.Empty;

    /// <summary>Seconds. 0 means the lengths have to agree exactly.</summary>
    public int Seconds { get; set; }
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

    /// <summary>
    /// Particular folders to watch, rather than whole drives. Watching E:\dump\ and
    /// watching E:\ are very different propositions on a disk holding a hundred thousand
    /// files, and one of them is usually what was meant.
    /// </summary>
    [XmlArray("WatchedFolders"), XmlArrayItem("Folder")]
    public List<string> WatchedFolders { get; set; } = new();

    /// <summary>
    /// True when the user has named somewhere in particular to watch. Without one, watching
    /// falls back on whatever was last scanned, which is what earlier versions did.
    /// </summary>
    [XmlIgnore]
    public bool HasExplicitWatchTargets => WatchedDrives.Count > 0 || WatchedFolders.Count > 0;

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

    /// <summary>Where <c>title.episode.tsv.gz</c> is fetched from; blank uses the default.</summary>
    public string ImdbEpisodeDownloadUrl { get; set; } = DefaultImdbEpisodeDownloadUrl;

    /// <summary>IMDb's published location for the episode dataset.</summary>
    public const string DefaultImdbEpisodeDownloadUrl =
        "https://datasets.imdbws.com/title.episode.tsv.gz";

    /// <summary>The episode download address to use, falling back to the built-in one.</summary>
    [XmlIgnore]
    public string EffectiveImdbEpisodeDownloadUrl =>
        string.IsNullOrWhiteSpace(ImdbEpisodeDownloadUrl)
            ? DefaultImdbEpisodeDownloadUrl
            : ImdbEpisodeDownloadUrl.Trim();

    /// <summary>
    /// The earliest release year worth extracting, or null for "every year there is".
    ///
    /// 1950 by default. The dataset reaches back to the 1890s and most of what is in there
    /// is of no use to anybody cataloguing their own films: the extract is smaller, loads
    /// faster and answers quicker for leaving it out. A row with no year at all is kept
    /// whatever this says — a missing date is not a date outside the range.
    /// </summary>
    public int? ExtractStartYear { get; set; } = 1950;

    /// <summary>
    /// The latest release year worth extracting, or null (the default) for "everything from
    /// the start year onwards", which is what almost everybody wants.
    /// </summary>
    public int? ExtractEndYear { get; set; }

    /// <summary>True when a title of this year belongs in the extract.</summary>
    public bool IsYearExtracted(int? year)
    {
        if (year is not { } y) return true;              // no year is not a year out of range
        if (ExtractStartYear is { } from && y < from) return false;
        if (ExtractEndYear is { } to && y > to) return false;
        return true;
    }

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
    /// Delete an emptied folder outright rather than sending it to the Recycle Bin. On by
    /// default, and safe in a way that deleting a *file* permanently is not: the folder is
    /// empty, or holds only what the size threshold below has already called scraps, so
    /// there is nothing in it to recover.
    /// </summary>
    public bool DeleteEmptyFoldersPermanently { get; set; } = true;

    /// <summary>
    /// Per-category size below which whatever a consolidation left behind counts as scraps,
    /// so the folder can go rather than being kept for the sake of a sample clip.
    /// </summary>
    [XmlArray("LeftoverThresholds"), XmlArrayItem("Threshold")]
    public List<LeftoverThreshold> LeftoverThresholds { get; set; } = new();

    /// <summary>Set once the defaults below have been seeded, so a cleared list stays cleared.</summary>
    public bool LeftoverThresholdsInitialised { get; set; }

    /// <summary>
    /// Take the folders a consolidation has emptied away without asking. On by default: what
    /// goes has already been judged to be nothing — an empty folder, or one holding less than
    /// the size limit for its category, with no catalogued file in it waiting to be filed —
    /// and a question whose answer is always yes is not a question worth asking.
    /// </summary>
    public bool RemoveEmptyFoldersAutomatically { get; set; } = true;

    /// <summary>
    /// How far two copies of one thing may disagree about their length, per category, and
    /// still be consolidated automatically rather than put to the user.
    /// </summary>
    [XmlArray("DurationTolerances"), XmlArrayItem("Tolerance")]
    public List<DurationTolerance> DurationTolerances { get; set; } = new();

    /// <summary>Set once the defaults below have been seeded, so a zeroed list stays zeroed.</summary>
    public bool DurationTolerancesInitialised { get; set; }

    /// <summary>
    /// The length tolerance for a category, in seconds. Extras follow the show or film they
    /// belong to; anything unlisted has to match exactly.
    /// </summary>
    public int DurationToleranceFor(string category)
    {
        if (string.IsNullOrWhiteSpace(category)) return 0;

        var match = DurationTolerances.FirstOrDefault(t =>
            string.Equals(t.Category, category, StringComparison.OrdinalIgnoreCase));
        if (match != null) return Math.Max(0, match.Seconds);

        if (string.Equals(category, "TvExtra", StringComparison.OrdinalIgnoreCase))
            return DurationToleranceFor("TvShow");
        if (string.Equals(category, "MovieExtra", StringComparison.OrdinalIgnoreCase))
            return DurationToleranceFor("Movie");

        return 0;
    }

    /// <summary>
    /// The figures a new install starts with. A minute either way on a film or an episode is
    /// the credits or an ident and decides nothing; two seconds on a track is already the
    /// difference between a single edit and an album version.
    /// </summary>
    private static readonly (string Category, int Seconds)[] DefaultDurationTolerances =
    {
        ("TvShow", 60),
        ("Movie", 60),
        ("Audio", 2)
    };

    /// <summary>Seed the defaults once, so a list the user has zeroed is not helpfully refilled.</summary>
    public void NormaliseDurationTolerances()
    {
        if (DurationTolerancesInitialised) return;
        DurationTolerancesInitialised = true;

        foreach (var (category, seconds) in DefaultDurationTolerances)
            if (!DurationTolerances.Any(t =>
                    string.Equals(t.Category, category, StringComparison.OrdinalIgnoreCase)))
                DurationTolerances.Add(new DurationTolerance { Category = category, Seconds = seconds });
    }

    /// <summary>
    /// Bring subtitles along when their video is consolidated or moved. Off, they are
    /// removed instead: a subtitle is matched to its film by name alone, so one left behind
    /// after the film has gone can never be matched to anything again.
    /// </summary>
    public bool ConsolidateSubtitles { get; set; } = true;

    /// <summary>
    /// How long to wait after spotting a new file before saying anything, in seconds.
    /// Five files arriving together are one thing that happened, not five, and five
    /// notifications about it is four too many.
    /// </summary>
    public int NewFileNotifyDelaySeconds { get; set; } = 20;

    /// <summary>
    /// The order categories appear in wherever one is chosen. Names not listed here follow
    /// in their built-in order, so a new category never disappears for want of being listed.
    /// </summary>
    [XmlArray("CategoryOrder"), XmlArrayItem("Category")]
    public List<string> CategoryOrder { get; set; } = new();

    /// <summary>
    /// The leftover size limit for a category, in bytes. Extras follow the show or film they
    /// belong to, as their folder does; anything unlisted is 0 — only an empty folder goes.
    /// </summary>
    public long LeftoverThresholdFor(string category)
    {
        if (string.IsNullOrWhiteSpace(category)) return 0;

        var match = LeftoverThresholds.FirstOrDefault(t =>
            string.Equals(t.Category, category, StringComparison.OrdinalIgnoreCase));
        if (match != null) return Math.Max(0, match.Bytes);

        if (string.Equals(category, "TvExtra", StringComparison.OrdinalIgnoreCase))
            return LeftoverThresholdFor("TvShow");
        if (string.Equals(category, "MovieExtra", StringComparison.OrdinalIgnoreCase))
            return LeftoverThresholdFor("Movie");

        return 0;
    }

    /// <summary>
    /// The figures a new install starts with: video categories get 25 MB, on the grounds
    /// that nothing left beside a filed film at that size is the film. Audio gets nothing,
    /// because a three-megabyte file in a music folder is very likely a track.
    /// </summary>
    private static readonly (string Category, long Bytes)[] DefaultLeftoverThresholds =
    {
        ("TvShow", 25L * 1024 * 1024),
        ("Movie", 25L * 1024 * 1024),
        ("Audio", 0)
    };

    /// <summary>Seed the defaults once, so an emptied list is not helpfully refilled.</summary>
    public void NormaliseLeftoverThresholds()
    {
        if (LeftoverThresholdsInitialised) return;
        LeftoverThresholdsInitialised = true;

        foreach (var (category, bytes) in DefaultLeftoverThresholds)
            if (!LeftoverThresholds.Any(t =>
                    string.Equals(t.Category, category, StringComparison.OrdinalIgnoreCase)))
                LeftoverThresholds.Add(new LeftoverThreshold { Category = category, Bytes = bytes });
    }

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
    /// Show the full explanation under every setting rather than folding it away.
    ///
    /// Off by default. The explanations are worth having — but all of them at once, on a tab
    /// somebody opened to tick one box, is a wall of prose that gets skipped, which means the
    /// paragraph that mattered gets skipped with it. Each group opens on its own, and hovering
    /// any setting says a line about it, so nothing is out of reach.
    /// </summary>
    public bool ShowSettingsExplanations { get; set; }

    /// <summary>
    /// Show the TMDb credentials in Settings, and the Validate TV (TMDb) command on the menu.
    ///
    /// Off by default: TMDb is deprecated, it is only consulted when the local IMDb extract
    /// is missing, and it answers one query every two seconds. A key already entered goes on
    /// working whatever this says — hiding something is not the same as switching it off.
    /// </summary>
    public bool ShowTmdbSettings { get; set; }

    /// <summary>
    /// Show the commands that are on their way out — the ones gathered under Redundant, each
    /// with the reason it is expected to go. Off by default, which is the point of the folder:
    /// a menu of thirty commands where six of them are historical is a menu nobody can read.
    /// </summary>
    public bool ShowRedundantCommands { get; set; }

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

    /// <summary>
    /// True when the extension is on the ignore list, by name or by pattern.
    ///
    /// A rule may be a plain extension (<c>.nfo</c>) or a wildcard one: <c>?</c> stands for
    /// exactly one character and <c>*</c> for any run of them, so <c>.mp?</c> covers .mp3 and
    /// .mp4 while <c>.m*</c> covers every extension beginning with m. The whole extension has
    /// to match — a pattern is not a "contains" search, or <c>.mp3</c> would ignore
    /// <c>.mp3x</c> as well.
    /// </summary>
    public bool IsExtensionIgnored(string extension)
    {
        if (string.IsNullOrEmpty(extension)) return false;

        foreach (var rule in IgnoredExtensions)
        {
            if (string.IsNullOrWhiteSpace(rule)) continue;
            var pattern = rule.Trim();

            if (HasWildcard(pattern))
            {
                // Anchored, unlike the results filter: ".mp3" must not ignore ".mp3x".
                if (Filtering.WildcardMatcher.IsMatchWhole(extension, pattern)) return true;
            }
            else if (string.Equals(pattern, extension, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

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
    /// The file-name pattern for a category, or null when it has none and the built-in
    /// naming applies. Extras follow the show or film they belong to, as their folder does.
    /// </summary>
    public string? NameTemplateFor(string category)
    {
        if (string.IsNullOrWhiteSpace(category)) return null;

        var match = CategoryFolders.FirstOrDefault(c =>
            string.Equals(c.Category, category, StringComparison.OrdinalIgnoreCase));
        if (match != null && !string.IsNullOrWhiteSpace(match.NameTemplate)) return match.NameTemplate;

        if (string.Equals(category, "TvExtra", StringComparison.OrdinalIgnoreCase))
            return NameTemplateFor("TvShow");
        if (string.Equals(category, "MovieExtra", StringComparison.OrdinalIgnoreCase))
            return NameTemplateFor("Movie");

        return null;
    }

    /// <summary>
    /// The entry holding everything configured about a category's filing, or null when the
    /// category has never been configured. Extras follow the show or film they belong to.
    /// </summary>
    public CategoryConsolidation? ConsolidationFor(string category)
    {
        if (string.IsNullOrWhiteSpace(category)) return null;

        var match = CategoryFolders.FirstOrDefault(c =>
            string.Equals(c.Category, category, StringComparison.OrdinalIgnoreCase));
        if (match != null) return match;

        if (string.Equals(category, "TvExtra", StringComparison.OrdinalIgnoreCase))
            return ConsolidationFor("TvShow");
        if (string.Equals(category, "MovieExtra", StringComparison.OrdinalIgnoreCase))
            return ConsolidationFor("Movie");

        return null;
    }

    /// <summary>
    /// True when this category's files are sorted into a first-letter folder inside its
    /// consolidation folder.
    ///
    /// Unset means what the category has always done, which is the only answer that leaves
    /// an existing library standing: films and programmes have been bucketed from the start,
    /// and every other category has gone straight into its folder.
    /// </summary>
    public bool UseLetterFoldersFor(string category) =>
        ConsolidationFor(category)?.UseLetterFolders ?? IsBucketedByDefault(category);

    /// <summary>The categories the built-in layout has always sorted A–Z.</summary>
    public static bool IsBucketedByDefault(string category) =>
        category is "TvShow" or "Movie" or "TvExtra" or "MovieExtra";

    /// <summary>
    /// The steps deciding which copy of a thing this category keeps, or an empty list when
    /// the built-in judgement applies.
    /// </summary>
    public IReadOnlyList<Consolidation.ConsolidationRule> RulesFor(string category) =>
        ConsolidationFor(category)?.Rules ?? new List<Consolidation.ConsolidationRule>();

    /// <summary>What counts as two copies of one thing, for this category.</summary>
    public Consolidation.DuplicateMatch MatchForCategory(string category) =>
        ConsolidationFor(category)?.MatchBy ?? Consolidation.DuplicateMatch.SameContentOrTitle;

    /// <summary>True when this category's rules need every copy decoded before they can run.</summary>
    public bool DeepCheckFor(string category) =>
        ConsolidationFor(category)?.DeepCheckBeforeConsolidating ?? false;

    /// <summary>True when this category's rules need every copy fingerprinted first.</summary>
    public bool FingerprintFor(string category) =>
        ConsolidationFor(category)?.FingerprintBeforeConsolidating ?? false;

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
        // A row with no category says nothing at all and goes. A row with no folder is
        // kept if it carries anything else the user has set — the rules for choosing
        // between copies belong to the category, not to the folder, and a category whose
        // folder has not been chosen yet is a job half done rather than a mistake.
        CategoryFolders.RemoveAll(c => string.IsNullOrWhiteSpace(c.Category) ||
                                       (string.IsNullOrWhiteSpace(c.Folder) && SaysNothingElse(c)));

        static bool SaysNothingElse(CategoryConsolidation c) =>
            c.Rules.Count == 0 && c.UseLetterFolders is null &&
            string.IsNullOrWhiteSpace(c.NameTemplate) &&
            c.MatchBy == DuplicateMatch.SameContentOrTitle &&
            !c.DeepCheckBeforeConsolidating && !c.FingerprintBeforeConsolidating;
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
        if (!File.Exists(path)) return Fresh();
        try
        {
            using var reader = new StreamReader(path);
            var settings = (AppSettings?)Serializer.Deserialize(reader) ?? new AppSettings();
            settings.NormaliseCategoryFolders();
            settings.NormaliseLeftoverThresholds();
            settings.NormaliseDurationTolerances();
            settings.NormaliseColumnNames();
            return settings;
        }
        catch { return Fresh(); }
    }

    /// <summary>A first-run settings object, with the defaults that are lists seeded.</summary>
    private static AppSettings Fresh()
    {
        var settings = new AppSettings();
        settings.NormaliseLeftoverThresholds();
        settings.NormaliseDurationTolerances();
        return settings;
    }

    /// <summary>
    /// Columns that have been renamed since a settings file was written, so a remembered
    /// width and a saved filter survive the rename rather than quietly pointing at a column
    /// that no longer exists.
    /// </summary>
    private static readonly (string Was, string Is)[] RenamedColumns =
    {
        ("Filed", "Consolidated"),
        ("Title", "Primary title")
    };

    /// <summary>Put remembered column widths and filters onto the current column names.</summary>
    public void NormaliseColumnNames()
    {
        foreach (var (was, now) in RenamedColumns)
        {
            foreach (var column in ColumnLayouts)
                if (string.Equals(column.Header, was, StringComparison.OrdinalIgnoreCase))
                    column.Header = now;

            foreach (var filter in SavedFilters)
                if (string.Equals(filter.Column, was, StringComparison.OrdinalIgnoreCase))
                    filter.Column = now;

            if (string.Equals(LastFilterColumn, was, StringComparison.OrdinalIgnoreCase))
                LastFilterColumn = now;
        }

        // A rename can leave two entries for one column — the old name migrated on top of a
        // new one already written. The first wins; the rest would only fight over the width.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ColumnLayouts.RemoveAll(c => !seen.Add(c.Header));
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
