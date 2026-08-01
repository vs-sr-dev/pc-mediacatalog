using System.Collections.Generic;
using System.Linq;
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
/// One group at a time: each is a distinct piece of content with its own right answer.
/// </summary>
public class ConsolidatedDuplicatesWindow : Window
{
    private sealed class Row
    {
        public required MediaFile File { get; init; }
        public required bool InLibrary { get; init; }
        public string Display =>
            $"{(InLibrary ? "★ in the library" : "  other copy   ")}  {Format.Bytes(File.SizeBytes),10}   " +
            $"{File.FullPath}";
    }

    private readonly ListBox _list;
    private readonly CheckBox _applyToAll;

    /// <summary>What the user chose.</summary>
    public ConsolidatedDuplicateAction Action { get; private set; } = ConsolidatedDuplicateAction.Nothing;

    /// <summary>The copy to keep, when <see cref="Action"/> is KeepChosen.</summary>
    public MediaFile? Keeper { get; private set; }

    /// <summary>Do the same for every remaining group without asking again.</summary>
    public bool ApplyToAll => _applyToAll.IsChecked == true;

    /// <param name="consolidated">The copy already sitting in its library location.</param>
    /// <param name="copies">Every copy of it, the library one included.</param>
    /// <param name="remaining">How many more groups are waiting, for the "do this to all" option.</param>
    public ConsolidatedDuplicatesWindow(MediaFile consolidated, IReadOnlyList<MediaFile> copies, int remaining)
    {
        Title = "Already consolidated"; Width = 900; Height = 440;
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
                   "into the library and the rest go.",
            Foreground = System.Windows.Media.Brushes.Gray,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(explain, Dock.Top);
        dock.Children.Add(explain);

        _applyToAll = new CheckBox
        {
            Content = $"Do the same for the other {remaining} file(s) in this run",
            Visibility = remaining > 0 ? Visibility.Visible : Visibility.Collapsed,
            Margin = new Thickness(0, 8, 0, 4)
        };

        var options = new StackPanel();
        options.Children.Add(_applyToAll);

        var buttons = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        buttons.Children.Add(Act("Delete all duplicates", ConsolidatedDuplicateAction.DeleteAllDuplicates,
            "Keep the copy in the library and send every other copy to the Recycle Bin."));
        buttons.Children.Add(Act("Keep the selected copy", ConsolidatedDuplicateAction.KeepChosen,
            "Delete the others, and move the copy you picked into the library if it is not there already."));
        buttons.Children.Add(new Button
        {
            Content = "Leave them alone", Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(12, 0, 0, 6), IsCancel = true
        });
        options.Children.Add(buttons);

        DockPanel.SetDock(options, Dock.Bottom);
        dock.Children.Add(options);

        _list = new ListBox
        {
            ItemsSource = copies.Select(f => new Row
            {
                File = f,
                InLibrary = ReferenceEquals(f, consolidated)
            }).ToList(),
            DisplayMemberPath = nameof(Row.Display),
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
                if (_list.SelectedItem is not Row row)
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
}
