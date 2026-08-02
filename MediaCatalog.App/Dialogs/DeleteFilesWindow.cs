using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MediaCatalog.App.Infrastructure;
using MediaCatalog.Core.Models;

namespace MediaCatalog.App;

/// <summary>
/// Confirms deleting files from disk, and lists every one of them — a batch answered once
/// is still a batch the user is entitled to read before it happens.
///
/// Files go to the Recycle Bin by default. Skipping the bin is irreversible, so it takes a
/// second confirmation; that confirmation starts clear every time the dialog opens, even
/// when the setting has pre-ticked the skip itself.
/// </summary>
public class DeleteFilesWindow : Window
{
    private readonly CheckBox _permanent = new()
    {
        Content = "Skip the Recycle Bin (delete permanently)",
        Margin = new Thickness(0, 10, 0, 0)
    };

    private readonly CheckBox _confirm = new()
    {
        Content = "I understand these files will be destroyed and cannot be recovered",
        Margin = new Thickness(20, 6, 0, 0),
        IsEnabled = false,
        Foreground = System.Windows.Media.Brushes.Firebrick
    };

    private readonly Button _delete = new()
    {
        Content = "Delete", Width = 180, FontWeight = FontWeights.Bold,
        Margin = new Thickness(0, 0, 6, 0)
    };

    /// <summary>True when the user chose to bypass the Recycle Bin.</summary>
    public bool DeletePermanently => _permanent.IsChecked == true;

    /// <param name="defaultSkipRecycleBin">
    /// Open with the bin already skipped, because the user has asked for that in Settings.
    /// The destructive confirmation is still theirs to tick.
    /// </param>
    public DeleteFilesWindow(IReadOnlyList<MediaFile> files, bool defaultSkipRecycleBin = false)
    {
        Title = "Delete files"; Width = 700; Height = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var dock = new DockPanel { Margin = new Thickness(14) };

        var total = files.Sum(f => f.SizeBytes);
        var heading = new TextBlock
        {
            Text = $"Delete {files.Count} file(s) — {Format.Bytes(total)} — from disk?",
            FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(heading, Dock.Top);
        dock.Children.Add(heading);

        var options = new StackPanel();
        options.Children.Add(_permanent);
        options.Children.Add(_confirm);
        options.Children.Add(new TextBlock
        {
            Text = "Recycled files can be restored from the Recycle Bin, and an accidental delete " +
                   "can be undone. Permanently deleted files are gone: nothing in this program or " +
                   "in Windows will bring them back.",
            Foreground = System.Windows.Media.Brushes.Gray, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        _delete.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(_delete);
        buttons.Children.Add(new Button { Content = "Cancel", Width = 90, IsCancel = true });
        options.Children.Add(buttons);

        DockPanel.SetDock(options, Dock.Bottom);
        dock.Children.Add(options);

        dock.Children.Add(new ListBox
        {
            ItemsSource = files.Select(f => $"{Format.Bytes(f.SizeBytes),10}   {f.FullPath}").ToList(),
            FontFamily = new System.Windows.Media.FontFamily("Consolas, Courier New, monospace"),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        });

        // The permanent-delete tick arms the confirmation; the confirmation gates the
        // button. Unticking "permanent" clears the confirmation, so switching back and
        // forth can never leave a stale tick behind.
        _permanent.Checked += (_, _) => { _confirm.IsEnabled = true; UpdateDeleteButton(); };
        _permanent.Unchecked += (_, _) =>
        {
            _confirm.IsChecked = false;
            _confirm.IsEnabled = false;
            UpdateDeleteButton();
        };
        _confirm.Checked += (_, _) => UpdateDeleteButton();
        _confirm.Unchecked += (_, _) => UpdateDeleteButton();

        // Setting the tick raises Checked, which arms the confirmation for them; what it
        // must never do is tick the confirmation as well.
        if (defaultSkipRecycleBin) _permanent.IsChecked = true;
        UpdateDeleteButton();

        Content = dock;
    }

    private void UpdateDeleteButton()
    {
        var permanent = _permanent.IsChecked == true;
        _delete.IsEnabled = !permanent || _confirm.IsChecked == true;
        _delete.Content = permanent ? "Delete permanently" : "Delete to Recycle Bin";
    }
}
