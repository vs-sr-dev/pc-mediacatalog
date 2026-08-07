using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MediaCatalog.App.Infrastructure;
using MediaCatalog.Core.Models;
using MediaCatalog.Core.Relocation;

namespace MediaCatalog.App;

/// <summary>
/// Shown when a move would land on a name that is already taken. Rather than silently
/// renaming the arrival — which is how a library ends up with "Film (1).mkv" beside
/// "Film.mkv", neither of them obviously the good one — both files are put side by side
/// with every known copy of either, and the user says which one to keep.
///
/// A deep check is available from here: whether a file decodes is often exactly the fact
/// that decides which of two identically named copies is worth keeping.
/// </summary>
public class FileCollisionWindow : Window
{
    /// <summary>One file in the picture, and what it is doing here.</summary>
    private sealed class Row : ObservableObject
    {
        public required MediaFile File { get; init; }
        public required string Role { get; init; }

        public string Display =>
            $"{Role,-22} {Format.Bytes(File.SizeBytes),10}   {Modified}   {Integrity}   {File.FullPath}";

        private string Modified => File.LastModifiedUtc == default
            ? "                "
            : File.LastModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        private string Integrity => File.Integrity switch
        {
            IntegrityStatus.Ok => "ok       ",
            IntegrityStatus.Corrupt => "CORRUPT  ",
            IntegrityStatus.IncompleteDownload => "partial  ",
            _ => "unchecked"
        };

        /// <summary>Redraw after a deep check has changed what we know.</summary>
        public void Refresh() => OnPropertyChanged(nameof(Display));
    }

    private readonly ObservableCollection<Row> _rows = new();
    private readonly ListBox _list;
    private readonly Button _consolidate;
    private readonly Func<MediaFile, Task<string>> _deepCheck;

    private readonly CheckBox _deleteDuplicates = new()
    {
        Content = "Delete every other copy of both files once this is decided",
        Margin = new Thickness(0, 10, 0, 2)
    };
    private readonly CheckBox _applyToRest = new()
    {
        Content = "Answer the rest of this move the same way",
        Margin = new Thickness(0, 2, 0, 2)
    };

    /// <summary>What the user decided. Cancel if they closed the window.</summary>
    public CollisionResolution Resolution { get; private set; } = CollisionResolution.Cancelled;

    public FileCollisionWindow(CollisionRequest request, Func<MediaFile, Task<string>> deepCheck)
    {
        _deepCheck = deepCheck;
        // "moved" or "consolidated" — name the operation the user actually started.
        var verb = string.IsNullOrWhiteSpace(request.Operation) ? "moved" : request.Operation;

        Title = "Two files, one name"; Width = 980; Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var dock = new DockPanel { Margin = new Thickness(14) };

        var heading = new TextBlock
        {
            Text = $"'{Path.GetFileName(request.DestinationPath)}' already exists where this file " +
                   $"is being {verb}.",
            FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4)
        };
        DockPanel.SetDock(heading, Dock.Top);
        dock.Children.Add(heading);

        var explain = new TextBlock
        {
            Text = request.SameContent
                ? "The two are byte-for-byte identical, so whichever you keep you keep the same " +
                  "content — the choice is really about which location to keep it in."
                : "The two are different files that happen to share a name. Sizes, dates and " +
                  "integrity are below; a deep check will decode a file end to end and say " +
                  "whether it is damaged. Picking a row and choosing \"Consolidate selected\" " +
                  "files that copy — any of them, not only the two in the collision.",
            Foreground = System.Windows.Media.Brushes.Gray,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(explain, Dock.Top);
        dock.Children.Add(explain);

        BuildRows(request, verb);

        var options = new StackPanel();
        options.Children.Add(_deleteDuplicates);
        options.Children.Add(_applyToRest);

        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(Choice($"Keep the one being {verb}", CollisionChoice.KeepIncoming,
            "Send the file at the destination to the Recycle Bin, then move this one in."));
        buttons.Children.Add(Choice("Keep the one already there", CollisionChoice.KeepExisting,
            $"Leave the destination alone; the file being {verb} stays where it is."));
        buttons.Children.Add(Choice("Keep both — rename the arrival", CollisionChoice.KeepBoth,
            "The arriving file is given a free name — \"name (1).ext\"."));
        buttons.Children.Add(Choice("Skip this file", CollisionChoice.Skip,
            "Leave both alone and carry on with the rest of the batch."));

        // The list is not only the two files in the collision: it holds every other copy of
        // either of them, and the best copy is often one of those. Picking a row says
        // "this is the one" — it goes to the destination and the rest make way for it.
        _consolidate = new Button
        {
            Content = "Consolidate selected", Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 0, 8, 6), IsEnabled = false,
            ToolTip = "File the copy you have picked at the destination name, whichever of " +
                      "the copies below it is. The one already there makes way for it."
        };
        _consolidate.Click += (_, _) => ConsolidateSelected();
        buttons.Children.Add(_consolidate);

        var deep = new Button
        {
            Content = "Deep check selected", Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(16, 0, 8, 6),
            ToolTip = "Decode the selected files with FFmpeg to find out whether they are damaged."
        };
        deep.Click += async (_, _) => await DeepCheckSelectedAsync(deep);
        buttons.Children.Add(deep);

        buttons.Children.Add(new Button
        {
            Content = "Cancel the move", Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 0, 0, 6), IsCancel = true
        });
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
        // Consolidating is a choice about one copy, so it waits until exactly one is picked.
        _list.SelectionChanged += (_, _) =>
            _consolidate.IsEnabled = _list.SelectedItems.Count == 1;
        _list.MouseDoubleClick += (_, _) =>
        {
            if (_list.SelectedItems.Count == 1) ConsolidateSelected();
        };
        dock.Children.Add(_list);

        Content = dock;
    }

    /// <summary>
    /// Keep the one copy the user picked out of the list. Everything else about the answer —
    /// clearing away the other copies, reusing it for the rest of the batch — is left to the
    /// caller, exactly as with the four straightforward choices.
    /// </summary>
    private void ConsolidateSelected()
    {
        if (_list.SelectedItems.OfType<Row>().SingleOrDefault() is not { } row) return;

        Resolution = new CollisionResolution(
            CollisionChoice.KeepSelected,
            _deleteDuplicates.IsChecked == true,
            ApplyToRemaining: false,
            row.File);
        DialogResult = true;
    }

    private void BuildRows(CollisionRequest request, string verb)
    {
        _rows.Add(new Row { File = request.Incoming, Role = "being " + verb });

        if (request.Existing != null)
            _rows.Add(new Row { File = request.Existing, Role = "at the destination" });
        else
            _rows.Add(new Row
            {
                // Not catalogued — describe it from the disk so it is still comparable.
                File = Describe(request.DestinationPath),
                Role = "at the destination"
            });

        foreach (var copy in request.IncomingDuplicates)
            _rows.Add(new Row { File = copy, Role = "copy of the first" });
        foreach (var copy in request.ExistingDuplicates)
            _rows.Add(new Row { File = copy, Role = "copy of the second" });
    }

    /// <summary>
    /// A stand-in entry for a file on disk that the catalogue has never seen, so the list
    /// can show its size and date alongside the rest.
    /// </summary>
    private static MediaFile Describe(string path)
    {
        var file = new MediaFile { FullPath = path, FileName = Path.GetFileName(path) };
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return file;
            file.SizeBytes = info.Length;
            file.LastModifiedUtc = info.LastWriteTimeUtc;
            file.Extension = info.Extension;
        }
        catch { /* whatever we could read is better than nothing */ }
        return file;
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
        var rows = _list.SelectedItems.OfType<Row>().ToList();
        if (rows.Count == 0) rows = _rows.ToList();

        button.IsEnabled = false;
        try
        {
            var results = new List<string>();
            foreach (var row in rows)
            {
                results.Add(await _deepCheck(row.File));
                row.Refresh();
            }
            MessageBox.Show(this, string.Join("\n", results), "Deep check",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally { button.IsEnabled = true; }
    }
}
