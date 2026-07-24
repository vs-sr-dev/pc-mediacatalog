using System.Collections.ObjectModel;
using System.Windows.Input;
using MediaCatalog.App.Infrastructure;
using MediaCatalog.Core.Duplicates;
using MediaCatalog.Core.Fingerprinting;
using MediaCatalog.Core.Models;
using MediaCatalog.Core.Naming;
using MediaCatalog.Core.Persistence;
using MediaCatalog.Core.Relocation;
using MediaCatalog.Core.Scanning;
using MediaCatalog.Core.Tools;

namespace MediaCatalog.App.ViewModels;

public enum FilterMode { All, Video, Audio, Movies, TvShows, Duplicates, NearDuplicates, Problems }

public class MainViewModel : ObservableObject
{
    private readonly string _catalogPath = CatalogStore.DefaultPath;
    private readonly string _toolSettingsPath = ToolSettings.DefaultPath;
    private readonly string _sessionPath = ScanSession.DefaultPath;
    private Catalog _catalog;
    private ToolSettings _toolSettings;
    private ExternalTools _tools;
    private ScanSession _session;
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

    public MainViewModel()
    {
        _catalog = CatalogStore.Load(_catalogPath);
        _toolSettings = ToolSettings.Load(_toolSettingsPath);
        _tools = ExternalTools.Resolve(_toolSettings);
        _session = ScanSession.Load(_sessionPath);
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
            StatusText = $"Paused scan available: {_session.LastDone}/{_session.LastTotal} done " +
                         $"on {_session.Roots.Count} drive(s). Click Resume to continue.";

        LoadDrives();
        RebuildRows();
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

        _cts = new CancellationTokenSource();
        _isPausing = false;
        IsScanning = true;
        CanResume = false;
        ProgressValue = 0;
        ProgressMax = 1;
        _lastDone = 0;
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

        // Checkpoint the catalogue to disk periodically so a pause/crash keeps work.
        void Checkpoint() => CatalogStore.Save(_catalog, _catalogPath);

        try
        {
            var engine = new ScanEngine(_catalog);
            await Task.Run(() => engine.ScanAsync(
                roots, progress, Checkpoint, TimeSpan.FromSeconds(30), _cts.Token));

            CatalogStore.Save(_catalog, _catalogPath);
            ScanSession.Clear(_sessionPath);
            _session = new ScanSession();
            StatusText = $"Scan complete. Catalogue saved to {_catalogPath}";
        }
        catch (OperationCanceledException)
        {
            CatalogStore.Save(_catalog, _catalogPath);
            if (_isPausing)
            {
                _session = new ScanSession
                {
                    Status = ScanSessionStatus.Paused,
                    Roots = roots,
                    LastDone = _lastDone,
                    LastTotal = _lastTotal,
                    UpdatedUtc = DateTime.UtcNow
                };
                _session.Save(_sessionPath);
                CanResume = true;
                StatusText = $"Paused at {_lastDone}/{_lastTotal}. Work saved — click Resume to continue.";
            }
            else
            {
                ScanSession.Clear(_sessionPath);
                _session = new ScanSession();
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

    /// <summary>Rebuild the display rows from the catalogue and mark duplicates.</summary>
    private void RebuildRows()
    {
        _allRows.Clear();
        var rowByPath = new Dictionary<string, FileRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in _catalog.Files)
        {
            var row = new FileRow(f);
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
            FilterMode.Movies => rows.Where(r => r.Model.VideoCategory == VideoCategory.Movie),
            FilterMode.TvShows => rows.Where(r => r.Model.VideoCategory == VideoCategory.TvShow),
            FilterMode.Duplicates => rows.Where(r => r.IsDuplicate),
            FilterMode.NearDuplicates => rows.Where(r => r.IsNearDuplicate),
            FilterMode.Problems => rows.Where(r =>
                r.Model.Integrity is IntegrityStatus.Corrupt or IntegrityStatus.IncompleteDownload),
            _ => rows
        };

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
