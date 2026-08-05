using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using MediaCatalog.Core.Consolidation;

namespace MediaCatalog.App;

/// <summary>
/// What the library is missing, programme by programme.
///
/// The list on the left is the programmes; the panel on the right is what each one is short
/// of, season by season. Two kinds of hole are shown and they are deliberately kept apart:
/// the gap in the middle of a season, which needs nothing but the files to find, and the
/// missing tail, which can only be found by knowing how many episodes the season actually
/// had. Without the IMDb episode data the second cannot be checked at all, and the window
/// says so rather than presenting a clean bill of health it has no right to give.
/// </summary>
public class MissingEpisodesWindow : Window
{
    private readonly MissingEpisodeReport _report;
    private readonly ListBox _shows = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _detail = new();

    public MissingEpisodesWindow(MissingEpisodeReport report)
    {
        _report = report;

        Title = "Missing episodes"; Width = 900; Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var dock = new DockPanel { Margin = new Thickness(14) };

        var heading = new TextBlock
        {
            Text = report.Describe(), TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6)
        };
        DockPanel.SetDock(heading, Dock.Top);
        dock.Children.Add(heading);

        if (!report.UsedImdb)
        {
            var warn = new TextBlock
            {
                Text = "There is no IMDb episode data, so each season was only checked up to the " +
                       "highest episode you hold. A season missing its last episodes therefore " +
                       "looks complete. Settings → Data sources → Download episodes puts that " +
                       "right; it is a small download and it only has to be done once.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = System.Windows.Media.Brushes.Firebrick,
                Margin = new Thickness(0, 0, 0, 8)
            };
            DockPanel.SetDock(warn, Dock.Top);
            dock.Children.Add(warn);
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        var copy = new Button
        {
            Content = "Copy the list", Width = 130, Margin = new Thickness(0, 0, 6, 0),
            ToolTip = "Put the whole report on the clipboard, so it can be pasted into whatever " +
                      "you keep your want-list in."
        };
        copy.Click += (_, _) => CopyAll();
        buttons.Children.Add(copy);
        buttons.Children.Add(new Button { Content = "Close", Width = 84, IsCancel = true, IsDefault = true });
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(buttons);

        if (report.Shows.Count == 0)
        {
            dock.Children.Add(new TextBlock
            {
                Text = report.ShowsChecked == 0
                    ? "Nothing to check. This looks at consolidated programmes with a season and " +
                      "an episode number — file some television into the library and try again."
                    : "Nothing is missing. Every season of every consolidated programme holds all " +
                      "of its episodes.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0)
            });
            Content = dock;
            return;
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _shows.ItemsSource = report.Shows.Select(Summarise).ToList();
        _shows.SelectionChanged += (_, _) => ShowDetail();
        Grid.SetColumn(_shows, 0);
        grid.Children.Add(_shows);

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _detail
        };
        Grid.SetColumn(scroller, 2);
        grid.Children.Add(scroller);

        dock.Children.Add(grid);
        Content = dock;

        _shows.SelectedIndex = 0;
    }

    private static string Summarise(ShowGaps show)
    {
        var parts = new List<string>();
        if (show.MissingEpisodes > 0) parts.Add($"{show.MissingEpisodes} episode(s)");
        if (show.MissingSeasons.Count > 0) parts.Add($"{show.MissingSeasons.Count} whole season(s)");
        return $"{show.Show}   —   {string.Join(", ", parts)}";
    }

    private void ShowDetail()
    {
        _detail.Children.Clear();
        if (_shows.SelectedIndex < 0 || _shows.SelectedIndex >= _report.Shows.Count) return;

        var show = _report.Shows[_shows.SelectedIndex];

        _detail.Children.Add(new TextBlock
        {
            Text = show.Show, FontWeight = FontWeights.Bold, FontSize = 15,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
        });

        foreach (var season in show.Seasons)
        {
            _detail.Children.Add(new TextBlock
            {
                Text = $"Season {season.Season:00}  —  {season.Missing.Count} missing of " +
                       $"{season.Expected}, {season.Held} held",
                FontWeight = FontWeights.Bold, Margin = new Thickness(0, 8, 0, 2)
            });

            if (!season.ExpectedFromImdb)
                _detail.Children.Add(Grey(
                    "The IMDb data has nothing on this season, so it was measured against the " +
                    "highest episode you hold — anything missing off the end is invisible."));

            foreach (var episode in season.Missing)
                _detail.Children.Add(new TextBlock
                {
                    Text = "    " + episode.Describe(), TextWrapping = TextWrapping.Wrap
                });
        }

        if (show.MissingSeasons.Count > 0)
        {
            _detail.Children.Add(new TextBlock
            {
                Text = "Seasons you have none of: " + string.Join(", ",
                    show.MissingSeasons.Select(s => s.ToString("00"))),
                FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 12, 0, 2)
            });
            _detail.Children.Add(Grey(
                "IMDb records these and the library holds nothing of them. That may be exactly " +
                "as you want it — one season of a programme is a perfectly ordinary thing to " +
                "own — which is why they are listed apart from the gaps above."));
        }
    }

    private static TextBlock Grey(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap,
        Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 0, 0, 4)
    };

    /// <summary>The whole report as plain text, for pasting somewhere it can be acted on.</summary>
    private void CopyAll()
    {
        var text = new StringBuilder();
        text.AppendLine(_report.Describe());
        text.AppendLine();

        foreach (var show in _report.Shows)
        {
            text.AppendLine(show.Show);
            foreach (var season in show.Seasons)
            {
                text.AppendLine($"  Season {season.Season:00} — {season.Missing.Count} missing of " +
                                $"{season.Expected}" +
                                (season.ExpectedFromImdb ? "" : " (counted from the files themselves)"));
                foreach (var episode in season.Missing)
                    text.AppendLine("    " + episode.Describe());
            }
            if (show.MissingSeasons.Count > 0)
                text.AppendLine("  No episodes at all of season(s): " +
                                string.Join(", ", show.MissingSeasons));
            text.AppendLine();
        }

        try { Clipboard.SetText(text.ToString()); }
        catch { /* the clipboard is occasionally held by something else; not worth a dialog */ }
    }
}
