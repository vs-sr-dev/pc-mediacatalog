using System.IO;
using System.Windows;
using System.Windows.Controls;
using MediaCatalog.App.ViewModels;
using Microsoft.Win32;

namespace MediaCatalog.App;

/// <summary>
/// Interaction logic for MainWindow.xaml. Keeps only view concerns here
/// (dialogs, folder picking); all catalogue logic lives in <see cref="MainViewModel"/>.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
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

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_vm.Settings, _vm.Categories) { Owner = this };
        if (dlg.ShowDialog() == true)
            _vm.ApplyAppSettings(dlg.Result);
    }

    private void OnClearFilter(object sender, RoutedEventArgs e) => _vm.FilterPattern = "";

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
        var confirm = MessageBox.Show(this,
            $"{verb} {rows.Count} file(s) into the structured library folders?\n\n" +
            "TV → <TvDir>\\<A-Z or #>\\<Show>\\Season NN\\\nFilms → <FilmDir>\\<A-Z or #>\\<Title (Year)>\\\n\n" +
            "Files without a category or a configured target are skipped.",
            "Consolidate", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        var result = await _vm.ConsolidateAsync(rows, deleteOriginal);
        MessageBox.Show(this, result, "Consolidate", MessageBoxButton.OK, MessageBoxImage.Information);
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
            _vm.SetCategoryForFolder(folder, dlg.SelectedCategory, dlg.IncludeSubdirectories);
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

        var subdirs = MessageBox.Show(this,
            $"Exclude this folder from results and future scans?\n\n{folder}\n\n" +
            "Yes = also exclude all subfolders.  No = just this folder.",
            "Exclude folder", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (subdirs == MessageBoxResult.Cancel) return;
        _vm.ExcludeFolder(folder, subdirs == MessageBoxResult.Yes);
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

    // --- Duplicates -------------------------------------------------------

    private void OnGridDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (FilesGrid.SelectedItem is FileRow row && row.IsDuplicate)
            ShowDuplicatesFor(row);
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
