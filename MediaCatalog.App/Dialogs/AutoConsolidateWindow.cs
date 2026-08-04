using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MediaCatalog.App.Infrastructure;
using MediaCatalog.Core.Consolidation;

namespace MediaCatalog.App;

/// <summary>
/// What a hands-off consolidation run is about to do, before it does any of it.
///
/// A run that files a library without being asked anything is a great deal of work happening
/// at once, and the only honest way to offer it is to say first what it will decide on its
/// own, what it will decide by looking at the files, and what it will not touch at all. Those
/// three numbers are the whole substance of the thing; everything else on this screen is an
/// explanation of them.
/// </summary>
public class AutoConsolidateWindow : Window
{
    public AutoConsolidateWindow(
        IReadOnlyList<AutoJob> jobs,
        IReadOnlyList<AutoReview> review,
        bool canFingerprint,
        bool canDeepCheck)
    {
        Title = "Consolidate automatically"; Width = 760; Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var singles = jobs.Count(j => j.Kind == AutoJobKind.Single);
        var exact = jobs.Where(j => j.Kind == AutoJobKind.ExactCopies).ToList();
        var rivals = jobs.Where(j => j.Kind == AutoJobKind.Rivals).ToList();
        var redundant = exact.Sum(j => j.Files.Count - 1);

        var dock = new DockPanel { Margin = new Thickness(14) };

        var heading = new TextBlock
        {
            Text = $"{jobs.Count} item(s) can be filed without asking you anything.",
            FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(heading, Dock.Top);
        dock.Children.Add(heading);

        var body = new StackPanel();

        body.Children.Add(Step($"{singles} have no other copy anywhere",
            "Nothing to decide: each one is moved into the library under the name its category, " +
            "title and numbering give it. A file already on the library's drive is renamed into " +
            "place rather than copied, so its size does not matter."));

        body.Children.Add(Step(
            $"{exact.Count} have copies that are byte-for-byte identical ({redundant} redundant)",
            "Which copy survives changes nothing about what the library ends up holding, so the " +
            "one already in the library wins — or, failing that, one on the library's own drive, " +
            "because moving that costs nothing. Every other copy is deleted to the Recycle Bin " +
            "once the survivor is safely in place."));

        body.Children.Add(Step(
            $"{rivals.Count} have copies that are genuinely different files",
            "The same thing from two different releases. Each distinct copy is fingerprinted and " +
            "compared — allowing for one starting a second or two after another, which is what an " +
            "extra beat of distributor logo does to two rips of one film. If they really are the " +
            "same content, the best picture wins, and among copies of one quality the smallest, " +
            "since at a given resolution the extra bytes are padding rather than detail. That copy " +
            "is then decoded end to end; if it is damaged it and its identical twins are removed " +
            "and the next best is tried, until one survives or none is left. If the fingerprints " +
            "disagree, nothing is touched and it comes to you instead — one of them is mislabelled, " +
            "and only you can say which."));

        body.Children.Add(Step($"{review.Count} will not be touched",
            "Each is missing something that decides where it goes — a title, a year, an episode " +
            "number, or a consolidation folder for its category. They are listed in full when the " +
            "run finishes."));

        if (rivals.Count > 0 && !canFingerprint)
            body.Children.Add(Warn(
                "FFmpeg and ffprobe (and fpcalc, for audio) are not set up, so the copies that " +
                "need comparing cannot be compared. Those items will be listed for you rather " +
                "than guessed at. Everything else still runs."));
        else if (rivals.Count > 0 && !canDeepCheck)
            body.Children.Add(Warn(
                "FFmpeg is not set up, so the copy chosen cannot be decoded to check it is sound. " +
                "The best copy will be filed on the strength of its quality and size alone."));

        body.Children.Add(new TextBlock
        {
            Text = "Nothing is deleted until the copy replacing it has actually arrived in the " +
                   "library. Stopping the run part-way leaves everything it had not yet reached " +
                   "exactly where it is, and the last ten operations are reversible from Undo.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0),
            Foreground = System.Windows.Media.Brushes.Gray
        });

        if (jobs.Count > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = "\nWhat it will file:", FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 8, 0, 4)
            });
            body.Children.Add(new ListBox
            {
                Height = 150,
                ItemsSource = jobs.Select(Line).ToList(),
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            });
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var go = new Button
        {
            Content = "Consolidate", Width = 120, IsDefault = true,
            FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 6, 0)
        };
        go.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(go);
        buttons.Children.Add(new Button { Content = "Cancel", Width = 84, IsCancel = true });
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(buttons);

        dock.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body
        });
        Content = dock;
    }

    private static string Line(AutoJob job)
    {
        var what = job.Kind switch
        {
            AutoJobKind.Single => "one copy",
            AutoJobKind.ExactCopies => $"{job.Files.Count} identical copies",
            _ => $"{job.Distinct.Count} different copies"
        };
        var bytes = Format.Bytes(job.Files.Sum(f => f.SizeBytes));
        return $"{job.Display}   —   {what}, {bytes}   [{job.Category}]";
    }

    private static FrameworkElement Step(string heading, string detail)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(new TextBlock
        {
            Text = "• " + heading, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = detail, TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.Gray,
            Margin = new Thickness(12, 2, 0, 0)
        });
        return panel;
    }

    private static FrameworkElement Warn(string text) => new TextBlock
    {
        Text = text, TextWrapping = TextWrapping.Wrap,
        Foreground = System.Windows.Media.Brushes.Firebrick,
        Margin = new Thickness(0, 0, 0, 10)
    };
}
