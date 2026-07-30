using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MediaCatalog.App.ViewModels;
using MediaCatalog.Core.Models;
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

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        BuildColumnHeaderMenu();
        ApplySavedColumnLayout();
        Closing += (_, _) => SaveColumnLayout();
        Closed += (_, _) => _vm.Shutdown();
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

    private void OnToolsClick(object sender, RoutedEventArgs e)
    {
        var dlg = new ToolSettingsWindow(_vm.CurrentToolSettings) { Owner = this };
        if (dlg.ShowDialog() == true)
            _vm.ApplyToolSettings(dlg.Result);
    }

    private void PromptForTools(string message)
    {
        var open = MessageBox.Show(this,
            message + "\n\nOpen the Tools settings now?",
            "Tools required", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (open == MessageBoxResult.Yes)
            OnToolsClick(this, new RoutedEventArgs());
    }

    // --- Settings & filter ------------------------------------------------

    /// <summary>
    /// Settings open non-modally so the catalogue stays usable while they are edited;
    /// saving applies immediately through the <see cref="SettingsWindow.Saved"/> event.
    /// </summary>
    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow != null)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }

        var dlg = new SettingsWindow(_vm.Settings, _vm.Categories, _vm.AvailableDriveRoots) { Owner = this };
        dlg.Saved += settings => _vm.ApplyAppSettings(settings);
        dlg.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow = dlg;
        dlg.Show();
    }

    private async void OnRefreshCatalogClick(object sender, RoutedEventArgs e)
    {
        var stale = _vm.StaleEntryCount;
        if (stale == 0)
        {
            MessageBox.Show(this,
                "Every catalogue entry already has everything this version knows how to work out. " +
                "Run a scan instead if you want to pick up new or changed files.",
                "Refresh catalogue", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(this,
            $"Re-derive metadata for {stale} catalogue entr(ies) that predate the current features " +
            "(extras detection, linking, title parsing)?\n\n" +
            "Entries that are already up to date are skipped, and nothing is re-scanned or re-hashed.",
            "Refresh catalogue", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        var result = await _vm.RefreshCatalogAsync();
        MessageBox.Show(this, result, "Refresh catalogue", MessageBoxButton.OK, MessageBoxImage.Information);
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
    }

    /// <summary>
    /// Files whose copy is already in the consolidation location were not moved — the
    /// source is simply redundant, so offer to delete it.
    /// </summary>
    private async Task OfferToDeleteRedundantAsync(IReadOnlyList<MediaFile> present)
    {
        if (present.Count == 0) return;

        var ask = MessageBox.Show(this,
            $"{present.Count} file(s) are already in the consolidation location, so nothing was copied " +
            "for them.\n\nDelete the redundant copies from where they are now?",
            "Already in the library", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ask != MessageBoxResult.Yes) return;

        await DeleteAsync(present.ToList());
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

    // --- Titles -----------------------------------------------------------

    private void OnEditTitle(object sender, RoutedEventArgs e)
    {
        var rows = FilesGrid.SelectedItems.OfType<FileRow>().ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show(this, "Select one or more files in the list first.",
                "Edit title", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var sharing = rows.Count == 1 ? _vm.CountSharingTitle(rows[0].Model) : 0;
        var note = sharing > 0
            ? $"\n\n{sharing} other file(s) currently share this title and will be updated too."
            : "";
        var prompt = rows.Count == 1
            ? $"Title for '{rows[0].FileName}':{note}"
            : $"Title for the {rows.Count} selected file(s):";

        var typed = PromptWindow.Ask(this, "Edit title", prompt, rows[0].Title);
        if (string.IsNullOrWhiteSpace(typed)) return;

        var result = _vm.SetTitleForFiles(rows, typed.Trim());
        MessageBox.Show(this, result, "Edit title", MessageBoxButton.OK, MessageBoxImage.Information);
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

    private async Task DeleteAsync(IReadOnlyList<MediaFile> files)
    {
        var dlg = new DeleteFilesWindow(files) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var message = await _vm.DeleteFilesAsync(files, toRecycleBin: !dlg.DeletePermanently);
        MessageBox.Show(this, message, "Delete files", MessageBoxButton.OK, MessageBoxImage.Information);
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
