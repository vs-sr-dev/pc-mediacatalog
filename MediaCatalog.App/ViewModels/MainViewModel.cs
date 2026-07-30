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
using MediaCatalog.Core.Models;
using MediaCatalog.Core.Naming;
using MediaCatalog.Core.Persistence;
using MediaCatalog.Core.Relocation;
using MediaCatalog.Core.Scanning;
using MediaCatalog.Core.Storage;
using MediaCatalog.Core.Tmdb;
using MediaCatalog.Core.Tools;

namespace MediaCatalog.App.ViewModels;

public enum FilterMode { All, Video, Audio, Movies, TvShows, Duplicates, NearDuplicates, Problems }

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
    private TaskbarNotifier? _notifier;
    private List<string> _lastRoots = new();
    private readonly List<FileRow> _allRows = new();
    private CancellationTokenSource? _cts;
    private bool _isPausing;
    private int _lastDone;
    private int _lastTotal;

    private string _statusText = "Ready.";
    private string _summaryText = "";
    private string _toolStatus = "";
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
            if (SetProperty(ref _selectedFilter, value))
                ApplyFilter();
        }
    }

    // --- Wildcard column filter ---
    public static readonly string[] FilterColumns =
        { "Name", "Kind", "Category", "Title", "Year", "S/E", "Size", "Integrity", "Path", "Dup", "TMDb" };

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
    }

    public void RemoveFilter(FilterClause clause)
    {
        ActiveFilters.Remove(clause);
        ApplyFilter();
    }

    public void ClearFilters()
    {
        ActiveFilters.Clear();
        FilterPattern = "";
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
        SummaryText =
            $"{_catalog.Files.Count} files  •  {video} video, {audio} audio  •  " +
            $"{groups.Count} duplicate sets ({Format.Bytes(reclaimable)} reclaimable){nearPart}";

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

        _cts = new CancellationTokenSource();
        IsScanning = true;
        ProgressValue = 0;
        ProgressMax = targets.Count;

        var progress = new Progress<AnalysisProgress>(p =>
        {
            ProgressMax = Math.Max(1, p.Total);
            ProgressValue = p.Done;
            StatusText = $"Analysing {p.Done}/{p.Total} — {p.CurrentFile}";
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
            IsScanning = false;
            _cts?.Dispose();
            _cts = null;
            RebuildRows();
        }
        return StatusText;
    }

    // --- Categories -------------------------------------------------------

    public void SetCategoryForFiles(IReadOnlyList<FileRow> rows, string category)
    {
        foreach (var r in rows) r.Model.CategoryOverride = category;
        CatalogStore.Save(_catalog, _catalogPath);
        RebuildRows();
        StatusText = $"Set category '{category}' on {rows.Count} file(s).";
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
    /// Move (or copy) selected TV/film files into the structured consolidation folders.
    /// Files whose category/target isn't set are skipped.
    /// </summary>
    public async Task<string> ConsolidateAsync(IReadOnlyList<FileRow> rows, bool deleteOriginal)
    {
        if (string.IsNullOrWhiteSpace(_settings.TvConsolidationDir) &&
            string.IsNullOrWhiteSpace(_settings.FilmConsolidationDir))
            return "Set a TV and/or Film consolidation folder in Settings first.";

        IsScanning = true;
        int ok = 0, skipped = 0, failed = 0;
        try
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var category = CategoryResolver.Effective(row.Model, _settings);
                var destDir = ConsolidationPlanner.PlanDirectory(row.Model, category, _settings);
                if (destDir == null) { skipped++; continue; }

                StatusText = $"Consolidating {i + 1}/{rows.Count}: {row.FileName}";
                var result = await FileRelocator.RelocateAsync(row.Model, destDir, deleteOriginal);
                if (result.Success) { ok++; row.Refresh(); } else failed++;
            }
            CatalogStore.Save(_catalog, _catalogPath);
        }
        finally
        {
            IsScanning = false;
            RebuildRows();
        }

        var msg = $"Consolidation: {ok} moved, {skipped} skipped (no category/target), {failed} failed.";
        StatusText = msg;
        return msg;
    }

    /// <summary>Propose consolidation moves for the whole catalogue.</summary>
    public List<ConsolidationSuggestion> SuggestConsolidation() =>
        ConsolidationSuggester.Suggest(
            _catalog.Files, _settings, f => CategoryResolver.Effective(f, _settings));

    /// <summary>Apply chosen consolidation suggestions (copy-and-verify, optional delete).</summary>
    public async Task<string> ApplyConsolidationAsync(
        IReadOnlyList<ConsolidationSuggestion> chosen, bool deleteOriginal)
    {
        IsScanning = true;
        int ok = 0, failed = 0;
        try
        {
            for (var i = 0; i < chosen.Count; i++)
            {
                var s = chosen[i];
                var destDir = System.IO.Path.GetDirectoryName(s.ProposedPath);
                if (string.IsNullOrEmpty(destDir)) { failed++; continue; }
                StatusText = $"Consolidating {i + 1}/{chosen.Count}: {s.File.FileName}";
                var r = await FileRelocator.RelocateAsync(s.File, destDir, deleteOriginal);
                if (r.Success) ok++; else failed++;
            }
            CatalogStore.Save(_catalog, _catalogPath);
        }
        finally
        {
            IsScanning = false;
            RebuildRows();
        }
        var msg = $"Consolidation: {ok} moved, {failed} failed.";
        StatusText = msg;
        return msg;
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

    public void ApplyAppSettings(AppSettings settings)
    {
        _settings = settings;
        _settings.Save(_settingsPath);
        StartupManager.Apply(_settings.StartWithWindows);
        StartWatchingIfEnabled(_lastRoots);
        OnPropertyChanged(nameof(Categories));
        RebuildRows();
        StatusText = "Settings saved.";
    }

    // --- New-file watching + notifications --------------------------------

    private TaskbarNotifier Notifier => _notifier ??= new TaskbarNotifier();

    private void StartWatchingIfEnabled(IEnumerable<string> roots)
    {
        StopWatching();
        if (!_settings.WatchForNewFiles) return;

        var rootList = roots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().ToList();
        if (rootList.Count == 0)
            rootList = _catalog.Files.Select(f => Path.GetPathRoot(f.FullPath) ?? "")
                .Where(r => r.Length > 0).Distinct().ToList();
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
                IndexedUtc = DateTime.UtcNow, Integrity = IntegrityStatus.Ok
            };
            MediaClassifier.Classify(entry);
            _catalog.Files.Add(entry);
            _catalog.ByPath[path] = entry;
            CatalogStore.Save(_catalog, _catalogPath);
            RebuildRows();

            Notifier.Notify("Media Catalog", $"Added new file: {info.Name}");
            StatusText = $"New file detected and added: {info.Name}";

            _ = HashNewFileAsync(entry); // fill in the content hash in the background
        }
        catch { /* a watcher hiccup must never crash the app */ }
    }

    private async Task HashNewFileAsync(MediaFile entry)
    {
        var hash = await FileHasher.ComputeSha256Async(entry.FullPath);
        if (string.IsNullOrEmpty(hash)) return;
        Application.Current?.Dispatcher.Invoke(() =>
        {
            entry.Sha256 = hash;
            CatalogStore.Save(_catalog, _catalogPath);
            RebuildRows();
        });
    }

    /// <summary>Release the watcher/tray icon on shutdown.</summary>
    public void Shutdown()
    {
        StopWatching();
        _notifier?.Dispose();
        _notifier = null;
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
