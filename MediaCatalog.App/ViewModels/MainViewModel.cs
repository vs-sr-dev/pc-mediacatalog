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
    Duplicates, NearDuplicates, Problems
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
        "Name", "Kind", "Category", "Title", "Year", "S/E", "Size", "Integrity", "Path",
        "Dup", "TMDb", "Filed"
    };

    public Array Columns => FilterColumns;

    private bool _filterNegate;

    public string FilterColumn
    {
        get => _filterColumn;
        set { if (SetProperty(ref _filterColumn, value)) ApplyFilter(); }
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

    /// <summary>Committed filter clauses (all must match; each may be negated).</summary>
    public ObservableCollection<FilterClause> ActiveFilters { get; } = new();

    /// <summary>Commit the current column/pattern/negate as a stacked filter clause.</summary>
    public void AddCurrentFilter()
    {
        if (string.IsNullOrEmpty(_filterPattern)) return;
        ActiveFilters.Add(new FilterClause
        {
            Column = _filterColumn, Pattern = _filterPattern, Negate = _filterNegate
        });
        FilterPattern = ""; // clears and re-applies
        SaveFilters();
    }

    public void RemoveFilter(FilterClause clause)
    {
        ActiveFilters.Remove(clause);
        ApplyFilter();
        SaveFilters();
    }

    public void ClearFilters()
    {
        ActiveFilters.Clear();
        FilterPattern = "";
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
    /// </summary>
    private string ProgressLine(string phase, int done, int total, string currentFile)
    {
        if (total <= 0) return phase;
        var counted = $"{phase}: {done}/{total}";
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
        var m = WildcardMatcher.IsMatch(row.ColumnValue(clause.Column), clause.Pattern);
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
        foreach (var folder in plan.Folders.Where(f =>
                     !_settings.AdditionalScanFolders.Contains(f, StringComparer.OrdinalIgnoreCase)))
            _settings.AdditionalScanFolders.Add(folder);
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
                waitForMissingRoots: waitForMissingDrives, ct: _cts.Token));

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
                pruneMissing: false, ct: _cts.Token));

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
        foreach (var f in _catalog.Files)
        {
            // Hide files under excluded folders or with ignored extensions (they stay in
            // the catalogue but drop out of the results until a rescan prunes them).
            if (_settings.IsPathExcluded(f.FullPath) || _settings.IsExtensionIgnored(f.Extension))
                continue;

            var category = CategoryResolver.Effective(f, _settings);

            // Being filed is a fact about where the file is, so it is re-derived rather
            // than trusted: consolidation folders change, files get moved, and a corrected
            // title moves the goalposts — a file under the old title's folder is in the
            // library but no longer in the right place in it.
            f.Consolidated = ConsolidationPlanner.IsCorrectlyFiled(f, category, _settings);

            var row = new FileRow(f) { Category = category };
            _allRows.Add(row);
            rowByPath[f.FullPath] = row;
        }

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

        var video = _catalog.Files.Count(f => f.Kind == MediaKind.Video);
        var audio = _catalog.Files.Count(f => f.Kind == MediaKind.Audio);
        var nearPart = nearGroups.Count > 0 ? $"  •  {nearGroups.Count} near-dup sets" : "";

        // Duplicate detection is by content hash, so anything unhashed is invisible to it.
        // Say so rather than quietly under-reporting.
        var unhashed = _catalog.Files.Count(f => !f.HasHash);
        var unhashedPart = unhashed > 0
            ? $"  •  ⚠ {unhashed} not hashed (use Re-hash pending)"
            : "";

        SummaryText =
            $"{_catalog.Files.Count} files  •  {video} video, {audio} audio  •  " +
            $"{groups.Count} duplicate sets ({Format.Bytes(reclaimable)} reclaimable){nearPart}{unhashedPart}";

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
            StatusText = $"{(deepCheck ? "Deep checking" : "Analysing")} {p.Done}/{p.Total} — {p.CurrentFile}";
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
                MediaClassifier.Classify(entry);
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

        PushFieldUndo($"category '{category}' on {affected.Count} file(s)", affected,
            f => f.CategoryOverride, (f, old) => f.CategoryOverride = old);

        foreach (var f in affected) f.CategoryOverride = category;
        CatalogStore.Save(_catalog, _catalogPath);
        RebuildRows();

        StatusText = duplicates.Count > 0
            ? $"Set category '{category}' on {targets.Count} file(s) and {duplicates.Count} duplicate(s)."
            : $"Set category '{category}' on {targets.Count} file(s).";
    }

    /// <summary>
    /// Snapshot one field of each file so the change can be put back, and register the
    /// reversal on the undo stack.
    /// </summary>
    private void PushFieldUndo<T>(
        string description,
        IReadOnlyList<MediaFile> files,
        Func<MediaFile, T> read,
        Action<MediaFile, T> write)
    {
        var before = files.Select(f => (File: f, Value: read(f))).ToList();
        Undo.Push(description, () =>
        {
            foreach (var (file, value) in before) write(file, value);
            ExtraLinker.Link(_catalog.Files);
            PersistAndRefresh();
            return Task.FromResult($"Reverted {description}.");
        });
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

    /// <summary>
    /// Apply a hand-typed title to the selected files and to their byte-identical copies —
    /// a copy of a file that had no title yet would otherwise be left behind. A corrected
    /// title counts as validated, like a TMDb result does.
    /// </summary>
    public string SetTitleForFiles(IReadOnlyList<FileRow> rows, string newTitle)
    {
        if (rows.Count == 0 || string.IsNullOrWhiteSpace(newTitle)) return "Nothing to update.";
        return SetTitleForModels(rows.Select(r => r.Model).ToList(), newTitle);
    }

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

        var changed = TitleUpdater.Set(withDuplicates, title, manual: true);

        // Extras take their title from what they hang off, so refresh the links.
        ExtraLinker.Link(_catalog.Files);

        // The name on disk follows the title. A corrected title that leaves the old name
        // in place is only half a correction — and the old name is what the next scan
        // would read the title back out of.
        var renamed = RenameToMatchTitles(withDuplicates);

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
    private List<(MediaFile File, string PreviousPath, string PreviousName)> RenameToMatchTitles(
        IEnumerable<MediaFile> files)
    {
        var renamed = new List<(MediaFile, string, string)>();
        if (!_settings.RenameOnTitleChange) return renamed;

        foreach (var file in files)
        {
            if (!File.Exists(file.FullPath)) continue;

            var proposal = RenameService.BuildProposal(file, CategoryResolver.Effective(file, _settings));
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
    /// Set (or clear) the season and episode by hand. Duplicates of the same content get
    /// the same numbering, and the category follows: a file with an episode number is an
    /// episode of something.
    /// </summary>
    public string SetSeasonEpisode(IReadOnlyList<FileRow> rows, int? season, int? episode)
    {
        if (rows.Count == 0) return "Nothing selected.";

        var targets = rows.Select(r => r.Model).ToList();
        var affected = targets.Concat(DuplicatesOf(targets)).Distinct().ToList();

        var before = affected.Select(f => (File: f, f.Season, f.Episode)).ToList();
        Undo.Push($"season/episode on {affected.Count} file(s)", () =>
        {
            foreach (var b in before) { b.File.Season = b.Season; b.File.Episode = b.Episode; }
            PersistAndRefresh();
            return Task.FromResult("Reverted the season/episode change.");
        });

        foreach (var f in affected)
        {
            f.Season = season;
            f.Episode = episode;
        }
        CatalogStore.Save(_catalog, _catalogPath);
        RebuildRows();

        var what = season is null && episode is null
            ? "Cleared season/episode"
            : $"Set S{season?.ToString("00") ?? "--"}E{episode?.ToString("00") ?? "--"}";
        StatusText = $"{what} on {affected.Count} file(s).";
        return StatusText;
    }

    /// <summary>
    /// Every field of a catalogue entry the user may correct, as the editor collected them.
    /// The file's own facts — its name, its date on disk, what a decode made of it — belong
    /// to that one file; what the content *is* belongs to every byte-identical copy of it.
    /// </summary>
    public record FileEdits(
        string Title,
        int? Year,
        int? Season,
        int? Episode,
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
            f.TitleManuallySet, f.Year, f.Season, f.Episode, f.CategoryOverride)).ToList();
        var previousModified = file.LastModifiedUtc;
        var previousIntegrity = file.Integrity;
        var previousKind = file.Kind;
        var previousPath = file.FullPath;
        var previousName = file.FileName;

        var changes = new List<string>();
        var title = (edits.Title ?? string.Empty).Trim();
        var titleChanged = !string.Equals(title, file.EffectiveTitle.Trim(), StringComparison.Ordinal);

        foreach (var copy in copies)
        {
            if (titleChanged && title.Length > 0)
            {
                copy.TmdbName = title;
                copy.TitleManuallySet = true;
                copy.TmdbVerified = false;
                copy.ImdbVerified = false;
            }
            copy.Year = edits.Year;
            copy.Season = edits.Season;
            copy.Episode = edits.Episode;
            copy.CategoryOverride = (edits.Category ?? string.Empty).Trim();
        }
        if (titleChanged && title.Length > 0) changes.Add($"title '{title}'");

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

        // An explicit name wins; otherwise a changed title renames the file to match, which
        // is what the naming scheme is for.
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
            renamed = RenameToMatchTitles(copies);
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
                b.File.CategoryOverride = b.CategoryOverride;
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

    /// <summary>Rename a file on disk, keeping the catalogue entry in step.</summary>
    public string RenameFile(FileRow row, string newName)
    {
        var file = row.Model;
        var name = (newName ?? string.Empty).Trim();
        if (name.Length == 0) return "Enter a file name.";
        if (string.Equals(name, file.FileName, StringComparison.Ordinal)) return "The name is unchanged.";

        var invalid = Path.GetInvalidFileNameChars().Where(c => name.Contains(c)).ToList();
        if (invalid.Count > 0)
            return "A file name cannot contain: " + string.Join(' ', invalid.Select(c => c.ToString()));

        var dir = Path.GetDirectoryName(file.FullPath) ?? "";
        var previousPath = file.FullPath;
        var previousName = file.FileName;

        var result = RenameService.Apply(new RenameProposal
        {
            File = file,
            CurrentName = file.FileName,
            ProposedName = name,
            ProposedPath = Path.Combine(dir, name)
        });

        if (!result.Success)
        {
            StatusText = result.Message;
            return result.Message;
        }

        // The name feeds classification, so re-derive what comes from it.
        MediaClassifier.Classify(file);
        ExtraLinker.Link(_catalog.Files);
        _catalog.RebuildIndex();
        CatalogStore.Save(_catalog, _catalogPath);
        RebuildRows();

        var newPath = file.FullPath;
        Undo.Push($"rename to '{name}'", () =>
        {
            var back = RenameService.Apply(new RenameProposal
            {
                File = file,
                CurrentName = Path.GetFileName(newPath),
                ProposedName = previousName,
                ProposedPath = previousPath
            });
            if (back.Success) MediaClassifier.Classify(file);
            _catalog.RebuildIndex();
            PersistAndRefresh();
            return Task.FromResult(back.Success
                ? $"Renamed back to '{previousName}'."
                : $"Could not rename back: {back.Message}");
        });

        StatusText = $"Renamed to '{name}'.";
        return StatusText;
    }

    /// <summary>
    /// Give everything under a folder the same title — a whole show in one go. Written
    /// onto each file rather than kept as a folder rule, so the catalogue alone says
    /// everything there is to know about a file.
    /// </summary>
    public string SetTitleForFolder(string folder, string title, bool includeSubdirs)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(title))
            return "Choose a folder and a title first.";

        var clean = title.Trim();
        var affected = FilesUnder(folder, includeSubdirs);
        if (affected.Count == 0)
        {
            StatusText = $"No catalogued files under {folder}.";
            return StatusText;
        }

        // The folder's files and their identical copies wherever those live — but not
        // files elsewhere that merely share the old title, which may be something else.
        var withDuplicates = affected.Concat(DuplicatesOf(affected)).Distinct().ToList();

        var snapshot = withDuplicates
            .Select(f => (File: f, f.TmdbName, f.TmdbVerified, f.ImdbVerified, f.TitleManuallySet))
            .ToList();

        var changed = TitleUpdater.Set(withDuplicates, clean, manual: true);
        DuplicateMetadata.Propagate(_catalog.Files);
        ExtraLinker.Link(_catalog.Files);

        // Names on disk follow the title here too, so a whole show renamed in one go comes
        // out consistent rather than half-corrected.
        var renamed = RenameToMatchTitles(withDuplicates);

        // Any leftover rule for this folder has just been made redundant.
        var previous = _settings.FolderTitleRules
            .Where(r => string.Equals(r.Path, folder, StringComparison.OrdinalIgnoreCase)).ToList();
        if (previous.Count > 0)
        {
            _settings.FolderTitleRules.RemoveAll(r =>
                string.Equals(r.Path, folder, StringComparison.OrdinalIgnoreCase));
            _settings.Save(_settingsPath);
        }

        CatalogStore.Save(_catalog, _catalogPath);
        RebuildRows();

        Undo.Push($"folder title '{clean}'", () =>
        {
            UndoRenames(renamed);
            foreach (var s in snapshot)
            {
                s.File.TmdbName = s.TmdbName;
                s.File.TmdbVerified = s.TmdbVerified;
                s.File.ImdbVerified = s.ImdbVerified;
                s.File.TitleManuallySet = s.TitleManuallySet;
            }
            if (previous.Count > 0)
            {
                _settings.FolderTitleRules.AddRange(previous);
                _settings.Save(_settingsPath);
            }
            ExtraLinker.Link(_catalog.Files);
            _catalog.RebuildIndex();
            PersistAndRefresh();
            return Task.FromResult($"Reverted the folder title '{clean}'.");
        });

        StatusText = $"Title '{clean}' set for {folder}{(includeSubdirs ? " and its subfolders" : "")} " +
                     $"— {changed} file(s) updated" +
                     (renamed.Count > 0 ? $", {renamed.Count} renamed to match." : ".");
        return StatusText;
    }

    /// <summary>
    /// Give every catalogued file under a folder the same category. Written onto the files
    /// themselves rather than kept as a folder rule: everything known about a file belongs
    /// in the catalogue, where it travels with the file and survives a settings reset.
    /// </summary>
    public string SetCategoryForFolder(string folder, string category, bool includeSubdirs)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(category))
            return "Choose a folder and a category first.";

        var affected = FilesUnder(folder, includeSubdirs);
        if (affected.Count == 0)
        {
            StatusText = $"No catalogued files under {folder}.";
            return StatusText;
        }

        // Duplicates elsewhere are the same content, so they are filed the same way.
        var withDuplicates = affected.Concat(DuplicatesOf(affected)).Distinct().ToList();

        PushFieldUndo($"category '{category}' on {withDuplicates.Count} file(s)", withDuplicates,
            f => f.CategoryOverride, (f, old) => f.CategoryOverride = old);

        foreach (var f in withDuplicates) f.CategoryOverride = category;

        // A rule for this folder, if one is left over from an earlier version, has just
        // been made redundant by the files themselves saying so.
        var retired = _settings.FolderCategoryRules.RemoveAll(r =>
            string.Equals(r.Path, folder, StringComparison.OrdinalIgnoreCase));
        if (retired > 0) _settings.Save(_settingsPath);

        CatalogStore.Save(_catalog, _catalogPath);
        RebuildRows();

        StatusText = $"Category '{category}' set on {withDuplicates.Count} file(s) under " +
                     $"{folder}{(includeSubdirs ? " and its subfolders" : "")}.";
        return StatusText;
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
    /// Put the rules a new exclusion would make redundant to the user. Set by the window;
    /// only consulted when the policy is to ask.
    /// </summary>
    public Func<IReadOnlyList<ExcludedFolder>, bool>? ConfirmRedundantExclusions { get; set; }

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
        var alreadyConsolidated = new List<MediaFile>();
        var toFile = new List<MediaFile>();
        foreach (var file in files)
        {
            if (ConsolidationPlanner.IsAtPlannedPath(file, CategoryResolver.Effective(file, _settings), _settings))
                alreadyConsolidated.Add(file);
            else
                toFile.Add(file);
        }

        var skipped = 0;
        var origins = toFile.Select(f => (File: f, f.FullPath, f.FileName)).ToList();
        var run = new CollisionRun("consolidated");

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
                    skipped++;
                    return new RelocationResult(false, "No category or target folder.", file.FullPath);
                }

                var name = ConsolidationPlanner.PlanFileName(file, category);
                return await FileInLibraryAsync(file, destDir, name, deleteOriginal, run, bytes);
            });

        var tidied = await FinishCollisionRunAsync(run);
        skipped += run.Skipped;

        // Anything that arrived is now filed; anything that moved for a corrected title is
        // filed under the new one.
        foreach (var file in files)
            file.Consolidated = ConsolidationPlanner.IsCorrectlyFiled(
                file, CategoryResolver.Effective(file, _settings), _settings);
        CatalogStore.Save(_catalog, _catalogPath);

        if (deleteOriginal)
            PushMoveUndo(origins.Where(o => !string.Equals(o.File.FullPath, o.FullPath,
                StringComparison.OrdinalIgnoreCase)).ToList());

        var failed = Math.Max(0, report.Failed - skipped);
        var parts = new List<string> { $"{report.Succeeded} moved" };
        if (skipped > 0) parts.Add($"{skipped} skipped");
        if (alreadyConsolidated.Count > 0) parts.Add($"{alreadyConsolidated.Count} already consolidated");
        if (report.AlreadyPresent.Count > 0) parts.Add($"{report.AlreadyPresent.Count} already in the library");
        if (tidied > 0) parts.Add($"{tidied} duplicate(s) removed");
        if (failed > 0) parts.Add($"{failed} failed");

        var msg = "Consolidation: " + string.Join(", ", parts) +
                  (run.Cancelled ? " (cancelled part-way)." : ".");
        StatusText = msg;
        return new ConsolidationOutcome(
            report.Succeeded, skipped, failed, report.AlreadyPresent, msg, alreadyConsolidated);
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
    /// Keep one copy of a piece of content and remove the rest, then make sure the survivor
    /// is the one in the library: the copies go first, which frees the library's own name
    /// for the keeper to move into when the keeper was not the library copy to begin with.
    /// </summary>
    public async Task<string> KeepOneCopyAsync(
        MediaFile keeper, IReadOnlyList<MediaFile> copies, bool toRecycleBin)
    {
        var others = copies.Where(f => !ReferenceEquals(f, keeper)).ToList();
        var removed = 0;
        if (others.Count > 0)
        {
            var outcome = await DeleteFilesAsync(others, toRecycleBin);
            removed = outcome.Result.Deleted;
        }

        var category = CategoryResolver.Effective(keeper, _settings);
        var message = $"Kept {keeper.FileName}; {removed} other copy(ies) removed.";

        if (!ConsolidationPlanner.IsAtPlannedPath(keeper, category, _settings))
        {
            var outcome = await ConsolidateModelsAsync(new[] { keeper }, deleteOriginal: true);
            message += " " + outcome.Message;
        }

        StatusText = message;
        return message;
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
                  (run.Cancelled ? " (cancelled part-way)." : ".");
        StatusText = msg;
        return new ConsolidationOutcome(report.Succeeded, 0, report.Failed, report.AlreadyPresent, msg);
    }

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
            var result = await FileRelocator.RelocateAsync(file, destinationDir, deleteOriginal,
                newFileName: null, DuplicatePolicy.Skip, bytes);
            if (!result.NameTaken) return result;

            var (policy, decided) = await AskAboutCollisionAsync(file, result.NewPath, run);
            if (decided != null) return decided;

            return await FileRelocator.RelocateAsync(file, destinationDir, deleteOriginal,
                newFileName: null, policy, bytes);
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
                      (run.Cancelled ? " (cancelled part-way)." : ".");
        StatusText = message;
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
        var result = await FileRelocator.RelocateAsync(
            file, destDir, deleteOriginal, newName, DuplicatePolicy.Skip, bytes);
        if (!result.NameTaken) return result;

        var (policy, decided) = await AskAboutCollisionAsync(file, result.NewPath, run);
        if (decided != null) return decided;

        return await FileRelocator.RelocateAsync(
            file, destDir, deleteOriginal, newName, policy, bytes);
    }

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
    public async Task<string> DeepCheckOneAsync(MediaFile file, CancellationToken ct = default)
    {
        if (!CanDoVideo)
            return "A deep check needs FFmpeg and ffprobe — set them up on the External tools tab first.";
        if (!File.Exists(file.FullPath))
            return "That file is no longer on disk.";

        try
        {
            var engine = new ContentAnalysisEngine(_tools);
            await engine.AnalyzeAsync(new[] { file }, fingerprint: false, deepCheck: true, null, ct);
            return $"{file.FileName}: {file.Integrity}";
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
    private record OperationReport(int Succeeded, int Failed, List<MediaFile> AlreadyPresent);

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
        int ok = 0, failed = 0;
        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                var fileStart = doneBytes;
                StatusText = $"{verb} {i + 1}/{files.Count}: {file.FileName}";

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
                else failed++;
            }
            CatalogStore.Save(_catalog, _catalogPath);
        }
        finally
        {
            EndTiming();
            IsScanning = false;
            RebuildRows();
        }

        return new OperationReport(ok, failed, present);
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
                         $"{report.Skipped:N0} unnamed episode rows skipped.";
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

        TmdbClient? tmdb = null;
        if (!string.IsNullOrWhiteSpace(_settings.TmdbApiKey) ||
            !string.IsNullOrWhiteSpace(_settings.TmdbReadAccessToken))
        {
            tmdb = new TmdbClient(_settings.TmdbApiKey, _settings.TmdbReadAccessToken,
                _tmdbCache, new RateLimiter(TimeSpan.FromSeconds(2)));
        }

        return new TitleVerifier(_imdb, tmdb, _settings.UseImdbFirst);
    }

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
        if (string.IsNullOrWhiteSpace(_settings.TmdbApiKey) &&
            string.IsNullOrWhiteSpace(_settings.TmdbReadAccessToken))
            return "Enter a TMDb API key or Read Access Token in Settings first.";

        var models = (rows.Count > 0 ? rows.Select(r => r.Model) : _catalog.Files).ToList();

        _cts = new CancellationTokenSource();
        IsScanning = true;
        ProgressValue = 0;
        ProgressMax = 1;

        var progress = new Progress<ValidationProgress>(p =>
        {
            ProgressMax = Math.Max(1, p.Total);
            ProgressValue = p.Done;
            StatusText = $"Validating TV names {p.Done}/{p.Total} — {p.Current}";
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

        // An explicit list of drives to watch wins; otherwise fall back to whatever was
        // scanned, which is what earlier versions did.
        if (_settings.WatchedDrives.Count > 0)
            roots = _settings.WatchedDrives;

        var rootList = roots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().ToList();
        if (rootList.Count == 0)
            rootList = _catalog.Files.Select(f => Path.GetPathRoot(f.FullPath) ?? "")
                .Where(r => r.Length > 0).Distinct().ToList();

        // Folders added by hand are watched too, wherever they live.
        rootList = rootList.Concat(_settings.AdditionalScanFolders)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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
            MediaClassifier.Classify(entry);
            _catalog.Files.Add(entry);
            _catalog.ByPath[path] = entry;
            ExtraLinker.Link(_catalog.Files);
            CatalogStore.Save(_catalog, _catalogPath);
            RebuildRows();

            Notify?.Invoke("Media Catalog", $"Added new file: {info.Name}");
            StatusText = $"New file detected and added: {info.Name}";

            _ = HashNewFileAsync(entry); // hash it once it has stopped growing
        }
        catch { /* a watcher hiccup must never crash the app */ }
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
            MediaClassifier.Classify(entry);
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
                StatusText = $"Re-hashing {done + 1}/{targets.Count}: {file.FileName}";
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
                MediaClassifier.Classify(file);
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
    }

    private void RaiseCommandStates()
    {
        (ScanCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ResumeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PauseCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
