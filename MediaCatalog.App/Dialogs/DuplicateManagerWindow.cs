using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MediaCatalog.App.Infrastructure;
using MediaCatalog.App.ViewModels;
using MediaCatalog.Core.Duplicates;
using MediaCatalog.Core.Models;
using Microsoft.Win32;

namespace MediaCatalog.App;

/// <summary>
/// Lists every exact copy of a file and lets the user copy, move, check or delete the
/// selected ones. Relocation reuses the copy-and-verify path, so moves are safe, and
/// deleting goes through the one shared delete conversation — which means a read-only
/// copy is dealt with here exactly as it is anywhere else.
/// </summary>
public class DuplicateManagerWindow : Window
{
    private sealed class DupRow
    {
        public required MediaFile File { get; init; }
        public required bool InLibrary { get; init; }
        public string Display =>
            $"{Format.Bytes(File.SizeBytes),10}   {(InLibrary ? "★ " : "  ")}{File.FullPath}" +
            (File.Integrity == IntegrityStatus.Corrupt ? "   [corrupt]" : "");
    }

    private readonly MainViewModel _vm;
    private readonly string _sha;
    private readonly ListBox _list = new()
    {
        SelectionMode = SelectionMode.Extended,
        Margin = new Thickness(0, 6, 0, 6),
        HorizontalContentAlignment = HorizontalAlignment.Stretch
    };
    private readonly TextBlock _summary = new();

    public DuplicateManagerWindow(MainViewModel vm, DuplicateGroup group)
    {
        _vm = vm;
        _sha = group.Sha256;

        Title = "Manage duplicates"; Width = 880; Height = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var dock = new DockPanel { Margin = new Thickness(12) };

        DockPanel.SetDock(_summary, Dock.Top);
        _summary.Margin = new Thickness(0, 0, 0, 4);
        _summary.FontWeight = FontWeights.Bold;
        dock.Children.Add(_summary);

        var hint = new TextBlock
        {
            Text = "These files are byte-for-byte identical — ★ marks the one in the library. " +
                   "Select one or more and copy, move or delete them, or keep a single copy and " +
                   "have the rest removed.",
            TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Gray
        };
        DockPanel.SetDock(hint, Dock.Top);
        dock.Children.Add(hint);

        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        buttons.Children.Add(MakeButton("Keep this one, delete the rest", KeepOneAsync,
            "Delete every other copy, then make sure the one kept is the copy in the library."));
        buttons.Children.Add(MakeButton("Copy selected to…", () => RelocateAsync(delete: false)));
        buttons.Children.Add(MakeButton("Move selected to…", () => RelocateAsync(delete: true)));
        buttons.Children.Add(MakeButton("Consolidate selected", ConsolidateAsync));
        buttons.Children.Add(MakeButton("Deep check selected", DeepCheckAsync,
            "Decode the selected copies with FFmpeg to find out whether any of them is damaged."));
        buttons.Children.Add(MakeButton("Delete selected…", DeleteAsync));
        buttons.Children.Add(new Button
        {
            Content = "Close", Width = 84, Margin = new Thickness(16, 0, 0, 0), IsCancel = true
        });
        dock.Children.Add(buttons);

        _list.DisplayMemberPath = nameof(DupRow.Display);
        _list.FontFamily = new System.Windows.Media.FontFamily("Consolas, Courier New, monospace");
        dock.Children.Add(_list);

        Content = dock;
        Reload();
    }

    private Button MakeButton(string text, System.Func<Task> onClick, string? tip = null)
    {
        var b = new Button
        {
            Content = text, Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 0, 8, 6), ToolTip = tip
        };
        b.Click += async (_, _) => await onClick();
        return b;
    }

    private List<MediaFile> Selected() =>
        _list.SelectedItems.OfType<DupRow>().Select(r => r.File).ToList();

    private List<MediaFile> AllCopies() =>
        _list.Items.OfType<DupRow>().Select(r => r.File).ToList();

    private async Task RelocateAsync(bool delete)
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
    private async Task ConsolidateAsync()
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

    /// <summary>
    /// Keep the selected copy and remove every other one — then, if the survivor was not
    /// the copy in the library, move it there so the library keeps the file that was kept.
    /// </summary>
    private async Task KeepOneAsync()
    {
        var files = Selected();
        if (files.Count != 1)
        {
            MessageBox.Show(this, "Select exactly one copy — the one to keep.",
                "Duplicates", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var all = AllCopies();
        var others = all.Count - 1;
        var confirm = MessageBox.Show(this,
            $"Keep this copy and delete the other {others}?\n\n{files[0].FullPath}\n\n" +
            "The others go to the Recycle Bin. If the copy kept is not the one in the " +
            "library, it is moved there afterwards.",
            "Keep one copy", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        var message = await _vm.KeepOneCopyAsync(files[0], all, toRecycleBin: true);
        MessageBox.Show(this, message, "Keep one copy", MessageBoxButton.OK, MessageBoxImage.Information);
        Reload();
    }

    private async Task DeepCheckAsync()
    {
        var files = Selected();
        if (files.Count == 0) { WarnNoSelection(); return; }

        var results = new List<string>();
        foreach (var file in files)
            results.Add(await _vm.DeepCheckOneAsync(file));

        MessageBox.Show(this, string.Join("\n", results), "Deep check",
            MessageBoxButton.OK, MessageBoxImage.Information);
        _vm.PersistAndRefresh();
        Reload();
    }

    private async Task DeleteAsync()
    {
        var files = Selected();
        if (files.Count == 0) { WarnNoSelection(); return; }
        await FileDeletion.RunAsync(this, _vm, files, "Delete duplicates");
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
        _list.ItemsSource = group.Files
            .Select(f => new DupRow { File = f, InLibrary = f.Consolidated })
            .ToList();
    }
}
