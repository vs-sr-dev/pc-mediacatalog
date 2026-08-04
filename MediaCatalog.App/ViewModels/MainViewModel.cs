using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using MediaCatalog.App.Infrastructure;
using MediaCatalog.Core.Classification;
using MediaCatalog.Core.Hashing;
using MediaCatalog.Core.Consolidation;
using MediaCatalog.Core.Duplicates;
using MediaCatalog.Core.Filtering;
using MediaCatalog.Core.Fingerprinting;
using MediaCatalog.Core.History;
using MediaCatalog.Core.Imdb;
using MediaCatalog.Core.Integrity;
using MediaCatalog.Core.Models;
using MediaCatalog.Core.Naming;
using MediaCatalog.Core.Persistence;
using MediaCatalog.Core.Relocation;
using MediaCatalog.Core.Scanning;
using MediaCatalog.Core.Storage;
using MediaCatalog.Core.Tmdb;
using MediaCatalog.Core.Tools;

namespace MediaCatalog.App.ViewModels;

public enum FilterMode
{
    All, Video, Audio, Movies, TvShows, Extras,
    Consolidated, NotConsolidated,
    Duplicates, NearDuplicates,
    /// <summary>Files claiming the same title and year without being the same bytes.</summary>
    SameTitle,
    /// <summary>
    /// Files whose year came from a title that has been used more than once — a remake, a
    /// reboot, a series and the film it came from. The most recent was taken; these are the
    /// ones where that could be the wrong one.
    /// </summary>
    UncertainYear,
    Problems
}

/// <summary>
/// Result of a consolidation run.
/// </summary>
/// <param name="AlreadyPresent">
/// Sources whose content is already sitting at the destination, so they were not copied
/// and the source is simply redundant.
/// </param>
/// <param name="AlreadyConsolidated">
/// Files that were already at the exact path the library layout gives them — nothing to
/// do but say so, and offer to tidy up any other copies of them.
/// </param>
public record ConsolidationOutcome(
    int Moved, int Skipped, int Failed, List<MediaFile> AlreadyPresent, string Message,
    List<MediaFile>? AlreadyConsolidated = null)
{
    public IReadOnlyList<MediaFile> Consolidated => AlreadyConsolidated ?? new List<MediaFile>();

    /// <summary>
    /// Folders the run left holding nothing — or nothing worth keeping, by the size limit
    /// set for the category. A move that files everything correctly and leaves a trail of
    /// folders holding a sample clip and a readme behind it has only done half the job.
    /// </summary>
    public IReadOnlyList<LeftoverFolder> LeftoverFolders { get; init; } =
        Array.Empty<LeftoverFolder>();

    /// <summary>
    /// Every file the run dealt with, wherever it ended up — including the ones a folder
    /// rename moved without touching individually. This is what the duplicate sweep at the
    /// end works from: a file that has just been filed must not be left with copies of it
    /// lying about, and a folder rename files just as many files as a copy does.
    /// </summary>
    public IReadOnlyList<MediaFile> Touched { get; init; } = Array.Empty<MediaFile>();
}

/// <summary>
/// What the scan wizard decided: what to walk, what to look for, and what to do with the
/// catalogue that already exists.
/// </summary>
/// <param name="WaitForMissingDrives">
/// Keep the scan open for a drive that is not attached, and pick it up when it appears —
/// the external-drive case, where "not plugged in" is not the same as "gone".
/// </param>
public record ScanPlan(
    IReadOnlyList<string> Drives,
    IReadOnlyList<string> Folders,
    ScanMediaFilter MediaFilter,
    ScanStartMode StartMode,
    bool WaitForMissingDrives)
{
    /// <summary>
    /// Every folder the wizard listed, ticked or not. This replaces the remembered list
    /// outright, so a folder removed there is genuinely gone rather than quietly kept
    /// because it happened not to be ticked when the scan started.
    /// </summary>
    public IReadOnlyList<string> AllFolders { get; init; } = Array.Empty<string>();

    /// <summary>Smallest file worth cataloguing, in bytes. 0 = no lower limit.</summary>
    public long MinSizeBytes { get; init; }

    /// <summary>Largest file worth cataloguing, in bytes. 0 = no upper limit.</summary>
    public long MaxSizeBytes { get; init; }

    /// <summary>
    /// Everything to walk. A chosen folder that already sits on a chosen drive is dropped:
    /// the drive covers it, and walking it twice only costs time.
    /// </summary>
    public IReadOnlyList<string> Roots =>
        Drives.Concat(Folders.Where(f => !Drives.Any(d => IsUnder(f, d)))).ToList();

    private static bool IsUnder(string path, string root)
    {
        var trimmed = root.TrimEnd('\\', '/');
        return trimmed.Length > 0 &&
               path.StartsWith(trimmed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Result of a delete, including the detail needed to explain any refusals.</summary>
public record DeleteOutcome(DeleteResult Result, string Message)
{
    /// <summary>Files that refused for what looks like a permissions problem.</summary>
    public IReadOnlyList<string> AccessDeniedPaths =>
        Result.Failures.Where(f => f.AccessDenied).Select(f => f.Path).ToList();
}

public class MainViewModel : ObservableObject
{
    private readonly string _catalogPath = CatalogStore.DefaultPath;
    private readonly string _toolSettingsPath = ToolSettings.DefaultPath;
    private readonly string _settingsPath = AppSettings.DefaultPath;
    private readonly string _sessionPath = ScanSession.DefaultPath;
    private readonly string _tmdbCachePath = AppPaths.TmdbCachePath;
    private Catalog _catalog;
    private ToolSettings _toolSettings;
    private AppSettings _settings;
    private ExternalTools _tools;
    private ScanSession _session;
    private TmdbCache _tmdbCache;
    private readonly ImdbTitleIndex _imdb = new(AppPaths.ImdbDataPath);
    private NewFileWatcher? _watcher;
    private List<string> _lastRoots = new();
    private readonly List<FileRow> _allRows = new();

    /// <summary>
    /// Exact-duplicate sets by content hash, rebuilt with the rows. Grouping the whole
    /// catalogue costs a pass over every file, and the duplicate manager asks for a group
    /// each time it reloads — so it is worked out once and looked up thereafter.
    /// </summary>
    private readonly Dictionary<string, DuplicateGroup> _duplicateGroups = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Files that claim to be the same content without being the same bytes — the same film
    /// downloaded twice from two different releases. Worked out with the rows, like the
    /// exact duplicates above, since both answer questions the grid asks of every file.
    /// </summary>
    private List<TitleDuplicateGroup> _titleDuplicates = new();

    private CancellationTokenSource? _cts;
    private bool _isPausing;
    private int _lastDone;
    private int _lastTotal;

    private string _statusText = "Ready.";
    private string _summaryText = "";
    private string _toolStatus = "";
    private string _etaText = "";
    private int _progressValue;
    private int _progressMax = 1;
    private bool _isScanning;
    private bool _canResume;
    private FilterMode _selectedFilter = FilterMode.All;
    private string _filterColumn = "Name";
    private string _filterPattern = "";

    public MainViewModel()
    {
        _catalog = CatalogStore.Load(_catalogPath);
        _toolSettings = ToolSettings.Load(_toolSettingsPath);
        _settings = AppSettings.Load(_settingsPath);
        _tools = ExternalTools.Resolve(_toolSettings);
        _session = ScanSession.Load(_sessionPath);
        _tmdbCache = TmdbCache.Load(_tmdbCachePath);
        UpdateToolStatus();

        // The Scan button opens the wizard; the window supplies it, since choosing what to
        // scan is a conversation rather than a command.
        ScanCommand = new RelayCommand(() => ScanRequested?.Invoke(), () => !IsScanning);
        ResumeCommand = new RelayCommand(() => ResumeRequested?.Invoke(),
            () => !IsScanning && CanResume);
        PauseCommand = new RelayCommand(() => { _isPausing = true; _cts?.Cancel(); }, () => IsScanning);
        CancelCommand = new RelayCommand(() => { _isPausing = false; _cts?.Cancel(); }, () => IsScanning);

        RestoreFilters();
        RefreshFilterValues();

        CanResume = _session.IsResumable;
        if (CanResume)
        {
            var how = _session.Status == ScanSessionStatus.Paused ? "Paused" : "Interrupted";
            StatusText = $"{how} scan can be resumed: {_session.LastDone}/{_session.LastTotal} done " +
                         $"on {_session.Roots.Count} drive(s). Click Resume to continue.";
        }
        else
        {
            // Nudge the one-off upgrade job, since nothing else would make it obvious that
            // an existing catalogue has work waiting that costs no re-scanning.
            var stale = StaleEntryCount;
            var rules = PendingFolderRuleCount;
            if (stale > 0 || rules > 0)
                StatusText = $"{stale} catalogue entr(ies) can be re-derived with this version's rules" +
                             (rules > 0 ? $", and {rules} folder rule(s) written onto their files" : "") +
                             " — click Refresh catalogue. No re-scanning or re-hashing is involved.";
        }

        RebuildRows();
        StartWatchingIfEnabled(_lastRoots); // resumes watching if enabled in settings
    }

    public ObservableCollection<FileRow> Files { get; } = new();

    public Array FilterModes => Enum.GetValues(typeof(FilterMode));

    public ICommand ScanCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand CancelCommand { get; }

    /// <summary>Raised when the user asks to scan; the window opens the wizard.</summary>
    public Action? ScanRequested { get; set; }

    /// <summary>Raised when the user asks to resume, so missing drives can be raised first.</summary>
    public Action? ResumeRequested { get; set; }

    /// <summary>The drives available to scan right now.</summary>
    public static IReadOnlyList<ScanRoot> AvailableDrives() => DriveScanner.GetAvailableDrives();

    /// <summary>True when nothing has been catalogued yet, so the wizard is the way in.</summary>
    public bool CatalogIsEmpty => _catalog.Files.Count == 0;

    /// <summary>How many entries the catalogue holds, whether or not they are on show.</summary>
    public int CataloguedFileCount => _catalog.Files.Count;

    /// <summary>The roots a resumable session would continue with.</summary>
    public IReadOnlyList<string> SessionRoots => _session.Roots;

    /// <summary>Session drives that are not attached at the moment.</summary>
    public IReadOnlyList<string> UnavailableSessionRoots() =>
        ScanEngine.UnavailableRoots(_session.Roots);

    /// <summary>True when a paused scan can be resumed.</summary>
    public bool CanResume
    {
        get => _canResume;
        set { if (SetProperty(ref _canResume, value)) RaiseCommandStates(); }
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string SummaryText
    {
        get => _summaryText;
        set => SetProperty(ref _summaryText, value);
    }

    public string ToolStatus
    {
        get => _toolStatus;
        set => SetProperty(ref _toolStatus, value);
    }

    /// <summary>Estimated time remaining for the running operation, e.g. "~4 min left".</summary>
    public string EtaText
    {
        get => _etaText;
        set => SetProperty(ref _etaText, value);
    }

    /// <summary>The last ten reversible operations.</summary>
    public UndoStack Undo { get; } = new(capacity: 10);

    /// <summary>Shows a notification; wired to the tray icon by the window.</summary>
    public Action<string, string>? Notify { get; set; }

    public bool CanDoVideo => _tools.CanDoVideo;
    public bool CanDoAudio => _tools.CanDoAudio;
    public bool CanAnalyze => _tools.CanDoVideo || _tools.CanDoAudio;

    public int ProgressValue
    {
        get => _progressValue;
        set => SetProperty(ref _progressValue, value);
    }

    public int ProgressMax
    {
        get => _progressMax;
        set => SetProperty(ref _progressMax, value);
    }

    public bool IsScanning
    {
        get => _isScanning;
        set
        {
            if (SetProperty(ref _isScanning, value))
            {
                RaiseCommandStates();
                OnPropertyChanged(nameof(IsIdle));
            }
        }
    }

    public bool IsIdle => !IsScanning;

    public FilterMode SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (!SetProperty(ref _selectedFilter, value)) return;
            ApplyFilter();
            SaveFilters();
        }
    }

    // --- Wildcard column filter ---
    public static readonly string[] FilterColumns =
    {
        "Name", "Kind", "Category", "Title", "Year", "S/E", "Size", "Length", "Quality",
        "Integrity", "Path", "Dup", "TMDb", "Filed"
    };

    public Array Columns => FilterColumns;

    /// <summary>
    /// What the user picks to mean "this column is empty here". Some of the most useful
    /// filters in the grid are about a blank cell — every file that is *not* a duplicate,
    /// every title nothing has confirmed — and an empty filter box cannot say that, because
    /// an empty box is how you say "no filter at all".
    /// </summary>
    public const string BlankFilterToken = "(blank)";

    /// <summary>
    /// The values a column can actually hold, for the columns that hold a fixed few. Typing
    /// "~dup" or "TvExtra" correctly from memory is not a reasonable thing to ask of anyone,
    /// so those columns offer their values instead. Columns with open-ended contents — a
    /// name, a path, a title — come back empty and are typed into as before.
    /// </summary>
    public IReadOnlyList<string> ValuesFor(string column) => column switch
    {
        "Dup" => new[] { BlankFilterToken, "DUP", "~dup", "title" },
        "Kind" => Enum.GetNames<MediaKind>(),
        "Filed" => new[] { "yes", "no" },
        "Integrity" => Enum.GetNames<IntegrityStatus>(),
        "TMDb" => new[] { BlankFilterToken, "✓", "✎" },
        "Category" => Categories.ToArray(),
        _ => Array.Empty<string>()
    };

    /// <summary>The values offered for the column the filter box is pointed at.</summary>
    public ObservableCollection<string> FilterValues { get; } = new();

    /// <summary>True when this column offers a list to pick from rather than free text.</summary>
    public bool FilterHasFixedValues => FilterValues.Count > 0;

    private void RefreshFilterValues()
    {
        var values = ValuesFor(_filterColumn);
        if (FilterValues.SequenceEqual(values, StringComparer.Ordinal)) return;

        // Emptying the list drops the combo box's selection, and an editable combo box
        // clears its text along with it — so what was typed is put back afterwards rather
        // than lost to a rebuild that had nothing to do with it.
        var wanted = _filterPattern;

        FilterValues.Clear();
        foreach (var value in values) FilterValues.Add(value);
        OnPropertyChanged(nameof(FilterHasFixedValues));

        // A pattern left over from the last column is nearly always meaningless against
        // this one — "Blade Runner" is not a value the Kind column has ever held.
        var keep = values.Count > 0 && !values.Contains(wanted, StringComparer.OrdinalIgnoreCase)
            ? ""
            : wanted;

        // Announced whether or not it changed here, since the combo box may have cleared
        // its own text on the rebuild above and needs telling what the value really is.
        _filterPattern = keep;
        OnPropertyChanged(nameof(FilterPattern));
    }

    private bool _filterNegate;

    public string FilterColumn
    {
        get => _filterColumn;
        set
        {
            if (!SetProperty(ref _filterColumn, value)) return;
            RefreshFilterValues();
            ApplyFilter();
        }
    }

    public string FilterPattern
    {
        get => _filterPattern;
        set { if (SetProperty(ref _filterPattern, value)) ApplyFilter(); }
    }

    public bool FilterNegate
    {
        get => _filterNegate;
        set { if (SetProperty(ref _filterNegate, value)) ApplyFilter(); }
    }

    /// <summary>
    /// Committed filter clauses. Every one of them has to match, whichever column it is on,
    /// so they stack across columns as well as within one: year and then name, or path and
    /// then Season/Episode. Each may be negated on its own.
    /// </summary>
    public ObservableCollection<FilterClause> ActiveFilters { get; } = new();

    /// <summary>True when anything is stacked, so the bar of chips is worth showing at all.</summary>
    public bool HasActiveFilters => ActiveFilters.Count > 0;

    /// <summary>Commit the current column/pattern/negate as a stacked filter clause.</summary>
    public void AddCurrentFilter()
    {
        if (string.IsNullOrEmpty(_filterPattern)) return;

        var clause = new FilterClause
        {
            Column = _filterColumn, Pattern = _filterPattern, Negate = _filterNegate
        };

        // The same clause twice narrows nothing and only clutters the bar.
        if (ActiveFilters.Any(f => f.Column == clause.Column && f.Pattern == clause.Pattern &&
                                   f.Negate == clause.Negate))
        {
            FilterPattern = "";
            return;
        }

        ActiveFilters.Add(clause);
        OnPropertyChanged(nameof(HasActiveFilters));
        FilterPattern = ""; // clears and re-applies
        SaveFilters();
    }

    public void RemoveFilter(FilterClause clause)
    {
        ActiveFilters.Remove(clause);
        OnPropertyChanged(nameof(HasActiveFilters));
        ApplyFilter();
        SaveFilters();
    }

    public void ClearFilters()
    {
        ActiveFilters.Clear();
        OnPropertyChanged(nameof(HasActiveFilters));
        FilterPattern = "";
        ApplyFilter();
        SaveFilters();
    }

    /// <summary>Put back the view and filters the last session closed with.</summary>
    private void RestoreFilters()
    {
        if (!_settings.RememberFilters) return;

        // Set the fields rather than the properties: the rows do not exist yet, and
        // saving here would simply write back what we are reading.
        if (Enum.TryParse<FilterMode>(_settings.LastFilterMode, out var mode))
            _selectedFilter = mode;
        if (FilterColumns.Contains(_settings.LastFilterColumn))
            _filterColumn = _settings.LastFilterColumn;
        _filterPattern = _settings.LastFilterPattern;
        _filterNegate = _settings.LastFilterNegate;

        foreach (var f in _settings.SavedFilters)
        {
            if (string.IsNullOrWhiteSpace(f.Column) || string.IsNullOrWhiteSpace(f.Pattern)) continue;
            ActiveFilters.Add(new FilterClause
            {
                Column = f.Column, Pattern = f.Pattern, Negate = f.Negate
            });
        }
    }

    /// <summary>
    /// Remember the view, the filter box and the committed filters. Called whenever they
    /// change rather than only at shutdown, so they survive however the app ends.
    /// </summary>
    public void SaveFilters()
    {
        if (!_settings.RememberFilters) return;

        _settings.LastFilterMode = SelectedFilter.ToString();
        _settings.LastFilterColumn = _filterColumn;
        _settings.LastFilterPattern = _filterPattern;
        _settings.LastFilterNegate = _filterNegate;
        _settings.SavedFilters = ActiveFilters
            .Select(f => new SavedFilter { Column = f.Column, Pattern = f.Pattern, Negate = f.Negate })
            .ToList();
        _settings.Save(_settingsPath);
    }

    // --- Progress & ETA ---------------------------------------------------

    private DateTime _operationStartedUtc;

    /// <summary>Begin timing an operation so <see cref="EtaText"/> can be estimated.</summary>
    private void BeginTiming()
    {
        _operationStartedUtc = DateTime.UtcNow;
        EtaText = "";
    }

    /// <summary>
    /// Update the estimate from the fraction completed. Work done so far sets the pace;
    /// nothing is shown until enough has happened for the figure to mean anything.
    /// </summary>
    private void UpdateEta(double done, double total)
    {
        if (total <= 0 || done <= 0) { EtaText = ""; return; }

        var elapsed = DateTime.UtcNow - _operationStartedUtc;
        if (elapsed < TimeSpan.FromSeconds(3) || done >= total) { EtaText = ""; return; }

        var remaining = TimeSpan.FromTicks((long)(elapsed.Ticks / done * (total - done)));
        EtaText = remaining.TotalHours >= 1
            ? $"~{(int)remaining.TotalHours}h {remaining.Minutes:00}m left"
            : remaining.TotalMinutes >= 1
                ? $"~{(int)remaining.TotalMinutes} min left"
                : $"~{Math.Max(1, (int)remaining.TotalSeconds)} sec left";
    }

    private void EndTiming() => EtaText = "";

    /// <summary>
    /// Compose a progress line. Thousands of small files going past in a second make a
    /// trailing file name flicker the whole line about — the counter never lands in the
    /// same place twice — so the name can be moved to the front, where it pushes nothing
    /// around, or dropped entirely.
    ///
    /// Every operation that works through files a file at a time comes through here —
    /// scanning, verifying, hashing, moving, analysing — so the setting means the same
    /// thing wherever the user sees a file name go past.
    /// </summary>
    /// <param name="extra">
    /// Anything worth adding after the counter, such as how far through the current file a
    /// slow job has reached. Always sits with the counter rather than with the name, so it
    /// does not move about as names change length.
    /// </param>
    private string ProgressLine(
        string phase, int done, int total, string currentFile, string extra = "")
    {
        var counted = total > 0 ? $"{phase}: {done}/{total}" : phase;
        if (extra.Length > 0) counted += " " + extra;
        if (string.IsNullOrEmpty(currentFile)) return counted;

        return _settings.ProgressNamePosition switch
        {
            ProgressNamePosition.Hidden => counted,
            ProgressNamePosition.Left => $"{currentFile}  —  {counted}",
            _ => $"{counted} — {currentFile}"
        };
    }

    private static bool Matches(FileRow row, FilterClause clause)
    {
        var value = row.ColumnValue(clause.Column);
        var m = string.Equals(clause.Pattern, BlankFilterToken, StringComparison.OrdinalIgnoreCase)
            ? string.IsNullOrWhiteSpace(value)
            : WildcardMatcher.IsMatch(value, clause.Pattern);
        return clause.Negate ? !m : m;
    }

    /// <summary>Built-in + custom categories, for the "set category" menus.</summary>
    public IReadOnlyList<string> Categories => CategoryResolver.All(_settings);

    public AppSettings Settings => _settings;

    private IReadOnlyList<string> _missingFiles = Array.Empty<string>();
    public IReadOnlyList<string> MissingFiles
    {
        get => _missingFiles;
        set { if (SetProperty(ref _missingFiles, value)) OnPropertyChanged(nameof(HasMissingFiles)); }
    }
    public bool HasMissingFiles => _missingFiles.Count > 0;

    private IReadOnlyList<MediaFile> _unhashedFiles = Array.Empty<MediaFile>();

    /// <summary>
    /// Files the last scan found but could not read to hash. Surfaced rather than left
    /// quiet: without a hash they are invisible to duplicate detection.
    /// </summary>
    public IReadOnlyList<MediaFile> UnhashedFiles
    {
        get => _unhashedFiles;
        set { if (SetProperty(ref _unhashedFiles, value)) OnPropertyChanged(nameof(HasUnhashedFiles)); }
    }

    public bool HasUnhashedFiles => _unhashedFiles.Count > 0;

    /// <summary>Raised when a scan ends having left files it could not hash.</summary>
    public Action? UnhashedFilesFound { get; set; }

    // --- Scan scope -------------------------------------------------------

    /// <summary>
    /// Whether a scan looks for everything, only video, or only audio. Saved as it
    /// changes, and never prunes what it wasn't looking for — so audio and video scans
    /// accumulate into one catalogue.
    /// </summary>
    public ScanMediaFilter ScanFilter
    {
        get => _settings.ScanMediaFilter;
        set
        {
            if (_settings.ScanMediaFilter == value) return;
            _settings.ScanMediaFilter = value;
            _settings.Save(_settingsPath);
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Start the scan the wizard described: over the chosen drives and folders, either
    /// adding to what is already catalogued or beginning again from nothing.
    /// </summary>
    public async Task<string> StartScanAsync(ScanPlan plan)
    {
        if (IsScanning) return "A scan is already running.";

        var roots = plan.Roots.Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (roots.Count == 0)
        {
            StatusText = "Choose at least one drive or folder to scan.";
            return StatusText;
        }

        // Remember the choices so the wizard opens where it left off, and so the rest of
        // the program works to the same limits this scan did.
        _settings.ScanDrives = plan.Drives.ToList();
        _settings.ScanWizardCompleted = true;
        _settings.ScanMediaFilter = plan.MediaFilter;
        _settings.MinFileSizeBytes = plan.MinSizeBytes;
        _settings.MaxFileSizeBytes = plan.MaxSizeBytes;

        // The wizard's list replaces the remembered one rather than being merged into it:
        // a merge can only ever add, which is why a folder removed there used to come back.
        _settings.AdditionalScanFolders = plan.AllFolders
            .Concat(plan.Folders)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _settings.Save(_settingsPath);
        OnPropertyChanged(nameof(ScanFilter));

        if (plan.StartMode == ScanStartMode.StartFresh)
        {
            // A fresh start throws away everything previously known — including the
            // resumable session and the cached enumeration, which describe the old one.
            _catalog = new Catalog();
            _catalog.RebuildIndex();
            _duplicateGroups.Clear();
            Undo.Clear();
            CatalogStore.Save(_catalog, _catalogPath);
            ClearSession();
            EnumerationCache.Clear(AppPaths.EnumerationPath);
        }

        await RunScanAsync(roots, resuming: false, plan.WaitForMissingDrives);
        return StatusText;
    }

    /// <summary>Continue an interrupted scan, optionally waiting for a drive to reappear.</summary>
    public async Task<string> ResumeScanAsync(bool waitForMissingDrives)
    {
        if (IsScanning) return "A scan is already running.";
        if (!CanResume) return "No paused scan to resume.";
        await RunScanAsync(_session.Roots.ToList(), resuming: true, waitForMissingDrives);
        return StatusText;
    }

    /// <summary>
    /// Run (or resume) a scan over <paramref name="roots"/>. Pause and cancel both stop
    /// the scan at a clean boundary; pause additionally saves a resumable session.
    /// </summary>
    private async Task RunScanAsync(List<string> roots, bool resuming, bool waitForMissingDrives = false)
    {
        if (roots.Count == 0)
        {
            StatusText = resuming
                ? "No paused scan to resume."
                : "Select at least one drive to scan.";
            return;
        }

        var resumeFromIndex = resuming ? _session.NextIndex : 0;

        _cts = new CancellationTokenSource();
        _isPausing = false;
        IsScanning = true;
        CanResume = false;
        ProgressValue = 0;
        ProgressMax = 1;
        _lastDone = resumeFromIndex;
        _lastTotal = 0;

        var progress = new Progress<ScanProgress>(p =>
        {
            _lastDone = p.Done;
            _lastTotal = p.Total;
            ProgressMax = Math.Max(1, p.Total);
            ProgressValue = p.Done;
            StatusText = ProgressLine(p.Phase, p.Done, p.Total, p.CurrentFile);
        });

        // Checkpoint the catalogue AND the session (as Running) so a crash mid-scan is
        // resumable, not just an explicit pause. 'index' is the resume position.
        void Checkpoint(int index)
        {
            CatalogStore.Save(_catalog, _catalogPath);
            SaveSession(ScanSessionStatus.Running, roots, index);
        }

        try
        {
            // Record an initial Running session immediately, so even an early crash resumes.
            SaveSession(ScanSessionStatus.Running, roots, resumeFromIndex);

            var engine = new ScanEngine(_catalog);
            var report = await Task.Run(() => engine.ScanAsync(
                roots, progress, Checkpoint, TimeSpan.FromSeconds(30),
                resume: resuming, resumeFromIndex: resumeFromIndex, settings: _settings,
                waitForMissingRoots: waitForMissingDrives, probeMedia: MediaProbeForScan(),
                ct: _cts.Token));

            CatalogStore.Save(_catalog, _catalogPath);
            ClearSession();
            _lastRoots = roots;
            StartWatchingIfEnabled(roots);
            MissingFiles = report.MissingFiles;
            UnhashedFiles = report.Unhashed;

            var notes = new List<string>();
            if (report.MissingFiles.Count > 0)
                notes.Add($"{report.MissingFiles.Count} file(s) could not be found");
            if (report.Unhashed.Count > 0)
                notes.Add($"{report.Unhashed.Count} could not be hashed");
            if (report.SkippedBySize > 0)
                notes.Add($"{report.SkippedBySize} skipped by the size limits");
            if (report.Unavailable.Count > 0)
                notes.Add($"{string.Join(", ", report.Unavailable)} never became available and " +
                          "were left untouched");

            StatusText = notes.Count > 0
                ? "Scan complete — " + string.Join(", ", notes) + "."
                : $"Scan complete. Catalogue saved to {_catalogPath}";

            // Files with no hash are invisible to duplicate detection, so they are put in
            // front of the user rather than left to be noticed.
            if (report.Unhashed.Count > 0) UnhashedFilesFound?.Invoke();
        }
        catch (OperationCanceledException)
        {
            CatalogStore.Save(_catalog, _catalogPath);
            if (_isPausing)
            {
                SaveSession(ScanSessionStatus.Paused, roots, _lastDone);
                CanResume = true;
                StatusText = $"Paused at {_lastDone}/{_lastTotal}. Work saved — click Resume to continue.";
            }
            else
            {
                ClearSession();
                EnumerationCache.Clear(AppPaths.EnumerationPath);
                StatusText = "Scan cancelled. Partial results saved.";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            _isPausing = false;
            IsScanning = false;
            _cts?.Dispose();
            _cts = null;
            RebuildRows();
        }
    }

    /// <summary>
    /// Scan one folder into the existing catalogue. Unlike a drive scan this never prunes:
    /// files elsewhere are simply out of scope, not gone. The folder is remembered so it
    /// can be rescanned with a click and is watched along with the drives.
    /// </summary>
    public async Task<string> ScanFolderAsync(string folder, bool remember = true)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist.";
        if (IsScanning) return "A scan is already running.";

        _cts = new CancellationTokenSource();
        IsScanning = true;
        BeginTiming();
        ProgressValue = 0;
        ProgressMax = 1;

        var before = _catalog.Files.Count;
        var progress = new Progress<ScanProgress>(p =>
        {
            ProgressMax = Math.Max(1, p.Total);
            ProgressValue = p.Done;
            UpdateEta(p.Done, p.Total);
            StatusText = ProgressLine(p.Phase, p.Done, p.Total, p.CurrentFile);
        });

        try
        {
            var engine = new ScanEngine(_catalog);
            await Task.Run(() => engine.ScanAsync(
                new[] { folder }, progress, onCheckpoint: null, checkpointInterval: null,
                resume: false, resumeFromIndex: 0, settings: _settings,
                pruneMissing: false, probeMedia: MediaProbeForScan(), ct: _cts.Token));

            if (remember &&
                !_settings.AdditionalScanFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
            {
                _settings.AdditionalScanFolders.Add(folder);
                _settings.Save(_settingsPath);
            }

            CatalogStore.Save(_catalog, _catalogPath);
            StatusText = $"Folder scan complete: {_catalog.Files.Count - before} new file(s) from {folder}.";
        }
        catch (OperationCanceledException)
        {
            CatalogStore.Save(_catalog, _catalogPath);
            StatusText = "Folder scan cancelled. Partial results saved.";
        }
        catch (Exception ex)
        {
            StatusText = $"Folder scan failed: {ex.Message}";
        }
        finally
        {
            EndTiming();
            IsScanning = false;
            _cts?.Dispose();
            _cts = null;
            RebuildRows();
        }
        return StatusText;
    }

    /// <summary>Folders scanned in addition to whole drives.</summary>
    public IReadOnlyList<string> ScanFolders => _settings.AdditionalScanFolders;

    public void ForgetScanFolder(string folder)
    {
        _settings.AdditionalScanFolders.RemoveAll(f =>
            string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        _settings.Save(_settingsPath);
    }

    private void SaveSession(ScanSessionStatus status, List<string> roots, int nextIndex)
    {
        _session = new ScanSession
        {
            Status = status,
            Roots = roots,
            NextIndex = nextIndex,
            LastDone = _lastDone,
            LastTotal = _lastTotal,
            UpdatedUtc = DateTime.UtcNow
        };
        _session.Save(_sessionPath);
    }

    private void ClearSession()
    {
        ScanSession.Clear(_sessionPath);
        _session = new ScanSession();
    }

    /// <summary>Rebuild the display rows from the catalogue and mark duplicates.</summary>
    private void RebuildRows()
    {
        _allRows.Clear();
        var rowByPath = new Dictionary<string, FileRow>(StringComparer.OrdinalIgnoreCase);
        var unnumbered = 0;

        foreach (var f in _catalog.Files)
        {
            // Hide files under excluded folders or with ignored extensions (they stay in
            // the catalogue but drop out of the results until a rescan prunes them).
            if (_settings.IsPathExcluded(f.FullPath) || _settings.IsExtensionIgnored(f.Extension))
                continue;

            var category = CategoryResolver.Effective(f, _settings);

            // A season and episode only mean something for a programme. On anything else
            // they were read out of a number that meant something else — a film called
            // "Apollo 13", a track numbered 104 — and are simply wrong, so they go the
            // moment the file is categorised as anything but television.
            if (MetadataNormaliser.StripNonTvNumbering(f, category)) unnumbered++;

            // Being filed is a fact about where the file is, so it is re-derived rather
            // than trusted: consolidation folders change, files get moved, and a corrected
            // title moves the goalposts — a file under the old title's folder is in the
            // library but no longer in the right place in it.
            f.Consolidated = ConsolidationPlanner.IsCorrectlyFiled(f, category, _settings);

            var row = new FileRow(f) { Category = category };
            _allRows.Add(row);
            rowByPath[f.FullPath] = row;
        }

        // Numbering taken off a file is a change to the catalogue, so it is written down
        // rather than re-derived on every redraw for the rest of time.
        if (unnumbered > 0) CatalogStore.Save(_catalog, _catalogPath);

        var groups = DuplicateFinder.FindExactDuplicates(_catalog.Files);
        _duplicateGroups.Clear();
        long reclaimable = 0;
        foreach (var g in groups)
        {
            _duplicateGroups[g.Sha256] = g;
            reclaimable += g.ReclaimableBytes;
            foreach (var f in g.Files)
                if (rowByPath.TryGetValue(f.FullPath, out var row))
                    row.IsDuplicate = true;
        }

        // Perceptual near-duplicates (only where fingerprints exist). Don't flag files
        // already marked as exact duplicates — DUP takes precedence over ~dup.
        var nearGroups = FingerprintMatcher.FindNearDuplicates(_catalog.Files);
        foreach (var g in nearGroups)
            foreach (var f in g.Files)
                if (rowByPath.TryGetValue(f.FullPath, out var row) && !row.IsDuplicate)
                    row.IsNearDuplicate = true;

        // Files that say they are the same thing without being the same bytes: the same
        // film downloaded twice from two different releases. Invisible to a hash, so they
        // are found by what the files claim about themselves instead.
        _titleDuplicates = TitleDuplicateFinder.Find(
            _catalog.Files, f => CategoryResolver.Effective(f, _settings));
        foreach (var g in _titleDuplicates)
            foreach (var f in g.Files)
                if (rowByPath.TryGetValue(f.FullPath, out var row)) row.IsTitleDuplicate = true;

        var video = _catalog.Files.Count(f => f.Kind == MediaKind.Video);
        var audio = _catalog.Files.Count(f => f.Kind == MediaKind.Audio);
        var nearPart = nearGroups.Count > 0 ? $"  •  {nearGroups.Count} near-dup sets" : "";
        var titlePart = _titleDuplicates.Count > 0
            ? $"  •  {_titleDuplicates.Count} same-title sets"
            : "";

        // Duplicate detection is by content hash, so anything unhashed is invisible to it.
        // Say so rather than quietly under-reporting.
        var unhashed = _catalog.Files.Count(f => !f.HasHash);
        var unhashedPart = unhashed > 0
            ? $"  •  ⚠ {unhashed} not hashed (use Re-hash pending)"
            : "";

        SummaryText =
            $"{_catalog.Files.Count} files  •  {video} video, {audio} audio  •  " +
            $"{groups.Count} duplicate sets ({Format.Bytes(reclaimable)} reclaimable)" +
            $"{nearPart}{titlePart}{unhashedPart}";

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        IEnumerable<FileRow> rows = _allRows;
        rows = SelectedFilter switch
        {
            FilterMode.Video => rows.Where(r => r.Model.Kind == MediaKind.Video),
            FilterMode.Audio => rows.Where(r => r.Model.Kind == MediaKind.Audio),
            FilterMode.Movies => rows.Where(r => r.Category == CategoryResolver.Movie),
            FilterMode.TvShows => rows.Where(r => r.Category == CategoryResolver.TvShow),
            FilterMode.Extras => rows.Where(r => CategoryResolver.IsExtra(r.Category)),
            FilterMode.Consolidated => rows.Where(r => r.Model.Consolidated),
            FilterMode.NotConsolidated => rows.Where(r => !r.Model.Consolidated),
            FilterMode.Duplicates => rows.Where(r => r.IsDuplicate),
            FilterMode.NearDuplicates => rows.Where(r => r.IsNearDuplicate),
            FilterMode.SameTitle => rows.Where(r => r.IsTitleDuplicate),
            FilterMode.UncertainYear => rows.Where(r => r.Model.YearAmbiguous),
            FilterMode.Problems => rows.Where(r =>
                r.Model.Integrity is IntegrityStatus.Corrupt or IntegrityStatus.IncompleteDownload),
            _ => rows
        };

        // Committed clauses (AND), then the live in-progress clause from the filter box.
        foreach (var clause in ActiveFilters)
            rows = rows.Where(r => Matches(r, clause));
        if (!string.IsNullOrEmpty(_filterPattern))
        {
            var live = new FilterClause { Column = _filterColumn, Pattern = _filterPattern, Negate = _filterNegate };
            rows = rows.Where(r => Matches(r, live));
        }

        Files.Clear();
        foreach (var r in rows.OrderBy(r => r.FullPath))
            Files.Add(r);
    }

    /// <summary>
    /// Copy-and-verify the given rows to <paramref name="destinationDir"/>, optionally
    /// deleting the originals only after a successful hash verification. One implementation
    /// with the plain move, so both get the progress, the ETA, the undo entry and the
    /// name-collision conversation.
    /// </summary>
    public Task<string> RelocateAsync(
        IReadOnlyList<FileRow> rows, string destinationDir, bool deleteOriginal) =>
        MoveFilesAsync(rows.Select(r => r.Model).ToList(), destinationDir, deleteOriginal);

    /// <summary>Build in-place rename proposals for the given rows (only ones that would change).</summary>
    public List<RenameProposal> BuildRenameProposals(IEnumerable<FileRow> rows) =>
        RenameService.BuildProposals(
                rows.Select(r => r.Model), f => CategoryResolver.Effective(f, _settings))
            .Where(p => p.WillChange)
            .ToList();

    /// <summary>Apply the chosen rename proposals on disk, then refresh and save.</summary>
    public async Task<string> ApplyRenamesAsync(IReadOnlyList<RenameProposal> proposals)
    {
        if (proposals.Count == 0) return "Nothing to rename.";

        IsScanning = true;
        int ok = 0, failed = 0;
        try
        {
            await Task.Run(() =>
            {
                foreach (var p in proposals)
                {
                    var result = RenameService.Apply(p);
                    if (result.Success) ok++; else failed++;
                }
            });
            CatalogStore.Save(_catalog, _catalogPath);
        }
        finally
        {
            IsScanning = false;
            RebuildRows();
        }

        var msg = $"Rename finished: {ok} renamed, {failed} failed.";
        StatusText = msg;
        return msg;
    }

    // --- Content analysis (fingerprints + deep integrity) -------------------

    public ToolSettings CurrentToolSettings => _toolSettings;

    /// <summary>Re-resolve tools after the user edits their paths, and persist.</summary>
    public void ApplyToolSettings(ToolSettings settings)
    {
        _toolSettings = settings;
        _toolSettings.Save(_toolSettingsPath);
        _tools = ExternalTools.Resolve(_toolSettings);
        UpdateToolStatus();
        OnPropertyChanged(nameof(CanDoVideo));
        OnPropertyChanged(nameof(CanDoAudio));
        OnPropertyChanged(nameof(CanAnalyze));
    }

    private void UpdateToolStatus()
    {
        string Mark(bool ok) => ok ? "✓" : "✗";
        ToolStatus =
            $"Tools: ffmpeg {Mark(_tools.HasFfmpeg)}  ffprobe {Mark(_tools.HasFfprobe)}  " +
            $"fpcalc {Mark(_tools.HasFpcalc)}";
    }

    // --- Length and quality ------------------------------------------------

    /// <summary>
    /// The thing a scan uses to read each file's length and quality, or null when there is
    /// nothing to read them with — no ffprobe, or the user has asked scans not to. Reading
    /// a container header is cheap next to hashing the file it belongs to, and entries that
    /// already know are skipped, so a rescan of a measured library costs nothing.
    /// </summary>
    private Func<MediaFile, CancellationToken, Task>? MediaProbeForScan()
    {
        if (!_settings.ProbeDuringScan || !_tools.HasFfprobe) return null;
        var probe = new MediaProbe(_tools);
        return async (file, ct) => await probe.ApplyAsync(file, ct);
    }

    /// <summary>
    /// Read the length, quality and container health of specific files — the single-file
    /// answer to the same question a scan asks of everything. Unlike a deep check this only
    /// reads the header, so it is a moment per file rather than minutes.
    /// </summary>
    public async Task<string> VerifyFilesAsync(IReadOnlyList<MediaFile> files)
    {
        if (files.Count == 0) return "Nothing selected.";
        if (!_tools.HasFfprobe)
            return "Reading length and quality needs ffprobe — set it up on the External tools tab first.";

        var targets = files.Where(f => f.Kind is MediaKind.Audio or MediaKind.Video).ToList();
        if (targets.Count == 0) return "None of the selected files is audio or video.";

        _cts = new CancellationTokenSource();
        IsScanning = true;
        BeginTiming();
        ProgressValue = 0;
        ProgressMax = targets.Count;

        var probe = new MediaProbe(_tools);
        var read = 0;
        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                _cts.Token.ThrowIfCancellationRequested();
                var file = targets[i];
                StatusText = ProgressLine("Verifying", i + 1, targets.Count, file.FileName);
                ProgressValue = i;
                UpdateEta(i, targets.Count);

                if (await probe.ApplyAsync(file, _cts.Token)) read++;
            }
            CatalogStore.Save(_catalog, _catalogPath);
            StatusText = $"Verified {read} file(s): length, quality and container read.";
        }
        catch (OperationCanceledException)
        {
            CatalogStore.Save(_catalog, _catalogPath);
            StatusText = $"Verification cancelled after {read} file(s). Progress saved.";
        }
        catch (Exception ex)
        {
            StatusText = $"Verification failed: {ex.Message}";
        }
        finally
        {
            EndTiming();
            IsScanning = false;
            _cts?.Dispose();
            _cts = null;
            RebuildRows();
        }
        return StatusText;
    }

    /// <summary>
    /// Run fingerprinting and/or a deep decode check over the given rows (or the whole
    /// catalogue if none supplied), then refresh near-duplicate grouping and save.
    /// </summary>
    public async Task<string> AnalyzeAsync(IReadOnlyList<FileRow> rows, bool deepCheck)
    {
        if (!CanAnalyze)
            return "No external tools found. Add ffmpeg/ffprobe (and fpcalc for audio) first.";

        var targets = (rows.Count > 0 ? rows.Select(r => r.Model) : _catalog.Files)
            .Where(f => f.Kind is MediaKind.Audio or MediaKind.Video)
            .ToList();
        if (targets.Count == 0) return "No audio/video files to analyse.";

        return await AnalyzeModelsAsync(targets, deepCheck);
    }

    /// <summary>
    /// Fingerprint and/or deep-check specific files, showing how far along it is and how
    /// long is left. The estimate is driven by bytes rather than file count: decoding a
    /// 20 GB remux and a 200 MB episode are not the same job.
    /// </summary>
    public async Task<string> AnalyzeModelsAsync(IReadOnlyList<MediaFile> targets, bool deepCheck)
    {
        if (targets.Count == 0) return "No audio/video files to analyse.";

        _cts = new CancellationTokenSource();
        IsScanning = true;
        BeginTiming();
        ProgressValue = 0;
        ProgressMax = 1000;

        var progress = new Progress<AnalysisProgress>(p =>
        {
            if (p.BytesTotal > 0)
            {
                ProgressValue = (int)Math.Min(1000, p.BytesDone * 1000 / p.BytesTotal);
                UpdateEta(p.BytesDone, p.BytesTotal);
            }
            else
            {
                ProgressMax = Math.Max(1, p.Total);
                ProgressValue = p.Done;
                UpdateEta(p.Done, p.Total);
            }
            StatusText = ProgressLine(
                deepCheck ? "Deep checking" : "Analysing", p.Done + 1, p.Total, p.CurrentFile,
                p.FileFraction > 0 ? $"({p.FileFraction:P0} of this file)" : "");
        });

        try
        {
            var engine = new ContentAnalysisEngine(_tools);
            await engine.AnalyzeAsync(targets, fingerprint: true, deepCheck: deepCheck, progress, _cts.Token);
            CatalogStore.Save(_catalog, _catalogPath);
            StatusText = "Analysis complete.";
        }
        catch (OperationCanceledException)
        {
            CatalogStore.Save(_catalog, _catalogPath);
            StatusText = "Analysis cancelled. Partial results saved.";
        }
        catch (Exception ex)
        {
            StatusText = $"Analysis failed: {ex.Message}";
        }
        finally
        {
            EndTiming();
            IsScanning = false;
            _cts?.Dispose();
            _cts = null;
            RebuildRows();
        }
        return StatusText;
    }

    /// <summary>
    /// Deep-check every media file under a folder, whether or not it is catalogued —
    /// anything new is added first, so the results are kept.
    /// </summary>
    public async Task<string> DeepCheckFolderAsync(string folder)
    {
        if (!CanDoVideo)
            return "Deep checks need FFmpeg and ffprobe. Add them under Tools… first.";
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist.";

        IsScanning = true;
        StatusText = $"Looking for media under {folder}…";
        List<string> paths;
        try
        {
            paths = await Task.Run(() => DriveScanner.EnumerateMediaFiles(
                new[] { folder }, CancellationToken.None,
                excludeDescent: _settings.IsDescentBlocked,
                ignoreExtension: _settings.IsExtensionIgnored).ToList());
        }
        finally { IsScanning = false; }

        if (paths.Count == 0) return "No media files found in that folder.";

        // Catalogue anything new so the check has somewhere to record its verdict.
        var targets = new List<MediaFile>();
        foreach (var path in paths)
        {
            if (!_catalog.ByPath.TryGetValue(path, out var entry))
            {
                var info = new FileInfo(path);
                entry = new MediaFile
                {
                    FullPath = path, FileName = info.Name, Extension = info.Extension,
                    SizeBytes = info.Length, LastModifiedUtc = info.LastWriteTimeUtc,
                    IndexedUtc = DateTime.UtcNow,
                    FeatureVersion = CatalogRefresher.CurrentFeatureVersion
                };
                MediaClassifier.Classify(entry, _settings);
                _catalog.Files.Add(entry);
                _catalog.ByPath[path] = entry;
            }
            if (entry.Kind is MediaKind.Audio or MediaKind.Video) targets.Add(entry);
        }
        _catalog.RebuildIndex();
        ExtraLinker.Link(_catalog.Files);

        return await AnalyzeModelsAsync(targets, deepCheck: true);
    }

    // --- Categories -------------------------------------------------------

    /// <summary>
    /// Set a category on the chosen files and on every exact duplicate of them, so the
    /// same content is never filed two different ways.
    /// </summary>
    public void SetCategoryForFiles(IReadOnlyList<FileRow> rows, string category)
    {
        if (rows.Count == 0) return;
        var targets = rows.Select(r => r.Model).ToList();
        var duplicates = DuplicatesOf(targets);
        var affected = targets.Concat(duplicates).ToList();

        // The numbering is snapshotted along with the category: calling a file a film takes
        // its season and episode off it, and undoing the one has to undo the other.
        var before = affected
            .Select(f => (File: f, f.CategoryOverride, f.Season, f.Episode, f.EpisodeEnd)).ToList();
        Undo.Push($"category '{category}' on {affected.Count} file(s)", () =>
        {
            foreach (var b in before)
            {
                b.File.CategoryOverride = b.CategoryOverride;
                b.File.Season = b.Season;
                b.File.Episode = b.Episode;
                b.File.EpisodeEnd = b.EpisodeEnd;
            }
            ExtraLinker.Link(_catalog.Files);
            PersistAndRefresh();
            return Task.FromResult($"Reverted the category '{category}'.");
        });

        foreach (var f in affected) f.CategoryOverride = category;

        // A season and episode belong to a programme. Categorised as anything else, they
        // were read out of a number that meant something else and are simply wrong.
        var unnumbered = MetadataNormaliser.StripNonTvNumbering(affected, _ => category);

        CatalogStore.Save(_catalog, _catalogPath);
        RebuildRows();

        StatusText = $"Set category '{category}' on {targets.Count} file(s)" +
                     (duplicates.Count > 0 ? $" and {duplicates.Count} duplicate(s)" : "") +
                     (unnumbered > 0 ? $"; season/episode cleared on {unnumbered}." : ".");
    }

    /// <summary>Other catalogue entries that are byte-identical to any of these files.</summary>
    private List<MediaFile> DuplicatesOf(IReadOnlyCollection<MediaFile> files)
    {
        var hashes = files.Where(f => f.HasHash).Select(f => f.Sha256)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (hashes.Count == 0) return new List<MediaFile>();

        var chosen = new HashSet<MediaFile>(files);
        return _catalog.Files
            .Where(f => f.HasHash && hashes.Contains(f.Sha256) && !chosen.Contains(f))
            .ToList();
    }

    // --- Titles -----------------------------------------------------------

    public string SetTitleForModels(IReadOnlyList<MediaFile> targets, string newTitle)
    {
        if (targets.Count == 0 || string.IsNullOrWhiteSpace(newTitle)) return "Nothing to update.";
        var title = newTitle.Trim();

        // The selected files and their byte-identical copies — and nothing else. Two files
        // can carry the same title and still be different things, so sharing a title is no
        // reason to be renamed together; sharing a hash is.
        var withDuplicates = targets.Concat(DuplicatesOf(targets)).Distinct().ToList();

        var snapshot = withDuplicates
            .Select(f => (File: f, f.TmdbName, f.TmdbVerified, f.ImdbVerified, f.TitleManuallySet, f.ParsedTitle))
            .ToList();

        // Read before the update, so a name carrying the old title can have it swapped for
        // the new one when the naming scheme declines to name the file itself.
        var wasCalled = withDuplicates.ToDictionary(f => f, f => f.EffectiveTitle);

        var changed = TitleUpdater.Set(withDuplicates, title, manual: true);

        // Extras take their title from what they hang off, so refresh the links.
        ExtraLinker.Link(_catalog.Files);

        // The name on disk follows the title. A corrected title that leaves the old name
        // in place is only half a correction — and the old name is what the next scan
        // would read the title back out of.
        var renamed = RenameToMatchTitles(withDuplicates,
            f => wasCalled.TryGetValue(f, out var was) ? was : null);

        CatalogStore.Save(_catalog, _catalogPath);
        RebuildRows();

        Undo.Push($"title '{title}' on {changed} file(s)", () =>
        {
            UndoRenames(renamed);
            foreach (var s in snapshot)
            {
                s.File.TmdbName = s.TmdbName;
                s.File.TmdbVerified = s.TmdbVerified;
                s.File.ImdbVerified = s.ImdbVerified;
                s.File.TitleManuallySet = s.TitleManuallySet;
                s.File.ParsedTitle = s.ParsedTitle;
            }
            ExtraLinker.Link(_catalog.Files);
            _catalog.RebuildIndex();
            PersistAndRefresh();
            return Task.FromResult($"Reverted the title '{title}'.");
        });

        StatusText = $"Title set to '{title}' on {changed} file(s)" +
                     (renamed.Count > 0 ? $"; {renamed.Count} file(s) renamed to match." : ".");
        return StatusText;
    }

    /// <summary>
    /// Rename files on disk so their names match the titles they now carry, following the
    /// naming scheme for each one's category. Returns what was renamed, oldest name first,
    /// so the change can be put back.
    /// </summary>
    /// <param name="previousTitleOf">
    /// What each file went by before. The naming scheme has nothing to offer for some
    /// categories — an extra, a file still filed as Other, a programme with no episode
    /// number — and for those the old title is swapped for the new one inside the name the
    /// file already has, which is the most a correction can honestly do without inventing
    /// a name nobody asked for. Null to skip that fallback.
    /// </param>
    private List<(MediaFile File, string PreviousPath, string PreviousName)> RenameToMatchTitles(
        IEnumerable<MediaFile> files, Func<MediaFile, string?>? previousTitleOf = null)
    {
        var renamed = new List<(MediaFile, string, string)>();
        if (!_settings.RenameOnTitleChange) return renamed;

        foreach (var file in files)
        {
            if (!File.Exists(file.FullPath)) continue;

            var proposal = RenameService.BuildProposal(file, CategoryResolver.Effective(file, _settings))
                           ?? RenameService.BuildTitleSwap(
                               file, previousTitleOf?.Invoke(file), file.EffectiveTitle);
            if (proposal is not { WillChange: true }) continue;

            var previousPath = file.FullPath;
            var previousName = file.FileName;
            if (RenameService.Apply(proposal).Success)
                renamed.Add((file, previousPath, previousName));
        }

        if (renamed.Count > 0) _catalog.RebuildIndex();
        return renamed;
    }

    /// <summary>Put renamed files back under the names they had.</summary>
    private void UndoRenames(IReadOnlyList<(MediaFile File, string PreviousPath, string PreviousName)> renamed)
    {
        foreach (var (file, previousPath, previousName) in renamed)
            RenameService.Apply(new RenameProposal
            {
                File = file,
                CurrentName = file.FileName,
                ProposedName = previousName,
                ProposedPath = previousPath
            });
    }

    /// <summary>
    /// Every field of a catalogue entry the user may correct, as the editor collected them.
    /// The file's own facts — its name, its date on disk, what a decode made of it — belong
    /// to that one file; what the content *is* belongs to every byte-identical copy of it.
    /// </summary>
    /// <param name="EpisodeEnd">
    /// The last episode of a double episode, when the file holds more than one. Null for
    /// the ordinary single-episode case.
    /// </param>
    public record FileEdits(
        string Title,
        int? Year,
        int? Season,
        int? Episode,
        int? EpisodeEnd,
        string Category,
        DateTime ModifiedUtc,
        IntegrityStatus Integrity,
        MediaKind Kind,
        string FileName);

    /// <summary>
    /// Apply a full set of corrections to one entry. The date is written to the file on
    /// disk as well as to the catalogue: left only in the catalogue, the next scan would
    /// read the old one back and treat the file as changed.
    /// </summary>
    public string ApplyFileEdits(MediaFile file, FileEdits edits)
    {
        var copies = new List<MediaFile> { file };
        copies.AddRange(CopiesOf(file));

        var before = copies.Select(f => (File: f, f.TmdbName, f.TmdbVerified, f.ImdbVerified,
            f.TitleManuallySet, f.Year, f.Season, f.Episode, f.EpisodeEnd,
            f.NumberingManuallySet, f.CategoryOverride, f.YearAmbiguous, f.AlternativeYear)).ToList();
        var previousModified = file.LastModifiedUtc;
        var previousIntegrity = file.Integrity;
        var previousKind = file.Kind;
        var previousPath = file.FullPath;
        var previousName = file.FileName;

        var changes = new List<string>();
        var title = (edits.Title ?? string.Empty).Trim();
        var previousTitle = file.EffectiveTitle.Trim();
        var titleChanged = title.Length > 0 &&
                           !string.Equals(title, previousTitle, StringComparison.Ordinal);
        var category = (edits.Category ?? string.Empty).Trim();

        // Numbering the user typed is theirs and stays: the editor asks about the category
        // when it does not fit, rather than quietly throwing the correction away.
        var numberingTyped = edits.Season != file.Season || edits.Episode != file.Episode ||
                             edits.EpisodeEnd != file.EpisodeEnd;

        // A year the user typed is not a guess, whatever the catalogue thought of it before:
        // the "could be the remake" warning is about a year we picked, not one they did.
        var yearTyped = edits.Year != file.Year;

        foreach (var copy in copies)
        {
            // Exactly what the old Edit title dialog did to a title, so the two agree now
            // that this is the only way to change one: a hand-typed title counts as
            // confirmed, and is recorded as the user's own rather than borrowing the credit
            // of a source that never saw it.
            if (titleChanged) TitleUpdater.Set(new[] { copy }, title, manual: true);

            copy.Year = edits.Year;
            if (yearTyped) { copy.YearAmbiguous = false; copy.AlternativeYear = null; }
            copy.Season = edits.Season;
            copy.Episode = edits.Episode;
            copy.EpisodeEnd = edits.EpisodeEnd;
            if (numberingTyped)
                copy.NumberingManuallySet = edits.Season is not null || edits.Episode is not null;
            copy.CategoryOverride = category;
        }
        if (titleChanged) changes.Add($"title '{title}'");
        if (numberingTyped && file.NumberingDisplay is { Length: > 0 } typed)
            changes.Add($"numbering {typed}");

        // A season and episode belong to a programme; on anything else they were read out
        // of a number that meant something else. Numbering typed in by hand is exempt —
        // see MetadataNormaliser.
        if (MetadataNormaliser.StripNonTvNumbering(copies, _ => category) > 0)
            changes.Add($"season/episode cleared — '{category}' is not a programme");

        file.Integrity = edits.Integrity;
        file.Kind = edits.Kind;
        if (previousIntegrity != edits.Integrity) changes.Add($"integrity {edits.Integrity}");
        if (previousKind != edits.Kind) changes.Add($"kind {edits.Kind}");

        // The date, on disk and in the catalogue, so the two agree.
        if (edits.ModifiedUtc != previousModified)
        {
            file.LastModifiedUtc = edits.ModifiedUtc;
            if (TrySetModified(file.FullPath, edits.ModifiedUtc))
                changes.Add($"date {edits.ModifiedUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
            else
                changes.Add($"date {edits.ModifiedUtc.ToLocalTime():yyyy-MM-dd HH:mm} (catalogue only — " +
                            "the file itself refused)");
        }

        // A name typed by hand wins outright; otherwise a changed title renames the file to
        // match, which is what the naming scheme is for and what correcting a title has
        // always meant. A title left on a file whose name still says the old one is only
        // half a correction — and the old name is what the next scan would read back.
        var renamed = new List<(MediaFile, string, string)>();
        var wanted = (edits.FileName ?? string.Empty).Trim();
        if (wanted.Length > 0 && !string.Equals(wanted, file.FileName, StringComparison.Ordinal))
        {
            var dir = Path.GetDirectoryName(file.FullPath) ?? string.Empty;
            var result = RenameService.Apply(new RenameProposal
            {
                File = file,
                CurrentName = file.FileName,
                ProposedName = wanted,
                ProposedPath = Path.Combine(dir, wanted)
            });
            if (result.Success)
            {
                renamed.Add((file, previousPath, previousName));
                changes.Add($"renamed to '{file.FileName}'");
            }
            else changes.Add($"could not rename: {result.Message}");
        }
        else if (titleChanged)
        {
            renamed = RenameToMatchTitles(copies, _ => previousTitle);
            if (renamed.Count > 0) changes.Add($"{renamed.Count} file(s) renamed to match");
        }

        ExtraLinker.Link(_catalog.Files);
        _catalog.RebuildIndex();
        CatalogStore.Save(_catalog, _catalogPath);
        RebuildRows();

        Undo.Push($"edits to '{previousName}'", () =>
        {
            UndoRenames(renamed);
            foreach (var b in before)
            {
                b.File.TmdbName = b.TmdbName;
                b.File.TmdbVerified = b.TmdbVerified;
                b.File.ImdbVerified = b.ImdbVerified;
                b.File.TitleManuallySet = b.TitleManuallySet;
                b.File.Year = b.Year;
                b.File.Season = b.Season;
                b.File.Episode = b.Episode;
                b.File.EpisodeEnd = b.EpisodeEnd;
                b.File.NumberingManuallySet = b.NumberingManuallySet;
                b.File.CategoryOverride = b.CategoryOverride;
                b.File.YearAmbiguous = b.YearAmbiguous;
                b.File.AlternativeYear = b.AlternativeYear;
            }
            file.Integrity = previousIntegrity;
            file.Kind = previousKind;
            file.LastModifiedUtc = previousModified;
            TrySetModified(file.FullPath, previousModified);
            ExtraLinker.Link(_catalog.Files);
            _catalog.RebuildIndex();
            PersistAndRefresh();
            return Task.FromResult($"Reverted the edits to '{previousName}'.");
        });

        StatusText = changes.Count == 0
            ? "Nothing was changed."
            : $"Updated {file.FileName}: " + string.Join(", ", changes) +
              (copies.Count > 1 ? $" (shared with {copies.Count - 1} identical copy(ies))." : ".");
        return StatusText;
    }

    private static bool TrySetModified(string path, DateTime utc)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.SetLastWriteTimeUtc(path, utc);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Everything a folder can be told about the programme or film in it. A field left null
    /// is not being changed; <see cref="ChangeYear"/> is what distinguishes "leave the year
    /// alone" from "the year is unknown, take it off".
    /// </summary>
    public record FolderDetails(string? Title, int? Year, bool ChangeYear, string? Category);

    /// <summary>
    /// Set title, year and category for everything under a folder in one go.
    ///
    /// The year is here because it is got wrong often enough to matter: a series whose file
    /// names carry the year of the season rather than of the show ends up filed under a year
    /// that is nobody's idea of right, and correcting that one episode at a time for a
    /// twelve-episode season is not a reasonable thing to ask.
    ///
    /// Written onto the files themselves rather than kept as a folder rule, like the title
    /// and category dialogs it replaces: everything known about a file belongs in the
    /// catalogue, where it travels with the file and survives a settings reset.
    /// </summary>
    public string SetFolderDetails(string folder, FolderDetails details, bool includeSubdirs)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "Choose a folder first.";

        var title = details.Title?.Trim();
        var category = details.Category?.Trim();
        var changingSomething = !string.IsNullOrWhiteSpace(title) || details.ChangeYear ||
                                !string.IsNullOrWhiteSpace(category);
        if (!changingSomething) return "Nothing was changed.";

        var affected = FilesUnder(folder, includeSubdirs);
        if (affected.Count == 0)
        {
            StatusText = $"No catalogued files under {folder}.";
            return StatusText;
        }

        // The folder's files and their identical copies wherever those live — but not files
        // elsewhere that merely share the old title, which may well be something else.
        var targets = affected.Concat(DuplicatesOf(affected)).Distinct().ToList();

        var snapshot = targets.Select(f => (File: f, f.TmdbName, f.TmdbVerified, f.ImdbVerified,
            f.TitleManuallySet, f.Year, f.YearAmbiguous, f.AlternativeYear, f.CategoryOverride,
            f.Season, f.Episode, f.EpisodeEnd)).ToList();
        var wasCalled = targets.ToDictionary(f => f, f => f.EffectiveTitle);

        var changes = new List<string>();

        if (!string.IsNullOrWhiteSpace(title))
        {
            var changed = TitleUpdater.Set(targets, title, manual: true);
            changes.Add($"title '{title}' on {changed} file(s)");

            // A rule for this folder left over from an earlier version is now redundant: the
            // files themselves say what they are called.
            if (_settings.FolderTitleRules.RemoveAll(r =>
                    string.Equals(r.Path, folder, StringComparison.OrdinalIgnoreCase)) > 0)
                _settings.Save(_settingsPath);
        }

        if (details.ChangeYear)
        {
            foreach (var file in targets)
            {
                file.Year = details.Year;
                // A year the user typed is not a guess, so the "could be the remake" mark
                // that a lookup left on it no longer applies.
                file.YearAmbiguous = false;
                file.AlternativeYear = null;
            }
            changes.Add(details.Year is { } y ? $"year {y}" : "year cleared");
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            foreach (var file in targets) file.CategoryOverride = category;
            var unnumbered = MetadataNormaliser.StripNonTvNumbering(targets, _ => category);
            changes.Add($"category '{category}'" +
                        (unnumbered > 0 ? $" (season/episode cleared on {unnumbered})" : ""));

            // A rule for this folder left over from an earlier version is now redundant.
            if (_settings.FolderCategoryRules.RemoveAll(r =>
                    string.Equals(r.Path, folder, StringComparison.OrdinalIgnoreCase)) > 0)
                _settings.Save(_settingsPath);
        }

        DuplicateMetadata.Propagate(_catalog.Files);
        ExtraLinker.Link(_catalog.Files);

        // Names on disk follow the title, as they do everywhere else a title is corrected.
        var renamed = string.IsNullOrWhiteSpace(title)
            ? new List<(MediaFile, string, string)>()
            : RenameToMatchTitles(targets, f => wasCalled.TryGetValue(f, out var was) ? was : null);

        _catalog.RebuildIndex();
        CatalogStore.Save(_catalog, _catalogPath);
        RebuildRows();

        Undo.Push($"folder details for '{Path.GetFileName(folder.TrimEnd('\\', '/'))}'", () =>
        {
            UndoRenames(renamed);
            foreach (var s in snapshot)
            {
                s.File.TmdbName = s.TmdbName;
                s.File.TmdbVerified = s.TmdbVerified;
                s.File.ImdbVerified = s.ImdbVerified;
                s.File.TitleManuallySet = s.TitleManuallySet;
                s.File.Year = s.Year;
                s.File.YearAmbiguous = s.YearAmbiguous;
                s.File.AlternativeYear = s.AlternativeYear;
                s.File.CategoryOverride = s.CategoryOverride;
                s.File.Season = s.Season;
                s.File.Episode = s.Episode;
                s.File.EpisodeEnd = s.EpisodeEnd;
            }
            ExtraLinker.Link(_catalog.Files);
            _catalog.RebuildIndex();
            PersistAndRefresh();
            return Task.FromResult("Reverted the folder details.");
        });

        StatusText = $"{folder}{(includeSubdirs ? " and its subfolders" : "")}: " +
                     string.Join(", ", changes) +
                     (renamed.Count > 0 ? $"; {renamed.Count} file(s) renamed to match." : ".");
        return StatusText;
    }

    /// <summary>
    /// What a folder already says about itself, for seeding the folder-details dialog: the
    /// title, year and category its files agree on, or null where they do not.
    /// </summary>
    public FolderDetails DetailsOf(string folder, bool includeSubdirs)
    {
        var files = FilesUnder(folder, includeSubdirs);
        if (files.Count == 0) return new FolderDetails(null, null, false, null);

        string? Agreed<T>(Func<MediaFile, T> of, Func<T, string?> show)
        {
            var values = files.Select(of).Distinct().ToList();
            return values.Count == 1 ? show(values[0]) : null;
        }

        var title = Agreed(f => f.EffectiveTitle, v => string.IsNullOrWhiteSpace(v) ? null : v);
        var year = Agreed(f => f.Year, v => v?.ToString());
        var category = Agreed(f => CategoryResolver.Effective(f, _settings), v => v);

        return new FolderDetails(
            title,
            int.TryParse(year, out var parsed) ? parsed : null,
            ChangeYear: false,
            category);
    }

    /// <summary>Catalogued files inside a folder (and optionally everything below it).</summary>
    private List<MediaFile> FilesUnder(string folder, bool includeSubdirs)
    {
        var root = folder.TrimEnd('\\', '/');
        return _catalog.Files
            .Where(f => includeSubdirs
                ? f.FullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                : string.Equals(Path.GetDirectoryName(f.FullPath), root, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public void AddCustomCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category)) return;
        if (!_settings.CustomCategories.Contains(category, StringComparer.OrdinalIgnoreCase) &&
            !CategoryResolver.BuiltIn.Contains(category, StringComparer.OrdinalIgnoreCase))
        {
            _settings.CustomCategories.Add(category);
            _settings.Save(_settingsPath);
            OnPropertyChanged(nameof(Categories));
        }
    }

    // --- Exclusions -------------------------------------------------------

    /// <summary>
    /// Put the rules a new exclusion would make redundant to the user, and hand back the
    /// ones they chose to drop — all, some or none. Set by the window; only consulted when
    /// the policy is to ask.
    /// </summary>
    public Func<IReadOnlyList<ExcludedFolder>, IReadOnlyList<ExcludedFolder>>?
        ConfirmRedundantExclusions { get; set; }

    public void ExcludeFolder(string folder, bool includeSubdirs)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        _settings.ExcludedFolders.RemoveAll(f =>
            string.Equals(f.Path, folder, StringComparison.OrdinalIgnoreCase));

        var candidate = new ExcludedFolder { Path = folder, IncludeSubdirectories = includeSubdirs };
        var pruned = AppSettings.PruneSuperseded(
            _settings.ExcludedFolders, candidate,
            _settings.RedundantExclusions, ConfirmRedundantExclusions);

        _settings.ExcludedFolders.Add(candidate);
        _settings.Save(_settingsPath);
        RebuildRows();

        StatusText = $"Excluded folder: {folder}{(includeSubdirs ? " (and subfolders)" : "")}" +
                     (pruned.Count > 0 ? $"; {pruned.Count} rule(s) it already covered were removed." : ".");
    }

    public void IgnoreExtension(string extension)
    {
        _settings.IgnoreExtension(extension);
        _settings.Save(_settingsPath);
        RebuildRows();
        StatusText = $"Ignoring '{extension}' files (removed from results and future scans).";
    }

    // --- Duplicates -------------------------------------------------------

    /// <summary>The exact-duplicate group a file belongs to, or null if it has none.</summary>
    public DuplicateGroup? DuplicateGroupFor(MediaFile file) =>
        DuplicateGroupBySha(file.Sha256);

    public DuplicateGroup? DuplicateGroupBySha(string sha) =>
        !string.IsNullOrEmpty(sha) && _duplicateGroups.TryGetValue(sha, out var group)
            ? group
            : null;

    /// <summary>
    /// Sets of files claiming to be the same content without being the same bytes. Worked
    /// out with the rows, so asking for them is a lookup rather than a pass over the
    /// catalogue.
    /// </summary>
    public IReadOnlyList<TitleDuplicateGroup> TitleDuplicateGroups() => _titleDuplicates;

    /// <summary>True when anything in the catalogue has a same-title twin.</summary>
    public bool HasTitleDuplicates => _titleDuplicates.Count > 0;

    /// <summary>The same-title set a file belongs to, or null when it has no twin.</summary>
    public TitleDuplicateGroup? TitleDuplicateGroupFor(MediaFile file) =>
        _titleDuplicates.FirstOrDefault(g => g.Files.Any(f => ReferenceEquals(f, file)));

    /// <summary>Every other catalogued copy of this exact file.</summary>
    public List<MediaFile> CopiesOf(MediaFile file) =>
        DuplicateGroupFor(file)?.Files.Where(f => !ReferenceEquals(f, file)).ToList()
        ?? new List<MediaFile>();

    /// <summary>The catalogue entry for a path, when there is one.</summary>
    public MediaFile? EntryAt(string path) =>
        !string.IsNullOrEmpty(path) && _catalog.ByPath.TryGetValue(path, out var f) ? f : null;

    /// <summary>Copy/move specific catalogue entries (used by the duplicate manager).</summary>
    public Task<string> RelocateModelsAsync(IReadOnlyList<MediaFile> files, string dir, bool delete) =>
        MoveFilesAsync(files, dir, delete);

    public void PersistAndRefresh()
    {
        CatalogStore.Save(_catalog, _catalogPath);
        RebuildRows();
    }

    /// <summary>
    /// Remove entries from the catalogue/results without touching the files on disk.
    /// (A later scan of the same location will re-add them unless excluded.)
    /// </summary>
    public void RemoveFromResults(IReadOnlyList<FileRow> rows)
    {
        if (rows.Count == 0) return;
        var models = new HashSet<MediaFile>(rows.Select(r => r.Model));
        _catalog.Files.RemoveAll(models.Contains);
        _catalog.RebuildIndex();
        CatalogStore.Save(_catalog, _catalogPath);
        RebuildRows();
        StatusText = $"Removed {rows.Count} file(s) from results (files left on disk).";
    }

    // --- Consolidation ----------------------------------------------------

    /// <summary>
    /// The specials/featurettes attached to these files, which travel with them.
    /// </summary>
    public List<MediaFile> LinkedExtras(IEnumerable<MediaFile> files)
    {
        var ids = files.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0) return new List<MediaFile>();
        return _catalog.Files
            .Where(f => f.IsExtra && !string.IsNullOrEmpty(f.LinkedFileId) && ids.Contains(f.LinkedFileId))
            .ToList();
    }

    /// <summary>
    /// Move (or copy) selected TV/film files into the structured consolidation folders,
    /// bringing their extras along. Files whose category/target isn't set are skipped, and
    /// files whose copy is already in the library are reported rather than duplicated.
    /// </summary>
    public async Task<ConsolidationOutcome> ConsolidateAsync(
        IReadOnlyList<FileRow> rows, bool deleteOriginal)
    {
        var present = new List<MediaFile>();
        if (!_settings.HasAnyConsolidationFolder)
            return new ConsolidationOutcome(0, 0, 0, present,
                "Set a consolidation folder for at least one category in Settings first.");

        var selected = rows.Select(r => r.Model).ToList();
        var files = selected.Concat(LinkedExtras(selected)).Distinct().ToList();
        var outcome = await ConsolidateModelsAsync(files, deleteOriginal);

        var extras = files.Count - selected.Count;
        if (extras > 0)
            StatusText = outcome.Message + $" ({extras} linked extra(s) included.)";
        return outcome;
    }

    /// <summary>
    /// Asked when the episode being consolidated is already in the library under a
    /// different name. Set by the window; left unset, the library's copy is kept and the
    /// arrival is left where it is, which is the answer that cannot lose anything.
    /// </summary>
    public Func<EpisodeConflict, Task<CollisionResolution>>? EpisodeConflictResolver { get; set; }

    /// <summary>
    /// Consolidate specific catalogue entries: each goes to the folder its category
    /// dictates, under its planned name, with progress and an ETA.
    /// </summary>
    public async Task<ConsolidationOutcome> ConsolidateModelsAsync(
        IReadOnlyList<MediaFile> files, bool deleteOriginal)
    {
        // A file already sitting at the exact path the layout gives it has nothing to do:
        // consolidating it again is how a second copy of it gets made. It is reported as
        // already consolidated instead, and the caller can offer to tidy up its copies.
        //
        // Sitting *somewhere* under the library is not the same thing. A file whose title
        // has since been corrected is under the old title's folder, and the plan below
        // moves it to the new one — which is exactly what re-consolidating should mean.
        // A file already inside the library but under a wrongly named folder does not want
        // copying anywhere: it wants its folder put right. Renaming the folder moves every
        // file in it at once, costs nothing whatever it holds, and — unlike copying the
        // files out one at a time — leaves no empty folder standing behind.
        var folderMoves = MoveMisfiledFolders(files);

        var alreadyConsolidated = new List<MediaFile>();
        var toFile = new List<MediaFile>();
        foreach (var file in files)
        {
            if (ConsolidationPlanner.IsAtPlannedPath(file, CategoryResolver.Effective(file, _settings), _settings))
                alreadyConsolidated.Add(file);
            else
                toFile.Add(file);
        }

        // An episode already in the library under a different name is not a name collision
        // and would sail straight past every check below — leaving the library holding the
        // same episode twice, which is the one thing a consolidation location exists to
        // prevent. So it is settled before anything moves.
        var episodes = await ResolveEpisodeConflictsAsync(toFile);

        // Files the run declined to move, which are failures as far as the loop counting
        // them is concerned but are not failures to report as such.
        var unfiled = 0;
        var origins = toFile.Select(f => (File: f, f.FullPath, f.FileName)).ToList();
        var run = new CollisionRun("consolidated");
        if (episodes.Cancelled) run.Cancelled = true;

        var report = toFile.Count == 0
            ? new OperationReport(0, 0, new List<MediaFile>())
            : await RunFileOperationAsync(toFile, "Consolidating", async (file, bytes) =>
            {
                if (run.Cancelled)
                    return new RelocationResult(false, "Cancelled.", file.FullPath);

                var category = CategoryResolver.Effective(file, _settings);
                var destDir = ConsolidationPlanner.PlanDirectory(file, category, _settings);
                if (destDir == null)
                {
                    unfiled++;
                    return new RelocationResult(false,
                        $"'{file.FileName}' has no category with a consolidation folder set, " +
                        "so there is nowhere to file it.", file.FullPath);
                }

                var name = ConsolidationPlanner.PlanFileName(file, category, _settings);
                return await FileInLibraryAsync(file, destDir, name, deleteOriginal, run, bytes);
            });

        var tidied = await FinishCollisionRunAsync(run) + episodes.Removed;
        var skipped = unfiled + run.Skipped + episodes.Skipped;

        // Anything that arrived is now filed; anything that moved for a corrected title is
        // filed under the new one. The library copies kept over an arrival count too — they
        // are what the run decided should be the filed copy.
        foreach (var file in files.Concat(episodes.Kept))
            file.Consolidated = ConsolidationPlanner.IsCorrectlyFiled(
                file, CategoryResolver.Effective(file, _settings), _settings);

        if (deleteOriginal)
            PushMoveUndo(origins.Where(o => !string.Equals(o.File.FullPath, o.FullPath,
                StringComparison.OrdinalIgnoreCase)).ToList());

        // Folders the move has emptied — or left holding only scraps. Left standing they are
        // litter from an operation the user asked for.
        var emptied = FindLeftovers(origins);

        var failed = Math.Max(0, report.Failed - unfiled - run.Skipped);
        var parts = new List<string> { $"{report.Succeeded} moved" };
        if (folderMoves.Count > 0) parts.Add($"{folderMoves.Count} folder(s) put right in place");
        if (episodes.KeptInLibrary > 0)
            parts.Add($"{episodes.KeptInLibrary} already in the library as the same episode");
        if (skipped > 0) parts.Add($"{skipped} skipped");
        if (alreadyConsolidated.Count > 0) parts.Add($"{alreadyConsolidated.Count} already consolidated");
        if (report.AlreadyPresent.Count > 0) parts.Add($"{report.AlreadyPresent.Count} already in the library");
        if (tidied > 0) parts.Add($"{tidied} duplicate(s) removed");
        if (failed > 0) parts.Add($"{failed} failed");

        var msg = "Consolidation: " + string.Join(", ", parts) +
                  (run.Cancelled ? " (cancelled part-way)." : ".");
        if (folderMoves.Count > 0)
            msg += "\n\n" + string.Join("\n", folderMoves.Take(10).Select(m => "    " + m.Describe()));
        if (episodes.Notes.Count > 0)
            msg += "\n\n" + string.Join("\n", episodes.Notes.Take(10).Select(n => "    " + n));
        msg += DescribeFailures(report.Reasons);

        StatusText = msg.Split('\n')[0];
        PersistAndRefresh();

        // Everything the run put in its place, however it got there. A folder rename files a
        // whole season without any of those files passing through the loop above, and they
        // need the same duplicate sweep as the ones that did.
        var touched = files
            .Concat(episodes.Kept)
            .Concat(folderMoves.SelectMany(m => m.Files))
            .Distinct()
            .ToList();

        return new ConsolidationOutcome(
            report.Succeeded, skipped, failed, report.AlreadyPresent, msg, alreadyConsolidated)
        { LeftoverFolders = emptied, Touched = touched };
    }

    /// <summary>
    /// The folders the moved files came out of that are now empty, or hold so little that
    /// what is left is scraps rather than content.
    ///
    /// How little counts as scraps is the user's, per category, and for good reason: three
    /// megabytes left where a film used to be is a sample clip or a readme, while three
    /// megabytes in a music folder is very probably a track. Where files of two categories
    /// shared a folder the stricter limit applies — being cautious costs an empty folder, and
    /// being casual costs somebody's music.
    /// </summary>
    private List<LeftoverFolder> FindLeftovers(
        IReadOnlyList<(MediaFile File, string FullPath, string FileName)> origins)
    {
        var limits = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var origin in origins)
        {
            var dir = Path.GetDirectoryName(origin.FullPath) ?? string.Empty;
            if (dir.Length == 0) continue;

            var limit = _settings.LeftoverThresholdFor(
                CategoryResolver.Effective(origin.File, _settings));
            limits[dir] = limits.TryGetValue(dir, out var already) ? Math.Min(already, limit) : limit;
        }

        var found = new List<LeftoverFolder>();
        foreach (var (dir, limit) in limits)
            found.AddRange(FolderLeftovers.Find(
                new[] { dir }, limit, IsWaitingToBeFiled, IsConfiguredFolder));
        return found.OrderByDescending(f => f.Path.Length).ToList();
    }

    /// <summary>
    /// True for a folder the user has named somewhere in the settings — a folder they scan,
    /// a folder they watch, a consolidation root, or a drive root.
    ///
    /// These are never taken away, however empty they end up. A download folder is empty most
    /// of the time; that is what it is for, and deleting it the moment the last thing in it
    /// was filed would be a poor reward for tidying up — and would quietly break the watching
    /// or scanning that pointed at it.
    /// </summary>
    private bool IsConfiguredFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return true;

        var path = folder.TrimEnd('\\', '/');
        if (path.Length == 0) return true;

        bool Same(string other) =>
            !string.IsNullOrWhiteSpace(other) &&
            string.Equals(other.TrimEnd('\\', '/'), path, StringComparison.OrdinalIgnoreCase);

        return _settings.AdditionalScanFolders.Any(Same) ||
               _settings.WatchedFolders.Any(Same) ||
               _settings.WatchedDrives.Any(Same) ||
               _settings.ScanDrives.Any(Same) ||
               _settings.CategoryFolders.Any(c => Same(c.Folder)) ||
               Same(_settings.TvConsolidationDir) ||
               Same(_settings.FilmConsolidationDir);
    }

    /// <summary>
    /// True for a file the catalogue knows about and has not filed yet. However small it is,
    /// that is work the user has not finished — so a folder holding one is never taken away,
    /// whatever the size limit says.
    /// </summary>
    private bool IsWaitingToBeFiled(string path) =>
        _catalog.ByPath.TryGetValue(path, out var entry) &&
        !ConsolidationPlanner.IsCorrectlyFiled(
            entry, CategoryResolver.Effective(entry, _settings), _settings);

    /// <summary>
    /// The doomed list with the file that is being kept taken out of it. Two byte-identical
    /// files are each other's copies, so "delete every copy of the loser" reaches the
    /// winner unless it is explicitly spared.
    /// </summary>
    private static IEnumerable<string> Spare(IEnumerable<string> doomed, MediaFile keeper) =>
        doomed.Where(p => !string.Equals(p, keeper.FullPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>What settling the same-episode conflicts came to.</summary>
    /// <param name="Kept">
    /// Library copies that won, so the caller can mark them filed — they are the copy the
    /// run has decided the library should hold.
    /// </param>
    private record EpisodeOutcome(
        int KeptInLibrary, int Skipped, int Removed, bool Cancelled,
        List<MediaFile> Kept, List<string> Notes);

    /// <summary>
    /// Settle every case where the episode being consolidated is already in the library
    /// under a different name, and take out of <paramref name="toFile"/> anything that
    /// should no longer move.
    ///
    /// Two files claiming the same season and episode of the same programme are the same
    /// episode whatever they are called, so filing the second one beside the first would
    /// leave the library holding it twice. When they are byte-for-byte identical there is
    /// nothing to decide: the library keeps the copy it already has and the arrival — with
    /// every other copy of it — goes. When they are genuinely different files, a different
    /// release or a different quality, only the user can say which is worth keeping, so
    /// they are asked.
    /// </summary>
    private async Task<EpisodeOutcome> ResolveEpisodeConflictsAsync(List<MediaFile> toFile)
    {
        var kept = new List<MediaFile>();
        var notes = new List<string>();
        int keptInLibrary = 0, skipped = 0, removed = 0;
        var cancelled = false;
        CollisionResolution? standing = null;

        // A whole season being consolidated shares one destination folder; reading it once
        // is enough.
        var adopted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in toFile.ToList())
        {
            if (cancelled) break;

            var category = CategoryResolver.Effective(file, _settings);

            // Anything sitting in the destination folder that the catalogue has never seen
            // is still a file in the library, and still the episode it is. Take it on
            // before asking whether the episode is already there.
            if (ConsolidationPlanner.PlanDirectory(file, category, _settings) is { } destDir &&
                adopted.Add(destDir))
                AdoptLibraryFolder(destDir);

            var twin = LibraryEpisodes.FindSameEpisode(
                file, category, _catalog.Files, _settings, f => CategoryResolver.Effective(f, _settings));
            if (twin == null) continue;

            // "Are these the same file?" decides the whole question without troubling the
            // user, so it is worth reading a hash for — but only when the sizes already
            // agree, since two files of different lengths cannot be the same bytes and
            // hashing a pair of them would cost minutes to learn nothing.
            if (file.SizeBytes == twin.SizeBytes)
            {
                await EnsureHashedAsync(file);
                await EnsureHashedAsync(twin);
            }

            var identical = file.HasHash && twin.HasHash &&
                            string.Equals(file.Sha256, twin.Sha256, StringComparison.OrdinalIgnoreCase);
            var episode = LibraryEpisodes.Describe(file);

            var choice = identical
                // Identical files decide it themselves: keeping either keeps the same
                // content, and the library's copy is already where it belongs.
                ? new CollisionResolution(CollisionChoice.KeepExisting, DeleteDuplicates: true)
                : standing ?? (EpisodeConflictResolver == null
                    // Nobody to ask, so take the answer that cannot lose anything.
                    ? new CollisionResolution(CollisionChoice.Skip)
                    : await EpisodeConflictResolver(new EpisodeConflict(
                        file, twin, CopiesOf(file), CopiesOf(twin), identical, episode)));

            if (!identical && choice.ApplyToRemaining) standing = choice;

            switch (choice.Choice)
            {
                case CollisionChoice.Cancel:
                    cancelled = true;
                    break;

                case CollisionChoice.KeepIncoming:
                {
                    // Clear the library's copy out of the way; the arrival then files
                    // itself in the ordinary run below. The keeper is held back from the
                    // clear-out: when the two are identical each counts as a copy of the
                    // other, and deleting "every copy" would take both.
                    var doomed = new List<string> { twin.FullPath };
                    if (choice.DeleteDuplicates) doomed.AddRange(CopiesOf(twin).Select(c => c.FullPath));
                    removed += await DeleteQuietlyAsync(
                        Spare(doomed, file), toRecycleBin: true);
                    notes.Add($"{episode}: kept the copy being consolidated; the library's went to " +
                              "the Recycle Bin.");
                    break;
                }

                case CollisionChoice.KeepExisting:
                {
                    // The library already holds this episode, so the arrival is redundant.
                    // An identical file is gone for good — there is nothing to recover that
                    // the library copy is not already holding; a different file the user
                    // chose against goes to the Recycle Bin, where they can think again.
                    var doomed = new List<string> { file.FullPath };
                    if (choice.DeleteDuplicates) doomed.AddRange(CopiesOf(file).Select(c => c.FullPath));

                    // The keeper never goes, however the copies are counted — and when the
                    // two are byte-identical, each *is* a copy of the other.
                    removed += await DeleteQuietlyAsync(Spare(doomed, twin), toRecycleBin: !identical);
                    toFile.Remove(file);
                    kept.Add(twin);
                    keptInLibrary++;
                    notes.Add(identical
                        ? $"{episode}: already in the library, byte for byte — the other copy " +
                          "was deleted."
                        : $"{episode}: kept the library's copy; the other went to the Recycle Bin.");
                    break;
                }

                default: // Skip, and KeepBoth — which is never offered, since two copies of
                         // one episode in the library is the thing being prevented.
                    toFile.Remove(file);
                    skipped++;
                    notes.Add($"{episode}: left alone — it is already in the library under " +
                              $"'{twin.FileName}'.");
                    break;
            }
        }

        // The library copy that won may itself be sitting under the wrong name; filing it
        // is the other half of "the library holds exactly one of this episode".
        foreach (var winner in kept.Where(w => !ConsolidationPlanner.IsAtPlannedPath(
                     w, CategoryResolver.Effective(w, _settings), _settings)))
            if (!toFile.Contains(winner)) toFile.Add(winner);

        return new EpisodeOutcome(keptInLibrary, skipped, removed, cancelled, kept, notes);
    }

    /// <summary>
    /// Read a file's hash if it has not got one, so a question that turns on whether two
    /// files are identical can actually be answered. Says so on the status bar: hashing a
    /// large file is not instant, and a program that goes quiet looks like one that hung.
    /// </summary>
    private async Task EnsureHashedAsync(MediaFile file)
    {
        if (file.HasHash || !File.Exists(file.FullPath)) return;

        var was = StatusText;
        StatusText = ProgressLine("Reading", 0, 0, file.FileName) + " to compare it";
        file.Sha256 = await FileHasher.ComputeSha256Async(file.FullPath);
        file.HashFailed = !file.HasHash;
        StatusText = was;
    }

    /// <summary>
    /// Catalogue any media file sitting in a library folder that we have never seen. A file
    /// the program does not know about is a file it cannot recognise as the episode it
    /// already holds — which is exactly how a library ends up with two of them.
    /// </summary>
    private void AdoptLibraryFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

        string[] paths;
        try { paths = Directory.GetFiles(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

        var added = false;
        foreach (var path in paths)
        {
            if (_catalog.ByPath.ContainsKey(path)) continue;

            var extension = Path.GetExtension(path);
            if (!MediaExtensions.IsMedia(extension)) continue;
            if (_settings.IsPathExcluded(path) || _settings.IsExtensionIgnored(extension)) continue;

            FileInfo info;
            try { info = new FileInfo(path); if (!info.Exists) continue; }
            catch { continue; }

            var entry = new MediaFile
            {
                FullPath = path, FileName = info.Name, Extension = info.Extension,
                SizeBytes = info.Length, LastModifiedUtc = info.LastWriteTimeUtc,
                IndexedUtc = DateTime.UtcNow, Integrity = IntegrityStatus.Ok,
                FeatureVersion = CatalogRefresher.CurrentFeatureVersion
            };
            MediaClassifier.Classify(entry, _settings);
            _catalog.Files.Add(entry);
            _catalog.ByPath[path] = entry;
            added = true;
        }

        if (added) ExtraLinker.Link(_catalog.Files);
    }

    /// <summary>
    /// Put right the folders that are in the library under the wrong name, by moving them
    /// rather than their contents. Only ever applied to a folder that agrees with itself —
    /// every catalogued file in it wanting the same destination — so a folder that turns
    /// out to hold two different things is left to the ordinary per-file path.
    /// </summary>
    private List<FolderMove> MoveMisfiledFolders(IReadOnlyList<MediaFile> files)
    {
        var candidates = files
            .Where(f => !f.Consolidated && ConsolidationPlanner.IsUnderConsolidationRoot(f, _settings))
            .ToList();
        if (candidates.Count == 0) return new List<FolderMove>();

        var moves = FolderRelocator.Relocate(candidates, _catalog.Files, _settings,
            f => CategoryResolver.Effective(f, _settings));
        if (moves.Count > 0)
        {
            _catalog.RebuildIndex();
            CatalogStore.Save(_catalog, _catalogPath);
        }
        return moves;
    }

    /// <summary>
    /// The other copies of files that were already consolidated — what there is to tidy up
    /// once a file turns out to need no moving.
    /// </summary>
    public List<MediaFile> CopiesOfAny(IEnumerable<MediaFile> files)
    {
        var chosen = new HashSet<MediaFile>(files);
        return chosen.SelectMany(CopiesOf).Distinct().Where(f => !chosen.Contains(f)).ToList();
    }

    /// <summary>
    /// Make sure the copy the user kept is the one in the library: when the survivor was
    /// not the library copy, it is moved into the place the copies have just vacated.
    ///
    /// The deleting is the caller's, done through the shared delete conversation so the
    /// Recycle Bin can be skipped here as it can anywhere else; all that is left afterwards
    /// is putting the survivor where it belongs.
    /// </summary>
    public async Task<string> EnsureKeeperFiledAsync(MediaFile keeper)
    {
        var category = CategoryResolver.Effective(keeper, _settings);
        if (ConsolidationPlanner.IsAtPlannedPath(keeper, category, _settings))
        {
            StatusText = $"{keeper.FileName} is the copy in the library.";
            return StatusText;
        }

        var outcome = await ConsolidateModelsAsync(new[] { keeper }, deleteOriginal: true);
        StatusText = outcome.Message;
        return outcome.Message;
    }

    /// <summary>
    /// Every copy of a file that is already correctly filed in the library, wherever else
    /// it is sitting. This is what "purge the duplicates of everything consolidated" comes
    /// down to: the library copy is the one to keep, so all the others are redundant by
    /// definition and the user needs only to say how firmly they should go.
    ///
    /// The library copies themselves are never in the returned list — the point is to keep
    /// them — and a set with no filed copy at all is left alone, since choosing between
    /// scattered copies is a decision rather than a tidy-up.
    /// </summary>
    public List<MediaFile> DuplicatesOfConsolidatedFiles()
    {
        var redundant = new List<MediaFile>();

        foreach (var group in _duplicateGroups.Values)
        {
            var filed = group.Files.Where(f =>
                ConsolidationPlanner.IsCorrectlyFiled(
                    f, CategoryResolver.Effective(f, _settings), _settings)).ToList();
            if (filed.Count == 0) continue;

            // More than one copy inside the library is possible — the same episode filed
            // under two spellings, say — and only one of them need survive.
            var keeper = filed[0];
            redundant.AddRange(group.Files.Where(f => !ReferenceEquals(f, keeper)));
        }

        return redundant.Distinct().Where(f => File.Exists(f.FullPath)).ToList();
    }

    // --- Folders left empty ------------------------------------------------

    /// <summary>
    /// The folders that held these files and now hold nothing. Offered after a delete so
    /// an operation the user asked for doesn't leave litter behind.
    /// </summary>
    public IReadOnlyList<string> EmptyFoldersLeftBy(IEnumerable<string> deletedPaths) =>
        EmptyFolderCleaner.EmptiedBy(deletedPaths);

    /// <summary>
    /// Remove folders the user has agreed to, taking any parent they empty in turn.
    ///
    /// Deleted outright rather than recycled, unless the user has said otherwise: what is
    /// going has already been judged to be nothing — either an empty folder or one holding
    /// less than the category's size limit, with no catalogued file waiting to be filed
    /// anywhere in it. There is nothing in the Recycle Bin's job description for that.
    ///
    /// The catalogue holds files rather than folders, so there is nothing to prune from it —
    /// but the results are rebuilt anyway, since a removed folder changes what "filed" means
    /// for anything that pointed into it, and anything that was inside it is now gone.
    /// </summary>
    public async Task<int> RemoveFoldersAsync(IReadOnlyList<string> folders)
    {
        if (folders.Count == 0) return 0;

        var recycle = !_settings.DeleteEmptyFoldersPermanently;
        var removed = await Task.Run(() => FolderLeftovers.Remove(folders, recycle, IsConfiguredFolder));
        if (removed.Count == 0) return 0;

        // A folder that went with something still in it takes those entries with it.
        var gone = removed.Select(f => f.TrimEnd('\\', '/') + Path.DirectorySeparatorChar).ToList();
        var dropped = _catalog.Files.RemoveAll(f =>
            gone.Any(g => f.FullPath.StartsWith(g, StringComparison.OrdinalIgnoreCase)));
        if (dropped > 0)
        {
            _catalog.RebuildIndex();
            ExtraLinker.Link(_catalog.Files);
            CatalogStore.Save(_catalog, _catalogPath);
        }

        RebuildRows();
        StatusText = $"{removed.Count} folder(s) removed" +
                     (dropped > 0 ? $", along with {dropped} catalogue entr(ies) inside them." : ".");
        return removed.Count;
    }

    /// <summary>
    /// Copies of files that are now filed which are still sitting somewhere else — what the
    /// sweep at the end of a consolidation is for.
    ///
    /// A file that has just been filed must not be left with copies of it lying about, and
    /// that is as true of a whole season put right by a folder rename as of a file copied one
    /// at a time. The filed copy is the keeper by definition, so everything here is redundant
    /// and all that is left is to ask how firmly it should go.
    /// </summary>
    public List<MediaFile> RedundantCopiesOf(IEnumerable<MediaFile> filed)
    {
        var redundant = new List<MediaFile>();
        var seen = new HashSet<MediaFile>();

        foreach (var file in filed)
        {
            if (!ConsolidationPlanner.IsCorrectlyFiled(
                    file, CategoryResolver.Effective(file, _settings), _settings)) continue;

            foreach (var copy in CopiesOf(file))
            {
                if (ReferenceEquals(copy, file) || !seen.Add(copy)) continue;

                // Two copies can both be inside the library — the same episode filed under
                // two spellings. Neither is redundant until one of them is the keeper, and
                // the keeper is the one that is correctly filed, so a second filed copy is
                // left alone rather than quietly deleted.
                if (ConsolidationPlanner.IsCorrectlyFiled(
                        copy, CategoryResolver.Effective(copy, _settings), _settings)) continue;

                if (File.Exists(copy.FullPath)) redundant.Add(copy);
            }
        }

        return redundant;
    }

    /// <summary>Propose consolidation moves for the whole catalogue.</summary>
    public List<ConsolidationSuggestion> SuggestConsolidation() =>
        ConsolidationSuggester.Suggest(
            _catalog.Files, _settings, f => CategoryResolver.Effective(f, _settings));

    /// <summary>Apply chosen consolidation suggestions (copy-and-verify, optional delete).</summary>
    public async Task<ConsolidationOutcome> ApplyConsolidationAsync(
        IReadOnlyList<ConsolidationSuggestion> chosen, bool deleteOriginal)
    {
        var byFile = chosen.ToDictionary(s => s.File, s => s.ProposedPath);
        var files = chosen.Select(s => s.File).ToList();
        var origins = files.Select(f => (File: f, f.FullPath, f.FileName)).ToList();
        var run = new CollisionRun("consolidated");

        var report = await RunFileOperationAsync(files, "Consolidating", async (file, bytes) =>
        {
            if (run.Cancelled)
                return new RelocationResult(false, "Cancelled.", file.FullPath);

            var proposed = byFile[file];
            var destDir = Path.GetDirectoryName(proposed);
            return string.IsNullOrEmpty(destDir)
                ? new RelocationResult(false, "No destination folder.", file.FullPath)
                : await FileInLibraryAsync(file, destDir, Path.GetFileName(proposed),
                    deleteOriginal, run, bytes);
        });

        var tidied = await FinishCollisionRunAsync(run);

        foreach (var file in files)
            file.Consolidated = ConsolidationPlanner.IsCorrectlyFiled(
                file, CategoryResolver.Effective(file, _settings), _settings);
        CatalogStore.Save(_catalog, _catalogPath);

        if (deleteOriginal)
            PushMoveUndo(origins.Where(o => !string.Equals(o.File.FullPath, o.FullPath,
                StringComparison.OrdinalIgnoreCase)).ToList());

        var parts = new List<string> { $"{report.Succeeded} moved" };
        if (report.AlreadyPresent.Count > 0) parts.Add($"{report.AlreadyPresent.Count} already in the library");
        if (run.Skipped > 0) parts.Add($"{run.Skipped} skipped");
        if (tidied > 0) parts.Add($"{tidied} duplicate(s) removed");
        if (report.Failed > 0) parts.Add($"{report.Failed} failed");

        var msg = "Consolidation: " + string.Join(", ", parts) +
                  (run.Cancelled ? " (cancelled part-way)." : ".") +
                  DescribeFailures(report.Reasons);

        StatusText = msg.Split('\n')[0];
        PersistAndRefresh();
        return new ConsolidationOutcome(report.Succeeded, 0, report.Failed, report.AlreadyPresent, msg)
        {
            LeftoverFolders = FindLeftovers(origins),
            Touched = files
        };
    }

    // --- Consolidating the whole catalogue, hands off ------------------------

    /// <summary>What a hands-off consolidation run came to.</summary>
    /// <param name="Review">
    /// Everything the run declined to touch, with the reason. This is the important half of
    /// the report: the files it filed are finished with, and the ones it did not are the
    /// list of what is left to do.
    /// </param>
    public record AutoConsolidationReport(
        int Filed, int Removed, int Corrupt, int Failed, bool Cancelled,
        List<AutoReview> Review, List<string> Notes,
        IReadOnlyList<MediaFile> Touched, IReadOnlyList<LeftoverFolder> LeftoverFolders)
    {
        public string Describe()
        {
            var parts = new List<string> { $"{Filed} filed" };
            if (Removed > 0) parts.Add($"{Removed} redundant copy(ies) deleted");
            if (Corrupt > 0) parts.Add($"{Corrupt} damaged copy(ies) removed");
            if (Failed > 0) parts.Add($"{Failed} failed");
            if (Review.Count > 0) parts.Add($"{Review.Count} left for you to look at");

            var text = "Automatic consolidation: " + string.Join(", ", parts) +
                       (Cancelled ? " (stopped part-way)." : ".");
            if (Notes.Count > 0)
                text += "\n\n" + string.Join("\n", Notes.Take(20).Select(n => "    " + n)) +
                        (Notes.Count > 20 ? $"\n    …and {Notes.Count - 20} more." : "");
            return text;
        }
    }

    /// <summary>What one job settled on, before anything has moved.</summary>
    private record AutoDecision(MediaFile Keeper, List<MediaFile> Doomed, string? Note);

    /// <summary>
    /// Work out what a hands-off run would do, without doing any of it — so the user can be
    /// shown the size of the job and what it will not touch before agreeing to it.
    /// </summary>
    public (List<AutoJob> Jobs, List<AutoReview> Review) PlanAutoConsolidation(
        IReadOnlyList<MediaFile>? scope = null) =>
        AutoConsolidator.Plan(
            scope is { Count: > 0 } ? scope : _catalog.Files,
            _settings, f => CategoryResolver.Effective(f, _settings));

    /// <summary>
    /// File everything that can be filed without asking, and say plainly what was left.
    ///
    /// The order of the decisions is the order a careful person would make them. A file that
    /// does not yet say what it is cannot be filed and is set aside. A file with no other
    /// copy is simply filed. Copies that are the same bytes decide themselves — the one
    /// already in the library wins, because keeping it means moving nothing. Only genuinely
    /// different files claiming to be the same thing are a real question, and that one is
    /// settled by looking: fingerprints to confirm they are the same content at all, quality
    /// and size to choose between them, and a decode to make sure the survivor is not the
    /// damaged one. A copy that fails the decode is removed along with its byte-identical
    /// twins — which are damaged by definition — and the next best is tried.
    ///
    /// Nothing is deleted until the copy that is being kept has actually arrived in the
    /// library. A run that fails half way through leaves every file it had not yet filed
    /// exactly where it was.
    /// </summary>
    public async Task<AutoConsolidationReport> AutoConsolidateAsync(
        IReadOnlyList<MediaFile>? scope = null)
    {
        var (jobs, review) = PlanAutoConsolidation(scope);
        var notes = new List<string>();
        var none = new List<MediaFile>();

        if (!_settings.HasAnyConsolidationFolder)
            return new AutoConsolidationReport(0, 0, 0, 0, false,
                review, new List<string>
                {
                    "No category has a consolidation folder set, so there is nowhere to file " +
                    "anything. Set one on the Library tab in Settings."
                }, none, Array.Empty<LeftoverFolder>());

        if (jobs.Count == 0)
            return new AutoConsolidationReport(0, 0, 0, 0, false, review, notes, none,
                Array.Empty<LeftoverFolder>());

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsScanning = true;
        BeginTiming();
        ProgressValue = 0;
        ProgressMax = Math.Max(1, jobs.Count);

        var decisions = new List<AutoDecision>();
        var corrupt = 0;
        var cancelled = false;

        try
        {
            for (var i = 0; i < jobs.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var job = jobs[i];

                ProgressValue = i;
                UpdateEta(i, jobs.Count);
                StatusText = ProgressLine("Working out what to keep", i + 1, jobs.Count, job.Display);

                var settled = await SettleJobAsync(job, review, notes, ct);
                if (settled == null) continue;

                corrupt += settled.Value.Corrupt;
                if (settled.Value.Decision is { } decision) decisions.Add(decision);
            }
        }
        catch (OperationCanceledException) { cancelled = true; }
        catch (Exception ex) { notes.Add($"Stopped: {ex.Message}"); }
        finally
        {
            EndTiming();
            IsScanning = false;
            _cts?.Dispose();
            _cts = null;
        }

        if (decisions.Count == 0)
        {
            RebuildRows();
            return new AutoConsolidationReport(0, 0, corrupt, 0, cancelled, review, notes, none,
                Array.Empty<LeftoverFolder>());
        }

        // Everything that survived the decisions, filed in one batch so the progress bar and
        // the ETA cover the whole job rather than restarting for each file.
        var keepers = decisions.Select(d => d.Keeper).Distinct().ToList();
        var outcome = await ConsolidateModelsAsync(
            keepers.Concat(LinkedExtras(keepers)).Distinct().ToList(), deleteOriginal: true);

        // Only now, and only for the ones that actually arrived: a copy is not redundant
        // until the copy replacing it is really in the library.
        var doomed = new List<string>();
        var filed = 0;
        foreach (var decision in decisions)
        {
            var arrived = ConsolidationPlanner.IsCorrectlyFiled(
                decision.Keeper, CategoryResolver.Effective(decision.Keeper, _settings), _settings);
            if (!arrived)
            {
                notes.Add($"{decision.Keeper.FileName}: could not be filed, so its other copies " +
                          "were left alone.");
                continue;
            }

            filed++;
            doomed.AddRange(decision.Doomed
                .Where(d => !ReferenceEquals(d, decision.Keeper))
                .Select(d => d.FullPath));
            if (decision.Note is { Length: > 0 } note) notes.Add(note);
        }

        var removed = await DeleteQuietlyAsync(doomed, toRecycleBin: true);

        PersistAndRefresh();

        var report = new AutoConsolidationReport(
            filed, removed, corrupt, outcome.Failed, cancelled, review, notes,
            outcome.Touched, outcome.LeftoverFolders);
        StatusText = report.Describe().Split('\n')[0];
        return report;
    }

    /// <summary>
    /// Decide one job: which copy survives and which go. Null when the job cannot be settled
    /// without the user, in which case the reason has been added to <paramref name="review"/>.
    /// </summary>
    private async Task<(AutoDecision? Decision, int Corrupt)?> SettleJobAsync(
        AutoJob job, List<AutoReview> review, List<string> notes, CancellationToken ct)
    {
        var destination = ConsolidationPlanner.PlanDirectory(job.Files[0], job.Category, _settings);

        switch (job.Kind)
        {
            case AutoJobKind.Single:
                return (new AutoDecision(job.Files[0], new List<MediaFile>(), null), 0);

            case AutoJobKind.ExactCopies:
            {
                // Every copy is the same bytes, so which one survives changes nothing about
                // what the library ends up holding — only how much work it takes to get
                // there. The copy already in the library wins for exactly that reason.
                var keeper = AutoConsolidator.PreferLibraryCopy(job.Files, destination, _settings);
                var others = job.Others(keeper);
                return (new AutoDecision(keeper, others,
                    others.Count > 0
                        ? $"{job.Display}: kept one of {job.Files.Count} identical copies."
                        : null), 0);
            }
        }

        // --- Rivals: genuinely different files claiming to be the same thing -------------

        // One file stands for each distinct set of bytes, preferring a copy already in the
        // library so the work of comparing is done on the copy we would rather keep anyway.
        var candidates = job.Distinct
            .Select(set => AutoConsolidator.PreferLibraryCopy(set, destination, _settings))
            .ToList();

        if (!CanAnalyze)
        {
            review.Add(new AutoReview(candidates[0],
                $"{job.Distinct.Count} different files claim to be this, and comparing them needs " +
                "FFmpeg/ffprobe (and fpcalc for audio) — set them up under Settings → External tools"));
            return null;
        }

        // Fingerprint whatever has not been fingerprinted yet. Only the representatives:
        // the other copies are the same bytes and would give the same answer.
        var unfingerprinted = candidates.Where(NeedsFingerprint).ToList();
        if (unfingerprinted.Count > 0)
        {
            StatusText = ProgressLine("Fingerprinting to compare", 0, 0, job.Display);
            var engine = new ContentAnalysisEngine(_tools);
            await engine.AnalyzeAsync(unfingerprinted, fingerprint: true, deepCheck: false, null, ct);
        }

        // Every copy has to look like the first one. The comparison allows for a copy that
        // starts a second or two later than another — an extra beat of distributor logo is
        // the usual reason two rips of one film do not line up — so a real match is not
        // mistaken for a disagreement.
        var odd = candidates.Skip(1)
            .FirstOrDefault(c => !FingerprintMatcher.LooksLikeSameContent(candidates[0], c));
        if (odd != null)
        {
            review.Add(new AutoReview(odd,
                $"claims to be \"{job.Display}\" but does not sound or look like the other copy — " +
                "one of them is mislabelled, so which to keep is your call"));
            return null;
        }

        // Best picture first; among copies of one quality the smallest, since at a given
        // resolution the extra bytes are padding rather than detail.
        var corrupt = 0;
        foreach (var candidate in AutoConsolidator.RankCandidates(candidates))
        {
            ct.ThrowIfCancellationRequested();

            if (CanDoVideo || candidate.Kind == MediaKind.Audio)
            {
                StatusText = ProgressLine("Deep checking the best copy", 0, 0, candidate.FileName);
                var engine = new ContentAnalysisEngine(_tools);
                await engine.AnalyzeAsync(new[] { candidate }, fingerprint: false, deepCheck: true,
                    null, ct);
            }

            if (candidate.Integrity == IntegrityStatus.Corrupt)
            {
                // Damaged, and so is every byte-identical copy of it — the same bytes cannot
                // be sound in one place and broken in another. They all go, and the next
                // best copy gets its turn.
                var twins = job.Distinct.First(set => set.Contains(candidate));
                corrupt += await DeleteQuietlyAsync(
                    twins.Select(t => t.FullPath), toRecycleBin: true);
                notes.Add($"{job.Display}: {candidate.FileName} would not decode — it and its " +
                          $"{twins.Count - 1} identical copy(ies) were removed.");
                continue;
            }

            var doomed = job.Files
                .Where(f => !ReferenceEquals(f, candidate) && File.Exists(f.FullPath))
                .ToList();
            return (new AutoDecision(candidate, doomed,
                $"{job.Display}: kept {candidate.FileName} ({candidate.QualityDisplay}, " +
                $"{Format.Bytes(candidate.SizeBytes)}) of {job.Distinct.Count} different copies."), corrupt);
        }

        review.Add(new AutoReview(job.Files[0],
            "every copy of this failed its decode, so there is nothing sound left to file"));
        return (null, corrupt);
    }

    /// <summary>True when nothing has fingerprinted this file in the way its kind needs.</summary>
    private static bool NeedsFingerprint(MediaFile file) => file.Kind switch
    {
        MediaKind.Audio => string.IsNullOrEmpty(file.AudioFingerprint),
        MediaKind.Video => string.IsNullOrEmpty(file.VideoFingerprint),
        _ => false
    };

    /// <summary>Files that cannot be consolidated because nothing is known about them yet.</summary>
    public List<MediaFile> WithoutTitle(IEnumerable<MediaFile> files) =>
        files.Where(f => string.IsNullOrWhiteSpace(f.EffectiveTitle) &&
                         !CategoryResolver.IsExtra(CategoryResolver.Effective(f, _settings)))
             .ToList();

    // --- Deleting files ---------------------------------------------------

    /// <summary>
    /// Delete files from disk (Recycle Bin unless <paramref name="toRecycleBin"/> is
    /// false) and drop the ones that actually went from the catalogue. Recycled deletes
    /// are undoable; the failures come back in full so the caller can explain them.
    /// </summary>
    public async Task<DeleteOutcome> DeleteFilesAsync(IReadOnlyList<MediaFile> files, bool toRecycleBin)
    {
        if (files.Count == 0)
            return new DeleteOutcome(new DeleteResult(0, new List<DeleteFailure>()), "Nothing selected.");

        IsScanning = true;
        DeleteResult result;
        try
        {
            var paths = files.Select(f => f.FullPath).ToList();
            StatusText = $"Deleting {files.Count} file(s)…";
            result = await Task.Run(() => FileDeleter.Delete(paths, toRecycleBin));
            ForgetDeleted(files, toRecycleBin);
        }
        finally
        {
            IsScanning = false;
            RebuildRows();
        }

        var where = toRecycleBin ? "sent to the Recycle Bin" : "permanently deleted";
        var message = $"{result.Deleted} file(s) {where}" +
                      (result.Failed > 0 ? $", {result.Failed} could not be deleted." : ".");
        if (result.Failed > 0)
            message += "\n\n" + string.Join("\n\n", result.Failures.Take(10).Select(f => f.Describe()));
        StatusText = message.Split('\n')[0];
        return new DeleteOutcome(result, message);
    }

    /// <summary>
    /// Drop entries whose file is gone, and — for recycled files — record how to put them
    /// back. A permanent delete is not undoable, by definition.
    /// </summary>
    private void ForgetDeleted(IReadOnlyList<MediaFile> files, bool recycled)
    {
        var gone = files.Where(f => !File.Exists(f.FullPath)).ToList();
        if (gone.Count == 0) return;

        var goneSet = new HashSet<MediaFile>(gone);
        _catalog.Files.RemoveAll(goneSet.Contains);
        _catalog.RebuildIndex();
        ExtraLinker.Link(_catalog.Files);
        CatalogStore.Save(_catalog, _catalogPath);

        if (!recycled) return;

        var paths = gone.Select(f => f.FullPath).ToList();
        Undo.Push($"delete of {gone.Count} file(s)", async () =>
        {
            var restored = await Task.Run(() => RecycleBin.Restore(paths));
            foreach (var file in gone.Where(f => File.Exists(f.FullPath)))
                _catalog.Files.Add(file);
            _catalog.RebuildIndex();
            ExtraLinker.Link(_catalog.Files);
            PersistAndRefresh();

            return restored.Count == paths.Count
                ? $"Restored {restored.Count} file(s) from the Recycle Bin."
                : $"Restored {restored.Count} of {paths.Count} file(s); the rest are still in the " +
                  "Recycle Bin and can be put back from there.";
        });
    }

    /// <summary>
    /// Retry a delete with administrative rights by relaunching this program elevated for
    /// the job — the current process cannot gain rights it started without.
    /// </summary>
    public async Task<DeleteOutcome> RetryDeleteElevatedAsync(
        IReadOnlyList<MediaFile> files, bool toRecycleBin)
    {
        if (files.Count == 0)
            return new DeleteOutcome(new DeleteResult(0, new List<DeleteFailure>()), "Nothing to retry.");

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return new DeleteOutcome(new DeleteResult(0, new List<DeleteFailure>()),
                "Could not work out where this program lives, so it cannot be restarted with more rights.");

        var listPath = Path.Combine(Path.GetTempPath(), $"mediacatalog-delete-{Guid.NewGuid():N}.txt");
        var resultPath = listPath + ".result";

        IsScanning = true;
        try
        {
            await File.WriteAllLinesAsync(listPath, files.Select(f => f.FullPath));
            StatusText = "Waiting for the elevated delete…";

            var args = $"{App.DeleteArgument} \"{listPath}\"" +
                       (toRecycleBin ? "" : $" {App.PermanentArgument}");
            var started = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args)
            {
                UseShellExecute = true,
                Verb = "runas"          // triggers the UAC prompt
            });
            if (started == null)
                return new DeleteOutcome(new DeleteResult(0, new List<DeleteFailure>()),
                    "The elevated helper did not start.");

            await started.WaitForExitAsync();

            var failures = new List<DeleteFailure>();
            if (File.Exists(resultPath))
                foreach (var line in await File.ReadAllLinesAsync(resultPath))
                {
                    var parts = line.Split('\t');
                    if (parts.Length == 2 && parts[0].Length > 0)
                        failures.Add(new DeleteFailure(parts[0], parts[1],
                            FileLocks.ProcessesUsing(parts[0]), AccessDenied: false));
                }

            var deleted = files.Count(f => !File.Exists(f.FullPath));
            ForgetDeleted(files, toRecycleBin);

            var result = new DeleteResult(deleted, failures);
            var message = failures.Count == 0
                ? $"{deleted} file(s) deleted with administrative rights."
                : $"{deleted} deleted, {failures.Count} still refused:\n\n" +
                  string.Join("\n\n", failures.Take(10).Select(f => f.Describe()));
            StatusText = message.Split('\n')[0];
            return new DeleteOutcome(result, message);
        }
        catch (Exception ex)
        {
            // The usual case here is the user dismissing the UAC prompt.
            var message = $"The elevated delete did not run: {ex.Message}";
            StatusText = message;
            return new DeleteOutcome(new DeleteResult(0, new List<DeleteFailure>()), message);
        }
        finally
        {
            IsScanning = false;
            TryDelete(listPath);
            TryDelete(resultPath);
            RebuildRows();
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    // --- Moving to a chosen folder ----------------------------------------

    /// <summary>Catalogued files that live in the same folders as <paramref name="files"/>.</summary>
    public List<MediaFile> SiblingsOf(IEnumerable<MediaFile> files)
    {
        var chosen = new HashSet<MediaFile>(files);
        var folders = chosen
            .Select(f => Path.GetDirectoryName(f.FullPath) ?? "")
            .Where(d => d.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return _catalog.Files
            .Where(f => !chosen.Contains(f) &&
                        folders.Contains(Path.GetDirectoryName(f.FullPath) ?? ""))
            .ToList();
    }

    /// <summary>
    /// Asked when a move would land on a name that is already taken. Set by the window,
    /// which puts both files — and every known copy of either — in front of the user.
    /// Left unset, the old behaviour stands: the arrival is renamed and both are kept.
    /// </summary>
    public Func<CollisionRequest, Task<CollisionResolution>>? CollisionResolver { get; set; }

    /// <summary>
    /// Move (or copy) files to a folder the user picked, with progress and an ETA based on
    /// the bytes actually copied. A name already in use at the destination is put to the
    /// user rather than quietly worked around.
    /// </summary>
    public async Task<string> MoveFilesAsync(
        IReadOnlyList<MediaFile> files, string destinationDir, bool deleteOriginal)
    {
        if (files.Count == 0) return "Nothing selected.";
        if (string.IsNullOrWhiteSpace(destinationDir)) return "No destination folder.";

        var origins = files.Select(f => (File: f, f.FullPath, f.FileName)).ToList();
        var verb = deleteOriginal ? "Moving" : "Copying";

        var run = new CollisionRun("moved");

        var report = await RunFileOperationAsync(files, verb, async (file, bytes) =>
        {
            if (run.Cancelled)
                return new RelocationResult(false, "Cancelled.", file.FullPath);

            // Skip first, so a taken name is reported rather than worked around, and the
            // question is only asked when there is genuinely something to decide.
            //
            // Subtitles always come along on a move the user asked for: they were chosen
            // together with the file, whatever the library setting says about consolidating.
            var result = await FileRelocator.RelocateAsync(file, destinationDir, deleteOriginal,
                newFileName: null, DuplicatePolicy.Skip, bytes, subtitles: SubtitlePolicy.Follow);
            if (!result.NameTaken) return result;

            var (policy, decided) = await AskAboutCollisionAsync(file, result.NewPath, run);
            if (decided != null) return decided;

            return await FileRelocator.RelocateAsync(file, destinationDir, deleteOriginal,
                newFileName: null, policy, bytes, subtitles: SubtitlePolicy.Follow);
        });

        var removed = await FinishCollisionRunAsync(run);

        if (report.Succeeded > 0 && deleteOriginal)
            PushMoveUndo(origins.Where(o => o.File.FullPath != o.FullPath).ToList());

        var parts = new List<string> { $"{report.Succeeded} done" };
        if (report.AlreadyPresent.Count > 0) parts.Add($"{report.AlreadyPresent.Count} already there");
        if (run.Skipped > 0) parts.Add($"{run.Skipped} skipped");
        if (removed > 0) parts.Add($"{removed} duplicate(s) removed");
        if (report.Failed > 0) parts.Add($"{report.Failed} failed");

        var message = $"{(deleteOriginal ? "Move" : "Copy")} finished: " + string.Join(", ", parts) +
                      (run.Cancelled ? " (cancelled part-way)." : ".") +
                      DescribeFailures(report.Reasons);

        StatusText = message.Split('\n')[0];
        PersistAndRefresh();
        return message;
    }

    // --- Name collisions --------------------------------------------------

    /// <summary>
    /// The state of one batch's collision conversation: an answer the user asked to reuse,
    /// the copies to clear away afterwards, and the files that must survive that clear-out.
    /// </summary>
    private sealed class CollisionRun
    {
        public CollisionRun(string operation) => Operation = operation;

        /// <summary>What the user started, as a past participle: "moved", "consolidated".</summary>
        public string Operation { get; }

        public CollisionResolution? Standing { get; set; }
        public bool Cancelled { get; set; }
        public int Skipped { get; set; }

        /// <summary>Copies to remove once the batch has finished walking its list.</summary>
        public List<string> TidyUp { get; } = new();

        /// <summary>Entries the clear-out must leave alone, read at the end from where they ended up.</summary>
        public List<MediaFile> Survivors { get; } = new();

        /// <summary>The same, for files the catalogue has never seen.</summary>
        public List<string> SurvivorPaths { get; } = new();
    }

    /// <summary>
    /// Put a taken destination name to the user and carry out everything the answer implies
    /// except the relocation itself.
    /// </summary>
    /// <returns>
    /// The policy to relocate under, or — when the file should not move at all — the result
    /// to hand back to the batch.
    /// </returns>
    private async Task<(DuplicatePolicy Policy, RelocationResult? Result)> AskAboutCollisionAsync(
        MediaFile file, string desired, CollisionRun run)
    {
        // Nobody to ask: keep both, which is what the program did before it asked anyone.
        if (CollisionResolver == null) return (DuplicatePolicy.Rename, null);

        var existing = EntryAt(desired);
        var resolution = run.Standing ??
                         await CollisionResolver(BuildCollisionRequest(file, desired, run.Operation));
        if (resolution.ApplyToRemaining) run.Standing = resolution;

        // Gathered before anything is deleted: once the loser is gone from the catalogue
        // there is no longer any way to ask what its other copies were.
        if (resolution.DeleteDuplicates &&
            resolution.Choice is CollisionChoice.KeepExisting or
                CollisionChoice.KeepIncoming or CollisionChoice.KeepBoth)
        {
            run.TidyUp.AddRange(CopiesOf(file).Select(c => c.FullPath));
            if (existing != null) run.TidyUp.AddRange(CopiesOf(existing).Select(c => c.FullPath));
        }

        switch (resolution.Choice)
        {
            case CollisionChoice.Cancel:
                run.Cancelled = true;
                return (DuplicatePolicy.Skip,
                    new RelocationResult(false, "Cancelled.", file.FullPath));

            case CollisionChoice.Skip:
                run.Skipped++;
                return (DuplicatePolicy.Skip,
                    new RelocationResult(false, "Skipped — that name is taken.", file.FullPath));

            case CollisionChoice.KeepExisting:
                // The file at the destination is the one to keep, so this one does not move
                // — and is itself one of the copies to clear away.
                if (existing != null) run.Survivors.Add(existing); else run.SurvivorPaths.Add(desired);
                if (resolution.DeleteDuplicates) run.TidyUp.Add(file.FullPath);
                return (DuplicatePolicy.Skip, new RelocationResult(false,
                    "Kept the file already there.", desired, AlreadyPresent: true));

            case CollisionChoice.KeepIncoming:
                // Clear the way, then move in. Recycled rather than destroyed: the user is
                // choosing between two files, not throwing one away.
                await DeleteQuietlyAsync(new[] { desired }, toRecycleBin: true);
                run.Survivors.Add(file);
                return (DuplicatePolicy.Rename, null);

            default: // KeepBoth — the arrival takes a free name and both are spared.
                run.Survivors.Add(file);
                if (existing != null) run.Survivors.Add(existing); else run.SurvivorPaths.Add(desired);
                return (DuplicatePolicy.Rename, null);
        }
    }

    /// <summary>
    /// Clear away the copies the collision answers marked as redundant. Left until the end
    /// of the batch: deleting entries mid-run would pull them out from under an operation
    /// that is still walking its list. Returns how many went.
    /// </summary>
    private async Task<int> FinishCollisionRunAsync(CollisionRun run)
    {
        if (run.TidyUp.Count == 0) return 0;

        // Read the survivors' paths now, after the moves, since that is where they are.
        var keep = run.Survivors.Select(f => f.FullPath)
            .Concat(run.SurvivorPaths)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var removed = await DeleteQuietlyAsync(
            run.TidyUp.Where(p => !keep.Contains(p)), toRecycleBin: true);
        if (removed > 0) PersistAndRefresh();
        return removed;
    }

    /// <summary>
    /// Move one file into the library, putting a name already held by something *else* to
    /// the user rather than giving up on it.
    ///
    /// A copy of the same file already sitting there is not a collision: that comes back as
    /// already-present, which is what the offer to delete the redundant source is for. Only
    /// a genuinely different file with the same name is worth stopping for.
    /// </summary>
    private async Task<RelocationResult> FileInLibraryAsync(
        MediaFile file, string destDir, string? newName, bool deleteOriginal,
        CollisionRun run, IProgress<long> bytes)
    {
        var subtitles = ConsolidationSubtitlePolicy;

        var result = await FileRelocator.RelocateAsync(
            file, destDir, deleteOriginal, newName, DuplicatePolicy.Skip, bytes,
            subtitles: subtitles);
        if (!result.NameTaken) return result;

        var (policy, decided) = await AskAboutCollisionAsync(file, result.NewPath, run);
        if (decided != null) return decided;

        return await FileRelocator.RelocateAsync(
            file, destDir, deleteOriginal, newName, policy, bytes, subtitles: subtitles);
    }

    /// <summary>
    /// What happens to a subtitle sitting beside a file being filed: it comes along, or —
    /// when the user has said the library should hold only the media — it goes. Leaving it
    /// is not one of the choices, because a subtitle whose film has moved away is matched to
    /// nothing and will never be matched to anything again.
    /// </summary>
    private SubtitlePolicy ConsolidationSubtitlePolicy =>
        _settings.ConsolidateSubtitles ? SubtitlePolicy.Follow : SubtitlePolicy.Discard;

    /// <summary>Everything the user needs to decide a collision: both files and all their copies.</summary>
    private CollisionRequest BuildCollisionRequest(
        MediaFile incoming, string destinationPath, string operation)
    {
        var existing = EntryAt(destinationPath);
        var sameContent = existing != null && incoming.HasHash && existing.HasHash &&
                          string.Equals(existing.Sha256, incoming.Sha256, StringComparison.OrdinalIgnoreCase);

        return new CollisionRequest(
            incoming,
            destinationPath,
            existing,
            CopiesOf(incoming),
            existing != null ? CopiesOf(existing) : new List<MediaFile>(),
            sameContent,
            operation);
    }

    /// <summary>
    /// Delete files in the middle of a larger operation, which owns the progress bar and
    /// the saving. Entries whose file has gone are dropped from the catalogue; nothing is
    /// pushed onto the undo stack, since the operation around this records its own.
    /// </summary>
    private async Task<int> DeleteQuietlyAsync(IEnumerable<string> paths, bool toRecycleBin)
    {
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .ToList();
        if (list.Count == 0) return 0;

        var result = await Task.Run(() => FileDeleter.Delete(list, toRecycleBin));

        var gone = list.Where(p => !File.Exists(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (gone.Count > 0)
        {
            _catalog.Files.RemoveAll(f => gone.Contains(f.FullPath));
            _catalog.RebuildIndex();
            ExtraLinker.Link(_catalog.Files);
        }
        return result.Deleted;
    }

    /// <summary>
    /// Deep-check one file without disturbing whatever else is running. The collision
    /// dialog offers this mid-move, where taking over the progress bar and the
    /// cancellation token would pull the rug from under the operation that opened it.
    /// </summary>
    /// <param name="progress">
    /// How far through this one file the decode has got, 0 to 1. Decoding a film end to end
    /// is minutes of work on a single item, so a caller showing "checking 2 of 5" and
    /// nothing else is showing almost nothing.
    /// </param>
    public async Task<string> DeepCheckOneAsync(
        MediaFile file, CancellationToken ct = default, IProgress<double>? progress = null)
    {
        if (!CanDoVideo)
            return "A deep check needs FFmpeg and ffprobe — set them up on the External tools tab first.";
        if (!File.Exists(file.FullPath))
            return "That file is no longer on disk.";

        try
        {
            var engine = new ContentAnalysisEngine(_tools);

            // Said on the status bar as well as handed to the caller: a deep check started
            // from a dialog is still the program going quiet for minutes, and the main
            // window is where the user looks to see whether anything is happening.
            var relay = new Progress<AnalysisProgress>(p =>
            {
                progress?.Report(p.FileFraction);
                StatusText = ProgressLine("Deep checking", 0, 0, file.FileName,
                    p.FileFraction > 0 ? $"— {p.FileFraction:P0}" : "");
            });

            await engine.AnalyzeAsync(new[] { file }, fingerprint: false, deepCheck: true, relay, ct);
            StatusText = $"{file.FileName}: {file.Integrity}";
            return StatusText;
        }
        catch (OperationCanceledException)
        {
            StatusText = $"Deep check of {file.FileName} stopped.";
            return StatusText;
        }
        catch (Exception ex)
        {
            return $"Could not check {file.FileName}: {ex.Message}";
        }
    }

    /// <summary>Register the reversal of a move: put each file back where it came from.</summary>
    private void PushMoveUndo(IReadOnlyList<(MediaFile File, string FullPath, string FileName)> origins)
    {
        if (origins.Count == 0) return;

        Undo.Push($"move of {origins.Count} file(s)", async () =>
        {
            int back = 0, failed = 0;
            foreach (var origin in origins)
            {
                var dir = Path.GetDirectoryName(origin.FullPath);
                if (string.IsNullOrEmpty(dir)) { failed++; continue; }
                var result = await FileRelocator.RelocateAsync(
                    origin.File, dir, deleteOriginal: true, origin.FileName, DuplicatePolicy.Rename);
                if (result.Success) back++; else failed++;
            }
            PersistAndRefresh();
            return failed == 0
                ? $"Moved {back} file(s) back."
                : $"Moved {back} file(s) back, {failed} could not be returned.";
        });
    }

    /// <summary>What a batch file operation did.</summary>
    /// <param name="Failures">
    /// Why each file that did not move failed, in full. A count on its own leaves the user
    /// with a number and no idea whether the drive is unplugged, the folder is read-only or
    /// the disk is full — and those want three different things done about them.
    /// </param>
    private record OperationReport(
        int Succeeded, int Failed, List<MediaFile> AlreadyPresent, List<string>? Failures = null)
    {
        public IReadOnlyList<string> Reasons => Failures ?? new List<string>();
    }

    /// <summary>
    /// The failure detail appended to an operation's summary. Capped, since a batch that
    /// went wrong for two hundred files went wrong for the same reason two hundred times.
    /// </summary>
    private static string DescribeFailures(IReadOnlyList<string> reasons, int show = 8)
    {
        if (reasons.Count == 0) return "";
        var listed = string.Join("\n\n", reasons.Take(show).Select(r => "    " + r));
        if (reasons.Count > show) listed += $"\n\n    …and {reasons.Count - show} more.";
        return "\n\nWhat went wrong:\n\n" + listed;
    }

    /// <summary>
    /// Run a copy/move over many files with a byte-accurate progress bar and ETA. The
    /// per-file work is supplied by the caller so consolidation and plain moves share this.
    /// </summary>
    private async Task<OperationReport> RunFileOperationAsync(
        IReadOnlyList<MediaFile> files,
        string verb,
        Func<MediaFile, IProgress<long>, Task<RelocationResult>> operation)
    {
        var totalBytes = Math.Max(1, files.Sum(f => f.SizeBytes));
        long doneBytes = 0;

        IsScanning = true;
        BeginTiming();
        ProgressValue = 0;
        ProgressMax = 1000;   // permille of the batch, so the bar moves within a big file

        var present = new List<MediaFile>();
        var failures = new List<string>();
        int ok = 0, failed = 0;
        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                var fileStart = doneBytes;
                StatusText = ProgressLine(verb, i + 1, files.Count, file.FileName);

                var bytes = new Progress<long>(written =>
                {
                    doneBytes += written;
                    ProgressValue = (int)Math.Min(1000, doneBytes * 1000 / totalBytes);
                    UpdateEta(doneBytes, totalBytes);
                });

                var result = await operation(file, bytes);

                // Keep the tally honest whatever happened to this file.
                doneBytes = fileStart + file.SizeBytes;
                ProgressValue = (int)Math.Min(1000, doneBytes * 1000 / totalBytes);

                if (result.AlreadyPresent && !result.Success) present.Add(file);
                else if (result.Success) { ok++; }
                else
                {
                    failed++;
                    // "Cancelled." and "Skipped." are answers, not failures — the user
                    // already knows what happened to those.
                    if (result.Message is { Length: > 0 } why &&
                        !why.StartsWith("Cancelled", StringComparison.OrdinalIgnoreCase) &&
                        !why.StartsWith("Skipped", StringComparison.OrdinalIgnoreCase))
                        failures.Add(why);
                }
            }
            CatalogStore.Save(_catalog, _catalogPath);
        }
        finally
        {
            EndTiming();
            IsScanning = false;
            RebuildRows();
        }

        return new OperationReport(ok, failed, present, failures);
    }

    // --- Catalogue refresh ------------------------------------------------

    /// <summary>How many catalogue entries a refresh would re-derive.</summary>
    public int StaleEntryCount => CatalogRefresher.CountStale(_catalog);

    /// <summary>How many titles a refresh would try to confirm or date.</summary>
    public int UnverifiedTitleCount => CatalogRefresher.CountUnverified(_catalog);

    /// <summary>How many folder rules are still waiting to be written onto their files.</summary>
    public int PendingFolderRuleCount =>
        _settings.FolderCategoryRules.Count + _settings.FolderTitleRules.Count;

    /// <summary>
    /// Re-derive metadata for entries that predate the current feature set (new
    /// categories, extras linking, title parsing) without re-walking drives or re-hashing,
    /// then confirm unverified titles against IMDb — falling back to TMDb — and fill in
    /// any missing years.
    /// </summary>
    public async Task<string> RefreshCatalogAsync(bool verifyTitles = true)
    {
        _cts = new CancellationTokenSource();
        IsScanning = true;
        BeginTiming();
        ProgressValue = 0;
        ProgressMax = Math.Max(1, StaleEntryCount);

        var progress = new Progress<RefreshProgress>(p =>
        {
            ProgressMax = Math.Max(1, p.Total);
            ProgressValue = p.Done;
            UpdateEta(p.Done, p.Total);
            StatusText = ProgressLine(p.Phase, p.Done, p.Total, p.Current);
        });

        try
        {
            var verifier = verifyTitles ? await BuildVerifierAsync(_cts.Token) : null;
            var report = await Task.Run(() =>
                CatalogRefresher.RefreshAsync(_catalog, _settings, verifier, progress, _cts.Token));

            CatalogStore.Save(_catalog, _catalogPath);
            _settings.Save(_settingsPath);   // folder rules retired during the migration
            _tmdbCache.Save(_tmdbCachePath);

            var parts = new List<string>
            {
                $"{report.Refreshed} entr(ies) updated",
                $"{report.Skipped} already current",
                $"{report.Linked} extra(s) linked",
                $"{report.Shared} value(s) shared with duplicates"
            };
            if (report.Numbered > 0) parts.Add($"{report.Numbered} gained a season/episode");
            if (report.Unnumbered > 0)
                parts.Add($"{report.Unnumbered} had a season/episode cleared (not programmes)");
            if (report.Adopted > 0) parts.Add($"{report.Adopted} took a folder rule onto the file itself");
            if (report.RulesRetired > 0) parts.Add($"{report.RulesRetired} folder rule(s) retired");
            if (report.Pruned > 0) parts.Add($"{report.Pruned} dropped by exclusions");

            StatusText = "Catalogue refresh: " + string.Join(", ", parts) + ".";
            if (report.Verified is { } v) StatusText += "\n" + v.Describe();
        }
        catch (OperationCanceledException)
        {
            CatalogStore.Save(_catalog, _catalogPath);
            StatusText = "Catalogue refresh cancelled. Partial results saved.";
        }
        catch (Exception ex)
        {
            StatusText = $"Catalogue refresh failed: {ex.Message}";
        }
        finally
        {
            EndTiming();
            IsScanning = false;
            _cts?.Dispose();
            _cts = null;
            RebuildRows();
        }
        return StatusText;
    }

    // --- IMDb -------------------------------------------------------------

    /// <summary>True once <c>IMDBData.tsv</c> exists and can be searched.</summary>
    public bool HasImdbData => _imdb.IsAvailable;

    /// <summary>The raw IMDb download, if one is sitting in the app folder waiting to be extracted.</summary>
    public string? ImdbSourceFile =>
        ImdbExtractor.FindSource(AppPaths.ImdbSourcePath, AppPaths.ImdbSourceGzPath);

    /// <summary>
    /// True when the raw IMDb file is present but the extract isn't — the one case where
    /// extraction should be offered without being asked for.
    /// </summary>
    public bool ImdbExtractionPending => !HasImdbData && ImdbSourceFile != null;

    public string ImdbStatus =>
        !HasImdbData
            ? ImdbSourceFile != null
                ? "IMDb: title.basics.tsv found, not yet extracted"
                : "IMDb: no data"
            : _imdb.IsLoaded
                ? $"IMDb: {_imdb.Count:N0} titles in memory"
                : "IMDb: reading from disk";

    /// <summary>
    /// Boil <c>title.basics.tsv</c> (or its gzip) down to <c>IMDBData.tsv</c>. The source
    /// is well over a gigabyte and is streamed a line at a time, never loaded.
    /// </summary>
    public async Task<string> ExtractImdbDataAsync()
    {
        var source = ImdbSourceFile;
        if (source == null)
            return "Put IMDb's title.basics.tsv (or title.basics.tsv.gz) in the program folder " +
                   $"({AppPaths.DataDirectory}) first. It is downloaded from " +
                   "https://datasets.imdbws.com/title.basics.tsv.gz";

        _cts = new CancellationTokenSource();
        IsScanning = true;
        BeginTiming();
        ProgressValue = 0;
        ProgressMax = 1000;

        var progress = new Progress<ImdbExtractProgress>(p =>
        {
            if (p.BytesTotal > 0)
            {
                ProgressValue = (int)Math.Min(1000, p.BytesRead * 1000 / p.BytesTotal);
                UpdateEta(p.BytesRead, p.BytesTotal);
            }
            StatusText = $"Extracting IMDb titles — {p.LinesKept:N0} kept";
        });

        try
        {
            _imdb.Unload();
            var report = await ImdbExtractor.ExtractAsync(
                source, AppPaths.ImdbDataPath, progress, _cts.Token);
            StatusText = $"IMDb extract written: {report.Kept:N0} titles kept, " +
                         $"{report.Skipped:N0} rows skipped (unnamed episodes and broadcast " +
                         "timestamps, which match nothing anyone searches for).";
            await EnsureImdbLoadedAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "IMDb extraction cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"IMDb extraction failed: {ex.Message}";
        }
        finally
        {
            EndTiming();
            IsScanning = false;
            _cts?.Dispose();
            _cts = null;
            OnPropertyChanged(nameof(HasImdbData));
            OnPropertyChanged(nameof(ImdbStatus));
        }
        return StatusText;
    }

    /// <summary>
    /// True when there is no IMDb data at all — neither our extract nor the raw download
    /// waiting to be turned into one. The one case worth offering to fetch it.
    /// </summary>
    public bool NeedsImdbDownload => !HasImdbData && ImdbSourceFile == null;

    /// <summary>
    /// Fetch <c>title.basics.tsv.gz</c> from the configured address and boil it down to
    /// the extract, so the user needn't go and find the file themselves. The download runs
    /// to a temporary name and is only kept once it has arrived whole.
    /// </summary>
    public async Task<string> DownloadImdbDataAsync()
    {
        if (IsScanning) return "Something else is running — wait for it to finish first.";

        _cts = new CancellationTokenSource();
        IsScanning = true;
        BeginTiming();
        ProgressValue = 0;
        ProgressMax = 1000;

        var progress = new Progress<ImdbDownloadProgress>(p =>
        {
            if (p.BytesTotal > 0)
            {
                ProgressValue = (int)Math.Min(1000, p.BytesRead * 1000 / p.BytesTotal);
                UpdateEta(p.BytesRead, p.BytesTotal);
                StatusText = $"Downloading IMDb titles — {Format.Bytes(p.BytesRead)} of " +
                             $"{Format.Bytes(p.BytesTotal)}";
            }
            else
            {
                StatusText = $"Downloading IMDb titles — {Format.Bytes(p.BytesRead)} so far";
            }
        });

        try
        {
            await ImdbDownloader.DownloadAsync(
                _settings.EffectiveImdbDownloadUrl, AppPaths.ImdbSourceGzPath, progress, _cts.Token);
            StatusText = "IMDb download finished — extracting…";
            OnPropertyChanged(nameof(ImdbSourceFile));
        }
        catch (OperationCanceledException)
        {
            StatusText = "IMDb download cancelled.";
            return StatusText;
        }
        catch (Exception ex)
        {
            StatusText = $"IMDb download failed: {ex.Message}";
            return StatusText;
        }
        finally
        {
            EndTiming();
            IsScanning = false;
            _cts?.Dispose();
            _cts = null;
            OnPropertyChanged(nameof(NeedsImdbDownload));
        }

        // The download on its own is not usable; the extract is what lookups read.
        return await ExtractImdbDataAsync();
    }

    /// <summary>
    /// Hold the extract in memory when the user has asked for that, so lookups don't
    /// re-read a large file. A no-op when the option is off or it is already loaded.
    /// </summary>
    private async Task EnsureImdbLoadedAsync(CancellationToken ct)
    {
        if (!_settings.ImdbInMemory) { _imdb.Unload(); return; }
        if (!_imdb.IsAvailable || _imdb.IsLoaded) return;

        StatusText = "Loading IMDb titles into memory…";
        await _imdb.LoadAsync(
            new Progress<long>(n => StatusText = $"Loading IMDb titles into memory — {n:N0}…"), ct);
        OnPropertyChanged(nameof(ImdbStatus));
    }

    /// <summary>
    /// The thing that confirms titles: the local IMDb extract first, TMDb only for what
    /// it cannot answer. Extraction is done here if the raw file is present and the
    /// extract isn't, so the first refresh after dropping the download in just works.
    /// </summary>
    private async Task<TitleVerifier> BuildVerifierAsync(CancellationToken ct)
    {
        if (ImdbExtractionPending)
        {
            StatusText = "Extracting IMDb titles (first run only)…";
            try
            {
                await ImdbExtractor.ExtractAsync(ImdbSourceFile!, AppPaths.ImdbDataPath, null, ct);
                OnPropertyChanged(nameof(HasImdbData));
            }
            catch (OperationCanceledException) { throw; }
            catch { /* carry on without it; TMDb still works */ }
        }

        await EnsureImdbLoadedAsync(ct);

        // TMDb is the fallback of last resort, and only when there is no local data at all.
        // It answers one query every two seconds, so a library of any size spends hours
        // there to reach an answer IMDBData.tsv gives in one pass over a local file — which
        // makes "use it as well" a choice nobody would knowingly make. When the extract
        // exists it is the whole answer, and TMDb is not consulted.
        var tmdb = HasImdbData ? null : BuildTmdbClient();

        return new TitleVerifier(_imdb, tmdb, _settings.UseImdbFirst);
    }

    /// <summary>The TMDb client, or null when no credentials have been entered.</summary>
    private TmdbClient? BuildTmdbClient() =>
        HasTmdbCredentials
            ? new TmdbClient(_settings.TmdbApiKey, _settings.TmdbReadAccessToken,
                _tmdbCache, new RateLimiter(TimeSpan.FromSeconds(2)))
            : null;

    /// <summary>True when a TMDb API key or read token has been entered.</summary>
    public bool HasTmdbCredentials =>
        !string.IsNullOrWhiteSpace(_settings.TmdbApiKey) ||
        !string.IsNullOrWhiteSpace(_settings.TmdbReadAccessToken);

    /// <summary>
    /// True when TMDb would not be consulted even if asked, because the local extract can
    /// answer everything it would be asked. Lets the UI say so rather than starting an
    /// hours-long job that had a local answer all along.
    /// </summary>
    public bool TmdbSupersededByImdb => HasImdbData;

    /// <summary>
    /// Confirm titles and fill in missing years for the given rows (or the whole
    /// catalogue when nothing is selected), IMDb first and TMDb only as a fallback.
    /// </summary>
    public async Task<string> VerifyTitlesAsync(IReadOnlyList<FileRow> rows)
    {
        var targets = (rows.Count > 0 ? rows.Select(r => r.Model) : _catalog.Files).ToList();

        _cts = new CancellationTokenSource();
        IsScanning = true;
        BeginTiming();
        ProgressValue = 0;
        ProgressMax = 1;

        var progress = new Progress<VerifyProgress>(p =>
        {
            ProgressMax = Math.Max(1, p.Total);
            ProgressValue = p.Done;
            UpdateEta(p.Done, p.Total);
            StatusText = ProgressLine(p.Phase, p.Done, p.Total, p.Current);
        });

        try
        {
            var verifier = await BuildVerifierAsync(_cts.Token);
            var report = await verifier.VerifyAsync(targets, progress, _cts.Token);
            _tmdbCache.Save(_tmdbCachePath);
            CatalogStore.Save(_catalog, _catalogPath);
            StatusText = report.Describe();
        }
        catch (OperationCanceledException)
        {
            _tmdbCache.Save(_tmdbCachePath);
            CatalogStore.Save(_catalog, _catalogPath);
            StatusText = "Title verification cancelled. Progress saved.";
        }
        catch (Exception ex)
        {
            StatusText = $"Title verification failed: {ex.Message}";
        }
        finally
        {
            EndTiming();
            IsScanning = false;
            _cts?.Dispose();
            _cts = null;
            RebuildRows();
        }
        return StatusText;
    }

    // --- TMDb validation --------------------------------------------------

    public async Task<string> ValidateTvAsync(IReadOnlyList<FileRow> rows)
    {
        if (!HasTmdbCredentials)
            return "Enter a TMDb API key or Read Access Token in Settings first.";
        if (TmdbSupersededByImdb)
            return "IMDBData.tsv is present, so there is nothing TMDb can add — and it " +
                   "answers one query every two seconds. Use Verify titles instead, which " +
                   "reads the local data in a single pass.";

        var models = (rows.Count > 0 ? rows.Select(r => r.Model) : _catalog.Files).ToList();

        _cts = new CancellationTokenSource();
        IsScanning = true;
        ProgressValue = 0;
        ProgressMax = 1;

        var progress = new Progress<ValidationProgress>(p =>
        {
            ProgressMax = Math.Max(1, p.Total);
            ProgressValue = p.Done;
            StatusText = ProgressLine("Validating TV names", p.Done, p.Total, p.Current);
        });

        try
        {
            var limiter = new RateLimiter(TimeSpan.FromSeconds(2)); // 1 query / 2s
            var client = new TmdbClient(_settings.TmdbApiKey, _settings.TmdbReadAccessToken, _tmdbCache, limiter);
            var validator = new TvNameValidator(client);
            var count = await validator.ValidateManyAsync(models, progress, _cts.Token);
            _tmdbCache.Save(_tmdbCachePath);
            CatalogStore.Save(_catalog, _catalogPath);
            StatusText = $"TMDb validation complete: {count} TV title(s) confirmed.";
        }
        catch (OperationCanceledException)
        {
            _tmdbCache.Save(_tmdbCachePath);
            CatalogStore.Save(_catalog, _catalogPath);
            StatusText = "TMDb validation cancelled. Progress saved.";
        }
        catch (Exception ex)
        {
            StatusText = $"TMDb validation failed: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            _cts?.Dispose();
            _cts = null;
            RebuildRows();
        }
        return StatusText;
    }

    // --- Settings ---------------------------------------------------------

    /// <summary>
    /// Drive roots offered in Settings for watching: the ones attached now, plus any the
    /// catalogue already knows about so an unplugged drive's setting is not lost.
    /// </summary>
    public IReadOnlyList<string> AvailableDriveRoots =>
        DriveScanner.GetAvailableDrives().Select(d => d.Path)
            .Concat(_settings.ScanDrives)
            .Concat(_catalog.Files.Select(f => Path.GetPathRoot(f.FullPath) ?? ""))
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public void ApplyAppSettings(AppSettings settings)
    {
        _settings = settings;
        _settings.SyncLegacyFolders();
        _settings.Save(_settingsPath);
        StartupManager.Apply(_settings.StartWithWindows, _settings.StartInTray);
        StartWatchingIfEnabled(_lastRoots);

        // Turning the memory option off should give the memory back straight away; turning
        // it on costs a load, which is left until something actually needs a lookup.
        if (!_settings.ImdbInMemory) _imdb.Unload();
        OnPropertyChanged(nameof(ImdbStatus));

        OnPropertyChanged(nameof(Categories));
        RefreshFilterValues();   // custom categories are one of the filter columns' values
        RebuildRows();
        StatusText = "Settings saved.";
    }

    /// <summary>Persist a settings change made outside the dialog (e.g. column layout).</summary>
    public void SaveSettings() => _settings.Save(_settingsPath);

    // --- New-file watching + notifications --------------------------------

    private void StartWatchingIfEnabled(IEnumerable<string> roots)
    {
        StopWatching();
        if (!_settings.WatchForNewFiles) return;

        List<string> rootList;

        if (_settings.HasExplicitWatchTargets)
        {
            // Named somewhere in particular — whole drives, particular folders, or both.
            // Taken literally and nothing added to it: watching E:\dump\ and watching all
            // of E: are very different requests, and somebody who asked for the first did
            // not ask for the second.
            rootList = _settings.WatchedDrives
                .Concat(_settings.WatchedFolders)
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        else
        {
            // Nothing named, so fall back on what was scanned — which is what earlier
            // versions did — plus the folders added by hand, wherever they live.
            rootList = roots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().ToList();
            if (rootList.Count == 0)
                rootList = _catalog.Files.Select(f => Path.GetPathRoot(f.FullPath) ?? "")
                    .Where(r => r.Length > 0).Distinct().ToList();

            rootList = rootList.Concat(_settings.AdditionalScanFolders)
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        if (rootList.Count == 0) return;

        _watcher = new NewFileWatcher(rootList, path =>
            Application.Current?.Dispatcher.Invoke(() => OnWatchedFile(path)));
    }

    private void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    private void OnWatchedFile(string path)
    {
        try
        {
            if (_catalog.ByPath.ContainsKey(path)) return;
            var ext = Path.GetExtension(path);
            if (_settings.IsPathExcluded(path) || _settings.IsExtensionIgnored(ext)) return;

            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0) return;

            var entry = new MediaFile
            {
                FullPath = path, FileName = info.Name, Extension = info.Extension,
                SizeBytes = info.Length, LastModifiedUtc = info.LastWriteTimeUtc,
                IndexedUtc = DateTime.UtcNow, Integrity = IntegrityStatus.Ok,
                FeatureVersion = CatalogRefresher.CurrentFeatureVersion,
                // A file that has only just appeared may still be downloading, so its
                // size and hash cannot be trusted until it settles.
                AwaitingDownload = true
            };
            MediaClassifier.Classify(entry, _settings);
            _catalog.Files.Add(entry);
            _catalog.ByPath[path] = entry;
            ExtraLinker.Link(_catalog.Files);
            CatalogStore.Save(_catalog, _catalogPath);
            RebuildRows();

            QueueNewFileNotice(info.Name);
            StatusText = $"New file detected and added: {info.Name}";

            _ = HashNewFileAsync(entry); // hash it once it has stopped growing
        }
        catch { /* a watcher hiccup must never crash the app */ }
    }

    // --- Telling the user about new files -----------------------------------
    //
    // Files arrive in handfuls. A download finishing writes one file; a folder being copied
    // in writes forty, within a second of each other, and forty notifications about that is
    // thirty-nine too many — it is one thing that happened, not forty.
    //
    // So the first arrival starts a short wait and everything landing during it joins the
    // same message. The wait is not extended by later arrivals: it is there to gather a
    // burst, not to hold the news back until the copying finally stops.

    private readonly List<string> _pendingNotices = new();
    private System.Windows.Threading.DispatcherTimer? _noticeTimer;

    private void QueueNewFileNotice(string fileName)
    {
        _pendingNotices.Add(fileName);
        if (_noticeTimer is { IsEnabled: true }) return;   // already gathering this burst

        var seconds = Math.Clamp(_settings.NewFileNotifyDelaySeconds, 1, 600);
        _noticeTimer ??= CreateNoticeTimer();
        _noticeTimer.Interval = TimeSpan.FromSeconds(seconds);
        _noticeTimer.Start();
    }

    private System.Windows.Threading.DispatcherTimer CreateNoticeTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer();
        timer.Tick += (_, _) => FlushNewFileNotices();
        return timer;
    }

    private void FlushNewFileNotices()
    {
        _noticeTimer?.Stop();

        var names = _pendingNotices.ToList();
        _pendingNotices.Clear();
        if (names.Count == 0) return;

        Notify?.Invoke("Media Catalog", names.Count == 1
            ? $"Added new file: {names[0]}"
            : $"Added {names.Count} new files, starting with {names[0]}.");
    }

    /// <summary>
    /// Hash a newly spotted file once its size has stopped changing, so a half-downloaded
    /// file is not recorded with the hash of its first few megabytes. Gives up waiting
    /// after a while and leaves it flagged for a later re-hash.
    /// </summary>
    private async Task HashNewFileAsync(MediaFile entry)
    {
        var settled = await WaitForStableSizeAsync(entry.FullPath, TimeSpan.FromMinutes(30));
        if (settled == null) return;   // vanished, or still growing when we gave up

        var hash = await FileHasher.ComputeSha256Async(entry.FullPath);
        if (string.IsNullOrEmpty(hash)) return;

        Application.Current?.Dispatcher.Invoke(() =>
        {
            entry.Sha256 = hash;
            entry.SizeBytes = settled.Value;
            entry.AwaitingDownload = false;
            MediaClassifier.Classify(entry, _settings);
            ExtraLinker.Link(_catalog.Files);
            CatalogStore.Save(_catalog, _catalogPath);
            RebuildRows();
        });
    }

    /// <summary>
    /// Watch a file's length until it stops changing, and return the settled size. Null if
    /// the file disappeared or was still growing when <paramref name="giveUpAfter"/> passed.
    /// </summary>
    private static async Task<long?> WaitForStableSizeAsync(string path, TimeSpan giveUpAfter)
    {
        var deadline = DateTime.UtcNow + giveUpAfter;
        long previous = -1;
        var stableChecks = 0;

        while (DateTime.UtcNow < deadline)
        {
            long length;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return null;
                length = info.Length;
            }
            catch { return null; }

            // Two quiet checks in a row, and the file can also be opened for reading:
            // together that is a good sign nothing is still writing to it.
            if (length == previous && length > 0)
            {
                stableChecks++;
                if (stableChecks >= 2 && CanOpenForRead(path)) return length;
            }
            else stableChecks = 0;

            previous = length;
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
        return null;
    }

    private static bool CanOpenForRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Files the watcher added that never got a trustworthy hash.</summary>
    public int PendingRehashCount =>
        _catalog.Files.Count(f => f.AwaitingDownload || !f.HasHash);

    /// <summary>
    /// Re-check files that were catalogued while they may still have been downloading:
    /// refresh their size, re-hash them, and re-derive what the name implies.
    /// </summary>
    public Task<string> RehashPendingAsync() =>
        RehashAsync(_catalog.Files.Where(f => f.AwaitingDownload || !f.HasHash).ToList());

    /// <summary>
    /// Re-read and re-hash specific files. Used both for the download-aware pending list
    /// and to retry the files a scan could not read.
    /// </summary>
    public async Task<string> RehashAsync(IReadOnlyList<MediaFile> targets)
    {
        if (targets.Count == 0) return "Every catalogued file already has a trustworthy hash.";

        _cts = new CancellationTokenSource();
        IsScanning = true;
        BeginTiming();
        ProgressValue = 0;
        ProgressMax = targets.Count;

        int done = 0, rehashed = 0, missing = 0;
        try
        {
            foreach (var file in targets)
            {
                _cts.Token.ThrowIfCancellationRequested();
                StatusText = ProgressLine("Re-hashing", done + 1, targets.Count, file.FileName);
                ProgressValue = done;
                UpdateEta(done, targets.Count);
                done++;

                if (!File.Exists(file.FullPath)) { missing++; continue; }

                var info = new FileInfo(file.FullPath);
                var hash = await FileHasher.ComputeSha256Async(file.FullPath, _cts.Token);
                if (string.IsNullOrEmpty(hash)) { file.HashFailed = true; continue; }

                file.SizeBytes = info.Length;
                file.LastModifiedUtc = info.LastWriteTimeUtc;
                file.Sha256 = hash;
                file.AwaitingDownload = false;
                file.HashFailed = false;
                MediaClassifier.Classify(file, _settings);
                rehashed++;
            }
            ExtraLinker.Link(_catalog.Files);
            DuplicateMetadata.Propagate(_catalog.Files);
            CatalogStore.Save(_catalog, _catalogPath);

            // Whatever still refuses stays on the list, so the user can see what is left.
            UnhashedFiles = targets.Where(f => !f.HasHash && File.Exists(f.FullPath)).ToList();
            var stillFailing = UnhashedFiles.Count;
            StatusText = $"Re-hashed {rehashed} file(s)" +
                         (missing > 0 ? $"; {missing} no longer exist" : "") +
                         (stillFailing > 0 ? $"; {stillFailing} still could not be read." : ".");
        }
        catch (OperationCanceledException)
        {
            CatalogStore.Save(_catalog, _catalogPath);
            StatusText = $"Re-hash cancelled after {rehashed} file(s). Progress saved.";
        }
        catch (Exception ex)
        {
            StatusText = $"Re-hash failed: {ex.Message}";
        }
        finally
        {
            EndTiming();
            IsScanning = false;
            _cts?.Dispose();
            _cts = null;
            RebuildRows();
        }
        return StatusText;
    }

    /// <summary>Release the watcher and remember the filters on shutdown.</summary>
    public void Shutdown()
    {
        SaveFilters();
        StopWatching();
        _noticeTimer?.Stop();
        _pendingNotices.Clear();
    }

    private void RaiseCommandStates()
    {
        (ScanCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ResumeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PauseCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
