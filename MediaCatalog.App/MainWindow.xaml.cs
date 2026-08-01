using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using MediaCatalog.App.Infrastructure;
using MediaCatalog.App.ViewModels;
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

        _tray = new TrayIcon(ShowFromTray, ExitApplication);
        _vm.Notify = _tray.Notify;
        _vm.Undo.Changed += UpdateUndoButton;
        _vm.UnhashedFilesFound = OfferUnhashedFiles;
        _vm.ScanRequested = () => _ = RunScanWizardAsync();
        _vm.ResumeRequested = () => _ = ResumeScanAsync();
        _vm.CollisionResolver = ResolveCollisionAsync;
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
    private bool _toldAboutTray;

    /// <summary>
    /// Send the window to the notification area instead of the taskbar when it is
    /// minimised, if that is what the user has asked for.
    /// </summary>
    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized || !_vm.Settings.MinimiseToTray) return;

        Hide();
        ShowInTaskbar = false;

        // Said once per run, the first time it happens, so the window does not simply
        // vanish with nothing to say where it went.
        if (_toldAboutTray) return;
        _toldAboutTray = true;
        _tray.Notify("Media Catalog",
            "Minimised to the notification area. Double-click the icon to bring it back.");
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveColumnLayout();

        if (!_exiting && _vm.Settings.WatchForNewFiles)
        {
            // Still needed in the background: hide rather than quit.
            e.Cancel = true;
            Hide();
            ShowInTaskbar = false;
            _tray.Notify("Media Catalog", "Still watching for new files. Right-click the tray icon to quit.");
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
    /// Put the exclusion rules a new one has made redundant to the user. Only reached when
    /// the policy is to ask — the other two settings decide without stopping.
    /// </summary>
    private bool AskAboutRedundantExclusions(IReadOnlyList<ExcludedFolder> superseded) =>
        MessageBox.Show(this,
            $"This rule already covers {superseded.Count} existing exclusion(s):\n\n" +
            string.Join("\n", superseded.Select(s => "    " + s.Path)) +
            "\n\nRemove them? Nothing about what gets excluded changes either way — the list " +
            "is simply shorter. You can change how this is handled on the Exclusions tab in Settings.",
            "Redundant rules", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

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
        var deleteOriginal = MessageBox.Show(this,
            $"Move {chosen.Count} file(s) to their consolidation folders?\n\n" +
            "Yes = move (copy, verify, delete original). No = copy only.",
            "Consolidate", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (deleteOriginal == MessageBoxResult.Cancel) return;
        var outcome = await _vm.ApplyConsolidationAsync(chosen, deleteOriginal == MessageBoxResult.Yes);
        MessageBox.Show(this, outcome.Message, "Consolidate", MessageBoxButton.OK, MessageBoxImage.Information);
        await OfferToDeleteRedundantAsync(outcome.AlreadyPresent);
        await HandleAlreadyConsolidatedAsync(outcome.Consolidated);
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
    /// Deal with the files that were already exactly where they belong. On its own that is
    /// simply "already consolidated" and worth no more than saying so — but when other
    /// copies of the same content exist, this is the moment to sort them out.
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

        var standing = ConsolidatedDuplicateAction.Nothing;
        var applyToAll = false;

        for (var i = 0; i < withCopies.Count; i++)
        {
            var file = withCopies[i];
            var copies = new List<MediaFile> { file };
            copies.AddRange(_vm.CopiesOf(file));
            if (copies.Count < 2) continue;    // the earlier rounds may have cleared them

            var action = standing;
            MediaFile? keeper = file;

            if (!applyToAll)
            {
                var dlg = new ConsolidatedDuplicatesWindow(file, copies, withCopies.Count - i - 1)
                { Owner = this };
                if (dlg.ShowDialog() != true) continue;

                action = dlg.Action;
                keeper = dlg.Keeper ?? file;
                if (dlg.ApplyToAll)
                {
                    applyToAll = true;
                    // "The same" can only mean the same policy, not the same file: the
                    // keeper is chosen per group. Deleting the extras is the policy that
                    // carries over.
                    standing = ConsolidatedDuplicateAction.DeleteAllDuplicates;
                }
            }

            switch (action)
            {
                case ConsolidatedDuplicateAction.DeleteAllDuplicates:
                    await FileDeletion.RunAsync(this, _vm,
                        copies.Where(c => !ReferenceEquals(c, file)).ToList(), "Delete duplicates");
                    break;

                case ConsolidatedDuplicateAction.KeepChosen:
                    var message = await _vm.KeepOneCopyAsync(keeper, copies, toRecycleBin: true);
                    MessageBox.Show(this, message, "Keep one copy",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    break;
            }
        }
        UpdateUndoButton();
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

        var deleteOriginal = DeleteOriginalCheck.IsChecked == true;
        var verb = deleteOriginal ? "MOVE (copy, verify, delete original)" : "COPY (verify)";
        var extras = _vm.LinkedExtras(rows.Select(r => r.Model)).Count;
        var confirm = MessageBox.Show(this,
            $"{verb} {rows.Count} file(s) into the structured library folders?\n\n" +
            "TV → <TvDir>\\<A-Z or #>\\<Show>\\Season NN\\NN - name.ext\n" +
            "Films → <FilmDir>\\<A-Z or #>\\<Title (Year)>\\\n" +
            "Specials/featurettes → the same folder, under \\Extras\\\n\n" +
            (extras > 0 ? $"{extras} linked extra(s) will travel with the selection.\n" : "") +
            "Files without a category or a configured target are skipped, and files already " +
            "in the library are reported rather than copied again.",
            "Consolidate", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        var outcome = await _vm.ConsolidateAsync(rows, deleteOriginal);
        MessageBox.Show(this, outcome.Message, "Consolidate", MessageBoxButton.OK, MessageBoxImage.Information);
        await OfferToDeleteRedundantAsync(outcome.AlreadyPresent);
        await HandleAlreadyConsolidatedAsync(outcome.Consolidated);
    }

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

    private void OnSetCategoryFolder(object sender, RoutedEventArgs e)
    {
        var row = FilesGrid.SelectedItems.OfType<FileRow>().FirstOrDefault();
        if (row == null) return;
        var folder = Path.GetDirectoryName(row.Model.FullPath);
        if (string.IsNullOrEmpty(folder)) return;

        var dlg = new CategoryFolderWindow(folder, _vm.Categories) { Owner = this };
        if (dlg.ShowDialog() == true)
            _vm.SetCategoryForFolder(dlg.SelectedFolder, dlg.SelectedCategory, dlg.IncludeSubdirectories);
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

        var copies = _vm.CopiesOf(row.Model).Count;
        var dlg = new FileDetailsWindow(row.Model, _vm.Categories, row.Category, copies) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Edits == null) return;

        var result = _vm.ApplyFileEdits(row.Model, dlg.Edits);
        UpdateUndoButton();
        MessageBox.Show(this, result, "Edit details", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnEditTitle(object sender, RoutedEventArgs e)
    {
        var rows = FilesGrid.SelectedItems.OfType<FileRow>().ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show(this, "Select one or more files in the list first.",
                "Edit title", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Only the byte-identical copies come along. Another file that happens to carry the
        // same title may well be something else entirely, and is left alone.
        var copies = rows.Count == 1 ? _vm.CopiesOf(rows[0].Model).Count : 0;
        var note = copies > 0
            ? $"\n\n{copies} identical copy(ies) of this file will be given the same title."
            : "";
        var prompt = rows.Count == 1
            ? $"Title for '{rows[0].FileName}':{note}"
            : $"Title for the {rows.Count} selected file(s):";

        var typed = PromptWindow.Ask(this, "Edit title", prompt, TitleSeed(rows[0].Model));
        if (string.IsNullOrWhiteSpace(typed)) return;

        var result = _vm.SetTitleForFiles(rows, typed.Trim());
        UpdateUndoButton();
        MessageBox.Show(this, result, "Edit title", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// What to put in the title box: the title it already has, or failing that the file
    /// name without its extension — a far better starting point than an empty box.
    /// </summary>
    private static string TitleSeed(MediaFile file) =>
        !string.IsNullOrWhiteSpace(file.EffectiveTitle)
            ? file.EffectiveTitle
            : Path.GetFileNameWithoutExtension(file.FileName);

    private void OnSetTitleFolder(object sender, RoutedEventArgs e)
    {
        var row = FilesGrid.SelectedItems.OfType<FileRow>().FirstOrDefault();
        if (row == null)
        {
            MessageBox.Show(this, "Select a file in the folder you want to title first.",
                "Set title for folder", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var folder = Path.GetDirectoryName(row.Model.FullPath);
        if (string.IsNullOrEmpty(folder)) return;

        // The folder's own name is usually the show name, so offer it as the starting point.
        var suggestion = !string.IsNullOrWhiteSpace(row.Model.EffectiveTitle)
            ? row.Model.EffectiveTitle
            : Path.GetFileName(folder);

        var dlg = new TitleFolderWindow(folder, suggestion) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var result = _vm.SetTitleForFolder(dlg.SelectedFolder, dlg.Title_, dlg.IncludeSubdirectories);
        UpdateUndoButton();
        MessageBox.Show(this, result, "Set title for folder", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnEditSeasonEpisode(object sender, RoutedEventArgs e)
    {
        var rows = FilesGrid.SelectedItems.OfType<FileRow>().ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show(this, "Select one or more files in the list first.",
                "Season / episode", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new SeasonEpisodeWindow(rows.Select(r => r.Model).ToList()) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var result = _vm.SetSeasonEpisode(rows, dlg.Season, dlg.Episode);
        UpdateUndoButton();
        MessageBox.Show(this, result, "Season / episode", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnRenameFile(object sender, RoutedEventArgs e)
    {
        var row = FilesGrid.SelectedItems.OfType<FileRow>().FirstOrDefault();
        if (row == null)
        {
            MessageBox.Show(this, "Select the file to rename first.",
                "Rename file", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var typed = PromptWindow.Ask(this, "Rename file",
            $"New name for the file (keep the extension):\n\n{row.FullPath}", row.FileName);
        if (string.IsNullOrWhiteSpace(typed) || typed.Trim() == row.FileName) return;

        var result = _vm.RenameFile(row, typed.Trim());
        UpdateUndoButton();
        MessageBox.Show(this, result, "Rename file", MessageBoxButton.OK, MessageBoxImage.Information);
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

    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Double-click opens the file with its associated application.
        if (FilesGrid.SelectedItem is FileRow row)
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

    private void OpenFile(FileRow row)
    {
        if (!File.Exists(row.FullPath))
        {
            MessageBox.Show(this, "The file no longer exists on disk.",
                "Open file", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(row.FullPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open the file:\n{ex.Message}",
                "Open file", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var row = FilesGrid.SelectedItems.OfType<FileRow>().FirstOrDefault();
        if (row == null) return;
        try
        {
            if (File.Exists(row.FullPath))
            {
                // Open Explorer with the file selected.
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{row.FullPath}\"")
                { UseShellExecute = true });
            }
            else
            {
                var dir = Path.GetDirectoryName(row.FullPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
                else
                    MessageBox.Show(this, "The containing folder no longer exists.",
                        "Open folder", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open the folder:\n{ex.Message}",
                "Open folder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
            MessageBox.Show(this, "This file has no exact duplicates.",
                "Duplicates", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        new DuplicateManagerWindow(_vm, group) { Owner = this }.ShowDialog();
    }
}
