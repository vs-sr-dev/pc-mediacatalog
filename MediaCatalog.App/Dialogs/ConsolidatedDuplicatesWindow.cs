using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MediaCatalog.App.Infrastructure;
using MediaCatalog.Core.Models;

namespace MediaCatalog.App;

/// <summary>What to do about the other copies of a file that is already consolidated.</summary>
public enum ConsolidatedDuplicateAction
{
    /// <summary>Leave every copy where it is.</summary>
    Nothing = 0,
    /// <summary>Delete every copy except the one in the library.</summary>
    DeleteAllDuplicates,
    /// <summary>Keep the chosen copy, delete the rest, and file the survivor.</summary>
    KeepChosen
}

/// <summary>
/// Shown when consolidating a file that is already exactly where it belongs. There is
/// nothing to move — but if other copies of it exist elsewhere, this is the natural moment
/// to deal with them, which is the whole point of having consolidated it.
///
/// Before one of them is chosen to survive, any of them can be decoded end to end: the
/// copies are byte-identical to each other, but the disk under one of them may not be, and
/// "which of these actually still reads" is exactly the question that decides which to keep.
///
/// One group at a time: each is a distinct piece of content with its own right answer.
/// </summary>
public class ConsolidatedDuplicatesWindow : Window
{
    private sealed class Row : ObservableObject
    {
        public required MediaFile File { get; init; }
        public required bool InLibrary { get; init; }

        public string Display =>
            $"{(InLibrary ? "★ in the library" : "  other copy   ")}  {Format.Bytes(File.SizeBytes),10}   " +
            $"{Integrity}   {File.FullPath}";

        private string Integrity => File.Integrity switch
        {
            IntegrityStatus.Ok => "ok       ",
            IntegrityStatus.Corrupt => "CORRUPT  ",
            IntegrityStatus.IncompleteDownload => "partial  ",
            _ => "unchecked"
        };

        /// <summary>Redraw once a deep check has changed what we know about the file.</summary>
        public void Refresh() => OnPropertyChanged(nameof(Display));
    }

    private readonly ObservableCollection<Row> _rows = new();
    private readonly ListBox _list;
    private readonly CheckBox _applyToAll;
    private readonly Func<MediaFile, Task<string>>? _deepCheck;

    /// <summary>What the user chose.</summary>
    public ConsolidatedDuplicateAction Action { get; private set; } = ConsolidatedDuplicateAction.Nothing;

    /// <summary>The copy to keep, when <see cref="Action"/> is KeepChosen.</summary>
    public MediaFile? Keeper { get; private set; }

    /// <summary>Do the same for every remaining group without asking again.</summary>
    public bool ApplyToAll => _applyToAll.IsChecked == true;

    /// <param name="consolidated">The copy already sitting in its library location.</param>
    /// <param name="copies">Every copy of it, the library one included.</param>
    /// <param name="remaining">How many more groups are waiting, for the "do this to all" option.</param>
    /// <param name="deepCheck">
    /// Decodes one file and reports what it made of it. Null when there are no external
    /// tools set up, in which case the button says so rather than being hidden.
    /// </param>
    public ConsolidatedDuplicatesWindow(
        MediaFile consolidated, IReadOnlyList<MediaFile> copies, int remaining,
        Func<MediaFile, Task<string>>? deepCheck = null)
    {
        _deepCheck = deepCheck;

        Title = "Already consolidated"; Width = 960; Height = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var dock = new DockPanel { Margin = new Thickness(14) };

        var heading = new TextBlock
        {
            Text = $"'{consolidated.FileName}' is already in its library location — there is nothing to move.",
            FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4)
        };
        DockPanel.SetDock(heading, Dock.Top);
        dock.Children.Add(heading);

        var explain = new TextBlock
        {
            Text = $"{copies.Count - 1} other identical copy(ies) exist elsewhere. Delete them all, or " +
                   "pick the copy to keep — if the one you pick is not the library copy, it is moved " +
                   "into the library and the rest go. They are the same bytes, but not necessarily on " +
                   "the same quality of disk: a deep check decodes them and says which ones still read.",
            Foreground = System.Windows.Media.Brushes.Gray,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(explain, Dock.Top);
        dock.Children.Add(explain);

        _applyToAll = new CheckBox
        {
            Content = $"Do the same for the other {remaining} file(s) in this run",
            ToolTip = "The remaining groups are dealt with in one go, and their copies are listed " +
                      "together in a single delete confirmation.",
            Visibility = remaining > 0 ? Visibility.Visible : Visibility.Collapsed,
            Margin = new Thickness(0, 8, 0, 4)
        };

        var options = new StackPanel();
        options.Children.Add(_applyToAll);

        var buttons = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        buttons.Children.Add(Act("Delete all duplicates", ConsolidatedDuplicateAction.DeleteAllDuplicates,
            "Keep the copy in the library and delete every other copy — the Recycle Bin unless you " +
            "say otherwise in the confirmation."));
        buttons.Children.Add(Act("Keep the selected copy", ConsolidatedDuplicateAction.KeepChosen,
            "Delete the others, and move the copy you picked into the library if it is not there already."));

        var deep = new Button
        {
            Content = "Deep check", Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(12, 0, 8, 6),
            ToolTip = deepCheck == null
                ? "Needs FFmpeg and ffprobe — set them up on the External tools tab in Settings."
                : "Decode the selected copies (or all of them) with FFmpeg and report which are damaged.",
            IsEnabled = deepCheck != null
        };
        deep.Click += async (_, _) => await DeepCheckAsync(deep);
        buttons.Children.Add(deep);

        buttons.Children.Add(new Button
        {
            Content = "Leave them alone", Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(4, 0, 0, 6), IsCancel = true
        });
        options.Children.Add(buttons);

        DockPanel.SetDock(options, Dock.Bottom);
        dock.Children.Add(options);

        foreach (var file in copies)
            _rows.Add(new Row { File = file, InLibrary = ReferenceEquals(file, consolidated) });

        _list = new ListBox
        {
            ItemsSource = _rows,
            DisplayMemberPath = nameof(Row.Display),
            SelectionMode = SelectionMode.Extended,
            FontFamily = new System.Windows.Media.FontFamily("Consolas, Courier New, monospace"),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        _list.SelectedIndex = copies.ToList().FindIndex(f => ReferenceEquals(f, consolidated));
        dock.Children.Add(_list);

        Content = dock;
    }

    private Button Act(string text, ConsolidatedDuplicateAction action, string tip)
    {
        var b = new Button
        {
            Content = text, Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 0, 8, 6), ToolTip = tip
        };
        b.Click += (_, _) =>
        {
            if (action == ConsolidatedDuplicateAction.KeepChosen)
            {
                if (_list.SelectedItems.OfType<Row>().FirstOrDefault() is not { } row)
                {
                    MessageBox.Show(this, "Select the copy you want to keep first.",
                        "Already consolidated", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                Keeper = row.File;
            }
            Action = action;
            DialogResult = true;
        };
        return b;
    }

    /// <summary>
    /// Decode the selected copies — or all of them when nothing is selected, which is what
    /// someone comparing copies actually wants — and say what each one turned out to be.
    /// </summary>
    private async Task DeepCheckAsync(Button button)
    {
        if (_deepCheck == null) return;

        var rows = _list.SelectedItems.OfType<Row>().ToList();
        if (rows.Count == 0) rows = _rows.ToList();

        button.IsEnabled = false;
        var wasContent = button.Content;
        try
        {
            var results = new List<string>();
            for (var i = 0; i < rows.Count; i++)
            {
                button.Content = $"Checking {i + 1}/{rows.Count}…";
                results.Add(await _deepCheck(rows[i].File));
                rows[i].Refresh();
            }
            MessageBox.Show(this, string.Join("\n", results), "Deep check",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            button.Content = wasContent;
            button.IsEnabled = true;
        }
    }
}
