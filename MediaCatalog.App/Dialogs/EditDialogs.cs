using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MediaCatalog.Core.Models;
using Microsoft.Win32;

namespace MediaCatalog.App;

/// <summary>Edits the season and episode numbers of one or more files by hand.</summary>
public class SeasonEpisodeWindow : Window
{
    private readonly TextBox _season = new() { Width = 70 };
    private readonly TextBox _episode = new() { Width = 70 };

    /// <summary>Null clears the value; the property is only read after a successful close.</summary>
    public int? Season { get; private set; }
    public int? Episode { get; private set; }

    public SeasonEpisodeWindow(IReadOnlyList<MediaFile> files)
    {
        Title = "Season / episode"; Width = 420; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;

        var first = files[0];
        _season.Text = first.Season?.ToString() ?? "";
        _episode.Text = first.Episode?.ToString() ?? "";

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock
        {
            Text = files.Count == 1
                ? first.FileName
                : $"{files.Count} selected files",
            FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = "Season:", VerticalAlignment = VerticalAlignment.Center, Width = 60 });
        row.Children.Add(_season);
        row.Children.Add(new TextBlock { Text = "Episode:", VerticalAlignment = VerticalAlignment.Center, Width = 66, Margin = new Thickness(16, 0, 0, 0) });
        row.Children.Add(_episode);
        panel.Children.Add(row);

        panel.Children.Add(new TextBlock
        {
            Text = "Leave a box empty to clear that value. Season 0 marks a special.",
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
        ok.Click += OnOk;
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Cancel", Width = 78, IsCancel = true });
        panel.Children.Add(buttons);

        Content = panel;
        Loaded += (_, _) => { _season.Focus(); _season.SelectAll(); };
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!TryRead(_season.Text, "season", out var season)) return;
        if (!TryRead(_episode.Text, "episode", out var episode)) return;

        Season = season;
        Episode = episode;
        DialogResult = true;
    }

    private bool TryRead(string text, string what, out int? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (int.TryParse(text.Trim(), out var parsed) && parsed >= 0 && parsed < 1000)
        {
            value = parsed;
            return true;
        }
        MessageBox.Show(this, $"Enter a whole number for the {what} (0–999), or leave it empty to clear it.",
            "Season / episode", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
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
