using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MediaCatalog.Core.Models;
using Microsoft.Win32;

namespace MediaCatalog.App;

/// <summary>
/// Sets one title for a whole folder — the quickest way to name a show whose episodes are
/// all in one place. The rule sticks, so files scanned into that folder later inherit it.
/// </summary>
public class TitleFolderWindow : Window
{
    private readonly ComboBox _folder = new();
    private readonly TextBox _title = new();
    private readonly CheckBox _subdirs = new() { Content = "Include all subfolders", IsChecked = true };

    public string SelectedFolder => _folder.SelectedItem as string ?? _folder.Text;
    public string Title_ => _title.Text.Trim();
    public bool IncludeSubdirectories => _subdirs.IsChecked == true;

    public TitleFolderWindow(string folder, string suggestedTitle)
    {
        Title = "Set title for folder"; Width = 560; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;

        var panel = new StackPanel { Margin = new Thickness(14) };

        // The folder and its ancestors, so the rule can be set higher up the tree — on the
        // show folder rather than on one season of it.
        var ancestors = new List<string>();
        var d = folder;
        while (!string.IsNullOrEmpty(d))
        {
            ancestors.Add(d);
            d = System.IO.Path.GetDirectoryName(d);
        }
        panel.Children.Add(new TextBlock { Text = "Apply to folder (pick this one or a parent):" });
        _folder.ItemsSource = ancestors;
        _folder.SelectedIndex = 0;
        _folder.Margin = new Thickness(0, 2, 0, 10);
        panel.Children.Add(_folder);

        panel.Children.Add(new TextBlock { Text = "Title for everything in it:" });
        _title.Text = suggestedTitle;
        _title.Margin = new Thickness(0, 2, 0, 10);
        panel.Children.Add(_title);
        panel.Children.Add(_subdirs);
        panel.Children.Add(new TextBlock
        {
            Text = "Counts as a confirmed title, so these files can be consolidated. Files added to " +
                   "this folder later pick it up automatically.",
            Foreground = System.Windows.Media.Brushes.Gray, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var ok = new Button { Content = "OK", Width = 78, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        ok.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(Title_)) DialogResult = true; };
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Cancel", Width = 78, IsCancel = true });
        panel.Children.Add(buttons);

        Content = panel;
        Loaded += (_, _) => { _title.Focus(); _title.SelectAll(); };
    }
}

/// <summary>
/// Chooses where to move files, and whether to take everything else in their folder along
/// — the usual case being a download folder holding one film plus its subtitles and extras.
/// </summary>
public class MoveFilesWindow : Window
{
    private readonly TextBox _folder = new() { VerticalContentAlignment = VerticalAlignment.Center };
    private readonly CheckBox _wholeFolder;
    private readonly CheckBox _delete = new()
    {
        Content = "Move (delete the original once the copy is verified)",
        IsChecked = true, Margin = new Thickness(0, 10, 0, 0)
    };

    public string Destination => _folder.Text.Trim();
    public bool IncludeContainingFolder => _wholeFolder.IsChecked == true;
    public bool DeleteOriginal => _delete.IsChecked == true;

    public MoveFilesWindow(int fileCount, IReadOnlyList<string> containingFolders, int siblingCount)
    {
        Title = "Move files"; Width = 620; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;

        _wholeFolder = new CheckBox
        {
            Content = containingFolders.Count == 1
                ? $"Also move the other {siblingCount} file(s) in {containingFolders[0]}"
                : $"Also move every other catalogued file in the {containingFolders.Count} containing folders ({siblingCount} file(s))",
            Margin = new Thickness(0, 10, 0, 0),
            IsEnabled = siblingCount > 0
        };

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock
        {
            Text = $"Move {fileCount} selected file(s) to:",
            FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6)
        });

        var row = new DockPanel();
        var browse = new Button { Content = "Browse…", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog { Title = "Choose destination folder" };
            if (dlg.ShowDialog(this) == true) _folder.Text = dlg.FolderName;
        };
        DockPanel.SetDock(browse, Dock.Right);
        row.Children.Add(browse);
        row.Children.Add(_folder);
        panel.Children.Add(row);

        panel.Children.Add(_wholeFolder);
        panel.Children.Add(_delete);
        panel.Children.Add(new TextBlock
        {
            Text = "Files are copied and hash-verified first; the original is only removed after a successful verify.",
            Foreground = System.Windows.Media.Brushes.Gray, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var ok = new Button { Content = "Move", Width = 90, IsDefault = true, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 6, 0) };
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(Destination))
            {
                MessageBox.Show(this, "Choose a destination folder first.",
                    "Move files", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            DialogResult = true;
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Cancel", Width = 84, IsCancel = true });
        panel.Children.Add(buttons);

        Content = panel;
    }
}
