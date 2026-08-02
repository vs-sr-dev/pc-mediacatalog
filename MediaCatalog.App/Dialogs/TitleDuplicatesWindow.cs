using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MediaCatalog.App.Infrastructure;
using MediaCatalog.App.ViewModels;
using MediaCatalog.Core.Duplicates;
using MediaCatalog.Core.Models;

namespace MediaCatalog.App;

/// <summary>
/// The duplicates a hash cannot find: two files that say they are the same film, and are,
/// without being the same bytes — the usual cause being the same thing downloaded twice
/// from two different releases.
///
/// Which of them to keep is genuinely a judgement call, so the dialog puts the facts that
/// decide it side by side — size, length, quality and whether the file still decodes — and
/// leaves the choosing to the user. Deleting goes through the ordinary delete conversation,
/// so the Recycle Bin can be skipped here exactly as it can anywhere else.
/// </summary>
public class TitleDuplicatesWindow : Window
{
    private sealed class Row : ObservableObject
    {
        public required MediaFile File { get; init; }

        public string Display =>
            $"{Format.Bytes(File.SizeBytes),10}  {File.LengthDisplay,8}  {File.QualityDisplay,9}  " +
            $"{Integrity}  {(File.Consolidated ? "★" : " ")} {File.FullPath}";

        private string Integrity => File.Integrity switch
        {
            IntegrityStatus.Ok => "ok       ",
            IntegrityStatus.Corrupt => "CORRUPT  ",
            IntegrityStatus.IncompleteDownload => "partial  ",
            _ => "unchecked"
        };

        public void Refresh() => OnPropertyChanged(nameof(Display));
    }

    private readonly MainViewModel _vm;
    private readonly ObservableCollection<TitleDuplicateGroup> _groups = new();
    private readonly ObservableCollection<Row> _rows = new();

    private readonly ListBox _groupList;
    private readonly ListBox _fileList;
    private readonly TextBlock _summary = new()
    {
        FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4)
    };

    public TitleDuplicatesWindow(MainViewModel vm)
    {
        _vm = vm;

        Title = "Possible duplicates"; Width = 1060; Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var dock = new DockPanel { Margin = new Thickness(12) };

        DockPanel.SetDock(_summary, Dock.Top);
        dock.Children.Add(_summary);

        var hint = new TextBlock
        {
            Text = "These files claim to be the same thing — same title, same year, same episode — " +
                   "but are not byte-identical, so each is a separate copy taking up its own room. " +
                   "★ marks a copy that is filed in the library. Deep check decodes them so a damaged " +
                   "copy is not the one you keep.",
            TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Gray,
            Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(hint, Dock.Top);
        dock.Children.Add(hint);

        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        buttons.Children.Add(Btn("Deep check this group", () => DeepCheckAsync(all: true),
            "Decode every copy in the selected group and report which are damaged."));
        buttons.Children.Add(Btn("Deep check selected", () => DeepCheckAsync(all: false),
            "Decode only the copies ticked on the right."));
        buttons.Children.Add(Btn("Keep selected, delete the rest", KeepSelectedAsync,
            "Delete every other copy in this group. The delete confirmation lists them and lets " +
            "you choose the Recycle Bin or a permanent delete."));
        buttons.Children.Add(Btn("Delete selected…", DeleteSelectedAsync,
            "Delete only the copies selected on the right."));
        buttons.Children.Add(Btn("Open containing folder", OpenFolderAsync,
            "Show the selected copy in Explorer."));
        buttons.Children.Add(new Button
        {
            Content = "Close", Width = 84, Margin = new Thickness(16, 0, 0, 0), IsCancel = true
        });
        dock.Children.Add(buttons);

        // The groups on the left, the copies within the selected group on the right: a
        // library can hold hundreds of these, and scrolling one flat list of everything is
        // no way to answer a question about one film.
        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _groupList = new ListBox
        {
            ItemsSource = _groups,
            DisplayMemberPath = nameof(TitleDuplicateGroup.Key),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        _groupList.SelectionChanged += (_, _) => ShowSelectedGroup();
        Grid.SetColumn(_groupList, 0);
        split.Children.Add(_groupList);

        _fileList = new ListBox
        {
            ItemsSource = _rows,
            DisplayMemberPath = nameof(Row.Display),
            SelectionMode = SelectionMode.Extended,
            FontFamily = new System.Windows.Media.FontFamily("Consolas, Courier New, monospace"),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        Grid.SetColumn(_fileList, 2);
        split.Children.Add(_fileList);

        dock.Children.Add(split);
        Content = dock;

        Reload();
    }

    private TitleDuplicateGroup? SelectedGroup => _groupList.SelectedItem as TitleDuplicateGroup;

    private List<MediaFile> Selected() =>
        _fileList.SelectedItems.OfType<Row>().Select(r => r.File).ToList();

    private Button Btn(string text, Func<Task> onClick, string tip)
    {
        var b = new Button
        {
            Content = text, Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 0, 8, 6), ToolTip = tip
        };
        b.Click += async (_, _) => await onClick();
        return b;
    }

    /// <summary>Rebuild the whole picture from the catalogue as it now stands.</summary>
    private void Reload()
    {
        var wasSelected = SelectedGroup?.Key;

        _groups.Clear();
        foreach (var group in _vm.TitleDuplicateGroups()) _groups.Add(group);

        if (_groups.Count == 0)
        {
            _summary.Text = "No files share a title and year without also sharing their contents.";
            _rows.Clear();
            return;
        }

        var reclaimable = _groups.Sum(g => g.ReclaimableBytes);
        _summary.Text = $"{_groups.Count} possible duplicate set(s) • " +
                        $"{_groups.Sum(g => g.Files.Count)} file(s) • " +
                        $"{Format.Bytes(reclaimable)} reclaimable by keeping one of each";

        var restored = _groups.FirstOrDefault(g =>
            string.Equals(g.Key, wasSelected, StringComparison.Ordinal));
        _groupList.SelectedItem = restored ?? _groups[0];
        ShowSelectedGroup();
    }

    private void ShowSelectedGroup()
    {
        _rows.Clear();
        if (SelectedGroup is not { } group) return;
        foreach (var file in group.Files) _rows.Add(new Row { File = file });

        // The biggest copy is the usual keeper, so it starts selected — a starting point,
        // not a recommendation: bigger is not always better, which is what the length and
        // quality columns are there to show.
        if (_rows.Count > 0) _fileList.SelectedIndex = 0;
    }

    private async Task DeepCheckAsync(bool all)
    {
        if (!_vm.CanDoVideo)
        {
            MessageBox.Show(this,
                "A deep check needs FFmpeg and ffprobe — set them up on the External tools tab in Settings.",
                "Deep check", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var files = all ? _rows.Select(r => r.File).ToList() : Selected();
        if (files.Count == 0) { WarnNoSelection(); return; }

        var results = new List<string>();
        foreach (var file in files)
        {
            results.Add(await _vm.DeepCheckOneAsync(file));
            foreach (var row in _rows.Where(r => ReferenceEquals(r.File, file))) row.Refresh();
        }

        _vm.PersistAndRefresh();
        MessageBox.Show(this, string.Join("\n", results), "Deep check",
            MessageBoxButton.OK, MessageBoxImage.Information);
        Reload();
    }

    private async Task KeepSelectedAsync()
    {
        if (SelectedGroup is not { } group) return;
        var keep = Selected();
        if (keep.Count == 0) { WarnNoSelection(); return; }

        var others = group.Files.Where(f => !keep.Contains(f)).ToList();
        if (others.Count == 0)
        {
            MessageBox.Show(this, "Every copy in this set is selected, so there is nothing to delete.",
                "Possible duplicates", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await FileDeletion.RunAsync(this, _vm, others, "Delete the other copies");
        Reload();
    }

    private async Task DeleteSelectedAsync()
    {
        var files = Selected();
        if (files.Count == 0) { WarnNoSelection(); return; }
        await FileDeletion.RunAsync(this, _vm, files, "Delete copies");
        Reload();
    }

    private Task OpenFolderAsync()
    {
        if (Selected().FirstOrDefault() is { } file) ShellOpen.SelectInExplorer(this, file.FullPath);
        else WarnNoSelection();
        return Task.CompletedTask;
    }

    private void WarnNoSelection() =>
        MessageBox.Show(this, "Select one or more copies on the right first.",
            "Possible duplicates", MessageBoxButton.OK, MessageBoxImage.Information);
}
