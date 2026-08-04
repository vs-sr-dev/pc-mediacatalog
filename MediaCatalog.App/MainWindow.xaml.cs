using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using MediaCatalog.App.Infrastructure;
using MediaCatalog.App.ViewModels;
using MediaCatalog.Core.Consolidation;
using MediaCatalog.Core.Models;
using MediaCatalog.Core.Relocation;
using MediaCatalog.Core.Storage;
using Microsoft.Win32;

namespace MediaCatalog.App;

/// <summary>
/// Interaction logic for MainWindow.xaml. Keeps only view concerns here
/// (dialogs, folder picking); all catalogue logic lives in <see cref="MainViewModel"/>.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    /// <summary>The open (non-modal) settings window, so a second click just focuses it.</summary>
    private SettingsWindow? _settingsWindow;

    private readonly TrayIcon _tray;

    /// <summary>Set once the user really means to quit, as opposed to closing the window.</summary>
    private bool _exiting;

    /// <param name="startHidden">
    /// True when Windows launched us at sign-in: the app lives in the notification area
    /// and only opens its window when asked.
    /// </param>
    public MainWindow(bool startHidden = false)
    {
        InitializeComponent();
        DataContext = _vm;

        // The version where a version belongs. Asking somebody which build they are running
        // should not mean sending them to a dialog to find out.
        Title = $"Media Catalog {AppVersion.Product}";

        _tray = new TrayIcon(ShowFromTray, ExitApplication);
        _vm.Notify = _tray.Notify;
        _vm.Undo.Changed += UpdateUndoButton;
        _vm.UnhashedFilesFound = OfferUnhashedFiles;
        _vm.ScanRequested = () => _ = RunScanWizardAsync();
        _vm.ResumeRequested = () => _ = ResumeScanAsync();
        _vm.CollisionResolver = ResolveCollisionAsync;
        _vm.EpisodeConflictResolver = ResolveEpisodeConflictAsync;
        _vm.ConfirmRedundantExclusions = AskAboutRedundantExclusions;
        UpdateUndoButton();

        BuildColumnHeaderMenu();
        ApplySavedColumnLayout();

        // Closing the window hides it while the app is watching for new files; quitting is
        // then an explicit choice from the tray menu.
        Closing += OnWindowClosing;
        StateChanged += OnWindowStateChanged;

        // Either the launcher asked for it (a sign-in start) or the user has asked for
        // every start to be a quiet one.
        _startHidden = startHidden || _vm.Settings.AlwaysStartMinimised;
        if (_startHidden) ShowInTaskbar = false;

        // An empty catalogue is the one situation where the app cannot do anything useful
        // until it is told what to look at, so the wizard opens itself. Not when we are
        // starting into the notification area, where a dialog nobody asked for is an ambush.
        if (!_startHidden && _vm.CatalogIsEmpty && !_vm.Settings.ScanWizardCompleted)
            Loaded += OnFirstRun;
    }

    private async void OnFirstRun(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstRun;
        var start = MessageBox.Show(this,
            "Nothing has been catalogued yet.\n\n" +
            "The scan wizard walks through what to look at and what to pick up. You can " +
            "open it again at any time with the Scan button.",
            "Welcome to Media Catalog", MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (start == MessageBoxResult.OK) await RunScanWizardAsync();
    }

    /// <summary>True when this run should wait in the notification area rather than open.</summary>
    public bool ShouldStartHidden => _startHidden;

    private readonly bool _startHidden;

    /// <summary>
    /// Send the window to the notification area instead of the taskbar when it is
    /// minimised, if that is what the user has asked for.
    ///
    /// Nothing is said about it. Minimising is something the user just did on purpose, to a
    /// window they configured to behave this way, and a notification confirming it is the
    /// program telling them what they already know.
    /// </summary>
    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized || !_vm.Settings.MinimiseToTray) return;

        Hide();
        ShowInTaskbar = false;
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveColumnLayout();

        if (!_exiting && _vm.Settings.WatchForNewFiles)
        {
            // Still needed in the background: hide rather than quit. Said nothing about —
            // the tray icon is there to be seen, and a notification every time the window
            // is closed is a notification about something the user just did on purpose.
            e.Cancel = true;
            Hide();
            ShowInTaskbar = false;
            return;
        }

        _vm.Shutdown();
        _tray.Dispose();
        Application.Current.Shutdown();
    }

    private void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _exiting = true;
        Close();
    }

    private void UpdateUndoButton()
    {
        var next = _vm.Undo.NextDescription;
        UndoButton.IsEnabled = _vm.Undo.CanUndo;
        UndoButton.ToolTip = next == null
            ? "Nothing to undo."
            : $"Undo {next} (up to ten operations are remembered).";
    }

    private async void OnUndoClick(object sender, RoutedEventArgs e)
    {
        if (!_vm.Undo.CanUndo)
        {
            MessageBox.Show(this, "There is nothing to undo.",
                "Undo", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var what = _vm.Undo.NextDescription;
        var confirm = MessageBox.Show(this, $"Undo {what}?",
            "Undo", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        var result = await _vm.Undo.UndoAsync();
        UpdateUndoButton();
        MessageBox.Show(this, result, "Undo", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void OnRelocateClick(object sender, RoutedEventArgs e)
    {
        var rows = FilesGrid.SelectedItems.OfType<FileRow>().ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show(this, "Select one or more files in the list first.",
                "Relocate", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "Choose destination folder",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
            return;

        var deleteOriginal = DeleteOriginalCheck.IsChecked == true;
        var verb = deleteOriginal ? "MOVE (copy, verify, then delete original)" : "COPY (verify)";
        var confirm = MessageBox.Show(this,
            $"{verb}\n\n{rows.Count} file(s)\n→ {dialog.FolderName}\n\nProceed?",
            "Confirm relocation", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK)
            return;

        var result = await _vm.RelocateAsync(rows, dialog.FolderName, deleteOriginal);
        MessageBox.Show(this, result, "Relocation", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void OnRenameClick(object sender, RoutedEventArgs e)
    {
        // Use the current selection if any, otherwise everything in view.
        var rows = FilesGrid.SelectedItems.OfType<FileRow>().ToList();
        if (rows.Count == 0)
            rows = _vm.Files.ToList();

        var proposals = _vm.BuildRenameProposals(rows);
        if (proposals.Count == 0)
        {
            MessageBox.Show(this,
                "No rename suggestions for these files (not enough metadata, or they already match the scheme).",
                "Rename", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var preview = new RenamePreviewWindow(proposals.Select(p => new RenameRow(p))) { Owner = this };
        if (preview.ShowDialog() != true)
            return;

        var chosen = preview.SelectedRows.Select(r => r.Proposal).ToList();
        var result = await _vm.ApplyRenamesAsync(chosen);
        MessageBox.Show(this, result, "Rename", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void OnAnalyzeClick(object sender, RoutedEventArgs e)
    {
        if (!_vm.CanAnalyze)
        {
            PromptForTools("Fingerprinting needs FFmpeg (video) and/or fpcalc (audio).");
            return;
        }

        var rows = FilesGrid.SelectedItems.OfType<FileRow>().ToList();
        var scope = rows.Count > 0 ? $"{rows.Count} selected file(s)" : "the whole catalogue";
        var confirm = MessageBox.Show(this,
            $"Compute fingerprints for {scope}?\n\nThis reads each file with the external tools; " +
            "it can take a while on large libraries. Already-fingerprinted files are skipped.",
            "Fingerprint / analyse", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        var result = await _vm.AnalyzeAsync(rows, deepCheck: false);
        MessageBox.Show(this, result, "Analyse", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void OnDeepCheckClick(object sender, RoutedEventArgs e)
    {
        if (!_vm.CanDoVideo)
        {
            PromptForTools("Deep integrity checks need FFmpeg and ffprobe.");
            return;
        }

        var rows = FilesGrid.SelectedItems.OfType<FileRow>().ToList();
        var scope = rows.Count > 0 ? $"{rows.Count} selected file(s)" : "the whole catalogue";
        var confirm = MessageBox.Show(this,
            $"Fully decode {scope} to detect corruption?\n\nThis is thorough but SLOW " +
            "(it decodes every file end to end). Consider selecting only suspect files first.",
            "Deep integrity check", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        var result = await _vm.AnalyzeAsync(rows, deepCheck: true);
        MessageBox.Show(this, result, "Deep check", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void PromptForTools(string message)
    {
        var open = MessageBox.Show(this,
            message + "\n\nOpen the External tools settings now?",
            "Tools required", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (open == MessageBoxResult.Yes)
            OpenSettings(SettingsTab.ExternalTools);
    }

    // --- Scanning ---------------------------------------------------------

    /// <summary>
    /// The scan wizard: what to do with the existing catalogue, where to look, what to
    /// pick up, and what to do about a drive that is not plugged in.
    /// </summary>
    private async Task RunScanWizardAsync()
    {
        var wizard = new ScanWizardWindow(
            _vm.Settings, _vm.CataloguedFileCount, _vm.Settings.ScanDrives) { Owner = this };

        if (wizard.ShowDialog() != true || wizard.Plan == null) return;

        var result = await _vm.StartScanAsync(wizard.Plan);
        MessageBox.Show(this, result, "Scan", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// Continue an interrupted scan. A drive that was part of it but is not attached now
    /// is raised before anything starts: cancelling is the default, since an external drive
    /// that is simply unplugged is not a reason to rewrite what is known about it.
    /// </summary>
    private async Task ResumeScanAsync()
    {
        var missing = _vm.UnavailableSessionRoots();
        var wait = false;

        if (missing.Count > 0)
        {
            var answer = MessageBox.Show(this,
                $"{string.Join(", ", missing)} {(missing.Count == 1 ? "is" : "are")} part of the " +
                "paused scan but not connected.\n\n" +
                "Yes — carry on, and scan that drive as soon as it is connected.\n" +
                "No — carry on with the drives that are here and leave it out.\n" +
                "Cancel — do nothing, so you can connect it first.\n\n" +
                "Nothing already catalogued on that drive is removed in any case.",
                "Drive not connected", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning,
                MessageBoxResult.Cancel);

            if (answer == MessageBoxResult.Cancel) return;
            wait = answer == MessageBoxResult.Yes;
        }

        var result = await _vm.ResumeScanAsync(wait);
        MessageBox.Show(this, result, "Resume scan", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // --- Settings & filter ------------------------------------------------

    /// <summary>
    /// Settings open non-modally so the catalogue stays usable while they are edited;
    /// saving applies immediately through the <see cref="SettingsWindow.Saved"/> event.
    /// </summary>
    private void OnSettingsClick(object sender, RoutedEventArgs e) => OpenSettings(SettingsTab.General);

    private void OpenSettings(SettingsTab tab)
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.FocusTab(tab);
            return;
        }

        var dlg = new SettingsWindow(
            _vm.Settings, _vm.Categories, _vm.AvailableDriveRoots,
            _vm.CurrentToolSettings, () => _vm.DownloadImdbDataAsync())
        { Owner = this };
        dlg.Saved += settings => _vm.ApplyAppSettings(settings);
        dlg.ToolsSaved += tools => _vm.ApplyToolSettings(tools);
        dlg.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow = dlg;
        dlg.Show(tab);
    }

    /// <summary>
    /// Put the exclusion rules a new one has made redundant to the user, one by one, and
    /// hand back the ones they chose to drop. Only reached when the policy is to ask — the
    /// other two settings decide without stopping.
    /// </summary>
    private IReadOnlyList<ExcludedFolder> AskAboutRedundantExclusions(
        IReadOnlyList<ExcludedFolder> superseded) =>
        RedundantRulesWindow.Ask(this, superseded);

    /// <summary>
    /// Two files want the same name: show both, and every known copy of either, and let the
    /// user say which one is worth keeping.
    /// </summary>
    private Task<CollisionResolution> ResolveCollisionAsync(CollisionRequest request)
    {
        var dlg = new FileCollisionWindow(request, file => _vm.DeepCheckOneAsync(file)) { Owner = this };
        dlg.ShowDialog();
        return Task.FromResult(dlg.Resolution);
    }

    /// <summary>
    /// The episode being consolidated is already in the library under another name. Both
    /// are put in front of the user with everything that decides between them, since only
    /// one of them can stay: a consolidation location that holds the same episode twice is
    /// not doing the one job it exists to do.
    /// </summary>
    private Task<CollisionResolution> ResolveEpisodeConflictAsync(EpisodeConflict conflict)
    {
        var dlg = new EpisodeConflictWindow(
            conflict, _vm.CanDoVideo ? file => _vm.DeepCheckOneAsync(file) : null) { Owner = this };
        dlg.ShowDialog();
        return Task.FromResult(dlg.Resolution);
    }

    private async void OnRefreshCatalogClick(object sender, RoutedEventArgs e)
    {
        var stale = _vm.StaleEntryCount;
        var unverified = _vm.UnverifiedTitleCount;
        var rules = _vm.PendingFolderRuleCount;

        if (stale == 0 && unverified == 0 && rules == 0)
        {
            MessageBox.Show(this,
                "Every catalogue entry already has everything this version knows how to work out, " +
                "and every title has been confirmed. Run a scan instead if you want to pick up new " +
                "or changed files.",
                "Refresh catalogue", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var what = new List<string>();
        if (stale > 0)
            what.Add($"• re-derive metadata for {stale} entr(ies) — including programmes with no " +
                     "season/episode yet, which are re-parsed with the current rules");
        if (rules > 0)
            what.Add($"• write {rules} folder rule(s) onto the files themselves, then retire the rules");
        if (unverified > 0)
            what.Add($"• confirm {unverified} title(s) and fill in missing years, checking the local " +
                     "IMDb data first and TMDb only for what it cannot answer");

        var confirm = MessageBox.Show(this,
            "Refresh the catalogue?\n\n" + string.Join("\n\n", what) +
            "\n\nNothing is re-scanned or re-hashed.",
            "Refresh catalogue", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        var result = await _vm.RefreshCatalogAsync();
        MessageBox.Show(this, result, "Refresh catalogue", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnAboutClick(object sender, RoutedEventArgs e) =>
        new AboutWindow { Owner = this }.ShowDialog();

    private async void OnVerifyTitlesClick(object sender, RoutedEventArgs e)
    {
        // Neither our extract nor the raw download is here, so offer to fetch it rather
        // than sending the user off to find a file themselves.
        if (_vm.NeedsImdbDownload)
        {
            var answer = MessageBox.Show(this,
                "There is no local IMDb data to check titles against yet.\n\n" +
                $"Download it now from {_vm.Settings.EffectiveImdbDownloadUrl}?\n" +
                "It is around 150 MB, and is boiled down to a two-column extract of titles and " +
                "years — the only part this program uses.\n\n" +
                "Yes — download it now.\n" +
                "No — carry on using TMDb alone, which is rate-limited to one query every two seconds.\n" +
                "Cancel — do nothing.\n\n" +
                "The address can be changed on the Data sources tab in Settings if it ever moves.",
                "Verify titles", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.Yes)
            {
                var downloaded = await _vm.DownloadImdbDataAsync();
                MessageBox.Show(this, downloaded, "IMDb data",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                if (!_vm.HasImdbData) return;   // it did not arrive; nothing to verify against
            }
        }

        var rows = FilesGrid.SelectedItems.OfType<FileRow>().ToList();
        var result = await _vm.VerifyTitlesAsync(rows);
        MessageBox.Show(this, result, "Verify titles", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// Put the unhashable files up as soon as the scan that found them ends — unless the
    /// app is sitting in the notification area, where a modal dialog nobody asked for
    /// would be an ambush. The toolbar button keeps them reachable either way.
    /// </summary>
    private void OfferUnhashedFiles()
    {
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            _tray.Notify("Media Catalog",
                $"{_vm.UnhashedFiles.Count} file(s) could not be hashed during the scan.");
            return;
        }
        Dispatcher.BeginInvoke(new Action(() => OnUnhashedFilesClick(this, new RoutedEventArgs())),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// The files a scan could not hash, with the three things worth doing about them:
    /// read them again, decode them to see if they are damaged, or delete them.
    /// </summary>
    private async void OnUnhashedFilesClick(object sender, RoutedEventArgs e)
    {
        var files = _vm.UnhashedFiles;
        if (files.Count == 0) return;

        var dlg = new UnhashedFilesWindow(files, _vm.CanDoVideo) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var chosen = dlg.Chosen;
        if (chosen.Count == 0) return;

        switch (dlg.Action)
        {
            case UnhashedAction.Retry:
                var rehashed = await _vm.RehashAsync(chosen);
                MessageBox.Show(this, rehashed, "Rescan", MessageBoxButton.OK, MessageBoxImage.Information);
                break;

            case UnhashedAction.DeepCheck:
                var checkedResult = await _vm.AnalyzeModelsAsync(chosen, deepCheck: true);
                MessageBox.Show(this, checkedResult, "Deep check",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                break;

            case UnhashedAction.Delete:
                await DeleteAsync(chosen.ToList());
                break;
        }
    }

    private void OnClearFilter(object sender, RoutedEventArgs e) => _vm.ClearFilters();

    private void OnAddFilter(object sender, RoutedEventArgs e) => _vm.AddCurrentFilter();

    /// <summary>
    /// Enter in the filter box stacks the filter, which is what pressing Enter in a box
    /// beside an Add button has always meant. The binding is on a delay, so what has just
    /// been typed is pushed through first — otherwise Enter would commit the pattern as it
    /// stood a fifth of a second ago.
    /// </summary>
    private void OnFilterValueKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        // The drop-down is open: Enter is choosing an item from it, not committing.
        if (sender is ComboBox { IsDropDownOpen: true }) return;

        if (sender is ComboBox box)
            box.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();

        _vm.AddCurrentFilter();
        e.Handled = true;
    }

    private void OnRemoveFilter(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FilterClause clause })
            _vm.RemoveFilter(clause);
    }

    private void OnColumnsClick(object sender, RoutedEventArgs e)
    {
        new ColumnChooserWindow(FilesGrid) { Owner = this }.ShowDialog();
        SaveColumnLayout();
    }

    private async void OnSuggestConsolidationClick(object sender, RoutedEventArgs e)
    {
        var suggestions = _vm.SuggestConsolidation();
        if (suggestions.Count == 0)
        {
            MessageBox.Show(this,
                "No consolidation suggestions. Set consolidation folders in Settings, and make sure " +
                "files have a category (TV files also need a validated title and season/episode).",
                "Suggest consolidation", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new ConsolidationSuggesterWindow(suggestions) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var chosen = dlg.Selected;
        if (chosen.Count == 0) return;
        var confirm = MessageBox.Show(this,
            $"Move {chosen.Count} file(s) to their consolidation folders?\n\n" +
            "Files are renamed or moved where that will do; anything genuinely copied is " +
            "verified against the original first, and the original is then permanently " +
            "deleted, leaving one copy in the library.",
            "Consolidate", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;
        var outcome = await _vm.ApplyConsolidationAsync(chosen, deleteOriginal: true);
        MessageBox.Show(this, outcome.Message, "Consolidate", MessageBoxButton.OK, MessageBoxImage.Information);
        await AfterConsolidationAsync(outcome);
    }

    /// <summary>
    /// A file with no title has nowhere to be filed, so ask for one before consolidating
    /// rather than silently skipping it. Returns false if the user backs out.
    /// </summary>
    private Task<bool> EnsureTitlesAsync(IReadOnlyList<MediaFile> files)
    {
        var untitled = _vm.WithoutTitle(files);
        if (untitled.Count == 0) return Task.FromResult(true);

        var ask = MessageBox.Show(this,
            $"{untitled.Count} of the selected file(s) have no title yet, and a title is what " +
            "decides where they are filed.\n\nEnter the missing titles now?",
            "Consolidate", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (ask == MessageBoxResult.Cancel) return Task.FromResult(false);
        if (ask == MessageBoxResult.No) return Task.FromResult(true);   // they'll be skipped

        foreach (var file in untitled)
        {
            // Re-check: naming one file can supply the title for its duplicates too.
            if (!string.IsNullOrWhiteSpace(file.EffectiveTitle)) continue;

            var typed = PromptWindow.Ask(this, "Title needed",
                $"Title for this file:\n\n{file.FullPath}", TitleSeed(file));
            if (typed == null) return Task.FromResult(false);            // cancelled the run
            if (string.IsNullOrWhiteSpace(typed)) continue;              // skip just this one

            _vm.SetTitleForModels(new[] { file }, typed.Trim());
        }
        UpdateUndoButton();
        return Task.FromResult(true);
    }

    /// <summary>
    /// Files whose copy is already in the consolidation location were not moved — the
    /// source is simply redundant, so offer to delete it.
    /// </summary>
    private async Task OfferToDeleteRedundantAsync(IReadOnlyList<MediaFile> present)
    {
        // Anything a collision answer already cleared away is not worth offering again.
        var left = present.Where(f => File.Exists(f.FullPath)).ToList();
        if (left.Count == 0) return;

        var ask = MessageBox.Show(this,
            $"{left.Count} file(s) are already in the consolidation location, so nothing was copied " +
            "for them.\n\nDelete the redundant copies from where they are now?",
            "Already in the library", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ask != MessageBoxResult.Yes) return;

        await DeleteAsync(left);
    }

    /// <summary>
    /// Folders a consolidation run has left behind. A move that files everything correctly
    /// and leaves a trail of folders holding a sample clip and a readme has only done half
    /// the job.
    ///
    /// Folders holding something are listed with what is in them and what it comes to, since
    /// removing those is a rather bigger thing than removing an empty one and the user should
    /// be able to see which they are agreeing to.
    /// </summary>
    private async Task OfferEmptiedFoldersAsync(IReadOnlyList<LeftoverFolder> leftovers)
    {
        if (leftovers.Count == 0 || !_vm.Settings.OfferRemoveEmptyFolders) return;

        var listed = string.Join("\n", leftovers.Take(15).Select(f => "    " + f.Describe()));
        if (leftovers.Count > 15) listed += $"\n    …and {leftovers.Count - 15} more";

        var withContents = leftovers.Count(f => !f.IsEmpty);
        var caveat = withContents > 0
            ? $"\n\n{withContents} of them still hold something, but less than the size limit set " +
              "for their category — so what is in them is scraps rather than content. None of " +
              "them holds a catalogued file waiting to be filed; those are never touched."
            : "";

        var permanent = _vm.Settings.DeleteEmptyFoldersPermanently;
        var ask = MessageBox.Show(this,
            $"{leftovers.Count} folder(s) the files came out of are finished with:\n\n{listed}{caveat}\n\n" +
            $"Remove them{(permanent ? " (deleted outright, not sent to the Recycle Bin)" : "")}? " +
            "Any parent folder they leave empty goes too.",
            "Folders left behind", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ask != MessageBoxResult.Yes) return;

        var removed = await _vm.RemoveFoldersAsync(leftovers.Select(f => f.Path).ToList());
        MessageBox.Show(this,
            removed == 0
                ? "The folders could not be removed — something else may be using them."
                : $"{removed} folder(s) removed.",
            "Folders left behind", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// The sweep that runs at the end of every consolidation, whatever route got there.
    ///
    /// A file that has just been filed must not be left with copies of it lying about — that
    /// is the one thing consolidating is for. It is easy to lose sight of when the filing did
    /// not involve moving anything: a whole season put right by renaming its folder is filed
    /// just as surely as a file copied one at a time, and used to leave every stray copy of
    /// those episodes exactly where it was, unmentioned.
    /// </summary>
    private async Task SweepDuplicatesAsync(IReadOnlyList<MediaFile> touched)
    {
        if (touched.Count == 0) return;

        var redundant = _vm.RedundantCopiesOf(touched);
        if (redundant.Count == 0) return;

        var bytes = redundant.Sum(f => f.SizeBytes);
        var ask = MessageBox.Show(this,
            $"{redundant.Count} copy(ies) — {Format.Bytes(bytes)} — of the file(s) just filed are " +
            "still sitting elsewhere.\n\n" +
            "The library copy of each is the one being kept, so these are redundant by " +
            "definition. Review them for deletion now?",
            "Duplicates of what was just filed", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ask != MessageBoxResult.Yes) return;

        await FileDeletion.RunAsync(this, _vm, redundant, "Delete duplicates");
        UpdateUndoButton();
    }

    /// <summary>
    /// Everything that happens after a consolidation run, in the one place, so every route
    /// into consolidating ends the same way: redundant sources, files that turned out to
    /// need no moving, folders left behind, and the duplicate sweep.
    /// </summary>
    private async Task AfterConsolidationAsync(ConsolidationOutcome outcome)
    {
        await OfferToDeleteRedundantAsync(outcome.AlreadyPresent);
        await HandleAlreadyConsolidatedAsync(outcome.Consolidated);
        await SweepDuplicatesAsync(outcome.Touched);
        await OfferEmptiedFoldersAsync(outcome.LeftoverFolders);
    }

    /// <summary>
    /// Deal with the files that were already exactly where they belong. On its own that is
    /// simply "already consolidated" and worth no more than saying so — but when other
    /// copies of the same content exist, this is the moment to sort them out.
    ///
    /// Answering one group with "do the same for the rest" ends the questions there: the
    /// remaining groups' copies are gathered up and put in a single delete confirmation
    /// that lists every one of them, rather than a run of identical dialogs the user has
    /// already said they do not want to see.
    /// </summary>
    private async Task HandleAlreadyConsolidatedAsync(IReadOnlyList<MediaFile> consolidated)
    {
        if (consolidated.Count == 0) return;

        // Only the ones with copies elsewhere are worth a conversation.
        var withCopies = consolidated.Where(f => _vm.CopiesOf(f).Count > 0).ToList();
        if (withCopies.Count == 0)
        {
            MessageBox.Show(this,
                consolidated.Count == 1
                    ? $"Already consolidated: {consolidated[0].FileName} is exactly where it belongs, " +
                      "and no other copy of it exists."
                    : $"Already consolidated: {consolidated.Count} file(s) are exactly where they belong, " +
                      "and no other copies of them exist.",
                "Already consolidated", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        for (var i = 0; i < withCopies.Count; i++)
        {
            var file = withCopies[i];
            var copies = new List<MediaFile> { file };
            copies.AddRange(_vm.CopiesOf(file));
            if (copies.Count < 2) continue;    // the earlier rounds may have cleared them

            var dlg = new ConsolidatedDuplicatesWindow(
                file, copies, withCopies.Count - i - 1,
                _vm.CanDoVideo ? f => _vm.DeepCheckOneAsync(f) : null)
            { Owner = this };
            if (dlg.ShowDialog() != true) continue;

            if (dlg.ApplyToAll)
            {
                // "The same" can only mean the same policy, not the same file: the keeper
                // is chosen per group, and the only policy that carries over is "the
                // library copy is the one that survives".
                await DeleteRemainingDuplicatesAsync(withCopies.Skip(i));
                break;
            }

            switch (dlg.Action)
            {
                case ConsolidatedDuplicateAction.DeleteAllDuplicates:
                    await FileDeletion.RunAsync(this, _vm,
                        copies.Where(c => !ReferenceEquals(c, file)).ToList(), "Delete duplicates");
                    break;

                case ConsolidatedDuplicateAction.KeepChosen:
                    var keeper = dlg.Keeper ?? file;
                    var others = copies.Where(c => !ReferenceEquals(c, keeper)).ToList();
                    if (!await FileDeletion.RunAsync(this, _vm, others, "Delete the other copies"))
                        break;
                    var message = await _vm.EnsureKeeperFiledAsync(keeper);
                    MessageBox.Show(this, message, "Keep one copy",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    break;
            }
        }
        UpdateUndoButton();
    }

    /// <summary>
    /// Every remaining group's other copies, in one delete confirmation. The user has said
    /// the library copy wins throughout — what is left is to show them the whole list once
    /// and let them choose the Recycle Bin or a permanent delete for the lot.
    /// </summary>
    private async Task DeleteRemainingDuplicatesAsync(IEnumerable<MediaFile> libraryCopies)
    {
        var doomed = new List<MediaFile>();
        foreach (var file in libraryCopies)
            foreach (var copy in _vm.CopiesOf(file))
                if (!doomed.Contains(copy)) doomed.Add(copy);

        if (doomed.Count == 0) return;
        await FileDeletion.RunAsync(this, _vm, doomed, "Delete duplicates");
    }

    private void OnMissingFilesClick(object sender, RoutedEventArgs e)
    {
        if (_vm.MissingFiles.Count == 0) return;
        new ListWindow("Missing files", "These files were listed by enumeration but could not be " +
            "found during the scan (files under a 'Temp' folder are ignored):", _vm.MissingFiles)
        { Owner = this }.ShowDialog();
    }

    // --- Consolidation ----------------------------------------------------

    private async void OnConsolidateClick(object sender, RoutedEventArgs e)
    {
        var rows = FilesGrid.SelectedItems.OfType<FileRow>().ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show(this, "Select the TV/film files to consolidate first.",
                "Consolidate", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!await EnsureTitlesAsync(rows.Select(r => r.Model).ToList())) return;

        // Two files claiming to be the same film, only one of which should end up in the
        // library. Settled before anything moves, since filing one of them and leaving the
        // other where it is has answered the question the wrong way round.
        if (!await ResolveTitleDuplicatesAsync(rows.Select(r => r.Model).ToList())) return;

        // The dialog may well have deleted some of what was selected.
        rows = rows.Where(r => File.Exists(r.Model.FullPath)).ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show(this, "Nothing is left of the selection to consolidate.",
                "Consolidate", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var extras = _vm.LinkedExtras(rows.Select(r => r.Model)).Count;
        var confirm = MessageBox.Show(this,
            $"MOVE {rows.Count} file(s) into the structured library folders?\n\n" +
            "TV → <TvDir>\\<A-Z or #>\\<Show>\\Season NN\\NN - name.ext\n" +
            "Films → <FilmDir>\\<A-Z or #>\\<Title (Year)>\\\n" +
            "Specials/featurettes → the same folder, under \\Extras\\\n\n" +
            (extras > 0 ? $"{extras} linked extra(s) will travel with the selection.\n\n" : "") +
            "A file already on the destination's drive is moved without being copied, and a " +
            "whole folder in the wrong place is renamed rather than emptied out. Anything " +
            "that does have to be copied is verified against the original, and only then is " +
            "the original permanently deleted — consolidating leaves one copy, in the library.\n\n" +
            "Files without a category or a configured target are skipped, and an episode " +
            "already in the library is never filed a second time.",
            "Consolidate", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        var outcome = await _vm.ConsolidateAsync(rows, deleteOriginal: true);
        MessageBox.Show(this, outcome.Message, "Consolidate", MessageBoxButton.OK, MessageBoxImage.Information);
        await AfterConsolidationAsync(outcome);
    }

    /// <summary>
    /// Files being consolidated that have a twin claiming to be the same thing without being
    /// the same bytes. Only one of them belongs in the library, and a content hash cannot
    /// choose between them, so the user is shown every copy with the facts that decide it —
    /// size, length, quality, and whether it still decodes — and picks the one to keep. The
    /// rest are deleted, and the survivor goes on to be consolidated with the selection.
    /// </summary>
    /// <returns>False when the user backed out of consolidating altogether.</returns>
    private Task<bool> ResolveTitleDuplicatesAsync(IReadOnlyList<MediaFile> files)
    {
        var groups = new List<Core.Duplicates.TitleDuplicateGroup>();
        foreach (var file in files)
            if (_vm.TitleDuplicateGroupFor(file) is { } group && !groups.Contains(group))
                groups.Add(group);
        if (groups.Count == 0) return Task.FromResult(true);

        var ask = MessageBox.Show(this,
            (groups.Count == 1
                ? $"\"{groups[0].Key}\" has more than one copy claiming to be it, and they are not " +
                  "the same bytes — the same thing from two different releases.\n\n"
                : $"{groups.Count} of the file(s) selected have another copy claiming to be the " +
                  "same thing without being the same bytes.\n\n") +
            "Only one of each should end up in the library, and nothing but a look at them can " +
            "say which.\n\n" +
            "Yes — show every copy now, so you can choose which to keep and delete the rest.\n" +
            "No — consolidate anyway, leaving the other copies where they are.\n" +
            "Cancel — do nothing.",
            "Possible duplicates", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        if (ask == MessageBoxResult.Cancel) return Task.FromResult(false);
        if (ask == MessageBoxResult.No) return Task.FromResult(true);

        // Opened on the first affected set; the list on the left holds the others, so one
        // dialog covers the lot however many were selected.
        new TitleDuplicatesWindow(_vm, groups[0].Files.FirstOrDefault()) { Owner = this }.ShowDialog();
        UpdateUndoButton();
        return Task.FromResult(true);
    }

    // --- Consolidating everything that can be done without asking ------------

    /// <summary>
    /// File everything the program can decide about on its own, and report what it could not.
    ///
    /// The user is shown the shape of the job before it starts — how many files need no
    /// decision, how many need copies comparing, and how many are missing something that
    /// only they can supply — because those three numbers are what the run is really about.
    /// </summary>
    private async void OnAutoConsolidateClick(object sender, RoutedEventArgs e)
    {
        var rows = FilesGrid.SelectedItems.OfType<FileRow>().ToList();
        var scope = rows.Count > 0 ? rows.Select(r => r.Model).ToList() : null;

        var (jobs, review) = _vm.PlanAutoConsolidation(scope);
        if (jobs.Count == 0)
        {
            MessageBox.Show(this,
                review.Count == 0
                    ? "There is nothing here that can be filed automatically."
                    : $"Nothing can be filed without you: all {review.Count} file(s) are missing " +
                      "something that decides where they go. Run it again after filling those in — " +
                      "the list is on the next screen.",
                "Automatic consolidation", MessageBoxButton.OK, MessageBoxImage.Information);
            if (review.Count > 0) ShowAutoReview(review);
            return;
        }

        var dialog = new AutoConsolidateWindow(jobs, review, _vm.CanAnalyze, _vm.CanDoVideo)
        { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var report = await _vm.AutoConsolidateAsync(scope);
        UpdateUndoButton();

        MessageBox.Show(this, report.Describe(), "Automatic consolidation",
            MessageBoxButton.OK, MessageBoxImage.Information);

        await SweepDuplicatesAsync(report.Touched);
        await OfferEmptiedFoldersAsync(report.LeftoverFolders);

        if (report.Review.Count > 0) ShowAutoReview(report.Review);
    }

    private void ShowAutoReview(IReadOnlyList<AutoReview> review) =>
        new ListWindow("Left for you to look at",
            "These were not filed, because something that decides where they go is missing or " +
            "cannot be worked out without you. Fix what each line names and run it again.",
            review.Select(r => $"{r.Reason}  —  {r.File.FullPath}"))
        { Owner = this }.ShowDialog();

    // --- TMDb -------------------------------------------------------------

    private async void OnValidateTvClick(object sender, RoutedEventArgs e)
    {
        var rows = FilesGrid.SelectedItems.OfType<FileRow>().ToList();
        var result = await _vm.ValidateTvAsync(rows);
        MessageBox.Show(this, result, "Validate TV", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnValidateTvSelected(object sender, RoutedEventArgs e) =>
        OnValidateTvClick(sender, e);

    // --- Category context menu --------------------------------------------

    private void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        // Rebuild the "Set category" submenu from the current category list.
        CategoryMenu.Items.Clear();
        foreach (var cat in _vm.Categories)
        {
            var item = new MenuItem { Header = cat, Tag = cat };
            item.Click += OnSetCategory;
            CategoryMenu.Items.Add(item);
        }
    }

    private void OnSetCategory(object sender, RoutedEventArgs e)
    {
        var rows = FilesGrid.SelectedItems.OfType<FileRow>().ToList();
        if (rows.Count == 0 || sender is not MenuItem { Tag: string category }) return;
        _vm.SetCategoryForFiles(rows, category);
    }

    private void OnAddCategory(object sender, RoutedEventArgs e)
    {
        var name = PromptWindow.Ask(this, "New category", "Category name:");
        if (!string.IsNullOrWhiteSpace(name)) _vm.AddCustomCategory(name.Trim());
    }

    // --- Exclusions -------------------------------------------------------

    private void OnExcludeFolder(object sender, RoutedEventArgs e)
    {
        var row = FilesGrid.SelectedItems.OfType<FileRow>().FirstOrDefault();
        if (row == null) return;
        var folder = Path.GetDirectoryName(row.Model.FullPath);
        if (string.IsNullOrEmpty(folder)) return;

        // Let the user edit the path first — they may want a parent folder, or a wildcard.
        var edited = PromptWindow.Ask(this, "Exclude folder",
            "Folder to exclude (edit to a parent, or use wildcards like ?:\\Windows):", folder);
        if (string.IsNullOrWhiteSpace(edited)) return;

        var subdirs = MessageBox.Show(this,
            $"Exclude this folder from results and future scans?\n\n{edited}\n\n" +
            "Yes = also exclude all subfolders.  No = just this folder.",
            "Exclude folder", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (subdirs == MessageBoxResult.Cancel) return;
        _vm.ExcludeFolder(edited.Trim(), subdirs == MessageBoxResult.Yes);
    }

    private void OnIgnoreExtension(object sender, RoutedEventArgs e)
    {
        var row = FilesGrid.SelectedItems.OfType<FileRow>().FirstOrDefault();
        if (row == null) return;
        var ext = row.Model.Extension;
        if (string.IsNullOrEmpty(ext)) return;
        var confirm = MessageBox.Show(this,
            $"Ignore all '{ext}' files? They'll be removed from the results and skipped in future scans.",
            "Ignore file type", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm == MessageBoxResult.OK) _vm.IgnoreExtension(ext);
    }

    // --- Titles, season/episode, file names --------------------------------

    /// <summary>
    /// Every field of one entry in a single dialog — including the date, which is read off
    /// the file system and is wrong often enough to be worth correcting by hand.
    /// </summary>
    private void OnEditDetails(object sender, RoutedEventArgs e)
    {
        var row = FilesGrid.SelectedItems.OfType<FileRow>().FirstOrDefault();
        if (row == null)
        {
            MessageBox.Show(this, "Select the file to edit first.",
                "Edit details", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        EditDetails(row);
    }

    private void EditDetails(FileRow row)
    {
        var copies = _vm.CopiesOf(row.Model).Count;
        var dlg = new FileDetailsWindow(row.Model, _vm.Categories, row.Category, copies) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Edits == null) return;

        var result = _vm.ApplyFileEdits(row.Model, dlg.Edits);
        UpdateUndoButton();
        MessageBox.Show(this, result, "Edit details", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// What to put in the title box: the title it already has, or failing that the file
    /// name without its extension — a far better starting point than an empty box.
    /// </summary>
    private static string TitleSeed(MediaFile file) =>
        !string.IsNullOrWhiteSpace(file.EffectiveTitle)
            ? file.EffectiveTitle
            : Path.GetFileNameWithoutExtension(file.FileName);

    /// <summary>
    /// One title for everything selected, wherever the files are. Selecting across folders is
    /// the whole point: a show whose episodes ended up in three different places is named once
    /// here rather than three times.
    /// </summary>
    private void OnSetTitleForSelection(object sender, RoutedEventArgs e)
    {
        var rows = FilesGrid.SelectedItems.OfType<FileRow>().ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show(this, "Select the files to name first.",
                "Set title", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var files = rows.Select(r => r.Model).ToList();
        var folders = files.Select(f => Path.GetDirectoryName(f.FullPath) ?? "")
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();

        // What they already agree on, if they agree on anything — otherwise the first one's
        // title, which is at least a starting point rather than an empty box.
        var titles = files.Select(f => f.EffectiveTitle).Distinct(StringComparer.Ordinal).ToList();
        var seed = titles.Count == 1 ? titles[0] : TitleSeed(files[0]);

        var typed = PromptWindow.Ask(this, "Set title",
            $"Title for {rows.Count} selected file(s)" +
            (folders > 1 ? $", across {folders} folders" : "") + ":" +
            (titles.Count > 1 ? $"\n\nThey currently carry {titles.Count} different titles." : ""),
            seed);
        if (string.IsNullOrWhiteSpace(typed)) return;

        var result = _vm.SetTitleForModels(files, typed.Trim());
        UpdateUndoButton();
        MessageBox.Show(this, result, "Set title", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// Everything a folder can be told at once: the title, the year and the category.
    ///
    /// The year is here because it is got wrong often enough to matter — a series whose file
    /// names carry the year of the season rather than of the show ends up filed under a year
    /// that is nobody's idea of right, and correcting that an episode at a time is not a
    /// reasonable thing to ask of anyone.
    /// </summary>
    private void OnSetFolderDetails(object sender, RoutedEventArgs e)
    {
        var row = FilesGrid.SelectedItems.OfType<FileRow>().FirstOrDefault();
        if (row == null)
        {
            MessageBox.Show(this, "Select a file in the folder you want to correct first.",
                "Set folder details", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var folder = Path.GetDirectoryName(row.Model.FullPath);
        if (string.IsNullOrEmpty(folder)) return;

        var dlg = new FolderDetailsWindow(
            folder, _vm.Categories,
            f => _vm.DetailsOf(f, includeSubdirs: true),
            // The folder's own name is usually the show's, so it is the fallback suggestion.
            Path.GetFileName(folder.TrimEnd('\\', '/')))
        { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var result = _vm.SetFolderDetails(
            dlg.SelectedFolder, dlg.Details, dlg.IncludeSubdirectories);
        UpdateUndoButton();
        MessageBox.Show(this, result, "Set folder details",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // --- Moving, scanning and checking folders -----------------------------

    private async void OnMoveToFolder(object sender, RoutedEventArgs e)
    {
        var rows = FilesGrid.SelectedItems.OfType<FileRow>().ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show(this, "Select the files to move first.",
                "Move files", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var chosen = rows.Select(r => r.Model).ToList();
        var siblings = _vm.SiblingsOf(chosen);
        var folders = chosen.Select(f => Path.GetDirectoryName(f.FullPath) ?? "")
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var dlg = new MoveFilesWindow(chosen.Count, folders, siblings.Count) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var files = dlg.IncludeContainingFolder ? chosen.Concat(siblings).ToList() : chosen;
        var result = await _vm.MoveFilesAsync(files, dlg.Destination, dlg.DeleteOriginal);
        UpdateUndoButton();
        MessageBox.Show(this, result, "Move files", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void OnScanFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder to add to the catalogue" };
        if (dialog.ShowDialog(this) != true) return;

        var result = await _vm.ScanFolderAsync(dialog.FolderName);
        MessageBox.Show(this, result, "Scan folder", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void OnDeepCheckFolderClick(object sender, RoutedEventArgs e)
    {
        if (!_vm.CanDoVideo)
        {
            PromptForTools("Deep integrity checks need FFmpeg and ffprobe.");
            return;
        }

        // Start from the selected file's folder when there is one — usually what is meant.
        var start = FilesGrid.SelectedItems.OfType<FileRow>().FirstOrDefault();
        var dialog = new OpenFolderDialog { Title = "Choose a folder to deep check (including subfolders)" };
        if (start != null && Path.GetDirectoryName(start.FullPath) is { Length: > 0 } dir && Directory.Exists(dir))
            dialog.InitialDirectory = dir;
        if (dialog.ShowDialog(this) != true) return;

        var confirm = MessageBox.Show(this,
            $"Fully decode every media file under this folder to detect corruption?\n\n{dialog.FolderName}\n\n" +
            "This is thorough but SLOW. Files not yet in the catalogue are added so their results are kept. " +
            "Progress and an estimated time are shown in the status bar, and Cancel stops it at any point.",
            "Deep check folder", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        var result = await _vm.DeepCheckFolderAsync(dialog.FolderName);
        MessageBox.Show(this, result, "Deep check folder", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void OnRehashClick(object sender, RoutedEventArgs e)
    {
        var pending = _vm.PendingRehashCount;
        if (pending == 0)
        {
            MessageBox.Show(this, "Every catalogued file already has a trustworthy hash.",
                "Re-hash", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(this,
            $"Re-hash {pending} file(s)?\n\nThese were added while they may still have been downloading, " +
            "or never got a hash. Re-hashing refreshes their size and content hash so duplicate " +
            "detection is reliable.",
            "Re-hash", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        var result = await _vm.RehashPendingAsync();
        MessageBox.Show(this, result, "Re-hash", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // --- Deleting files ---------------------------------------------------

    private async void OnDeleteFiles(object sender, RoutedEventArgs e)
    {
        var rows = FilesGrid.SelectedItems.OfType<FileRow>().ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show(this, "Select the files to delete first.",
                "Delete files", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await DeleteAsync(rows.Select(r => r.Model).ToList());
    }

    /// <summary>
    /// Deleting goes through the one shared conversation, so the results grid, the
    /// duplicate manager and the unhashed-files list all behave identically.
    /// </summary>
    private async Task DeleteAsync(IReadOnlyList<MediaFile> files)
    {
        await FileDeletion.RunAsync(this, _vm, files);
        UpdateUndoButton();
    }

    // --- Column layout ----------------------------------------------------

    /// <summary>
    /// Right-click menu for the column headers. Built here rather than in XAML because
    /// handlers declared inside a Style setter compile into class handlers on the menu,
    /// which fire for every item instead of the one clicked.
    /// </summary>
    private void BuildColumnHeaderMenu()
    {
        static MenuItem Item(string header, RoutedEventHandler onClick)
        {
            var item = new MenuItem { Header = header };
            item.Click += onClick;
            return item;
        }

        var menu = new ContextMenu();
        menu.Items.Add(Item("Set column width…", OnSetColumnWidth));
        menu.Items.Add(Item("Fit width to contents", OnFitColumnWidth));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Hide this column", OnHideColumn));
        menu.Items.Add(Item("Choose columns…", OnColumnsClick));

        // One shared menu for every header; PlacementTarget says which one opened it.
        var style = new Style(typeof(DataGridColumnHeader));
        style.Setters.Add(new Setter(ContextMenuProperty, menu));

        // WPF does not pass a column's tooltip on to the header it draws, so each header
        // is pointed back at its own column's ToolTipService.ToolTip. That keeps the
        // wording in the XAML beside the column it explains.
        style.Setters.Add(new Setter(ToolTipProperty, new Binding
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.Self),
            Path = new PropertyPath("Column.(0)", ToolTipService.ToolTipProperty)
        }));

        FilesGrid.ColumnHeaderStyle = style;
    }

    /// <summary>The column whose header was right-clicked.</summary>
    private static DataGridColumn? ColumnFrom(object sender)
    {
        if (sender is not MenuItem item) return null;
        var menu = ItemsControl.ItemsControlFromItemContainer(item) as ContextMenu
                   ?? item.Parent as ContextMenu;
        return (menu?.PlacementTarget as DataGridColumnHeader)?.Column;
    }

    private void OnSetColumnWidth(object sender, RoutedEventArgs e)
    {
        if (ColumnFrom(sender) is not { } column) return;

        var typed = PromptWindow.Ask(this, "Column width",
            $"Width in pixels for the '{column.Header}' column:",
            ((int)Math.Round(column.ActualWidth)).ToString());
        if (string.IsNullOrWhiteSpace(typed)) return;

        if (!double.TryParse(typed.Trim(), out var width) || width < 20 || width > 4000)
        {
            MessageBox.Show(this, "Enter a width between 20 and 4000 pixels.",
                "Column width", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        column.Width = new DataGridLength(width);
        SaveColumnLayout();
    }

    private void OnFitColumnWidth(object sender, RoutedEventArgs e)
    {
        if (ColumnFrom(sender) is not { } column) return;
        column.Width = new DataGridLength(1, DataGridLengthUnitType.Auto);
        // Let the grid measure, then remember the width it settled on.
        Dispatcher.BeginInvoke(new Action(SaveColumnLayout),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnHideColumn(object sender, RoutedEventArgs e)
    {
        if (ColumnFrom(sender) is not { } column) return;
        column.Visibility = Visibility.Collapsed;
        SaveColumnLayout();
    }

    private void ApplySavedColumnLayout()
    {
        foreach (var column in FilesGrid.Columns)
        {
            var header = column.Header?.ToString() ?? "";
            var saved = _vm.Settings.ColumnLayouts
                .FirstOrDefault(c => string.Equals(c.Header, header, StringComparison.Ordinal));
            if (saved == null) continue;

            if (saved.Width >= 20) column.Width = new DataGridLength(saved.Width);
            column.Visibility = saved.Visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>Remember column widths and visibility for the next run.</summary>
    internal void SaveColumnLayout()
    {
        _vm.Settings.ColumnLayouts = FilesGrid.Columns.Select(c => new ColumnLayout
        {
            Header = c.Header?.ToString() ?? "",
            Width = Math.Round(c.ActualWidth),
            Visible = c.Visibility == Visibility.Visible
        }).ToList();
        _vm.SaveSettings();
    }

    // --- Duplicates -------------------------------------------------------

    /// <summary>
    /// Double-click does whatever the user has said it should: play the file, or open its
    /// details for editing. Both are reasonable defaults for a catalogue — one treats it as
    /// a library to watch, the other as a catalogue to correct — and which one is right
    /// depends entirely on what the user is doing with it today.
    /// </summary>
    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FilesGrid.SelectedItem is not FileRow row) return;

        if (_vm.Settings.DoubleClickAction == DoubleClickAction.EditDetails)
            EditDetails(row);
        else
            OpenFile(row);
    }

    // --- Open file / folder, remove from results --------------------------

    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        var rows = FilesGrid.SelectedItems.OfType<FileRow>().ToList();
        if (rows.Count == 0) return;
        _vm.RemoveFromResults(rows);
        e.Handled = true;
    }

    private void OnRemoveFromResults(object sender, RoutedEventArgs e)
    {
        var rows = FilesGrid.SelectedItems.OfType<FileRow>().ToList();
        if (rows.Count > 0) _vm.RemoveFromResults(rows);
    }

    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItems.OfType<FileRow>().FirstOrDefault() is { } row)
            OpenFile(row);
    }

    private void OpenFile(FileRow row) => ShellOpen.Open(this, row.FullPath);

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItems.OfType<FileRow>().FirstOrDefault() is { } row)
            ShellOpen.SelectInExplorer(this, row.FullPath);
    }

    private void OnShowDuplicates(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItems.OfType<FileRow>().FirstOrDefault() is { } row)
            ShowDuplicatesFor(row);
    }

    private void ShowDuplicatesFor(FileRow row)
    {
        var group = _vm.DuplicateGroupFor(row.Model);
        if (group == null)
        {
            // No byte-identical copy — but the catalogue may still hold something claiming
            // to be the same film, which is the other half of the same question.
            if (_vm.TitleDuplicateGroupFor(row.Model) != null)
            {
                var open = MessageBox.Show(this,
                    "This file has no byte-for-byte duplicates, but other file(s) claim the same " +
                    "title and year — the same thing downloaded twice from different releases.\n\n" +
                    "Open the possible-duplicates list?",
                    "Duplicates", MessageBoxButton.YesNo, MessageBoxImage.Information);
                // Opened on this file's set, with this file picked out: they clicked a row,
                // and hunting for it again in a list of hundreds is not an answer.
                if (open == MessageBoxResult.Yes) OpenTitleDuplicates(row.Model);
                return;
            }

            MessageBox.Show(this, "This file has no exact duplicates.",
                "Duplicates", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        new DuplicateManagerWindow(_vm, group) { Owner = this }.ShowDialog();
    }

    // --- Possible duplicates (same title, different bytes) -----------------

    /// <summary>
    /// The toolbar button opens the whole list; a row's context menu opens it on that row's
    /// set. Which is why <paramref name="focus"/> is optional rather than two dialogs.
    /// </summary>
    private void OnShowTitleDuplicates(object sender, RoutedEventArgs e)
    {
        // Whatever is selected in the grid is very likely what the user is thinking about,
        // so it decides where the dialog opens — when it has a set at all.
        var selected = FilesGrid.SelectedItems.OfType<FileRow>().FirstOrDefault();
        OpenTitleDuplicates(
            selected != null && _vm.TitleDuplicateGroupFor(selected.Model) != null
                ? selected.Model
                : null);
    }

    private void OpenTitleDuplicates(MediaFile? focus = null)
    {
        if (!_vm.HasTitleDuplicates)
        {
            MessageBox.Show(this,
                "No files share a title and year without also sharing their contents.\n\n" +
                "This looks for the same thing downloaded twice from two different releases — " +
                "identical in what they are, different in every byte, so a content hash cannot " +
                "see them. Titles have to be filled in for it to find anything, so run Verify " +
                "titles first if the Title column is mostly empty.",
                "Possible duplicates", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        new TitleDuplicatesWindow(_vm, focus) { Owner = this }.ShowDialog();
        UpdateUndoButton();
    }

    /// <summary>
    /// Clear out every stray copy of everything already filed in the library, in one go.
    /// The library copy is the one being kept by definition, so there is nothing to choose
    /// between — only how firmly the rest should go, which the delete confirmation asks.
    /// </summary>
    private async void OnPurgeConsolidatedDuplicatesClick(object sender, RoutedEventArgs e)
    {
        var redundant = _vm.DuplicatesOfConsolidatedFiles();
        if (redundant.Count == 0)
        {
            MessageBox.Show(this,
                "Nothing to purge: no file that is filed in the library has another copy " +
                "sitting anywhere else.",
                "Purge duplicates", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var bytes = redundant.Sum(f => f.SizeBytes);
        var confirm = MessageBox.Show(this,
            $"{redundant.Count} file(s) — {Format.Bytes(bytes)} — are copies of files already " +
            "filed in the library.\n\n" +
            "The library copy of each is kept; every other copy is listed on the next screen, " +
            "where you can send them to the Recycle Bin or delete them permanently.\n\n" +
            "Go on to review the list?",
            "Purge duplicates", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        await FileDeletion.RunAsync(this, _vm, redundant, "Purge duplicates");
        UpdateUndoButton();
    }

    /// <summary>
    /// Read the length and quality of the selected files — the single-file answer to what a
    /// scan works out for everything. Only the container header is read, so it is a moment
    /// per file rather than the minutes a deep check takes.
    /// </summary>
    private async void OnVerifyFilesClick(object sender, RoutedEventArgs e)
    {
        var rows = FilesGrid.SelectedItems.OfType<FileRow>().ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show(this, "Select the files to verify first.",
                "Verify", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = await _vm.VerifyFilesAsync(rows.Select(r => r.Model).ToList());
        MessageBox.Show(this, result, "Verify", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
