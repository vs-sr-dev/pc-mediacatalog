using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MediaCatalog.App.Infrastructure;
using MediaCatalog.App.ViewModels;
using MediaCatalog.Core.Duplicates;
using MediaCatalog.Core.Filtering;
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

    // A deep check decodes each file end to end: minutes per film, and until now it said
    // nothing at all while it did. Both halves of "how far along is this" are shown — which
    // file of how many, and how far into that one.
    private readonly ProgressBar _progressBar = new()
    {
        Height = 14, Minimum = 0, Maximum = 1000, Visibility = Visibility.Collapsed,
        Margin = new Thickness(0, 4, 0, 0)
    };
    private readonly TextBlock _progressText = new()
    {
        Foreground = System.Windows.Media.Brushes.Gray, Visibility = Visibility.Collapsed,
        TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0)
    };

    /// <summary>Stops a deep check part-way; the files already done keep their verdicts.</summary>
    private CancellationTokenSource? _checking;

    private readonly Button _stop;

    // Narrows the list on the left. A library of any size produces hundreds of these sets,
    // and scrolling for the one film you came here about is not a way to find it.
    private readonly TextBox _filter = new()
    {
        VerticalContentAlignment = VerticalAlignment.Center,
        ToolTip = "Type part of a title to narrow the list. Wildcards work: * for any run of " +
                  "characters, ? for one."
    };

    /// <summary>
    /// The file the user came here about, so the dialog opens on its set with that copy
    /// already picked out. Arriving at a list of hundreds and being left to find your own
    /// file in it is not an answer to "show me the duplicates of this".
    /// </summary>
    private readonly MediaFile? _focus;

    public TitleDuplicatesWindow(MainViewModel vm, MediaFile? focus = null)
    {
        _vm = vm;
        _focus = focus;

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

        var footer = new StackPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        footer.Children.Add(buttons);
        footer.Children.Add(_progressBar);
        footer.Children.Add(_progressText);
        buttons.Children.Add(Btn("Deep check this group", () => DeepCheckAsync(all: true),
            "Decode every copy in the selected group and report which are damaged."));
        buttons.Children.Add(Btn("Deep check selected", () => DeepCheckAsync(all: false),
            "Decode only the copies ticked on the right."));
        _stop = Btn("Stop", StopCheckingAsync,
            "Stop the deep check here. Files already decoded keep their verdicts.");
        _stop.Visibility = Visibility.Collapsed;
        buttons.Children.Add(_stop);
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
        dock.Children.Add(footer);

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

        // The filter sits above the list it filters, where it belongs.
        var left = new DockPanel();
        var filterRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var filterLabel = new TextBlock
        {
            Text = "Name:", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        DockPanel.SetDock(filterLabel, Dock.Left);
        filterRow.Children.Add(filterLabel);
        var clear = new Button
        {
            Content = "✕", Width = 24, Margin = new Thickness(4, 0, 0, 0),
            ToolTip = "Clear the filter."
        };
        clear.Click += (_, _) => _filter.Clear();
        DockPanel.SetDock(clear, Dock.Right);
        filterRow.Children.Add(clear);
        filterRow.Children.Add(_filter);
        _filter.TextChanged += (_, _) => Reload();
        DockPanel.SetDock(filterRow, Dock.Top);
        left.Children.Add(filterRow);
        left.Children.Add(_groupList);

        Grid.SetColumn(left, 0);
        split.Children.Add(left);

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

        var all = _vm.TitleDuplicateGroups();
        var pattern = _filter.Text.Trim();
        var shown = pattern.Length == 0 ? all : all.Where(g => Matches(g, pattern)).ToList();

        _groups.Clear();
        foreach (var group in shown) _groups.Add(group);

        if (all.Count == 0)
        {
            _summary.Text = "No files share a title and year without also sharing their contents.";
            _rows.Clear();
            return;
        }

        if (_groups.Count == 0)
        {
            _summary.Text = $"Nothing matches \"{pattern}\" — {all.Count} set(s) in all.";
            _rows.Clear();
            return;
        }

        var reclaimable = _groups.Sum(g => g.ReclaimableBytes);
        _summary.Text = $"{_groups.Count} possible duplicate set(s)" +
                        (_groups.Count < all.Count ? $" of {all.Count}" : "") + " • " +
                        $"{_groups.Sum(g => g.Files.Count)} file(s) • " +
                        $"{Format.Bytes(reclaimable)} reclaimable by keeping one of each";

        // What to open on, in order of how much the user asked for it: the file they came
        // here about, then whatever was selected before the reload, then the first set.
        var wanted =
            (_focus != null
                ? _groups.FirstOrDefault(g => g.Files.Any(f => ReferenceEquals(f, _focus)))
                : null)
            ?? _groups.FirstOrDefault(g => string.Equals(g.Key, wasSelected, StringComparison.Ordinal))
            ?? _groups[0];

        _groupList.SelectedItem = wanted;
        _groupList.ScrollIntoView(wanted);
        ShowSelectedGroup();
    }

    /// <summary>
    /// True when a set is worth showing for what was typed. The set's own name is checked
    /// first, and then the file names in it — somebody who remembers the file rather than
    /// the title should still find it.
    /// </summary>
    private static bool Matches(TitleDuplicateGroup group, string pattern) =>
        WildcardMatcher.IsMatch(group.Key, pattern) ||
        group.Files.Any(f => WildcardMatcher.IsMatch(f.FileName, pattern));

    private void ShowSelectedGroup()
    {
        _rows.Clear();
        if (SelectedGroup is not { } group) return;
        foreach (var file in group.Files) _rows.Add(new Row { File = file });
        if (_rows.Count == 0) return;

        // The copy the user came here about, if it is in this set — they clicked a file, and
        // being made to find it again in a list of its own duplicates is no answer. Failing
        // that the biggest, which is the usual keeper: a starting point rather than a
        // recommendation, since bigger is not always better, which is what the length and
        // quality columns are there to show.
        var focused = _focus == null
            ? -1
            : _rows.ToList().FindIndex(r => ReferenceEquals(r.File, _focus));

        _fileList.SelectedIndex = focused >= 0 ? focused : 0;
        _fileList.ScrollIntoView(_fileList.SelectedItem);
    }

    private async Task DeepCheckAsync(bool all)
    {
        if (_checking != null) return;   // one at a time

        if (!_vm.CanDoVideo)
        {
            MessageBox.Show(this,
                "A deep check needs FFmpeg and ffprobe — set them up on the External tools tab in Settings.",
                "Deep check", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var files = all ? _rows.Select(r => r.File).ToList() : Selected();
        if (files.Count == 0) { WarnNoSelection(); return; }

        _checking = new CancellationTokenSource();
        ShowProgress(true);

        var results = new List<string>();
        var cancelled = false;
        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                if (_checking.IsCancellationRequested) { cancelled = true; break; }

                var file = files[i];
                var index = i;
                Report(index, files.Count, file, 0);

                // Decoding a film is minutes of work on one file, so how far into *this*
                // file it has reached is as much of the answer as which file it is on.
                var within = new Progress<double>(fraction =>
                    Report(index, files.Count, file, fraction));

                results.Add(await _vm.DeepCheckOneAsync(file, _checking.Token, within));
                foreach (var row in _rows.Where(r => ReferenceEquals(r.File, file))) row.Refresh();
            }
        }
        catch (OperationCanceledException) { cancelled = true; }
        finally
        {
            _checking.Dispose();
            _checking = null;
            ShowProgress(false);
        }

        _vm.PersistAndRefresh();
        MessageBox.Show(this,
            (cancelled ? $"Stopped after {results.Count} of {files.Count} file(s).\n\n" : "") +
            (results.Count == 0 ? "Nothing was decoded." : string.Join("\n", results)),
            "Deep check", MessageBoxButton.OK, MessageBoxImage.Information);
        Reload();
    }

    /// <summary>Say which file of how many, and how far into it, in one line and one bar.</summary>
    private void Report(int index, int total, MediaFile file, double fraction)
    {
        // The bar covers the whole batch, so a file finishing does not send it back to zero.
        _progressBar.Value = Math.Clamp((index + fraction) / total * 1000, 0, 1000);

        var left = total - index - 1;
        _progressText.Text =
            $"Deep checking {index + 1} of {total} — {file.FileName}" +
            (fraction > 0 ? $"  ({fraction:P0} of this file)" : "") +
            (left > 0 ? $"  •  {left} to go after this one" : "  •  last one");
    }

    private void ShowProgress(bool running)
    {
        var visible = running ? Visibility.Visible : Visibility.Collapsed;
        _progressBar.Visibility = visible;
        _progressText.Visibility = visible;
        _stop.Visibility = visible;
        if (!running) _progressBar.Value = 0;
    }

    private Task StopCheckingAsync()
    {
        _checking?.Cancel();
        _progressText.Text = "Stopping after this file…";
        return Task.CompletedTask;
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
