using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MediaCatalog.App.Infrastructure;
using MediaCatalog.App.ViewModels;
using MediaCatalog.Core.Classification;
using MediaCatalog.Core.Models;

namespace MediaCatalog.App;

/// <summary>
/// Edits every field of a catalogue entry that a person can reasonably know better than
/// the program does — including the date, which is read off the file system and is wrong
/// often enough to be worth correcting (a copy that reset it, an archive that never had it).
///
/// What the content *is* — title, year, numbering, category — is written to every
/// byte-identical copy, because they are the same thing. What the file *is* — its name,
/// its date, what a decode made of it — belongs to this one file alone.
/// </summary>
public class FileDetailsWindow : Window
{
    private readonly MediaFile _file;

    private readonly TextBox _title = new();
    private readonly TextBox _secondaryTitle = new();
    private readonly TextBox _genres = new();
    private readonly TextBox _year = new() { Width = 80 };
    private readonly TextBox _season = new() { Width = 80 };
    private readonly TextBox _episode = new() { Width = 80 };
    private readonly TextBox _episodeEnd = new() { Width = 80 };
    private readonly ComboBox _category = new() { IsEditable = true, Width = 200 };
    private readonly TextBox _date = new() { Width = 190 };
    private readonly ComboBox _integrity = new() { Width = 200 };
    private readonly ComboBox _kind = new() { Width = 200 };
    private readonly TextBox _fileName = new();

    /// <summary>The corrections, valid once the dialog closed with OK.</summary>
    public MainViewModel.FileEdits? Edits { get; private set; }

    public FileDetailsWindow(MediaFile file, IReadOnlyList<string> categories, string effectiveCategory,
        int identicalCopies)
    {
        _file = file;

        Title = "Edit details"; Width = 640; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;

        _title.Text = file.EffectiveTitle;
        _secondaryTitle.Text = file.SecondaryTitle;
        _genres.Text = file.Genres;
        _year.Text = file.Year?.ToString() ?? "";
        _season.Text = file.Season?.ToString() ?? "";
        _episode.Text = file.Episode?.ToString() ?? "";
        _episodeEnd.Text = file.EpisodeEnd?.ToString() ?? "";
        _category.ItemsSource = categories;
        _category.Text = effectiveCategory;
        _date.Text = file.LastModifiedUtc == default
            ? ""
            : file.LastModifiedUtc.ToLocalTime().ToString(DateFormat, CultureInfo.InvariantCulture);
        _integrity.ItemsSource = Enum.GetValues(typeof(IntegrityStatus));
        _integrity.SelectedItem = file.Integrity;
        _kind.ItemsSource = Enum.GetValues(typeof(MediaKind));
        _kind.SelectedItem = file.Kind;
        _fileName.Text = file.FileName;

        var panel = new StackPanel { Margin = new Thickness(16) };

        panel.Children.Add(new TextBlock
        {
            Text = file.FullPath, FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2)
        });
        panel.Children.Add(Hint($"{Format.Bytes(file.SizeBytes)}" +
                                (file.HasHash ? $"  •  {file.Sha256[..12]}…" : "  •  not hashed") +
                                (identicalCopies > 0 ? $"  •  {identicalCopies} identical copy(ies)" : "")));

        panel.Children.Add(Section("What the content is"));
        panel.Children.Add(Labeled("Primary title:", _title));
        panel.Children.Add(Labeled("Second title:", _secondaryTitle));
        panel.Children.Add(Hint("The primary title is the programme's or the film's name, and is what " +
                                "decides where the file is filed. The second title is the name under " +
                                "that one — an episode's own title, a film's tag line — and decides " +
                                "nothing. Most files have only the first."));
        panel.Children.Add(Labeled("Genres:", _genres));
        panel.Children.Add(Labeled("Year:", _year));
        panel.Children.Add(Labeled("Season:", _season));
        panel.Children.Add(Labeled("Episode:", _episode));
        panel.Children.Add(Labeled("…to episode:", _episodeEnd));
        panel.Children.Add(Hint("Only for a double episode — \"S06E11E12\" is episodes 11 and 12, so " +
                                "its episode is 11 and its \"to episode\" is 12. Leave it empty for an " +
                                "ordinary single episode."));
        panel.Children.Add(Labeled("Category:", _category));
        panel.Children.Add(Hint(identicalCopies > 0
            ? $"These are written to this file and to its {identicalCopies} identical copy(ies): " +
              "the same content should never be described two different ways."
            : "Leave the year, season or episode empty to clear it."));

        panel.Children.Add(Section("What the file is"));
        panel.Children.Add(Labeled("File name:", _fileName));
        panel.Children.Add(Labeled("Modified:", _date));
        panel.Children.Add(Labeled("Integrity:", _integrity));
        panel.Children.Add(Labeled("Kind:", _kind));
        panel.Children.Add(Hint($"The date is written to the file on disk as well as to the catalogue — " +
                                $"left in the catalogue alone, the next scan would read the old one back. " +
                                $"Format: {DateFormat}. Clear it to leave the date as it is."));
        panel.Children.Add(Hint("Change the title and leave the file name alone, and the file is renamed " +
                                "to match the naming scheme (unless that is switched off in Settings)."));

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var ok = new Button
        {
            Content = "Save", Width = 90, IsDefault = true,
            FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 6, 0)
        };
        ok.Click += OnSave;
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Cancel", Width = 84, IsCancel = true });
        panel.Children.Add(buttons);

        Content = panel;
        Loaded += (_, _) => { _title.Focus(); _title.SelectAll(); };
    }

    private const string DateFormat = "yyyy-MM-dd HH:mm:ss";

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!TryNumber(_year.Text, "year", 1800, 2999, out var year)) return;
        if (!TryNumber(_season.Text, "season", 0, 999, out var season)) return;
        if (!TryNumber(_episode.Text, "episode", 0, 9999, out var episode)) return;
        if (!TryNumber(_episodeEnd.Text, "last episode", 0, 9999, out var episodeEnd)) return;

        if (episodeEnd is { } last && (episode is not { } first || last <= first))
        {
            MessageBox.Show(this,
                "The \"to episode\" is the last episode of a double, so it has to come after the " +
                "episode itself — 11 to 12, not the other way round. Leave it empty unless the " +
                "file really does hold more than one episode.",
                "Edit details", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var category = _category.Text.Trim();
        if (!ConfirmNumberingCategory(season, episode, ref category)) return;

        var modified = _file.LastModifiedUtc;
        var typed = _date.Text.Trim();
        if (typed.Length > 0)
        {
            if (!DateTime.TryParseExact(typed, DateFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var local) &&
                !DateTime.TryParse(typed, CultureInfo.CurrentCulture, DateTimeStyles.None, out local))
            {
                MessageBox.Show(this,
                    $"That date could not be read. Write it as {DateFormat}, for example " +
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}.",
                    "Edit details", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            modified = local.ToUniversalTime();
        }

        var name = _fileName.Text.Trim();
        var invalid = System.IO.Path.GetInvalidFileNameChars().Where(c => name.Contains(c)).ToList();
        if (invalid.Count > 0)
        {
            MessageBox.Show(this,
                "A file name cannot contain: " + string.Join(' ', invalid.Select(c => c.ToString())),
                "Edit details", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Edits = new MainViewModel.FileEdits(
            _title.Text.Trim(),
            _secondaryTitle.Text.Trim(),
            _genres.Text.Trim(),
            year,
            season,
            episode,
            episodeEnd,
            category,
            modified,
            (IntegrityStatus)(_integrity.SelectedItem ?? IntegrityStatus.NotChecked),
            (MediaKind)(_kind.SelectedItem ?? MediaKind.Unknown),
            name);
        DialogResult = true;
    }

    /// <summary>
    /// A season and an episode are things only a programme has. Somebody typing one onto a
    /// file filed as a film is not asking for it to be thrown away — they are telling us
    /// the file was identified wrongly, and the category is what wants correcting. So the
    /// numbering is kept either way, and changing the category is offered rather than done.
    /// </summary>
    private bool ConfirmNumberingCategory(int? season, int? episode, ref string category)
    {
        if (season is null && episode is null) return true;
        if (category is CategoryResolver.TvShow or CategoryResolver.TvExtra) return true;

        var answer = MessageBox.Show(this,
            $"A season and episode number belong to a programme, and this file is filed as " +
            $"'{(category.Length == 0 ? "nothing in particular" : category)}'.\n\n" +
            "Yes — change the category to TvShow as well, which is almost certainly what the " +
            "numbering means.\n" +
            "No — keep the numbering and leave the category alone.\n" +
            "Cancel — go back to the editor.",
            "Season and episode", MessageBoxButton.YesNoCancel, MessageBoxImage.Question,
            MessageBoxResult.Yes);

        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.Yes)
        {
            category = CategoryResolver.TvShow;
            _category.Text = category;
        }
        return true;
    }

    private bool TryNumber(string text, string what, int min, int max, out int? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (int.TryParse(text.Trim(), out var parsed) && parsed >= min && parsed <= max)
        {
            value = parsed;
            return true;
        }
        MessageBox.Show(this,
            $"Enter a whole number for the {what} between {min} and {max}, or leave it empty to clear it.",
            "Edit details", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private static TextBlock Section(string text) => new()
    {
        Text = text, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 14, 0, 4)
    };

    private static TextBlock Hint(string text) => new()
    {
        Text = text, Foreground = System.Windows.Media.Brushes.Gray,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0)
    };

    private static FrameworkElement Labeled(string label, FrameworkElement control)
    {
        var dp = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        var t = new TextBlock { Text = label, Width = 90, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(t, Dock.Left);
        dp.Children.Add(t);

        if (!double.IsNaN(control.Width))
        {
            control.HorizontalAlignment = HorizontalAlignment.Left;
            DockPanel.SetDock(control, Dock.Left);
        }
        dp.Children.Add(control);
        return dp;
    }
}
