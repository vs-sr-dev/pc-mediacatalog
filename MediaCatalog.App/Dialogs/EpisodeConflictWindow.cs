using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MediaCatalog.App.Infrastructure;
using MediaCatalog.Core.Consolidation;
using MediaCatalog.Core.Models;
using MediaCatalog.Core.Relocation;

namespace MediaCatalog.App;

/// <summary>
/// Shown when the episode being consolidated is already in the library under a different
/// name — two releases of one episode, named by two different people, with nothing about
/// either name to say they are the same thing.
///
/// This is not a name collision and cannot be treated as one: the two files would sit
/// happily side by side, and the library would quietly be holding the same episode twice.
/// So there is no "keep both" here. One of them stays, and the facts that decide which —
/// size, length, quality, and whether the file still decodes — are put side by side.
/// </summary>
public class EpisodeConflictWindow : Window
{
    private sealed class Row : ObservableObject
    {
        public required MediaFile File { get; init; }
        public required string Role { get; init; }

        public string Display =>
            $"{Role,-24} {Format.Bytes(File.SizeBytes),10}  {File.LengthDisplay,8}  " +
            $"{File.QualityDisplay,9}  {Integrity}  {File.FullPath}";

        private string Integrity => File.Integrity switch
        {
            IntegrityStatus.Ok => "ok       ",
            IntegrityStatus.Corrupt => "CORRUPT  ",
            IntegrityStatus.IncompleteDownload => "partial  ",
            _ => "unchecked"
        };

        public void Refresh() => OnPropertyChanged(nameof(Display));
    }

    private readonly ObservableCollection<Row> _rows = new();
    private readonly ListBox _list;
    private readonly Func<MediaFile, Task<string>>? _deepCheck;

    private readonly CheckBox _deleteDuplicates = new()
    {
        Content = "Delete every other copy of both files once this is decided",
        IsChecked = true,
        Margin = new Thickness(0, 10, 0, 2)
    };
    private readonly CheckBox _applyToRest = new()
    {
        Content = "Answer the rest of this run the same way",
        Margin = new Thickness(0, 2, 0, 2)
    };

    /// <summary>What the user decided. Skipping is what closing the window means.</summary>
    public CollisionResolution Resolution { get; private set; } = new(CollisionChoice.Skip);

    public EpisodeConflictWindow(EpisodeConflict conflict, Func<MediaFile, Task<string>>? deepCheck)
    {
        _deepCheck = deepCheck;

        Title = "That episode is already in the library"; Width = 1000; Height = 580;
        MinWidth = 720; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var dock = new DockPanel { Margin = new Thickness(14) };

        var heading = new TextBlock
        {
            Text = $"{conflict.Description} is already filed in the library, under a different name.",
            FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4)
        };
        DockPanel.SetDock(heading, Dock.Top);
        dock.Children.Add(heading);

        var explain = new TextBlock
        {
            Text = "Both files say they are the same season and episode of the same programme, so " +
                   "they are the same episode whatever they are called — filing this one would " +
                   "leave the library holding it twice. They are different files, though: a " +
                   "different release, or a different quality. Which one should the library keep? " +
                   "The other, and every other copy of either, can go with your answer.",
            Foreground = System.Windows.Media.Brushes.Gray,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(explain, Dock.Top);
        dock.Children.Add(explain);

        _rows.Add(new Row { File = conflict.Existing, Role = "in the library" });
        _rows.Add(new Row { File = conflict.Incoming, Role = "being consolidated" });
        foreach (var copy in conflict.ExistingCopies)
            _rows.Add(new Row { File = copy, Role = "copy of the library one" });
        foreach (var copy in conflict.IncomingCopies)
            _rows.Add(new Row { File = copy, Role = "copy of the arriving one" });

        var options = new StackPanel();
        options.Children.Add(_deleteDuplicates);
        options.Children.Add(_applyToRest);

        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(Choice("Keep the library's copy", CollisionChoice.KeepExisting,
            "Leave the library exactly as it is. The file being consolidated — and, if the box " +
            "above is ticked, every other copy of it — goes to the Recycle Bin."));
        buttons.Children.Add(Choice("Keep the one being consolidated", CollisionChoice.KeepIncoming,
            "File this one in the library and send the copy that was there to the Recycle Bin."));
        buttons.Children.Add(Choice("Leave both alone", CollisionChoice.Skip,
            "Nothing is moved or deleted, and the file stays where it is. The library keeps the " +
            "copy it already had."));

        if (_deepCheck != null)
        {
            var deep = new Button
            {
                Content = "Deep check selected", Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(16, 0, 8, 6),
                ToolTip = "Decode the selected files end to end with FFmpeg — whether a copy is " +
                          "damaged is usually the fact that settles which one to keep."
            };
            deep.Click += async (_, _) => await DeepCheckSelectedAsync(deep);
            buttons.Children.Add(deep);
        }

        var cancel = new Button
        {
            Content = "Cancel the whole run", Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 0, 0, 6),
            ToolTip = "Stop consolidating here. Everything already done stays done."
        };
        cancel.Click += (_, _) =>
        {
            Resolution = new CollisionResolution(CollisionChoice.Cancel);
            DialogResult = true;
        };
        buttons.Children.Add(cancel);
        options.Children.Add(buttons);

        DockPanel.SetDock(options, Dock.Bottom);
        dock.Children.Add(options);

        _list = new ListBox
        {
            ItemsSource = _rows,
            DisplayMemberPath = nameof(Row.Display),
            SelectionMode = SelectionMode.Extended,
            FontFamily = new System.Windows.Media.FontFamily("Consolas, Courier New, monospace"),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        dock.Children.Add(_list);

        Content = dock;
    }

    private Button Choice(string text, CollisionChoice choice, string tip)
    {
        var b = new Button
        {
            Content = text, Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 0, 8, 6), ToolTip = tip
        };
        b.Click += (_, _) =>
        {
            Resolution = new CollisionResolution(
                choice,
                _deleteDuplicates.IsChecked == true,
                _applyToRest.IsChecked == true);
            DialogResult = true;
        };
        return b;
    }

    private async Task DeepCheckSelectedAsync(Button button)
    {
        if (_deepCheck == null) return;

        var rows = _list.SelectedItems.OfType<Row>().ToList();
        if (rows.Count == 0) rows = _rows.ToList();

        var was = button.Content;
        button.IsEnabled = false;
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
            button.Content = was;
            button.IsEnabled = true;
        }
    }
}
