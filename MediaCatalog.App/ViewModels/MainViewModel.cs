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
/// Result of a consolidation run. <paramref name="AlreadyPresent"/> holds sources whose
/// file is already in the consolidation location, so they can be offered for deletion.
/// </summary>
public record ConsolidationOutcome(
    int Moved, int Skipped, int Failed, List<MediaFile> AlreadyPresent, string Message);

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
    private NewFileWatcher? _watcher;
    private List<string> _lastRoots = new();
    private readonly List<FileRow> _allRows = new();
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

        ScanCommand = new RelayCommand(async () => await RunScanAsync(SelectedRootPaths(), resuming: false),
            () => !IsScanning);
        ResumeCommand = new RelayCommand(async () => await RunScanAsync(_session.Roots, resuming: true),
            () => !IsScanning && CanResume);
        PauseCommand = new RelayCommand(() => { _isPausing = true; _cts?.Cancel(); }, () => IsScanning);
        CancelCommand = new RelayCommand(() => { _isPausing = false; _cts?.Cancel(); }, () => IsScanning);
        RefreshDrivesCommand = new RelayCommand(LoadDrives, () => !IsScanning);
        SelectAllDrivesCommand = new RelayCommand(() => SetAllDrives(true), () => !IsScanning);
        SelectNoDrivesCommand = new RelayCommand(() => SetAllDrives(false), () => !IsScanning);

        RestoreFilters();

        CanResume = _session.IsResumable;
        if (CanResume)
        {
            var how = _session.Status == ScanSessionStatus.Paused ? "Paused" : "Interrupted";
            StatusText = $"{how} scan can be resumed: {_session.LastDone}/{_session.LastTotal} done " +
                         $"on {_session.Roots.Count} drive(s). Click Resume to continue.";
        }

        LoadDrives();
        RebuildRows();
        StartWatchingIfEnabled(_lastRoots); // resumes watching if enabled in settings
    }

    public ObservableCollection<DriveItem> Drives { get; } = new();
    public ObservableCollection<FileRow> Files { get; } = new();

    public Array FilterModes => Enum.GetValues(typeof(FilterMode));

    public ICommand ScanCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RefreshDrivesCommand { get; }
    public ICommand SelectAllDrivesCommand { get; }
    public ICommand SelectNoDrivesCommand { get; }

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

    private void LoadDrives()
    {
        var previouslySelected = Drives.Where(d => d.IsSelected)
            .Select(d => d.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Drives.Clear();
        foreach (var root in DriveScanner.GetAvailableDrives())
        {
            var item = new DriveItem(root);
            if (previouslySelected.Contains(root.Path)) item.IsSelected = true;
            Drives.Add(item);
        }
    }

    private void SetAllDrives(bool selected)
    {
        foreach (var d in Drives) d.IsSelected = selected;
    }

    private List<string> SelectedRootPaths() =>
        Drives.Where(d => d.IsSelected).Select(d => d.Path).ToList();

    /// <summary>
    /// Run (or resume) a scan over <paramref name="roots"/>. Pause and cancel both stop
    /// the scan at a clean boundary; pause additionally saves a resumable session.
    /// </summary>
    private async Task RunScanAsync(List<string> roots, bool resuming)
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
            StatusText = p.Total > 0
                ? $"{p.Phase}: {p.Done}/{p.Total} — {p.CurrentFile}"
                : p.Phase;
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
                resume: resuming, resumeFromIndex: resumeFromIndex, settings: _settings, ct: _cts.Token));

            CatalogStore.Save(_catalog, _catalogPath);
            ClearSession();
            _lastRoots = roots;
            StartWatchingIfEnabled(roots);
            MissingFiles = report.MissingFiles;
            StatusText = report.MissingFiles.Count > 0
                ? $"Scan complete. {report.MissingFiles.Count} file(s) could not be found — see the missing-files list."
                : $"Scan complete. Catalogue saved to {_catalogPath}";
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
            StatusText = p.Total > 0
                ? $"{p.Phase}: {p.Done}/{p.Total} — {p.CurrentFile}"
                : p.Phase;
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

            // Being in the library is a fact about where the file is, so it is re-derived
            // rather than trusted: consolidation folders change, and files get moved.
            f.Consolidated = ConsolidationPlanner.IsInConsolidationLocation(f, _settings);

            var row = new FileRow(f) { Category = CategoryResolver.Effective(f, _settings) };
            _allRows.Add(row);
            rowByPath[f.FullPath] = row;
        }

        var groups = DuplicateFinder.FindExactDuplicates(_catalog.Files);
        long reclaimable = 0;
        foreach (var g in groups)
        {
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
    /// deleting the originals only after a successful hash verification.
    /// </summary>
    public async Task<string> RelocateAsync(
        IReadOnlyList<FileRow> rows, string destinationDir, bool deleteOriginal)
    {
        if (rows.Count == 0) return "Nothing selected.";

        IsScanning = true;
        int ok = 0, failed = 0;
        try
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                StatusText = $"Relocating {i + 1}/{rows.Count}: {row.FileName}";
                var result = await FileRelocator.RelocateAsync(
                    row.Model, destinationDir, deleteOriginal);
                if (result.Success) { ok++; row.Refresh(); }
                else failed++;
            }
            CatalogStore.Save(_catalog, _catalogPath);
        }
        finally
        {
            IsScanning = false;
            RebuildRows();
        }

        var msg = $"Relocation finished: {ok} succeeded, {failed} failed.";
        StatusText = msg;
        return msg;
    }

    /// <summary>Build in-place rename proposals for the given rows (only ones that would change).</summary>
    public List<RenameProposal> BuildRenameProposals(IEnumerable<FileRow> rows)
    {
        var models = rows.Select(r => r.Model);
        return RenameService.BuildProposals(models)
            .Where(p => p.WillChange)
            .ToList();
    }

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

    /// <summary>How many other entries share this file's current title.</summary>
    public int CountSharingTitle(MediaFile file) =>
        TitleUpdater.SameTitleAs(_catalog.Files, file).Count;

    /// <summary>
    /// Apply a hand-typed title to the selected files, to everything that shared their
    /// previous title, and to their exact duplicates — a copy of a file that had no title
    /// yet would otherwise be left behind. A corrected title counts as validated, like a
    /// TMDb result does.
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

        // Duplicates are the same content, so they get the title whether or not they
        // currently share the (possibly empty) old one.
        var withDuplicates = targets.Concat(DuplicatesOf(targets)).Distinct().ToList();

        var snapshot = _catalog.Files
            .Select(f => (File: f, f.TmdbName, f.TmdbVerified, f.TitleManuallySet, f.ParsedTitle))
            .ToList();

        var changed = TitleUpdater.Apply(_catalog.Files, withDuplicates, title, manual: true);

        // Extras take their title from what they hang off, so refresh the links.
        ExtraLinker.Link(_catalog.Files);
        CatalogStore.Save(_catalog, _catalogPath);
        RebuildRows();

        Undo.Push($"title '{title}' on {changed} file(s)", () =>
        {
            foreach (var s in snapshot)
            {
                s.File.TmdbName = s.TmdbName;
                s.File.TmdbVerified = s.TmdbVerified;
                s.File.TitleManuallySet = s.TitleManuallySet;
                s.File.ParsedTitle = s.ParsedTitle;
            }
            ExtraLinker.Link(_catalog.Files);
            PersistAndRefresh();
            return Task.FromResult($"Reverted the title '{title}'.");
        });

        StatusText = $"Title set to '{title}' on {changed} file(s).";
        return StatusText;
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
    /// Give everything under a folder the same title — a whole show in one go. The rule is
    /// remembered so files scanned later inherit it, and applied to what is already here.
    /// </summary>
    public string SetTitleForFolder(string folder, string title, bool includeSubdirs)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(title))
            return "Choose a folder and a title first.";

        var clean = title.Trim();
        var rules = _settings.FolderTitleRules;
        var previous = rules.Where(r => string.Equals(r.Path, folder, StringComparison.OrdinalIgnoreCase))
            .ToList();
        rules.RemoveAll(r => string.Equals(r.Path, folder, StringComparison.OrdinalIgnoreCase));
        rules.Add(new FolderTitleRule
        {
            Path = folder, Title = clean, IncludeSubdirectories = includeSubdirs
        });
        _settings.Save(_settingsPath);

        // Apply to the files already catalogued under the folder.
        var affected = _catalog.Files
            .Where(f => includeSubdirs
                ? f.FullPath.StartsWith(folder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                : string.Equals(Path.GetDirectoryName(f.FullPath), folder.TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase))
            .ToList();

        var snapshot = affected
            .Select(f => (File: f, f.TmdbName, f.TmdbVerified, f.TitleManuallySet))
            .ToList();

        var changed = TitleUpdater.Apply(_catalog.Files, affected, clean, manual: true);
        DuplicateMetadata.Propagate(_catalog.Files);
        ExtraLinker.Link(_catalog.Files);
        CatalogStore.Save(_catalog, _catalogPath);
        RebuildRows();

        Undo.Push($"folder title '{clean}'", () =>
        {
            foreach (var s in snapshot)
            {
                s.File.TmdbName = s.TmdbName;
                s.File.TmdbVerified = s.TmdbVerified;
                s.File.TitleManuallySet = s.TitleManuallySet;
            }
            _settings.FolderTitleRules.RemoveAll(r =>
                string.Equals(r.Path, folder, StringComparison.OrdinalIgnoreCase));
            _settings.FolderTitleRules.AddRange(previous);
            _settings.Save(_settingsPath);
            ExtraLinker.Link(_catalog.Files);
            PersistAndRefresh();
            return Task.FromResult($"Reverted the folder title '{clean}'.");
        });

        StatusText = $"Title '{clean}' set for {folder}{(includeSubdirs ? " and its subfolders" : "")} " +
                     $"— {changed} file(s) updated.";
        return StatusText;
    }

    public void SetCategoryForFolder(string folder, string category, bool includeSubdirs)
    {
        _settings.FolderCategoryRules.RemoveAll(r =>
            string.Equals(r.Path, folder, StringComparison.OrdinalIgnoreCase));
        _settings.FolderCategoryRules.Add(new FolderCategoryRule
        {
            Path = folder, Category = category, IncludeSubdirectories = includeSubdirs
        });
        _settings.Save(_settingsPath);
        RebuildRows();
        StatusText = $"Folder rule: '{category}' for {folder}{(includeSubdirs ? " (and subfolders)" : "")}.";
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

    public void ExcludeFolder(string folder, bool includeSubdirs)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        _settings.ExcludedFolders.RemoveAll(f =>
            string.Equals(f.Path, folder, StringComparison.OrdinalIgnoreCase));
        _settings.ExcludedFolders.Add(new ExcludedFolder { Path = folder, IncludeSubdirectories = includeSubdirs });
        _settings.Save(_settingsPath);
        RebuildRows();
        StatusText = $"Excluded folder: {folder}{(includeSubdirs ? " (and subfolders)" : "")}.";
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
    public DuplicateGroup? DuplicateGroupFor(MediaFile file)
    {
        if (string.IsNullOrEmpty(file.Sha256)) return null;
        return DuplicateGroupBySha(file.Sha256);
    }

    public DuplicateGroup? DuplicateGroupBySha(string sha)
    {
        if (string.IsNullOrEmpty(sha)) return null;
        return DuplicateFinder.FindExactDuplicates(_catalog.Files)
            .FirstOrDefault(g => string.Equals(g.Sha256, sha, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Copy/move specific catalogue entries (used by the duplicate manager).</summary>
    public async Task<string> RelocateModelsAsync(IReadOnlyList<MediaFile> files, string dir, bool delete)
    {
        int ok = 0, failed = 0;
        foreach (var f in files)
        {
            var r = await FileRelocator.RelocateAsync(f, dir, delete);
            if (r.Success) ok++; else failed++;
        }
        PersistAndRefresh();
        return $"{ok} {(delete ? "moved" : "copied")}, {failed} failed.";
    }

    /// <summary>Delete a file from disk and catalogue (used by the duplicate manager).</summary>
    public string DeleteFile(MediaFile file)
    {
        try
        {
            if (File.Exists(file.FullPath)) File.Delete(file.FullPath);
            _catalog.Files.Remove(file);
            _catalog.RebuildIndex();
            CatalogStore.Save(_catalog, _catalogPath);
            RebuildRows();
            return $"Deleted {file.FileName}.";
        }
        catch (Exception ex)
        {
            return $"Could not delete {file.FileName}: {ex.Message}";
        }
    }

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
        var skipped = 0;
        var origins = files.Select(f => (File: f, f.FullPath, f.FileName)).ToList();

        var report = await RunFileOperationAsync(files, "Consolidating", (file, bytes) =>
        {
            var category = CategoryResolver.Effective(file, _settings);
            var destDir = ConsolidationPlanner.PlanDirectory(file, category, _settings);
            if (destDir == null)
            {
                skipped++;
                return Task.FromResult(new RelocationResult(false, "No category or target folder.", file.FullPath));
            }
            return FileRelocator.RelocateAsync(
                file, destDir, deleteOriginal,
                ConsolidationPlanner.PlanFileName(file, category), DuplicatePolicy.Skip, bytes);
        });

        // Anything that arrived is now in the library.
        foreach (var file in files)
            file.Consolidated = ConsolidationPlanner.IsInConsolidationLocation(file, _settings);
        CatalogStore.Save(_catalog, _catalogPath);

        if (deleteOriginal)
            PushMoveUndo(origins.Where(o => !string.Equals(o.File.FullPath, o.FullPath,
                StringComparison.OrdinalIgnoreCase)).ToList());

        var failed = Math.Max(0, report.Failed - skipped);
        var msg = $"Consolidation: {report.Succeeded} moved, {skipped} skipped (no category/target), " +
                  $"{report.AlreadyPresent.Count} already in the library, {failed} failed.";
        StatusText = msg;
        return new ConsolidationOutcome(report.Succeeded, skipped, failed, report.AlreadyPresent, msg);
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

        var report = await RunFileOperationAsync(files, "Consolidating", (file, bytes) =>
        {
            var proposed = byFile[file];
            var destDir = Path.GetDirectoryName(proposed);
            return string.IsNullOrEmpty(destDir)
                ? Task.FromResult(new RelocationResult(false, "No destination folder.", file.FullPath))
                : FileRelocator.RelocateAsync(file, destDir, deleteOriginal,
                    Path.GetFileName(proposed), DuplicatePolicy.Skip, bytes);
        });

        foreach (var file in files)
            file.Consolidated = ConsolidationPlanner.IsInConsolidationLocation(file, _settings);
        CatalogStore.Save(_catalog, _catalogPath);

        if (deleteOriginal)
            PushMoveUndo(origins.Where(o => !string.Equals(o.File.FullPath, o.FullPath,
                StringComparison.OrdinalIgnoreCase)).ToList());

        var msg = $"Consolidation: {report.Succeeded} moved, {report.AlreadyPresent.Count} " +
                  $"already in the library, {report.Failed} failed.";
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
    /// Move (or copy) files to a folder the user picked, with progress and an ETA based on
    /// the bytes actually copied.
    /// </summary>
    public async Task<string> MoveFilesAsync(
        IReadOnlyList<MediaFile> files, string destinationDir, bool deleteOriginal)
    {
        if (files.Count == 0) return "Nothing selected.";
        if (string.IsNullOrWhiteSpace(destinationDir)) return "No destination folder.";

        var origins = files.Select(f => (File: f, f.FullPath, f.FileName)).ToList();
        var verb = deleteOriginal ? "Moving" : "Copying";
        var report = await RunFileOperationAsync(files, verb, (file, bytes) =>
            FileRelocator.RelocateAsync(file, destinationDir, deleteOriginal,
                newFileName: null, DuplicatePolicy.Rename, bytes));

        if (report.Succeeded > 0 && deleteOriginal)
            PushMoveUndo(origins.Where(o => o.File.FullPath != o.FullPath).ToList());

        var message = $"{(deleteOriginal ? "Move" : "Copy")} finished: {report.Succeeded} done, " +
                      $"{report.AlreadyPresent.Count} already there, {report.Failed} failed.";
        StatusText = message;
        return message;
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

    /// <summary>
    /// Re-derive metadata for entries that predate the current feature set (new
    /// categories, extras linking, title parsing) without re-walking drives or re-hashing.
    /// </summary>
    public async Task<string> RefreshCatalogAsync()
    {
        _cts = new CancellationTokenSource();
        IsScanning = true;
        ProgressValue = 0;
        ProgressMax = Math.Max(1, StaleEntryCount);

        var progress = new Progress<RefreshProgress>(p =>
        {
            ProgressMax = Math.Max(1, p.Total);
            ProgressValue = p.Done;
            StatusText = $"Refreshing catalogue {p.Done}/{p.Total} — {p.Current}";
        });

        try
        {
            var report = await Task.Run(() =>
                CatalogRefresher.Refresh(_catalog, _settings, progress, _cts.Token));
            CatalogStore.Save(_catalog, _catalogPath);
            StatusText =
                $"Catalogue refresh: {report.Refreshed} entr(ies) updated, {report.Skipped} already current, " +
                $"{report.Linked} extra(s) linked, {report.Shared} value(s) shared with duplicates" +
                (report.Pruned > 0 ? $", {report.Pruned} dropped by exclusions." : ".");
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
    /// Drive roots offered in Settings for watching. Taken from the list already loaded
    /// for the drives panel — re-enumerating stalls on network/removable volumes.
    /// </summary>
    public IReadOnlyList<string> AvailableDriveRoots =>
        Drives.Select(d => d.Path).ToList();

    public void ApplyAppSettings(AppSettings settings)
    {
        _settings = settings;
        _settings.SyncLegacyFolders();
        _settings.Save(_settingsPath);
        StartupManager.Apply(_settings.StartWithWindows, _settings.StartInTray);
        StartWatchingIfEnabled(_lastRoots);
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
    public async Task<string> RehashPendingAsync()
    {
        var targets = _catalog.Files.Where(f => f.AwaitingDownload || !f.HasHash).ToList();
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
                if (string.IsNullOrEmpty(hash)) continue;

                file.SizeBytes = info.Length;
                file.LastModifiedUtc = info.LastWriteTimeUtc;
                file.Sha256 = hash;
                file.AwaitingDownload = false;
                MediaClassifier.Classify(file);
                rehashed++;
            }
            ExtraLinker.Link(_catalog.Files);
            CatalogStore.Save(_catalog, _catalogPath);
            StatusText = $"Re-hashed {rehashed} file(s)" +
                         (missing > 0 ? $"; {missing} no longer exist." : ".");
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
        (RefreshDrivesCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SelectAllDrivesCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SelectNoDrivesCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
