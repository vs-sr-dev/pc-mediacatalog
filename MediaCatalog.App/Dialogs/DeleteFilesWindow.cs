using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MediaCatalog.App.Infrastructure;
using MediaCatalog.Core.Models;

namespace MediaCatalog.App;

/// <summary>
/// Confirms deleting files from disk. Files go to the Recycle Bin by default; skipping
/// the bin is irreversible, so it additionally requires a confirmation tick that starts
/// clear every time the dialog is opened.
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
        Content = "Delete", Width = 160, FontWeight = FontWeights.Bold,
        Margin = new Thickness(0, 0, 6, 0)
    };

    /// <summary>True when the user chose to bypass the Recycle Bin.</summary>
    public bool DeletePermanently => _permanent.IsChecked == true;

    public DeleteFilesWindow(IReadOnlyList<MediaFile> files)
    {
        Title = "Delete files"; Width = 620; Height = 460;
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
            Text = "Recycled files can be restored from the Recycle Bin. Permanently deleted files cannot.",
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
            ItemsSource = files.Select(f => $"{Format.Bytes(f.SizeBytes)}   {f.FullPath}").ToList(),
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
