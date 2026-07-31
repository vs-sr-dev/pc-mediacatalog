using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MediaCatalog.App.Infrastructure;
using MediaCatalog.App.ViewModels;
using MediaCatalog.Core.Duplicates;
using MediaCatalog.Core.Models;
using Microsoft.Win32;

namespace MediaCatalog.App;

/// <summary>
/// Lists every exact copy of a file and lets the user copy, move or delete the
/// selected ones. Reuses the copy-and-verify relocation, so moves are safe.
/// </summary>
public class DuplicateManagerWindow : Window
{
    private sealed class DupRow
    {
        public required MediaFile File { get; init; }
        public string Display => $"{Format.Bytes(File.SizeBytes)}   {File.FullPath}";
    }

    private readonly MainViewModel _vm;
    private readonly string _sha;
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Extended, Margin = new Thickness(0, 6, 0, 6) };
    private readonly TextBlock _summary = new();

    public DuplicateManagerWindow(MainViewModel vm, DuplicateGroup group)
    {
        _vm = vm;
        _sha = group.Sha256;

        Title = "Manage duplicates"; Width = 820; Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var dock = new DockPanel { Margin = new Thickness(12) };

        DockPanel.SetDock(_summary, Dock.Top);
        _summary.Margin = new Thickness(0, 0, 0, 4);
        dock.Children.Add(_summary);

        var hint = new TextBlock
        {
            Text = "These files are byte-for-byte identical. Select one or more, then copy, move or delete them.",
            TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Gray
        };
        DockPanel.SetDock(hint, Dock.Top);
        dock.Children.Add(hint);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        buttons.Children.Add(MakeButton("Copy selected to…", async () => await RelocateAsync(delete: false)));
        buttons.Children.Add(MakeButton("Move selected to…", async () => await RelocateAsync(delete: true)));
        buttons.Children.Add(MakeButton("Consolidate selected", async () => await ConsolidateAsync()));
        buttons.Children.Add(MakeButton("Delete selected", OnDelete));
        buttons.Children.Add(new Button
        {
            Content = "Close", Width = 84, Margin = new Thickness(16, 0, 0, 0), IsCancel = true
        });
        dock.Children.Add(buttons);

        _list.DisplayMemberPath = nameof(DupRow.Display);
        dock.Children.Add(_list);

        Content = dock;
        Reload();
    }

    private Button MakeButton(string text, System.Action onClick)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
        b.Click += (_, _) => onClick();
        return b;
    }

    private List<MediaFile> Selected() =>
        _list.SelectedItems.OfType<DupRow>().Select(r => r.File).ToList();

    private async System.Threading.Tasks.Task RelocateAsync(bool delete)
    {
        var files = Selected();
        if (files.Count == 0) { WarnNoSelection(); return; }

        var dlg = new OpenFolderDialog { Title = delete ? "Move to folder" : "Copy to folder" };
        if (dlg.ShowDialog(this) != true) return;

        var msg = await _vm.RelocateModelsAsync(files, dlg.FolderName, delete);
        MessageBox.Show(this, msg, "Duplicates", MessageBoxButton.OK, MessageBoxImage.Information);
        Reload();
    }

    /// <summary>
    /// File the selected copies where their category says they belong, without the user
    /// having to work out the destination folder themselves.
    /// </summary>
    private async System.Threading.Tasks.Task ConsolidateAsync()
    {
        var files = Selected();
        if (files.Count == 0) { WarnNoSelection(); return; }

        var untitled = _vm.WithoutTitle(files);
        if (untitled.Count > 0)
        {
            var typed = PromptWindow.Ask(this, "Title needed",
                "These copies have no title yet, and the title decides where they are filed.\n\n" +
                "Title for them:",
                System.IO.Path.GetFileNameWithoutExtension(untitled[0].FileName));
            if (string.IsNullOrWhiteSpace(typed)) return;
            _vm.SetTitleForModels(untitled, typed.Trim());
        }

        var move = MessageBox.Show(this,
            $"Consolidate {files.Count} selected file(s) into the library?\n\n" +
            "Yes = move (copy, verify, delete original). No = copy only.",
            "Consolidate", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (move == MessageBoxResult.Cancel) return;

        var outcome = await _vm.ConsolidateModelsAsync(files, move == MessageBoxResult.Yes);
        MessageBox.Show(this, outcome.Message, "Consolidate", MessageBoxButton.OK, MessageBoxImage.Information);
        Reload();
    }

    private void OnDelete()
    {
        var files = Selected();
        if (files.Count == 0) { WarnNoSelection(); return; }

        var confirm = MessageBox.Show(this,
            $"Permanently delete {files.Count} selected file(s) from disk?",
            "Delete duplicates", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        foreach (var f in files) _vm.DeleteFile(f);
        Reload();
    }

    private void WarnNoSelection() =>
        MessageBox.Show(this, "Select one or more files in the list first.",
            "Duplicates", MessageBoxButton.OK, MessageBoxImage.Information);

    private void Reload()
    {
        var group = _vm.DuplicateGroupBySha(_sha);
        if (group == null)
        {
            _summary.Text = "No duplicates remain.";
            _list.ItemsSource = null;
            return;
        }
        _summary.Text = $"{group.Files.Count} copies • {Format.Bytes(group.SizeBytes)} each • " +
                        $"{Format.Bytes(group.ReclaimableBytes)} reclaimable";
        _list.ItemsSource = group.Files.Select(f => new DupRow { File = f }).ToList();
    }
}
