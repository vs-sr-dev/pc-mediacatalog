using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MediaCatalog.App.Infrastructure;
using MediaCatalog.Core.Models;

namespace MediaCatalog.App;

/// <summary>What the user chose to do about the files that could not be hashed.</summary>
public enum UnhashedAction
{
    /// <summary>Close and leave them as they are.</summary>
    None,
    /// <summary>Read them again — usually enough once whatever held them open has let go.</summary>
    Retry,
    /// <summary>Decode them with FFmpeg to find out whether they are damaged.</summary>
    DeepCheck,
    /// <summary>Delete them.</summary>
    Delete
}

/// <summary>
/// Shown after a scan when some files could not be hashed. Without a hash a file is
/// invisible to duplicate detection, so rather than let it disappear quietly the list is
/// put in front of the user with the three things worth doing about it.
/// </summary>
public class UnhashedFilesWindow : Window
{
    private readonly ListBox _list;

    /// <summary>What the user picked; <see cref="UnhashedAction.None"/> if they just closed it.</summary>
    public UnhashedAction Action { get; private set; } = UnhashedAction.None;

    /// <summary>The files the action applies to: the selected ones, or all of them.</summary>
    public IReadOnlyList<MediaFile> Chosen =>
        _list.SelectedItems.Count > 0
            ? _list.SelectedItems.Cast<Row>().Select(r => r.File).ToList()
            : _list.Items.Cast<Row>().Select(r => r.File).ToList();

    private sealed record Row(MediaFile File)
    {
        public override string ToString() => $"{Format.Bytes(File.SizeBytes),10}   {File.FullPath}";
    }

    public UnhashedFilesWindow(IReadOnlyList<MediaFile> files, bool canDeepCheck)
    {
        Title = "Files that could not be hashed";
        Width = 820; Height = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var dock = new DockPanel { Margin = new Thickness(12) };

        var message = new TextBlock
        {
            Text = $"{files.Count} file(s) were found but could not be read to compute a hash — " +
                   "usually because another program has them open, or the drive refused the read. " +
                   "Duplicate detection cannot see them until they have one.\n\n" +
                   "Select the files to act on, or leave the selection empty to act on all of them.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(message, Dock.Top);
        dock.Children.Add(message);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };

        buttons.Children.Add(Act("Rescan (hash again)", UnhashedAction.Retry, 150,
            "Read the files again and hash them.", isDefault: true));
        buttons.Children.Add(Act("Deep check…", UnhashedAction.DeepCheck, 110,
            canDeepCheck
                ? "Decode each file with FFmpeg to find out whether it is damaged."
                : "Needs FFmpeg and ffprobe — set them up under Tools… first.",
            enabled: canDeepCheck));
        buttons.Children.Add(Act("Delete…", UnhashedAction.Delete, 90,
            "Delete these files (you will be asked to confirm)."));
        buttons.Children.Add(new Button
        {
            Content = "Close", Width = 84, IsCancel = true, Margin = new Thickness(6, 0, 0, 0)
        });
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(buttons);

        _list = new ListBox
        {
            ItemsSource = files.Select(f => new Row(f)).ToList(),
            SelectionMode = SelectionMode.Extended,
            FontFamily = new System.Windows.Media.FontFamily("Consolas, Courier New, monospace"),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        dock.Children.Add(_list);

        Content = dock;

        Button Act(string label, UnhashedAction action, double width, string tip,
            bool enabled = true, bool isDefault = false)
        {
            var b = new Button
            {
                Content = label, Width = width, ToolTip = tip, IsEnabled = enabled,
                IsDefault = isDefault, Margin = new Thickness(6, 0, 0, 0)
            };
            b.Click += (_, _) => { Action = action; DialogResult = true; };
            return b;
        }
    }
}
