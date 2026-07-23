using System.Windows;
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
}
